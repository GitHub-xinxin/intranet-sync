using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IntranetSync.Core.Config;
using IntranetSync.Core.Logging;
using Newtonsoft.Json;

namespace IntranetSync.Core.Cloud
{
    /// <summary>
    /// HTTPS client for cloud sync API. Never logs Authorization header or tokens.
    /// Batch upsert posts to CloudSettings.ApiBaseUrl as a full URL (no path append).
    /// </summary>
    public sealed class CloudApiClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly SyncLogger _log;
        private CloudSettings _settings;

        public CloudApiClient(CloudSettings settings, SyncLogger log)
        {
            _settings = settings ?? new CloudSettings();
            _log = log;
            _http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(Math.Max(10, _settings.TimeoutSeconds))
            };
            ApplyAuth();
        }

        public void UpdateSettings(CloudSettings settings)
        {
            _settings = settings ?? new CloudSettings();
            _http.Timeout = TimeSpan.FromSeconds(Math.Max(10, _settings.TimeoutSeconds));
            ApplyAuth();
        }

        private void ApplyAuth()
        {
            _http.DefaultRequestHeaders.Authorization = null;
            if (!string.IsNullOrEmpty(_settings.ServiceToken))
            {
                _http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", _settings.ServiceToken);
            }
        }

        public async Task<SyncBatchResponse> PostBatchAsync(SyncBatchRequest request, CancellationToken ct)
        {
            string url = RequireUrl(_settings.ApiBaseUrl, "Cloud API batch URL");
            string json = JsonConvert.SerializeObject(request);
            _log.Info("POST batch url=" + url + " rows=" + (request.Rows == null ? 0 : request.Rows.Count));

            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            using (var resp = await _http.PostAsync(url, content, ct).ConfigureAwait(false))
            {
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    _log.Error("batch upsert failed status=" + (int)resp.StatusCode + " body=" + Truncate(body));
                    throw new InvalidOperationException("Cloud batch failed: HTTP " + (int)resp.StatusCode);
                }

                return JsonConvert.DeserializeObject<SyncBatchResponse>(body) ?? new SyncBatchResponse();
            }
        }

        public async Task<OssTokenResponse> GetOssTokenAsync(CancellationToken ct)
        {
            string url = ResolveOssTokenUrl();
            _log.Info("GET oss-token url=" + url);
            using (var resp = await _http.GetAsync(url, ct).ConfigureAwait(false))
            {
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    _log.Error("oss-token failed status=" + (int)resp.StatusCode);
                    throw new InvalidOperationException("OSS token failed: HTTP " + (int)resp.StatusCode);
                }

                return JsonConvert.DeserializeObject<OssTokenResponse>(body);
            }
        }

        public async Task<PdfLookupResponse> LookupPdfAsync(string sha256, CancellationToken ct)
        {
            string root = ResolveApiRoot();
            string url = Combine(root, "/sync/pdf/" + Uri.EscapeDataString(sha256));
            using (var resp = await _http.GetAsync(url, ct).ConfigureAwait(false))
            {
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return new PdfLookupResponse { Found = false, Sha256 = sha256 };

                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    return new PdfLookupResponse { Found = false, Sha256 = sha256 };

                var result = JsonConvert.DeserializeObject<PdfLookupResponse>(body) ?? new PdfLookupResponse();
                result.Sha256 = sha256;
                return result;
            }
        }

        public async Task RegisterPdfAsync(string sha256, string ossKey, string url, CancellationToken ct)
        {
            string root = ResolveApiRoot();
            string endpoint = Combine(root, "/sync/pdf/complete");
            var payload = new { sha256 = sha256, ossKey = ossKey, url = url, tenantId = _settings.TenantId };
            using (var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json"))
            using (var resp = await _http.PostAsync(endpoint, content, ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode)
                    _log.Warn("pdf/complete register soft-fail status=" + (int)resp.StatusCode);
            }
        }

        private string ResolveOssTokenUrl()
        {
            if (!string.IsNullOrWhiteSpace(_settings.OssTokenUrl))
                return _settings.OssTokenUrl.Trim();

            return Combine(ResolveApiRoot(), "/sync/oss-token");
        }

        /// <summary>
        /// Derive API root for auxiliary endpoints from batch URL or explicit OSS token URL.
        /// </summary>
        private string ResolveApiRoot()
        {
            string hint = !string.IsNullOrWhiteSpace(_settings.OssTokenUrl)
                ? _settings.OssTokenUrl.Trim()
                : (_settings.ApiBaseUrl ?? "").Trim();

            if (string.IsNullOrEmpty(hint))
                return "";

            string trimmed = hint.TrimEnd('/');
            string[] suffixes = { "/sync/batch", "/sync/oss-token", "/sync/pdf/complete" };
            foreach (var suffix in suffixes)
            {
                if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return trimmed.Substring(0, trimmed.Length - suffix.Length);
            }

            try
            {
                var uri = new Uri(trimmed);
                // Fall back to origin when custom batch path is used
                return uri.GetLeftPart(UriPartial.Authority);
            }
            catch
            {
                return trimmed;
            }
        }

        private static string RequireUrl(string url, string name)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException(name + " is not configured");
            return url.Trim();
        }

        private static string Combine(string baseUrl, string path)
        {
            return (baseUrl ?? "").TrimEnd('/') + path;
        }

        private static string Truncate(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= 400 ? s : s.Substring(0, 400) + "...";
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}
