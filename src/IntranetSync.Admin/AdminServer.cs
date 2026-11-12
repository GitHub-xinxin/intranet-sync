using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IntranetSync.Core.Config;
using IntranetSync.Core.Logging;
using IntranetSync.Core.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace IntranetSync.Admin
{
    /// <summary>
    /// Local admin HTTP server (default 127.0.0.1). Serves embedded HTML and JSON APIs.
    /// </summary>
    public sealed class AdminServer : IDisposable
    {
        private readonly ConfigStore _store;
        private readonly SyncLogger _log;
        private readonly SyncHost _host;
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private Task _acceptLoop;
        private readonly JsonSerializerSettings _jsonSettings;

        public AdminServer(ConfigStore store, SyncLogger log, SyncHost host)
        {
            _store = store;
            _log = log;
            _host = host;
            _jsonSettings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Ignore
            };
        }

        public string Prefix { get; private set; }

        public void Start()
        {
            var cfg = _store.LoadOrDefault();
            string bind = string.IsNullOrWhiteSpace(cfg.Admin.BindAddress) ? "127.0.0.1" : cfg.Admin.BindAddress;
            int port = cfg.Admin.Port > 0 ? cfg.Admin.Port : 8787;
            Prefix = string.Format("http://{0}:{1}/", bind, port);

            _listener = new HttpListener();
            _listener.Prefixes.Add(Prefix);
            _listener.Start();
            _cts = new CancellationTokenSource();
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
            _log.Info("Admin UI listening on " + Prefix);
        }

        public void Stop()
        {
            try { if (_cts != null) _cts.Cancel(); } catch { }
            try { if (_listener != null && _listener.IsListening) _listener.Stop(); } catch { }
            try { if (_acceptLoop != null) _acceptLoop.Wait(TimeSpan.FromSeconds(5)); } catch { }
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener != null && _listener.IsListening)
            {
                HttpListenerContext ctx = null;
                try
                {
                    ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.Warn("Accept error: " + ex.Message);
                    continue;
                }

                var captured = ctx;
                Task.Run(() => HandleAsync(captured));
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            try
            {
                string path = ctx.Request.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant();
                if (string.IsNullOrEmpty(path)) path = "/";

                if (!Authorize(ctx))
                {
                    await WriteJsonAsync(ctx, 401, new { error = "unauthorized" }).ConfigureAwait(false);
                    return;
                }

                if (ctx.Request.HttpMethod == "GET" && (path == "/" || path == "/index.html"))
                {
                    await WriteResourceAsync(ctx, "Www.index.html", "text/html; charset=utf-8").ConfigureAwait(false);
                    return;
                }
                if (ctx.Request.HttpMethod == "GET" && path == "/app.js")
                {
                    await WriteResourceAsync(ctx, "Www.app.js", "application/javascript; charset=utf-8").ConfigureAwait(false);
                    return;
                }
                if (ctx.Request.HttpMethod == "GET" && path == "/style.css")
                {
                    await WriteResourceAsync(ctx, "Www.style.css", "text/css; charset=utf-8").ConfigureAwait(false);
                    return;
                }

                if (path == "/api/config" && ctx.Request.HttpMethod == "GET")
                {
                    var dto = SyncConfigDto.FromConfig(_store.LoadOrDefault());
                    await WriteJsonAsync(ctx, 200, dto).ConfigureAwait(false);
                    return;
                }

                if (path == "/api/config" && ctx.Request.HttpMethod == "POST")
                {
                    string body = await ReadBodyAsync(ctx.Request).ConfigureAwait(false);
                    var existing = _store.LoadOrDefault();
                    var jo = JObject.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
                    // Normalize camelCase → SyncConfig via serializer
                    var incoming = JsonConvert.DeserializeObject<SyncConfig>(body, _jsonSettings) ?? new SyncConfig();
                    MergeSecrets(existing, incoming);
                    _store.Save(incoming);
                    _log.Info("Config saved (secrets encrypted at rest)");
                    await WriteJsonAsync(ctx, 200, SyncConfigDto.FromConfig(incoming)).ConfigureAwait(false);
                    return;
                }

                if (path == "/api/sql/test" && ctx.Request.HttpMethod == "POST")
                {
                    string msg = await _host.Engine.TestSqlAsync(CancellationToken.None).ConfigureAwait(false);
                    await WriteJsonAsync(ctx, 200, new { ok = true, message = msg }).ConfigureAwait(false);
                    return;
                }

                if (path == "/api/sync/start" && ctx.Request.HttpMethod == "POST")
                {
                    var cfg = _store.LoadOrDefault();
                    cfg.Sync.Enabled = true;
                    _store.Save(cfg);
                    _host.Start();
                    await WriteJsonAsync(ctx, 200, new { ok = true, running = _host.IsRunning }).ConfigureAwait(false);
                    return;
                }

                if (path == "/api/sync/stop" && ctx.Request.HttpMethod == "POST")
                {
                    var cfg = _store.LoadOrDefault();
                    cfg.Sync.Enabled = false;
                    _store.Save(cfg);
                    // keep host loop but disabled; or stop entirely:
                    _host.Stop();
                    await WriteJsonAsync(ctx, 200, new { ok = true, running = _host.IsRunning }).ConfigureAwait(false);
                    return;
                }

                if (path == "/api/sync/run" && ctx.Request.HttpMethod == "POST")
                {
                    var result = await _host.RunOnceAsync().ConfigureAwait(false);
                    await WriteJsonAsync(ctx, 200, result).ConfigureAwait(false);
                    return;
                }

                if (path == "/api/status" && ctx.Request.HttpMethod == "GET")
                {
                    var cfg = _store.LoadOrDefault();
                    var wm = _host.Engine.Watermarks.Get(cfg.View.ViewName ?? "");
                    await WriteJsonAsync(ctx, 200, new
                    {
                        running = _host.IsRunning,
                        enabled = cfg.Sync.Enabled,
                        lastRunUtc = _host.LastRunUtc,
                        lastResult = _host.LastResult,
                        watermark = wm,
                        adminUrl = Prefix
                    }).ConfigureAwait(false);
                    return;
                }

                if (path == "/api/logs" && ctx.Request.HttpMethod == "GET")
                {
                    await WriteJsonAsync(ctx, 200, _log.GetRecent(200)).ConfigureAwait(false);
                    return;
                }

                if (path == "/api/failures" && ctx.Request.HttpMethod == "GET")
                {
                    await WriteJsonAsync(ctx, 200, _host.Engine.Failures.ListRecent(200)).ConfigureAwait(false);
                    return;
                }

                await WriteJsonAsync(ctx, 404, new { error = "not found", path = path }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error("Admin request failed", ex);
                try
                {
                    await WriteJsonAsync(ctx, 500, new { error = SyncLogger.Mask(ex.Message) }).ConfigureAwait(false);
                }
                catch { }
            }
        }

        private bool Authorize(HttpListenerContext ctx)
        {
            var cfg = _store.LoadOrDefault();
            if (string.IsNullOrEmpty(cfg.Admin.AdminPassword))
                return true;

            string header = ctx.Request.Headers["Authorization"];
            if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return false;
            string token = header.Substring("Bearer ".Length).Trim();
            return string.Equals(token, cfg.Admin.AdminPassword, StringComparison.Ordinal);
        }

        private static void MergeSecrets(SyncConfig existing, SyncConfig incoming)
        {
            if (IsPlaceholder(incoming.Sql.Password)) incoming.Sql.Password = existing.Sql.Password;
            if (IsPlaceholder(incoming.Cloud.ServiceToken)) incoming.Cloud.ServiceToken = existing.Cloud.ServiceToken;
            if (IsPlaceholder(incoming.Oss.AccessKeySecret)) incoming.Oss.AccessKeySecret = existing.Oss.AccessKeySecret;
            if (IsPlaceholder(incoming.Oss.SecurityToken)) incoming.Oss.SecurityToken = existing.Oss.SecurityToken;
            if (IsPlaceholder(incoming.Pdf.BasicPassword)) incoming.Pdf.BasicPassword = existing.Pdf.BasicPassword;
            if (IsPlaceholder(incoming.Admin.AdminPassword)) incoming.Admin.AdminPassword = existing.Admin.AdminPassword;
        }

        private static bool IsPlaceholder(string v)
        {
            return v == "********" || v == "****" || v == null;
        }

        private async Task WriteResourceAsync(HttpListenerContext ctx, string suffix, string contentType)
        {
            string name = "IntranetSync.Admin." + suffix;
            var asm = Assembly.GetExecutingAssembly();
            using (var stream = asm.GetManifestResourceStream(name))
            {
                if (stream == null)
                {
                    // list for diagnostics
                    var names = asm.GetManifestResourceNames();
                    await WriteTextAsync(ctx, 500, "text/plain",
                        "Missing embedded resource: " + name + " available=" + string.Join(",", names)).ConfigureAwait(false);
                    return;
                }
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = contentType;
                await stream.CopyToAsync(ctx.Response.OutputStream).ConfigureAwait(false);
                ctx.Response.OutputStream.Close();
            }
        }

        private async Task WriteJsonAsync(HttpListenerContext ctx, int status, object obj)
        {
            string json = JsonConvert.SerializeObject(obj, _jsonSettings);
            await WriteTextAsync(ctx, status, "application/json; charset=utf-8", json).ConfigureAwait(false);
        }

        private static async Task WriteTextAsync(HttpListenerContext ctx, int status, string contentType, string text)
        {
            byte[] buf = Encoding.UTF8.GetBytes(text ?? "");
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = buf.Length;
            await ctx.Response.OutputStream.WriteAsync(buf, 0, buf.Length).ConfigureAwait(false);
            ctx.Response.OutputStream.Close();
        }

        private static async Task<string> ReadBodyAsync(HttpListenerRequest req)
        {
            using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
            {
                return await reader.ReadToEndAsync().ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            Stop();
            if (_listener != null)
            {
                try { _listener.Close(); } catch { }
            }
        }
    }
}
