using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GitDeployPro.Services.Theme
{
    public sealed class ThemePackValidationResult
    {
        public bool IsValid => Errors.Count == 0;
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();

        public string FormatMessage()
        {
            var sb = new StringBuilder();
            foreach (var error in Errors)
            {
                sb.AppendLine("Error: " + error);
            }

            foreach (var warning in Warnings)
            {
                sb.AppendLine("Warning: " + warning);
            }

            return sb.ToString().TrimEnd();
        }
    }

    public static class ThemePackValidator
    {
        public static ThemePackValidationResult Validate(ThemePack? pack)
        {
            var result = new ThemePackValidationResult();
            if (pack == null)
            {
                result.Errors.Add("Theme pack is null.");
                return result;
            }

            if (string.IsNullOrWhiteSpace(pack.DisplayName))
            {
                result.Errors.Add("displayName is required.");
            }

            if (string.IsNullOrWhiteSpace(pack.Id))
            {
                result.Warnings.Add("id is missing; filename stem will be used.");
            }

            if (!string.IsNullOrWhiteSpace(pack.Schema)
                && !string.Equals(pack.Schema, ThemeTokenCatalog.SchemaId, StringComparison.OrdinalIgnoreCase))
            {
                result.Warnings.Add($"Unexpected schema '{pack.Schema}'. Expected '{ThemeTokenCatalog.SchemaId}'.");
            }

            if (pack.Palette == null || pack.Palette.Count == 0)
            {
                result.Errors.Add("palette is required and must contain colors.");
            }
            else
            {
                foreach (var key in ThemeTokenCatalog.RequiredPaletteKeys)
                {
                    if (!pack.Palette.ContainsKey(key))
                    {
                        result.Warnings.Add($"palette.{key} is missing; Default value will be used.");
                    }
                }

                foreach (var kv in pack.Palette)
                {
                    ValidateColorOrRef(result, "palette." + kv.Key, kv.Value, allowRef: false);
                    if (!ThemeTokenCatalog.RequiredPaletteKeys.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        result.Warnings.Add($"Unknown palette key '{kv.Key}'.");
                    }
                }
            }

            ValidateMap(result, "deploy.shell", pack.Deploy?.Shell);
            ValidateMap(result, "deploy.header", pack.Deploy?.Header);
            ValidateMap(result, "deploy.branches", pack.Deploy?.Branches);
            ValidateMap(result, "deploy.changedFiles", pack.Deploy?.ChangedFiles);
            ValidateMap(result, "deploy.directUpload", pack.Deploy?.DirectUpload);
            ValidateMap(result, "deploy.logs", pack.Deploy?.Logs);
            ValidateMap(result, "deploy.editor", pack.Deploy?.Editor, allowNonColorKeys: new[] { "monacoTheme" });
            ValidateMap(result, "deploy.diff", pack.Deploy?.Diff);
            ValidateMap(result, "deploy.ftp.chrome", pack.Deploy?.Ftp?.Chrome);

            if (pack.Deploy?.Ftp?.FileTypes != null)
            {
                foreach (var kv in pack.Deploy.Ftp.FileTypes)
                {
                    if (!ThemeTokenCatalog.FileTypeKeys.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        result.Warnings.Add($"Unknown ftp.fileTypes key '{kv.Key}'.");
                    }

                    ValidateColorOrRef(result, $"deploy.ftp.fileTypes.{kv.Key}.icon", kv.Value?.Icon, allowRef: true);
                    ValidateColorOrRef(result, $"deploy.ftp.fileTypes.{kv.Key}.badgeBg", kv.Value?.BadgeBg, allowRef: true);
                    ValidateColorOrRef(result, $"deploy.ftp.fileTypes.{kv.Key}.badgeBorder", kv.Value?.BadgeBorder, allowRef: true);
                    ValidateColorOrRef(result, $"deploy.ftp.fileTypes.{kv.Key}.badgeFg", kv.Value?.BadgeFg, allowRef: true);
                }
            }

            var terminal = pack.Deploy?.Terminal;
            if (terminal != null)
            {
                ValidateColorOrRef(result, "deploy.terminal.hostBackground", terminal.HostBackground, allowRef: true);
                ValidateColorOrRef(result, "deploy.terminal.statusConnected", terminal.StatusConnected, allowRef: true);
                ValidateColorOrRef(result, "deploy.terminal.statusDisconnected", terminal.StatusDisconnected, allowRef: true);
                ValidateColorOrRef(result, "deploy.terminal.statusConnecting", terminal.StatusConnecting, allowRef: true);
                ValidateColorOrRef(result, "deploy.terminal.statusError", terminal.StatusError, allowRef: true);
                ValidateColorOrRef(result, "deploy.terminal.presetsDrawerBackground", terminal.PresetsDrawerBackground, allowRef: true);
                ValidateColorOrRef(result, "deploy.terminal.presetsDrawerBorder", terminal.PresetsDrawerBorder, allowRef: true);
                ValidateColorOrRef(result, "deploy.terminal.presetsHeaderForeground", terminal.PresetsHeaderForeground, allowRef: true);
                ValidateColorOrRef(result, "deploy.terminal.presetsChipBackground", terminal.PresetsChipBackground, allowRef: true);
                if (terminal.Xterm != null)
                {
                    foreach (var kv in terminal.Xterm)
                    {
                        var path = "deploy.terminal.xterm." + kv.Key;
                        if (kv.Value != null && kv.Value.StartsWith("rgba", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        ValidateColorOrRef(result, path, kv.Value, allowRef: true);
                    }
                }
            }

            return result;
        }

        private static void ValidateMap(
            ThemePackValidationResult result,
            string prefix,
            Dictionary<string, string>? map,
            string[]? allowNonColorKeys = null)
        {
            if (map == null)
            {
                return;
            }

            foreach (var kv in map)
            {
                var path = prefix + "." + kv.Key;
                if (allowNonColorKeys != null
                    && allowNonColorKeys.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(kv.Value))
                    {
                        result.Errors.Add($"{path} is empty.");
                    }

                    continue;
                }

                ValidateColorOrRef(result, path, kv.Value, allowRef: true);
            }
        }

        private static void ValidateColorOrRef(
            ThemePackValidationResult result,
            string path,
            string? value,
            bool allowRef)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (ThemeColorParser.IsPaletteRef(value))
            {
                if (!allowRef)
                {
                    result.Errors.Add($"{path} cannot use palette references.");
                    return;
                }

                var key = ThemeColorParser.GetPaletteRefKey(value);
                if (string.IsNullOrWhiteSpace(key)
                    || !ThemeTokenCatalog.RequiredPaletteKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    result.Errors.Add($"{path} references unknown palette key '@{key}'.");
                }

                return;
            }

            if (value.StartsWith("rgba", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!ThemeColorParser.TryParse(value, out _))
            {
                result.Errors.Add($"{path} has invalid color '{value}'.");
            }
        }
    }
}
