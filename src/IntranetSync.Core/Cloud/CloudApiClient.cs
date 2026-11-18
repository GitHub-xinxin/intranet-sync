using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IntranetSync.Core.Config;
using IntranetSync.Core.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
            // Per-request CancelAfter is the primary timeout; keep HttpClient.Timeout as a backstop.
            _http = new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            ApplyAuth();
        }

        public void UpdateSettings(CloudSettings settings)
        {
            _settings = settings ?? new CloudSettings();
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
            int rows = request.Rows == null ? 0 : request.Rows.Count;
            int attempts = Math.Max(1, _settings.BatchRetryCount);
            int delaySec = Math.Max(1, _settings.BatchRetryDelaySeconds);
            Exception last = null;

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                _log.Info("POST batch url=" + url + " rows=" + rows +
                          (attempts > 1 ? " attempt=" + attempt + "/" + attempts : ""));

                try
                {
                    return await PostBatchOnceAsync(url, json, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (!IsTransientBatchFailure(ex, ct) || attempt >= attempts)
                        throw;

                    int wait = delaySec * attempt;
                    _log.Warn("POST batch transient failure attempt=" + attempt + "/" + attempts +
                              " wait=" + wait + "s: " + ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);
                }
            }

            throw last ?? new InvalidOperationException("Cloud batch failed with no response");
        }

        private async Task<SyncBatchResponse> PostBatchOnceAsync(string url, string json, CancellationToken ct)
        {
            int timeoutSec = Math.Max(10, _settings.TimeoutSeconds);
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
                try
                {
                    using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                    using (var resp = await _http.PostAsync(url, content, timeoutCts.Token).ConfigureAwait(false))
                    {
                        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        _log.Info("POST batch response status=" + (int)resp.StatusCode + " body=" + Truncate(body));
                        if (!resp.IsSuccessStatusCode)
                        {
                            _log.Error("batch upsert failed status=" + (int)resp.StatusCode + " body=" + Truncate(body));
                            var ex = new InvalidOperationException("Cloud batch failed: HTTP " + (int)resp.StatusCode);
                            ex.Data["httpStatus"] = (int)resp.StatusCode;
                            throw ex;
                        }

                        return ParseBatchResponse(body);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        "Cloud batch timed out after " + timeoutSec + "s (no response from " + url + ")");
                }
            }
        }

        /// <summary>
        /// Timeout / network / 5xx are retryable. Caller cancel and 4xx are not.
        /// </summary>
        private static bool IsTransientBatchFailure(Exception ex, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                return false;
            if (ex is TimeoutException)
                return true;
            if (ex is OperationCanceledException)
                return true;
            if (ex is HttpRequestException)
                return true;
            if (ex is InvalidOperationException && ex.Data != null && ex.Data.Contains("httpStatus"))
            {
                int status = Convert.ToInt32(ex.Data["httpStatus"]);
                return status == 408 || status == 425 || status == 429 || status >= 500;
            }
            return false;
        }

        public async Task<OssTokenResponse> GetOssTokenAsync(CancellationToken ct)
        {
            string url = ResolveOssTokenUrl();
            _log.Info("GET oss-token url=" + url);
            using (var timeoutCts = CreateTimeoutCts(ct))
            {
                try
                {
                    using (var resp = await _http.GetAsync(url, timeoutCts.Token).ConfigureAwait(false))
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
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("OSS token request timed out");
                }
            }
        }

        public async Task<PdfLookupResponse> LookupPdfAsync(string sha256, CancellationToken ct)
        {
            string root = ResolveApiRoot();
            string url = Combine(root, "/sync/pdf/" + Uri.EscapeDataString(sha256));
            using (var timeoutCts = CreateTimeoutCts(ct))
            {
                try
                {
                    using (var resp = await _http.GetAsync(url, timeoutCts.Token).ConfigureAwait(false))
                    {
                        if (resp.StatusCode == HttpStatusCode.NotFound)
                            return new PdfLookupResponse { Found = false, Sha256 = sha256 };

                        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode)
                            return new PdfLookupResponse { Found = false, Sha256 = sha256 };

                        var result = JsonConvert.DeserializeObject<PdfLookupResponse>(body) ?? new PdfLookupResponse();
                        result.Sha256 = sha256;
                        return result;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _log.Warn("PDF lookup timed out sha256=" + sha256);
                    return new PdfLookupResponse { Found = false, Sha256 = sha256 };
                }
            }
        }

        public async Task RegisterPdfAsync(string sha256, string ossKey, string url, CancellationToken ct)
        {
            string root = ResolveApiRoot();
            string endpoint = Combine(root, "/sync/pdf/complete");
            var payload = new { sha256 = sha256, ossKey = ossKey, url = url, tenantId = _settings.TenantId };
            using (var timeoutCts = CreateTimeoutCts(ct))
            {
                try
                {
                    using (var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json"))
                    using (var resp = await _http.PostAsync(endpoint, content, timeoutCts.Token).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                            _log.Warn("pdf/complete register soft-fail status=" + (int)resp.StatusCode);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _log.Warn("pdf/complete register soft-fail: timed out");
                }
                catch (Exception ex)
                {
                    _log.Warn("pdf/complete register soft-fail: " + ex.Message);
                }
            }
        }

        private CancellationTokenSource CreateTimeoutCts(CancellationToken ct)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, _settings.TimeoutSeconds)));
            return cts;
        }

        private static SyncBatchResponse ParseBatchResponse(string body)
        {
            var parsed = new SyncBatchResponse();
            if (string.IsNullOrWhiteSpace(body))
                return parsed;

            JObject jo;
            try
            {
                jo = JObject.Parse(body);
            }
            catch
            {
                return parsed;
            }

            var direct = JsonConvert.DeserializeObject<SyncBatchResponse>(body);
            if (direct != null)
            {
                parsed.Accepted = direct.Accepted;
                parsed.Rejected = direct.Rejected;
                parsed.Message = direct.Message;
            }

            if (parsed.Accepted <= 0)
            {
                int n;
                if (TryReadCount(jo, out n) || TryReadCount(jo["data"] as JObject, out n) || TryReadCount(jo["result"] as JObject, out n))
                    parsed.Accepted = n;
            }

            return parsed;
        }

        private static bool TryReadCount(JObject jo, out int count)
        {
            count = 0;
            if (jo == null) return false;
            string[] keys = { "accepted", "imported", "importCount", "successCount", "success", "count", "total" };
            foreach (var key in keys)
            {
                JToken tok;
                if (!jo.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out tok) || tok == null)
                    continue;
                if (tok.Type == JTokenType.Integer || tok.Type == JTokenType.Float)
                {
                    count = tok.Value<int>();
                    return count > 0;
                }
            }
            return false;
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
