using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using GitDeployPro.Models;
using MahApps.Metro.Controls;

namespace GitDeployPro.Windows
{
    public partial class ProjectFtpTargetWindow : MetroWindow
    {
        public string? SelectedProfileId { get; private set; }

        public ProjectFtpTargetWindow(
            IReadOnlyList<ConnectionProfile> profiles,
            string? currentDefaultId,
            string? prompt = null)
        {
            InitializeComponent();
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                PromptTextBlock.Text = prompt;
            }

            var items = (profiles ?? Array.Empty<ConnectionProfile>())
                .Where(ConnectionProfileFilters.IsRemoteFileProfile)
                .Select(p => new FtpTargetItem(p, currentDefaultId))
                .ToList();
            ProfilesList.ItemsSource = items;

            var preferred = items.FirstOrDefault(i => i.IsCurrentDefault) ?? items.FirstOrDefault();
            if (preferred != null)
            {
                ProfilesList.SelectedItem = preferred;
            }
        }

        public static bool TryPick(
            Window? owner,
            IReadOnlyList<ConnectionProfile> profiles,
            string? currentDefaultId,
            out string selectedId,
            string? prompt = null)
        {
            selectedId = string.Empty;
            var dialog = new ProjectFtpTargetWindow(profiles, currentDefaultId, prompt);
            var result = owner != null
                ? Services.WindowOwnerService.ShowDialogOwned(dialog, preferredOwner: owner)
                : dialog.ShowDialog();
            if (result == true && !string.IsNullOrWhiteSpace(dialog.SelectedProfileId))
            {
                selectedId = dialog.SelectedProfileId;
                return true;
            }

            return false;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (ProfilesList.SelectedItem is not FtpTargetItem item)
            {
                return;
            }

            SelectedProfileId = item.Profile.Id;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private sealed class FtpTargetItem
        {
            public FtpTargetItem(ConnectionProfile profile, string? currentDefaultId)
            {
                Profile = profile;
                IsCurrentDefault = string.Equals(profile.Id, currentDefaultId, StringComparison.OrdinalIgnoreCase);
            }

            public ConnectionProfile Profile { get; }
            public string Name => Profile.Name;
            public string Host => Profile.Host;
            public bool IsCurrentDefault { get; }
            public Visibility DefaultBadgeVisibility => IsCurrentDefault ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
