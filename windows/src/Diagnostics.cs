using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;

namespace DpiTunnel
{
    public class CheckResult
    {
        public string Name;
        public string Url;
        public bool Success;
        public long ElapsedMs;
        public string Message;
    }

    public static class Diagnostics
    {
        public static readonly KeyValuePair<string, string>[] PopularResources = new KeyValuePair<string, string>[]
        {
            new KeyValuePair<string, string>("YouTube", "https://www.youtube.com"),
            new KeyValuePair<string, string>("Instagram", "https://www.instagram.com"),
            new KeyValuePair<string, string>("Discord", "https://discord.com"),
            new KeyValuePair<string, string>("Telegram", "tg_proxy"),
            new KeyValuePair<string, string>("RuTracker", "https://rutracker.org"),
            new KeyValuePair<string, string>("Rutor.info", "https://rutor.info"),
            new KeyValuePair<string, string>("X (Twitter)", "https://x.com"),
            new KeyValuePair<string, string>("Medium", "https://medium.com")
        };

        public static void CheckAllAsync(Action<CheckResult> onSingleResult, Action onFinished)
        {
            CheckAllAsync(onSingleResult, onFinished, false);
        }

        public static void CheckAllAsync(Action<CheckResult> onSingleResult, Action onFinished, bool useSmartProxy)
        {
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)12288 | SecurityProtocolType.Tls;
                ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
                ServicePointManager.DefaultConnectionLimit = 32;
            }
            catch { }

            int remaining = PopularResources.Length;
            foreach (KeyValuePair<string, string> entry in PopularResources)
            {
                KeyValuePair<string, string> item = entry;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    CheckResult result;
                    try
                    {
                        if (item.Key == "Telegram")
                        {
                            result = TestTelegramProxy();
                        }
                        else
                        {
                            result = TestSite(item.Key, item.Value, useSmartProxy);
                        }
                    }
                    catch (Exception ex)
                    {
                        result = new CheckResult
                        {
                            Name = item.Key,
                            Url = item.Value,
                            Success = false,
                            ElapsedMs = 0,
                            Message = "Ошибка"
                        };
                        RuntimeLogger.Write("DIAG", item.Key + " probe exception: " + ex.Message);
                    }

                    if (onSingleResult != null)
                    {
                        onSingleResult(result);
                    }

                    if (Interlocked.Decrement(ref remaining) == 0 && onFinished != null)
                    {
                        onFinished();
                    }
                });
            }
        }

        private static CheckResult TestTelegramProxy()
        {
            CheckResult res = new CheckResult();
            res.Name = "Telegram";
            res.Url = "tg_proxy";

            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect("156.239.251.46", 443, null, null);
                    bool wait = ar.AsyncWaitHandle.WaitOne(2500, false);
                    if (wait && client.Connected)
                    {
                        client.EndConnect(ar);
                        client.ReceiveTimeout = 2500;
                        client.SendTimeout = 2500;
                        using (SslStream ssl = new SslStream(client.GetStream(), false, delegate { return true; }))
                        {
                            ssl.AuthenticateAsClient("www.microsoft.com");
                        }
                        sw.Stop();
                        res.ElapsedMs = sw.ElapsedMilliseconds;
                        res.Success = true;
                        res.Message = string.Format("{0} мс", res.ElapsedMs);
                        RuntimeLogger.Write("DIAG", "Telegram MTProxy (156.239.251.46:443) -> OK (" + res.ElapsedMs + " ms)");
                        return res;
                    }
                }
            }
            catch (Exception ex)
            {
                RuntimeLogger.Write("DIAG", "Telegram MTProxy fail: " + ex.Message);
            }

            sw.Stop();
            res.ElapsedMs = sw.ElapsedMilliseconds;
            res.Success = false;
            res.Message = "Нужен прокси";
            return res;
        }

        public static bool ProbeSiteFast(string url, int proxyPort, int timeoutMs)
        {
            int code;
            long ms;
            string detail;
            return ProbeSiteDetailed(url, proxyPort, timeoutMs, out code, out ms, out detail);
        }

        public static bool ProbeSiteDetailed(string url, int proxyPort, int timeoutMs, out int httpStatus, out long elapsedMs, out string detail)
        {
            httpStatus = 0;
            elapsedMs = 0;
            detail = "";
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)12288 | SecurityProtocolType.Tls;
                ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
                HttpWebRequest req = CreateRequest(url, timeoutMs, proxyPort > 0 ? new WebProxy("127.0.0.1", proxyPort) : null);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    sw.Stop();
                    elapsedMs = sw.ElapsedMilliseconds;
                    httpStatus = (int)resp.StatusCode;
                    detail = string.Format("OK({0}, {1}ms)", httpStatus, elapsedMs);
                    return true;
                }
            }
            catch (WebException wex)
            {
                sw.Stop();
                elapsedMs = sw.ElapsedMilliseconds;
                if (wex.Response != null)
                {
                    try { httpStatus = (int)((HttpWebResponse)wex.Response).StatusCode; } catch { }
                    if (httpStatus == 502 || httpStatus == 504)
                    {
                        detail = string.Format("FAIL(HTTP {0}, {1}ms)", httpStatus, elapsedMs);
                        return false;
                    }
                    detail = string.Format("OK({0}, {1}ms)", httpStatus, elapsedMs);
                    return true;
                }

                string inner = wex.InnerException != null ? (" | " + wex.InnerException.Message) : "";
                detail = string.Format("FAIL({0}: {1}{2}, {3}ms)", wex.Status, wex.Message, inner, elapsedMs);
                return false;
            }
            catch (Exception ex)
            {
                sw.Stop();
                elapsedMs = sw.ElapsedMilliseconds;
                detail = string.Format("ERR({0}, {1}ms)", ex.Message, elapsedMs);
                return false;
            }
        }

        private static HttpWebRequest CreateRequest(string url, int timeoutMs, IWebProxy proxy)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.KeepAlive = false;
            req.ConnectionGroupName = Guid.NewGuid().ToString("N");
            req.ServicePoint.Expect100Continue = false;
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/128.0.0.0";
            req.AllowAutoRedirect = false;
            if (proxy != null)
            {
                req.Proxy = proxy;
            }
            return req;
        }

        private static CheckResult TestSite(string name, string url, bool viaSmartProxy)
        {
            CheckResult res = new CheckResult();
            res.Name = name;
            res.Url = url;

            int code;
            long ms;
            string detail;
            bool ok = ProbeSiteDetailed(url, viaSmartProxy ? 1085 : 0, 4200, out code, out ms, out detail);

            res.ElapsedMs = ms;
            res.Success = ok;
            if (ok)
            {
                res.Message = string.Format("{0} мс", res.ElapsedMs);
            }
            else if (detail.IndexOf("NameResolutionFailure") >= 0)
            {
                res.Message = "Нет DNS";
            }
            else if (code == 502 || code == 504)
            {
                res.Message = "Недоступен";
            }
            else
            {
                res.Message = "Таймаут";
            }

            RuntimeLogger.Write("DIAG", string.Format("{0} ({1}) viaProxy={2} -> {3}", name, url, viaSmartProxy, detail));
            return res;
        }
    }
}
