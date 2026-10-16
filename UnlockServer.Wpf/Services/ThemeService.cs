using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace UnlockServer.Services
{
    /// <summary>界面主题</summary>
    public enum AppTheme
    {
        Dark,
        Light,
        System,
        Acrylic
    }

    /// <summary>
    /// 主题服务：运行时热切换调色板；毛玻璃模式额外开启 DWM 亚克力背景。
    /// </summary>
    public static class ThemeService
    {
        /// <summary>用户选择的主题（可能是 System）</summary>
        public static AppTheme Current { get; private set; } = AppTheme.Dark;

        /// <summary>主题变更通知</summary>
        public static event Action ThemeChanged;

        public static bool IsAcrylic => Resolve(Current) == AppTheme.Acrylic;

        /// <summary>毛玻璃（亚克力）需要 Win11 22000+</summary>
        public static bool AcrylicSupported => Environment.OSVersion.Version.Build >= 22000;

        public static string Name(AppTheme theme)
        {
            switch (theme)
            {
                case AppTheme.Light: return "light";
                case AppTheme.System: return "system";
                case AppTheme.Acrylic: return "acrylic";
                default: return "dark";
            }
        }

        public static AppTheme Parse(string name)
        {
            switch ((name ?? "").Trim().ToLowerInvariant())
            {
                case "light": return AppTheme.Light;
                case "system": return AppTheme.System;
                case "acrylic": return AppTheme.Acrylic;
                default: return AppTheme.Dark;
            }
        }

        /// <summary>把 System 解析成实际生效的 Dark / Light</summary>
        public static AppTheme Resolve(AppTheme theme)
        {
            if (theme == AppTheme.System)
                return IsSystemDark() ? AppTheme.Dark : AppTheme.Light;
            if (theme == AppTheme.Acrylic && !AcrylicSupported)
                return IsSystemDark() ? AppTheme.Dark : AppTheme.Light;
            return theme;
        }

        /// <summary>读注册表判断系统是深色还是浅色（默认深色）</summary>
        public static bool IsSystemDark()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var value = key?.GetValue("AppsUseLightTheme");
                    if (value is int i) return i == 0;
                }
            }
            catch
            {
                // 读不到就按深色处理
            }
            return true;
        }

        /// <summary>应用主题（替换调色板字典；不含窗口外壳，外壳切换见 ApplyWindowShell）</summary>
        public static void Apply(string themeName)
        {
            Current = Parse(themeName);

            var app = Application.Current;
            if (app == null) return;

            var file = "Themes/Palette." + Resolve(Current).ToString() + ".xaml";
            var dict = new ResourceDictionary { Source = new Uri(file, UriKind.Relative) };

            var merged = app.Resources.MergedDictionaries;
            for (int i = 0; i < merged.Count; i++)
            {
                var src = merged[i].Source?.OriginalString ?? "";
                if (src.IndexOf("Palette.", StringComparison.OrdinalIgnoreCase) < 0 &&
                    src.IndexOf("Colors.xaml", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                merged[i] = dict;
                ThemeChanged?.Invoke();
                return;
            }
            merged.Insert(0, dict);
            ThemeChanged?.Invoke();
        }

        /// <summary>
        /// 按当前主题调整窗口外壳。
        /// 普通主题：无边框透明窗口 + 自绘圆角阴影；
        /// 毛玻璃：不透明窗口 + WindowChrome 玻璃帧 + DWM 亚克力（圆角由 DWM 提供）。
        /// 必须在窗口 Show 之前调用。
        /// </summary>
        public static void ApplyWindowShell(Window window)
        {
            if (window == null) return;

            if (IsAcrylic)
            {
                window.AllowsTransparency = false;
                window.WindowStyle = WindowStyle.None;
                try
                {
                    System.Windows.Shell.WindowChrome.SetWindowChrome(window,
                        new System.Windows.Shell.WindowChrome
                        {
                            CaptionHeight = 0,
                            CornerRadius = new CornerRadius(0),
                            GlassFrameThickness = new Thickness(-1),
                            ResizeBorderThickness = new Thickness(6),
                            UseAeroCaptionButtons = false
                        });
                }
                catch
                {
                    // 极端情况下退回透明窗口模式
                    window.AllowsTransparency = true;
                }
            }
            else
            {
                window.AllowsTransparency = true;
                try { System.Windows.Shell.WindowChrome.SetWindowChrome(window, null); } catch { }
            }
        }

        /// <summary>
        /// 修正无边框窗口的 Win32 样式。
        ///
        /// 背景：WPF 的 <c>WindowStyle="None" + AllowsTransparency="True"</c> 会创建带 WS_POPUP 的
        /// 分层窗口，但 XAML 默认仍保留 WS_SYSMENU。当窗口同时具备 WS_SYSMENU 却缺少 WS_CAPTION 时，
        /// 系统的命中测试无法判定任何点归属该窗口，于是整个窗口对所有坐标返回 HTNOWHERE ——
        /// 表现为窗口无法拖动、按钮点不动（鼠标消息被穿透到下层窗口）。
        ///
        /// 修复：清掉 WS_SYSMENU / WS_THICKFRAME / 最小化最大化按钮位，补上 WS_POPUP，
        /// 使窗口成为合法的无边框弹出窗口。必须在 <c>SourceInitialized</c> 阶段调用。
        /// </summary>
        public static void FixBorderlessWindowStyle(Window window)
        {
            if (window == null) return;

            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                int style = GetWindowLong(hwnd, GWL_STYLE);

                // 去掉让命中测试失效、且外观已自绘的系统边框相关位
                style &= ~WS_SYSMENU;        // 0x00080000
                style &= ~WS_THICKFRAME;     // 0x00040000  可调整边框（本程序 ResizeMode=NoResize）
                style &= ~WS_MINIMIZEBOX;    // 0x00020000
                style &= ~WS_MAXIMIZEBOX;    // 0x00010000
                style &= ~WS_DLGFRAME;       // 0x00400000
                style &= ~WS_BORDER;         // 0x00800000

                // 无边框顶层窗口的正确形态
                style |= WS_POPUP;           // 0x80000000

                SetWindowLong(hwnd, GWL_STYLE, style);

                // 让样式变更立即生效（框架已变更）
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
            catch
            {
                // 样式修正失败不致命：仅退化为窗口不可拖动，不影响锁屏/解锁主功能
            }
        }

        /// <summary>窗口句柄就绪后，让 DWM 补上亚克力背景与圆角（仅毛玻璃模式生效）</summary>
        public static void ApplyDwmBackdrop(Window window)
        {
            if (window == null || !IsAcrylic) return;

            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                // 毛玻璃需要客户区完全交给 DWM 合成
                var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref margins);

                // DWMWA_SYSTEMBACKDROP_TYPE = 38，3 = DWMSBT_TRANSIENTWINDOW（亚克力）
                int backdrop = 3;
                DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int));

                // DWMWA_WINDOW_CORNER_PREFERENCE = 33，2 = DWMWCP_ROUND
                int corner = 2;
                DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));
            }
            catch
            {
                // 老系统 / 远程桌面下可能失败，此时窗口退化为半透明纯色，不影响使用
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Margins
        {
            public int Left;
            public int Right;
            public int Top;
            public int Bottom;
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // ===== 窗口样式修正（无边框窗口的命中测试修复）=====

        private const int GWL_STYLE = -16;

        private const int WS_BORDER = 0x00800000;
        private const int WS_DLGFRAME = 0x00400000;
        private const int WS_SYSMENU = 0x00080000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_MAXIMIZEBOX = 0x00010000;

        // WS_POPUP = 0x80000000，超出 int 正数范围，用 unchecked 转成有符号常量
        private const int WS_POPUP = unchecked((int)0x80000000);

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter,
            int x, int y, int cx, int cy, uint flags);
    }
}
