using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GitDeployPro.Services;
using Newtonsoft.Json;

namespace GitDeployPro.Services.Theme
{
    public sealed class ThemePackStore
    {
        private static readonly Lazy<ThemePackStore> LazyInstance = new(() => new ThemePackStore());
        public static ThemePackStore Instance => LazyInstance.Value;

        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore
        };

        public string ThemesDirectory
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "GitDeployPro",
                    "Themes");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public const string GuideFileName = "THEME_PACK_GUIDE.md";

        /// <summary>
        /// Path to the theme guide in the user Themes folder (seeded from the app install).
        /// </summary>
        public string GuidePath => Path.Combine(ThemesDirectory, GuideFileName);

        public void EnsureSeeded()
        {
            WritePackIfMissing("Default.json", ThemeTokenCatalog.CreateDefaultPack());
            WritePackIfMissing("Dark.json", ThemeTokenCatalog.CreateDarkPack());

            // Prefer repo/output templates when present (overwrite only if AppData file missing).
            var bundled = Path.Combine(AppContext.BaseDirectory, "Themes", "Packs");
            if (Directory.Exists(bundled))
            {
                foreach (var file in Directory.EnumerateFiles(bundled, "*.json"))
                {
                    var dest = Path.Combine(ThemesDirectory, Path.GetFileName(file));
                    if (!File.Exists(dest))
                    {
                        File.Copy(file, dest, overwrite: false);
                    }
                }

                // Keep the MD guide next to user themes; refresh from the install copy when available.
                SeedGuideFromBundled(bundled);
            }
        }

        private void SeedGuideFromBundled(string bundledPacksDir)
        {
            var bundledGuide = Path.Combine(bundledPacksDir, GuideFileName);
            if (!File.Exists(bundledGuide))
            {
                return;
            }

            try
            {
                File.Copy(bundledGuide, GuidePath, overwrite: true);
            }
            catch
            {
                // Ignore locked/readonly guide copies
            }
        }

        public IReadOnlyList<AppThemeInfo> LoadCustomThemes()
        {
            EnsureSeeded();
            var results = new List<AppThemeInfo>();
            foreach (var file in Directory.EnumerateFiles(ThemesDirectory, "*.json")
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var fileName = Path.GetFileName(file);
                // Skip seed templates that mirror built-ins; built-ins are registered separately.
                if (string.Equals(fileName, "Default.json", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(fileName, "Dark.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var pack = LoadPackFromFile(file);
                    var validation = ThemePackValidator.Validate(pack);
                    if (!validation.IsValid)
                    {
                        continue;
                    }

                    var id = string.IsNullOrWhiteSpace(pack.Id)
                        ? SanitizeId(Path.GetFileNameWithoutExtension(file))
                        : SanitizeId(pack.Id);

                    results.Add(new AppThemeInfo
                    {
                        Id = id,
                        DisplayName = string.IsNullOrWhiteSpace(pack.DisplayName)
                            ? Path.GetFileNameWithoutExtension(file)
                            : pack.DisplayName.Trim(),
                        Source = ThemeSourceKind.JsonPack,
                        PackPath = file,
                        Pack = pack
                    });
                }
                catch
                {
                    // Skip corrupt packs
                }
            }

            return results;
        }

        public AppThemeInfo ImportFromFile(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                throw new FileNotFoundException("Theme file not found.", sourcePath);
            }

            var pack = LoadPackFromFile(sourcePath);
            if (string.IsNullOrWhiteSpace(pack.DisplayName))
            {
                pack.DisplayName = Path.GetFileNameWithoutExtension(sourcePath);
            }

            var validation = ThemePackValidator.Validate(pack);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException(validation.FormatMessage());
            }

            var stem = SanitizeId(Path.GetFileNameWithoutExtension(sourcePath));
            if (string.Equals(stem, "default", StringComparison.OrdinalIgnoreCase)
                || string.Equals(stem, "dark", StringComparison.OrdinalIgnoreCase))
            {
                stem += "-custom";
            }

            pack.Id = string.IsNullOrWhiteSpace(pack.Id) ? stem : SanitizeId(pack.Id);
            if (string.Equals(pack.Id, ThemeService.DefaultThemeId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pack.Id, ThemeService.DarkThemeId, StringComparison.OrdinalIgnoreCase))
            {
                pack.Id = stem;
            }

            var destName = stem + ".json";
            var destPath = Path.Combine(ThemesDirectory, destName);
            var n = 1;
            while (File.Exists(destPath)
                   && !string.Equals(Path.GetFullPath(destPath), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
            {
                destName = $"{stem}-{n}.json";
                destPath = Path.Combine(ThemesDirectory, destName);
                n++;
            }

            File.WriteAllText(destPath, JsonConvert.SerializeObject(pack, JsonSettings));

            var config = new ConfigurationService();
            config.UpdateGlobalConfig(cfg =>
            {
                cfg.CustomThemeFiles ??= new List<string>();
                if (!cfg.CustomThemeFiles.Contains(destName, StringComparer.OrdinalIgnoreCase))
                {
                    cfg.CustomThemeFiles.Add(destName);
                }
            });

            return new AppThemeInfo
            {
                Id = pack.Id,
                DisplayName = pack.DisplayName,
                Source = ThemeSourceKind.JsonPack,
                PackPath = destPath,
                Pack = pack
            };
        }

        public void ExportTheme(ThemePack pack, string destPath)
        {
            File.WriteAllText(destPath, JsonConvert.SerializeObject(pack, JsonSettings));
        }

        public void DeleteCustomTheme(string themeId, string? packPath)
        {
            if (string.Equals(themeId, ThemeService.DefaultThemeId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(themeId, ThemeService.DarkThemeId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Built-in themes cannot be deleted.");
            }

            if (!string.IsNullOrWhiteSpace(packPath) && File.Exists(packPath))
            {
                File.Delete(packPath);
                var fileName = Path.GetFileName(packPath);
                new ConfigurationService().UpdateGlobalConfig(cfg =>
                {
                    cfg.CustomThemeFiles?.RemoveAll(f =>
                        string.Equals(f, fileName, StringComparison.OrdinalIgnoreCase));
                });
            }
        }

        public ThemePack LoadPackFromFile(string path)
        {
            var json = File.ReadAllText(path);
            var pack = JsonConvert.DeserializeObject<ThemePack>(json)
                       ?? throw new InvalidOperationException("Theme JSON deserialized to null.");
            return pack;
        }

        public ThemePack MergeWithDefaults(ThemePack pack)
        {
            var defaults = string.Equals(pack.BasedOn, "dark", StringComparison.OrdinalIgnoreCase)
                ? ThemeTokenCatalog.CreateDarkPack()
                : ThemeTokenCatalog.CreateDefaultPack();

            foreach (var key in ThemeTokenCatalog.RequiredPaletteKeys)
            {
                if (!pack.Palette.ContainsKey(key) && defaults.Palette.TryGetValue(key, out var value))
                {
                    pack.Palette[key] = value;
                }
            }

            pack.Deploy ??= new ThemePackDeploySection();
            pack.Deploy.Shell = MergeDict(pack.Deploy.Shell, defaults.Deploy.Shell);
            pack.Deploy.Header = MergeDict(pack.Deploy.Header, defaults.Deploy.Header);
            pack.Deploy.Branches = MergeDict(pack.Deploy.Branches, defaults.Deploy.Branches);
            pack.Deploy.ChangedFiles = MergeDict(pack.Deploy.ChangedFiles, defaults.Deploy.ChangedFiles);
            pack.Deploy.DirectUpload = MergeDict(pack.Deploy.DirectUpload, defaults.Deploy.DirectUpload);
            pack.Deploy.Logs = MergeDict(pack.Deploy.Logs, defaults.Deploy.Logs);
            pack.Deploy.Editor = MergeDict(pack.Deploy.Editor, defaults.Deploy.Editor);
            pack.Deploy.Diff = MergeDict(pack.Deploy.Diff, defaults.Deploy.Diff);
            pack.Deploy.Ftp ??= new ThemePackFtpSection();
            pack.Deploy.Ftp.Chrome = MergeDict(pack.Deploy.Ftp.Chrome, defaults.Deploy.Ftp.Chrome);
            pack.Deploy.Ftp.FileTypes ??= new Dictionary<string, ThemePackFileTypeVisual>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in defaults.Deploy.Ftp.FileTypes)
            {
                if (!pack.Deploy.Ftp.FileTypes.ContainsKey(kv.Key))
                {
                    pack.Deploy.Ftp.FileTypes[kv.Key] = kv.Value;
                }
            }

            pack.Deploy.Terminal ??= defaults.Deploy.Terminal;
            pack.Deploy.Terminal.Xterm ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in defaults.Deploy.Terminal.Xterm)
            {
                if (!pack.Deploy.Terminal.Xterm.ContainsKey(kv.Key))
                {
                    pack.Deploy.Terminal.Xterm[kv.Key] = kv.Value;
                }
            }

            if (pack.Deploy.Terminal.TextPresets == null || pack.Deploy.Terminal.TextPresets.Count == 0)
            {
                pack.Deploy.Terminal.TextPresets = defaults.Deploy.Terminal.TextPresets;
            }

            pack.Deploy.Terminal.HostBackground ??= defaults.Deploy.Terminal.HostBackground;
            pack.Deploy.Terminal.StatusConnected ??= defaults.Deploy.Terminal.StatusConnected;
            pack.Deploy.Terminal.StatusDisconnected ??= defaults.Deploy.Terminal.StatusDisconnected;
            pack.Deploy.Terminal.StatusConnecting ??= defaults.Deploy.Terminal.StatusConnecting;
            pack.Deploy.Terminal.StatusError ??= defaults.Deploy.Terminal.StatusError;

            return pack;
        }

        private void WritePackIfMissing(string fileName, ThemePack pack)
        {
            var path = Path.Combine(ThemesDirectory, fileName);
            if (!File.Exists(path))
            {
                File.WriteAllText(path, JsonConvert.SerializeObject(pack, JsonSettings));
            }
        }

        private static Dictionary<string, string> MergeDict(Dictionary<string, string>? target, Dictionary<string, string> source)
        {
            target ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in source)
            {
                if (!target.ContainsKey(kv.Key))
                {
                    target[kv.Key] = kv.Value;
                }
            }

            return target;
        }

        public static string SanitizeId(string value)
        {
            var cleaned = Regex.Replace(value.Trim().ToLowerInvariant(), @"[^a-z0-9_\-]+", "-");
            cleaned = Regex.Replace(cleaned, @"-+", "-").Trim('-');
            return string.IsNullOrWhiteSpace(cleaned) ? "theme" : cleaned;
        }
    }
}
