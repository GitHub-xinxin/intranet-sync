using System.ComponentModel;
using System.Configuration.Install;
using System.ServiceProcess;

namespace IntranetSync.Service
{
    /// <summary>
    /// InstallUtil.exe installer for Windows Service registration.
    /// </summary>
    [RunInstaller(true)]
    public class ProjectInstaller : Installer
    {
        private readonly ServiceProcessInstaller _processInstaller;
        private readonly ServiceInstaller _serviceInstaller;

        public ProjectInstaller()
        {
            _processInstaller = new ServiceProcessInstaller
            {
                Account = ServiceAccount.LocalSystem
            };

            _serviceInstaller = new ServiceInstaller
            {
                ServiceName = "IntranetSyncAgent",
                DisplayName = "Intranet SQL Cloud Sync Agent",
                Description = "Incrementally syncs SQL Server views to cloud API and uploads PDFs to OSS.",
                StartType = ServiceStartMode.Automatic
            };

            Installers.Add(_processInstaller);
            Installers.Add(_serviceInstaller);
        }
    }
}
