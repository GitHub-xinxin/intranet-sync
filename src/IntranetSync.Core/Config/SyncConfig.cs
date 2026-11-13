using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace IntranetSync.Core.Config
{
    /// <summary>
    /// 同步服务完整配置 / Full agent configuration (secrets stored encrypted on disk).
    /// </summary>
    public sealed class SyncConfig
    {
        public SqlSettings Sql { get; set; } = new SqlSettings();
        public ViewSettings View { get; set; } = new ViewSettings();
        public CloudSettings Cloud { get; set; } = new CloudSettings();
        public OssSettings Oss { get; set; } = new OssSettings();
        public SyncRuntimeSettings Sync { get; set; } = new SyncRuntimeSettings();
        public AdminSettings Admin { get; set; } = new AdminSettings();
        public PdfSettings Pdf { get; set; } = new PdfSettings();
    }

    public sealed class SqlSettings
    {
        public string Server { get; set; } = "localhost";
        public string Database { get; set; } = "";
        public string UserId { get; set; } = "";
        /// <summary>明文仅在内存；落盘时由 ConfigStore 加密。</summary>
        public string Password { get; set; } = "";
        public bool IntegratedSecurity { get; set; }
        public int ConnectTimeoutSeconds { get; set; } = 15;

        public string BuildConnectionString()
        {
            if (IntegratedSecurity)
            {
                return string.Format(
                    "Server={0};Database={1};Integrated Security=True;Connect Timeout={2};Application Name=IntranetSyncAgent;",
                    Server, Database, ConnectTimeoutSeconds);
            }

            return string.Format(
                "Server={0};Database={1};User Id={2};Password={3};Connect Timeout={4};Application Name=IntranetSyncAgent;",
                Server, Database, UserId, Password, ConnectTimeoutSeconds);
        }

        /// <summary>日志用：密码打码。</summary>
        public string BuildMaskedConnectionString()
        {
            if (IntegratedSecurity)
            {
                return string.Format(
                    "Server={0};Database={1};Integrated Security=True;",
                    Server, Database);
            }

            return string.Format(
                "Server={0};Database={1};User Id={2};Password=****;",
                Server, Database, UserId);
        }
    }

    public sealed class ViewSettings
    {
        public string ViewName { get; set; } = "";
        /// <summary>业务唯一列（不必是 SQL 主键约束），云端 upsert 用。</summary>
        public string PrimaryKeyColumn { get; set; } = "person_id";
        /// <summary>增量列；为空则使用行哈希回退。</summary>
        public string IncrementalColumn { get; set; } = "updated_at";
        /// <summary>PDF URL 列名列表（可多列）。</summary>
        public List<string> PdfUrlColumns { get; set; } = new List<string> { "PDF" };
        public int PageSize { get; set; } = 300;
    }

    public sealed class CloudSettings
    {
        /// <summary>
        /// 数据同步完整 POST 地址（不再自动拼接 /sync/batch）。
        /// 例：https://api.example.com/sync/batch
        /// </summary>
        public string ApiBaseUrl { get; set; } = "https://api.example.com/sync/batch";
        /// <summary>
        /// STS 临时凭证完整 GET 地址（可选）。留空则尝试从 ApiBaseUrl 推导 /sync/oss-token。
        /// </summary>
        public string OssTokenUrl { get; set; } = "";
        /// <summary>服务 Token；落盘加密，日志打码。</summary>
        public string ServiceToken { get; set; } = "";
        public string TenantId { get; set; } = "default";
        public int TimeoutSeconds { get; set; } = 60;
    }

    public sealed class OssSettings
    {
        /// <summary>endpoint / region，如 oss-cn-hangzhou.aliyuncs.com</summary>
        public string Endpoint { get; set; } = "";
        public string Bucket { get; set; } = "";
        public string Prefix { get; set; } = "sync";
        /// <summary>true = GET /sync/oss-token 取 STS；false = 使用本地 AccessKey（仅内网主机）。</summary>
        public bool UseSts { get; set; } = true;
        public string AccessKeyId { get; set; } = "";
        public string AccessKeySecret { get; set; } = "";
        public string SecurityToken { get; set; } = "";
        /// <summary>公网访问域名（可选），用于拼 cloud URL。</summary>
        public string PublicBaseUrl { get; set; } = "";
    }

    public sealed class SyncRuntimeSettings
    {
        public int IntervalSeconds { get; set; } = 180;
        public bool Enabled { get; set; } = false;
        /// <summary>单行 PDF 失败是否阻断整批（默认否）。</summary>
        public bool FailBatchOnPdfError { get; set; } = false;
    }

    public sealed class AdminSettings
    {
        public string BindAddress { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 8787;
        /// <summary>可选简单口令；空则仅本机访问时无口令。</summary>
        public string AdminPassword { get; set; } = "";
    }

    public sealed class PdfSettings
    {
        public long MaxBytes { get; set; } = 50L * 1024 * 1024;
        public int MaxConcurrency { get; set; } = 3;
        /// <summary>anonymous | basic | ntlm</summary>
        public string AuthMode { get; set; } = "anonymous";
        public string BasicUser { get; set; } = "";
        public string BasicPassword { get; set; } = "";
        public int DownloadTimeoutSeconds { get; set; } = 120;
    }

    /// <summary>API 往返用的脱敏视图（密码/Token 不回传明文时可带占位）。</summary>
    public sealed class SyncConfigDto
    {
        public SqlSettings Sql { get; set; }
        public ViewSettings View { get; set; }
        public CloudSettings Cloud { get; set; }
        public OssSettings Oss { get; set; }
        public SyncRuntimeSettings Sync { get; set; }
        public AdminSettings Admin { get; set; }
        public PdfSettings Pdf { get; set; }
        public bool PasswordSet { get; set; }
        public bool ServiceTokenSet { get; set; }
        public bool OssSecretSet { get; set; }

        public static SyncConfigDto FromConfig(SyncConfig cfg)
        {
            var clone = JsonConvert.DeserializeObject<SyncConfig>(JsonConvert.SerializeObject(cfg));
            bool pwd = !string.IsNullOrEmpty(clone.Sql.Password);
            bool tok = !string.IsNullOrEmpty(clone.Cloud.ServiceToken);
            bool oss = !string.IsNullOrEmpty(clone.Oss.AccessKeySecret);
            clone.Sql.Password = pwd ? "********" : "";
            clone.Cloud.ServiceToken = tok ? "********" : "";
            clone.Oss.AccessKeySecret = oss ? "********" : "";
            clone.Pdf.BasicPassword = string.IsNullOrEmpty(clone.Pdf.BasicPassword) ? "" : "********";
            clone.Admin.AdminPassword = string.IsNullOrEmpty(clone.Admin.AdminPassword) ? "" : "********";

            return new SyncConfigDto
            {
                Sql = clone.Sql,
                View = clone.View,
                Cloud = clone.Cloud,
                Oss = clone.Oss,
                Sync = clone.Sync,
                Admin = clone.Admin,
                Pdf = clone.Pdf,
                PasswordSet = pwd,
                ServiceTokenSet = tok,
                OssSecretSet = oss
            };
        }
    }
}
