using System;
using System.ServiceProcess;
using IntranetSync.Admin;
using IntranetSync.Core;
using IntranetSync.Core.Config;
using IntranetSync.Core.Logging;
using IntranetSync.Core.Sync;

namespace IntranetSync.Service
{
    public partial class SyncWindowsService : ServiceBase
    {
        private ConfigStore _store;
        private SyncLogger _log;
        private SyncHost _host;
        private AdminServer _admin;

        public SyncWindowsService()
        {
            ServiceName = "IntranetSyncAgent";
            CanStop = true;
            CanShutdown = true;
            AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            RuntimeCompat.EnsureModernTls();
            _store = new ConfigStore();
            _log = new SyncLogger(_store.DataDirectory);
            _host = new SyncHost(_store, _log);
            _admin = new AdminServer(_store, _log, _host);

            try
            {
                _admin.Start();
                var cfg = _store.LoadOrDefault();
                if (cfg.Sync.Enabled)
                    _host.Start();
                _log.Info("Windows Service started");
            }
            catch (Exception ex)
            {
                if (_log != null) _log.Error("Service OnStart failed", ex);
                throw;
            }
        }

        protected override void OnStop()
        {
            try
            {
                if (_host != null) _host.Stop();
                if (_admin != null) _admin.Stop();
                if (_log != null) _log.Info("Windows Service stopped");
            }
            catch (Exception ex)
            {
                if (_log != null) _log.Error("Service OnStop error", ex);
            }
        }

        protected override void OnShutdown()
        {
            OnStop();
            base.OnShutdown();
        }
    }
}
