using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using GitDeployPro.Controls;
using GitDeployPro.Models;
using GitDeployPro.Services.Localization;
using GitDeployPro.Services.Remote;
using MahApps.Metro.Controls;

namespace GitDeployPro.Windows
{
    public partial class SyncManifestEditorWindow : MetroWindow
    {
        private readonly IEnumerable<RemoteTreeNode> _roots;
        private readonly ConnectionProfile _profile;
        private readonly Func<RemoteTreeNode, bool>? _excludeNode;

        public SyncManifest SavedManifest { get; private set; } = new();

        public SyncManifestEditorWindow(
            ConnectionProfile profile,
            string manifestRemotePath,
            IEnumerable<RemoteTreeNode> roots,
            SyncManifest currentManifest,
            Func<RemoteTreeNode, bool>? excludeNode = null)
        {
            InitializeComponent();
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _roots = roots ?? throw new ArgumentNullException(nameof(roots));
            _excludeNode = excludeNode;

            HeaderText.Text = string.IsNullOrWhiteSpace(profile.Name)
                ? Loc.T("deploy.sync.edit")
                : $"{Loc.T("deploy.sync.edit")} — {profile.Name}";
            ManifestPathText.Text = manifestRemotePath;

            EditorTreeView.ItemsSource = _roots;
            SyncManifestService.ApplyChecksToTree(currentManifest, _roots);
            UpdateCheckedCount();
        }

        private void ItemCheckBox_Click(object sender, RoutedEventArgs e)
        {
            UpdateCheckedCount();
        }

        private void UpdateCheckedCount()
        {
            var manifest = SyncManifestService.BuildFromCheckedNodes(_roots, _profile, _excludeNode);
            CheckedCountText.Text = Loc.T("deploy.sync.checkedCount", manifest.Paths.Count);
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var root in _roots)
            {
                if (_excludeNode != null && _excludeNode(root))
                {
                    foreach (var child in root.Children.Where(node => !node.IsPlaceholder && (_excludeNode == null || !_excludeNode(node))))
                    {
                        child.IsChecked = true;
                    }
                }
                else
                {
                    root.IsChecked = true;
                }
            }

            UpdateCheckedCount();
        }

        private void DeselectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var root in _roots)
            {
                root.ClearChecked();
            }

            UpdateCheckedCount();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            SavedManifest = SyncManifestService.BuildFromCheckedNodes(_roots, _profile, _excludeNode);
            if (SavedManifest.Paths.Count == 0)
            {
                ModernMessageBox.Show(
                    Loc.T("deploy.sync.editEmpty"),
                    Loc.T("deploy.sync.edit"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
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
