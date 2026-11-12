using System;
using System.IO;
using System.Text;
using System.Net.NetworkInformation;

namespace DpiTunnel
{
    public static class HostsHelper
    {
        private const string START_MARKER = "# --- DPI Tunnel Unblock Start ---";
        private const string END_MARKER = "# --- DPI Tunnel Unblock End ---";

        /// <summary>
        /// Checks read-only whether a VPN TUN adapter (such as Happ VPN) is active.
        /// </summary>
        public static string GetConflictingVpnAdapter()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus == OperationalStatus.Up &&
                        (ni.Name.IndexOf("happ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         ni.Description.IndexOf("SocksTunnel", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        return ni.Name;
                    }
                }
            }
            catch { }
            return null;
        }

        public static void Apply()
        {
            // No longer modifies system hosts file: DNS resolution for blocked domains
            // is handled safely in SmartProxyServer via Yandex DNS (77.88.8.8:1253).
            Remove();
        }

        public static void Remove()
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
                if (!File.Exists(path)) return;

                string content = File.ReadAllText(path, Encoding.UTF8);
                int start = content.IndexOf(START_MARKER);
                int end = content.IndexOf(END_MARKER);

                if (start != -1 && end != -1 && end > start)
                {
                    string cleaned = content.Substring(0, start) + content.Substring(end + END_MARKER.Length);
                    File.WriteAllText(path, cleaned.TrimEnd() + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
