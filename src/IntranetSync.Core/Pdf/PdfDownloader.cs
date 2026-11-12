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

            string temp = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".pdf");

            using (var handler = CreateHandler())
            using (var http = new HttpClient(handler))
            {
                http.Timeout = TimeSpan.FromSeconds(Math.Max(30, _settings.DownloadTimeoutSeconds));
                using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    long? length = resp.Content.Headers.ContentLength;
                    if (length.HasValue && length.Value > _settings.MaxBytes)
                        throw new InvalidOperationException("PDF exceeds max size: " + length.Value + " > " + _settings.MaxBytes);

                    using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
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
