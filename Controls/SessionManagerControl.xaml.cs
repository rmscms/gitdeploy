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
using GitDeployPro.Windows;

namespace GitDeployPro.Controls
{
    public partial class SessionManagerControl : System.Windows.Controls.UserControl
    {
        private readonly ConfigurationService _configService = new();
        private List<SessionFolder> _folders = new();
        private List<ConnectionProfile> _connections = new();
        
        // Event for connection selection
        public event EventHandler<ConnectionProfile>? ConnectionSelected;
        public event EventHandler<ConnectionProfile>? ConnectionConnectRequested;
        
        // Track active connections and their windows
        private static Dictionary<string, TerminalWindow> _activeConnections = new();

        // ViewModel for TreeView items
        public class SessionTreeNode : INotifyPropertyChanged
        {
            private bool _isExpanded = true;
            private ObservableCollection<SessionTreeNode> _children = new();
            private bool _isActive = false;

            public string Id { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public string Icon { get; set; } = "📁";
            public string IconColor { get; set; } = "#4FC3F7";
            public bool IsFolder { get; set; }
            public string? ParentFolderId { get; set; }
            public object? Data { get; set; }
            public string ConnectionType { get; set; } = "";
            
            public bool IsActive
            {
                get => _isActive;
                set
                {
                    if (_isActive != value)
                    {
                        _isActive = value;
                        OnPropertyChanged(nameof(IsActive));
                        OnPropertyChanged(nameof(ActiveIndicator));
                        OnPropertyChanged(nameof(DisplayNameWithIndicator));
                    }
                }
            }
            
            public string ActiveIndicator => IsActive ? "🟢 " : "";
            
            public string DisplayNameWithIndicator => ActiveIndicator + DisplayNameWithCount;
            
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

        public SessionManagerControl()
        {
            InitializeComponent();
            LoadData();
            BuildTree();
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
                    if (connNode != null) // Only add SSH connections
                    {
                        SessionsTreeView.Items.Add(connNode);
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
                ParentFolderId = folder.ParentFolderId,
                Data = folder,
                ConnectionType = ""
            };

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
                    if (connNode != null) // Only add SSH connections
                    {
                        node.Children.Add(connNode);
                    }
                }
            }

            return node;
        }

        private SessionTreeNode CreateConnectionNode(ConnectionProfile conn)
        {
            // Only show SSH connections (exclude database and FTP)
            if (conn.DbType != DatabaseType.None || !conn.UseSSH)
            {
                return null; // Skip non-SSH connections
            }

            string icon = "🔒";
            string iconColor = "#9C27B0";
            string connectionType = "(SSH)";

            return new SessionTreeNode
            {
                Id = conn.Id,
                DisplayName = conn.Name,
                Icon = icon,
                IconColor = iconColor,
                IsFolder = false,
                IsExpanded = false,
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

        // Event handlers for item content (Border in ItemTemplate)
        private void ItemContent_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.DataContext is SessionTreeNode node)
            {
                if (!node.IsFolder)
                {
                    // For connections, prepare for drag
                    _dragStartPoint = e.GetPosition(null);
                    _draggedItem = node;
                    _isDragging = false;
                    // Don't handle - allow other events
                }
            }
        }
        
