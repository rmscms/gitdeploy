using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GitDeployPro.Models;
using GitDeployPro.Services;
using GitDeployPro.Controls;

namespace GitDeployPro.Windows
{
    public partial class SessionManagerWindow
    {
        private readonly ConfigurationService _configService = new();
        private List<SessionFolder> _folders = new();
        private List<ConnectionProfile> _connections = new();

        // ViewModel for TreeView items
        public class SessionTreeNode : INotifyPropertyChanged
        {
            private bool _isExpanded = true; // Default expanded for folders
            private ObservableCollection<SessionTreeNode> _children = new();

            public string Id { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public string Icon { get; set; } = "📁";
            public string IconColor { get; set; } = "#4FC3F7";
            public bool IsFolder { get; set; }
            public string? ParentFolderId { get; set; }
            public object? Data { get; set; } // SessionFolder or ConnectionProfile
            public string ConnectionType { get; set; } = "";
            
            public ObservableCollection<SessionTreeNode> Children
            {
                get => _children;
                set
                {
                    if (_children != value)
                    {
                        if (_children != null)
                        {
                            _children.CollectionChanged -= Children_CollectionChanged;
                        }
                        _children = value ?? new ObservableCollection<SessionTreeNode>();
                        if (_children != null)
                        {
                            _children.CollectionChanged += Children_CollectionChanged;
                        }
                        OnPropertyChanged(nameof(Children));
                        OnPropertyChanged(nameof(ChildrenCount));
                        OnPropertyChanged(nameof(DisplayNameWithCount));
                    }
                }
            }

            public int ChildrenCount => Children?.Count ?? 0;

            public string DisplayNameWithCount
            {
                get
                {
                    if (IsFolder)
                    {
                        return $"{DisplayName} {ChildrenCount}";
                    }
                    return DisplayName;
                }
            }
            
            public bool IsExpanded
            {
                get => _isExpanded;
                set
                {
                    if (_isExpanded != value)
                    {
                        _isExpanded = value;
                        OnPropertyChanged(nameof(IsExpanded));
                    }
                }
            }

            public Visibility ConnectionTypeVisibility => string.IsNullOrWhiteSpace(ConnectionType) 
                ? Visibility.Collapsed 
                : Visibility.Visible;

            private void Children_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
            {
                OnPropertyChanged(nameof(ChildrenCount));
                OnPropertyChanged(nameof(DisplayNameWithCount));
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            protected void OnPropertyChanged(string propertyName)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        public SessionManagerWindow()
        {
            InitializeComponent();
            Loaded += SessionManagerWindow_Loaded;
            LoadData();
            BuildTree();
        }

        private void SessionManagerWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Expand all folders after window is loaded
            SessionsTreeView.Loaded += SessionsTreeView_Loaded;
        }

        private void SessionsTreeView_Loaded(object sender, RoutedEventArgs e)
        {
            // Force layout update and then expand
            SessionsTreeView.UpdateLayout();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ExpandAllFolders(SessionsTreeView.Items);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void LoadData()
        {
            _folders = _configService.LoadSessionFolders();
            _connections = _configService.LoadConnections();
        }

        private void BuildTree()
        {
            SessionsTreeView.Items.Clear();
            var filterText = FilterTextBox?.Text?.ToLower() ?? "";

            // Build root folders
            var rootFolders = _folders
                .Where(f => f.ParentFolderId == null)
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var folder in rootFolders)
            {
                if (string.IsNullOrWhiteSpace(filterText) || folder.Name.ToLower().Contains(filterText))
                {
                    var folderNode = CreateFolderNode(folder, filterText);
                    if (folderNode != null)
                    {
                        SessionsTreeView.Items.Add(folderNode);
                    }
                }
            }

            // Add root connections
            var rootConnections = _connections
                .Where(c => string.IsNullOrWhiteSpace(c.FolderId))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var conn in rootConnections)
            {
                if (string.IsNullOrWhiteSpace(filterText) || 
                    conn.Name.ToLower().Contains(filterText) ||
                    conn.Host.ToLower().Contains(filterText))
                {
                    var connNode = CreateConnectionNode(conn);
                    SessionsTreeView.Items.Add(connNode);
                }
            }

            // Expansion will happen in SessionsTreeView_Loaded
        }

        private void ExpandAllFolders(ItemCollection items)
        {
            foreach (var item in items)
            {
                if (item is SessionTreeNode node && node.IsFolder)
                {
                    var container = SessionsTreeView.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
                    if (container != null)
                    {
                        container.IsExpanded = true;
                        node.IsExpanded = true;
                        ExpandChildren(container);
                    }
                }
            }
        }

