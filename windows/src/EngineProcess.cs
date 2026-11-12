using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace DpiTunnel
{
    public class EngineProcess
    {
        private sealed class ProbeCandidate
        {
            public readonly string Engine;
            public readonly string Name;
            public readonly string Args;

            public ProbeCandidate(string engine, string name, string args)
            {
                Engine = engine;
                Name = name;
                Args = args;
            }
        }

        private Process _process;
        private Process _ciadpiHelper;
        private SmartProxyServer _smartProxy;
        private string _currentEngine = "";
        private string _activeStrategyLabel = "ByeDPI";
        private int _startGen = 0;
        private int _isProbing = 0;
        private int _zapretIfIdx = 0;
        private const int SmartProxyPort = 1085;

        public event Action<string> OnLog;
        public event Action<bool> OnStateChanged;
        public event Action<string> OnSiteProxied;

        private static readonly ProbeCandidate[] ZapretCandidates = new ProbeCandidate[]
        {
            new ProbeCandidate("Zapret", "z-multidisorder-if", "{WF_IFACE}--wf-l3=ipv4 --wf-tcp=443 --filter-tcp=443 --hostlist=gdpi_blacklist.txt --dpi-desync=multidisorder --dpi-desync-split-pos=1,sniext+1,host+1,midsld,endhost-1"),
            new ProbeCandidate("Zapret", "z-dis-1", "{WF_IFACE}--wf-l3=ipv4 --wf-tcp=443 --filter-tcp=443 --hostlist=gdpi_blacklist.txt --dpi-desync=multidisorder --dpi-desync-split-pos=1"),
            new ProbeCandidate("Zapret", "z-dis-1-midsld", "{WF_IFACE}--wf-l3=ipv4 --wf-tcp=443 --filter-tcp=443 --hostlist=gdpi_blacklist.txt --dpi-desync=multidisorder --dpi-desync-split-pos=1,midsld"),
            new ProbeCandidate("Zapret", "z-multisplit-1", "{WF_IFACE}--wf-l3=ipv4 --wf-tcp=443 --filter-tcp=443 --hostlist=gdpi_blacklist.txt --dpi-desync=multisplit --dpi-desync-split-pos=1,midsld"),
            new ProbeCandidate("Zapret", "z-seqovl-split", "{WF_IFACE}--wf-l3=ipv4 --wf-tcp=443 --filter-tcp=443 --hostlist=gdpi_blacklist.txt --dpi-desync=multisplit --dpi-desync-split-pos=2,midsld --dpi-desync-split-seqovl=681 --dpi-desync-split-seqovl-pattern=@files/tls_clienthello_www_google_com.bin"),
            new ProbeCandidate("Zapret", "z-seqovl-dis", "{WF_IFACE}--wf-l3=ipv4 --wf-tcp=443 --filter-tcp=443 --hostlist=gdpi_blacklist.txt --dpi-desync=multidisorder --dpi-desync-split-pos=2,midsld --dpi-desync-split-seqovl=1 --dpi-desync-split-seqovl-pattern=0x00"),
            new ProbeCandidate("Zapret", "z-hostfake-badseq", "{WF_IFACE}--wf-l3=ipv4 --wf-tcp=443 --filter-tcp=443 --hostlist=gdpi_blacklist.txt --dpi-desync=hostfakesplit --dpi-desync-hostfakesplit-midhost=midsld --dpi-desync-hostfakesplit-mod=host=ya.ru,altorder=1 --dpi-desync-fooling=badseq --dpi-desync-badseq-increment=0x80000000"),
            new ProbeCandidate("Zapret", "z-hostfake-md5", "{WF_IFACE}--wf-l3=ipv4 --wf-tcp=443 --filter-tcp=443 --hostlist=gdpi_blacklist.txt --dpi-desync=hostfakesplit --dpi-desync-hostfakesplit-midhost=midsld --dpi-desync-hostfakesplit-mod=host=ya.ru,altorder=1 --dpi-desync-fooling=md5sig"),
            new ProbeCandidate("Zapret", "z-fakedsplit-md5", "{WF_IFACE}--wf-l3=ipv4 --wf-tcp=443 --filter-tcp=443 --hostlist=gdpi_blacklist.txt --dpi-desync=fakedsplit --dpi-desync-split-pos=1,midsld --dpi-desync-fooling=md5sig --dpi-desync-fakedsplit-pattern=@files/tls_clienthello_www_google_com.bin"),
            new ProbeCandidate("Zapret", "z-md5sig-google", "{WF_IFACE}--wf-l3=ipv4 --wf-tcp=443 --filter-tcp=443 --hostlist=gdpi_blacklist.txt --dpi-desync=fake,multidisorder --dpi-desync-split-pos=1,midsld --dpi-desync-fooling=md5sig --dpi-desync-repeats=4 --dpi-desync-fake-tls=@files/tls_clienthello_www_google_com.bin --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com"),
            new ProbeCandidate("Zapret", "z-badseq-vk", "{WF_IFACE}--wf-l3=ipv4 --wf-tcp=443 --filter-tcp=443 --hostlist=gdpi_blacklist.txt --dpi-desync=fake,multidisorder --dpi-desync-split-pos=1,midsld --dpi-desync-fooling=badseq --dpi-desync-badseq-increment=0x80000000 --dpi-desync-repeats=6 --dpi-desync-fake-tls=@files/tls_clienthello_vk_com.bin --dpi-desync-fake-tls-mod=rnd,dupsid"),
            new ProbeCandidate("Zapret", "z-datanoack", "{WF_IFACE}--wf-l3=ipv4 --wf-tcp=443 --filter-tcp=443 --hostlist=gdpi_blacklist.txt --dpi-desync=fake,multidisorder --dpi-desync-split-pos=1,midsld --dpi-desync-fooling=datanoack --dpi-desync-repeats=3 --dpi-desync-fake-tls=@files/tls_clienthello_www_google_com.bin --dpi-desync-fake-tls-mod=rnd,dupsid,sni=ya.ru"),
            new ProbeCandidate("Zapret", "z-ts-google", "{WF_IFACE}--wf-l3=ipv4 --wf-tcp=443 --filter-tcp=443 --hostlist=gdpi_blacklist.txt --dpi-desync=fake,multidisorder --dpi-desync-split-pos=1,midsld --dpi-desync-fooling=ts --dpi-desync-repeats=4 --dpi-desync-fake-tls=@files/tls_clienthello_www_google_com.bin --dpi-desync-fake-tls-mod=rnd,dupsid,sni=ya.ru"),
            new ProbeCandidate("Zapret", "z-syndata-dis", "{WF_IFACE}--wf-l3=ipv4 --wf-tcp=443 --filter-tcp=443 --hostlist=gdpi_blacklist.txt --dpi-desync=syndata,multidisorder --dpi-desync-split-pos=1,sniext+1,midsld")
        };

        private static readonly ProbeCandidate[] ByeDpiDefaultCandidates = new ProbeCandidate[]
        {
            new ProbeCandidate("ByeDPI", "split+oob+sni", "-s 1 -o 1 -s 1+s -M hcsmix"),
            new ProbeCandidate("ByeDPI", "dis1+sni", "-d 1 -s 1+s -M hcsmix"),
            new ProbeCandidate("ByeDPI", "dis1+oob+sni", "-d 1 -o 1 -s 1+s -M hcsmix"),
            new ProbeCandidate("ByeDPI", "multi-split", "-s 1 -s 2+s -s 5+s -M hcsmix"),
            new ProbeCandidate("ByeDPI", "chunk-2b", "-s 1:16:2 -M hcsmix"),
            new ProbeCandidate("ByeDPI", "fake-ttl3-ya", "-f -1 -t 3 -n ya.ru -s 1 -s 1+s -M hcsmix"),
            new ProbeCandidate("ByeDPI", "fake-ttl2-rand", "-f -1 -t 2 -Q rand -s 1+s -M hcsmix"),
            new ProbeCandidate("ByeDPI", "tlsrec-oob", "-s 1 -r 1+s -o 1 -M hcsmix")
        };

        private static readonly ProbeCandidate[] GoodbyeDpiCandidates = new ProbeCandidate[]
        {
            new ProbeCandidate("GoodbyeDPI", "rev-frag", "-p -q -e 1 -f 1 --reverse-frag"),
            new ProbeCandidate("GoodbyeDPI", "rev-wrongseq", "-p -q -e 1 -f 1 --reverse-frag --wrong-seq"),
            new ProbeCandidate("GoodbyeDPI", "rev-sni", "-p -q -e 1 -f 1 --reverse-frag --frag-by-sni"),
            new ProbeCandidate("GoodbyeDPI", "nat-sni", "-p -q -e 2 -f 2 --native-frag --frag-by-sni")
        };

        public bool IsRunning
        {
            get
            {
                try
                {
                    bool mainAlive = _process != null && !_process.HasExited;
                    bool helperAlive = _ciadpiHelper != null && !_ciadpiHelper.HasExited;
                    bool proxyAlive = _smartProxy != null && _smartProxy.IsRunning;
                    return proxyAlive && (mainAlive || helperAlive || _smartProxy.DirectUpstream);
                }
                catch
                {
                    return false;
                }
            }
        }

        public bool IsSmartProxyActive
        {
            get { return _smartProxy != null && _smartProxy.IsRunning; }
        }

        public string CurrentEngine
        {
            get { return _currentEngine; }
        }

        public string ActiveStrategyLabel
        {
            get { return _activeStrategyLabel; }
        }

        public static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        public void RefreshPac()
        {
            if (_smartProxy != null && _smartProxy.IsRunning)
            {
                string pacUrl = string.Format("http://127.0.0.1:{0}/proxy.pac?v={1}", SmartProxyPort, DateTime.UtcNow.Ticks);
                ProxyHelper.SetAutoConfigPac(true, pacUrl);
            }
        }

        public bool Start(AppSettings settings)
        {
            Stop();

            int myGen = Interlocked.Increment(ref _startGen);
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _currentEngine = settings.Engine;
            _activeStrategyLabel = "ByeDPI";

            // Ensure no stale hosts overrides interfere with dynamic CDN resolution
            HostsHelper.Remove();

            string conflictVpn = HostsHelper.GetConflictingVpnAdapter();
            bool vpnActive = !string.IsNullOrEmpty(conflictVpn);
            if (vpnActive)
            {
                Log("Обнаружен VPN (" + conflictVpn + "): совместный режим PAC + Яндекс DNS (77.88.8.8:1253).");
            }

            int socksPort = settings.Port > 0 ? settings.Port : 1080;
            string winwsExe = Path.Combine(baseDir, @"bin\zapret\winws.exe");
            string ciaExe = Path.Combine(baseDir, @"bin\ciadpi.exe");
            string gdpiExe = Path.Combine(baseDir, @"bin\goodbyedpi\goodbyedpi.exe");

            RuntimeLogger.Write("ENGINE", string.Format(
                "Start: admin={0}, engine={1}, preset={2}, port={3}, vpn='{4}', winws={5}, cia={6}, gdpi={7}",
                IsAdministrator(), settings.Engine, settings.Preset, socksPort, conflictVpn,
                File.Exists(winwsExe), File.Exists(ciaExe), File.Exists(gdpiExe)));

            if (!File.Exists(winwsExe) && !File.Exists(ciaExe) && !File.Exists(gdpiExe))
            {
                Log("ОШИБКА: Не найдены движки обхода (winws.exe / ciadpi.exe / goodbyedpi.exe).");
                return false;
            }

            // Start SmartProxyServer (PAC + Yandex 77.88.8.8:1253 UDP DNS)
            _smartProxy = new SmartProxyServer(SmartProxyPort, socksPort);
            _smartProxy.OnLog += (msg) => Log(msg);
            _smartProxy.OnSiteProxied += (host) =>
            {
                if (OnSiteProxied != null) OnSiteProxied(host);
            };
            _smartProxy.Start();

            if (_smartProxy.IsRunning)
            {
                RefreshPac();
                Log("Умный выборочный прокси (PAC) активирован для заблокированных сайтов.");
            }
            else if (settings.AutoEnableSystemProxy)
            {
                ProxyHelper.SetSystemProxy(true, "127.0.0.1:" + socksPort);
                Log("Системный прокси SOCKS5 активирован (127.0.0.1:" + socksPort + ")");
            }

            if (IsCancelled(myGen)) return false;

            // Warm DNS cache via Yandex DNS 77.88.8.8:1253 before probing strategies
            if (_smartProxy != null && _smartProxy.IsRunning)
            {
                _smartProxy.ResolveDnsAll("www.youtube.com");
                _smartProxy.ResolveDnsAll("rutor.info");
                _smartProxy.ResolveDnsAll("rutracker.org");
            }

            if (IsCancelled(myGen)) return false;

            bool isCustom = settings.Preset.Contains("Пользовательский") && !string.IsNullOrEmpty(settings.CustomArgs);
            if (isCustom)
            {
                return StartCustomMode(baseDir, winwsExe, ciaExe, gdpiExe, socksPort, settings, vpnActive);
            }

            bool ok = AutoSelectAndStart(baseDir, winwsExe, ciaExe, gdpiExe, socksPort, settings, vpnActive, myGen);
            if (!ok || IsCancelled(myGen))
            {
                return false;
            }

            if (OnStateChanged != null) OnStateChanged(true);
            return true;
        }

        private bool StartCustomMode(string baseDir, string winwsExe, string ciaExe, string gdpiExe, int socksPort, AppSettings settings, bool vpnActive)
        {
            if (settings.Engine.Equals("GoodbyeDPI", StringComparison.OrdinalIgnoreCase) && !vpnActive && IsAdministrator() && File.Exists(gdpiExe))
            {
                string blacklist = PrepareDomainListFile(baseDir, @"config\gdpi_blacklist.txt", true);
                _smartProxy.DirectUpstream = true;
                _process = LaunchPacketDriver(gdpiExe, blacklist, new ProbeCandidate("GoodbyeDPI", "custom", settings.CustomArgs), true);
                if (_process != null && !_process.HasExited)
                {
                    _activeStrategyLabel = "GoodbyeDPI";
                    if (OnStateChanged != null) OnStateChanged(true);
                    return true;
                }
            }

            if (File.Exists(ciaExe))
            {
                _smartProxy.DirectUpstream = false;
                string hosts = settings.Engine.Equals("ByeDPI", StringComparison.OrdinalIgnoreCase) && !settings.GlobalMode
                    ? PrepareDomainListFile(baseDir, @"config\hosts_active.txt", false)
                    : "";
                _ciadpiHelper = LaunchByeDpi(ciaExe, socksPort, settings.CustomArgs, hosts, true);
                if (_ciadpiHelper != null && !_ciadpiHelper.HasExited)
                {
                    _activeStrategyLabel = "ByeDPI";
                    if (OnStateChanged != null) OnStateChanged(true);
                    return true;
                }
            }

            return false;
        }

        private bool AutoSelectAndStart(string baseDir, string winwsExe, string ciaExe, string gdpiExe, int socksPort, AppSettings settings, bool vpnActive, int myGen)
        {
            Interlocked.Exchange(ref _isProbing, 1);
            try
            {
                bool isAdmin = IsAdministrator();
                bool hasZapret = isAdmin && File.Exists(winwsExe);
                bool hasCia = File.Exists(ciaExe);
                bool hasGdpi = isAdmin && File.Exists(gdpiExe);

                ProbeCandidate bestPartial = null;
                int bestPartialScore = 0; // 2 = YT only, 1 = Rutor only
                ProbeCandidate bestYtCia = null;
                ProbeCandidate bestRutorCia = null;

                string blacklist = PrepareDomainListFile(baseDir, @"config\gdpi_blacklist.txt", true);

                // 1. Try Zapret (winws.exe) first — supports multidisorder, seqovl, hostfakesplit, md5sig, badseq 0x80000000
                if (hasZapret)
                {
                    Log("Подбор стратегии Zapret (winws.exe) для YouTube и сайтов...");
                    ProbeCandidate winZapret = ProbePacketDriverList(winwsExe, blacklist, "winws", ZapretCandidates, myGen, ref bestPartial, ref bestPartialScore);
                    if (IsCancelled(myGen)) return false;
                    if (winZapret != null)
                    {
                        _activeStrategyLabel = "Zapret (" + winZapret.Name + ")";
                        Log("Выбран рабочий профиль: Zapret [" + winZapret.Name + "]");
                        return true;
                    }
                }
                else if (!isAdmin && File.Exists(winwsExe))
                {
                    Log("ВНИМАНИЕ: Для Zapret/WinDivert требуются права Администратора.");
                }

                // 2. If no Zapret strategy unlocked both, check ByeDPI (when VPN TUN isn't intercepting TCP)
                if (!vpnActive && hasCia)
                {
                    Log("Проверка ByeDPI (без VPN)...");
                    ProbeCandidate[] ciaOrder = GetOrderedByeDpiCandidates(settings);
                    int batch1Count = Math.Min(4, ciaOrder.Length);
                    ProbeCandidate win1 = ProbeByeDpiBatch(ciaExe, ciaOrder, 0, batch1Count, 1091, 1191, myGen, ref bestPartial, ref bestPartialScore, ref bestYtCia, ref bestRutorCia);
                    if (IsCancelled(myGen)) return false;
                    if (win1 != null)
                    {
                        return FinalizeByeDpiWinner(baseDir, ciaExe, socksPort, win1, bestRutorCia, settings);
                    }

                    if (ciaOrder.Length > batch1Count)
                    {
                        ProbeCandidate win2 = ProbeByeDpiBatch(ciaExe, ciaOrder, batch1Count, ciaOrder.Length - batch1Count, 1095, 1195, myGen, ref bestPartial, ref bestPartialScore, ref bestYtCia, ref bestRutorCia);
                        if (IsCancelled(myGen)) return false;
                        if (win2 != null)
                        {
                            return FinalizeByeDpiWinner(baseDir, ciaExe, socksPort, win2, bestRutorCia, settings);
                        }
                    }
                }

                // 3. Probe GoodbyeDPI (rev-frag opens Rutor/RuTracker/youtu.be/ytimg even when Happ TUN is active)
                if (hasGdpi)
                {
                    Log("Проверка GoodbyeDPI (WinDivert)...");
                    ProbeCandidate winGdpi = ProbePacketDriverList(gdpiExe, blacklist, "goodbyedpi", GoodbyeDpiCandidates, myGen, ref bestPartial, ref bestPartialScore);
                    if (IsCancelled(myGen)) return false;
                    if (winGdpi != null)
                    {
                        _activeStrategyLabel = "GoodbyeDPI (" + winGdpi.Name + ")";
                        Log("Выбран рабочий профиль: GoodbyeDPI [" + winGdpi.Name + "]");
                        return true;
                    }
                }

                if (IsCancelled(myGen)) return false;

                // Apply best partial match if no single strategy unlocked both YouTube and Rutor
                if (bestPartial != null)
                {
                    if (bestPartial.Engine == "Zapret" && hasZapret)
                    {
                        _smartProxy.DirectUpstream = true;
                        _process = LaunchPacketDriver(winwsExe, blacklist, bestPartial, true);
                        if (_process != null && !_process.HasExited)
                        {
                            _activeStrategyLabel = "Zapret (" + bestPartial.Name + ")";
                            Log("Запущен частичный профиль: Zapret [" + bestPartial.Name + "]");
                            return true;
                        }
                    }
                    else if (bestPartial.Engine == "GoodbyeDPI" && hasGdpi)
                    {
                        _smartProxy.DirectUpstream = true;
                        _process = LaunchPacketDriver(gdpiExe, blacklist, bestPartial, true);
                        if (_process != null && !_process.HasExited)
                        {
                            _activeStrategyLabel = "GoodbyeDPI (" + bestPartial.Name + ")";
                            Log("Запущен частичный профиль: GoodbyeDPI [" + bestPartial.Name + "]");
                            return true;
                        }
                    }
                    else if (hasCia)
                    {
                        return FinalizeByeDpiWinner(baseDir, ciaExe, socksPort, bestYtCia ?? bestPartial, bestRutorCia, settings);
                    }
                }

                if (hasCia)
                {
                    Log("Не удалось автоматически подтвердить YouTube; запуск резервного ByeDPI.");
                    return FinalizeByeDpiWinner(baseDir, ciaExe, socksPort, ByeDpiDefaultCandidates[0], ByeDpiDefaultCandidates[1], settings);
                }

                return false;
            }
            finally
            {
                Interlocked.Exchange(ref _isProbing, 0);
            }
        }

        private ProbeCandidate[] GetOrderedByeDpiCandidates(AppSettings settings)
        {
            if (settings.Preset.Contains("Альт") || settings.Preset.Contains("Режим 5") || settings.Preset.Contains("ALT"))
            {
                return new ProbeCandidate[]
                {
                    ByeDpiDefaultCandidates[1],
                    ByeDpiDefaultCandidates[3],
                    ByeDpiDefaultCandidates[4],
                    ByeDpiDefaultCandidates[2],
                    ByeDpiDefaultCandidates[0],
                    ByeDpiDefaultCandidates[5],
                    ByeDpiDefaultCandidates[6],
                    ByeDpiDefaultCandidates[7]
                };
            }
            return ByeDpiDefaultCandidates;
        }

        private ProbeCandidate ProbeByeDpiBatch(
            string ciaExe,
            ProbeCandidate[] list,
            int offset,
            int count,
            int baseSocksPort,
            int baseProxyPort,
            int myGen,
            ref ProbeCandidate bestPartial,
            ref int bestPartialScore,
            ref ProbeCandidate bestYtCia,
            ref ProbeCandidate bestRutorCia)
        {
            Process[] procs = new Process[count];
            SmartProxyServer[] proxies = new SmartProxyServer[count];
            bool[] ytResults = new bool[count];
            bool[] rutorResults = new bool[count];
            string[] ytDetails = new string[count];
            string[] rutorDetails = new string[count];
            CountdownEvent done = new CountdownEvent(count);

            try
            {
                for (int i = 0; i < count; i++)
                {
                    if (IsCancelled(myGen)) break;
                    int sPort = baseSocksPort + i;
                    int pPort = baseProxyPort + i;
                    ProbeCandidate cand = list[offset + i];

                    procs[i] = LaunchByeDpi(ciaExe, sPort, cand.Args, "", false);
                    proxies[i] = new SmartProxyServer(pPort, sPort);
                    proxies[i].Start();
                }

                Thread.Sleep(300);

                for (int i = 0; i < count; i++)
                {
                    int idx = i;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        try
                        {
                            if (!IsCancelled(myGen) && proxies[idx] != null && proxies[idx].IsRunning && procs[idx] != null && !procs[idx].HasExited)
                            {
                                bool ytOk, rutOk;
                                string ytDet, rutDet;
                                ProbeBothDetailed(baseProxyPort + idx, 3200, out ytOk, out rutOk, out ytDet, out rutDet);
                                ytResults[idx] = ytOk;
                                rutorResults[idx] = rutOk;
                                ytDetails[idx] = ytDet;
                                rutorDetails[idx] = rutDet;
                            }
                            else
                            {
                                string reason = (procs[idx] == null) ? "not started" : (procs[idx].HasExited ? ("exited code=" + procs[idx].ExitCode) : "proxy not running");
                                ytDetails[idx] = reason;
                                rutorDetails[idx] = reason;
                            }
                        }
                        finally
                        {
                            done.Signal();
                        }
                    });
                }

                done.Wait(4500);

                for (int i = 0; i < count; i++)
                {
                    ProbeCandidate cand = list[offset + i];
                    bool ytOk = ytResults[i];
                    bool rutOk = rutorResults[i];
                    int score = (ytOk ? 2 : 0) + (rutOk ? 1 : 0);

                    RuntimeLogger.Write("PROBE", string.Format("{0} [{1}] -> YT={2} | Rutor={3} (score={4})",
                        cand.Engine, cand.Name, ytDetails[i] ?? "N/A", rutorDetails[i] ?? "N/A", score));
                    Log(string.Format("   {0} [{1}]: YT={2} | Rutor={3}", cand.Engine, cand.Name, ytOk ? "OK" : "FAIL", rutOk ? "OK" : "FAIL"));

                    if (ytOk && bestYtCia == null) bestYtCia = cand;
                    if (rutOk && bestRutorCia == null) bestRutorCia = cand;

                    if (score > bestPartialScore)
                    {
                        bestPartialScore = score;
                        bestPartial = cand;
                    }

                    if (ytOk && rutOk)
                    {
                        return cand;
                    }
                }
            }
            finally
            {
                for (int i = 0; i < count; i++)
                {
                    if (proxies[i] != null)
                    {
                        try { proxies[i].Stop(); } catch { }
                    }
                    if (procs[i] != null)
                    {
                        KillProcessHandle(ref procs[i]);
                    }
                }
            }

            return null;
        }

        private ProbeCandidate ProbePacketDriverList(
            string exePath,
            string blacklistPath,
            string procName,
            ProbeCandidate[] candidates,
            int myGen,
            ref ProbeCandidate bestPartial,
            ref int bestPartialScore)
        {
            if (_smartProxy == null || !_smartProxy.IsRunning) return null;

            _smartProxy.DirectUpstream = true;
            KillProcessByName("winws");
            KillProcessByName("goodbyedpi");
            Thread.Sleep(200);

            bool isZapret = (procName == "winws");
            if (isZapret)
            {
                _zapretIfIdx = GetPrimaryPhysicalIpv4IfIndex();
                PrepareZapretLists(exePath, blacklistPath);
                RunWinwsDryRun(exePath);
            }

            string winwsDbgPath = Path.Combine(Path.GetDirectoryName(exePath), "winws_debug.log");
            if (isZapret)
            {
                try { if (File.Exists(winwsDbgPath)) File.Delete(winwsDbgPath); } catch { }
            }

            ProbeCandidate bestYtCand = null;
            int bestYtScore = 0;
            ProbeCandidate bestSitesCand = null;
            int bestSitesScore = 0;

            for (int i = 0; i < candidates.Length; i++)
            {
                if (IsCancelled(myGen)) break;

                ProbeCandidate cand = candidates[i];
                Log("Тест " + cand.Engine + " [" + cand.Name + "]...");

                _process = LaunchPacketDriver(exePath, blacklistPath, cand, false, false);
                if (_process == null)
                {
                    RuntimeLogger.Write("PROBE", cand.Engine + " [" + cand.Name + "] -> Launch returned null");
                    continue;
                }

                Thread.Sleep(350);
                if (_process.HasExited || IsCancelled(myGen))
                {
                    int ec = -1;
                    try { if (_process.HasExited) ec = _process.ExitCode; } catch { }
                    RuntimeLogger.Write("PROBE", string.Format("{0} [{1}] -> Exited early (exitCode={2})", cand.Engine, cand.Name, ec));
                    Log(string.Format("   {0} [{1}]: процесс завершился досрочно (код {2})", cand.Engine, cand.Name, ec));
                    KillProcessHandle(ref _process);
                    continue;
                }

                bool ytOk, rutOk, cfOk;
                string ytDet, rutDet, cfDet;
                ProbeTripleDetailed(SmartProxyPort, 2700, out ytOk, out rutOk, out cfOk, out ytDet, out rutDet, out cfDet);

                int score = (ytOk ? 4 : 0) + (rutOk ? 2 : 0) + (cfOk ? 1 : 0);
                RuntimeLogger.Write("PROBE", string.Format("{0} [{1}] -> YT={2} | Rutor={3} | RuTracker={4} (score={5})", cand.Engine, cand.Name, ytDet, rutDet, cfDet, score));
                Log(string.Format("   {0} [{1}]: YT={2} | Rutor={3} | RuTracker={4}", cand.Engine, cand.Name, ytOk ? "OK" : "FAIL", rutOk ? "OK" : "FAIL", cfOk ? "OK" : "FAIL"));

                if (ytOk)
                {
                    int ys = 4 + (rutOk ? 2 : 0) + (cfOk ? 1 : 0);
                    if (ys > bestYtScore) { bestYtScore = ys; bestYtCand = cand; }
                }
                if (rutOk || cfOk)
                {
                    int ss = (cfOk ? 4 : 0) + (rutOk ? 2 : 0);
                    if (ss > bestSitesScore) { bestSitesScore = ss; bestSitesCand = cand; }
                }

                if (score > bestPartialScore)
                {
                    bestPartialScore = score;
                    bestPartial = cand;
                }

                if (ytOk && rutOk && cfOk)
                {
                    return cand;
                }

                KillProcessHandle(ref _process);
                KillProcessByName(procName);
                Thread.Sleep(200);
            }

            if (isZapret && !IsCancelled(myGen) && bestYtCand != null && bestSitesCand != null && bestYtCand.Args != bestSitesCand.Args)
            {
                ProbeCandidate combo = BuildZapretDualProfileCandidate(bestYtCand, bestSitesCand);
                if (combo != null)
                {
                    Log("Комбинируем Zapret [" + combo.Name + "]...");
                    _process = LaunchPacketDriver(exePath, blacklistPath, combo, false, false);
                    if (_process != null)
                    {
                        Thread.Sleep(350);
                        if (!_process.HasExited && !IsCancelled(myGen))
                        {
                            bool ytOk, rutOk, cfOk;
                            string ytDet, rutDet, cfDet;
                            ProbeTripleDetailed(SmartProxyPort, 2700, out ytOk, out rutOk, out cfOk, out ytDet, out rutDet, out cfDet);
                            int score = (ytOk ? 4 : 0) + (rutOk ? 2 : 0) + (cfOk ? 1 : 0);
                            RuntimeLogger.Write("PROBE", string.Format("Zapret [{0}] -> YT={1} | Rutor={2} | RuTracker={3} (score={4})", combo.Name, ytDet, rutDet, cfDet, score));
                            Log(string.Format("   Zapret [{0}]: YT={1} | Rutor={2} | RuTracker={3}", combo.Name, ytOk ? "OK" : "FAIL", rutOk ? "OK" : "FAIL", cfOk ? "OK" : "FAIL"));
                            if (score > bestPartialScore)
                            {
                                bestPartialScore = score;
                                bestPartial = combo;
                            }
                            if (ytOk && rutOk && cfOk)
                            {
                                return combo;
                            }
                        }
                        KillProcessHandle(ref _process);
                        KillProcessByName(procName);
                        Thread.Sleep(200);
                    }
                }
            }

            return null;
        }

        private bool FinalizeByeDpiWinner(string baseDir, string ciaExe, int socksPort, ProbeCandidate primary, ProbeCandidate fallbackRutor, AppSettings settings)
        {
            string fullArgs = primary.Args;
            ProbeCandidate fallback = (fallbackRutor != null && fallbackRutor.Args != primary.Args)
                ? fallbackRutor
                : (primary.Args != ByeDpiDefaultCandidates[1].Args ? ByeDpiDefaultCandidates[1] : ByeDpiDefaultCandidates[7]);

            if (fallback != null && fallback.Args != primary.Args)
            {
                fullArgs = primary.Args + " -T 1 -L 3 -u 300 -A torst,ssl_err,none " + fallback.Args;
            }

            string hostsFile = settings.Engine.Equals("ByeDPI", StringComparison.OrdinalIgnoreCase) && !settings.GlobalMode
                ? PrepareDomainListFile(baseDir, @"config\hosts_active.txt", false)
                : "";

            _smartProxy.DirectUpstream = false;
            _ciadpiHelper = LaunchByeDpi(ciaExe, socksPort, fullArgs, hostsFile, true);
            if (_ciadpiHelper != null && !_ciadpiHelper.HasExited)
            {
                _activeStrategyLabel = "ByeDPI (" + primary.Name + ")";
                Log("Запущен ByeDPI на 127.0.0.1:" + socksPort + " [" + primary.Name + "]");
                return true;
            }
            return false;
        }

        private void ProbeTripleDetailed(int proxyPort, int timeoutMs, out bool ytOk, out bool rutorOk, out bool cfOk, out string ytDetail, out string rutorDetail, out string cfDetail)
        {
            bool yt = false;
            bool rut = false;
            bool cf = false;
            string ytDet = "timeout_wait";
            string rutDet = "timeout_wait";
            string cfDet = "timeout_wait";
            CountdownEvent ev = new CountdownEvent(3);

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    int code; long ms;
                    yt = Diagnostics.ProbeSiteDetailed("https://www.youtube.com", proxyPort, timeoutMs, out code, out ms, out ytDet);
                }
                finally { ev.Signal(); }
            });
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    int code; long ms;
                    rut = Diagnostics.ProbeSiteDetailed("https://rutor.info", proxyPort, timeoutMs, out code, out ms, out rutDet);
                }
                finally { ev.Signal(); }
            });
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    int code; long ms;
                    cf = Diagnostics.ProbeSiteDetailed("https://rutracker.org", proxyPort, timeoutMs, out code, out ms, out cfDet);
                }
                finally { ev.Signal(); }
            });

            ev.Wait(timeoutMs + 900);
            ytOk = yt;
            rutorOk = rut;
            cfOk = cf;
            ytDetail = ytDet;
            rutorDetail = rutDet;
            cfDetail = cfDet;
        }

        private static bool IsYoutubeOrGoogleDomain(string d)
        {
            return d.Contains("youtube") || d.Contains("youtu.be") || d == "yt.be"
                || d.Contains("googlevideo") || d.Contains("ytimg") || d.Contains("ggpht")
                || d.Contains("googleusercontent") || d.Contains("gstatic")
                || d.EndsWith(".google.com") || d == "google.com" || d.Contains("gvt1.com");
        }

        private static void PrepareZapretLists(string exePath, string blacklistPath)
        {
            try
            {
                if (string.IsNullOrEmpty(blacklistPath) || !File.Exists(blacklistPath)) return;
                string zapDir = Path.GetDirectoryName(exePath);
                string localList = Path.Combine(zapDir, "gdpi_blacklist.txt");
                File.Copy(blacklistPath, localList, true);

                List<string> ytList = new List<string>();
                List<string> sitesList = new List<string>();
                foreach (string raw in File.ReadAllLines(blacklistPath, Encoding.UTF8))
                {
                    string d = raw.Trim().ToLowerInvariant();
                    if (string.IsNullOrEmpty(d)) continue;
                    if (IsYoutubeOrGoogleDomain(d)) ytList.Add(d);
                    else sitesList.Add(d);
                }

                Encoding utf8NoBom = new UTF8Encoding(false);
                File.WriteAllLines(Path.Combine(zapDir, "gdpi_youtube.txt"), ytList.ToArray(), utf8NoBom);
                File.WriteAllLines(Path.Combine(zapDir, "gdpi_sites.txt"), sitesList.ToArray(), utf8NoBom);
            }
            catch (Exception ex)
            {
                RuntimeLogger.Write("ENGINE", "PrepareZapretLists err: " + ex.Message);
            }
        }

        private static ProbeCandidate BuildZapretDualProfileCandidate(ProbeCandidate ytCand, ProbeCandidate sitesCand)
        {
            const string globalPrefix = "{WF_IFACE}--wf-l3=ipv4 --wf-tcp=443 ";
            if (!ytCand.Args.StartsWith(globalPrefix) || !sitesCand.Args.StartsWith(globalPrefix))
            {
                return null;
            }
            string p1 = ytCand.Args.Substring(globalPrefix.Length).Replace("--hostlist=gdpi_blacklist.txt", "--hostlist=gdpi_youtube.txt");
            string p2 = sitesCand.Args.Substring(globalPrefix.Length).Replace("--hostlist=gdpi_blacklist.txt", "--hostlist=gdpi_sites.txt");
            return new ProbeCandidate("Zapret", ytCand.Name + "+" + sitesCand.Name, globalPrefix + p1 + " --new " + p2);
        }

        private void ProbeBothDetailed(int proxyPort, int timeoutMs, out bool ytOk, out bool rutorOk, out string ytDetail, out string rutorDetail)
        {
            bool yt = false;
            bool rut = false;
            string ytDet = "timeout_wait";
            string rutDet = "timeout_wait";
            CountdownEvent ev = new CountdownEvent(2);

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    int code; long ms;
                    yt = Diagnostics.ProbeSiteDetailed("https://www.youtube.com", proxyPort, timeoutMs, out code, out ms, out ytDet);
                }
                finally { ev.Signal(); }
            });
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    int code; long ms;
                    rut = Diagnostics.ProbeSiteDetailed("https://rutor.info", proxyPort, timeoutMs, out code, out ms, out rutDet);
                }
                finally { ev.Signal(); }
            });

            ev.Wait(timeoutMs + 800);
            ytOk = yt;
            rutorOk = rut;
            ytDetail = ytDet;
            rutorDetail = rutDet;
        }

        private Process LaunchByeDpi(string ciaExe, int socksPort, string strategyArgs, string hostsFile, bool attachEvents)
        {
            try
            {
                string fullArgs = string.Format("-i 127.0.0.1 -p {0} {1}", socksPort, strategyArgs);
                if (!string.IsNullOrEmpty(hostsFile))
                {
                    fullArgs += " -H \"" + hostsFile + "\"";
                }

                RuntimeLogger.Write("CMD", string.Format("ByeDPI: \"{0}\" {1}", ciaExe, fullArgs));

                ProcessStartInfo cpsi = new ProcessStartInfo();
                cpsi.FileName = ciaExe;
                cpsi.Arguments = fullArgs;
                cpsi.WorkingDirectory = Path.GetDirectoryName(ciaExe);
                cpsi.UseShellExecute = false;
                cpsi.CreateNoWindow = true;
                cpsi.RedirectStandardOutput = true;
                cpsi.RedirectStandardError = true;

                Process p = new Process();
                p.StartInfo = cpsi;
                p.EnableRaisingEvents = true;
                p.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data)) RuntimeLogger.Write("ByeDPI-OUT", e.Data);
                };
                p.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data)) RuntimeLogger.Write("ByeDPI-ERR", e.Data);
                };
                if (attachEvents)
                {
                    p.Exited += (s, e) =>
                    {
                        int ec = -1;
                        try { ec = p.ExitCode; } catch { }
                        RuntimeLogger.Write("ByeDPI", "Exited code=" + ec);
                        if (Thread.VolatileRead(ref _isProbing) == 0 && !IsRunning && OnStateChanged != null)
                        {
                            OnStateChanged(false);
                        }
                    };
                }

                if (p.Start())
                {
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    return p;
                }
            }
            catch (Exception ex)
            {
                RuntimeLogger.Write("ByeDPI-EX", ex.ToString());
                if (attachEvents) Log("ОШИБКА запуска ByeDPI: " + ex.Message);
            }
            return null;
        }

        private Process LaunchPacketDriver(string exePath, string blacklistPath, ProbeCandidate cand, bool attachEvents)
        {
            return LaunchPacketDriver(exePath, blacklistPath, cand, attachEvents, false);
        }

        private Process LaunchPacketDriver(string exePath, string blacklistPath, ProbeCandidate cand, bool attachEvents, bool writeDebugFile)
        {
            try
            {
                string fullArgs = cand.Args;
                if (cand.Engine == "Zapret")
                {
                    string ifPart = _zapretIfIdx > 0 ? ("--wf-iface=" + _zapretIfIdx + " ") : "";
                    fullArgs = fullArgs.Replace("{WF_IFACE}", ifPart);
                    if (writeDebugFile)
                    {
                        fullArgs = "--debug=@winws_debug.log " + fullArgs;
                    }
                }
                else if (cand.Engine == "GoodbyeDPI" && !string.IsNullOrEmpty(blacklistPath) && File.Exists(blacklistPath))
                {
                    fullArgs += " --blacklist \"" + blacklistPath + "\"";
                }

                string cwd = Path.GetDirectoryName(exePath);
                RuntimeLogger.Write("CMD", string.Format("{0} [{1}]: \"{2}\" {3} (cwd={4})", cand.Engine, cand.Name, exePath, fullArgs, cwd));

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exePath;
                psi.Arguments = fullArgs;
                psi.WorkingDirectory = cwd;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;

                Process p = new Process();
                p.StartInfo = psi;
                p.EnableRaisingEvents = true;

                p.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        RuntimeLogger.Write(cand.Engine + "-OUT", e.Data);
                        if (attachEvents) Log("[OUT] " + e.Data);
                    }
                };
                p.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        RuntimeLogger.Write(cand.Engine + "-ERR", e.Data);
                        if (attachEvents || e.Data.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 || e.Data.IndexOf("too long", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Log("[" + cand.Engine + "] " + e.Data);
                        }
                    }
                };
                p.Exited += (s, e) =>
                {
                    int ec = -1;
                    try { ec = p.ExitCode; } catch { }
                    RuntimeLogger.Write(cand.Engine, string.Format("[{0}] Exited code={1}", cand.Name, ec));
                    if (attachEvents) Log("Процесс " + cand.Engine + " завершился (код " + ec + ").");
                    if (Thread.VolatileRead(ref _isProbing) == 0 && !IsRunning && OnStateChanged != null)
                    {
                        OnStateChanged(false);
                    }
                };

                if (p.Start())
                {
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    return p;
                }
            }
            catch (Exception ex)
            {
                RuntimeLogger.Write(cand.Engine + "-EX", ex.ToString());
                Log("Ошибка запуска " + cand.Engine + ": " + ex.Message);
            }
            return null;
        }

        public void Stop()
        {
            Interlocked.Increment(ref _startGen);
            HostsHelper.Remove();

            if (_smartProxy != null)
            {
                _smartProxy.Stop();
                _smartProxy = null;
            }

            if (ProxyHelper.IsPacEnabled() || ProxyHelper.IsSystemProxyEnabled())
            {
                ProxyHelper.SetAutoConfigPac(false, "");
                ProxyHelper.SetSystemProxy(false, "");
                Log("Системный прокси / PAC Windows отключён.");
            }

            KillProcessHandle(ref _ciadpiHelper);
            KillProcessHandle(ref _process);

            KillProcessByName("ciadpi");
            KillProcessByName("goodbyedpi");
            KillProcessByName("winws");

            if (OnStateChanged != null) OnStateChanged(false);
        }

        private bool IsCancelled(int myGen)
        {
            return Thread.VolatileRead(ref _startGen) != myGen;
        }

        private static void KillProcessHandle(ref Process proc)
        {
            if (proc == null) return;
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill();
                    proc.WaitForExit(1500);
                }
            }
            catch { }
            finally
            {
                try { proc.Dispose(); } catch { }
                proc = null;
            }
        }

        private void KillProcessByName(string processName)
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName(processName))
                {
                    try { p.Kill(); p.WaitForExit(1000); } catch { }
                }
            }
            catch { }
        }

        private string PrepareDomainListFile(string baseDir, string relativeTargetPath, bool includeYoutubeList)
        {
            try
            {
                string targetPath = Path.Combine(baseDir, relativeTargetPath);
                List<string> domains = SmartProxyServer.BuildBlockedDomainList();

                if (includeYoutubeList)
                {
                    string ytList = Path.Combine(baseDir, @"config\russia-youtube.txt");
                    if (File.Exists(ytList))
                    {
                        foreach (string raw in File.ReadAllLines(ytList, Encoding.UTF8))
                        {
                            string d = raw.Trim().ToLowerInvariant();
                            if (string.IsNullOrEmpty(d) || d.StartsWith("#")) continue;
                            if (d == "googleapis.com" || d == "github.com") continue;
                            if (!domains.Contains(d)) domains.Add(d);
                        }
                    }
                }

                // UTF-8 without BOM so C/Cygwin tools (winws.exe, goodbyedpi.exe) match line 1 cleanly
                File.WriteAllLines(targetPath, domains.ToArray(), new UTF8Encoding(false));
                return targetPath;
            }
            catch
            {
                return "";
            }
        }

        private static int GetPrimaryPhysicalIpv4IfIndex()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    string combo = (ni.Name + " " + ni.Description).ToLowerInvariant();
                    if (combo.Contains("happ") || combo.Contains("sockstunnel") || combo.Contains("wireguard") || combo.Contains("hyper-v") || combo.Contains("vethernet") || combo.Contains("virtual"))
                        continue;

                    IPInterfaceProperties props = ni.GetIPProperties();
                    if (props == null || props.GatewayAddresses == null || props.GatewayAddresses.Count == 0) continue;
                    IPv4InterfaceProperties v4 = props.GetIPv4Properties();
                    if (v4 != null && v4.Index > 0)
                    {
                        RuntimeLogger.Write("ENGINE", string.Format("Zapret physical NIC selected: ifIdx={0} '{1}' ({2})", v4.Index, ni.Name, ni.Description));
                        return v4.Index;
                    }
                }
            }
            catch (Exception ex)
            {
                RuntimeLogger.Write("ENGINE", "GetPrimaryPhysicalIpv4IfIndex err: " + ex.Message);
            }
            return 0;
        }

        private static void RunWinwsDryRun(string exePath)
        {
            RunWinwsCheck(exePath, "--version", "winws-VER", 5000);
            string dryLog = Path.Combine(Path.GetDirectoryName(exePath), "winws_dry.log");
            try { if (File.Exists(dryLog)) File.Delete(dryLog); } catch { }
            RunWinwsCheck(exePath, "--debug=@winws_dry.log --dry-run --wf-l3=ipv4 --wf-tcp=443 --filter-tcp=443 --hostlist=gdpi_blacklist.txt --dpi-desync=multidisorder --dpi-desync-split-pos=1,sniext+1,host+1,midsld,endhost-1", "winws-DRY", 10000);
            DumpLogFile(dryLog, "winws-DRY-FILE", 60);
        }

        private static void RunWinwsCheck(string exePath, string args, string tag, int timeoutMs)
        {
            Process p = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exePath;
                psi.Arguments = args;
                psi.WorkingDirectory = Path.GetDirectoryName(exePath);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;

                p = new Process();
                p.StartInfo = psi;
                int linesLogged = 0;
                p.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data) && Interlocked.Increment(ref linesLogged) <= 40)
                    {
                        RuntimeLogger.Write(tag, e.Data);
                    }
                };
                p.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data) && Interlocked.Increment(ref linesLogged) <= 40)
                    {
                        RuntimeLogger.Write(tag + "-ERR", e.Data);
                    }
                };

                if (p.Start())
                {
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        RuntimeLogger.Write(tag, "Timeout (" + timeoutMs + "ms)");
                        try { p.Kill(); } catch { }
                    }
                    else
                    {
                        p.WaitForExit();
                        RuntimeLogger.Write(tag, "exitCode=" + p.ExitCode);
                    }
                }
            }
            catch (Exception ex)
            {
                RuntimeLogger.Write(tag + "-EX", ex.Message);
            }
            finally
            {
                try { if (p != null) p.Dispose(); } catch { }
            }
        }

        private static void DumpLogFile(string path, string category, int maxLines)
        {
            try
            {
                if (!File.Exists(path))
                {
                    RuntimeLogger.Write(category, "file not created: " + path);
                    return;
                }
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                RuntimeLogger.Write(category, "lines=" + lines.Length);
                int count = Math.Min(lines.Length, maxLines);
                for (int i = 0; i < count; i++)
                {
                    if (!string.IsNullOrEmpty(lines[i]))
                    {
                        RuntimeLogger.Write(category, lines[i]);
                    }
                }
            }
            catch (Exception ex)
            {
                RuntimeLogger.Write(category, "read err: " + ex.Message);
            }
        }

        private void Log(string msg)
        {
            if (OnLog != null) OnLog(msg);
        }
    }
}
