using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GitDeployPro.Controls;
using GitDeployPro.Services;
using GitDeployPro.Services.Update;

namespace GitDeployPro.Pages
{
    public partial class AboutPage : Page
    {
        private readonly ConfigurationService _configService = new();

        public AboutPage()
        {
            InitializeComponent();
            Loaded += (_, _) => RefreshUpdateStatus();
        }

        private void RefreshUpdateStatus()
        {
            var globalConfig = _configService.LoadGlobalConfig();
            var updateService = new AppUpdateService();
            VersionText.Text = $"Version: {updateService.GetCurrentVersion()}";
            LastCheckText.Text = globalConfig.LastUpdateCheckUtc.HasValue
                ? $"Last automatic check: {globalConfig.LastUpdateCheckUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}"
                : "Last automatic check: never";
            InstallPathText.Text = $"Install path: {AppInstallPaths.ExecutablePath}";
            RunningPathText.Text = $"Running from: {Environment.ProcessPath ?? "(unknown)"}";
        }

        private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            CheckForUpdatesButton.IsEnabled = false;
            try
            {
                var owner = Window.GetWindow(this);
                await AppUpdateCoordinator.RunManualCheckAsync(owner);
                RefreshUpdateStatus();
            }
            finally
            {
                CheckForUpdatesButton.IsEnabled = true;
            }
        }

        private void OpenInstallFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(AppInstallPaths.InstallDirectory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = AppInstallPaths.InstallDirectory,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(
                    $"Could not open install folder:\n{ex.Message}",
                    "About",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }
}
