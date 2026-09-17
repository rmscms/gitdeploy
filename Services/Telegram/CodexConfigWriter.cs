using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using GitDeployPro.Services;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// Keeps %USERPROFILE%\.codex\config.toml aligned with the selected Codex provider.
    /// </summary>
    public static class CodexConfigWriter
    {
        public static string ConfigDirectory
            => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");

        public static string ConfigFilePath => Path.Combine(ConfigDirectory, "config.toml");

        public static void EnsureProviderConfig(string? providerId, string? modelSlug = null)
        {
            Directory.CreateDirectory(ConfigDirectory);
            var provider = CodexProviderCatalog.Get(providerId);
            var model = string.IsNullOrWhiteSpace(modelSlug)
                ? provider.DefaultModel
                : modelSlug.Trim();

            var path = ConfigFilePath;
            var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            var next = UpsertToml(existing, provider, model);
            File.WriteAllText(path, next, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        /// <summary>Legacy alias.</summary>
        public static void EnsureOpenRouterConfig(string? modelSlug = null)
            => EnsureProviderConfig(CodexProviderCatalog.OpenRouter, modelSlug);

        public static string UpsertToml(string existing, CodexProviderInfo provider, string model)
        {
            var text = existing ?? string.Empty;
            var providerId = provider.Id;

            text = UpsertTopLevel(text, "model_provider", QuoteToml(providerId));
            text = UpsertTopLevel(text, "model", QuoteToml(model));

            // Drop every known custom provider table so switching providers does not leave duplicates.
            foreach (var known in CodexProviderCatalog.GetAll())
            {
                text = Regex.Replace(
                    text,
                    $@"\[model_providers\.{Regex.Escape(known.Id)}(?:\.[^\]]+)?\][\s\S]*?(?=(\r?\n\[|\z))",
                    string.Empty,
                    RegexOptions.IgnoreCase);
            }

            // Also clear any leftover openrouter.auth nests from older builds.
            text = Regex.Replace(
                text,
                @"\[model_providers\.openrouter(?:\.[^\]]+)?\][\s\S]*?(?=(\r?\n\[|\z))",
                string.Empty,
                RegexOptions.IgnoreCase);

            text = Regex.Replace(text, @"(\r?\n){3,}", "\n\n").TrimEnd();

            var providerBlock =
                $"""
                 [model_providers.{providerId}]
                 name = {QuoteToml(provider.Label)}
                 base_url = {QuoteToml(provider.BaseUrl)}
                 wire_api = "responses"
                 env_key = {QuoteToml(provider.EnvKey)}
                 """;

            if (!string.IsNullOrWhiteSpace(text) && !text.EndsWith("\n", StringComparison.Ordinal))
            {
                text += "\n";
            }

            text += "\n" + providerBlock.TrimEnd() + "\n";
            return text.TrimEnd() + "\n";
        }

        private static string UpsertTopLevel(string text, string key, string valueLiteral)
        {
            var pattern = $@"(?m)^{Regex.Escape(key)}\s*=\s*.*$";
            var line = $"{key} = {valueLiteral}";
            if (Regex.IsMatch(text, pattern))
            {
                return Regex.Replace(text, pattern, line);
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return line + "\n";
            }

            return line + "\n" + text.TrimStart();
        }

        private static string QuoteToml(string value)
        {
            var escaped = (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
            return $"\"{escaped}\"";
        }

        public static string? DecryptApiKey()
        {
            try
            {
                var encrypted = new ConfigurationService().LoadGlobalConfig().OpenRouterApiKey;
                if (string.IsNullOrWhiteSpace(encrypted))
                {
                    return null;
                }

                var plain = EncryptionService.Decrypt(encrypted);
                return string.IsNullOrWhiteSpace(plain) ? null : plain.Trim();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Legacy alias.</summary>
        public static string? DecryptOpenRouterKey() => DecryptApiKey();

        public static string ResolveRuntimeApiKey(string? providerId, string? storedKey)
        {
            var provider = CodexProviderCatalog.Get(providerId);
            if (!provider.RequiresApiKey)
            {
                // Local servers ignore the key but Codex may still require the env var to be set.
                return string.IsNullOrWhiteSpace(storedKey) ? "local" : storedKey.Trim();
            }

            return (storedKey ?? string.Empty).Trim();
        }
    }
}
