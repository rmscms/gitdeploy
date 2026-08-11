using System;
using System.Collections.Generic;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;

namespace GitDeployPro.Services.Theme
{
    /// <summary>Resolved runtime colors for Deploy surfaces after applying a theme pack.</summary>
    public sealed class DeployThemeTokens
    {
        private readonly Dictionary<string, MediaColor> _colors = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ThemePackFileTypeVisual> _fileTypes =
            new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, MediaColor> Colors => _colors;
        public IReadOnlyDictionary<string, string> Strings => _strings;
        public IReadOnlyDictionary<string, ThemePackFileTypeVisual> FileTypes => _fileTypes;

        public string MonacoTheme { get; private set; } = "vs-dark";
        public IReadOnlyList<string> TerminalTextPresets { get; private set; } = Array.Empty<string>();

        public MediaColor GetColor(string path, MediaColor fallback)
            => _colors.TryGetValue(path, out var c) ? c : fallback;

        public string GetHex(string path, string fallback)
            => _colors.TryGetValue(path, out var c) ? ThemeColorParser.ToHex(c) : fallback;

        public string GetString(string path, string fallback)
            => _strings.TryGetValue(path, out var s) && !string.IsNullOrWhiteSpace(s) ? s : fallback;

        public SolidColorBrush GetBrush(string path, MediaColor fallback)
        {
            var brush = new SolidColorBrush(GetColor(path, fallback));
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }

            return brush;
        }

        public ThemePackFileTypeVisual GetFileType(string key)
        {
            if (_fileTypes.TryGetValue(key, out var visual))
            {
                return visual;
            }

            return ThemeTokenCatalog.BuildDefaultFileTypes()["defaultFile"];
        }

