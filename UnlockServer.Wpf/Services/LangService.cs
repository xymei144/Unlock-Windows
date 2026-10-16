using System;
using System.Windows;

namespace UnlockServer.Services
{
    /// <summary>
    /// 语言服务：运行时热切换中英文资源字典。
    /// 界面上文字用 {DynamicResource T.Xxx}，代码里用 LangService.T("T.Xxx")。
    /// </summary>
    public static class LangService
    {
        public const string Zh = "zh";
        public const string En = "en";

        /// <summary>当前语言（zh / en）</summary>
        public static string Current { get; private set; } = Zh;

        /// <summary>语言变更通知，供 ViewModel 刷新已是字符串的属性</summary>
        public static event Action LanguageChanged;

        public static string Normalize(string lang)
        {
            return string.Equals(lang, En, StringComparison.OrdinalIgnoreCase) ? En : Zh;
        }

        /// <summary>取翻译文本；找不到 key 时原样返回 key，方便发现漏翻译</summary>
        public static string T(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            var app = Application.Current;
            if (app == null) return key;
            try
            {
                return app.TryFindResource(key) as string ?? key;
            }
            catch
            {
                return key;
            }
        }

        /// <summary>取翻译文本并做 string.Format</summary>
        public static string F(string key, params object[] args)
        {
            var fmt = T(key);
            if (args == null || args.Length == 0) return fmt;
            try { return string.Format(fmt, args); }
            catch { return fmt; }
        }

        /// <summary>应用语言（替换 Application 里的 Lang.* 字典）</summary>
        public static void Apply(string lang)
        {
            Current = Normalize(lang);

            var app = Application.Current;
            if (app == null) return;

            var uri = new Uri(
                Current == En ? "Themes/Lang.en.xaml" : "Themes/Lang.zh.xaml",
                UriKind.Relative);
            var dict = new ResourceDictionary { Source = uri };

            var merged = app.Resources.MergedDictionaries;
            for (int i = 0; i < merged.Count; i++)
            {
                var src = merged[i].Source?.OriginalString ?? "";
                if (src.IndexOf("Lang.", StringComparison.OrdinalIgnoreCase) < 0) continue;
                merged[i] = dict;
                LanguageChanged?.Invoke();
                return;
            }
            merged.Add(dict);
            LanguageChanged?.Invoke();
        }
    }
}
