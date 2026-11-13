using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Aliyun.OSS;
using IntranetSync.Core.Cloud;
using IntranetSync.Core.Config;
using IntranetSync.Core.Logging;

namespace IntranetSync.Core.Pdf
{
    public sealed class UploadResult
    {
        public string OssKey { get; set; }
        public string Url { get; set; }
    }

    /// <summary>
    /// Streams PDF to Aliyun OSS via STS credentials, or to mock upload URL from cloud stub.
    /// </summary>
    public sealed class OssUploader
    {
        private readonly OssSettings _oss;
        private readonly CloudSettings _cloud;
        private readonly CloudApiClient _api;
        private readonly SyncLogger _log;
        private OssTokenResponse _cachedToken;
        private readonly object _tokenGate = new object();

        public OssUploader(OssSettings oss, CloudSettings cloud, CloudApiClient api, SyncLogger log)
        {
            _oss = oss ?? new OssSettings();
            _cloud = cloud ?? new CloudSettings();
            _api = api;
            _log = log;
        }

        public async Task<UploadResult> UploadAsync(string localPath, string sha256, CancellationToken ct)
        {
            string prefix = string.IsNullOrEmpty(_oss.Prefix) ? "sync" : _oss.Prefix.Trim('/');
            string ossKey = string.Format("{0}/{1}/{2}.pdf", prefix, _cloud.TenantId ?? "default", sha256);

            OssTokenResponse token = await ResolveTokenAsync(ct).ConfigureAwait(false);

            if (token != null && token.Mock && !string.IsNullOrEmpty(token.UploadUrl))
            {
                return await MockUploadAsync(token, localPath, ossKey, sha256, ct).ConfigureAwait(false);
            }

            string endpoint;
            string bucket;
            string publicBase;
            var client = CreateClient(token, out endpoint, out bucket, out publicBase);

            using (var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            {
                var meta = new ObjectMetadata();
                meta.ContentType = "application/pdf";
                meta.ContentLength = fs.Length;
                // PutObject streams from the file handle
                client.PutObject(bucket, ossKey, fs, meta);
            }

            string url = BuildAccessUrl(client, token, endpoint, bucket, publicBase, ossKey);
            _log.Info("OSS uploaded key=" + ossKey + (_oss.UseSignedUrl ? " (signed)" : ""));
            return new UploadResult { OssKey = ossKey, Url = url };
        }

        /// <summary>
        /// Build cloud-facing PDF URL for an existing object (fresh signature when UseSignedUrl).
        /// </summary>
        public async Task<string> BuildAccessUrlAsync(string ossKey, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(ossKey))
                throw new ArgumentException("ossKey is empty");

            OssTokenResponse token = await ResolveTokenAsync(ct).ConfigureAwait(false);
            if (token != null && token.Mock)
            {
                string mockBase = !string.IsNullOrEmpty(token.PublicBaseUrl) ? token.PublicBaseUrl : _oss.PublicBaseUrl;
                return string.IsNullOrEmpty(mockBase)
                    ? ossKey
                    : mockBase.TrimEnd('/') + "/" + ossKey;
            }

            string endpoint;
            string bucket;
            string publicBase;
            var client = CreateClient(token, out endpoint, out bucket, out publicBase);
            return BuildAccessUrl(client, token, endpoint, bucket, publicBase, ossKey);
        }

        /// <summary>
        /// Download a private OSS object with STS/AK credentials (avoids anonymous HTTP 403).
        /// </summary>
        public async Task DownloadObjectAsync(string bucket, string ossKey, string localPath, string endpointHint, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(ossKey))
                throw new ArgumentException("OSS bucket/key required for credential download");

            ct.ThrowIfCancellationRequested();
            OssTokenResponse token = await ResolveTokenAsync(ct).ConfigureAwait(false);
            if (token != null && token.Mock)
                throw new InvalidOperationException("Cannot download private OSS object while OSS mock mode is enabled");

            string endpoint = (token != null && !string.IsNullOrEmpty(token.Endpoint)) ? token.Endpoint : _oss.Endpoint;
            if (!string.IsNullOrWhiteSpace(endpointHint))
                endpoint = endpointHint.Trim();
            if (string.IsNullOrEmpty(endpoint))
                throw new InvalidOperationException("OSS endpoint not configured");

            string ak = token != null ? token.AccessKeyId : _oss.AccessKeyId;
            string sk = token != null ? token.AccessKeySecret : _oss.AccessKeySecret;
            string st = token != null ? token.SecurityToken : _oss.SecurityToken;
            if (string.IsNullOrEmpty(ak) || string.IsNullOrEmpty(sk))
                throw new InvalidOperationException("OSS credentials not configured");

            OssClient client = !string.IsNullOrEmpty(st)
                ? new OssClient(endpoint, ak, sk, st)
                : new OssClient(endpoint, ak, sk);

