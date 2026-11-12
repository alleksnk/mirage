using System;
using System.IO;
using System.Text;

namespace DpiTunnel
{
    public static class RuntimeLogger
    {
        private static readonly object _lock = new object();
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public static string LogFilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dpi_runtime.log"); }
        }

        public static void InitSession(string header)
        {
            lock (_lock)
            {
                try
                {
                    string path = LogFilePath;
                    if (File.Exists(path))
                    {
                        FileInfo fi = new FileInfo(path);
                        if (fi.Length > 512 * 1024)
                        {
                            string oldPath = path + ".old";
                            if (File.Exists(oldPath)) File.Delete(oldPath);
                            File.Move(path, oldPath);
                        }
                    }

                    string line = string.Format(
                        "{0}============================================================{0}[{1:yyyy-MM-dd HH:mm:ss.fff}] [SESSION] {2}{0}============================================================{0}",
                        Environment.NewLine, DateTime.Now, header);
                    File.AppendAllText(path, line, Utf8NoBom);
                }
                catch { }
            }
        }

        public static void Write(string category, string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            lock (_lock)
            {
                try
                {
                    string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1}] {2}{3}",
                        DateTime.Now,
                        string.IsNullOrEmpty(category) ? "INFO" : category,
                        message,
                        Environment.NewLine);
                    File.AppendAllText(LogFilePath, line, Utf8NoBom);
                }
                catch { }
            }
        }
    }
}

