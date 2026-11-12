using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Drawing;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Forms = System.Windows.Forms;

namespace DpiTunnel
{
    public class Program
    {
        private static AppSettings _settings;
        private static EngineProcess _engine;
        private static ActivityMonitor _monitor;
        private static Forms.NotifyIcon _notifyIcon;
        private static Window _mainWindow;
        private static Mutex _appMutex;
        private static bool _isExplicitExit = false;
        private static volatile bool _isStarting = false;
        private static int _startUiSeq = 0;

        // UI Controls
        private static TabControl _mainTabs;
        private static Button _btnToggle;
        private static TextBlock _txtMainStatus;
        private static TextBlock _txtMainSubStatus;
        private static Border _badgeStatus;
        private static Ellipse _dotStatus;
        private static TextBlock _txtBadgeStatus;
        private static TextBlock _txtFooterStatus;
        private static ComboBox _cmbEngine;
        private static ComboBox _cmbPreset;
        private static ComboBox _cmbMainPreset;
        private static TextBox _txtSitesEditor;
        private static TextBox _txtLogs;
        private static CheckBox _chkMinimizeToTray;
        private static CheckBox _chkAutoStart;
        private static TextBox _txtTgLink;
        private static TextBox _txtQuickAddSite;
        private static Button _btnQuickAddSubmit;

        // Diagnostic resource indicators
        private static readonly Dictionary<string, KeyValuePair<Ellipse, TextBlock>> _resourceIndicators =
            new Dictionary<string, KeyValuePair<Ellipse, TextBlock>>(StringComparer.OrdinalIgnoreCase);
        private static TextBlock _txtResourcesSummary;
        private static Button _btnRunCheck;
        private static int _diagRunSeq = 0;

        private const string TG_PRIMARY = "tg://proxy?server=156.239.251.46&port=443&secret=eeff2a510da3958357083ec111f4de115f7777772e6d6963726f736f66742e636f6d";
        private const string TG_BACKUP1 = "tg://proxy?server=89.108.71.59&port=443&secret=ee00d85706e58f9b5ead3b6c8a08d984df636c6f75642e6d61696c2e7275";
        private const string TG_BACKUP2 = "tg://proxy?server=77.mishutkin.click&port=443&secret=eec8017bc0cd6ea4b51ca268fb7b3aba6237372e6d69736875746b696e2e636c69636b";
        private const string TG_LOCAL_SOCKS = "tg://socks?server=127.0.0.1&port=1080";

