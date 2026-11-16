using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using IntranetSync.Core.Cloud;
using IntranetSync.Core.Config;
using IntranetSync.Core.Logging;
using IntranetSync.Core.Pdf;
using Newtonsoft.Json.Linq;

namespace IntranetSync.Core.Sync
{
    public sealed class SyncRunResult
    {
        public bool Success { get; set; }
        public int RowsRead { get; set; }
        public int RowsUpserted { get; set; }
        public int RowsReconciled { get; set; }
        public int ViewCount { get; set; }
        public int RowsVisited { get; set; }
        public int PdfFailures { get; set; }
        public string Message { get; set; }
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// Incremental sync: watermark → paged SELECT → PDF pipeline → batch upsert,
    /// then optional PK reconcile to catch rows missed by watermark.
    /// </summary>
    public sealed class SyncEngine
    {
        private readonly ConfigStore _store;
        private readonly SyncLogger _log;
        private readonly WatermarkStore _watermarks;
        private readonly FailureQueue _failures;
        private readonly PdfHashMap _pdfMap;
        private readonly RowHashCursor _rowHashes;
        private readonly SqlViewReader _reader;
        private readonly string _dataDir;

        public SyncEngine(ConfigStore store, SyncLogger log)
        {
            _store = store;
            _log = log;
            _dataDir = store.DataDirectory;
            _watermarks = new WatermarkStore(_dataDir);
            _failures = new FailureQueue(_dataDir);
            _pdfMap = new PdfHashMap(_dataDir);
            _rowHashes = new RowHashCursor(_dataDir);
            _reader = new SqlViewReader(log);
        }

        public FailureQueue Failures { get { return _failures; } }
        public WatermarkStore Watermarks { get { return _watermarks; } }

        public async Task<string> TestSqlAsync(CancellationToken ct)
        {
            var cfg = _store.LoadOrDefault();
            return await _reader.TestConnectionAsync(cfg.Sql, ct).ConfigureAwait(false);
        }

