using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace GitDeployPro.Controls
{
    /// <summary>Shared severity coloring for deploy / transfer log lines.</summary>
    public static class LogColorizer
    {
        public static System.Windows.Media.Brush Resolve(string key, string fallbackHex)
        {
            if (System.Windows.Application.Current?.TryFindResource(key) is System.Windows.Media.Brush brush)
            {
                return brush;
            }

            return (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(fallbackHex)!;
        }

        public static System.Windows.Media.Brush PickBrush(string message)
        {
            if (Has(message, "❌", "failed", "error", "exception", "denied", "refused"))
            {
                return Resolve("Status.Error", "#FF5C6A");
            }

            if (Has(message, "⚠", "warning", "⏭", "skipped", "missing", "cancel"))
            {
                return Resolve("Status.Warning", "#E6B84D");
            }

            if (Has(message, "✅", "🎉", "done", "uploaded", "connected", "success"))
            {
                return Resolve("Status.Success", "#3DDC84");
            }

            if (Has(message, "🔌", "🚚", "👷", "🗺", "📁", "🗑", "connecting", "uploading", "workers"))
            {
                return Resolve("Status.Info", "#5BB8FF");
            }

            return Resolve("Text.Primary", "#E6EAF2");
        }

        public static Run[] BuildRuns(string timestamp, string message)
        {
            return new[]
            {
                new Run($"[{timestamp}] ") { Foreground = Resolve("Text.Muted", "#7A8499") },
                new Run(message) { Foreground = PickBrush(message) }
            };
        }

        private static bool Has(string text, params string[] needles)
        {
            foreach (var needle in needles)
            {
                if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
