using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using Newtonsoft.Json;

namespace GitDeployPro.Services.Localization
{
    /// <summary>
    /// App-wide EN/FA strings. Default language is English.
    /// Bind with: {Binding [key], Source={x:Static loc:LocalizationService.Instance}}
    /// or {loc:T key}
    /// </summary>
    public sealed class LocalizationService : INotifyPropertyChanged
    {
        public const string English = "en";
        public const string Persian = "fa";

        private static readonly Lazy<LocalizationService> Lazy = new(() => new LocalizationService());
        public static LocalizationService Instance => Lazy.Value;

        private readonly Dictionary<string, Dictionary<string, string>> _packs = new(StringComparer.OrdinalIgnoreCase);
        private string _language = English;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler? LanguageChanged;

        public string Language => _language;

        /// <summary>Layout stays LTR; Persian only affects tooltip tip keys.</summary>
        public bool IsRtl => false;

        /// <summary>Indexer for WPF bindings: [deploy.tip.terminal]</summary>
        public string this[string key] => Get(key);

        private LocalizationService()
        {
            LoadPack(English, "en.json");
            LoadPack(Persian, "fa.json");
            EnsureMinimumKeys();
        }

        public void InitializeFromConfig()
        {
            try
            {
                var lang = new ConfigurationService().LoadGlobalConfig().UiLanguage;
                SetLanguage(Normalize(lang), persist: false, raiseUi: false);
            }
            catch
            {
                SetLanguage(English, persist: false, raiseUi: false);
            }
        }

        public IReadOnlyList<LanguageOption> GetLanguageOptions() =>
            new[]
            {
                // Chrome labels stay English even when tip language is Persian.
                new LanguageOption(English, Lookup(English, "lang.en") ?? "English"),
                new LanguageOption(Persian, Lookup(English, "lang.fa") ?? "Persian")
            };

        public void SetLanguage(string languageCode, bool persist = true, bool raiseUi = true)
        {
            var normalized = Normalize(languageCode);
            if (string.Equals(_language, normalized, StringComparison.OrdinalIgnoreCase) && raiseUi)
            {
                ApplyFlowDirection();
                return;
            }

            _language = normalized;
            ApplyCulture();
            if (persist)
            {
                try
                {
                    new ConfigurationService().UpdateGlobalConfig(cfg => cfg.UiLanguage = _language);
                }
                catch
                {
                }
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));

            if (raiseUi)
            {
                ApplyFlowDirection();
                LanguageChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public string Get(string key, params object[] args)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            // Menus/labels always English; only tooltip tip keys follow selected language.
            var packLanguage = IsTooltipKey(key) ? _language : English;
            var text = Lookup(packLanguage, key) ?? Lookup(English, key) ?? key;
            if (args is { Length: > 0 })
            {
                try
                {
                    return string.Format(CultureInfo.InvariantCulture, text, args);
                }
                catch
                {
                    return text;
                }
            }

            return text;
        }

        private static bool IsTooltipKey(string key)
        {
            return key.Contains(".tip.", StringComparison.OrdinalIgnoreCase);
        }

        public void ApplyFlowDirection(DependencyObject? root = null)
        {
            // Always LTR — Persian must not flip chrome layout.
            const System.Windows.FlowDirection direction = System.Windows.FlowDirection.LeftToRight;
            try
            {
                if (root != null)
                {
                    if (root is FrameworkElement fe)
                    {
                        fe.FlowDirection = direction;
                    }

                    return;
                }

                if (System.Windows.Application.Current == null)
                {
                    return;
                }

                foreach (Window window in System.Windows.Application.Current.Windows)
                {
                    window.FlowDirection = direction;
                }
            }
            catch
            {
            }
        }

        private string? Lookup(string language, string key)
        {
            if (_packs.TryGetValue(language, out var pack) &&
                pack.TryGetValue(key, out var value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return null;
        }

        private static string Normalize(string? languageCode)
        {
            if (string.IsNullOrWhiteSpace(languageCode))
            {
                return English;
            }

            var code = languageCode.Trim().ToLowerInvariant();
            if (code.StartsWith("fa", StringComparison.Ordinal) || code is "persian" or "farsi")
            {
                return Persian;
            }

            return English;
        }

        private void ApplyCulture()
        {
            try
            {
                // Keep formatting culture stable (English). Tip language does not flip UI culture.
                var culture = CultureInfo.GetCultureInfo("en-US");
                Thread.CurrentThread.CurrentUICulture = culture;
                CultureInfo.DefaultThreadCurrentUICulture = culture;
            }
            catch
            {
            }
        }

        private void LoadPack(string language, string fileName)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var resourceName = Array.Find(
                    asm.GetManifestResourceNames(),
                    n => n.EndsWith($".Localization.{fileName}", StringComparison.OrdinalIgnoreCase)
                         || n.EndsWith($"Resources.Localization.{fileName}", StringComparison.OrdinalIgnoreCase)
                         || n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

                Dictionary<string, string>? pack = null;
                if (!string.IsNullOrWhiteSpace(resourceName))
                {
                    using var stream = asm.GetManifestResourceStream(resourceName);
                    if (stream != null)
                    {
                        using var reader = new StreamReader(stream);
                        pack = JsonConvert.DeserializeObject<Dictionary<string, string>>(reader.ReadToEnd());
                    }
                }

                if (pack == null)
                {
                    var path = Path.Combine(AppContext.BaseDirectory, "Resources", "Localization", fileName);
                    if (File.Exists(path))
                    {
                        pack = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path));
                    }
                }

                _packs[language] = pack ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                _packs[language] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void EnsureMinimumKeys()
        {
            // Hard fallback if embedded JSON missing at runtime.
            void Put(string lang, string key, string value)
            {
                if (!_packs.TryGetValue(lang, out var pack))
                {
                    pack = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    _packs[lang] = pack;
                }

                if (!pack.ContainsKey(key))
                {
                    pack[key] = value;
                }
            }

            Put(English, "lang.en", "English");
            Put(English, "lang.fa", "Persian (فارسی)");
            Put(Persian, "lang.en", "English");
            Put(Persian, "lang.fa", "فارسی");
        }

        public sealed record LanguageOption(string Code, string DisplayName)
        {
            // ComboBox selection box falls back to ToString() when DisplayMemberPath fails on records.
            public override string ToString() => DisplayName;
        }
    }

    public static class Loc
    {
        public static string T(string key, params object[] args) =>
            LocalizationService.Instance.Get(key, args);
    }
}
