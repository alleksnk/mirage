using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace DpiTunnel
{
    public class SmartProxyServer
    {
        private TcpListener _listener;
        private volatile bool _running = false;
        private Thread _acceptThread;
        private readonly int _listenPort;
        private readonly int _socksPort;

        private class DnsCacheEntry
        {
            public IPAddress[] Ips;
            public DateTime ExpiryUtc;
        }

        private readonly Dictionary<string, DnsCacheEntry> _dnsCache =
            new Dictionary<string, DnsCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly object _dnsLock = new object();
        private readonly Random _rand = new Random();
        private volatile bool _directUpstream = false;

        public bool DirectUpstream
        {
            get { return _directUpstream; }
            set { _directUpstream = value; }
        }

        public void ClearDnsCache()
        {
            lock (_dnsLock)
            {
                _dnsCache.Clear();
            }
        }

        public event Action<string> OnSiteProxied;
        public event Action<string> OnLog;

        // Core DPI-blocked domains (YouTube, torrents, Discord, social media).
        // Deliberately excludes root "googleapis.com" so Antigravity IDE AI endpoints remain DIRECT (via VPN).
        private static readonly string[] CoreBlockedDomains = new string[]
        {
            "youtube.com",
            "youtu.be",
            "yt.be",
            "googlevideo.com",
            "ytimg.com",
            "ggpht.com",
            "googleusercontent.com",
            "gstatic.com",
            "accounts.google.com",
            "play.google.com",
            "suggestqueries.google.com",
            "gvt1.com",
            "youtube-nocookie.com",
            "youtubei.googleapis.com",
            "youtube.googleapis.com",
            "youtubeembeddedplayer.googleapis.com",
            "jnn-pa.googleapis.com",
            "wide-youtube.l.google.com",
            "yt-video-upload.l.google.com",
            "youtube-ui.l.google.com",
            "rutor.info",
            "rutor.is",
            "rutracker.org",
            "rutracker.net",
            "rutracker.nl",
            "nnmclub.to",
            "discord.com",
            "discordapp.com",
            "discord.gg",
            "discord.media",
            "discordapp.net",
            "discord-attachments-uploads-prd.storage.googleapis.com",
            "instagram.com",
            "cdninstagram.com",
            "twitter.com",
            "x.com",
            "twimg.com",
            "notion.so",
            "medium.com"
        };

        public SmartProxyServer(int listenPort, int socksPort)
        {
            _listenPort = listenPort;
            _socksPort = socksPort;
        }

        public int ListenPort
        {
            get { return _listenPort; }
        }

        public bool IsRunning
        {
            get { return _running; }
        }

        public void Start()
        {
            Stop();

            try
            {
                // Prevent .NET ThreadPool starvation during browser connection bursts (YouTube opens 15-30 sockets at once)
                int wMin, cMin;
                ThreadPool.GetMinThreads(out wMin, out cMin);
                if (wMin < 64 || cMin < 64)
                {
                    ThreadPool.SetMinThreads(Math.Max(wMin, 64), Math.Max(cMin, 64));
                }

                _listener = new TcpListener(IPAddress.Loopback, _listenPort);
                _listener.Start(128);
                _running = true;

                _acceptThread = new Thread(AcceptLoop);
                _acceptThread.IsBackground = true;
                _acceptThread.Start();

                Log(string.Format("Локальный PAC/HTTP-прокси запущен на 127.0.0.1:{0} (DNS 77.88.8.8:1253 -> ByeDPI :{1})", _listenPort, _socksPort));
            }
            catch (Exception ex)
            {
                _running = false;
                Log("ОШИБКА запуска локального прокси: " + ex.Message);
            }
        }

        public void Stop()
        {
            _running = false;
            if (_listener != null)
            {
                try { _listener.Stop(); } catch { }
                _listener = null;
            }
            lock (_dnsLock)
            {
                _dnsCache.Clear();
            }
        }

        private void AcceptLoop()
        {
            while (_running && _listener != null)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(HandleClient, client);
                }
                catch
                {
                    if (!_running) break;
                    Thread.Sleep(50);
                }
            }
        }

        private void HandleClient(object state)
        {
            TcpClient client = (TcpClient)state;
            NetworkStream clientStream = null;
            TcpClient upstream = null;
            NetworkStream upstreamStream = null;
            bool handedOff = false;

            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 8000;
                clientStream = client.GetStream();

                // Read HTTP request headers up to \r\n\r\n
                byte[] buffer = new byte[16384];
                int totalRead = 0;
                int headerEnd = -1;

                while (totalRead < buffer.Length)
                {
                    int n = clientStream.Read(buffer, totalRead, buffer.Length - totalRead);
                    if (n <= 0) return;
                    totalRead += n;

                    headerEnd = FindHeaderEnd(buffer, totalRead);
                    if (headerEnd >= 0) break;
                }

                if (headerEnd < 0) return;

                string headerText = Encoding.ASCII.GetString(buffer, 0, headerEnd);
                string[] lines = headerText.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                if (lines.Length == 0) return;

                string[] reqParts = lines[0].Split(' ');
                if (reqParts.Length < 2) return;

                string method = reqParts[0].ToUpperInvariant();
                string target = reqParts[1];

                // 1. PAC file request (supports both relative /proxy.pac and absolute http://.../proxy.pac)
                if (method == "GET" && target.IndexOf("/proxy.pac", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ServePacFile(clientStream);
                    return;
                }

                string targetHost = "";
                int targetPort = 80;
                bool isConnect = (method == "CONNECT");

                if (isConnect)
                {
                    targetPort = 443;
                    int colonIdx = target.LastIndexOf(':');
                    if (colonIdx > 0)
                    {
                        targetHost = target.Substring(0, colonIdx);
                        int.TryParse(target.Substring(colonIdx + 1), out targetPort);
                    }
                    else
                    {
                        targetHost = target;
                    }
                }
                else
                {
                    Uri uri;
                    if (Uri.TryCreate(target, UriKind.Absolute, out uri))
                    {
                        targetHost = uri.Host;
                        targetPort = uri.Port > 0 ? uri.Port : 80;
                    }
                    else
                    {
                        foreach (string line in lines)
                        {
                            if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                            {
                                string h = line.Substring(5).Trim();
                                int cIdx = h.LastIndexOf(':');
                                if (cIdx > 0)
                                {
                                    targetHost = h.Substring(0, cIdx);
                                    int.TryParse(h.Substring(cIdx + 1), out targetPort);
                                }
                                else
                                {
                                    targetHost = h;
                                }
                                break;
                            }
                        }
                    }
                }

                if (string.IsNullOrEmpty(targetHost))
                {
                    SendErrorResponse(clientStream, "400 Bad Request");
                    return;
                }

                // Resolve via Yandex DNS (77.88.8.8:1253) to bypass poisoned/VPN DNS
                IPAddress[] resolvedIps = ResolveDnsAll(targetHost);
                if (resolvedIps == null || resolvedIps.Length == 0)
                {
                    RuntimeLogger.Write("PROXY", string.Format("502 DNS fail for {0}:{1}", targetHost, targetPort));
                    SendErrorResponse(clientStream, "502 Bad Gateway");
                    return;
                }

                // Connect either directly (when kernel WinDivert/GoodbyeDPI is active) or via ByeDPI SOCKS5
                upstream = _directUpstream
                    ? ConnectDirect(resolvedIps, targetPort)
                    : ConnectViaSocks5List(resolvedIps, targetPort);
                if (upstream == null)
                {
                    RuntimeLogger.Write("PROXY", string.Format("502 Upstream connect fail for {0}:{1} (direct={2}, ip0={3})", targetHost, targetPort, _directUpstream, resolvedIps[0]));
                    SendErrorResponse(clientStream, "502 Bad Gateway");
                    return;
                }

                upstream.NoDelay = true;
                upstreamStream = upstream.GetStream();

                if (OnSiteProxied != null)
                {
                    OnSiteProxied(targetHost);
                }

                client.ReceiveTimeout = 0;

                if (isConnect)
                {
                    byte[] okResp = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                    clientStream.Write(okResp, 0, okResp.Length);
                    clientStream.Flush();

                    int extraOffset = headerEnd + 4;
                    if (totalRead > extraOffset)
                    {
                        upstreamStream.Write(buffer, extraOffset, totalRead - extraOffset);
                    }
                }
                else
                {
                    Uri uri;
                    string originPath = target;
                    if (Uri.TryCreate(target, UriKind.Absolute, out uri))
                    {
                        originPath = uri.PathAndQuery;
                    }

                    StringBuilder sb = new StringBuilder();
                    string proto = reqParts.Length >= 3 ? reqParts[2] : "HTTP/1.1";
                    sb.AppendFormat("{0} {1} {2}\r\n", method, originPath, proto);
                    for (int i = 1; i < lines.Length; i++)
                    {
                        string l = lines[i];
                        if (string.IsNullOrEmpty(l)) continue;
                        if (l.StartsWith("Proxy-Connection:", StringComparison.OrdinalIgnoreCase)) continue;
                        sb.Append(l).Append("\r\n");
                    }
                    sb.Append("Connection: close\r\n\r\n");

                    byte[] reqBytes = Encoding.ASCII.GetBytes(sb.ToString());
                    upstreamStream.Write(reqBytes, 0, reqBytes.Length);

                    int extraOffset = headerEnd + 4;
                    if (totalRead > extraOffset)
                    {
                        upstreamStream.Write(buffer, extraOffset, totalRead - extraOffset);
                    }
                    upstreamStream.Flush();
                }

                // Start non-blocking async IOCP bidirectional relay (zero blocked threads)
                handedOff = true;
                AsyncTunnel tunnel = new AsyncTunnel(client, clientStream, upstream, upstreamStream);
                tunnel.Start();
            }
            catch
            {
            }
            finally
            {
                if (!handedOff)
                {
                    try { if (upstreamStream != null) upstreamStream.Close(); } catch { }
                    try { if (upstream != null) upstream.Close(); } catch { }
                    try { if (clientStream != null) clientStream.Close(); } catch { }
                    try { if (client != null) client.Close(); } catch { }
                }
            }
        }

        private sealed class AsyncTunnel
        {
            private readonly TcpClient _client;
            private readonly NetworkStream _clientStream;
            private readonly TcpClient _upstream;
            private readonly NetworkStream _upstreamStream;
            private int _activeDirs = 2;
            private int _closed = 0;

            public AsyncTunnel(TcpClient client, NetworkStream clientStream, TcpClient upstream, NetworkStream upstreamStream)
            {
                _client = client;
                _clientStream = clientStream;
                _upstream = upstream;
                _upstreamStream = upstreamStream;
            }

            public void Start()
            {
                Pump(_clientStream, _upstreamStream, _upstream);
                Pump(_upstreamStream, _clientStream, _client);
            }

            private void Pump(NetworkStream src, NetworkStream dst, TcpClient dstClient)
            {
                byte[] buf = new byte[32768];
                AsyncCallback onRead = null;
                AsyncCallback onWrite = null;

                onWrite = delegate(IAsyncResult arWrite)
                {
                    try
                    {
                        dst.EndWrite(arWrite);
                        if (Volatile.Read(ref _closed) != 0) return;
                        src.BeginRead(buf, 0, buf.Length, onRead, null);
                    }
                    catch
                    {
                        CloseBoth();
                    }
                };

                onRead = delegate(IAsyncResult arRead)
                {
                    try
                    {
                        int n = src.EndRead(arRead);
                        if (n <= 0)
                        {
                            try { dstClient.Client.Shutdown(SocketShutdown.Send); } catch { }
                            if (Interlocked.Decrement(ref _activeDirs) <= 0)
                            {
                                CloseBoth();
                            }
                            return;
                        }
                        if (Volatile.Read(ref _closed) != 0) return;
                        dst.BeginWrite(buf, 0, n, onWrite, null);
                    }
                    catch
                    {
                        CloseBoth();
                    }
                };

                try
                {
                    src.BeginRead(buf, 0, buf.Length, onRead, null);
                }
                catch
                {
                    CloseBoth();
                }
            }

            private void CloseBoth()
            {
                if (Interlocked.Exchange(ref _closed, 1) != 0) return;
                try { _upstreamStream.Close(); } catch { }
                try { _upstream.Close(); } catch { }
                try { _clientStream.Close(); } catch { }
                try { _client.Close(); } catch { }
            }
        }

        public static List<string> BuildBlockedDomainList()
        {
            List<string> domains = new List<string>(CoreBlockedDomains);

            try
            {
                string sitesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"config\sites.txt");
                if (File.Exists(sitesPath))
                {
                    foreach (string raw in File.ReadAllLines(sitesPath, Encoding.UTF8))
                    {
                        string d = raw.Trim().ToLowerInvariant();
                        if (string.IsNullOrEmpty(d) || d.StartsWith("#")) continue;
                        if (d == "googleapis.com" || d == "github.com") continue;
                        if (!domains.Contains(d))
                        {
                            domains.Add(d);
                        }
                    }
                }
            }
            catch { }

            return domains;
        }

        private void ServePacFile(NetworkStream clientStream)
        {
            List<string> domains = BuildBlockedDomainList();

            StringBuilder jsList = new StringBuilder();
            for (int i = 0; i < domains.Count; i++)
            {
                if (i > 0) jsList.Append(",");
                jsList.Append("\"").Append(domains[i].Replace("\"", "")).Append("\"");
            }

            string pacBody =
                "function FindProxyForURL(url, host) {\r\n" +
                "    host = host.toLowerCase();\r\n" +
                "    if (host === \"127.0.0.1\" || host === \"localhost\" || isPlainHostName(host)) return \"DIRECT\";\r\n" +
                "    var domains = [" + jsList.ToString() + "];\r\n" +
                "    for (var i = 0; i < domains.length; i++) {\r\n" +
                "        var d = domains[i];\r\n" +
                "        if (host === d || dnsDomainIs(host, \".\" + d)) {\r\n" +
                "            return \"PROXY 127.0.0.1:" + _listenPort + "; DIRECT\";\r\n" +
                "        }\r\n" +
                "    }\r\n" +
                "    return \"DIRECT\";\r\n" +
                "}\r\n";

            byte[] bodyBytes = Encoding.UTF8.GetBytes(pacBody);
            string headers = string.Format(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: application/x-ns-proxy-autoconfig; charset=utf-8\r\n" +
                "Cache-Control: no-cache, no-store, must-revalidate\r\n" +
                "Content-Length: {0}\r\n" +
                "Connection: close\r\n\r\n", bodyBytes.Length);
            byte[] headerBytes = Encoding.ASCII.GetBytes(headers);

            clientStream.Write(headerBytes, 0, headerBytes.Length);
            clientStream.Write(bodyBytes, 0, bodyBytes.Length);
            clientStream.Flush();
        }

        public static TcpClient ConnectDirect(IPAddress[] ips, int port)
        {
            if (ips == null || ips.Length == 0) return null;
            int maxTry = Math.Min(ips.Length, 3);
            for (int i = 0; i < maxTry; i++)
            {
                TcpClient cli = null;
                try
                {
                    cli = new TcpClient();
                    IAsyncResult ar = cli.BeginConnect(ips[i], port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(2500, false) || !cli.Connected)
                    {
                        cli.Close();
                        continue;
                    }
                    cli.EndConnect(ar);
                    return cli;
                }
                catch
                {
                    try { if (cli != null) cli.Close(); } catch { }
                }
            }
            return null;
        }

        private TcpClient ConnectViaSocks5List(IPAddress[] ips, int port)
        {
            if (ips == null || ips.Length == 0) return null;
            int maxTry = Math.Min(ips.Length, 3);
            for (int i = 0; i < maxTry; i++)
            {
                TcpClient s = ConnectViaSocks5(_socksPort, ips[i], port);
                if (s != null) return s;
            }
            return null;
        }

        public static TcpClient ConnectViaSocks5(int socksPort, IPAddress ip, int port)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                TcpClient socks = null;
                try
                {
                    socks = new TcpClient();
                    IAsyncResult ar = socks.BeginConnect(IPAddress.Loopback, socksPort, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(2000, false) || !socks.Connected)
                    {
                        socks.Close();
                        if (attempt == 0) Thread.Sleep(150);
                        continue;
                    }
                    socks.EndConnect(ar);
                    socks.ReceiveTimeout = 6000;
                    socks.SendTimeout = 6000;

                    NetworkStream ns = socks.GetStream();
                    ns.Write(new byte[] { 0x05, 0x01, 0x00 }, 0, 3);
                    byte[] greetResp = new byte[2];
                    if (ReadExact(ns, greetResp, 2) != 2 || greetResp[0] != 0x05 || greetResp[1] != 0x00)
                    {
                        socks.Close();
                        continue;
                    }

                    byte[] ipBytes = ip.GetAddressBytes();
                    if (ipBytes.Length != 4)
                    {
                        socks.Close();
                        return null;
                    }

                    byte[] req = new byte[10];
                    req[0] = 0x05;
                    req[1] = 0x01;
                    req[2] = 0x00;
                    req[3] = 0x01;
                    req[4] = ipBytes[0];
                    req[5] = ipBytes[1];
                    req[6] = ipBytes[2];
                    req[7] = ipBytes[3];
                    req[8] = (byte)(port >> 8);
                    req[9] = (byte)(port & 0xFF);

                    ns.Write(req, 0, 10);

                    byte[] repHead = new byte[4];
                    if (ReadExact(ns, repHead, 4) != 4 || repHead[1] != 0x00)
                    {
                        socks.Close();
                        return null;
                    }

                    int addrLen = 4;
                    if (repHead[3] == 0x04) addrLen = 16;
                    else if (repHead[3] == 0x03)
                    {
                        byte[] lenByte = new byte[1];
                        if (ReadExact(ns, lenByte, 1) != 1) { socks.Close(); return null; }
                        addrLen = lenByte[0];
                    }
                    byte[] tail = new byte[addrLen + 2];
                    ReadExact(ns, tail, tail.Length);

                    socks.ReceiveTimeout = 0;
                    socks.SendTimeout = 0;
                    return socks;
                }
                catch
                {
                    try { if (socks != null) socks.Close(); } catch { }
                    if (attempt == 0) Thread.Sleep(150);
                }
            }
            return null;
        }

        public IPAddress[] ResolveDnsAll(string host)
        {
            IPAddress parsed;
            if (IPAddress.TryParse(host, out parsed))
            {
                if (parsed.AddressFamily == AddressFamily.InterNetwork) return new IPAddress[] { parsed };
            }

            lock (_dnsLock)
            {
                DnsCacheEntry cached;
                if (_dnsCache.TryGetValue(host, out cached) && cached.ExpiryUtc > DateTime.UtcNow)
                {
                    return cached.Ips;
                }
            }

            string dnsSource = "77.88.8.8:1253";
            IPAddress[] ips = QueryDnsUdp(host, "77.88.8.8", 1253, 1500);
            if (ips == null || ips.Length == 0) { dnsSource = "77.88.8.8:1253(retry)"; ips = QueryDnsUdp(host, "77.88.8.8", 1253, 1500); }
            if (ips == null || ips.Length == 0) { dnsSource = "77.88.8.8:53"; ips = QueryDnsUdp(host, "77.88.8.8", 53, 1500); }
            if (ips == null || ips.Length == 0) { dnsSource = "8.8.8.8:53"; ips = QueryDnsUdp(host, "8.8.8.8", 53, 1500); }

            if (ips == null || ips.Length == 0)
            {
                dnsSource = "OS-DNS";
                try
                {
                    List<IPAddress> list = new List<IPAddress>();
                    foreach (IPAddress a in Dns.GetHostAddresses(host))
                    {
                        if (a.AddressFamily == AddressFamily.InterNetwork)
                        {
                            list.Add(a);
                        }
                    }
                    if (list.Count > 0) ips = list.ToArray();
                }
                catch { }
            }

            if (ips != null && ips.Length > 0)
            {
                lock (_dnsLock)
                {
                    _dnsCache[host] = new DnsCacheEntry
                    {
                        Ips = ips,
                        ExpiryUtc = DateTime.UtcNow.AddMinutes(5)
                    };
                }
                string[] ipStrs = new string[Math.Min(ips.Length, 4)];
                for (int k = 0; k < ipStrs.Length; k++) ipStrs[k] = ips[k].ToString();
                RuntimeLogger.Write("DNS", string.Format("{0} -> [{1}] (total={2}, via {3})", host, string.Join(", ", ipStrs), ips.Length, dnsSource));
            }
            else
            {
                RuntimeLogger.Write("DNS", string.Format("{0} -> FAILED (all resolvers)", host));
            }

            return ips;
        }

        private IPAddress[] QueryDnsUdp(string host, string dnsServer, int port, int timeoutMs)
        {
            try
            {
                using (UdpClient udp = new UdpClient())
                {
                    udp.Client.ReceiveTimeout = timeoutMs;
                    udp.Client.SendTimeout = timeoutMs;

                    ushort txId;
                    lock (_rand)
                    {
                        txId = (ushort)_rand.Next(1, 65535);
                    }

                    byte[] packet = BuildDnsQuery(host, txId);
                    udp.Send(packet, packet.Length, dnsServer, port);

                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] resp = udp.Receive(ref remote);
                    return ParseDnsResponseAllA(resp, txId);
                }
            }
            catch
            {
                return null;
            }
        }

        private static byte[] BuildDnsQuery(string host, ushort txId)
        {
            MemoryStream ms = new MemoryStream();
            ms.WriteByte((byte)(txId >> 8));
            ms.WriteByte((byte)(txId & 0xFF));
            ms.WriteByte(0x01); ms.WriteByte(0x00);
            ms.WriteByte(0x00); ms.WriteByte(0x01);
            ms.WriteByte(0x00); ms.WriteByte(0x00);
            ms.WriteByte(0x00); ms.WriteByte(0x00);
            ms.WriteByte(0x00); ms.WriteByte(0x00);

            foreach (string label in host.Split('.'))
            {
                if (string.IsNullOrEmpty(label)) continue;
                byte[] labelBytes = Encoding.ASCII.GetBytes(label);
                ms.WriteByte((byte)labelBytes.Length);
                ms.Write(labelBytes, 0, labelBytes.Length);
            }
            ms.WriteByte(0x00);
            ms.WriteByte(0x00); ms.WriteByte(0x01);
            ms.WriteByte(0x00); ms.WriteByte(0x01);
            return ms.ToArray();
        }

        private static IPAddress[] ParseDnsResponseAllA(byte[] resp, ushort expectedTxId)
        {
            if (resp == null || resp.Length < 12) return null;
            ushort respId = (ushort)((resp[0] << 8) | resp[1]);
            if (respId != expectedTxId) return null;

            int rcode = resp[3] & 0x0F;
            if (rcode != 0) return null;

            int qdCount = (resp[4] << 8) | resp[5];
            int anCount = (resp[6] << 8) | resp[7];

            int pos = 12;
            for (int q = 0; q < qdCount; q++)
            {
                pos = SkipDnsName(resp, pos);
                if (pos < 0 || pos + 4 > resp.Length) return null;
                pos += 4;
            }

            List<IPAddress> addrs = new List<IPAddress>();
            for (int a = 0; a < anCount; a++)
            {
                pos = SkipDnsName(resp, pos);
                if (pos < 0 || pos + 10 > resp.Length) break;

                int type = (resp[pos] << 8) | resp[pos + 1];
                int rdLen = (resp[pos + 8] << 8) | resp[pos + 9];
                pos += 10;

                if (pos + rdLen > resp.Length) break;

                if (type == 1 && rdLen == 4)
                {
                    addrs.Add(new IPAddress(new byte[] { resp[pos], resp[pos + 1], resp[pos + 2], resp[pos + 3] }));
                }

                pos += rdLen;
            }

            return addrs.Count > 0 ? addrs.ToArray() : null;
        }

        private static int SkipDnsName(byte[] buf, int pos)
        {
            int safety = 0;
            while (pos < buf.Length && safety++ < 128)
            {
                byte len = buf[pos];
                if (len == 0) return pos + 1;
                if ((len & 0xC0) == 0xC0)
                {
                    return pos + 2;
                }
                pos += 1 + len;
            }
            return -1;
        }

        private static int ReadExact(Stream s, byte[] buf, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int n = s.Read(buf, offset, count - offset);
                if (n <= 0) return offset;
                offset += n;
            }
            return offset;
        }

        private static int FindHeaderEnd(byte[] buf, int length)
        {
            for (int i = 0; i <= length - 4; i++)
            {
                if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                {
                    return i;
                }
            }
            return -1;
        }

        private static void SendErrorResponse(NetworkStream s, string status)
        {
            try
            {
                byte[] b = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nConnection: close\r\n\r\n");
                s.Write(b, 0, b.Length);
                s.Flush();
            }
            catch { }
        }

        private void Log(string msg)
        {
            if (OnLog != null) OnLog(msg);
        }
    }
}
