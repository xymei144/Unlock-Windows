using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace UnlockServer.Views
{
    public static class ToastService
    {
        public static void Show(string message, int seconds = 4)
        {
            var app = Application.Current;
            if (app == null) return;

            void ShowCore()
            {
                var window = CreateWindow();
                var border = CreateBorder();
                border.Child = new TextBlock
                {
                    Text = message ?? "",
                    Foreground = ResolveBrush("TextPrimaryBrush", Colors.White),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap
                };

                // 外层 Grid：底层投影（不参与命中）+ 上层内容
                var shell = new Grid();
                shell.Children.Add(CreateShadowLayer());
                shell.Children.Add(border);

                window.Content = shell;
                PlaceBottomRight(window);
                window.Show();

                if (!string.IsNullOrEmpty(App.ToastDebugShot))
                {
                    var shot = App.ToastDebugShot;
                    window.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            int w = (int)Math.Ceiling(window.ActualWidth);
                            int h = (int)Math.Ceiling(window.ActualHeight);
                            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                                w, h, 96, 96, PixelFormats.Pbgra32);
                            rtb.Render(border);
                            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                            using (var fs = System.IO.File.Create(shot)) enc.Save(fs);
                        }
                        catch (Exception ex)
                        {
                            try { System.IO.File.WriteAllText(shot + ".err.txt", ex.ToString()); } catch { }
                        }
                        finally
                        {
                            Application.Current.Shutdown();
                        }
                    }), DispatcherPriority.Loaded);
                    return;
                }

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds > 0 ? seconds : 4) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    try { window.Close(); } catch { }
                };
                timer.Start();
            }

            if (app.Dispatcher.CheckAccess())
                ShowCore();
            else
                app.Dispatcher.Invoke(ShowCore);
        }

        public static ActionToast ShowAction(string title, string hint, int seconds, Action onCancel, Action onTimeout)
        {
            var app = Application.Current;
            if (app == null) return ActionToast.Empty;

            ActionToast toast = null;
            void ShowCore() => toast = ActionToast.ShowCore(title, hint, seconds, onCancel, onTimeout);

            if (app.Dispatcher.CheckAccess())
                ShowCore();
            else
                app.Dispatcher.Invoke(ShowCore);
            return toast ?? ActionToast.Empty;
        }

        internal static Window CreateWindow()
        {
            return new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowActivated = false
            };
        }

        /// <summary>
        /// 构造通知卡片。
        /// 注意：WPF 中对元素设置 Effect 会将其渲染到独立离屏表面，
        /// 使该元素及其子元素的鼠标命中测试失效（卡片上的「取消」按钮会点不动）。
        /// 因此投影放在底层装饰 Border 上（IsHitTestVisible=False），
        /// 返回的是承载内容的上层 Border。
        /// </summary>
        internal static Border CreateBorder()
        {
            return new Border
            {
                Background = ResolveBrush("WindowSolidBrush", Color.FromRgb(26, 26, 38)),
                BorderBrush = ResolveBrush("PrimaryBrush", Color.FromRgb(76, 141, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(18, 14, 18, 14),
                Margin = new Thickness(10),
                MaxWidth = 380,
                Cursor = Cursors.Hand
            };
        }

        /// <summary>与内容层配套的底层投影装饰（不参与命中测试）</summary>
        internal static Border CreateShadowLayer()
        {
            return new Border
            {
                Background = ResolveBrush("WindowSolidBrush", Color.FromRgb(26, 26, 38)),
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(10),
                IsHitTestVisible = false,
                Focusable = false,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 18,
                    ShadowDepth = 0,
                    Opacity = 0.45,
                    Color = Colors.Black
                }
            };
        }

        /// <summary>从主题资源里取画笔，取不到就用兜底色</summary>
        internal static Brush ResolveBrush(string key, Color fallback)
        {
            try
            {
                var brush = Application.Current?.TryFindResource(key) as Brush;
                if (brush != null) return brush;
            }
            catch
            {
                // 应用还没初始化完时忽略
            }
            return new SolidColorBrush(fallback);
        }

        internal static void PlaceBottomRight(Window window)
        {
            window.Loaded += (s, e) =>
            {
                var work = SystemParameters.WorkArea;
                window.Left = work.Right - window.ActualWidth - 18;
                window.Top = work.Bottom - window.ActualHeight - 18;
            };
        }
    }

    public sealed class ActionToast : IDisposable
    {
        public static readonly ActionToast Empty = new ActionToast();

        private Window _window;
        private DispatcherTimer _timer;
        private TextBlock _title;
        private bool _closed;
        private int _remain;
        private string _titleText;
        private Action _onCancel;
        private Action _onTimeout;

        private ActionToast() { }

        internal static ActionToast ShowCore(string title, string hint, int seconds, Action onCancel, Action onTimeout)
        {
            var toast = new ActionToast
            {
                _remain = seconds > 0 ? seconds : 1,
                _titleText = title ?? "",
                _onCancel = onCancel,
                _onTimeout = onTimeout
            };

            toast._window = ToastService.CreateWindow();

            // 内容层 + 底层投影层
            var border = ToastService.CreateBorder();

            var stack = new StackPanel();
            toast._title = new TextBlock
            {
                Text = toast.BuildTitle(),
                Foreground = ToastService.ResolveBrush("TextPrimaryBrush", Colors.White),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };
            stack.Children.Add(toast._title);
            stack.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(hint) ? Services.LangService.T("T.Toast.CancelHint") : hint,
                Foreground = ToastService.ResolveBrush("TextSecondaryBrush", Color.FromRgb(180, 186, 200)),
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            border.Child = stack;

            // 外层 Grid：底层负责投影（不参与命中），上层承载内容
            var shell = new Grid();
            shell.Children.Add(ToastService.CreateShadowLayer());
            shell.Children.Add(border);

            toast._window.Content = shell;
            toast._window.MouseLeftButtonUp += (s, e) => toast.Cancel();
            ToastService.PlaceBottomRight(toast._window);
            toast._window.Show();

            toast._timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            toast._timer.Tick += (s, e) => toast.Tick();
            toast._timer.Start();
            return toast;
        }

        private string BuildTitle() => _titleText + "  " + Services.LangService.F("T.Toast.Seconds", _remain);

        private void Tick()
        {
            if (_closed) return;
            _remain--;
            if (_remain <= 0)
            {
                var done = _onTimeout;
                Close();
                done?.Invoke();
                return;
            }
            if (_title != null)
                _title.Text = BuildTitle();
        }

        public void Cancel()
        {
            if (_closed) return;
            var cancel = _onCancel;
            Close();
            cancel?.Invoke();
        }

        public void Dismiss()
        {
            if (_closed) return;
            Close();
        }

        private void Close()
        {
            if (_closed) return;
            _closed = true;
            _onCancel = null;
            _onTimeout = null;
            try { _timer?.Stop(); } catch { }
            try { _window?.Close(); } catch { }
            _timer = null;
            _window = null;
        }

        public void Dispose() => Dismiss();
    }
}
