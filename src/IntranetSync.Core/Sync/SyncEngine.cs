using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
        public RowHashCursor RowHashes { get { return _rowHashes; } }

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
                // 空主键时 SQL 游标仍用 person_id；云端/本地行身份见 RowIdentity（不按人去重）。
                cfg.View.PrimaryKeyColumn = "person_id";
                hasPk = true;
                _log.Warn("PrimaryKeyColumn 为空，本轮 SQL 游标按 person_id；云端 pk 按物理行（含 updated_at/PDF）");
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
                        // PDF 失败：不推送、不提交哈希；仍推进水位，避免卡死后续行。全表补齐会重试。
                        lastWm = row.Watermark ?? lastWm;
                        if (!string.IsNullOrEmpty(row.PrimaryKey))
                            lastIncPk = row.PrimaryKey;
                        if (built == null)
                            continue;

                        batchRows.Add(built);
                        builtIdentities.Add(identity);
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

                    // Only advance after a fully accepted page (or empty push page: hash-skips / PDF skips).
                    if (!string.IsNullOrEmpty(lastWm))
                        _watermarks.Advance(cfg.View.ViewName, lastWm, lastIncPk);

                    if (rows.Count < pageSize)
                        break;
                }

                // 平时从水位增量。每隔一段时间再全表补一次，接住后放入视图、但 updated_at 更早的历史行。
                int reconcileMinutes = cfg.Sync.ReconcileIntervalMinutes;
                bool reconcileDue = !wmRec.LastFullReconcileAt.HasValue
                    || reconcileMinutes <= 0
                    || DateTime.UtcNow - wmRec.LastFullReconcileAt.Value >= TimeSpan.FromMinutes(reconcileMinutes);
                bool needReconcile = cfg.Sync.ReconcileEnabled && reconcileDue;
                int viewCount = 0;
                int visited = 0;
                int skippedUnchanged = 0;
                if (needReconcile)
                {
                    viewCount = await _reader.CountAsync(cfg.Sql, cfg.View, ct).ConfigureAwait(false);
                    _log.Info("Reconcile start view=" + cfg.View.ViewName
                        + " viewCount=" + viewCount);
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
                            if (!string.IsNullOrEmpty(identity)
                                && !_rowHashes.HasChanged(identity, rowHash))
                            {
                                skippedUnchanged++;
                                continue;
                            }

                            var built = await TryBuildSyncRowAsync(
                                    cfg, pipeline, row, rowHash, ct,
                                    () => { pdfFail++; }).ConfigureAwait(false);
                            // PDF 失败：本行不推、不写哈希；下轮/下次全表补齐会再试（哈希未提交）。
                            if (built == null)
                                continue;

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

                // PageSize 对齐时 visited 常略大于扫描开始时的 viewCount（视图增长或最后一页凑整）。
                // 仅当 visited < viewCount 才算未扫完；否则会永远 INCOMPLETE、每轮强制全表补齐。
                if (needReconcile && visited < viewCount)
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
        /// 视图「物理行」身份：不按 person_id 去重。
        /// 同人可有多条检测（职业/健康、不同报告），用 person_id + updated_at + PDF 指纹区分，
        /// 本地 row-hashes 与云端 upsert 的 pk 都用此键，保证视图有几行就对应推几行。
        /// </summary>
        private static string RowIdentity(ViewRow row)
        {
            if (row == null) return "";
            string pk = row.PrimaryKey ?? "";
            string wm = row.Watermark ?? "";
            string pdf = PdfFingerprint(row.Fields);
            if (!string.IsNullOrEmpty(pk) && !string.IsNullOrEmpty(wm) && !string.IsNullOrEmpty(pdf))
                return pk + "|" + wm + "|" + pdf;
            if (!string.IsNullOrEmpty(pk) && !string.IsNullOrEmpty(wm))
                return pk + "|" + wm;
            if (!string.IsNullOrEmpty(pk) && !string.IsNullOrEmpty(pdf))
                return pk + "|" + pdf;
            if (!string.IsNullOrEmpty(pk))
                return pk;
            if (!string.IsNullOrEmpty(wm) && !string.IsNullOrEmpty(pdf))
                return wm + "|" + pdf;
            return !string.IsNullOrEmpty(wm) ? wm : pdf;
        }

        /// <summary>从 PDF 列取稳定短指纹（URL 规范化后 SHA256 前 12 位），区分同人不同报告。</summary>
        private static string PdfFingerprint(JObject fields)
        {
            if (fields == null) return "";
            string[] prefer = { "PDF", "pdf", "pdf_url", "PdfUrl" };
            foreach (var name in prefer)
            {
                JToken tok;
                if (!fields.TryGetValue(name, out tok) || tok == null || tok.Type == JTokenType.Null)
                    continue;
                string raw = tok.ToString();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                return ShortSha(NormalizePdfUrl(raw));
            }
            foreach (var prop in fields.Properties())
            {
                if (prop.Name == null || prop.Name.IndexOf("pdf", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (prop.Value == null || prop.Value.Type == JTokenType.Null) continue;
                string raw = prop.Value.ToString();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                return ShortSha(NormalizePdfUrl(raw));
            }
            return "";
        }

        private static string NormalizePdfUrl(string url)
        {
            return (url ?? "").Trim().Replace('\\', '/').ToLowerInvariant();
        }

        private static string ShortSha(string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
                var sb = new StringBuilder(12);
                for (int i = 0; i < 6; i++)
                    sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
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
            string cloudPk = RowIdentity(row);

            if (cfg.View.PdfUrlColumns != null)
            {
                var seenPdfCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var col in cfg.View.PdfUrlColumns)
                {
                    if (string.IsNullOrWhiteSpace(col)) continue;
                    if (!seenPdfCols.Add(col.Trim()))
                        continue; // 避免 PDF/pdf 重复列：第一次已把字段写成 oss_key，第二次会误当 URL 下载

                    JToken tok;
                    if (!row.Fields.TryGetValue(col, out tok) || tok == null || tok.Type == JTokenType.Null)
                        continue;

                    string intranetUrl = tok.ToString();
                    if (string.IsNullOrWhiteSpace(intranetUrl)) continue;

                    var resolved = await pipeline.ResolveAsync(intranetUrl, ct).ConfigureAwait(false);
                    if (!resolved.Success)
                    {
                        onPdfFail();
                        _failures.Enqueue(new FailureItem
                        {
                            ViewName = cfg.View.ViewName,
                            PrimaryKey = cloudPk,
                            PdfColumn = col,
                            PdfUrl = intranetUrl,
                            Error = resolved.Error
                        });

                        if (cfg.Sync.FailBatchOnPdfError)
                            throw new InvalidOperationException("PDF failed and FailBatchOnPdfError=true: " + resolved.Error);

                        // 默认：整行跳过（不推云、不提交行哈希），等全表补齐/下轮再试。
                        _log.Warn("PDF failed, skip row (no push/hash) pk=" + (cloudPk ?? "")
                            + " col=" + col + " err=" + resolved.Error);
                        return null;
                    }

                    // 字段与 pdf.url 只带 oss_key，不再写入预签名/公网访问链接
                    string keyOrUrl = !string.IsNullOrEmpty(resolved.OssKey) ? resolved.OssKey : resolved.CloudUrl;
                    row.Fields[col] = keyOrUrl;
                    pdfMeta[col] = new PdfMeta
                    {
                        Sha256 = resolved.Sha256,
                        OssKey = resolved.OssKey,
                        Url = keyOrUrl
                    };
                    _failures.Remove(cfg.View.ViewName, cloudPk, col);
                }
            }

            if (string.IsNullOrEmpty(cloudPk) && row.Fields != null)
            {
                JToken pkTok;
                if (row.Fields.TryGetValue(cfg.View.PrimaryKeyColumn ?? "", out pkTok) && pkTok != null && pkTok.Type != JTokenType.Null)
                    cloudPk = pkTok.ToString();
            }

            return new SyncRow
            {
                // 云端 upsert 键 = 物理行身份（含 PDF），禁止只按 person_id 合并
                PrimaryKey = cloudPk,
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
