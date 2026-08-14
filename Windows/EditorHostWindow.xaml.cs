using System;
using System.Windows;
using GitDeployPro.Services.Theme;
using MahApps.Metro.Controls;

namespace GitDeployPro.Windows
{
    public partial class EditorHostWindow : MetroWindow
    {
        public EditorHostWindow()
        {
            InitializeComponent();
            ThemeService.Instance.ThemeChanged += OnThemeChanged;
            Closed += (_, _) => ThemeService.Instance.ThemeChanged -= OnThemeChanged;
            ApplyTheme();
        }

        public void SetFileTitle(string? path)
        {
            Title = string.IsNullOrWhiteSpace(path) ? "Editor" : path.Trim();
        }

        public void ApplyTheme()
        {
            if (TryFindResource("Surface.Base") is System.Windows.Media.Brush baseBrush)
            {
                Background = baseBrush;
                EditorHost.Background = baseBrush;
            }

            if (TryFindResource("Surface.Card") is System.Windows.Media.Brush card)
            {
                WindowTitleBrush = card;
            }

            if (TryFindResource("Surface.Shell") is System.Windows.Media.Brush shell)
            {
                NonActiveWindowTitleBrush = shell;
            }

            if (TryFindResource("Border.Subtle") is System.Windows.Media.Brush border)
            {
                BorderBrush = border;
            }

            if (TryFindResource("Text.Primary") is System.Windows.Media.Brush text)
            {
                TitleForeground = text;
                Foreground = text;
            }
        }

        private void OnThemeChanged(object? sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => OnThemeChanged(sender, e));
                return;
            }

            ApplyTheme();
        }
    }
}
