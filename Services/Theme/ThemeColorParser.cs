using System;
using System.Globalization;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;

namespace GitDeployPro.Services.Theme
{
    public static class ThemeColorParser
    {
        public static bool TryParse(string? value, out MediaColor color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var text = value.Trim();
            if (text.StartsWith("@", StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                var converted = MediaColorConverter.ConvertFromString(text);
                if (converted is MediaColor c)
                {
                    color = c;
                    return true;
                }
            }
            catch
            {
                // fall through
            }

            // Support RRGGBB without #
            if (text.Length == 6 && int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            {
                try
                {
                    var converted = MediaColorConverter.ConvertFromString("#" + text);
                    if (converted is MediaColor c)
                    {
                        color = c;
                        return true;
                    }
                }
                catch
                {
                    // ignored
                }
            }

            return false;
        }

        public static string ToHex(MediaColor color)
        {
            if (color.A == 255)
            {
                return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            }

            return $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        public static bool IsPaletteRef(string? value)
            => !string.IsNullOrWhiteSpace(value) && value.TrimStart().StartsWith("@", StringComparison.Ordinal);

        public static string? GetPaletteRefKey(string? value)
        {
            if (!IsPaletteRef(value))
            {
                return null;
            }

            return value!.Trim().TrimStart('@').Trim();
        }
    }
}
