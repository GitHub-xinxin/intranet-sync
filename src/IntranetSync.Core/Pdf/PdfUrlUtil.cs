using System;
using System.Text.RegularExpressions;
using IntranetSync.Core.Config;

namespace IntranetSync.Core.Pdf
{
    public sealed class OssUrlParts
    {
        public string Bucket { get; set; }
        public string Key { get; set; }
        public string EndpointHost { get; set; }
        public string OriginalUrl { get; set; }
    }

    /// <summary>
    /// Normalize PDF URLs and detect Aliyun OSS object URLs (virtual-host / path-style / PublicBaseUrl).
    /// </summary>
    public static class PdfUrlUtil
    {
        // bucket.oss-cn-hangzhou.aliyuncs.com or bucket.oss-cn-hangzhou-internal.aliyuncs.com
        private static readonly Regex VirtualHostOss = new Regex(
            @"^(?<bucket>[^.]+)\.(?<endpoint>oss-[a-z0-9-]+\.aliyuncs\.com)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string Normalize(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return url;

            string trimmed = url.Trim();
            // SQL / Windows paths often leak backslashes into URL paths.
            if (trimmed.IndexOf('\\') >= 0)
                trimmed = trimmed.Replace('\\', '/');
            return trimmed;
        }

        public static bool TryParseOssUrl(string url, out OssUrlParts parts)
        {
            parts = null;
            if (string.IsNullOrWhiteSpace(url))
                return false;

            Uri uri;
            if (!Uri.TryCreate(Normalize(url), UriKind.Absolute, out uri))
                return false;
            if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                return false;

            string host = uri.Host ?? "";
            string path = (uri.AbsolutePath ?? "").TrimStart('/');
            // Strip query (signature params) from key
            if (string.IsNullOrEmpty(path))
                return false;

            var m = VirtualHostOss.Match(host);
            if (m.Success)
            {
                parts = new OssUrlParts
                {
                    Bucket = m.Groups["bucket"].Value,
                    EndpointHost = m.Groups["endpoint"].Value,
                    Key = Uri.UnescapeDataString(path),
                    OriginalUrl = url
                };
                return true;
            }

            // path-style: oss-cn-hangzhou.aliyuncs.com/bucket/key
            if (host.StartsWith("oss-", StringComparison.OrdinalIgnoreCase) &&
                host.EndsWith(".aliyuncs.com", StringComparison.OrdinalIgnoreCase))
            {
                int slash = path.IndexOf('/');
                if (slash <= 0 || slash >= path.Length - 1)
                    return false;
                parts = new OssUrlParts
                {
                    Bucket = path.Substring(0, slash),
                    EndpointHost = host,
                    Key = Uri.UnescapeDataString(path.Substring(slash + 1)),
                    OriginalUrl = url
                };
                return true;
            }

            return false;
        }

        /// <summary>
        /// True when URL already points at the configured destination OSS (bucket / public base).
        /// </summary>
        public static bool IsConfiguredOssUrl(string url, OssSettings oss)
        {
            OssUrlParts parts;
            if (TryParseOssUrl(url, out parts))
            {
                if (oss != null && !string.IsNullOrWhiteSpace(oss.Bucket) &&
                    string.Equals(parts.Bucket, oss.Bucket.Trim(), StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (oss == null || string.IsNullOrWhiteSpace(oss.PublicBaseUrl))
                return false;

            Uri baseUri;
            Uri fileUri;
            if (!Uri.TryCreate(oss.PublicBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out baseUri))
                return false;
            if (!Uri.TryCreate(Normalize(url), UriKind.Absolute, out fileUri))
                return false;

            return string.Equals(baseUri.Scheme, fileUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(baseUri.Host, fileUri.Host, StringComparison.OrdinalIgnoreCase) &&
                   fileUri.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        }

        public static string ExtractOssKeyFromConfiguredUrl(string url, OssSettings oss)
        {
            OssUrlParts parts;
            if (TryParseOssUrl(url, out parts))
                return parts.Key;

            if (oss != null && !string.IsNullOrWhiteSpace(oss.PublicBaseUrl))
            {
                string prefix = oss.PublicBaseUrl.TrimEnd('/') + "/";
                string n = Normalize(url);
                if (n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(n.Substring(prefix.Length));
            }

            return null;
        }
    }
}
