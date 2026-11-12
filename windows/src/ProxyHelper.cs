using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DpiTunnel
{
    public static class ProxyHelper
    {
        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        private const int INTERNET_OPTION_REFRESH = 37;

        private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        public static void SetSystemProxy(bool enable, string socksHostPort)
        {
            try
            {
                using (RegistryKey reg = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, true))
                {
                    if (reg != null)
                    {
                        if (enable)
                        {
                            reg.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                            // Set SOCKS proxy so all browsers (Chrome, Edge, Firefox, Brave) pick it up
                            reg.SetValue("ProxyServer", "socks=" + socksHostPort, RegistryValueKind.String);
                            // Avoid proxying local LAN
                            reg.SetValue("ProxyOverride", "<local>;localhost;127.0.0.1", RegistryValueKind.String);
                            if (reg.GetValue("AutoConfigURL") != null)
                            {
                                reg.DeleteValue("AutoConfigURL", false);
                            }
                        }
                        else
                        {
                            reg.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                        }
                    }
                }

                // Notify Windows subsystems and open browsers
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error configuring Windows proxy: " + ex.Message);
            }
        }

        public static void SetAutoConfigPac(bool enable, string pacUrl)
        {
            try
            {
                using (RegistryKey reg = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, true))
                {
                    if (reg != null)
                    {
                        if (enable)
                        {
                            // Use PAC-only mode so non-blocked sites & Antigravity IDE VPN stay DIRECT
                            reg.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                            reg.SetValue("AutoConfigURL", pacUrl, RegistryValueKind.String);
                        }
                        else
                        {
                            if (reg.GetValue("AutoConfigURL") != null)
                            {
                                reg.DeleteValue("AutoConfigURL", false);
                            }
                            reg.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                        }
                    }
                }

                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error configuring Windows PAC: " + ex.Message);
            }
        }

        public static bool IsPacEnabled()
        {
            try
            {
                using (RegistryKey reg = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, false))
                {
                    if (reg != null)
                    {
                        object val = reg.GetValue("AutoConfigURL");
                        return val != null && val.ToString().Contains("127.0.0.1");
                    }
                }
            }
            catch { }
            return false;
        }

        public static bool IsSystemProxyEnabled()
        {
            try
            {
                using (RegistryKey reg = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, false))
                {
                    if (reg != null)
                    {
                        object val = reg.GetValue("ProxyEnable");
                        return val != null && Convert.ToInt32(val) == 1;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        public static string GetCurrentProxyServer()
        {
            try
            {
                using (RegistryKey reg = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, false))
                {
                    if (reg != null)
                    {
                        object val = reg.GetValue("ProxyServer");
                        return val != null ? val.ToString() : string.Empty;
                    }
                }
            }
            catch
            {
            }
            return string.Empty;
        }
    }
}

