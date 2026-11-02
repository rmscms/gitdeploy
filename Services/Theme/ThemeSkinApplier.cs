using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Markup;
using GitDeployPro.Controls;
using GitDeployPro.Services.Localization;
using WpfApplication = System.Windows.Application;

namespace GitDeployPro.Services.Theme
{
    /// <summary>
    /// Skin layer of a theme pack: an optional <c>Skin.xaml</c> ResourceDictionary that redefines whole style
    /// families (buttons, cards, tabs, inputs, badges, ...). Colours / metrics / icons stay live; the skin is merged
    /// once at startup (before any window resolves its StaticResources), so switching to a pack with a different
    /// skin asks for a restart.
    /// Lookup order: user skin <c>%AppData%\GitDeployPro\Skins\&lt;id&gt;\Skin.xaml</c>, then the bundled
    /// <c>Themes/Skins/&lt;id&gt;/Skin.xaml</c>.
    /// </summary>
    public static class ThemeSkinApplier
    {
        private static bool _startupApplied;

        /// <summary>Skin currently merged into Application.Resources (null = default skin).</summary>
        public static string? AppliedSkinId { get; private set; }

        /// <summary>Skin requested by the selected pack (null = default skin).</summary>
        public static string? RequestedSkinId { get; private set; }

        /// <summary>True when the selected pack wants a different skin than the one loaded at startup.</summary>
        public static bool RestartRequired =>
            _startupApplied && !string.Equals(
                AppliedSkinId ?? string.Empty,
                RequestedSkinId ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);

        public static string UserSkinsRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GitDeployPro",
            "Skins");

        public static void Apply(ThemePack pack)
        {
            var requested = NormalizeId(pack.Skin);
            RequestedSkinId = requested;

            if (_startupApplied)
            {
                return;
            }

            _startupApplied = true;
            if (requested == null)
            {
                return;
            }

            var app = WpfApplication.Current;
            if (app?.Resources == null)
            {
                return;
            }

            try
            {
                var dictionary = LoadSkin(requested);
                if (dictionary == null)
                {
                    return;
                }

                // Merged LAST: lookups walk merged dictionaries from the end, so skin keys win over Controls.xaml.
                app.Resources.MergedDictionaries.Add(dictionary);
                AppliedSkinId = requested;
            }
            catch (Exception ex)
            {
                // A broken skin must never stop the app from starting: fall back to the default skin.
                Debug.WriteLine("Skin '" + requested + "' failed to load: " + ex.Message);
                AppliedSkinId = null;
            }
        }

        public static void Validate(ThemePack pack, IList<string> errors, IList<string> warnings)
        {
            var id = NormalizeId(pack.Skin);
            if (string.IsNullOrWhiteSpace(pack.Skin))
            {
                return;
            }

            if (id == null)
            {
                errors.Add("skin: only letters, digits, '-' and '_' are allowed.");
                return;
            }

            if (!SkinExists(id))
            {
                warnings.Add($"skin '{id}' was not found (expected Themes/Skins/{id}/Skin.xaml or %AppData%\\GitDeployPro\\Skins\\{id}\\Skin.xaml); the default skin is used.");
            }
        }

        /// <summary>Asks to restart when the selected theme needs a different skin than the running one.</summary>
        public static void PromptRestartIfNeeded()
        {
            if (!RestartRequired)
            {
                return;
            }

            var answer = ModernMessageBox.Show(
                Loc.T("theme.skinRestart.body"),
                Loc.T("theme.skinRestart.title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (!answer)
            {
                return;
            }

            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
                }
            }
            catch
            {
                return;
            }

            WpfApplication.Current?.Shutdown();
        }

        private static string? NormalizeId(string? id)
        {
            var trimmed = (id ?? string.Empty).Trim();
            return trimmed.Length == 0 || trimmed.Any(c => !(char.IsLetterOrDigit(c) || c == '-' || c == '_'))
                ? null
                : trimmed;
        }

        private static bool SkinExists(string id)
        {
            if (File.Exists(Path.Combine(UserSkinsRoot, id, "Skin.xaml")))
            {
                return true;
            }

            try
            {
                return WpfApplication.GetResourceStream(BundledUri(id)) != null;
            }
            catch
            {
                return false;
            }
        }

        private static Uri BundledUri(string id)
            => new($"pack://application:,,,/Themes/Skins/{id}/Skin.xaml", UriKind.Absolute);

        private static ResourceDictionary? LoadSkin(string id)
        {
            var userFile = Path.Combine(UserSkinsRoot, id, "Skin.xaml");
            if (File.Exists(userFile))
            {
                using var stream = File.OpenRead(userFile);
                return XamlReader.Load(stream) as ResourceDictionary;
            }

            return SkinExists(id) ? new ResourceDictionary { Source = BundledUri(id) } : null;
        }
    }
}
