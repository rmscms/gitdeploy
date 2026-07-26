using System.Windows;
using GitDeployPro.Services.Update;

namespace GitDeployPro.Windows
{
    public enum UpdateDialogChoice
    {
        Later,
        UpdateNow,
        Exit
    }

    public partial class UpdateAvailableWindow : Window
    {
        public UpdateDialogChoice Choice { get; private set; } = UpdateDialogChoice.Later;

        public UpdateAvailableWindow(UpdateCheckResult result)
        {
            InitializeComponent();

            var current = result.CurrentVersion?.ToString() ?? "?";
            var remote = result.RemoteVersion?.ToString() ?? result.Manifest?.Version ?? "?";
            var notes = string.IsNullOrWhiteSpace(result.Manifest?.ReleaseNotes)
                ? "No release notes provided."
                : result.Manifest!.ReleaseNotes.Trim();

            if (result.IsMandatory)
            {
                TitleText.Text = "Critical update required";
                VersionText.Text =
                    $"Version {current} → {remote}\n" +
                    "Download starts in the background so you can keep working, then Restart to install.\n" +
                    "You must install this update before continuing long-term.";
                VersionText.Foreground = TryFindResource("Status.Warning") as System.Windows.Media.Brush
                    ?? System.Windows.Media.Brushes.Orange;
                SecondaryButton.Content = "Exit";
                PrimaryButton.Content = "Download update";
                Choice = UpdateDialogChoice.Exit;
                Closing += (_, e) =>
                {
                    // Closing the window without choosing Update means Exit for mandatory updates.
                    if (Choice != UpdateDialogChoice.UpdateNow)
                    {
                        Choice = UpdateDialogChoice.Exit;
                    }
                };
            }
            else
            {
                TitleText.Text = "Update available";
                VersionText.Text =
                    $"Version {current} → {remote}\n" +
                    "Download runs in the background. Keep using the app, then Restart when ready.";
                SecondaryButton.Content = "Later";
                PrimaryButton.Content = "Download update";
            }

            NotesText.Text = notes;
        }

        private void PrimaryButton_Click(object sender, RoutedEventArgs e)
        {
            Choice = UpdateDialogChoice.UpdateNow;
            DialogResult = true;
            Close();
        }

        private void SecondaryButton_Click(object sender, RoutedEventArgs e)
        {
            Choice = SecondaryButton.Content?.ToString() == "Exit"
                ? UpdateDialogChoice.Exit
                : UpdateDialogChoice.Later;
            DialogResult = false;
            Close();
        }
    }
}