        private void ExpandChildren(TreeViewItem parentItem)
        {
            foreach (var child in parentItem.Items)
            {
                if (child is SessionTreeNode node && node.IsFolder)
                {
                    var generator = parentItem.ItemContainerGenerator;
                    if (generator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                    {
                        var childContainer = generator.ContainerFromItem(child) as TreeViewItem;
                        if (childContainer != null)
                        {
                            childContainer.IsExpanded = true;
                            node.IsExpanded = true;
                            ExpandChildren(childContainer);
                        }
                    }
                }
            }
        }

        private SessionTreeNode? CreateFolderNode(SessionFolder folder, string filterText)
        {
            var node = new SessionTreeNode
            {
                Id = folder.Id,
                DisplayName = folder.Name,
                Icon = "📁",
                IconColor = "#FFB74D",
                IsFolder = true,
                IsExpanded = true, // Folders expanded by default
                ParentFolderId = folder.ParentFolderId,
                Data = folder,
                ConnectionType = ""
            };

            // Add child folders
            var childFolders = _folders
                .Where(f => f.ParentFolderId == folder.Id)
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var childFolder in childFolders)
            {
                if (string.IsNullOrWhiteSpace(filterText) || childFolder.Name.ToLower().Contains(filterText))
                {
                    var childNode = CreateFolderNode(childFolder, filterText);
                    if (childNode != null)
                    {
                        node.Children.Add(childNode);
                    }
                }
            }

            // Add connections in this folder
            var folderConnections = _connections
                .Where(c => c.FolderId == folder.Id)
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var conn in folderConnections)
            {
                if (string.IsNullOrWhiteSpace(filterText) ||
                    conn.Name.ToLower().Contains(filterText) ||
                    conn.Host.ToLower().Contains(filterText))
                {
                    var connNode = CreateConnectionNode(conn);
                    node.Children.Add(connNode);
                }
            }

            return node;
        }

        private SessionTreeNode CreateConnectionNode(ConnectionProfile conn)
        {
            string icon = "🔒";
            string iconColor = "#9C27B0";
            string connectionType = "";

            if (conn.DbType != DatabaseType.None)
            {
                icon = "🛢️";
                iconColor = "#2196F3";
                connectionType = "(database)";
            }
            else if (!conn.UseSSH)
            {
                icon = "📂";
                iconColor = "#4CAF50";
                connectionType = "(FTP)";
            }
            else
            {
                connectionType = "(SSH)";
            }

            return new SessionTreeNode
            {
                Id = conn.Id,
                DisplayName = conn.Name,
                Icon = icon,
                IconColor = iconColor,
                IsFolder = false,
                IsExpanded = false, // Connections are not expandable
                ParentFolderId = conn.FolderId,
                Data = conn,
                ConnectionType = $"{connectionType} ({conn.Host})"
            };
        }

        private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            BuildTree();
        }

