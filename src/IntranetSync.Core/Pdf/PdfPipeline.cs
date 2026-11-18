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
        /// <summary>兼容旧字段；云端不使用访问链接时与 OssKey 相同或为空。</summary>
        public string CloudUrl { get; set; }
        public string Sha256 { get; set; }
        public string OssKey { get; set; }
        public string Error { get; set; }
        public bool SkippedDuplicate { get; set; }
    }

    /// <summary>
    /// Download → SHA256 → local/cloud dedupe → stream upload OSS.
    /// 只保证对象上到 OSS（返回 ossKey）；不再签发/拼装 PDF 访问链接（云端不使用）。
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
                // 已是 oss_key（非绝对 URL）或已指向目标桶：直接跳过下载/上传。
                if (PdfUrlUtil.IsBareOssObjectKey(url, _oss) || PdfUrlUtil.IsConfiguredOssUrl(url, _oss))
                {
                    string key = PdfUrlUtil.ExtractOssKeyFromConfiguredUrl(url, _oss) ?? url;
                    _log.Info("PDF already on configured OSS, skip download/upload key=" + (key ?? "(unknown)"));
                    return OkKey(key, null, skipped: true);
                }

                using (var downloaded = await AcquirePdfAsync(url, ct).ConfigureAwait(false))
                {
                    PdfMapEntry local;
                    if (_map.TryGet(downloaded.Sha256, out local) &&
                        (!string.IsNullOrEmpty(local.OssKey) || !string.IsNullOrEmpty(local.Url)))
                    {
                        string key = !string.IsNullOrEmpty(local.OssKey) ? local.OssKey : local.Url;
                        _log.Info("PDF already on configured OSS, skip download/upload key=" + (key ?? "(unknown)"));
                        return OkKey(key, downloaded.Sha256, skipped: true);
                    }

                    // optional cloud lookup
                    try
                    {
                        var remote = await _api.LookupPdfAsync(downloaded.Sha256, ct).ConfigureAwait(false);
                        if (remote != null && remote.Found &&
                            (!string.IsNullOrEmpty(remote.Url) || !string.IsNullOrEmpty(remote.OssKey)))
                        {
                            string key = !string.IsNullOrEmpty(remote.OssKey) ? remote.OssKey : remote.Url;
                            _map.Put(downloaded.Sha256, remote.OssKey, key);
                            _log.Info("PDF found via cloud lookup, skip upload key=" + (key ?? "(unknown)"));
                            return OkKey(key, downloaded.Sha256, skipped: true);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("PDF cloud lookup soft-fail: " + ex.Message);
                    }

                    var uploaded = await _uploader.UploadAsync(downloaded.TempPath, downloaded.Sha256, ct).ConfigureAwait(false);
                    _map.Put(downloaded.Sha256, uploaded.OssKey, uploaded.OssKey);

                    try
                    {
                        await _api.RegisterPdfAsync(downloaded.Sha256, uploaded.OssKey, uploaded.OssKey, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("PDF register soft-fail: " + ex.Message);
                    }

                    return OkKey(uploaded.OssKey, downloaded.Sha256, skipped: false);
                }
            }
            catch (OperationCanceledException)
            {
                // 整轮超时 / 停止服务：不算 PDF 业务失败，交给上层结束本轮，下轮自动重试。
                throw;
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

        private static PdfResolveResult OkKey(string ossKey, string sha256, bool skipped)
        {
            return new PdfResolveResult
            {
                Success = true,
                OssKey = ossKey,
                // 云端不用访问链接：字段里放 ossKey，便于对照；不再签 GET URL
                CloudUrl = ossKey,
                Sha256 = sha256,
                SkippedDuplicate = skipped
            };
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