        private void ItemContent_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && _draggedItem != null && !_draggedItem.IsFolder && !_isDragging)
            {
                System.Windows.Point currentPosition = e.GetPosition(null);
                System.Windows.Vector diff = _dragStartPoint - currentPosition;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    _isDragging = true;
                    e.Handled = true;
                    
                    var dataObject = new System.Windows.DataObject(typeof(SessionTreeNode), _draggedItem);
                    DragDrop.DoDragDrop(sender as DependencyObject, dataObject, System.Windows.DragDropEffects.Move);
                    _draggedItem = null;
                    _isDragging = false;
                }
            }
        }
        
        private void ItemContent_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.DataContext is SessionTreeNode node)
            {
                if (!node.IsFolder && node.Data is ConnectionProfile conn)
                {
                    // Double-click on connection - connect
                    e.Handled = true;
                    _draggedItem = null;
                    _isDragging = false;
                    
                    ConnectionConnectRequested?.Invoke(this, conn);
                }
            }
        }
        
        private void ItemContent_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.DataContext is SessionTreeNode node)
            {
                if (!node.IsFolder && node.Data is ConnectionProfile conn)
                {
                    // Show context menu for connections
                    e.Handled = true;
                    
                    ShowConnectionContextMenu(border, conn);
                }
            }
        }
        
        // Old TreeViewItem event handlers (keep for folder functionality)
        private void TreeViewItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is TreeViewItem item && item.DataContext is SessionTreeNode node)
            {
                if (node.IsFolder)
                {
                    // For folders, toggle expand/collapse on click
                    // Only toggle if clicking directly on the folder header
                    var hitElement = e.OriginalSource as DependencyObject;
                    var clickedItem = FindParent<TreeViewItem>(hitElement);
                    
                    if (clickedItem == item)
                    {
                        item.IsExpanded = !item.IsExpanded;
                        node.IsExpanded = item.IsExpanded;
                        e.Handled = true;
                    }
                }
            }
        }
        
        private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
        {
            var parentObject = child;
            while (parentObject != null)
            {
                if (parentObject is T parent)
                {
                    return parent;
                }
                parentObject = System.Windows.Media.VisualTreeHelper.GetParent(parentObject);
            }
            return null;
        }
        
        private void TreeViewItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is TreeViewItem item && item.DataContext is SessionTreeNode node)
            {
                if (!node.IsFolder && node.Data is ConnectionProfile conn)
                {
                    // Double-click on connection - connect
                    e.Handled = true; // Prevent event bubbling
                    _draggedItem = null; // Cancel drag
                    _isDragging = false;
                    
                    // Invoke connection request
                    ConnectionConnectRequested?.Invoke(this, conn);
                }
            }
        }
        
        private void TreeViewItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is TreeViewItem item && item.DataContext is SessionTreeNode node)
            {
                if (!node.IsFolder && node.Data is ConnectionProfile conn)
                {
                    // Select the item first
                    item.IsSelected = true;
                    SessionsTreeView.Focus();
                    
                    // Show context menu for connections
                    ShowConnectionContextMenu(item, conn);
                    e.Handled = true; // Prevent event bubbling to parent folder
                }
                else
                {
                    // For folders, prevent context menu
                    e.Handled = true;
                }
            }
        }
        
        private void ShowConnectionContextMenu(FrameworkElement element, ConnectionProfile conn)
        {
            var contextMenu = new ContextMenu
            {
                Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2D2D30")),
                BorderBrush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#3E3E42")),
                Foreground = System.Windows.Media.Brushes.White,
                PlacementTarget = element,
                Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint
            };
            
            var connectItem = new MenuItem
            {
                Header = "🔌 Connect",
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = System.Windows.Media.Brushes.White
            };
            connectItem.Click += (s, e) => 
            {
                contextMenu.IsOpen = false;
                ConnectionConnectRequested?.Invoke(this, conn);
            };
            contextMenu.Items.Add(connectItem);
            
            var disconnectItem = new MenuItem
            {
                Header = "⏹ Disconnect",
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = System.Windows.Media.Brushes.White
            };
            disconnectItem.Click += (s, e) => 
            {
                contextMenu.IsOpen = false;
                // TODO: Implement disconnect
            };
            contextMenu.Items.Add(disconnectItem);
            
            contextMenu.Items.Add(new Separator
            {
                Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#3E3E42"))
            });
            
            var removeItem = new MenuItem
            {
                Header = "🗑 Remove",
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = System.Windows.Media.Brushes.White
            };
            removeItem.Click += (s, e) =>
            {
                contextMenu.IsOpen = false;
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
            };
            contextMenu.Items.Add(removeItem);
            
            // Open context menu
            element.ContextMenu = contextMenu;
            contextMenu.IsOpen = true;
        }
        
        private void SessionsTreeView_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            // Allow context menu for connections
            if (SessionsTreeView.SelectedItem is SessionTreeNode node && !node.IsFolder)
            {
                return; // Allow context menu
            }
            // Prevent context menu for folders
            e.Handled = true;
        }

        private void TreeViewItem_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && _draggedItem != null && !_draggedItem.IsFolder && !_isDragging)
            {
                System.Windows.Point currentPosition = e.GetPosition(null);
                System.Windows.Vector diff = _dragStartPoint - currentPosition;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    _isDragging = true;
                    e.Handled = true;
                    var dataObject = new System.Windows.DataObject(typeof(SessionTreeNode), _draggedItem);
                    DragDrop.DoDragDrop(sender as DependencyObject, dataObject, System.Windows.DragDropEffects.Move);
                    _draggedItem = null;
                    _isDragging = false;
                }
            }
        }
        
        private void TreeViewItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // Handle single click on connections to prevent parent folder collapse
            if (sender is TreeViewItem item && item.DataContext is SessionTreeNode node)
            {
                if (!node.IsFolder && !_isDragging)
                {
                    // Single click on connection - just select it, don't collapse parent
                    e.Handled = true;
                }
            }
        }

        private void TreeViewItem_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            if (sender is TreeViewItem item && item.DataContext is SessionTreeNode targetNode)
            {
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
            e.Handled = true;
            
            if (sender is TreeViewItem item && item.DataContext is SessionTreeNode targetNode)
            {
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
                    if (conn.FolderId == targetNode.Id)
                    {
                        return;
                    }

                    conn.FolderId = targetNode.Id;
                    _configService.AddOrUpdateConnection(conn);
                    LoadData();
                    BuildTree();
                }
            }
        }

        private void SessionsTreeView_DragOver(object sender, System.Windows.DragEventArgs e)
        {
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
        
        public static void RegisterActiveConnection(string connectionId, TerminalWindow window)
        {
            _activeConnections[connectionId] = window;
        }
        
        public static void UnregisterActiveConnection(string connectionId)
        {
            _activeConnections.Remove(connectionId);
        }
    }
}

