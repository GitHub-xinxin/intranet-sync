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
        public int PdfFailures { get; set; }
        public string Message { get; set; }
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// Incremental sync: watermark → paged SELECT → PDF pipeline → batch upsert.
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

            using (var api = new CloudApiClient(cfg.Cloud, _log))
            {
                var pipeline = new PdfPipeline(cfg.Pdf, cfg.Oss, cfg.Cloud, api, _pdfMap, _log, _dataDir);
                bool useIncremental = !string.IsNullOrWhiteSpace(cfg.View.IncrementalColumn);
                var wmRec = _watermarks.Get(cfg.View.ViewName);
                string cursor = wmRec.LastWatermark ?? "";

                int totalRead = 0;
                int totalUpsert = 0;
                int pdfFail = 0;
                string lastWm = cursor;
                const int maxPages = 100;

                for (int page = 0; page < maxPages; page++)
                {
                    ct.ThrowIfCancellationRequested();
                    var rows = await _reader.ReadPageAsync(cfg.Sql, cfg.View, lastWm, ct).ConfigureAwait(false);
                    if (rows == null || rows.Count == 0)
                        break;

                    totalRead += rows.Count;
                    var batchRows = new List<SyncRow>();

                    foreach (var row in rows)
                    {
                        ct.ThrowIfCancellationRequested();
                        string rowHash = RowHasher.Compute(row.PrimaryKey, row.Fields);

                        if (!useIncremental)
                        {
                            if (!_rowHashes.HasChanged(row.PrimaryKey, rowHash))
                            {
                                lastWm = row.Watermark ?? lastWm;
                                continue;
                            }
                        }

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
                                    pdfFail++;
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

                                    // leave original URL out of cloud payload — skip field replacement
                                    continue;
                                }

                                // Replace with cloud URL only (never PDF bytes)
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
                            continue;

                        batchRows.Add(new SyncRow
                        {
                            PrimaryKey = row.PrimaryKey,
                            Watermark = row.Watermark,
                            RowHash = rowHash,
                            Fields = row.Fields,
                            Pdf = pdfMeta.Count > 0 ? pdfMeta : null
                        });

                        lastWm = row.Watermark ?? lastWm;
                    }

                    if (batchRows.Count > 0)
                    {
                        var req = new SyncBatchRequest
                        {
                            TenantId = cfg.Cloud.TenantId,
                            ViewName = cfg.View.ViewName,
                            Rows = batchRows
                        };
                        var resp = await api.PostBatchAsync(req, ct).ConfigureAwait(false);
                        totalUpsert += resp.Accepted > 0 ? resp.Accepted : batchRows.Count;

                        foreach (var br in batchRows)
                        {
                            if (!useIncremental)
                                _rowHashes.Commit(br.PrimaryKey, br.RowHash);
                        }
                        _rowHashes.Persist();
                    }

                    // Advance watermark after each successful page
                    if (useIncremental && !string.IsNullOrEmpty(lastWm))
                        _watermarks.Advance(cfg.View.ViewName, lastWm);
                    else if (!useIncremental && !string.IsNullOrEmpty(lastWm))
                        _watermarks.Advance(cfg.View.ViewName, lastWm);

                    if (rows.Count < (cfg.View.PageSize > 0 ? cfg.View.PageSize : 300))
                        break;
                }

                sw.Stop();
                result.Success = true;
                result.RowsRead = totalRead;
                result.RowsUpserted = totalUpsert;
                result.PdfFailures = pdfFail;
                result.Duration = sw.Elapsed;
                result.Message = string.Format(
                    "OK read={0} upsert={1} pdfFail={2} elapsed={3}ms",
                    totalRead, totalUpsert, pdfFail, (int)sw.Elapsed.TotalMilliseconds);
                _log.Info(result.Message);
                return result;
            }
        }
    }
}
