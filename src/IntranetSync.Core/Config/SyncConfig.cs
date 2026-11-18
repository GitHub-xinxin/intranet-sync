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
        /// <summary>
        /// 检测记录唯一列（默认 person_id）：云端 upsert 键、水位并列游标、PDF 失败重试回查键。
        /// </summary>
        public string PrimaryKeyColumn { get; set; } = "person_id";
        /// <summary>
        /// 报告生成/修改时间水位列。只顺序往后增加；报告修改时会变更，从而再次进入增量窗口。
        /// </summary>
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
        /// <summary>单次云端 HTTP 请求超时（秒）。超时后取消请求，避免整轮同步永久挂起。</summary>
        public int TimeoutSeconds { get; set; } = 60;
        /// <summary>batch 推送遇超时/网络/5xx 时的重试次数（含首次）。默认 3。</summary>
        public int BatchRetryCount { get; set; } = 3;
        /// <summary>batch 重试间隔基数（秒）；第 n 次重试前等待 n * 该值。</summary>
        public int BatchRetryDelaySeconds { get; set; } = 5;
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
        /// <summary>公网访问域名（可选，已不再用于拼 PDF 访问链接；保留兼容旧配置）。</summary>
        public string PublicBaseUrl { get; set; } = "";
        /// <summary>已废弃：代理不再签发 PDF 访问链接（云端不使用）。保留字段仅兼容旧配置反序列化。</summary>
        public bool UseSignedUrl { get; set; } = false;
        /// <summary>已废弃：见 UseSignedUrl。</summary>
        public int SignedUrlExpireSeconds { get; set; } = 86400;
    }

    public sealed class SyncRuntimeSettings
    {
        public int IntervalSeconds { get; set; } = 180;
        /// <summary>是否按 IntervalSeconds 自动跑同步。默认 true；管理台「启动同步 / 停止同步」会改写此值。</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>
        /// 已废弃：引擎固定为「仅跳过失败行」。保留字段兼容旧配置反序列化。
        /// </summary>
        public bool FailBatchOnPdfError { get; set; } = false;
        /// <summary>
        /// 已废弃：不再做周期性全表扫描。保留字段仅兼容旧配置反序列化；引擎忽略此开关。
        /// </summary>
        public bool ReconcileEnabled { get; set; } = false;
        /// <summary>已废弃：见 ReconcileEnabled。保留兼容旧配置。</summary>
        public int ReconcileIntervalMinutes { get; set; } = 15;
        /// <summary>
        /// 每轮增量同步结束后，是否重试 pdf-failures 队列中的 person_id（回查视图 → PDF → upsert）。
        /// </summary>
        public bool PdfRetryEnabled { get; set; } = true;
        /// <summary>每轮最多处理的 PDF 失败条数（按失败项计，会去重 person_id）。默认 100。</summary>
        public int PdfRetryBatchSize { get; set; } = 100;
        /// <summary>
        /// 单轮同步最长秒数。超时则取消本轮并在下一间隔重试（水位未推进的数据会再推）。
        /// 0 = 不额外限制整轮（仍受每次 HTTP TimeoutSeconds 约束）。默认 1800（30 分钟）。
        /// </summary>
        public int MaxRoundSeconds { get; set; } = 1800;
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
