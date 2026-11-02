using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using WpfBinding = System.Windows.Data.Binding;
using WpfBindingMode = System.Windows.Data.BindingMode;
using WpfBindingOperations = System.Windows.Data.BindingOperations;

namespace GitDeployPro.Services.Theme
{
    /// <summary>
    /// The app has ONE fixed look (Midnight Blue, Themes/AppTheme.json). This service installs its palette into the
    /// live palette brushes at startup and exposes the Deploy tokens (terminal, tree, diff, ...) to code-behind.
    /// </summary>
    public sealed class ThemeService
    {
        public const string AppThemeId = "midnight-blue";

        private static readonly Lazy<ThemeService> LazyInstance = new(() => new ThemeService());
        public static ThemeService Instance => LazyInstance.Value;

        public static IReadOnlyList<string> PaletteBrushKeys => ThemeTokenCatalog.RequiredPaletteKeys;

        private sealed class MutableColor : DependencyObject
        {
            public static readonly DependencyProperty ColorProperty =
                DependencyProperty.Register(
                    nameof(Color),
                    typeof(System.Windows.Media.Color),
                    typeof(MutableColor),
                    new PropertyMetadata(Colors.Transparent));

            public System.Windows.Media.Color Color
            {
                get => (System.Windows.Media.Color)GetValue(ColorProperty);
                set => SetValue(ColorProperty, value);
            }
        }

        private readonly Dictionary<string, MutableColor> _colorHolders = new(StringComparer.Ordinal);
        private readonly Dictionary<string, System.Windows.Media.Color> _paletteColors = new(StringComparer.OrdinalIgnoreCase);
        private bool _initialized;

        private ThemeService()
        {
        }

        public string CurrentThemeId { get; private set; } = AppThemeId;

        public ThemePack? CurrentPack { get; private set; }

        public DeployThemeTokens CurrentTokens { get; private set; } =
            DeployThemeTokens.Resolve(ThemeTokenCatalog.CreateDarkPack(), _ => null);

        public event EventHandler? ThemeChanged;

        public void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            InstallLivePaletteBrushes();
            CurrentTokens = DeployThemeTokens.Resolve(LoadAppPack(), ResolvePaletteColor);
            _initialized = true;
        }

        /// <summary>Installs the fixed Midnight Blue palette + Deploy tokens (call once at startup).</summary>
        public void ApplyAppTheme()
        {
            Initialize();

            var pack = LoadAppPack();
            ApplyPalette(pack);
            CurrentTokens = DeployThemeTokens.Resolve(pack, ResolvePaletteColor);
            PushTokenToPalette(
                "Tree.SearchMatchBackground",
                "tree.searchMatchBackground",
                System.Windows.Media.Color.FromRgb(0xFF, 0xBF, 0x47));
            PushTokenToPalette(
                "Tree.SearchMatchForeground",
                "tree.searchMatchForeground",
                System.Windows.Media.Color.FromRgb(0x10, 0x13, 0x1A));
            CurrentPack = pack;
            CurrentThemeId = AppThemeId;
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }

        private static ThemePack LoadAppPack()
        {
            try
            {
                using var stream = typeof(ThemeService).Assembly.GetManifestResourceStream("GitDeployPro.AppTheme.json");
                if (stream != null)
                {
                    using var reader = new System.IO.StreamReader(stream);
                    var pack = Newtonsoft.Json.JsonConvert.DeserializeObject<ThemePack>(reader.ReadToEnd());
                    if (pack != null)
                    {
                        return pack;
                    }
                }
            }
            catch
            {
                // fall through to the built-in dark seed
            }

            return ThemeTokenCatalog.CreateDarkPack();
        }

        public System.Windows.Media.Color GetTokenColor(string path, System.Windows.Media.Color fallback)
            => CurrentTokens.GetColor(path, fallback);

        public SolidColorBrush GetTokenBrush(string path, System.Windows.Media.Color fallback)
            => CurrentTokens.GetBrush(path, fallback);

        public string GetTokenHex(string path, string fallback)
            => CurrentTokens.GetHex(path, fallback);

        private void ApplyPalette(ThemePack pack)
        {
            _paletteColors.Clear();
            foreach (var key in ThemeTokenCatalog.RequiredPaletteKeys)
            {
                if (!pack.Palette.TryGetValue(key, out var value)
                    || !ThemeColorParser.TryParse(value, out var mediaColor))
                {
                    continue;
                }

                _paletteColors[key] = mediaColor;

                if (_colorHolders.TryGetValue(key, out var holder))
                {
                    holder.Color = mediaColor;
                }

                var colorKey = "Color." + key;
                var colorsDict = FindColorsDictionary(System.Windows.Application.Current?.Resources);
                if (colorsDict != null && colorsDict.Contains(colorKey))
                {
                    colorsDict[colorKey] = mediaColor;
                }
            }
        }

        private void PushTokenToPalette(string paletteKey, string tokenPath, System.Windows.Media.Color fallback)
        {
            var color = CurrentTokens.GetColor(tokenPath, fallback);
            _paletteColors[paletteKey] = color;
            if (_colorHolders.TryGetValue(paletteKey, out var holder))
            {
                holder.Color = color;
            }
        }

        private System.Windows.Media.Color? ResolvePaletteColor(string key)
            => _paletteColors.TryGetValue(key, out var c) ? c : null;

        private void InstallLivePaletteBrushes()
        {
            var app = System.Windows.Application.Current;
            if (app?.Resources == null)
            {
                return;
            }

            var colorsDict = FindColorsDictionary(app.Resources);
            var target = colorsDict ?? app.Resources;

            foreach (var key in ThemeTokenCatalog.RequiredPaletteKeys)
            {
                System.Windows.Media.Color initial = default;
                var hasColor = false;

                if (target[key] is SolidColorBrush existing)
                {
                    initial = existing.Color;
                    hasColor = true;
                }
                else if (app.TryFindResource(key) is SolidColorBrush found)
                {
                    initial = found.Color;
                    hasColor = true;
                }

                if (!hasColor)
                {
                    continue;
                }

                var holder = new MutableColor { Color = initial };
                var liveBrush = new SolidColorBrush();
                WpfBindingOperations.SetBinding(
                    liveBrush,
                    SolidColorBrush.ColorProperty,
                    new WpfBinding(nameof(MutableColor.Color))
                    {
                        Source = holder,
                        Mode = WpfBindingMode.OneWay
                    });

                _colorHolders[key] = holder;
                _paletteColors[key] = initial;

                if (colorsDict != null && colorsDict.Contains(key))
                {
                    colorsDict[key] = liveBrush;
                }
                else
                {
                    app.Resources[key] = liveBrush;
                }
            }
        }

        private static ResourceDictionary? FindColorsDictionary(ResourceDictionary? root)
        {
            if (root == null)
            {
                return null;
            }

            foreach (var merged in root.MergedDictionaries)
            {
                if (merged?.Source != null)
                {
                    var source = merged.Source.OriginalString;
                    if (source.Contains("Themes/Colors.xaml", StringComparison.OrdinalIgnoreCase)
                        && !source.Contains("Colors.Dark", StringComparison.OrdinalIgnoreCase))
                    {
                        return merged;
                    }
                }

                var nested = FindColorsDictionary(merged);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }
    }
}
