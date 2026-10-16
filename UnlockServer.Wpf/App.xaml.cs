using System;
using System.Threading;
using System.Windows;
using UnlockServer.Services;

namespace UnlockServer
{
    public partial class App : Application
    {
        private static Mutex _mutex;
        public static bool IsHideRun { get; private set; }

        /// <summary>调试用：启动时把主窗口渲染成 PNG 后退出（--ui-debug-shot=路径 --tab=N）</summary>
        public static string UiDebugShot { get; private set; }

        /// <summary>调试用：弹一条通知并渲染成 PNG 后退出（--toast-shot=路径 [--toast-text=文字]）</summary>
        public static string ToastDebugShot { get; private set; }
        public static string ToastDebugText { get; private set; }

        /// <summary>主窗口启动时默认选中的标签页（重建窗口时也用它保留当前位置）</summary>
        public static int PendingTab { get; set; }

        /// <summary>调试用：强制主题 / 语言（--theme=light --lang=en）</summary>
        public static string DebugTheme { get; private set; }
        public static string DebugLang { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            if (e.Args.Length > 0 &&
                string.Equals(e.Args[0], LocalUnlock.InstallArg, StringComparison.OrdinalIgnoreCase))
            {
                Environment.Exit(LocalUnlock.RunElevatedInstall());
                return;
            }

            if (e.Args.Length > 0 &&
                string.Equals(e.Args[0], LocalUnlock.UninstallArg, StringComparison.OrdinalIgnoreCase))
            {
                Environment.Exit(LocalUnlock.RunElevatedUninstall());
                return;
            }

            const string mutexName = "UnlockServer_SingleInstance_Mutex";
            _mutex = new Mutex(true, mutexName, out bool createdNew);

            if (!createdNew)
            {
                MessageBox.Show("程序已经在运行中！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                Current.Shutdown();
                return;
            }

            if (e.Args.Length > 0 && e.Args[0].ToLower() == "hide")
            {
                IsHideRun = true;
            }

            for (int i = 0; i < e.Args.Length; i++)
            {
                var a = e.Args[i];
                if (a.StartsWith("--ui-debug-shot=", StringComparison.Ordinal))
                    UiDebugShot = a.Substring(16);
                else if (a.StartsWith("--tab=", StringComparison.Ordinal) &&
                         int.TryParse(a.Substring(6), out int tab))
                {
                    PendingTab = tab;
                }
                else if (a.StartsWith("--theme=", StringComparison.Ordinal))
                    DebugTheme = a.Substring(8);
                else if (a.StartsWith("--lang=", StringComparison.Ordinal))
                    DebugLang = a.Substring(7);
                else if (a.StartsWith("--toast-shot=", StringComparison.Ordinal))
                    ToastDebugShot = a.Substring(13);
                else if (a.StartsWith("--toast-text=", StringComparison.Ordinal))
                    ToastDebugText = a.Substring(13);
            }

            // 先套用主题与语言，再让 StartupUri 建出主窗口，避免闪一下默认样式
            ApplyAppearanceFromConfig();

            LocalUnlock.EnsureInstalled();

            // 加载「广播名 -> 设备地址」的别名表（苹果设备地址轮换时的兜底识别手段）
            RpaResolver.Initialize(System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "device-aliases.tsv"));

            // 全局异常处理
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            base.OnStartup(e);
        }

        /// <summary>从配置读出主题与语言并应用（命令行调试参数优先）</summary>
        private static void ApplyAppearanceFromConfig()
        {
            string theme = DebugTheme;
            string lang = DebugLang;

            if (string.IsNullOrEmpty(theme) || string.IsNullOrEmpty(lang))
            {
                try
                {
                    var settings = ConfigService.Instance.LoadSettings();
                    if (string.IsNullOrEmpty(theme)) theme = settings.Theme;
                    if (string.IsNullOrEmpty(lang)) lang = settings.Language;
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLine("读取外观配置失败: " + ex.Message);
                }
            }

            ThemeService.Apply(theme);
            LangService.Apply(lang);

            // 跟随系统时，系统深浅色一变就跟着换
            try
            {
                Microsoft.Win32.SystemEvents.UserPreferenceChanged += (s, args) =>
                {
                    if (ThemeService.Current != AppTheme.System) return;
                    Current?.Dispatcher?.Invoke(() => ThemeService.Apply("system"));
                };
            }
            catch
            {
                // 某些精简系统上没有 SystemEvents，忽略
            }
        }

        /// <summary>切换毛玻璃 / 普通窗口外壳时需要重建主窗口（AllowsTransparency 不能运行时改）</summary>
        public static void RebuildMainWindow(int tab)
        {
            var app = Current;
            if (app == null) return;

            var old = app.MainWindow as Views.MainWindow;
            PendingTab = tab;

            Views.MainWindow win = null;
            try
            {
                win = new Views.MainWindow();
                app.MainWindow = win;
                win.Show();
            }
            catch (Exception ex)
            {
                LogHelper.WriteLine("重建主窗口失败: " + ex.Message);
                try { win?.Close(); } catch { }
                return;
            }

            if (old != null)
            {
                old.ReleaseForRebuild();
            }
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            LogHelper.WriteLine($"UI线程异常: {e.Exception.Message}\n{e.Exception.StackTrace}");
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogHelper.WriteLine($"非UI线程异常: {ex.Message}\n{ex.StackTrace}");
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}

