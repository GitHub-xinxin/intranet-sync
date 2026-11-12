using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace IntranetSync.Core.Cloud
{
    public sealed class SyncBatchRequest
    {
        [JsonProperty("tenantId")]
        public string TenantId { get; set; }

        [JsonProperty("viewName")]
        public string ViewName { get; set; }

        [JsonProperty("rows")]
        public List<SyncRow> Rows { get; set; }
    }

    public sealed class SyncRow
    {
        [JsonProperty("pk")]
        public string PrimaryKey { get; set; }

        [JsonProperty("watermark")]
        public string Watermark { get; set; }

        [JsonProperty("rowHash")]
        public string RowHash { get; set; }

        /// <summary>Business fields; PDF columns already replaced with cloud HTTPS URLs. Never contains PDF bytes.</summary>
        [JsonProperty("fields")]
        public JObject Fields { get; set; }

        [JsonProperty("pdf")]
        public Dictionary<string, PdfMeta> Pdf { get; set; }
    }

    public sealed class PdfMeta
    {
        [JsonProperty("sha256")]
        public string Sha256 { get; set; }

        [JsonProperty("ossKey")]
        public string OssKey { get; set; }

        [JsonProperty("url")]
        public string Url { get; set; }
    }

    public sealed class SyncBatchResponse
    {
        [JsonProperty("accepted")]
        public int Accepted { get; set; }

        [JsonProperty("rejected")]
        public List<string> Rejected { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }
    }

    public sealed class OssTokenResponse
    {
        [JsonProperty("accessKeyId")]
        public string AccessKeyId { get; set; }

        [JsonProperty("accessKeySecret")]
        public string AccessKeySecret { get; set; }

        [JsonProperty("securityToken")]
        public string SecurityToken { get; set; }

        [JsonProperty("expiration")]
        public DateTime Expiration { get; set; }

        [JsonProperty("endpoint")]
        public string Endpoint { get; set; }

        [JsonProperty("bucket")]
        public string Bucket { get; set; }

        [JsonProperty("prefix")]
        public string Prefix { get; set; }

        [JsonProperty("publicBaseUrl")]
        public string PublicBaseUrl { get; set; }

        /// <summary>When mock=true, agent may POST file to uploadUrl instead of real OSS.</summary>
        [JsonProperty("mock")]
        public bool Mock { get; set; }

        [JsonProperty("uploadUrl")]
        public string UploadUrl { get; set; }
    }

    public sealed class PdfLookupResponse
    {
        [JsonProperty("found")]
        public bool Found { get; set; }

        [JsonProperty("sha256")]
        public string Sha256 { get; set; }

        [JsonProperty("ossKey")]
        public string OssKey { get; set; }

        [JsonProperty("url")]
        public string Url { get; set; }
    }
}
