using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;

namespace DpiTunnel
{
    public class ActivityMonitor
    {
        private Thread _worker;
        private bool _isRunning = false;
        private readonly Dictionary<string, DateTime> _lastLogged = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public event Action<string, string> OnSiteActivity;

        // Known IPs or subnets for instant matching
        private static readonly Dictionary<string, string> KnownSubnets = new Dictionary<string, string>()
        {
            { "193.46.255", "Rutor.info" },
            { "195.82.146", "RuTracker.org" },
            { "162.159.13", "Discord" },
            { "162.159.12", "Discord" },
            { "140.82.11",  "GitHub" },
            { "140.82.12",  "GitHub" },
            { "142.250.",   "YouTube / Google Video" },
            { "142.251.",   "YouTube / Google Video" },
            { "172.217.",   "YouTube / Google Media" },
            { "89.108.71",  "Telegram (FakeTLS Mail.ru)" },
            { "156.239.251", "Telegram (FakeTLS Microsoft)" },
            { "77.mishutkin", "Telegram (FakeTLS)" }
        };

        public void Start()
        {
            Stop();
            _isRunning = true;
            _worker = new Thread(MonitorLoop);
            _worker.IsBackground = true;
            _worker.Start();
        }

        public void Stop()
        {
            _isRunning = false;
            if (_worker != null)
            {
                try { _worker.Abort(); } catch { }
                _worker = null;
            }
        }

        public void ReportSite(string siteName, string details)
        {
            DateTime now = DateTime.Now;
            if (_lastLogged.ContainsKey(siteName) && (now - _lastLogged[siteName]).TotalSeconds < 15)
            {
                return; // suppress duplicate logs within 15 seconds
            }

            _lastLogged[siteName] = now;
            if (OnSiteActivity != null)
            {
                OnSiteActivity(siteName, details);
            }
        }

        private void MonitorLoop()
        {
            while (_isRunning)
            {
                try
                {
                    IPGlobalProperties props = IPGlobalProperties.GetIPGlobalProperties();
                    TcpConnectionInformation[] connections = props.GetActiveTcpConnections();

                    foreach (TcpConnectionInformation conn in connections)
                    {
                        if (conn.State == TcpState.Established && (conn.RemoteEndPoint.Port == 443 || conn.RemoteEndPoint.Port == 80))
                        {
                            string ipStr = conn.RemoteEndPoint.Address.ToString();

                            foreach (KeyValuePair<string, string> pair in KnownSubnets)
                            {
                                if (ipStr.StartsWith(pair.Key))
                                {
                                    ReportSite(pair.Value, "HTTPS/443 трафик успешно разблокирован");
                                    break;
                                }
                            }
                        }
                    }
                }
                catch
                {
                }

                Thread.Sleep(2000);
            }
        }
    }
}
