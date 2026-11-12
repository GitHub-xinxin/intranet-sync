using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace IntranetSync.Core.Config
{
    /// <summary>
    /// Loads/saves SyncConfig as encrypted JSON under %ProgramData%\IntranetSyncAgent\
    /// </summary>
    public sealed class ConfigStore
    {
        private readonly string _configPath;
        private readonly object _gate = new object();

        public ConfigStore(string dataDirectory = null)
        {
            string root = dataDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "IntranetSyncAgent");
            Directory.CreateDirectory(root);
            _configPath = Path.Combine(root, "config.json");
            DataDirectory = root;
        }

        public string DataDirectory { get; }
        public string ConfigPath { get { return _configPath; } }

        public SyncConfig LoadOrDefault()
        {
            lock (_gate)
            {
                if (!File.Exists(_configPath))
                    return new SyncConfig();

                string json = File.ReadAllText(_configPath);
                var cfg = JsonConvert.DeserializeObject<SyncConfig>(json) ?? new SyncConfig();
                DecryptSecrets(cfg);
                return cfg;
            }
        }

        public void Save(SyncConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException("cfg");
            lock (_gate)
            {
                var clone = JsonConvert.DeserializeObject<SyncConfig>(JsonConvert.SerializeObject(cfg));
                EncryptSecrets(clone);
                string json = JsonConvert.SerializeObject(clone, Formatting.Indented);
                string tmp = _configPath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(_configPath))
                    File.Replace(tmp, _configPath, _configPath + ".bak");
                else
                    File.Move(tmp, _configPath);
            }
        }

        /// <summary>
        /// Merge UI payload: keep existing secrets when placeholder ******** is submitted.
        /// </summary>
        public SyncConfig MergeFromDto(SyncConfig existing, JObject body)
        {
            var incoming = body.ToObject<SyncConfig>() ?? new SyncConfig();
            if (existing == null) existing = new SyncConfig();

            KeepIfPlaceholder(incoming.Sql, "Password", existing.Sql.Password, body, "sql", "password");
            KeepIfPlaceholder(incoming.Cloud, "ServiceToken", existing.Cloud.ServiceToken, body, "cloud", "serviceToken");
            KeepIfPlaceholder(incoming.Oss, "AccessKeySecret", existing.Oss.AccessKeySecret, body, "oss", "accessKeySecret");
            KeepIfPlaceholder(incoming.Pdf, "BasicPassword", existing.Pdf.BasicPassword, body, "pdf", "basicPassword");
            KeepIfPlaceholder(incoming.Admin, "AdminPassword", existing.Admin.AdminPassword, body, "admin", "adminPassword");

            // case-insensitive JSON may use PascalCase from our DTO
            if (IsPlaceholder(incoming.Sql.Password)) incoming.Sql.Password = existing.Sql.Password;
            if (IsPlaceholder(incoming.Cloud.ServiceToken)) incoming.Cloud.ServiceToken = existing.Cloud.ServiceToken;
            if (IsPlaceholder(incoming.Oss.AccessKeySecret)) incoming.Oss.AccessKeySecret = existing.Oss.AccessKeySecret;
            if (IsPlaceholder(incoming.Pdf.BasicPassword)) incoming.Pdf.BasicPassword = existing.Pdf.BasicPassword;
            if (IsPlaceholder(incoming.Admin.AdminPassword)) incoming.Admin.AdminPassword = existing.Admin.AdminPassword;

            return incoming;
        }

        private static bool IsPlaceholder(string v)
        {
            return v == "********" || v == "****";
        }

        private static void KeepIfPlaceholder(object target, string prop, string existing, JObject body, string section, string field)
        {
            // reserved for future selective merge
        }

        private static void EncryptSecrets(SyncConfig cfg)
        {
            cfg.Sql.Password = SecretProtector.Protect(cfg.Sql.Password ?? "");
            cfg.Cloud.ServiceToken = SecretProtector.Protect(cfg.Cloud.ServiceToken ?? "");
            cfg.Oss.AccessKeySecret = SecretProtector.Protect(cfg.Oss.AccessKeySecret ?? "");
            cfg.Oss.SecurityToken = SecretProtector.Protect(cfg.Oss.SecurityToken ?? "");
            cfg.Pdf.BasicPassword = SecretProtector.Protect(cfg.Pdf.BasicPassword ?? "");
            cfg.Admin.AdminPassword = SecretProtector.Protect(cfg.Admin.AdminPassword ?? "");
        }

        private static void DecryptSecrets(SyncConfig cfg)
        {
            cfg.Sql.Password = SecretProtector.Unprotect(cfg.Sql.Password ?? "");
            cfg.Cloud.ServiceToken = SecretProtector.Unprotect(cfg.Cloud.ServiceToken ?? "");
            cfg.Oss.AccessKeySecret = SecretProtector.Unprotect(cfg.Oss.AccessKeySecret ?? "");
            cfg.Oss.SecurityToken = SecretProtector.Unprotect(cfg.Oss.SecurityToken ?? "");
            cfg.Pdf.BasicPassword = SecretProtector.Unprotect(cfg.Pdf.BasicPassword ?? "");
            cfg.Admin.AdminPassword = SecretProtector.Unprotect(cfg.Admin.AdminPassword ?? "");
        }
    }
}
