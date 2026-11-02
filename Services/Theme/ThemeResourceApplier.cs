using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace GitDeployPro.Services.Theme
{
    /// <summary>
    /// Applies the non-colour parts of a theme pack to the running app:
    /// <c>icons</c> (path data for <c>Icon.&lt;name&gt;</c> geometries) and <c>metrics</c>
    /// (CornerRadius / Thickness / double resources such as <c>Radius.Card</c>).
    /// Overrides are stored at the top level, so switching to a theme without them simply removes the override.
    /// </summary>
    public static class ThemeResourceApplier
    {
        private static readonly HashSet<string> TouchedIcons = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> TouchedMetrics = new(StringComparer.OrdinalIgnoreCase);

        public static void Apply(ThemePack pack)
        {
            var app = System.Windows.Application.Current;
            if (app?.Resources == null)
            {
                return;
            }

            ApplyMap(app.Resources, pack.Icons, "Icon.", TouchedIcons, ParseIcon);
            ApplyMap(app.Resources, pack.Metrics, string.Empty, TouchedMetrics, ParseMetric);
        }

        public static void Validate(ThemePack pack, IList<string> errors, IList<string> warnings)
        {
            if (pack.Icons != null)
            {
                foreach (var kv in pack.Icons)
                {
                    if (ParseIcon(kv.Key, kv.Value) == null)
                    {
                        errors.Add($"icons.{kv.Key}: invalid path data.");
                    }
                }
            }

            if (pack.Metrics != null)
            {
                foreach (var kv in pack.Metrics)
                {
                    if (ParseMetric(kv.Key, kv.Value) == null)
                    {
                        errors.Add($"metrics.{kv.Key}: expected a number (or \"l,t,r,b\" for Spacing.*).");
                    }
                }
            }
        }

        private static void ApplyMap(
            ResourceDictionary resources,
            Dictionary<string, string>? values,
            string keyPrefix,
            HashSet<string> touched,
            Func<string, string, object?> parse)
        {
            // restore anything a previous theme overrode
            foreach (var key in new List<string>(touched))
            {
                if (values != null && values.ContainsKey(StripPrefix(key, keyPrefix)))
                {
                    continue;
                }

                // overrides live in the top-level dictionary: removing them re-exposes the merged defaults
                resources.Remove(key);
                touched.Remove(key);
            }

            if (values == null)
            {
                return;
            }

            foreach (var kv in values)
            {
                var parsed = parse(kv.Key, kv.Value);
                if (parsed == null)
                {
                    continue;
                }

                var resourceKey = keyPrefix + kv.Key;
                resources[resourceKey] = parsed;
                touched.Add(resourceKey);
            }
        }

        private static string StripPrefix(string key, string prefix)
            => prefix.Length > 0 && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? key[prefix.Length..] : key;

        private static object? ParseIcon(string name, string data)
        {
            try
            {
                var geometry = Geometry.Parse(data);
                geometry.Freeze();
                return geometry;
            }
            catch
            {
                return null;
            }
        }

        private static object? ParseMetric(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var parts = value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var numbers = new double[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
                {
                    return null;
                }
            }

            if (key.StartsWith("Radius.", StringComparison.OrdinalIgnoreCase))
            {
                return numbers.Length == 4
                    ? new CornerRadius(numbers[0], numbers[1], numbers[2], numbers[3])
                    : new CornerRadius(numbers[0]);
            }

            if (key.StartsWith("Spacing.", StringComparison.OrdinalIgnoreCase))
            {
                return numbers.Length switch
                {
                    1 => new Thickness(numbers[0]),
                    2 => new Thickness(numbers[0], numbers[1], numbers[0], numbers[1]),
                    4 => new Thickness(numbers[0], numbers[1], numbers[2], numbers[3]),
                    _ => null
                };
            }

            return numbers.Length == 1 ? numbers[0] : null;
        }
    }
}
