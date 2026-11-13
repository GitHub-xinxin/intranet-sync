using System;
using System.IO;
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
        private readonly OssSettings _oss;
        private readonly PdfHashMap _map;
        private readonly CloudApiClient _api;
        private readonly SyncLogger _log;
        private readonly string _tempDir;
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
            _oss = ossSettings ?? new OssSettings();
            _map = map;
            _api = api;
            _log = log;
            _tempDir = Path.Combine(dataDirectory, "pdf-tmp");
            Directory.CreateDirectory(_tempDir);
            int conc = pdfSettings != null && pdfSettings.MaxConcurrency > 0 ? pdfSettings.MaxConcurrency : 3;
            _gate = new SemaphoreSlim(Math.Min(Math.Max(conc, 1), 8));
        }

        public async Task<PdfResolveResult> ResolveAsync(string intranetUrl, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(intranetUrl))
            {
                return new PdfResolveResult { Success = true, CloudUrl = null };
            }

            string url = PdfUrlUtil.Normalize(intranetUrl);

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Source already points at destination private OSS — do not anonymous-GET (403).
                if (PdfUrlUtil.IsConfiguredOssUrl(url, _oss))
                {
                    string key = PdfUrlUtil.ExtractOssKeyFromConfiguredUrl(url, _oss);
                    _log.Info("PDF already on configured OSS, skip download/upload key=" + (key ?? "(unknown)"));
                    string access = await ResolveAccessUrlAsync(key, url, ct).ConfigureAwait(false);
                    return new PdfResolveResult
                    {
                        Success = true,
                        CloudUrl = access,
                        OssKey = key,
                        SkippedDuplicate = true
                    };
                }

                using (var downloaded = await AcquirePdfAsync(url, ct).ConfigureAwait(false))
                {
                    PdfMapEntry local;
                    if (_map.TryGet(downloaded.Sha256, out local) &&
                        (!string.IsNullOrEmpty(local.OssKey) || !string.IsNullOrEmpty(local.Url)))
                    {
                        string access = await ResolveAccessUrlAsync(local.OssKey, local.Url, ct).ConfigureAwait(false);
                        return new PdfResolveResult
                        {
                            Success = true,
                            CloudUrl = access,
                            Sha256 = downloaded.Sha256,
                            OssKey = local.OssKey,
                            SkippedDuplicate = true
                        };
                    }

                    // optional cloud lookup
                    try
                    {
                        var remote = await _api.LookupPdfAsync(downloaded.Sha256, ct).ConfigureAwait(false);
                        if (remote != null && remote.Found &&
                            (!string.IsNullOrEmpty(remote.Url) || !string.IsNullOrEmpty(remote.OssKey)))
                        {
                            string access = await ResolveAccessUrlAsync(remote.OssKey, remote.Url, ct).ConfigureAwait(false);
                            _map.Put(downloaded.Sha256, remote.OssKey, access);
                            return new PdfResolveResult
                            {
                                Success = true,
                                CloudUrl = access,
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
                _log.Error("PDF pipeline failed for url=" + url, ex);
                return new PdfResolveResult { Success = false, Error = ex.Message };
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Prefer a fresh private-bucket signed URL when enabled; fall back to stored/static URL.
        /// </summary>
        private async Task<string> ResolveAccessUrlAsync(string ossKey, string fallbackUrl, CancellationToken ct)
        {
            if (_oss.UseSignedUrl && !string.IsNullOrWhiteSpace(ossKey))
            {
                try
                {
                    return await _uploader.BuildAccessUrlAsync(ossKey, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Warn("PDF signed URL soft-fail: " + ex.Message);
                    if (!string.IsNullOrEmpty(fallbackUrl))
                        return fallbackUrl;
                    throw;
                }
            }

            if (!string.IsNullOrEmpty(fallbackUrl))
                return fallbackUrl;

            if (!string.IsNullOrWhiteSpace(ossKey))
                return await _uploader.BuildAccessUrlAsync(ossKey, ct).ConfigureAwait(false);

            return fallbackUrl;
        }

        private async Task<DownloadedPdf> AcquirePdfAsync(string url, CancellationToken ct)
        {
            OssUrlParts oss;
            if (PdfUrlUtil.TryParseOssUrl(url, out oss))
            {
                // Private (or public) OSS object URL in source — pull with AK/STS instead of anonymous HTTP.
                _log.Info("PDF source is OSS object, credential download bucket=" + oss.Bucket + " key=" + oss.Key);
                string temp = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".pdf");
                try
                {
                    await _uploader.DownloadObjectAsync(oss.Bucket, oss.Key, temp, oss.EndpointHost, ct).ConfigureAwait(false);
                    return await _downloader.IngestLocalFileAsync(temp, ct).ConfigureAwait(false);
                }
                catch
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    throw;
                }
            }

            return await _downloader.DownloadAsync(url, ct).ConfigureAwait(false);
        }
    }
}
