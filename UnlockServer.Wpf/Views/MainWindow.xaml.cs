using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using UnlockServer.Services;
using UnlockServer.ViewModels;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace UnlockServer.Views
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private NotifyIcon _notifyIcon;
        private ContextMenuStrip _trayMenu;
        private bool _isExiting;
        private bool _rebuilding;
        private bool _trayTipShown;
        private bool _suppressAppearance;

        public MainWindow()
        {
            InitializeComponent();

            // 毛玻璃主题需要「不透明窗口 + DWM 亚克力」，必须在窗口显示前决定
            // （放在 InitializeComponent 之后，否则会被 XAML 里的 AllowsTransparency="True" 覆盖）
            ThemeService.ApplyWindowShell(this);

            WanClient.reloadConfig();

            _viewModel = new MainViewModel();
            DataContext = _viewModel;

            // 订阅 ViewModel 事件
            _viewModel.OnRequestSearchDevice += ViewModel_OnRequestSearchDevice;
            _viewModel.OnRequestShowWindow += ViewModel_OnRequestShowWindow;
            _viewModel.OnRequestExit += ViewModel_OnRequestExit;

            // 初始化系统托盘
            InitializeNotifyIcon();
            LangService.LanguageChanged += OnLanguageChanged;

            // 加载密码到 PasswordBox
            PasswordBox.Password = _viewModel.Settings.Password;

            SourceInitialized += (s, e) =>
            {
                // 修复无边框窗口的 Win32 样式，否则整个窗口命中测试返回 HTNOWHERE，
                // 鼠标事件无法送达 WPF，表现为窗口拖不动、按钮点不动
                ThemeService.FixBorderlessWindowStyle(this);
                ThemeService.ApplyDwmBackdrop(this);
            };
            Loaded += MainWindow_Loaded;
            Closed += (s, e) => LangService.LanguageChanged -= OnLanguageChanged;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            SyncAppearanceRadios();
            _viewModel.Initialize();

            // 如果是隐藏启动，直接最小化到托盘
            if (App.IsHideRun)
            {
                Hide();
            }

            if (!string.IsNullOrEmpty(App.ToastDebugShot))
            {
                var text = string.IsNullOrEmpty(App.ToastDebugText)
                    ? LangService.T("T.Msg.AddedNeedPassword")
                    : App.ToastDebugText;
                Dispatcher.BeginInvoke(new Action(() => ToastService.Show(text, 30)),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                return;
            }

            if (!string.IsNullOrEmpty(App.UiDebugShot))
            {
                Dispatcher.BeginInvoke(new Action(() => SaveDebugShot(App.UiDebugShot)),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }

        /// <summary>调试：把当前窗口渲染成 PNG 保存后退出，用于无人值守的界面检查。</summary>
        private void SaveDebugShot(string path)
        {
            try
            {
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
                int w = (int)Math.Ceiling(ActualWidth * dpi.PixelsPerDip);
                int h = (int)Math.Ceiling(ActualHeight * dpi.PixelsPerDip);
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    w, h, 96 * dpi.PixelsPerDip, 96 * dpi.PixelsPerDip,
                    System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(this);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using (var fs = System.IO.File.Create(path)) enc.Save(fs);
            }
            catch (Exception ex)
            {
                try { System.IO.File.WriteAllText(path + ".err.txt", ex.ToString()); } catch { }
            }
            finally
            {
                Application.Current.Shutdown();
            }
        }

        #region 外观（主题 / 语言）

        /// <summary>把界面上四个主题按钮、两个语言按钮对齐到当前设置</summary>
        private void SyncAppearanceRadios()
        {
            _suppressAppearance = true;
            try
            {
                var theme = ThemeService.Name(ThemeService.Current);
                ThemeDarkRadio.IsChecked = theme == "dark";
                ThemeLightRadio.IsChecked = theme == "light";
                ThemeSystemRadio.IsChecked = theme == "system";
                ThemeAcrylicRadio.IsChecked = theme == "acrylic";
                ThemeAcrylicRadio.IsEnabled = ThemeService.AcrylicSupported;

                var lang = LangService.Current;
                LangZhRadio.IsChecked = lang == LangService.Zh;
                LangEnRadio.IsChecked = lang == LangService.En;
            }
            finally
            {
                _suppressAppearance = false;
            }
        }

        private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressAppearance || _viewModel?.Settings == null) return;
            var name = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrEmpty(name)) return;

            var wasAcrylic = ThemeService.IsAcrylic;
            _viewModel.Settings.Theme = name;
            _viewModel.SaveAppearance();
            ThemeService.Apply(name);

            // 毛玻璃与普通窗口的窗口样式不同，无法原地切换 → 重建主窗口
            if (ThemeService.IsAcrylic != wasAcrylic)
            {
                var tab = _viewModel.SelectedTab;
                Dispatcher.BeginInvoke(new Action(() => App.RebuildMainWindow(tab)));
            }
        }

        private void LangRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressAppearance || _viewModel?.Settings == null) return;
            var lang = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrEmpty(lang)) return;

            _viewModel.Settings.Language = lang;
            _viewModel.SaveAppearance();
            LangService.Apply(lang);
        }

        private void OnLanguageChanged()
        {
            Dispatcher.BeginInvoke(new Action(RebuildTrayMenu));
        }

        #endregion

        #region 托盘

        private void InitializeNotifyIcon()
        {
            _notifyIcon = new NotifyIcon
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(
                    System.Reflection.Assembly.GetExecutingAssembly().Location),
                Text = LangService.T("T.App.Title"),
                Visible = true
            };

            // 双击托盘图标显示窗口
            _notifyIcon.DoubleClick += (s, e) => ShowMainWindow();

            RebuildTrayMenu();
        }

        /// <summary>重建托盘右键菜单（语言切换后需要重建才会显示新语言）</summary>
        private void RebuildTrayMenu()
        {
            if (_notifyIcon == null) return;

            var menu = new ContextMenuStrip();

            var showItem = new ToolStripMenuItem(LangService.T("T.Tray.Show"));
            showItem.Click += (s, e) => ShowMainWindow();
            menu.Items.Add(showItem);

            var lockItem = new ToolStripMenuItem(LangService.T("T.Tray.Lock"));
            lockItem.Click += (s, e) => _viewModel.LockScreenCommand.Execute(null);
            menu.Items.Add(lockItem);

            menu.Items.Add(new ToolStripSeparator());

            var hideItem = new ToolStripMenuItem(LangService.T("T.Tray.Hide"));
            hideItem.Click += (s, e) => Hide();
            menu.Items.Add(hideItem);

            var exitItem = new ToolStripMenuItem(LangService.T("T.Tray.Exit"));
            exitItem.Click += (s, e) =>
            {
                if (MessageDialog.ShowConfirm(LangService.T("T.Msg.ExitConfirm")))
                {
                    ExitApplication();
                }
            };
            menu.Items.Add(exitItem);

            var old = _trayMenu;
            _trayMenu = menu;
            _notifyIcon.Text = LangService.T("T.Tray.Tip");
            _notifyIcon.ContextMenuStrip = menu;
            old?.Dispose();
        }

        private void HideToTray()
        {
            Hide();
            if (_trayTipShown) return;
            _trayTipShown = true;
            ToastService.Show(LangService.T("T.Toast.Tray"));
        }

        private void ShowMainWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
        }

        /// <summary>销毁托盘与定时器，但保留进程（用于切换主题后重建窗口）</summary>
        internal void ReleaseForRebuild()
        {
            _rebuilding = true;
            try { _viewModel.Cleanup(); } catch { }
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            _trayMenu?.Dispose();
            _trayMenu = null;
            Close();
        }

        #endregion

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_rebuilding)
            {
                // 正在换主题重建窗口，不拦截关闭、也不退出进程
                return;
            }

            if (_isExiting)
            {
                // 真正退出
                _viewModel.Cleanup();
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            else
            {
                e.Cancel = true;
                HideToTray();
            }
        }

        private void ExitApplication()
        {
            _isExiting = true;
            _viewModel.Cleanup();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            Application.Current.Shutdown();
        }

        #region 无边框窗口控制

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragDiag($"MouseLeftButtonDown  点击次数={e.ClickCount}  状态={e.ButtonState}  来源={(e.OriginalSource as FrameworkElement)?.GetType().Name}");

            if (e.ClickCount == 2)
            {
                // 双击标题栏不做任何事（无边框窗口不支持最大化）
                return;
            }

            if (e.ButtonState != MouseButtonState.Pressed) return;

            try
            {
                DragMove();
                DragDiag("DragMove 正常返回");
            }
            catch (InvalidOperationException ex)
            {
                DragDiag($"DragMove 抛异常: {ex.GetType().Name} {ex.Message}");
            }
            catch (Exception ex)
            {
                DragDiag($"DragMove 异常: {ex.GetType().Name} {ex.Message}");
            }
        }

        /// <summary>诊断用：把标题栏拖动相关事件写入 dragdiag.txt</summary>
        private static void DragDiag(string msg)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dragdiag.txt"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n");
            }
            catch { }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            HideToTray();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        #endregion

        #region ViewModel 事件处理

        private void ViewModel_OnRequestSearchDevice(object sender, int bluetoothType)
        {
            var deviceListWindow = new DeviceListWindow(bluetoothType, _viewModel.Settings.DeviceAddress)
            {
                Owner = this
            };

            if (deviceListWindow.ShowDialog() == true)
            {
                _viewModel.AddBoundDevice(deviceListWindow.SelectedAddress, deviceListWindow.SelectedBluetoothType);
            }
        }

        private void ViewModel_OnRequestShowWindow(object sender, EventArgs e)
        {
            ShowMainWindow();
        }

        private void ViewModel_OnRequestExit(object sender, EventArgs e)
        {
            ExitApplication();
        }

        #endregion

        #region UI 事件处理

        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_viewModel?.Settings != null)
            {
                _viewModel.Settings.Password = PasswordBox.Password;
            }
        }

        #endregion
    }
}
