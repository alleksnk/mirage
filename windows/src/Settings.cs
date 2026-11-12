using System;
using System.IO;
using System.Collections.Generic;
using Microsoft.Win32;

namespace DpiTunnel
{
    public class AppSettings
    {
        public string Engine = "ByeDPI";
        public string Preset = "YouTube + Discord (Оптимальный)";
        public int Port = 1080;
        public bool AutoEnableSystemProxy = true;
        public bool MinimizeToTray = true;
        public bool AutoStartWithWindows = false;
        public bool GlobalMode = true;
        public string CustomArgs = "";

        private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "DpiTunnel";

        public static string GetConfigPath()
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return Path.Combine(dir, "settings.ini");
        }

        public static AppSettings Load()
        {
            AppSettings settings = new AppSettings();
            string path = GetConfigPath();
            if (!File.Exists(path))
            {
                return settings;
            }

            try
            {
                string[] lines = File.ReadAllLines(path);
                foreach (string rawLine in lines)
                {
                    string line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

                    int idx = line.IndexOf('=');
                    if (idx > 0)
                    {
                        string key = line.Substring(0, idx).Trim();
                        string val = line.Substring(idx + 1).Trim();

                        if (key.Equals("Engine", StringComparison.OrdinalIgnoreCase)) settings.Engine = val;
                        else if (key.Equals("Preset", StringComparison.OrdinalIgnoreCase)) settings.Preset = val;
                        else if (key.Equals("Port", StringComparison.OrdinalIgnoreCase)) int.TryParse(val, out settings.Port);
                        else if (key.Equals("AutoEnableSystemProxy", StringComparison.OrdinalIgnoreCase)) settings.AutoEnableSystemProxy = val == "1" || val.ToLower() == "true";
                        else if (key.Equals("MinimizeToTray", StringComparison.OrdinalIgnoreCase)) settings.MinimizeToTray = val == "1" || val.ToLower() == "true";
                        else if (key.Equals("AutoStartWithWindows", StringComparison.OrdinalIgnoreCase)) settings.AutoStartWithWindows = val == "1" || val.ToLower() == "true";
                        else if (key.Equals("GlobalMode", StringComparison.OrdinalIgnoreCase)) settings.GlobalMode = val == "1" || val.ToLower() == "true";
                        else if (key.Equals("CustomArgs", StringComparison.OrdinalIgnoreCase)) settings.CustomArgs = val;
                    }
                }
            }
            catch
            {
            }

            return settings;
        }

        public void Save()
        {
            try
            {
                string path = GetConfigPath();
                List<string> lines = new List<string>();
                lines.Add("# DPI Tunnel Settings");
                lines.Add("Engine=" + Engine);
                lines.Add("Preset=" + Preset);
                lines.Add("Port=" + Port);
                lines.Add("AutoEnableSystemProxy=" + (AutoEnableSystemProxy ? "1" : "0"));
                lines.Add("MinimizeToTray=" + (MinimizeToTray ? "1" : "0"));
                lines.Add("AutoStartWithWindows=" + (AutoStartWithWindows ? "1" : "0"));
                lines.Add("GlobalMode=" + (GlobalMode ? "1" : "0"));
                lines.Add("CustomArgs=" + CustomArgs);

                File.WriteAllLines(path, lines.ToArray());
                UpdateAutoStart(AutoStartWithWindows);
            }
            catch
            {
            }
        }

        public void UpdateAutoStart(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true))
                {
                    if (key != null)
                    {
                        if (enable)
                        {
                            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                            key.SetValue(AppName, "\"" + exePath + "\" --minimized");
                        }
                        else
                        {
                            if (key.GetValue(AppName) != null)
                            {
                                key.DeleteValue(AppName, false);
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }
    }
}

