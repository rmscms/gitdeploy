using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using GitDeployPro.Controls;
using GitDeployPro.Services.Localization;
using GitDeployPro.Services.Telegram;
using MahApps.Metro.Controls;
using Forms = System.Windows.Forms;

namespace GitDeployPro.Windows
{
    public partial class ManageWorkspaceWindow : MetroWindow
    {
        private readonly string _projectPath;
        private readonly ObservableCollection<string> _extras = new();

        public ManageWorkspaceWindow(string projectPath)
        {
            InitializeComponent();
            _projectPath = projectPath?.Trim() ?? string.Empty;
            ExtrasList.ItemsSource = _extras;

            var info = CursorWorkspaceRoots.GetRoots(_projectPath);
            PrimaryPathBox.Text = string.IsNullOrWhiteSpace(info.Primary) ? _projectPath : info.Primary;
            foreach (var extra in info.Extras)
            {
                _extras.Add(extra);
            }
        }

        public bool Saved { get; private set; }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            if (_extras.Count >= CursorWorkspaceRoots.MaxExtraRoots)
            {
                ModernMessageBox.Show(
                    Loc.T("telegram.workspace.maxRoots", CursorWorkspaceRoots.MaxExtraRoots),
                    Loc.T("telegram.workspace.manageTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            using var dialog = new Forms.FolderBrowserDialog
            {
                Description = Loc.T("telegram.workspace.pickFolder"),
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };

            var primary = PrimaryPathBox.Text?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(primary) && Directory.Exists(primary))
            {
                dialog.SelectedPath = primary;
            }

            if (dialog.ShowDialog() != Forms.DialogResult.OK
                || string.IsNullOrWhiteSpace(dialog.SelectedPath))
            {
                return;
            }

            string full;
            try
            {
                full = Path.GetFullPath(dialog.SelectedPath.Trim());
            }
            catch
            {
                return;
            }

            if (!Directory.Exists(full))
            {
                return;
            }

            if (string.Equals(full, primary, StringComparison.OrdinalIgnoreCase))
            {
                ModernMessageBox.Show(
                    Loc.T("telegram.workspace.sameAsPrimary"),
                    Loc.T("telegram.workspace.manageTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (_extras.Any(x => string.Equals(x, full, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            _extras.Add(full);
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (ExtrasList.SelectedItem is string selected)
            {
                _extras.Remove(selected);
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var primary = PrimaryPathBox.Text?.Trim() ?? _projectPath;
            CursorWorkspaceRoots.SaveExtras(primary, _extras.ToList());
            CursorAgentBridge.Instance.RestartProjectAgent(
                primary,
                Loc.T("telegram.workspace.rootsUpdated"));
            CursorWorkspaceRoots.AnnounceToChatAndTelegram(primary, sendTelegram: true, force: true);
            Saved = true;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
