using System;
using System.Net;

namespace IntranetSync.Core
{
    /// <summary>
    /// .NET 4.5.1 on Server 2012 R2 defaults may omit TLS 1.2; cloud API / OSS need it.
    /// </summary>
    public static class RuntimeCompat
    {
        public static void EnsureModernTls()
        {
            try
            {
                ServicePointManager.SecurityProtocol |=
                    SecurityProtocolType.Tls
                    | SecurityProtocolType.Tls11
                    | SecurityProtocolType.Tls12;
            }
            catch
            {
                // Ignore if a protocol flag is unavailable on an older host patch level.
            }
        }
    }
}
