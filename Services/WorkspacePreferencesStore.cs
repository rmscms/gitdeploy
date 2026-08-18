using System;
using System.Linq;

namespace GitDeployPro.Services
{
    /// <summary>App-wide editor/terminal appearance (same for every project).</summary>
    public static class WorkspacePreferencesStore
    {
        public const string EditorModeDock = "dock";
        public const string EditorModeFloat = "float";
        public const double DefaultFontSize = 14;
        public const string DefaultForeground = "#D4D4D4";

        public static readonly double[] FontSizes = { 10, 12, 14, 16, 18, 20 };

        public static readonly (string Name, string Hex)[] Foregrounds =
        {
            ("Gray", "#D4D4D4"),
            ("White", "#FFFFFF"),
            ("Green", "#00FF00"),
            ("Cyan", "#00FFFF"),
            ("Yellow", "#FFFF00")
        };

        private static readonly ConfigurationService ConfigService = new();

        public static event Action? AppearanceChanged;

        public static string NormalizeEditorOpenMode(string? mode) =>
            string.Equals(mode, EditorModeFloat, StringComparison.OrdinalIgnoreCase)
                ? EditorModeFloat
                : EditorModeDock;

        public static bool OpenEditorInFloat() =>
            NormalizeEditorOpenMode(ConfigService.LoadGlobalConfig().DeployEditorOpenMode) == EditorModeFloat;

        public static void SaveEditorOpenMode(string mode)
        {
            var normalized = NormalizeEditorOpenMode(mode);
            ConfigService.UpdateGlobalConfig(cfg => cfg.DeployEditorOpenMode = normalized);
        }

        public static double NormalizeFontSize(double size)
        {
            foreach (var allowed in FontSizes)
            {
                if (Math.Abs(allowed - size) < 0.01)
                {
                    return allowed;
                }
            }

            return DefaultFontSize;
        }

        public static string NormalizeForeground(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
            {
                return DefaultForeground;
            }

            var trimmed = hex.Trim();
            var match = Foregrounds.FirstOrDefault(item =>
                string.Equals(item.Hex, trimmed, StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrEmpty(match.Hex) ? DefaultForeground : match.Hex;
        }

        public static (double FontSize, string Foreground) LoadAppearance()
        {
            var config = ConfigService.LoadGlobalConfig();
            return (NormalizeFontSize(config.TerminalFontSize), NormalizeForeground(config.TerminalForeground));
        }

        public static void SaveAppearance(double fontSize, string foreground)
        {
            var size = NormalizeFontSize(fontSize);
            var hex = NormalizeForeground(foreground);
            var current = LoadAppearance();
            if (Math.Abs(current.FontSize - size) < 0.01
                && string.Equals(current.Foreground, hex, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ConfigService.UpdateGlobalConfig(cfg =>
            {
                cfg.TerminalFontSize = size;
                cfg.TerminalForeground = hex;
            });
            AppearanceChanged?.Invoke();
        }
    }
}
