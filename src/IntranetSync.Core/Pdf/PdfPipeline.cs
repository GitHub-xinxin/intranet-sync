using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using IntranetSync.Core.Cloud;
using IntranetSync.Core.Config;
using IntranetSync.Core.Logging;

namespace IntranetSync.Core.Pdf
{
    public sealed class PdfResolveResult
    {
        public bool Success { get; set; }
        public string CloudUrl { get; set; }
        public string Sha256 { get; set; }
        public string OssKey { get; set; }
        public string Error { get; set; }
        public bool SkippedDuplicate { get; set; }
    }

    /// <summary>
    /// Download → SHA256 → local/cloud dedupe → stream upload OSS → return cloud URL.
    /// Concurrency limited (default 2–3). Never places PDF bytes into DB/JSON payloads.
    /// </summary>
    public sealed class PdfPipeline
    {
        private readonly PdfDownloader _downloader;
        private readonly OssUploader _uploader;
        private readonly PdfHashMap _map;
        private readonly CloudApiClient _api;
        private readonly SyncLogger _log;
        private readonly SemaphoreSlim _gate;

        public PdfPipeline(
            PdfSettings pdfSettings,
            OssSettings ossSettings,
            CloudSettings cloudSettings,
            CloudApiClient api,
            PdfHashMap map,
            SyncLogger log,
            string dataDirectory)
        {
            _downloader = new PdfDownloader(pdfSettings, log, dataDirectory);
            _uploader = new OssUploader(ossSettings, cloudSettings, api, log);
            _map = map;
            _api = api;
            _log = log;
            int conc = pdfSettings != null && pdfSettings.MaxConcurrency > 0 ? pdfSettings.MaxConcurrency : 3;
            _gate = new SemaphoreSlim(Math.Min(Math.Max(conc, 1), 8));
        }

        public async Task<PdfResolveResult> ResolveAsync(string intranetUrl, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(intranetUrl))
            {
                return new PdfResolveResult { Success = true, CloudUrl = null };
            }

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using (var downloaded = await _downloader.DownloadAsync(intranetUrl, ct).ConfigureAwait(false))
                {
                    PdfMapEntry local;
                    if (_map.TryGet(downloaded.Sha256, out local) && !string.IsNullOrEmpty(local.Url))
                    {
                        return new PdfResolveResult
                        {
                            Success = true,
                            CloudUrl = local.Url,
                            Sha256 = downloaded.Sha256,
                            OssKey = local.OssKey,
                            SkippedDuplicate = true
                        };
                    }

                    // optional cloud lookup
                    try
                    {
                        var remote = await _api.LookupPdfAsync(downloaded.Sha256, ct).ConfigureAwait(false);
                        if (remote != null && remote.Found && !string.IsNullOrEmpty(remote.Url))
                        {
                            _map.Put(downloaded.Sha256, remote.OssKey, remote.Url);
                            return new PdfResolveResult
                            {
                                Success = true,
                                CloudUrl = remote.Url,
                                Sha256 = downloaded.Sha256,
                                OssKey = remote.OssKey,
                                SkippedDuplicate = true
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("PDF cloud lookup soft-fail: " + ex.Message);
                    }

                    var uploaded = await _uploader.UploadAsync(downloaded.TempPath, downloaded.Sha256, ct).ConfigureAwait(false);
                    _map.Put(downloaded.Sha256, uploaded.OssKey, uploaded.Url);

                    try
                    {
                        await _api.RegisterPdfAsync(downloaded.Sha256, uploaded.OssKey, uploaded.Url, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("PDF register soft-fail: " + ex.Message);
                    }

                    return new PdfResolveResult
                    {
                        Success = true,
                        CloudUrl = uploaded.Url,
                        Sha256 = downloaded.Sha256,
                        OssKey = uploaded.OssKey
                    };
                }
            }
            catch (Exception ex)
            {
                _log.Error("PDF pipeline failed for url=" + intranetUrl, ex);
                return new PdfResolveResult { Success = false, Error = ex.Message };
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
