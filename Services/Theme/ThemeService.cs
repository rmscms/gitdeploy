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
    /// Extensible Deploy theme registry: built-in packs + imported JSON packs.
    /// Live palette brushes stay unfrozen via Color bindings.
    /// </summary>
    public sealed class ThemeService
    {
        public const string DefaultThemeId = "default";
        public const string DarkThemeId = "dark";

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

        private readonly List<AppThemeInfo> _themes = new();
        private readonly Dictionary<string, MutableColor> _colorHolders = new(StringComparer.Ordinal);
        private readonly Dictionary<string, System.Windows.Media.Color> _paletteColors = new(StringComparer.OrdinalIgnoreCase);
        private bool _initialized;

        private ThemeService()
        {
        }

        public IReadOnlyList<AppThemeInfo> Themes => _themes;

        public string CurrentThemeId { get; private set; } = DarkThemeId;

        public ThemePack? CurrentPack { get; private set; }

        public DeployThemeTokens CurrentTokens { get; private set; } =
            DeployThemeTokens.Resolve(ThemeTokenCatalog.CreateDefaultPack(), _ => null);

        public event EventHandler? ThemeChanged;
        public event EventHandler? ThemesChanged;

        public void RegisterTheme(AppThemeInfo theme, bool replaceExisting = false)
        {
            if (theme == null || string.IsNullOrWhiteSpace(theme.Id))
            {
                throw new ArgumentException("Theme id is required.", nameof(theme));
            }

            var existing = _themes.FindIndex(t =>
                string.Equals(t.Id, theme.Id, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                if (!replaceExisting)
                {
                    throw new InvalidOperationException($"Theme '{theme.Id}' is already registered.");
                }

                _themes[existing] = theme;
            }
            else
            {
                _themes.Add(theme);
            }

            ThemesChanged?.Invoke(this, EventArgs.Empty);
        }

        public void UnregisterTheme(string themeId)
        {
            if (string.Equals(themeId, DefaultThemeId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(themeId, DarkThemeId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var removed = _themes.RemoveAll(t =>
                string.Equals(t.Id, themeId, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                ThemesChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            InstallLivePaletteBrushes();
            RegisterBuiltIns();
            ThemePackStore.Instance.EnsureSeeded();
            ReloadCustomThemes();
            CurrentTokens = DeployThemeTokens.Resolve(
                ThemeTokenCatalog.CreateDefaultPack(),
                ResolvePaletteColor);
            _initialized = true;
            CurrentThemeId = DarkThemeId;
        }

        public void ReloadCustomThemes()
        {
            _themes.RemoveAll(t => t.Source == ThemeSourceKind.JsonPack);
            foreach (var custom in ThemePackStore.Instance.LoadCustomThemes())
            {
                if (_themes.Any(t => string.Equals(t.Id, custom.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    custom.Id = custom.Id + "-" + Guid.NewGuid().ToString("N")[..6];
                }

                _themes.Add(custom);
            }

            ThemesChanged?.Invoke(this, EventArgs.Empty);
        }

        public AppThemeInfo? FindTheme(string? themeId)
        {
            if (string.IsNullOrWhiteSpace(themeId))
            {
                return null;
            }

            return _themes.FirstOrDefault(t =>
                string.Equals(t.Id, themeId, StringComparison.OrdinalIgnoreCase));
        }

        public void ApplyTheme(string? themeId)
        {
            Initialize();

            var theme = FindTheme(themeId) ?? FindTheme(DarkThemeId) ?? _themes[0];
            var pack = ResolvePack(theme);
            pack = ThemePackStore.Instance.MergeWithDefaults(pack);

            ApplyPalette(pack);
            CurrentTokens = DeployThemeTokens.Resolve(pack, ResolvePaletteColor);
            CurrentPack = pack;
            CurrentThemeId = theme.Id;
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }

        public AppThemeInfo ImportThemeFile(string path)
        {
            Initialize();
            var info = ThemePackStore.Instance.ImportFromFile(path);
            RegisterTheme(info, replaceExisting: true);
            return info;
        }

        public void DeleteCustomTheme(string themeId)
        {
            var theme = FindTheme(themeId);
            if (theme == null || theme.IsBuiltIn)
            {
                throw new InvalidOperationException("Only custom themes can be deleted.");
            }

            ThemePackStore.Instance.DeleteCustomTheme(theme.Id, theme.PackPath);
            UnregisterTheme(theme.Id);
            if (string.Equals(CurrentThemeId, themeId, StringComparison.OrdinalIgnoreCase))
            {
                ApplyTheme(DefaultThemeId);
            }
        }

        public ThemePack GetExportPack(string? themeId)
        {
            Initialize();
            var theme = FindTheme(themeId) ?? FindTheme(DarkThemeId) ?? _themes[0];
            return ThemePackStore.Instance.MergeWithDefaults(ResolvePack(theme));
        }

        public System.Windows.Media.Color GetTokenColor(string path, System.Windows.Media.Color fallback)
            => CurrentTokens.GetColor(path, fallback);

        public SolidColorBrush GetTokenBrush(string path, System.Windows.Media.Color fallback)
            => CurrentTokens.GetBrush(path, fallback);

        public string GetTokenHex(string path, string fallback)
            => CurrentTokens.GetHex(path, fallback);

        private void RegisterBuiltIns()
        {
            _themes.Clear();
            _themes.Add(new AppThemeInfo
            {
                Id = DefaultThemeId,
                DisplayName = "Default",
                Source = ThemeSourceKind.BuiltInPack,
                Pack = ThemeTokenCatalog.CreateDefaultPack(),
                ResourceUri = new Uri("pack://application:,,,/Themes/Colors.xaml")
            });
            _themes.Add(new AppThemeInfo
            {
                Id = DarkThemeId,
                DisplayName = "Dark",
                Source = ThemeSourceKind.BuiltInPack,
                Pack = ThemeTokenCatalog.CreateDarkPack(),
                ResourceUri = new Uri("pack://application:,,,/Themes/Colors.Dark.xaml")
            });
        }

        private ThemePack ResolvePack(AppThemeInfo theme)
        {
            if (theme.Pack != null)
            {
                return ClonePack(theme.Pack);
            }

            if (!string.IsNullOrWhiteSpace(theme.PackPath) && System.IO.File.Exists(theme.PackPath))
            {
                var loaded = ThemePackStore.Instance.LoadPackFromFile(theme.PackPath);
                theme.Pack = loaded;
                return ClonePack(loaded);
            }

            if (string.Equals(theme.Id, DarkThemeId, StringComparison.OrdinalIgnoreCase))
            {
                return ThemeTokenCatalog.CreateDarkPack();
            }

            return ThemeTokenCatalog.CreateDefaultPack();
        }

        private static ThemePack ClonePack(ThemePack pack)
        {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(pack);
            return Newtonsoft.Json.JsonConvert.DeserializeObject<ThemePack>(json) ?? ThemeTokenCatalog.CreateDefaultPack();
        }

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
