using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        public int RowsPdfRetried { get; set; }
        public int ViewCount { get; set; }
        public int PendingPdfFailures { get; set; }
        public int PdfFailures { get; set; }
        public string Message { get; set; }
        public TimeSpan Duration { get; set; }

        /// <summary>兼容旧管理台字段名；现为 PDF 重试成功条数。</summary>
        public int RowsReconciled
        {
            get { return RowsPdfRetried; }
            set { RowsPdfRetried = value; }
        }

        /// <summary>兼容旧字段；已不再做全表扫描。</summary>
        public int RowsVisited { get; set; }
    }

    /// <summary>
    /// 检测记录同步：
    /// 1) person_id 作为唯一键（云端 upsert / 本地行身份）
    /// 2) updated_at 单调水位增量拉取（报告修改会推高 updated_at，再次 upsert）
    /// 3) PDF 失败入队，本轮末尾按 person_id 回查重试（不再做 15 分钟全表扫描）
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
            if (string.IsNullOrWhiteSpace(cfg.View.PrimaryKeyColumn))
            {
                cfg.View.PrimaryKeyColumn = "person_id";
                _log.Warn("PrimaryKeyColumn 为空，默认使用 person_id 作为检测记录唯一键");
            }

            if (!useIncremental)
            {
                result.Message = "必须配置增量列 updated_at（报告生成/修改时间水位）";
                return result;
            }

            int purged = _rowHashes.PurgeLegacyCompositeKeys();
            if (purged > 0)
                _log.Info("已清理旧复合行身份哈希 " + purged + " 条（现仅用 person_id）");

            using (var api = new CloudApiClient(cfg.Cloud, _log))
            {
                var pipeline = new PdfPipeline(cfg.Pdf, cfg.Oss, cfg.Cloud, api, _pdfMap, _log, _dataDir);
                var wmRec = _watermarks.Get(cfg.View.ViewName);
                string cursor = wmRec.LastWatermark ?? "";
                string lastPk = wmRec.LastPrimaryKey ?? "";

                int totalRead = 0;
                int totalUpsert = 0;
                int pdfFail = 0;
                int pdfRetried = 0;
                string lastWm = cursor;
                string lastIncPk = lastPk;
                const int maxPages = 100;
                int pageSize = cfg.View.PageSize > 0 ? cfg.View.PageSize : 300;

                // ---- Phase 1: updated_at 水位增量（修改报告会抬高 updated_at，按 person_id upsert）----
                for (int page = 0; page < maxPages; page++)
                {
                    ct.ThrowIfCancellationRequested();
                    IList<ViewRow> rows = await _reader.ReadPageAsync(cfg.Sql, cfg.View, lastWm, lastIncPk, ct)
                        .ConfigureAwait(false);

                    if (rows == null || rows.Count == 0)
                        break;

                    totalRead += rows.Count;
                    var batchRows = new List<SyncRow>();
                    var builtIdentities = new List<string>();

                    foreach (var row in rows)
                    {
                        ct.ThrowIfCancellationRequested();
                        string identity = RowIdentity(row);
                        if (string.IsNullOrEmpty(identity))
                        {
                            _log.Warn("跳过无 person_id 的行 wm=" + (row.Watermark ?? ""));
                            lastWm = row.Watermark ?? lastWm;
                            continue;
                        }

                        string rowHash = RowHasher.Compute(identity, row.Fields);
                        var built = await TryBuildSyncRowAsync(
                                cfg, pipeline, row, identity, rowHash, ct,
                                () => { pdfFail++; }).ConfigureAwait(false);

                        // PDF 失败：不推送、不提交哈希；水位仍前进，避免卡死；靠失败队列定时重试闭环。
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

                    if (!string.IsNullOrEmpty(lastWm))
                        _watermarks.Advance(cfg.View.ViewName, lastWm, lastIncPk);

                    if (rows.Count < pageSize)
                        break;
                }

                // ---- Phase 2: 定时重试此前 PDF 失败的 person_id（替代 15 分钟全表扫描）----
                if (cfg.Sync.PdfRetryEnabled)
                {
                    pdfRetried = await RetryFailedPdfsAsync(cfg, api, pipeline, ct, () => { pdfFail++; })
                        .ConfigureAwait(false);
                    totalUpsert += pdfRetried;
                }

                int viewCount = 0;
                try
                {
                    viewCount = await _reader.CountAsync(cfg.Sql, cfg.View, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Warn("COUNT view failed: " + ex.Message);
                }

                int pending = _failures.CountForView(cfg.View.ViewName);

                sw.Stop();
                result.RowsRead = totalRead;
                result.RowsUpserted = totalUpsert;
                result.RowsPdfRetried = pdfRetried;
                result.ViewCount = viewCount;
                result.PendingPdfFailures = pending;
                result.PdfFailures = pdfFail;
                result.Duration = sw.Elapsed;
                result.Success = true;
                result.Message = string.Format(
                    "OK mode=watermark+pdfRetry viewCount={0} upsert={1} pdfRetry={2} pdfFail={3} pendingPdf={4} syncedHashes={5} elapsed={6}ms",
                    viewCount, totalUpsert, pdfRetried, pdfFail, pending, _rowHashes.Count, (int)sw.Elapsed.TotalMilliseconds);
                _log.Info(result.Message);

                if (pending > 0)
                {
                    _log.Warn("闭环未完成：视图 " + viewCount + " 行，仍有 " + pending
                        + " 条 PDF 失败待重试；成功后云端应按 person_id 与视图对齐");
                }

                return result;
            }
        }

        /// <summary>检测记录唯一身份 = person_id（PrimaryKeyColumn）。</summary>
        private static string RowIdentity(ViewRow row)
        {
            if (row == null) return "";
            if (!string.IsNullOrEmpty(row.PrimaryKey))
                return row.PrimaryKey.Trim();

            if (row.Fields == null) return "";
            string[] prefer = { "person_id", "PersonId", "PERSON_ID" };
            foreach (var name in prefer)
            {
                JToken tok;
                if (!row.Fields.TryGetValue(name, out tok) || tok == null || tok.Type == JTokenType.Null)
                    continue;
                string v = tok.ToString();
                if (!string.IsNullOrWhiteSpace(v))
                    return v.Trim();
            }
            return "";
        }

        /// <summary>失败队列旧复合键 → person_id。</summary>
        private static string NormalizePersonIdKey(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = raw.Trim();
            int pipe = s.IndexOf('|');
            if (pipe > 0)
                s = s.Substring(0, pipe).Trim();
            return s;
        }

        /// <summary>
        /// 从失败队列按 person_id 回查视图，重新跑 PDF → upsert；成功则出队。
        /// </summary>
        private async Task<int> RetryFailedPdfsAsync(
            SyncConfig cfg,
            CloudApiClient api,
            PdfPipeline pipeline,
            CancellationToken ct,
            Action onPdfFail)
        {
            int take = cfg.Sync.PdfRetryBatchSize > 0 ? cfg.Sync.PdfRetryBatchSize : 100;
            var pending = _failures.ListForView(cfg.View.ViewName, take);
            if (pending == null || pending.Count == 0)
                return 0;

            // 旧队列可能存过 person_id|updated_at|pdf 复合键；重试只取第一段 person_id。
            var keys = pending
                .Where(x => !string.IsNullOrWhiteSpace(x.PrimaryKey))
                .Select(x => NormalizePersonIdKey(x.PrimaryKey))
                .Where(k => !string.IsNullOrEmpty(k))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (keys.Count == 0)
                return 0;

            _log.Info("PDF retry start view=" + cfg.View.ViewName
                + " failures=" + pending.Count + " distinctPk=" + keys.Count);

            IList<ViewRow> rows = await _reader.ReadByPrimaryKeysAsync(cfg.Sql, cfg.View, keys, ct)
                .ConfigureAwait(false);

            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var batchRows = new List<SyncRow>();
            var builtIdentities = new List<string>();
            int upserted = 0;

            foreach (var row in rows ?? new ViewRow[0])
            {
                ct.ThrowIfCancellationRequested();
                string identity = RowIdentity(row);
                if (string.IsNullOrEmpty(identity))
                    continue;
                found.Add(identity);

                string rowHash = RowHasher.Compute(identity, row.Fields);
                var built = await TryBuildSyncRowAsync(
                        cfg, pipeline, row, identity, rowHash, ct, onPdfFail).ConfigureAwait(false);
                if (built == null)
                    continue;

                batchRows.Add(built);
                builtIdentities.Add(identity);

                if (batchRows.Count >= (cfg.View.PageSize > 0 ? cfg.View.PageSize : 300))
                {
                    upserted += await FlushRetryBatchAsync(api, cfg, batchRows, builtIdentities, ct)
                        .ConfigureAwait(false);
                    batchRows.Clear();
                    builtIdentities.Clear();
                }
            }

            if (batchRows.Count > 0)
            {
                upserted += await FlushRetryBatchAsync(api, cfg, batchRows, builtIdentities, ct)
                    .ConfigureAwait(false);
            }

            // 视图中已不存在的 person_id：视为过期失败项，直接出队，避免永久占坑。
            foreach (var key in keys)
            {
                if (found.Contains(key))
                    continue;
                _failures.RemoveAllForPrimaryKey(cfg.View.ViewName, key);
                _log.Warn("PDF retry: person_id 已不在视图，移除失败项 pk=" + key);
            }

            _log.Info("PDF retry done upsert=" + upserted + " pending=" + _failures.CountForView(cfg.View.ViewName));
            return upserted;
        }

        private async Task<int> FlushRetryBatchAsync(
            CloudApiClient api,
            SyncConfig cfg,
            List<SyncRow> batchRows,
            List<string> builtIdentities,
            CancellationToken ct)
        {
            int accepted = await PostBatchStrictAsync(api, cfg, batchRows, ct).ConfigureAwait(false);
            for (int i = 0; i < batchRows.Count; i++)
            {
                if (!string.IsNullOrEmpty(builtIdentities[i]))
                    _rowHashes.Commit(builtIdentities[i], batchRows[i].RowHash);
            }
            _rowHashes.Persist();
            return accepted;
        }

        private async Task<SyncRow> TryBuildSyncRowAsync(
            SyncConfig cfg,
            PdfPipeline pipeline,
            ViewRow row,
            string cloudPk,
            string rowHash,
            CancellationToken ct,
            Action onPdfFail)
        {
            var pdfMeta = new Dictionary<string, PdfMeta>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(cloudPk))
                cloudPk = RowIdentity(row);

            if (cfg.View.PdfUrlColumns != null)
            {
                var seenPdfCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var col in cfg.View.PdfUrlColumns)
                {
                    if (string.IsNullOrWhiteSpace(col)) continue;
                    if (!seenPdfCols.Add(col.Trim()))
                        continue;

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

                        _log.Warn("PDF failed, skip row (no push/hash) pk=" + (cloudPk ?? "")
                            + " col=" + col + " err=" + resolved.Error);
                        return null;
                    }

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

            return new SyncRow
            {
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
