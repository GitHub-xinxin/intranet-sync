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

            OssTokenResponse token = null;
            if (_oss.UseSts)
            {
                token = await GetTokenAsync(ct).ConfigureAwait(false);
            }

            if (token != null && token.Mock && !string.IsNullOrEmpty(token.UploadUrl))
            {
                return await MockUploadAsync(token, localPath, ossKey, sha256, ct).ConfigureAwait(false);
            }

            string endpoint = (token != null && !string.IsNullOrEmpty(token.Endpoint)) ? token.Endpoint : _oss.Endpoint;
            string bucket = (token != null && !string.IsNullOrEmpty(token.Bucket)) ? token.Bucket : _oss.Bucket;
            string ak = token != null ? token.AccessKeyId : _oss.AccessKeyId;
            string sk = token != null ? token.AccessKeySecret : _oss.AccessKeySecret;
            string st = token != null ? token.SecurityToken : _oss.SecurityToken;
            string publicBase = (token != null && !string.IsNullOrEmpty(token.PublicBaseUrl))
                ? token.PublicBaseUrl
                : _oss.PublicBaseUrl;

            if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(bucket))
                throw new InvalidOperationException("OSS endpoint/bucket not configured");

            OssClient client;
            if (!string.IsNullOrEmpty(st))
                client = new OssClient(endpoint, ak, sk, st);
            else
                client = new OssClient(endpoint, ak, sk);

            using (var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            {
                var meta = new ObjectMetadata();
                meta.ContentType = "application/pdf";
                meta.ContentLength = fs.Length;
                // PutObject streams from the file handle
                client.PutObject(bucket, ossKey, fs, meta);
            }

            string url = BuildUrl(publicBase, endpoint, bucket, ossKey);
            _log.Info("OSS uploaded key=" + ossKey);
            return new UploadResult { OssKey = ossKey, Url = url };
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

        private static string BuildUrl(string publicBase, string endpoint, string bucket, string ossKey)
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
