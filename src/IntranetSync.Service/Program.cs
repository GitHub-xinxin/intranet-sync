using System;
using System.ServiceProcess;
using IntranetSync.Admin;
using IntranetSync.Core;
using IntranetSync.Core.Config;
using IntranetSync.Core.Logging;
using IntranetSync.Core.Sync;

namespace IntranetSync.Service
{
    /// <summary>
    /// Entry: Windows Service by default; console mode with --console for debugging.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            RuntimeCompat.EnsureModernTls();

            bool console = false;
            foreach (var a in args)
            {
                if (string.Equals(a, "--console", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "-c", StringComparison.OrdinalIgnoreCase))
                {
                    console = true;
                }
            }

            // Interactive session or explicit flag → console host
            if (console || Environment.UserInteractive)
            {
                return RunConsole();
            }

            ServiceBase.Run(new ServiceBase[] { new SyncWindowsService() });
            return 0;
        }

        private static int RunConsole()
        {
            Console.WriteLine("IntranetSyncAgent console mode. Admin UI on configured loopback port.");
            Console.WriteLine("Press Ctrl+C to stop.");

            var store = new ConfigStore();
            var log = new SyncLogger(store.DataDirectory);
            var host = new SyncHost(store, log);
            var admin = new AdminServer(store, log, host);

            var cfg = store.LoadOrDefault();
            admin.Start();
            if (cfg.Sync.Enabled)
                host.Start();

            Console.WriteLine("Data dir: " + store.DataDirectory);
            Console.WriteLine("Open: " + admin.Prefix);

            var exit = new System.Threading.ManualResetEvent(false);
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                exit.Set();
            };
            exit.WaitOne();

            host.Stop();
            admin.Stop();
            return 0;
        }
    }
}