            // GetObject is sync in Aliyun.OSS 2.x — offload to thread pool for net451.
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using (var obj = client.GetObject(bucket, ossKey))
                using (var src = obj.Content)
                using (var fs = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    src.CopyTo(fs);
                }
            }, ct).ConfigureAwait(false);

            _log.Info("OSS downloaded key=" + ossKey + " bucket=" + bucket);
        }

        private async Task<OssTokenResponse> ResolveTokenAsync(CancellationToken ct)
        {
            if (!_oss.UseSts)
                return null;
            return await GetTokenAsync(ct).ConfigureAwait(false);
        }

        private OssClient CreateClient(OssTokenResponse token, out string endpoint, out string bucket, out string publicBase)
        {
            endpoint = (token != null && !string.IsNullOrEmpty(token.Endpoint)) ? token.Endpoint : _oss.Endpoint;
            bucket = (token != null && !string.IsNullOrEmpty(token.Bucket)) ? token.Bucket : _oss.Bucket;
            string ak = token != null ? token.AccessKeyId : _oss.AccessKeyId;
            string sk = token != null ? token.AccessKeySecret : _oss.AccessKeySecret;
            string st = token != null ? token.SecurityToken : _oss.SecurityToken;
            publicBase = (token != null && !string.IsNullOrEmpty(token.PublicBaseUrl))
                ? token.PublicBaseUrl
                : _oss.PublicBaseUrl;

            if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(bucket))
                throw new InvalidOperationException("OSS endpoint/bucket not configured");
            if (string.IsNullOrEmpty(ak) || string.IsNullOrEmpty(sk))
                throw new InvalidOperationException("OSS credentials not configured");

            if (!string.IsNullOrEmpty(st))
                return new OssClient(endpoint, ak, sk, st);
            return new OssClient(endpoint, ak, sk);
        }

        private async Task<UploadResult> MockUploadAsync(OssTokenResponse token, string localPath, string ossKey, string sha256, CancellationToken ct)
        {
            string url = token.UploadUrl.TrimEnd('/') + "/" + Uri.EscapeDataString(sha256);
            using (var http = new HttpClient())
            using (var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            using (var content = new StreamContent(fs))
            {
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
                using (var resp = await http.PutAsync(url, content, ct).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                }
            }

            string publicUrl = string.IsNullOrEmpty(token.PublicBaseUrl)
                ? url
                : token.PublicBaseUrl.TrimEnd('/') + "/" + ossKey;
            _log.Info("Mock OSS uploaded key=" + ossKey);
            return new UploadResult { OssKey = ossKey, Url = publicUrl };
        }

        private async Task<OssTokenResponse> GetTokenAsync(CancellationToken ct)
        {
            lock (_tokenGate)
            {
                if (_cachedToken != null && _cachedToken.Expiration > DateTime.UtcNow.AddMinutes(3))
                    return _cachedToken;
            }

            var token = await _api.GetOssTokenAsync(ct).ConfigureAwait(false);
            lock (_tokenGate)
            {
                _cachedToken = token;
            }
            return token;
        }

        private string BuildAccessUrl(
            OssClient client,
            OssTokenResponse token,
            string endpoint,
            string bucket,
            string publicBase,
            string ossKey)
        {
            if (_oss.UseSignedUrl)
            {
                DateTime expiration = ResolveSignedExpiration(token);
                Uri signed = client.GeneratePresignedUri(bucket, ossKey, expiration, SignHttpMethod.Get);
                _log.Info("OSS signed URL expireUtc=" + expiration.ToUniversalTime().ToString("o") + " key=" + ossKey);
                return signed.ToString();
            }

            return BuildStaticUrl(publicBase, endpoint, bucket, ossKey);
        }

        private DateTime ResolveSignedExpiration(OssTokenResponse token)
        {
            int seconds = _oss.SignedUrlExpireSeconds > 0 ? _oss.SignedUrlExpireSeconds : 86400;
            // Aliyun OSS V1 presign practical upper bound ~7 days for some clients; allow config but clamp to 7d.
            if (seconds > 7 * 24 * 3600)
                seconds = 7 * 24 * 3600;
            if (seconds < 60)
                seconds = 60;

            DateTime expiration = DateTime.UtcNow.AddSeconds(seconds);

            // STS credentials cannot sign beyond their own lifetime.
            if (token != null && token.Expiration > DateTime.MinValue)
            {
                DateTime stsExp = token.Expiration.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(token.Expiration, DateTimeKind.Utc)
                    : token.Expiration.ToUniversalTime();
                if (expiration > stsExp)
                    expiration = stsExp;
            }

            // SDK examples commonly use local Kind; pass UTC-equivalent local for consistency.
            return expiration.Kind == DateTimeKind.Utc ? expiration.ToLocalTime() : expiration;
        }

        private static string BuildStaticUrl(string publicBase, string endpoint, string bucket, string ossKey)
        {
            if (!string.IsNullOrEmpty(publicBase))
                return publicBase.TrimEnd('/') + "/" + ossKey;

            // virtual-host style
            string host = endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? endpoint.TrimEnd('/')
                : "https://" + bucket + "." + endpoint.TrimEnd('/');
            if (!endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return "https://" + bucket + "." + endpoint.TrimEnd('/') + "/" + ossKey;
            return host + "/" + ossKey;
        }
    }
}