        public static DeployThemeTokens Resolve(ThemePack pack, Func<string, MediaColor?> resolvePalette)
        {
            var tokens = new DeployThemeTokens();
            var deploy = pack.Deploy ?? new ThemePackDeploySection();

            void AddMap(string prefix, Dictionary<string, string>? map)
            {
                if (map == null)
                {
                    return;
                }

                foreach (var kv in map)
                {
                    var path = prefix + "." + kv.Key;
                    ResolveValue(tokens, path, kv.Value, resolvePalette);
                }
            }

            AddMap("shell", deploy.Shell);
            AddMap("header", deploy.Header);
            AddMap("branches", deploy.Branches);
            AddMap("changedFiles", deploy.ChangedFiles);
            AddMap("directUpload", deploy.DirectUpload);
            AddMap("logs", deploy.Logs);
            AddMap("editor", deploy.Editor);
            AddMap("diff", deploy.Diff);
            AddMap("ftp.chrome", deploy.Ftp?.Chrome);

            var terminal = deploy.Terminal ?? new ThemePackTerminalSection();
            ResolveValue(tokens, "terminal.hostBackground", terminal.HostBackground, resolvePalette);
            ResolveValue(tokens, "terminal.statusConnected", terminal.StatusConnected, resolvePalette);
            ResolveValue(tokens, "terminal.statusDisconnected", terminal.StatusDisconnected, resolvePalette);
            ResolveValue(tokens, "terminal.statusConnecting", terminal.StatusConnecting, resolvePalette);
            ResolveValue(tokens, "terminal.statusError", terminal.StatusError, resolvePalette);
            ResolveValue(tokens, "terminal.presetsDrawerBackground", terminal.PresetsDrawerBackground, resolvePalette);
            ResolveValue(tokens, "terminal.presetsDrawerBorder", terminal.PresetsDrawerBorder, resolvePalette);
            ResolveValue(tokens, "terminal.presetsHeaderForeground", terminal.PresetsHeaderForeground, resolvePalette);
            ResolveValue(tokens, "terminal.presetsChipBackground", terminal.PresetsChipBackground, resolvePalette);
            ResolveValue(tokens, "terminal.suggestionGhost", terminal.SuggestionGhost, resolvePalette);
            ResolveValue(tokens, "terminal.suggestionListBackground", terminal.SuggestionListBackground, resolvePalette);
            ResolveValue(tokens, "terminal.suggestionListActive", terminal.SuggestionListActive, resolvePalette);
            ResolveValue(tokens, "terminal.suggestionListBorder", terminal.SuggestionListBorder, resolvePalette);
            AddMap("terminal.xterm", terminal.Xterm);
            tokens.TerminalTextPresets = terminal.TextPresets?.Count > 0
                ? terminal.TextPresets.ToArray()
                : new[] { "#D4D4D4", "#FFFFFF", "#00FF00", "#00FFFF", "#FFFF00" };

            tokens.MonacoTheme = tokens.GetString("editor.monacoTheme", "vs-dark");
            if (deploy.Editor != null && deploy.Editor.TryGetValue("monacoTheme", out var monaco)
                && !string.IsNullOrWhiteSpace(monaco)
                && !ThemeColorParser.IsPaletteRef(monaco)
                && !ThemeColorParser.TryParse(monaco, out _))
            {
                tokens.MonacoTheme = monaco.Trim();
                tokens._strings["editor.monacoTheme"] = tokens.MonacoTheme;
            }

            var fileTypes = deploy.Ftp?.FileTypes ?? ThemeTokenCatalog.BuildDefaultFileTypes();
            var defaults = ThemeTokenCatalog.BuildDefaultFileTypes();
            foreach (var key in ThemeTokenCatalog.FileTypeKeys)
            {
                fileTypes.TryGetValue(key, out var custom);
                defaults.TryGetValue(key, out var fallback);
                var merged = new ThemePackFileTypeVisual
                {
                    Icon = ResolveColorString(custom?.Icon ?? fallback?.Icon, resolvePalette) ?? fallback?.Icon,
                    BadgeBg = ResolveColorString(custom?.BadgeBg ?? fallback?.BadgeBg, resolvePalette) ?? fallback?.BadgeBg,
                    BadgeBorder = ResolveColorString(custom?.BadgeBorder ?? fallback?.BadgeBorder, resolvePalette) ?? fallback?.BadgeBorder,
                    BadgeFg = ResolveColorString(custom?.BadgeFg ?? fallback?.BadgeFg, resolvePalette) ?? fallback?.BadgeFg
                };
                tokens._fileTypes[key] = merged;
            }

            return tokens;
        }

        private static void ResolveValue(
            DeployThemeTokens tokens,
            string path,
            string? value,
            Func<string, MediaColor?> resolvePalette)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            // Non-color strings (monaco theme name, rgba selection)
            if (path.EndsWith("monacoTheme", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("rgba", StringComparison.OrdinalIgnoreCase))
            {
                tokens._strings[path] = value.Trim();
                return;
            }

            if (ThemeColorParser.IsPaletteRef(value))
            {
                var key = ThemeColorParser.GetPaletteRefKey(value)!;
                var paletteColor = resolvePalette(key);
                if (paletteColor.HasValue)
                {
                    tokens._colors[path] = paletteColor.Value;
                    tokens._strings[path] = ThemeColorParser.ToHex(paletteColor.Value);
                }

                return;
            }

            if (ThemeColorParser.TryParse(value, out var color))
            {
                tokens._colors[path] = color;
                tokens._strings[path] = ThemeColorParser.ToHex(color);
                return;
            }

            tokens._strings[path] = value.Trim();
        }

        private static string? ResolveColorString(string? value, Func<string, MediaColor?> resolvePalette)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            if (ThemeColorParser.IsPaletteRef(value))
            {
                var key = ThemeColorParser.GetPaletteRefKey(value)!;
                var paletteColor = resolvePalette(key);
                return paletteColor.HasValue ? ThemeColorParser.ToHex(paletteColor.Value) : value;
            }

            if (ThemeColorParser.TryParse(value, out var color))
            {
                return ThemeColorParser.ToHex(color);
            }

            return value;
        }
    }
}
