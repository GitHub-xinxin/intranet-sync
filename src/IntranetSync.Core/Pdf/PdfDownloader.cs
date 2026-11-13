using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IntranetSync.Core.Config;
using IntranetSync.Core.Logging;

namespace IntranetSync.Core.Pdf
{
    public sealed class DownloadedPdf : IDisposable
    {
        public string TempPath { get; set; }
        public string Sha256 { get; set; }
        public long Length { get; set; }

        public void Dispose()
        {
            try
            {
                if (!string.IsNullOrEmpty(TempPath) && File.Exists(TempPath))
                    File.Delete(TempPath);
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Streams intranet PDF to a temp file while computing SHA256. Never loads whole file into memory.
    /// </summary>
    public sealed class PdfDownloader
    {
        private readonly PdfSettings _settings;
        private readonly SyncLogger _log;
        private readonly string _tempDir;

        public PdfDownloader(PdfSettings settings, SyncLogger log, string dataDirectory)
        {
            _settings = settings ?? new PdfSettings();
            _log = log;
            _tempDir = Path.Combine(dataDirectory, "pdf-tmp");
            Directory.CreateDirectory(_tempDir);
        }

        public async Task<DownloadedPdf> DownloadAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("PDF URL is empty");

            url = PdfUrlUtil.Normalize(url);
            string temp = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".pdf");

            using (var handler = CreateHandler())
            using (var http = new HttpClient(handler))
            {
                http.Timeout = TimeSpan.FromSeconds(Math.Max(30, _settings.DownloadTimeoutSeconds));
                using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        string hint = "";
                        OssUrlParts ossParts;
                        if ((int)resp.StatusCode == 403 && PdfUrlUtil.TryParseOssUrl(url, out ossParts))
                        {
                            hint = " (private OSS URL cannot be fetched anonymously; pipeline should use OSS credentials or skip if already uploaded)";
                        }
                        throw new HttpRequestException(
                            "Response status code does not indicate success: " + (int)resp.StatusCode + " (" + resp.StatusCode + ")." + hint);
                    }

                    long? length = resp.Content.Headers.ContentLength;
                    if (length.HasValue && length.Value > _settings.MaxBytes)
                        throw new InvalidOperationException("PDF exceeds max size: " + length.Value + " > " + _settings.MaxBytes);

                    using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    {
                        return await WriteHashedAsync(src, temp, ct).ConfigureAwait(false);
                    }
                }
            }
        }

        /// <summary>
        /// Hash an already-downloaded local PDF (e.g. fetched via OSS SDK) and take ownership of the temp file.
        /// </summary>
        public async Task<DownloadedPdf> IngestLocalFileAsync(string localPath, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
                throw new FileNotFoundException("PDF temp file missing", localPath);

            string temp = localPath;
            // Move into our temp dir if needed so DownloadedPdf disposal stays consistent.
            string expectedDir = Path.GetFullPath(_tempDir);
            string actualDir = Path.GetFullPath(Path.GetDirectoryName(localPath) ?? "");
            if (!string.Equals(expectedDir, actualDir, StringComparison.OrdinalIgnoreCase))
            {
                temp = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".pdf");
                File.Move(localPath, temp);
            }

            using (var src = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            {
                long len = src.Length;
                if (len > _settings.MaxBytes)
                {
                    try { File.Delete(temp); } catch { }
                    throw new InvalidOperationException("PDF exceeds max size: " + len + " > " + _settings.MaxBytes);
                }

                using (var sha = SHA256.Create())
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await src.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        sha.TransformBlock(buffer, 0, read, null, 0);
                    }
                    sha.TransformFinalBlock(buffer, 0, 0);
                    string hash = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                    _log.Info("PDF ingested bytes=" + total + " sha256=" + hash.Substring(0, 12) + "...");
                    return new DownloadedPdf { TempPath = temp, Sha256 = hash, Length = total };
                }
            }
        }

        private async Task<DownloadedPdf> WriteHashedAsync(Stream src, string temp, CancellationToken ct)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > _settings.MaxBytes)
                    {
                        fs.Close();
                        try { File.Delete(temp); } catch { }
                        throw new InvalidOperationException("PDF exceeds max size while streaming: " + total);
                    }
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    await fs.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                string hash = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                _log.Info("PDF downloaded bytes=" + total + " sha256=" + hash.Substring(0, 12) + "...");
                return new DownloadedPdf { TempPath = temp, Sha256 = hash, Length = total };
            }
        }

        private HttpClientHandler CreateHandler()
        {
            var handler = new HttpClientHandler();
            string mode = (_settings.AuthMode ?? "anonymous").ToLowerInvariant();
            if (mode == "ntlm" || mode == "windows")
            {
                handler.UseDefaultCredentials = true;
                handler.PreAuthenticate = true;
            }
            else if (mode == "basic" && !string.IsNullOrEmpty(_settings.BasicUser))
            {
                handler.Credentials = new NetworkCredential(_settings.BasicUser, _settings.BasicPassword);
                handler.PreAuthenticate = true;
            }
            return handler;
        }
    }
}
