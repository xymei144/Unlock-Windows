using System.Windows;
using System.Windows.Media;

namespace UnlockServer.Views
{
    /// <summary>
    /// 自定义消息对话框
    /// </summary>
    public partial class MessageDialog : Window
    {
        public enum DialogType
        {
            Info,
            Success,
            Warning,
            Error,
            Question
        }

        public enum DialogButtons
        {
            Ok,
            OkCancel,
            YesNo
        }

        public bool Result { get; private set; }

        public MessageDialog()
        {
            InitializeComponent();

            // 修复无边框窗口的 Win32 样式（否则命中测试返回 HTNOWHERE，按钮点不动）
            SourceInitialized += (s, e) => Services.ThemeService.FixBorderlessWindowStyle(this);
        }

        /// <summary>
        /// 带输入框的对话框：确定返回输入内容，取消或关闭返回 null
        /// </summary>
        public static string ShowInput(string message, string title = null, string initial = "", Window owner = null)
        {
            var dialog = new MessageDialog();
            dialog.Owner = null;
            dialog.Topmost = true;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.SetContent(message, title, DialogType.Question, DialogButtons.OkCancel);

            dialog.InputBox.Text = initial ?? "";
            dialog.InputBox.Visibility = Visibility.Visible;
            dialog.Loaded += (s, e) =>
            {
                dialog.InputBox.Focus();
                dialog.InputBox.SelectAll();
            };

            dialog.ShowDialog();
            return dialog.Result ? dialog.InputBox.Text : null;
        }

        private void InputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                Result = true;
                Close();
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                Result = false;
                Close();
            }
        }

        /// <summary>
        /// 显示消息对话框
        /// </summary>
        public static bool Show(string message, string title = null, DialogType type = DialogType.Info, DialogButtons buttons = DialogButtons.Ok, Window owner = null)
        {
            var dialog = new MessageDialog();
            dialog.Owner = null;
            dialog.Topmost = true;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.SetContent(message, title, type, buttons);
            dialog.ShowDialog();
            return dialog.Result;
        }

        /// <summary>
        /// 显示信息提示
        /// </summary>
        public static void ShowInfo(string message, string title = null, Window owner = null)
        {
            Show(message, title, DialogType.Info, DialogButtons.Ok, owner);
        }

        /// <summary>
        /// 显示成功提示
        /// </summary>
        public static void ShowSuccess(string message, string title = null, Window owner = null)
        {
            Show(message, title, DialogType.Success, DialogButtons.Ok, owner);
        }

        /// <summary>
        /// 显示警告提示
        /// </summary>
        public static void ShowWarning(string message, string title = null, Window owner = null)
        {
            Show(message, title, DialogType.Warning, DialogButtons.Ok, owner);
        }

        /// <summary>
        /// 显示错误提示
        /// </summary>
        public static void ShowError(string message, string title = null, Window owner = null)
        {
            Show(message, title, DialogType.Error, DialogButtons.Ok, owner);
        }

        /// <summary>
        /// 显示确认对话框
        /// </summary>
        public static bool ShowConfirm(string message, string title = null, Window owner = null)
        {
            return Show(message, title, DialogType.Question, DialogButtons.YesNo, owner);
        }

        private void SetContent(string message, string title, DialogType type, DialogButtons buttons)
        {
            TitleText.Text = string.IsNullOrEmpty(title) ? DefaultTitle(type) : title;
            MessageText.Text = message;
            // 设置图标和颜色
            switch (type)
            {
                case DialogType.Info:
                    IconText.Text = "ℹ️";
                    IconBorder.Background = new SolidColorBrush(Color.FromRgb(59, 130, 246));
                    break;
                case DialogType.Success:
                    IconText.Text = "✓";
                    IconBorder.Background = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                    break;
                case DialogType.Warning:
                    IconText.Text = "⚠";
                    IconBorder.Background = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                    break;
                case DialogType.Error:
                    IconText.Text = "✕";
                    IconBorder.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                    break;
                case DialogType.Question:
                    IconText.Text = "?";
                    IconBorder.Background = new SolidColorBrush(Color.FromRgb(139, 92, 246));
                    break;
            }

            // 设置按钮
            switch (buttons)
            {
                case DialogButtons.Ok:
                    CancelButton.Visibility = Visibility.Collapsed;
                    OkButton.Content = Services.LangService.T("T.Btn.Ok");
                    break;
                case DialogButtons.OkCancel:
                    CancelButton.Visibility = Visibility.Visible;
                    CancelButton.Content = Services.LangService.T("T.Btn.Cancel");
                    OkButton.Content = Services.LangService.T("T.Btn.Ok");
                    break;
                case DialogButtons.YesNo:
                    CancelButton.Visibility = Visibility.Visible;
                    CancelButton.Content = Services.LangService.T("T.Btn.No");
                    OkButton.Content = Services.LangService.T("T.Btn.Yes");
                    break;
            }
        }

        private static string DefaultTitle(DialogType type)
        {
            switch (type)
            {
                case DialogType.Success: return Services.LangService.T("T.Dlg.Success");
                case DialogType.Warning: return Services.LangService.T("T.Dlg.Warning");
                case DialogType.Error: return Services.LangService.T("T.Dlg.Error");
                case DialogType.Question: return Services.LangService.T("T.Dlg.Confirm");
                default: return Services.LangService.T("T.Dlg.Info");
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            Result = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Result = false;
            Close();
        }
    }
}

