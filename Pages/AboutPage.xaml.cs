using System.Windows;
using System.Windows.Controls;
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
            VersionText.Text = $"Version: {new AppUpdateService().GetCurrentVersion()}";
            LastCheckText.Text = globalConfig.LastUpdateCheckUtc.HasValue
                ? $"Last automatic check: {globalConfig.LastUpdateCheckUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}"
                : "Last automatic check: never";
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
    }
}
