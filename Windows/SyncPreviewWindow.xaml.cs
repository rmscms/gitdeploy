using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using GitDeployPro.Controls;
using GitDeployPro.Models;
using GitDeployPro.Services.Localization;
using MahApps.Metro.Controls;

namespace GitDeployPro.Windows
{
    public partial class SyncPreviewWindow : MetroWindow
    {
        private readonly ObservableCollection<SyncPathPreviewItem> _items = new();

        public IReadOnlyList<SyncManifestPathEntry> SelectedPaths { get; private set; } = new List<SyncManifestPathEntry>();

        public SyncPreviewWindow(string manifestRemotePath, IEnumerable<SyncPathPreviewItem> items)
        {
            InitializeComponent();
            PathsList.ItemsSource = _items;
            ManifestPathText.Text = manifestRemotePath;
            foreach (var item in items)
            {
                _items.Add(item);
            }

            UpdateSummary();
        }

        private void UpdateSummary()
        {
            var checkedCount = _items.Count(item => item.IsChecked);
            SummaryText.Text = Loc.T("deploy.sync.previewSummary", _items.Count, checkedCount);
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _items)
            {
                item.IsChecked = true;
            }

            PathsList.Items.Refresh();
            UpdateSummary();
        }

        private void DeselectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _items)
            {
                item.IsChecked = false;
            }

            PathsList.Items.Refresh();
            UpdateSummary();
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            SelectedPaths = _items
                .Where(item => item.IsChecked)
                .Select(item => new SyncManifestPathEntry
                {
                    Remote = item.Remote,
                    Kind = item.IsDirectory ? "folder" : "file"
                })
                .ToList();

            if (SelectedPaths.Count == 0)
            {
                ModernMessageBox.Show(
                    Loc.T("deploy.sync.previewEmpty"),
                    Loc.T("deploy.sync.preview"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information,
                    context: this);
                return;
            }

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
