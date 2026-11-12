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
            string url = Combine(_settings.ApiBaseUrl, "/sync/batch");
            string json = JsonConvert.SerializeObject(request);
            _log.Info("POST /sync/batch rows=" + (request.Rows == null ? 0 : request.Rows.Count));

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
            string url = Combine(_settings.ApiBaseUrl, "/sync/oss-token");
            _log.Info("GET /sync/oss-token");
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
            string url = Combine(_settings.ApiBaseUrl, "/sync/pdf/" + Uri.EscapeDataString(sha256));
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
            string endpoint = Combine(_settings.ApiBaseUrl, "/sync/pdf/complete");
            var payload = new { sha256 = sha256, ossKey = ossKey, url = url, tenantId = _settings.TenantId };
            using (var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json"))
            using (var resp = await _http.PostAsync(endpoint, content, ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode)
                    _log.Warn("pdf/complete register soft-fail status=" + (int)resp.StatusCode);
            }
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