        [STAThread]
        public static void Main(string[] args)
        {
            bool createdNew;
            _appMutex = new Mutex(true, "Global\\DpiTunnelSingleInstanceMutex", out createdNew);
            if (!createdNew)
            {
                Forms.MessageBox.Show("DPI Tunnel уже запущен! Проверьте системный трей (возле часов).", "DPI Tunnel", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
                return;
            }

            try
            {
                Application app = new Application();
                app.DispatcherUnhandledException += (s, exArgs) =>
                {
                    LogStartupError("DispatcherUnhandledException", exArgs.Exception);
                    Forms.MessageBox.Show("Ошибка интерфейса:\n" + exArgs.Exception.Message, "DPI Tunnel", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
                    exArgs.Handled = true;
                };
                AppDomain.CurrentDomain.UnhandledException += (s, exArgs) =>
                {
                    LogStartupError("UnhandledException", exArgs.ExceptionObject as Exception);
                };

                _settings = AppSettings.Load();
                RuntimeLogger.InitSession(string.Format("DPI Tunnel | Admin={0} | Engine={1} | Preset={2}",
                    EngineProcess.IsAdministrator(), _settings.Engine, _settings.Preset));
                LogNetworkSnapshot();

                _engine = new EngineProcess();
                _monitor = new ActivityMonitor();

                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string xamlPath = System.IO.Path.Combine(baseDir, @"src\MainWindow.xaml");
                string xamlContent = "";

                if (File.Exists(xamlPath))
                {
                    xamlContent = File.ReadAllText(xamlPath, Encoding.UTF8);
                }
                else
                {
                    Forms.MessageBox.Show("Не найден файл интерфейса: " + xamlPath, "Ошибка", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
                    return;
                }

                _mainWindow = (Window)XamlReader.Parse(xamlContent);

                string iconPath = System.IO.Path.Combine(baseDir, @"config\mirage.ico");
                if (!File.Exists(iconPath))
                {
                    iconPath = System.IO.Path.Combine(baseDir, @"config\app.ico");
                }
                if (File.Exists(iconPath))
                {
                    _mainWindow.Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri(iconPath));
                }

                InitializeControls();
                SetupTrayIcon(iconPath);
                HookEvents();
                LoadSettingsToUI();

                AppDomain.CurrentDomain.ProcessExit += delegate
                {
                    Cleanup();
                };

                // Start engine after the window is loaded so the UI appears immediately and templates are ready
                _mainWindow.Loaded += delegate
                {
                    StartEngine();
                };

                _mainWindow.Show();
                app.Run(_mainWindow);
            }
            catch (Exception ex)
            {
                LogStartupError("Main", ex);
                Forms.MessageBox.Show("Ошибка запуска DPI Tunnel:\n" + ex.Message, "DPI Tunnel — Ошибка", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
            }
            finally
            {
                Cleanup();
                if (_appMutex != null)
                {
                    try { _appMutex.ReleaseMutex(); } catch { }
                    _appMutex.Dispose();
                }
            }
        }

        private static void LogStartupError(string context, Exception ex)
        {
            try
            {
                string detail = ex != null ? ex.ToString() : "Unknown error";
                RuntimeLogger.Write("ERROR", context + ": " + detail);
                string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup_error.log");
                string msg = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}: {2}{3}", DateTime.Now, context, detail, Environment.NewLine);
                File.AppendAllText(logPath, msg, Encoding.UTF8);
            }
            catch { }
        }

        private static void LogNetworkSnapshot()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    string ipStr = "";
                    try
                    {
                        foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                ipStr += (ipStr.Length > 0 ? "," : "") + ua.Address.ToString();
                            }
                        }
                    }
                    catch { }
                    RuntimeLogger.Write("NET", string.Format("NIC '{0}' ({1}) type={2} ip=[{3}]", ni.Name, ni.Description, ni.NetworkInterfaceType, ipStr));
                }
            }
            catch { }
        }

        private static void InitializeControls()
        {
            _mainTabs = (TabControl)_mainWindow.FindName("MainTabs");
            _btnToggle = (Button)_mainWindow.FindName("BtnToggle");
            _txtMainStatus = (TextBlock)_mainWindow.FindName("TxtMainStatus");
            _txtMainSubStatus = (TextBlock)_mainWindow.FindName("TxtMainSubStatus");
            _badgeStatus = (Border)_mainWindow.FindName("BadgeStatus");
            _dotStatus = (Ellipse)_mainWindow.FindName("DotStatus");
            _txtBadgeStatus = (TextBlock)_mainWindow.FindName("TxtBadgeStatus");
            _txtFooterStatus = (TextBlock)_mainWindow.FindName("TxtFooterStatus");
            _cmbEngine = (ComboBox)_mainWindow.FindName("CmbEngine");
            _cmbPreset = (ComboBox)_mainWindow.FindName("CmbPreset");
            _cmbMainPreset = (ComboBox)_mainWindow.FindName("CmbMainPreset");
            _txtSitesEditor = (TextBox)_mainWindow.FindName("TxtSitesEditor");
            _txtLogs = (TextBox)_mainWindow.FindName("TxtLogs");
            _chkMinimizeToTray = (CheckBox)_mainWindow.FindName("ChkMinimizeToTray");
            _chkAutoStart = (CheckBox)_mainWindow.FindName("ChkAutoStart");
            _txtTgLink = (TextBox)_mainWindow.FindName("TxtTgLink");
            _txtQuickAddSite = (TextBox)_mainWindow.FindName("TxtQuickAddSite");
            _btnQuickAddSubmit = (Button)_mainWindow.FindName("BtnQuickAddSubmit");

            _txtResourcesSummary = (TextBlock)_mainWindow.FindName("TxtResourcesSummary");
            _btnRunCheck = (Button)_mainWindow.FindName("BtnRunCheck");

            RegisterIndicator("YouTube", "DotCheckYouTube", "TxtCheckYouTube");
            RegisterIndicator("Instagram", "DotCheckInstagram", "TxtCheckInstagram");
            RegisterIndicator("Discord", "DotCheckDiscord", "TxtCheckDiscord");
            RegisterIndicator("Telegram", "DotCheckTelegram", "TxtCheckTelegram");
            RegisterIndicator("RuTracker", "DotCheckRuTracker", "TxtCheckRuTracker");
            RegisterIndicator("Rutor.info", "DotCheckRutor", "TxtCheckRutor");
            RegisterIndicator("X (Twitter)", "DotCheckX", "TxtCheckX");
            RegisterIndicator("Medium", "DotCheckMedium", "TxtCheckMedium");
        }

        private static void RegisterIndicator(string resourceName, string dotName, string textName)
        {
            Ellipse dot = _mainWindow.FindName(dotName) as Ellipse;
            TextBlock txt = _mainWindow.FindName(textName) as TextBlock;
            if (dot != null && txt != null)
            {
                _resourceIndicators[resourceName] = new KeyValuePair<Ellipse, TextBlock>(dot, txt);
            }
        }

        private static void SetupTrayIcon(string iconPath)
        {
            _notifyIcon = new Forms.NotifyIcon();
            if (File.Exists(iconPath))
            {
                _notifyIcon.Icon = new Icon(iconPath);
            }
            else
            {
                _notifyIcon.Icon = SystemIcons.Application;
            }

            _notifyIcon.Text = "Mirage — Свободный доступ";
            _notifyIcon.Visible = true;

            Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
            Forms.ToolStripMenuItem itemOpen = new Forms.ToolStripMenuItem("Открыть окно");
            itemOpen.Click += delegate { ShowMainWindow(); };
            menu.Items.Add(itemOpen);

            Forms.ToolStripMenuItem itemToggle = new Forms.ToolStripMenuItem("Включить / Выключить обход");
            itemToggle.Click += delegate { ToggleEngine(); };
            menu.Items.Add(itemToggle);

            Forms.ToolStripMenuItem itemTg = new Forms.ToolStripMenuItem("Подключить Telegram");
            itemTg.Click += delegate { OpenTgUrl(TG_PRIMARY); };
            menu.Items.Add(itemTg);

            menu.Items.Add(new Forms.ToolStripSeparator());

            Forms.ToolStripMenuItem itemExit = new Forms.ToolStripMenuItem("Выход");
            itemExit.Click += delegate
            {
                _isExplicitExit = true;
                _mainWindow.Close();
            };
            menu.Items.Add(itemExit);

            _notifyIcon.ContextMenuStrip = menu;
            _notifyIcon.DoubleClick += delegate { ShowMainWindow(); };
        }

        private static void ShowMainWindow()
        {
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }

        private static void HookEvents()
        {
            _mainWindow.Closing += (s, e) =>
            {
                if (!_isExplicitExit && _chkMinimizeToTray != null && _chkMinimizeToTray.IsChecked == true)
                {
                    e.Cancel = true;
                    _mainWindow.Hide();
                    _notifyIcon.ShowBalloonTip(2000, "DPI Tunnel", "Приложение свернуто в трей (возле часов).", Forms.ToolTipIcon.Info);
                }
            };

            _btnToggle.Click += delegate { ToggleEngine(); };

            // Real-time activity monitor logging
            _monitor.OnSiteActivity += delegate(string site, string details)
            {
                AppendLog(string.Format("[OK] {0} — {1}", site, details));
            };

            _engine.OnLog += delegate(string line)
            {
                // Show important status, warning, and error lines
                if (line.Contains("Запуск") || line.Contains("завершился") || line.Contains("активирован") ||
                    line.Contains("Локальный") || line.Contains("Обнаружен") ||
                    line.Contains("ВНИМАНИЕ") || line.Contains("ОШИБКА"))
                {
                    AppendLog("[DPI] " + line);
                }
            };

            _engine.OnSiteProxied += delegate(string host)
            {
                if (_monitor != null)
                {
                    _monitor.ReportSite(host, "Разблокирован (PAC + Яндекс DNS 77.88.8.8)");
                }
            };

            _engine.OnStateChanged += delegate(bool running)
            {
                _mainWindow.Dispatcher.BeginInvoke(new Action(delegate
                {
                    UpdateUIState(running);
                }));
            };

            if (_btnRunCheck != null)
            {
                _btnRunCheck.Click += delegate { RunDiagnostics(); };
            }

            if (_cmbMainPreset != null)
            {
                _cmbMainPreset.SelectionChanged += delegate
                {
                    if (_cmbMainPreset.SelectedItem != null)
                    {
                        string newPreset = ((ComboBoxItem)_cmbMainPreset.SelectedItem).Content.ToString();
                        if (_settings.Preset != newPreset)
                        {
                            _settings.Preset = newPreset;
                            _settings.Save();
                            if (_engine.IsRunning || _isStarting)
                            {
                                AppendLog("Переключение режима на: " + _settings.Preset);
                                StartEngine();
                            }
                        }
                    }
                };
            }

            // Quick Add Site Button
            if (_btnQuickAddSubmit != null && _txtQuickAddSite != null)
            {
                _btnQuickAddSubmit.Click += delegate
                {
                    string site = _txtQuickAddSite.Text.Trim().ToLower();
                    site = site.Replace("https://", "").Replace("http://", "").TrimEnd('/');
                    if (string.IsNullOrEmpty(site)) return;

                    AppendSite(site);
                    _txtQuickAddSite.Clear();
                    AppendLog("➕ Добавлен в обход: " + site);
                    MessageBox.Show("Сайт " + site + " добавлен в список обхода!", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
                };
            }

            // Telegram card on Main tab - click switches to Telegram tab
            Border cardTg = (Border)_mainWindow.FindName("CardTelegram");
            if (cardTg != null)
            {
                cardTg.MouseLeftButtonUp += delegate
                {
                    if (_mainTabs != null) _mainTabs.SelectedIndex = 2;
                };
            }

            // Telegram tab buttons
            Button btnTgPrimary = (Button)_mainWindow.FindName("BtnTgPrimary");
            if (btnTgPrimary != null)
            {
                btnTgPrimary.Click += delegate
                {
                    if (_txtTgLink != null) _txtTgLink.Text = TG_PRIMARY;
                    OpenTgUrl(TG_PRIMARY);
                };
            }

            Button btnTgBackup1 = (Button)_mainWindow.FindName("BtnTgBackup1");
            if (btnTgBackup1 != null)
            {
                btnTgBackup1.Click += delegate
                {
                    if (_txtTgLink != null) _txtTgLink.Text = TG_BACKUP1;
                    OpenTgUrl(TG_BACKUP1);
                };
            }

            Button btnTgBackup2 = (Button)_mainWindow.FindName("BtnTgBackup2");
            if (btnTgBackup2 != null)
            {
                btnTgBackup2.Click += delegate
                {
                    if (_txtTgLink != null) _txtTgLink.Text = TG_BACKUP2;
                    OpenTgUrl(TG_BACKUP2);
                };
            }

            Button btnTgLocal = (Button)_mainWindow.FindName("BtnTgLocalSocks");
            if (btnTgLocal != null)
            {
                btnTgLocal.Click += delegate
                {
                    if (_txtTgLink != null) _txtTgLink.Text = TG_LOCAL_SOCKS;
                    OpenTgUrl(TG_LOCAL_SOCKS);
                };
            }

            Button btnCopyTg = (Button)_mainWindow.FindName("BtnCopyTgLink");
            if (btnCopyTg != null)
            {
                btnCopyTg.Click += delegate
                {
                    if (_txtTgLink != null) Clipboard.SetText(_txtTgLink.Text);
                    MessageBox.Show("Ссылка на прокси скопирована в буфер обмена!", "Telegram", MessageBoxButton.OK, MessageBoxImage.Information);
                };
            }

            // Sites save & reset
            Button btnSaveSites = (Button)_mainWindow.FindName("BtnSaveSites");
            if (btnSaveSites != null) btnSaveSites.Click += delegate { SaveSites(); };

            Button btnResetSites = (Button)_mainWindow.FindName("BtnResetSites");
            if (btnResetSites != null) btnResetSites.Click += delegate { ResetSites(); };

            // Settings
            Button btnSaveSettings = (Button)_mainWindow.FindName("BtnSaveSettings");
            if (btnSaveSettings != null)
            {
                btnSaveSettings.Click += delegate
                {
                    SaveSettingsFromUI();
                    MessageBox.Show("Настройки сохранены!", "DPI Tunnel", MessageBoxButton.OK, MessageBoxImage.Information);
                };
            }

            // Logs
            Button btnClearLogs = (Button)_mainWindow.FindName("BtnClearLogs");
            if (btnClearLogs != null)
            {
                btnClearLogs.Click += delegate { if (_txtLogs != null) _txtLogs.Clear(); };
            }
        }

        private static void AppendLog(string message)
        {
            RuntimeLogger.Write("UI", message);
            if (_mainWindow == null || _txtLogs == null) return;
            _mainWindow.Dispatcher.BeginInvoke(new Action(delegate
            {
                string line = string.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, message);
                _txtLogs.AppendText(line + Environment.NewLine);
                _txtLogs.ScrollToEnd();
            }));
        }

        private static void LoadSettingsToUI()
        {
            if (_chkMinimizeToTray != null) _chkMinimizeToTray.IsChecked = _settings.MinimizeToTray;
            if (_chkAutoStart != null) _chkAutoStart.IsChecked = _settings.AutoStartWithWindows;

            if (_cmbEngine != null)
            {
                foreach (ComboBoxItem item in _cmbEngine.Items)
                {
                    if (item.Content.ToString().IndexOf(_settings.Engine, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _cmbEngine.SelectedItem = item;
                        break;
                    }
                }
            }

            if (_cmbMainPreset != null)
            {
                foreach (ComboBoxItem item in _cmbMainPreset.Items)
                {
                    if (item.Content.ToString().Equals(_settings.Preset, StringComparison.OrdinalIgnoreCase))
                    {
                        _cmbMainPreset.SelectedItem = item;
                        break;
                    }
                }
            }

            if (_cmbPreset != null)
            {
                foreach (ComboBoxItem item in _cmbPreset.Items)
                {
                    if (item.Content.ToString().Equals(_settings.Preset, StringComparison.OrdinalIgnoreCase))
                    {
                        _cmbPreset.SelectedItem = item;
                        break;
                    }
                }
            }

            // Load Sites
            string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"config\sites.txt");
            if (File.Exists(path) && _txtSitesEditor != null)
            {
                _txtSitesEditor.Text = File.ReadAllText(path, Encoding.UTF8);
            }
        }

        private static void SaveSettingsFromUI()
        {
            if (_cmbEngine != null && _cmbEngine.SelectedItem != null)
            {
                string sel = ((ComboBoxItem)_cmbEngine.SelectedItem).Content.ToString();
                _settings.Engine = sel.Contains("GoodbyeDPI") ? "GoodbyeDPI" : "ByeDPI";
            }
            if (_cmbMainPreset != null && _cmbMainPreset.SelectedItem != null)
            {
                _settings.Preset = ((ComboBoxItem)_cmbMainPreset.SelectedItem).Content.ToString();
            }
            else if (_cmbPreset != null && _cmbPreset.SelectedItem != null)
            {
                _settings.Preset = ((ComboBoxItem)_cmbPreset.SelectedItem).Content.ToString();
            }

            if (_chkMinimizeToTray != null) _settings.MinimizeToTray = _chkMinimizeToTray.IsChecked == true;
            if (_chkAutoStart != null) _settings.AutoStartWithWindows = _chkAutoStart.IsChecked == true;

            _settings.Save();
        }

        private static void SaveSites()
        {
            string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"config\sites.txt");
            if (_txtSitesEditor != null)
            {
                File.WriteAllText(path, _txtSitesEditor.Text, Encoding.UTF8);
            }
            if (_engine != null) _engine.RefreshPac();
            MessageBox.Show("Список сайтов сохранён и сразу применён!", "Сайты", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static void ResetSites()
        {
            string defaultSites = "# Список доменов для избирательного обхода DPI\r\nyoutube.com\r\ngooglevideo.com\r\nytimg.com\r\nyoutu.be\r\ndiscord.com\r\ndiscordapp.com\r\ndiscord.gg\r\nt.me\r\ntelegram.org\r\ngithub.com\r\nrutor.info\r\nrutor.is\r\nrutracker.org\r\nnnmclub.to\r\ninstagram.com\r\ntwitter.com\r\nx.com";
            if (_txtSitesEditor != null) _txtSitesEditor.Text = defaultSites;
            SaveSites();
        }

        private static void AppendSite(string domain)
        {
            string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"config\sites.txt");
            if (File.Exists(path))
            {
                string current = File.ReadAllText(path, Encoding.UTF8);
                if (!current.Contains(domain))
                {
                    File.AppendAllText(path, Environment.NewLine + domain, Encoding.UTF8);
                }
            }
            if (_txtSitesEditor != null && !_txtSitesEditor.Text.Contains(domain))
            {
                _txtSitesEditor.AppendText(Environment.NewLine + domain);
            }
            if (_engine != null) _engine.RefreshPac();
        }

        private static void ToggleEngine()
        {
            if (_engine.IsRunning || _isStarting)
            {
                StopEngine();
            }
            else
            {
                StartEngine();
            }
        }

        private static void StartEngine()
        {
            SaveSettingsFromUI();
            int seq = System.Threading.Interlocked.Increment(ref _startUiSeq);
            _isStarting = true;

            AppSettings snap = new AppSettings();
            snap.Engine = _settings.Engine;
            snap.Preset = _settings.Preset;
            snap.Port = _settings.Port;
            snap.AutoEnableSystemProxy = _settings.AutoEnableSystemProxy;
            snap.MinimizeToTray = _settings.MinimizeToTray;
            snap.AutoStartWithWindows = _settings.AutoStartWithWindows;
            snap.GlobalMode = _settings.GlobalMode;
            snap.CustomArgs = _settings.CustomArgs;

            SetStartingUIState();

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = _engine.Start(snap);
                _mainWindow.Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (System.Threading.Thread.VolatileRead(ref _startUiSeq) != seq)
                    {
                        return;
                    }
                    _isStarting = false;
                    UpdateUIState(ok);
                    if (ok)
                    {
                        _monitor.Start();
                        AppendLog("[OK] Обход активен (" + _engine.ActiveStrategyLabel + ")");
                        _notifyIcon.ShowBalloonTip(2000, "DPI Tunnel", "Обход блокировок активирован!", Forms.ToolTipIcon.Info);
                        RunDiagnostics();
                    }
                }));
            });
        }

        private static void StopEngine()
        {
            System.Threading.Interlocked.Increment(ref _startUiSeq);
            _isStarting = false;
            _monitor.Stop();
            _engine.Stop();
            AppendLog("[STOP] Обход выключен. Трафик идёт напрямую.");
            _notifyIcon.ShowBalloonTip(2000, "DPI Tunnel", "Обход блокировок выключен.", Forms.ToolTipIcon.Info);
        }

        private static void SetStartingUIState()
        {
            SolidColorBrush amberBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11));
            SolidColorBrush subBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 152, 176));
            SolidColorBrush redBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));

            _txtMainStatus.Text = "Подключение и подбор профиля";
            _txtMainStatus.Foreground = amberBrush;
            _txtMainSubStatus.Text = "Тестируем Zapret, GoodbyeDPI и ByeDPI через Яндекс DNS (1253)...";
            _txtMainSubStatus.Foreground = subBrush;
            _dotStatus.Fill = amberBrush;
            _txtBadgeStatus.Text = "Подбор...";
            _txtFooterStatus.Text = "Подбор оптимальной стратегии обхода...";

            var border = (Border)_btnToggle.Template.FindName("Border", _btnToggle);
            if (border != null) border.Background = redBrush;
            var btnIcon = (TextBlock)_btnToggle.Template.FindName("BtnIcon", _btnToggle);
            if (btnIcon != null) btnIcon.Text = "■";
            var btnLabel = (TextBlock)_btnToggle.Template.FindName("BtnLabel", _btnToggle);
            if (btnLabel != null) btnLabel.Text = "ОСТАНОВИТЬ";
        }

        private static void UpdateUIState(bool running)
        {
            if (_isStarting) return;

            SolidColorBrush greenBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 197, 94));
            SolidColorBrush redBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
            SolidColorBrush blueBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 130, 246));
            SolidColorBrush whiteBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(241, 245, 249));
            SolidColorBrush subBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 152, 176));

            if (running)
            {
                string strat = _engine != null ? _engine.ActiveStrategyLabel : "ByeDPI";
                string conflictVpn = HostsHelper.GetConflictingVpnAdapter();
                if (!string.IsNullOrEmpty(conflictVpn) && _engine != null && _engine.IsSmartProxyActive)
                {
                    _txtMainStatus.Text = "Обход активен (совместно с VPN)";
                    _txtMainStatus.Foreground = greenBrush;
                    _txtMainSubStatus.Text = strat + " • PAC 127.0.0.1:1085 • Яндекс DNS";
                    _txtMainSubStatus.Foreground = subBrush;
                    _dotStatus.Fill = greenBrush;
                    _txtBadgeStatus.Text = "Активно • PAC";
                    _txtFooterStatus.Text = "PAC 127.0.0.1:1085 • " + strat;
                }
                else
                {
                    _txtMainStatus.Text = "Обход активен";
                    _txtMainStatus.Foreground = greenBrush;
                    _txtMainSubStatus.Text = strat + " • PAC 127.0.0.1:1085 • Яндекс DNS";
                    _txtMainSubStatus.Foreground = subBrush;
                    _dotStatus.Fill = greenBrush;
                    _txtBadgeStatus.Text = "Активно";
                    _txtFooterStatus.Text = "PAC 127.0.0.1:1085 • " + strat;
                }

                var border = (Border)_btnToggle.Template.FindName("Border", _btnToggle);
                if (border != null) border.Background = redBrush;
                var btnIcon = (TextBlock)_btnToggle.Template.FindName("BtnIcon", _btnToggle);
                if (btnIcon != null) btnIcon.Text = "■";
                var btnLabel = (TextBlock)_btnToggle.Template.FindName("BtnLabel", _btnToggle);
                if (btnLabel != null) btnLabel.Text = "ОСТАНОВИТЬ";
            }
            else
            {
                _txtMainStatus.Text = "Обход DPI выключен";
                _txtMainStatus.Foreground = whiteBrush;
                _txtMainSubStatus.Text = "Нажмите «Включить», чтобы обойти блокировки";
                _txtMainSubStatus.Foreground = subBrush;
                _dotStatus.Fill = redBrush;
                _txtBadgeStatus.Text = "Отключено";

                var border = (Border)_btnToggle.Template.FindName("Border", _btnToggle);
                if (border != null) border.Background = blueBrush;
                var btnIcon = (TextBlock)_btnToggle.Template.FindName("BtnIcon", _btnToggle);
                if (btnIcon != null) btnIcon.Text = "▶";
                var btnLabel = (TextBlock)_btnToggle.Template.FindName("BtnLabel", _btnToggle);
                if (btnLabel != null) btnLabel.Text = "ВКЛЮЧИТЬ ОБХОД";

                _txtFooterStatus.Text = "Остановлено";
            }
        }

        private static void OpenTgUrl(string url)
        {
            try
            {
                AppendLog("[TG] Открытие ссылки MTProxy: " + url);
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppendLog("[ERR] Ошибка открытия Telegram: " + ex.Message);
            }
        }

        private static void RunDiagnostics()
        {
            int runId = Interlocked.Increment(ref _diagRunSeq);
            _btnRunCheck.IsEnabled = false;
            _btnRunCheck.Content = "Проверка...";

            SolidColorBrush amberBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11));
            SolidColorBrush greenBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 197, 94));
            SolidColorBrush redBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
            SolidColorBrush cyanBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 189, 248));
            SolidColorBrush mutedBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 152, 176));

            foreach (KeyValuePair<string, KeyValuePair<Ellipse, TextBlock>> kv in _resourceIndicators)
            {
                kv.Value.Key.Fill = amberBrush;
                kv.Value.Value.Text = "...";
                kv.Value.Value.Foreground = mutedBrush;
            }
            int totalCount = Diagnostics.PopularResources.Length;
            int okCount = 0;
            if (_txtResourcesSummary != null)
            {
                _txtResourcesSummary.Text = "Проверка...";
                _txtResourcesSummary.Foreground = amberBrush;
            }

            bool ytOk = false;
            bool rutorOk = false;

            Diagnostics.CheckAllAsync(
                delegate(CheckResult res)
                {
                    if (res.Name == "YouTube") ytOk = res.Success;
                    else if (res.Name == "Rutor.info") rutorOk = res.Success;

                    _mainWindow.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (Thread.VolatileRead(ref _diagRunSeq) != runId) return;

                        if (res.Success) okCount++;

                        KeyValuePair<Ellipse, TextBlock> ui;
                        if (_resourceIndicators.TryGetValue(res.Name, out ui))
                        {
                            ui.Value.Text = res.Message;
                            if (res.Success)
                            {
                                ui.Key.Fill = greenBrush;
                                ui.Value.Foreground = greenBrush;
                            }
                            else if (res.Name == "Telegram")
                            {
                                ui.Key.Fill = cyanBrush;
                                ui.Value.Foreground = cyanBrush;
                            }
                            else
                            {
                                ui.Key.Fill = redBrush;
                                ui.Value.Foreground = redBrush;
                            }
                        }

                        if (_txtResourcesSummary != null)
                        {
                            _txtResourcesSummary.Text = string.Format("{0} / {1}", okCount, totalCount);
                            _txtResourcesSummary.Foreground = okCount == totalCount ? greenBrush : (okCount > 0 ? mutedBrush : redBrush);
                        }

                        if (res.Success)
                        {
                            AppendLog(string.Format("[OK] {0}: доступен ({1} мс)", res.Name, res.ElapsedMs));
                        }
                        else
                        {
                            AppendLog(string.Format("[FAIL] {0}: {1}", res.Name, res.Message));
                        }
                    }));
                },
                delegate
                {
                    _mainWindow.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (Thread.VolatileRead(ref _diagRunSeq) != runId) return;

                        _btnRunCheck.IsEnabled = true;
                        _btnRunCheck.Content = "Проверить";

                        if (_txtResourcesSummary != null)
                        {
                            _txtResourcesSummary.Text = string.Format("{0} / {1} онлайн", okCount, totalCount);
                            _txtResourcesSummary.Foreground = okCount >= totalCount - 1 ? greenBrush : (okCount > 0 ? amberBrush : redBrush);
                        }

                        if (_engine != null && _engine.IsRunning && !ytOk && rutorOk)
                        {
                            _txtMainStatus.Text = "Сайты работают • YouTube ограничен";
                            _txtMainStatus.Foreground = amberBrush;
                            _txtMainSubStatus.Text = "Оператор фильтрует SNI youtube.com; для YouTube может потребоваться VPN";
                            _txtMainSubStatus.Foreground = amberBrush;
                            _dotStatus.Fill = amberBrush;
                            _txtBadgeStatus.Text = "Частично";
                            AppendLog("[WARN] Сайты разблокированы, но оператор продолжает блокировать SNI youtube.com.");
                        }
                        else if (_engine != null && _engine.IsRunning && !ytOk && !rutorOk)
                        {
                            string conflictVpn = HostsHelper.GetConflictingVpnAdapter();
                            if (!string.IsNullOrEmpty(conflictVpn))
                            {
                                _txtMainStatus.Text = "Конфликт с VPN-адаптером (" + conflictVpn + ")";
                                _txtMainStatus.Foreground = amberBrush;
                                _txtMainSubStatus.Text = "Сторонний TUN перехватывает сокеты. Отключите его TUN-режим.";
                                _txtMainSubStatus.Foreground = amberBrush;
                                _dotStatus.Fill = amberBrush;
                                _txtBadgeStatus.Text = "Конфликт TUN";
                                AppendLog("[WARN] Обнаружен конфликт с TUN-адаптером VPN (" + conflictVpn + ").");
                            }
                        }
                    }));
                },
                (_engine != null && _engine.IsSmartProxyActive)
            );
        }

        private static void Cleanup()
        {
            if (_monitor != null) _monitor.Stop();
            if (_engine != null) _engine.Stop();
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
        }
    }
}
