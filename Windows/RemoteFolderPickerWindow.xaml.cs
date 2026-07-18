using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using GitDeployPro.Controls;
using GitDeployPro.Models;
using GitDeployPro.Services.Remote;

namespace GitDeployPro.Windows
{
    public partial class RemoteFolderPickerWindow
    {
        private readonly IRemoteFileService _remoteService;
        private readonly RemoteTreeBuilder _treeBuilder = new();
        private readonly string _remoteRoot;
        private readonly string? _blockedPathPrefix;
        private readonly ObservableCollection<RemoteTreeNode> _rootNodes = new();
        private bool _isLoadingChildren;

        public string? SelectedFolderPath { get; private set; }

        public RemoteFolderPickerWindow(
            IRemoteFileService remoteService,
            string remoteRoot,
            string sourceName,
            string sourcePath,
            bool sourceIsDirectory)
        {
            InitializeComponent();

            _remoteService = remoteService ?? throw new ArgumentNullException(nameof(remoteService));
            _remoteRoot = RemotePathResolver.EnsureTrailingSlash(remoteRoot);
            _blockedPathPrefix = sourceIsDirectory ? sourcePath : null;

            PromptTextBlock.Text = $"Choose a destination folder for '{sourceName}':";
            FolderTreeView.ItemsSource = _rootNodes;

            var rootNode = _treeBuilder.CreateRootFolderNode(_remoteRoot);
            if (!RemoteTreeBuilder.IsBlockedPath(rootNode.FullPath, _blockedPathPrefix))
            {
                _rootNodes.Add(rootNode);
                rootNode.IsSelected = true;
                UpdateSelection(rootNode);
            }
        }

        private void FolderTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is RemoteTreeNode node && node.IsDirectory && !node.IsPlaceholder)
            {
                UpdateSelection(node);
            }
        }

        private void UpdateSelection(RemoteTreeNode node)
        {
            if (RemoteTreeBuilder.IsBlockedPath(node.FullPath, _blockedPathPrefix))
            {
                SelectedFolderPath = null;
                SelectedPathText.Text = "Selected: (invalid destination)";
                OkButton.IsEnabled = false;
                return;
            }

            SelectedFolderPath = RemotePathResolver.EnsureTrailingSlash(node.FullPath).TrimEnd('/');
            if (string.IsNullOrWhiteSpace(SelectedFolderPath))
            {
                SelectedFolderPath = "/";
            }

            SelectedPathText.Text = $"Selected: {SelectedFolderPath}";
            OkButton.IsEnabled = true;
        }

        private async void FolderTreeItem_Expanded(object sender, RoutedEventArgs e)
        {
            if (_isLoadingChildren)
            {
                return;
            }

            if (e.OriginalSource is not TreeViewItem treeItem)
            {
                return;
            }

            if (treeItem.DataContext is not RemoteTreeNode node)
            {
                return;
            }

            if (!node.IsDirectory || node.IsLoaded || node.IsPlaceholder)
            {
                return;
            }

            e.Handled = true;
            await LoadChildrenAsync(node).ConfigureAwait(true);
        }

        private async Task LoadChildrenAsync(RemoteTreeNode node)
        {
            if (!_remoteService.IsConnected || node == null || !node.IsDirectory)
            {
                return;
            }

            _isLoadingChildren = true;
            try
            {
                var path = RemotePathResolver.EnsureTrailingSlash(node.FullPath);
                var entries = await _remoteService.ListDirectoryAsync(path).ConfigureAwait(true);
                var children = _treeBuilder.BuildFolderNodes(entries, _blockedPathPrefix);
                node.Children.Clear();
                foreach (var child in children)
                {
                    node.Children.Add(child);
                }

                node.IsLoaded = true;
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(
                    $"Failed to load folders:\n{ex.Message}",
                    "Move to...",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    owner: this);
            }
            finally
            {
                _isLoadingChildren = false;
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(SelectedFolderPath)
                || RemoteTreeBuilder.IsBlockedPath(SelectedFolderPath, _blockedPathPrefix))
            {
                ModernMessageBox.Show(
                    "Please select a valid destination folder.",
                    "Move to...",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    owner: this);
                return;
            }

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