        public async Task<SyncRunResult> RunOnceAsync(CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var cfg = _store.LoadOrDefault();
            var result = new SyncRunResult();

            if (string.IsNullOrWhiteSpace(cfg.View.ViewName))
            {
                result.Message = "ViewName not configured";
                return result;
            }

            bool useIncremental = !string.IsNullOrWhiteSpace(cfg.View.IncrementalColumn);
            bool hasPk = !string.IsNullOrWhiteSpace(cfg.View.PrimaryKeyColumn);
            if (!hasPk)
            {
                // 线上配置经常把主键留空。VW_PERSON 的业务键是 person_id，空主键会导致
                // 云端按空 pk 覆盖、补漏扫描整表跳过。
                cfg.View.PrimaryKeyColumn = "person_id";
                hasPk = true;
                _log.Warn("PrimaryKeyColumn 为空，本轮按 person_id 做游标、补漏和云端 pk");
            }
            if (!useIncremental && !hasPk)
            {
                result.Message = "无唯一标识列时必须配置增量列（如 updated_at）";
                return result;
            }

            using (var api = new CloudApiClient(cfg.Cloud, _log))
            {
                var pipeline = new PdfPipeline(cfg.Pdf, cfg.Oss, cfg.Cloud, api, _pdfMap, _log, _dataDir);
                var wmRec = _watermarks.Get(cfg.View.ViewName);
                string cursor = wmRec.LastWatermark ?? "";
                string lastPk = wmRec.LastPrimaryKey ?? "";

                int totalRead = 0;
                int totalUpsert = 0;
                int totalReconciled = 0;
                int pdfFail = 0;
                string lastWm = cursor;
                string lastIncPk = lastPk;
                const int maxPages = 100;
                int pageSize = cfg.View.PageSize > 0 ? cfg.View.PageSize : 300;

                // ---- Phase 1: incremental (or hash-mode) pages ----
                for (int page = 0; page < maxPages; page++)
                {
                    ct.ThrowIfCancellationRequested();
                    IList<ViewRow> rows;
                    if (useIncremental)
                    {
                        rows = await _reader.ReadPageAsync(cfg.Sql, cfg.View, lastWm, lastIncPk, ct)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        rows = await _reader.ReadPageAsync(cfg.Sql, cfg.View, lastWm, ct)
                            .ConfigureAwait(false);
                    }

                    if (rows == null || rows.Count == 0)
                        break;

                    totalRead += rows.Count;
                    var batchRows = new List<SyncRow>();
                    var builtIdentities = new List<string>();

                    foreach (var row in rows)
                    {
                        ct.ThrowIfCancellationRequested();
                        string identity = RowIdentity(row);
                        string rowHash = RowHasher.Compute(identity, row.Fields);

                        // 仅「无增量列 + 有唯一列」时用行哈希去重
                        if (!useIncremental)
                        {
                            if (string.IsNullOrEmpty(identity) || !_rowHashes.HasChanged(identity, rowHash))
                            {
                                lastWm = row.Watermark ?? lastWm;
                                lastIncPk = row.PrimaryKey ?? lastIncPk;
                                continue;
                            }
                        }

                        var built = await TryBuildSyncRowAsync(
                                cfg, pipeline, row, rowHash, ct,
                                () => { pdfFail++; }).ConfigureAwait(false);
                        if (built == null)
                            throw new InvalidOperationException("Row was not pushed; incremental page aborted to avoid data loss");

                        batchRows.Add(built);
                        builtIdentities.Add(identity);
                        lastWm = row.Watermark ?? lastWm;
                        if (!string.IsNullOrEmpty(row.PrimaryKey))
                            lastIncPk = row.PrimaryKey;
                    }

                    if (batchRows.Count > 0)
                    {
                        int accepted = await PostBatchStrictAsync(api, cfg, batchRows, ct).ConfigureAwait(false);
                        totalUpsert += accepted;

                        for (int i = 0; i < batchRows.Count; i++)
                        {
                            if (!string.IsNullOrEmpty(builtIdentities[i]))
                                _rowHashes.Commit(builtIdentities[i], batchRows[i].RowHash);
                        }
                        _rowHashes.Persist();
                    }

                    // Only advance after a fully accepted page (or empty push page that was all hash-skips).
                    if (!string.IsNullOrEmpty(lastWm))
                        _watermarks.Advance(cfg.View.ViewName, lastWm, lastIncPk);

                    if (rows.Count < pageSize)
                        break;
                }

                // 全表补齐只做一次：修历史漏推。完成后下一轮只从水位查，不再每轮扫全表。
                bool needReconcile = cfg.Sync.ReconcileEnabled && !wmRec.FullReconcileCompleted;
                int viewCount = 0;
                int visited = 0;
                int skippedUnchanged = 0;
                if (needReconcile)
                {
                    viewCount = await _reader.CountAsync(cfg.Sql, cfg.View, ct).ConfigureAwait(false);
                    _log.Info("Reconcile start view=" + cfg.View.ViewName + " viewCount=" + viewCount);
                    int skip = 0;
                    int guard = 0;
                    while (visited < viewCount)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (++guard > 100000)
                            throw new InvalidOperationException("Covering scan exceeded page guard");

                        var rows = await _reader.ReadCoveringPageAsync(cfg.Sql, cfg.View, skip, ct)
                            .ConfigureAwait(false);
                        if (rows == null || rows.Count == 0)
                            break;

                        visited += rows.Count;
                        skip += rows.Count;
                        totalRead += rows.Count;
                        var batchRows = new List<SyncRow>();
                        var batchKeys = new List<string>();

                        foreach (var row in rows)
                        {
                            ct.ThrowIfCancellationRequested();
                            string identity = RowIdentity(row);
                            string rowHash = RowHasher.Compute(identity, row.Fields);
                            if (!string.IsNullOrEmpty(identity) && !_rowHashes.HasChanged(identity, rowHash))
                            {
                                skippedUnchanged++;
                                continue;
                            }

                            var built = await TryBuildSyncRowAsync(
                                    cfg, pipeline, row, rowHash, ct,
                                    () => { pdfFail++; }).ConfigureAwait(false);
                            if (built == null)
                                throw new InvalidOperationException("Row was not pushed; covering scan aborted to avoid data loss");

                            batchRows.Add(built);
                            batchKeys.Add(identity);
                        }

                        if (batchRows.Count > 0)
                        {
                            int accepted = await PostBatchStrictAsync(api, cfg, batchRows, ct).ConfigureAwait(false);
                            totalUpsert += accepted;
                            totalReconciled += accepted;

                            for (int i = 0; i < batchRows.Count; i++)
                            {
                                if (!string.IsNullOrEmpty(batchKeys[i]))
                                    _rowHashes.Commit(batchKeys[i], batchRows[i].RowHash);
                            }
                            _rowHashes.Persist();
                        }

                        if (rows.Count < pageSize)
                            break;
                    }

                    _log.Info("Reconcile done visited=" + visited + " viewCount=" + viewCount + " pushed=" + totalReconciled + " unchanged=" + skippedUnchanged);
                }

                sw.Stop();
                result.RowsRead = totalRead;
                result.RowsUpserted = totalUpsert;
                result.RowsReconciled = totalReconciled;
                result.ViewCount = viewCount;
                result.RowsVisited = visited;
                result.PdfFailures = pdfFail;
                result.Duration = sw.Elapsed;

                if (needReconcile && visited != viewCount)
                {
                    result.Success = false;
                    result.Message = string.Format(
                        "INCOMPLETE visited={0} viewCount={1} upsert={2} pdfFail={3}. Full reconcile not finished; next round will continue it.",
                        visited, viewCount, totalUpsert, pdfFail);
                    _log.Error(result.Message);
                    return result;
                }

                if (needReconcile)
                    _watermarks.MarkFullReconcileCompleted(cfg.View.ViewName);

                result.Success = true;
                result.Message = string.Format(
                    "OK mode={0} viewCount={1} visited={2} upsert={3} reconcile={4} unchanged={5} pdfFail={6} elapsed={7}ms",
                    needReconcile ? "reconcile" : "watermark",
                    viewCount, visited, totalUpsert, totalReconciled, skippedUnchanged, pdfFail, (int)sw.Elapsed.TotalMilliseconds);
                _log.Info(result.Message);
                return result;
            }
        }

