using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Shapes;
using WpfPath = System.Windows.Shapes.Path;

namespace GitDeployPro.Behaviors
{
    /// <summary>
    /// The design language uses stroke icons (Themes/Icons.xaml) instead of colour emoji. Many labels in the app still
    /// carry emoji in XAML or code-behind ("🔄", "📝 COMMIT", ...). This class swaps every known emoji inside a
    /// <see cref="TextBlock"/> for the matching <c>AppIcon</c> path (same colour as the text) when the TextBlock loads
    /// or its Text changes, so those labels follow the theme without touching each call site.
    /// Unknown emoji and plain arrows are left alone.
    /// </summary>
    public static class EmojiIconizer
    {
        private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
        {
            ["✅"] = "check-circle",          // ✅
            ["❌"] = "x-circle",              // ❌
            ["✕"] = "x",                     // ✕
            ["✖"] = "x",                     // ✖
            ["⚠"] = "alert",                 // ⚠
            ["\U0001F4C1"] = "folder",            // 📁
            ["\U0001F4C2"] = "folder-open",       // 📂
            ["\U0001F5C2"] = "layers",            // 🗂
            ["\U0001F680"] = "rocket",            // 🚀
            ["\U0001F504"] = "refresh",           // 🔄
            ["↻"] = "refresh",               // ↻
            ["↺"] = "restore",               // ↺
            ["\U0001F4C4"] = "file-text",         // 📄
            ["\U0001F5D1"] = "trash",             // 🗑
            ["★"] = "star",                  // ★
            ["☆"] = "star",                  // ☆
            ["⬆"] = "arrow-up",              // ⬆
            ["⬇"] = "download",              // ⬇
            ["\U0001F4E4"] = "upload",            // 📤
            ["\U0001F4E5"] = "download",          // 📥
            ["⚡"] = "zap",                   // ⚡
            ["↩"] = "undo",                  // ↩
            ["⏳"] = "clock",                 // ⏳
            ["⏱"] = "clock",                 // ⏱
            ["\U0001F4DD"] = "edit",              // 📝
            ["✎"] = "edit",                  // ✎
            ["✏"] = "edit",                  // ✏
            ["✔"] = "check",                 // ✔
            ["✓"] = "check",                 // ✓
            ["☑"] = "check",                 // ☑
            ["⏸"] = "pause",                 // ⏸
            ["⏹"] = "stop",                  // ⏹
            ["▶"] = "play",                  // ▶
            ["☁"] = "cloud",                 // ☁
            ["⛔"] = "x-circle",              // ⛔
            ["\U0001F6AB"] = "x-circle",          // 🚫
            ["\U0001F6A8"] = "alert",             // 🚨
            ["\U0001F4CB"] = "clipboard",         // 📋
            ["\U0001F441"] = "eye",               // 👁
            ["\U0001F6E2"] = "database",          // 🛢
            ["\U0001F50C"] = "link",              // 🔌
            ["\U0001F517"] = "link",              // 🔗
            ["\U0001F4CE"] = "paperclip",         // 📎
            ["➕"] = "plus",                  // ➕
            ["\U0001F50D"] = "search",            // 🔍
            ["\U0001F512"] = "lock",              // 🔒
            ["\U0001F510"] = "lock",              // 🔐
            ["\U0001F5D6"] = "square",            // 🗖
            ["▾"] = "chevron-down",          // ▾
            ["▸"] = "chevron-right",         // ▸
            ["⚙"] = "settings",              // ⚙
            ["\U0001F4E6"] = "package",           // 📦
            ["\U0001F4CA"] = "activity",          // 📊
            ["\U0001F9F9"] = "wand",              // 🧹
            ["✨"] = "wand",                  // ✨
            ["\U0001F418"] = "file-code",         // 🐘
        };

        private static readonly Regex EmojiRx = new(
            "([\uD800-\uDBFF][\uDC00-\uDFFF]|[←-➿⬀-⯿⏩-⏿])️?",
            RegexOptions.Compiled);

        private static bool _registered;

        public static void Register()
        {
            if (_registered)
            {
                return;
            }

            _registered = true;
            EventManager.RegisterClassHandler(
                typeof(TextBlock),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnTextBlockLoaded));
        }

        private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
            "Hooked", typeof(bool), typeof(EmojiIconizer), new PropertyMetadata(false));

        private static readonly DependencyProperty BusyProperty = DependencyProperty.RegisterAttached(
            "Busy", typeof(bool), typeof(EmojiIconizer), new PropertyMetadata(false));

        private static void OnTextBlockLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBlock tb)
            {
                return;
            }

            Process(tb);

            if (!(bool)tb.GetValue(HookedProperty))
            {
                tb.SetValue(HookedProperty, true);
                DependencyPropertyDescriptor
                    .FromProperty(TextBlock.TextProperty, typeof(TextBlock))
                    ?.AddValueChanged(tb, (_, _) => Process(tb));
            }
        }

        private static void Process(TextBlock tb)
        {
            if ((bool)tb.GetValue(BusyProperty))
            {
                return;
            }

            // Only plain-text TextBlocks (Text / a single Run). Rich content keeps its own inlines.
            if (tb.Inlines.Any(i => i is not Run))
            {
                return;
            }

            var text = tb.Text;
            if (string.IsNullOrEmpty(text) || !EmojiRx.IsMatch(text))
            {
                return;
            }

            var matches = EmojiRx.Matches(text).Cast<Match>().Where(m => Map.ContainsKey(m.Groups[1].Value)).ToList();
            if (matches.Count == 0)
            {
                return;
            }

            tb.SetValue(BusyProperty, true);
            try
            {
                var size = tb.FontSize > 0 ? tb.FontSize * 1.15 : 16;
                var inlines = new List<Inline>();
                var last = 0;
                foreach (var m in matches)
                {
                    if (m.Index > last)
                    {
                        inlines.Add(new Run(text.Substring(last, m.Index - last)));
                    }

                    inlines.Add(CreateIcon(tb, Map[m.Groups[1].Value], size));
                    last = m.Index + m.Length;
                }

                if (last < text.Length)
                {
                    inlines.Add(new Run(text.Substring(last)));
                }

                // A space after the icon reads better than the emoji's own width.
                tb.Inlines.Clear();
                foreach (var inline in inlines)
                {
                    tb.Inlines.Add(inline);
                }
            }
            finally
            {
                tb.SetValue(BusyProperty, false);
            }
        }

        private static InlineUIContainer CreateIcon(TextBlock owner, string kind, double size)
        {
            var path = new WpfPath();
            AppIcon.SetKind(path, kind);
            AppIcon.SetSize(path, size);
            // Follow the label colour (also when the TextBlock is recoloured by a trigger).
            path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground")
            {
                Source = owner
            });

            return new InlineUIContainer(path)
            {
                BaselineAlignment = BaselineAlignment.Center
            };
        }
    }
}
