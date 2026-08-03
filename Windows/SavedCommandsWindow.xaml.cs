using System;
using System.Windows;
using GitDeployPro.Controls;
using GitDeployPro.Services.Theme;
using MahApps.Metro.Controls;

namespace GitDeployPro.Windows
{
    public partial class SavedCommandsWindow : MetroWindow
    {
        public event EventHandler? CloseRequested;
        public event EventHandler<string>? PresentationModeRequested;

        public SavedCommandsWindow()
        {
            InitializeComponent();
            Panel.InjectCommand = command => InjectCommand?.Invoke(command);
            Panel.CloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
            Panel.PresentationModeRequested += (_, mode) => PresentationModeRequested?.Invoke(this, mode);
            ThemeService.Instance.ThemeChanged += OnThemeChanged;
            Closed += (_, _) => ThemeService.Instance.ThemeChanged -= OnThemeChanged;
            Panel.SetPresentationMode(SavedCommandsPanel.ModeFloat);
            ApplyTheme();
        }

        public Action<string>? InjectCommand { get; set; }

        public void SetPresentationMode(string mode) => Panel.SetPresentationMode(mode);

        public void Reload() => Panel.Reload();

        public void ApplyTheme()
        {
            Panel.ApplyTheme();

            var theme = ThemeService.Instance;
            var card = theme.GetTokenBrush(
                "terminal.presetsDrawerBackground",
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0A0A0A")!);
            var border = theme.GetTokenBrush(
                "terminal.presetsDrawerBorder",
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#333333")!);
            var text = theme.GetTokenBrush(
                "terminal.presetsHeaderForeground",
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFFFFF")!);

            Background = card;
            WindowTitleBrush = card;
            NonActiveWindowTitleBrush = card;
            BorderBrush = border;
            TitleForeground = text;
            Foreground = text;
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