        private void SessionsTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            // Handle selection if needed
        }

        private void AddFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var inputDialog = new InputDialog("Enter folder name:", "New Folder", "New Folder");
            if (inputDialog.ShowDialog() == true)
            {
                var name = inputDialog.InputText;
                if (string.IsNullOrWhiteSpace(name)) return;

                var folder = new SessionFolder
                {
                    Name = name,
                    ParentFolderId = GetSelectedFolderId()
                };

                _folders.Add(folder);
                _configService.AddOrUpdateSessionFolder(folder);
                LoadData();
                BuildTree();
            }
        }

        private void NewFolderButton_Click(object sender, RoutedEventArgs e)
        {
            AddFolderButton_Click(sender, e);
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (SessionsTreeView.SelectedItem is SessionTreeNode selected)
            {
                if (selected.IsFolder)
                {
                    var result = ModernMessageBox.ShowWithResult(
                        $"Delete folder '{selected.DisplayName}' and move its contents to parent?",
                        "Confirm Delete",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question,
                        "Delete",
                        "Cancel");

                    if (result == MessageBoxResult.Yes)
                    {
                        _configService.DeleteSessionFolder(selected.Id);
                        LoadData();
                        BuildTree();
                    }
                }
                else if (selected.Data is ConnectionProfile conn)
                {
                    var result = ModernMessageBox.ShowWithResult(
                        $"Delete connection '{conn.Name}'?",
                        "Confirm Delete",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question,
                        "Delete",
                        "Cancel");

                    if (result == MessageBoxResult.Yes)
                    {
                        _configService.DeleteConnection(conn.Id);
                        LoadData();
                        BuildTree();
                    }
                }
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            RemoveButton_Click(sender, e);
        }

        private void LinkButton_Click(object sender, RoutedEventArgs e)
        {
            var connWindow = new ConnectionManagerWindow();
            if (connWindow.ShowDialog() == true)
            {
                LoadData();
                BuildTree();
            }
        }

        private string? GetSelectedFolderId()
        {
            if (SessionsTreeView.SelectedItem is SessionTreeNode selected && selected.IsFolder)
            {
                return selected.Id;
            }
            return null;
        }

        // Drag & Drop handlers
        private SessionTreeNode? _draggedItem = null;
        private System.Windows.Point _dragStartPoint;
        private bool _isDragging = false;

        private void TreeViewItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is TreeViewItem item && item.DataContext is SessionTreeNode node)
            {
                // Only prepare drag for connections (not folders)
                if (!node.IsFolder)
                {
                    _dragStartPoint = e.GetPosition(null);
                    _draggedItem = node;
                    _isDragging = false;
                }
                // For folders, don't handle at all - let default expand/collapse work
                // Don't set e.Handled = true for folders
            }
        }

        private void TreeViewItem_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // Only handle drag for connections
            if (e.LeftButton == MouseButtonState.Pressed && _draggedItem != null && !_draggedItem.IsFolder && !_isDragging)
            {
                System.Windows.Point currentPosition = e.GetPosition(null);
                System.Windows.Vector diff = _dragStartPoint - currentPosition;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    _isDragging = true;
                    e.Handled = true;
                    // Use DataObject for better compatibility
                    var dataObject = new System.Windows.DataObject(typeof(SessionTreeNode), _draggedItem);
                    DragDrop.DoDragDrop(sender as DependencyObject, dataObject, System.Windows.DragDropEffects.Move);
                    _draggedItem = null;
                    _isDragging = false;
                }
            }
        }

        private void TreeViewItem_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            if (sender is TreeViewItem item && item.DataContext is SessionTreeNode targetNode)
            {
                // Try both DataObject and direct type
                SessionTreeNode? draggedNode = null;
                if (e.Data.GetData(typeof(SessionTreeNode)) is SessionTreeNode node1)
                {
                    draggedNode = node1;
                }
                else if (e.Data.GetData(typeof(SessionTreeNode).FullName) is SessionTreeNode node2)
                {
                    draggedNode = node2;
                }
                else if (e.Data.GetData(System.Windows.DataFormats.Serializable) is SessionTreeNode node3)
                {
                    draggedNode = node3;
                }

                if (draggedNode != null && targetNode.IsFolder && !draggedNode.IsFolder)
                {
                    e.Effects = System.Windows.DragDropEffects.Move;
                    e.Handled = true;
                    return;
                }
            }
            e.Effects = System.Windows.DragDropEffects.None;
        }

        private void TreeViewItem_Drop(object sender, System.Windows.DragEventArgs e)
        {
            e.Handled = true; // Mark as handled to prevent bubbling
            
            if (sender is TreeViewItem item && item.DataContext is SessionTreeNode targetNode)
            {
                // Try multiple formats to get dragged node
                SessionTreeNode? draggedNode = null;
                if (e.Data.GetData(typeof(SessionTreeNode)) is SessionTreeNode node1)
                {
                    draggedNode = node1;
                }
                else if (e.Data.GetData(typeof(SessionTreeNode).FullName) is SessionTreeNode node2)
                {
                    draggedNode = node2;
                }
                else if (e.Data.GetData(System.Windows.DataFormats.Serializable) is SessionTreeNode node3)
                {
                    draggedNode = node3;
                }

                if (draggedNode != null && targetNode.IsFolder && !draggedNode.IsFolder && draggedNode.Data is ConnectionProfile conn)
                {
                    // Prevent dropping on itself
                    if (conn.FolderId == targetNode.Id)
                    {
                        return;
                    }

                    // Move connection to target folder
                    conn.FolderId = targetNode.Id;
                    _configService.AddOrUpdateConnection(conn);
                    
                    // Refresh tree
                    LoadData();
                    BuildTree();
                }
            }
        }

        private void SessionsTreeView_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            // Allow dropping on TreeView itself (root level)
            if (e.Data.GetData(typeof(SessionTreeNode)) is SessionTreeNode draggedNode && !draggedNode.IsFolder)
            {
                e.Effects = System.Windows.DragDropEffects.Move;
                e.Handled = true;
            }
            else
            {
                e.Effects = System.Windows.DragDropEffects.None;
            }
        }

        private void SessionsTreeView_Drop(object sender, System.Windows.DragEventArgs e)
        {
            // Drop on TreeView root - remove from folder
            if (e.Data.GetData(typeof(SessionTreeNode)) is SessionTreeNode draggedNode)
            {
                if (!draggedNode.IsFolder && draggedNode.Data is ConnectionProfile conn)
                {
                    conn.FolderId = null;
                    _configService.AddOrUpdateConnection(conn);
                    LoadData();
                    BuildTree();
                }
            }
        }
    }
}