        /// <summary>
        /// Local identity. person_id alone collapses the duplicate row; append updated_at so both are tracked.
        /// Cloud pk stays person_id so import-persons updates the same person instead of inserting a second copy.
        /// </summary>
        private static string RowIdentity(ViewRow row)
        {
            string pk = row == null ? "" : (row.PrimaryKey ?? "");
            string wm = row == null ? "" : (row.Watermark ?? "");
            if (string.IsNullOrEmpty(pk))
                return wm;
            if (string.IsNullOrEmpty(wm))
                return pk;
            return pk + "|" + wm;
        }

        private async Task<SyncRow> TryBuildSyncRowAsync(
            SyncConfig cfg,
            PdfPipeline pipeline,
            ViewRow row,
            string rowHash,
            CancellationToken ct,
            Action onPdfFail)
        {
            var pdfMeta = new Dictionary<string, PdfMeta>(StringComparer.OrdinalIgnoreCase);
            bool rowPdfFailed = false;

            if (cfg.View.PdfUrlColumns != null)
            {
                foreach (var col in cfg.View.PdfUrlColumns)
                {
                    if (string.IsNullOrWhiteSpace(col)) continue;
                    JToken tok;
                    if (!row.Fields.TryGetValue(col, out tok) || tok == null || tok.Type == JTokenType.Null)
                        continue;

                    string intranetUrl = tok.ToString();
                    if (string.IsNullOrWhiteSpace(intranetUrl)) continue;

                    var resolved = await pipeline.ResolveAsync(intranetUrl, ct).ConfigureAwait(false);
                    if (!resolved.Success)
                    {
                        onPdfFail();
                        rowPdfFailed = true;
                        _failures.Enqueue(new FailureItem
                        {
                            ViewName = cfg.View.ViewName,
                            PrimaryKey = row.PrimaryKey,
                            PdfColumn = col,
                            PdfUrl = intranetUrl,
                            Error = resolved.Error,
                            Attempts = 1
                        });

                        if (cfg.Sync.FailBatchOnPdfError)
                            throw new InvalidOperationException("PDF failed and FailBatchOnPdfError=true: " + resolved.Error);

                        continue;
                    }

                    row.Fields[col] = resolved.CloudUrl;
                    pdfMeta[col] = new PdfMeta
                    {
                        Sha256 = resolved.Sha256,
                        OssKey = resolved.OssKey,
                        Url = resolved.CloudUrl
                    };
                }
            }

            if (rowPdfFailed && cfg.Sync.FailBatchOnPdfError)
                return null;

            string pk = row.PrimaryKey;
            if (string.IsNullOrEmpty(pk) && row.Fields != null)
            {
                JToken pkTok;
                if (row.Fields.TryGetValue(cfg.View.PrimaryKeyColumn ?? "", out pkTok) && pkTok != null && pkTok.Type != JTokenType.Null)
                    pk = pkTok.ToString();
            }

            return new SyncRow
            {
                PrimaryKey = pk,
                Watermark = row.Watermark,
                RowHash = rowHash,
                Fields = row.Fields,
                Pdf = pdfMeta.Count > 0 ? pdfMeta : null
            };
        }

        /// <summary>
        /// Post batch and require full acceptance before caller advances watermark / commits hashes.
        /// </summary>
        private async Task<int> PostBatchStrictAsync(
            CloudApiClient api,
            SyncConfig cfg,
            List<SyncRow> batchRows,
            CancellationToken ct)
        {
            var req = new SyncBatchRequest
            {
                TenantId = cfg.Cloud.TenantId,
                ViewName = cfg.View.ViewName,
                Rows = batchRows
            };
            var resp = await api.PostBatchAsync(req, ct).ConfigureAwait(false);

            if (resp.Rejected != null && resp.Rejected.Count > 0)
            {
                throw new InvalidOperationException(
                    "Cloud batch rejected " + resp.Rejected.Count + " row(s); watermark not advanced. First=" +
                    resp.Rejected[0]);
            }

            int accepted;
            if (resp.Accepted <= 0)
            {
                // Legacy APIs may omit accepted; treat as full success only when no rejects.
                _log.Warn("Cloud batch accepted<=0; assuming all " + batchRows.Count + " accepted (legacy response)");
                accepted = batchRows.Count;
            }
            else if (resp.Accepted < batchRows.Count)
            {
                throw new InvalidOperationException(
                    "Cloud batch partial accept " + resp.Accepted + "/" + batchRows.Count +
                    "; watermark not advanced to avoid data loss");
            }
            else
            {
                accepted = resp.Accepted;
            }

            return accepted;
        }
    }
}
