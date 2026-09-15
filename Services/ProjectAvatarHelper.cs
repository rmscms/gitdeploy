using System;
using System.Windows.Media;

namespace GitDeployPro.Services
{
    public static class ProjectAvatarHelper
    {
        private static readonly string[] Palette =
        {
            "#3574F0",
            "#E05555",
            "#579A57",
            "#E59500",
            "#9B59B6",
            "#00ACC1",
            "#F06292"
        };

        public static string GetInitial(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "?";
            }

            return name.Trim()[0].ToString().ToUpperInvariant();
        }

        public static SolidColorBrush GetColor(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return new SolidColorBrush(System.Windows.Media.Colors.Gray);
            }

            var index = Math.Abs(name.GetHashCode()) % Palette.Length;
            try
            {
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(Palette[index])!;
                return new SolidColorBrush(color);
            }
            catch
            {
                return new SolidColorBrush(System.Windows.Media.Colors.Gray);
            }
        }
    }
}
