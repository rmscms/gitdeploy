using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GitDeployPro.Services;
using GitDeployPro.Services.Remote;
using GitDeployPro.Services.Theme;
using FluentFTP;
using GitDeployPro.Models;
using GitDeployPro.Windows;

namespace GitDeployPro.Controls
{
    public partial class DirectUploadWorkspaceControl : System.Windows.Controls.UserControl
    {
        public static readonly DependencyProperty CompactModeProperty =
            DependencyProperty.Register(
                nameof(CompactMode),
                typeof(bool),
                typeof(DirectUploadWorkspaceControl),
                new PropertyMetadata(false, OnCompactModeChanged));

        private ConfigurationService _configService;
        private readonly GitService _gitService = new GitService();
        private string _projectPath = string.Empty;
        private string _scanRootPath = string.Empty;
        private string _mappedLocalRoot = string.Empty;
        private string _profileRemoteBasePath = "/";
        private string _activeRemoteBasePath = "/";
        private ObservableCollection<FileSystemItem> _items;
        private bool _isUploading = false;
        private bool _isRefreshingFromDisk = false;
        private bool _uploadPanelManuallyOpen = false;
        private CancellationTokenSource? _cancellationTokenSource;
        private DispatcherTimer? _autoDiskSyncTimer;
        private const int AutoDiskSyncSeconds = 10;
        private const string SessionFileName = ".gitdeploy.session";
        private const string TreeLegendTooltip =
            "Legend: green = clean · red = changed · blue = untracked · grey = ignored · green background = mapped";

        public event EventHandler<LocalEditorModeChangedEventArgs>? EditorModeChanged;
        public event EventHandler? EditorFloatRequested;
        public event EventHandler? UploadActionsPanelVisibilityChanged;

        private static readonly HashSet<string> HardExcludeNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", "Desktop.ini", "Thumbs.db",
            ".gitdeploy.config", ".gitdeploy.session", ".gitdeploy.history"
        };

        public bool CompactMode
        {
            get => (bool)GetValue(CompactModeProperty);
            set => SetValue(CompactModeProperty, value);
        }

        public bool IsUploadActionsPanelVisible =>
            UploadProcessSection != null && UploadProcessSection.Visibility == Visibility.Visible;

        public bool IsUploadActionsPanelPinned => _uploadPanelManuallyOpen;

        public DirectUploadWorkspaceControl()
        {
            InitializeComponent();
            _configService = new ConfigurationService();
            _items = new ObservableCollection<FileSystemItem>();
            FileTreeView.ItemsSource = _items;

            Loaded += DirectUploadWorkspaceControl_Loaded;
            Unloaded += DirectUploadWorkspaceControl_Unloaded;
            ThemeService.Instance.ThemeChanged += OnDeployThemeChanged;
            if (LocalEditor != null)
            {
                LocalEditor.EditorModeChanged += (_, args) => EditorModeChanged?.Invoke(this, args);
                LocalEditor.FloatRequested += (_, _) => EditorFloatRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        private void OnDeployThemeChanged(object? sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => OnDeployThemeChanged(sender, e));
                return;
            }

            ApplyCompactMode(CompactMode);
            RefreshTreeThemeBrushes(_items);
        }

        private static void RefreshTreeThemeBrushes(IEnumerable<FileSystemItem>? items)
        {
            if (items == null)
            {
                return;
            }

            foreach (var item in items)
            {
                item.ApplyThemeColors();
                if (item.Children is { Count: > 0 })
                {
                    RefreshTreeThemeBrushes(item.Children);
                }
            }
        }

        public void HostLocalEditorIn(Decorator host) => LocalEditor?.HostIn(host);

        public void RestoreLocalEditorHome() => LocalEditor?.RestoreHome();

        public void SetLocalEditorFloated(bool floated) => LocalEditor?.SetEditorFloated(floated);

        public string GetLocalEditorPath() => LocalEditor?.OpenedFilePath ?? string.Empty;

        public bool TryCloseLocalEditor(bool force = false) => LocalEditor?.TryClose(force) ?? true;

        public Task TryReloadLocalEditorIfMatchesAsync(string localPath) =>
            LocalEditor?.TryReloadFromDiskIfMatchesAsync(localPath) ?? Task.CompletedTask;

        public void ToggleUploadActionsPanel()
        {
            if (!CompactMode || UploadProcessSection == null)
            {
                return;
            }

            if (_isUploading)
            {
                // Keep progress visible while uploading; ignore hide attempts.
                return;
            }

            _uploadPanelManuallyOpen = !_uploadPanelManuallyOpen;
            ApplyUploadPanelVisibility();
        }

        public void ShowUploadActionsPanel()
        {
            if (!CompactMode)
            {
                return;
            }

            _uploadPanelManuallyOpen = true;
            ApplyUploadPanelVisibility();
        }

        public void HideUploadActionsPanel()
        {
            if (!CompactMode || _isUploading)
            {
                return;
            }

            _uploadPanelManuallyOpen = false;
            ApplyUploadPanelVisibility();
        }

        private static void OnCompactModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DirectUploadWorkspaceControl control)
            {
                control.ApplyCompactMode((bool)e.NewValue);
            }
        }

        private void ApplyCompactMode(bool compact)
        {
            if (PageHeaderRow == null || UploadLogSection == null || ContentRootGrid == null)
            {
                return;
            }

            PageHeaderRow.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            UploadLogSection.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            ContentRootGrid.Margin = compact ? new Thickness(4) : new Thickness(20);

            if (ToolbarSection != null)
            {
                ToolbarSection.Padding = compact ? new Thickness(6, 4, 6, 4) : new Thickness(15);
                ToolbarSection.Margin = compact ? new Thickness(0, 0, 0, 6) : new Thickness(0, 0, 0, 10);
            }

            if (UploadProcessSection != null)
            {
                UploadProcessSection.Padding = compact ? new Thickness(6) : new Thickness(10);
                UploadProcessSection.Margin = compact ? new Thickness(0, 0, 0, 6) : new Thickness(0, 0, 0, 10);
                UploadProcessSection.CornerRadius = new CornerRadius(compact ? 6 : 10);
            }

            if (UploadStatusCard != null)
            {
                UploadStatusCard.Padding = compact ? new Thickness(8, 6, 8, 6) : new Thickness(10, 8, 10, 8);
            }

            if (StartUploadButton != null)
            {
                StartUploadButton.Height = compact ? 30 : 38;
            }

            if (StopButton != null)
            {
                StopButton.Height = compact ? 30 : 38;
            }

            if (RefreshButton != null)
            {
                RefreshButton.Padding = compact ? new Thickness(6, 2, 6, 2) : new Thickness(10, 4, 10, 4);
                RefreshButton.Margin = compact ? new Thickness(0, 0, 4, 0) : new Thickness(0, 0, 10, 0);
            }

            if (SelectAllButton != null)
            {
                SelectAllButton.Padding = compact ? new Thickness(6, 2, 6, 2) : new Thickness(10, 4, 10, 4);
                SelectAllButton.Margin = compact ? new Thickness(2, 0, 2, 0) : new Thickness(10, 0, 10, 0);
            }

            if (DeselectAllButton != null)
            {
                DeselectAllButton.Padding = compact ? new Thickness(6, 2, 6, 2) : new Thickness(10, 4, 10, 4);
                DeselectAllButton.Margin = compact ? new Thickness(2, 0, 0, 0) : new Thickness(10, 0, 0, 0);
            }

            // Compact dock: icon-only buttons so the toolbar can wrap cleanly without overlapping stats.
            if (RefreshButtonLabel != null)
            {
                RefreshButtonLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                RefreshButtonLabel.Text = "Refresh from Disk";
                if (RefreshButtonLabel.Parent is StackPanel refreshIcon && refreshIcon.Children.Count > 0
                    && refreshIcon.Children[0] is TextBlock refreshEmoji)
                {
                    refreshEmoji.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 5, 0);
                }
            }

            if (SelectAllButtonLabel != null)
            {
                SelectAllButtonLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                if (SelectAllButtonLabel.Parent is StackPanel selectIcon && selectIcon.Children.Count > 0
                    && selectIcon.Children[0] is TextBlock selectEmoji)
                {
                    selectEmoji.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 5, 0);
                }
            }

            if (DeselectAllButtonLabel != null)
            {
                DeselectAllButtonLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                if (DeselectAllButtonLabel.Parent is StackPanel deselectIcon && deselectIcon.Children.Count > 0
                    && deselectIcon.Children[0] is TextBlock deselectEmoji)
                {
                    deselectEmoji.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 5, 0);
                }
            }

            if (TreeLegendText != null)
            {
                TreeLegendText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            }

            if (TreeSection != null)
            {
                TreeSection.ToolTip = compact ? TreeLegendTooltip : null;
            }

            if (FileTreeView != null)
            {
                FileTreeView.Padding = compact ? new Thickness(4) : new Thickness(8);
                FileTreeView.FontSize = compact ? 12 : 13;

                var tokens = ThemeService.Instance.CurrentTokens;
                var selectedBrush = tokens.GetBrush(
                    "directUpload.treeSelection",
                    GetThemeColor("Surface.Raised", System.Windows.Media.Colors.DimGray));
                var hoverBrush = tokens.GetBrush(
                    "directUpload.treeHover",
                    GetThemeColor("Surface.Shell", System.Windows.Media.Colors.DarkSlateGray));
                var itemStyle = new Style(typeof(TreeViewItem));
                itemStyle.Setters.Add(new Setter(
                    TreeViewItem.IsExpandedProperty,
                    new System.Windows.Data.Binding("IsExpanded") { Mode = BindingMode.TwoWay }));
                itemStyle.Setters.Add(new Setter(
                    System.Windows.Controls.Control.ForegroundProperty,
                    GetThemeBrush("Text.Secondary", System.Windows.Media.Brushes.Gray)));
                itemStyle.Setters.Add(new Setter(
                    System.Windows.Controls.Control.BackgroundProperty,
                    System.Windows.Media.Brushes.Transparent));
                itemStyle.Setters.Add(new Setter(
                    System.Windows.Controls.Control.FontSizeProperty,
                    compact ? 12.0 : 13.0));
                itemStyle.Setters.Add(new Setter(
                    System.Windows.Controls.Control.PaddingProperty,
                    compact ? new Thickness(2, 1, 2, 1) : new Thickness(3, 1, 3, 1)));

                var selectedTrigger = new Trigger { Property = TreeViewItem.IsSelectedProperty, Value = true };
                selectedTrigger.Setters.Add(new Setter(
                    System.Windows.Controls.Control.BackgroundProperty,
                    selectedBrush));
                itemStyle.Triggers.Add(selectedTrigger);

                var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
                hoverTrigger.Setters.Add(new Setter(
                    System.Windows.Controls.Control.BackgroundProperty,
                    hoverBrush));
                itemStyle.Triggers.Add(hoverTrigger);

                FileTreeView.ItemContainerStyle = itemStyle;
            }

            if (!compact)
            {
                _uploadPanelManuallyOpen = false;
            }

            ApplyUploadPanelVisibility();
        }

        private void ApplyUploadPanelVisibility()
        {
            if (UploadProcessSection == null)
            {
                return;
            }

            var previous = UploadProcessSection.Visibility;
            if (!CompactMode)
            {
                UploadProcessSection.Visibility = Visibility.Visible;
                SetUploadInProgressUi(false);
            }
            else
            {
                var show = _uploadPanelManuallyOpen || _isUploading;
                UploadProcessSection.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                SetUploadInProgressUi(_isUploading);
            }

            if (previous != UploadProcessSection.Visibility || CompactMode)
            {
                UploadActionsPanelVisibilityChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void SetUploadInProgressUi(bool inProgress)
        {
            if (!CompactMode || !inProgress)
            {
                if (UploadOptionsPanel != null)
                {
                    UploadOptionsPanel.Visibility = Visibility.Visible;
                }

                if (UploadActionButtonsRow != null)
                {
                    UploadActionButtonsRow.Visibility = Visibility.Visible;
                }

                if (CompactStopButton != null)
                {
                    CompactStopButton.Visibility = Visibility.Collapsed;
                }

                return;
            }

            // Compact + uploading: progress only, small Stop for cancel.
            if (UploadOptionsPanel != null)
            {
                UploadOptionsPanel.Visibility = Visibility.Collapsed;
            }

            if (UploadActionButtonsRow != null)
            {
                UploadActionButtonsRow.Visibility = Visibility.Collapsed;
            }

            if (CompactStopButton != null)
            {
                CompactStopButton.Visibility = Visibility.Visible;
                CompactStopButton.IsEnabled = true;
            }
        }

        private System.Windows.Media.Brush GetThemeBrush(string resourceKey, System.Windows.Media.Brush fallback)
        {
            if (string.IsNullOrWhiteSpace(resourceKey))
            {
                return fallback;
            }

            return System.Windows.Application.Current?.TryFindResource(resourceKey) as System.Windows.Media.Brush ?? fallback;
        }

        private static System.Windows.Media.Color GetThemeColor(string resourceKey, System.Windows.Media.Color fallback)
        {
            if (string.IsNullOrWhiteSpace(resourceKey))
            {
                return fallback;
            }

            return System.Windows.Application.Current?.TryFindResource(resourceKey) is System.Windows.Media.SolidColorBrush brush
                ? brush.Color
                : fallback;
        }

        private async void DirectUploadWorkspaceControl_Loaded(object sender, RoutedEventArgs e)
        {
            ThemeService.Instance.ThemeChanged -= OnDeployThemeChanged;
            ThemeService.Instance.ThemeChanged += OnDeployThemeChanged;
            ApplyCompactMode(CompactMode);
            await LoadProjectFilesAsync();
            CheckSessionStatus();
            EnsureAutoDiskSyncTimer();
            ApplyAutoDiskSyncState();
        }

        private void DirectUploadWorkspaceControl_Unloaded(object sender, RoutedEventArgs e)
        {
            ThemeService.Instance.ThemeChanged -= OnDeployThemeChanged;
            StopAutoDiskSyncTimer();
        }

        public Task RefreshFromDiskPublicAsync() => RefreshFromDiskAsync();

        public Task RefreshGitOverlayPublicAsync() => ApplyGitOverlayAsync();

        private void EnsureAutoDiskSyncTimer()
        {
            if (_autoDiskSyncTimer != null)
            {
                return;
            }

            _autoDiskSyncTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(AutoDiskSyncSeconds)
            };
            _autoDiskSyncTimer.Tick += AutoDiskSyncTimer_Tick;
        }

        private void StopAutoDiskSyncTimer()
        {
            if (_autoDiskSyncTimer == null)
            {
                return;
            }

            _autoDiskSyncTimer.Stop();
            _autoDiskSyncTimer.Tick -= AutoDiskSyncTimer_Tick;
            _autoDiskSyncTimer = null;
        }

        private void AutoDiskSyncCheck_Changed(object sender, RoutedEventArgs e)
        {
            ApplyAutoDiskSyncState();
        }

        private void ApplyAutoDiskSyncState()
        {
            EnsureAutoDiskSyncTimer();
            if (_autoDiskSyncTimer == null || AutoDiskSyncCheck == null)
            {
                return;
            }

            if (AutoDiskSyncCheck.IsChecked == true)
            {
                _autoDiskSyncTimer.Start();
                // Keep status quiet — background sync should feel invisible.
            }
            else
            {
                _autoDiskSyncTimer.Stop();
            }
        }

        private void AutoDiskSyncTimer_Tick(object? sender, EventArgs e)
        {
            if (_isUploading || _isRefreshingFromDisk || AutoDiskSyncCheck?.IsChecked != true)
            {
                return;
            }

            _ = RefreshFromDiskAsync(preserveSelection: true, quiet: true);
        }

        private void CheckSessionStatus()
        {
            if (!TryRefreshProjectPath())
            {
                SessionStatusText.Text = "";
                DeleteSessionButton.Visibility = Visibility.Collapsed;
                return;
            }

            string sessionPath = Path.Combine(_projectPath, SessionFileName);
            if (File.Exists(sessionPath))
            {
                try
                {
                    int lineCount = File.ReadLines(sessionPath).Count();
                    if (lineCount > 0)
                    {
                        SessionStatusText.Text = $"{lineCount} files in session cache";
                        DeleteSessionButton.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        SessionStatusText.Text = "";
                        DeleteSessionButton.Visibility = Visibility.Collapsed;
                    }
                }
                catch
                {
                    SessionStatusText.Text = "";
                    DeleteSessionButton.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                SessionStatusText.Text = "";
                DeleteSessionButton.Visibility = Visibility.Collapsed;
            }
        }

        private void DeleteSessionButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryRefreshProjectPath())
            {
                ModernMessageBox.Show("No project selected.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (ModernMessageBox.Show("Are you sure you want to clear the upload session history?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning))
            {
                try
                {
                    string sessionPath = Path.Combine(_projectPath, SessionFileName);
                    if (File.Exists(sessionPath)) File.Delete(sessionPath);
                    CheckSessionStatus();
                    ModernMessageBox.Show("Session history cleared.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    ModernMessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async Task LoadProjectFilesAsync(HashSet<string>? restoreCheckedPaths = null, bool quiet = false)
        {
            try
            {
                var config = LoadCurrentProjectConfig(out bool hasProject);
                if (!hasProject)
                {
                    if (!quiet)
                    {
                        StatusText.Text = "No project selected.";
                    }

                    StartUploadButton.IsEnabled = false;
                    UpdateConnectionInfoBanner(null, skipProjectRefresh: true);
                    return;
                }

                var profile = ResolveConnectionProfile(config.ConnectionProfileId);
                var mapping = GetPrimaryMapping(profile);
                var roots = ResolveRoots(config, mapping);
                _mappedLocalRoot = mapping != null && !string.Equals(roots.localRoot, _projectPath, StringComparison.OrdinalIgnoreCase)
                    ? roots.localRoot
                    : string.Empty;
                _scanRootPath = _projectPath;
                _profileRemoteBasePath = NormalizeRemoteBase(profile?.RemotePath ?? config.RemotePath);
                _activeRemoteBasePath = roots.remoteRoot;

                if (!quiet)
                {
                    UpdateConnectionInfoBanner(config, skipProjectRefresh: true, profileOverride: profile, mappingOverride: mapping);
                    StatusText.Text = "Scanning files...";
                    StartUploadButton.IsEnabled = false;
                }

                var projectRoot = _projectPath;
                var mappedLocal = _mappedLocalRoot;
                var hadItems = _items.Count > 0;
                var rootItems = await Task.Run(() => ScanDirectory(projectRoot));

                // In-place merge keeps expanded folders / checkboxes / TreeView containers stable.
                MergeTreeItems(_items, rootItems, parent: null);
                // Theme brushes must be applied on the UI thread (live palette brushes are not BG-safe).
                RefreshTreeThemeBrushes(_items);

                if (!string.IsNullOrEmpty(mappedLocal))
                {
                    // Only auto-expand the mapped path on first populate; never fight the user on refresh.
                    MarkMappedFolder(_items, mappedLocal, expandPath: !hadItems);
                }

                if (restoreCheckedPaths != null && restoreCheckedPaths.Count > 0)
                {
                    RestoreCheckedRelativePaths(_items, restoreCheckedPaths);
                }

                await ApplyGitOverlayAsync();

                UpdateStats();
                if (!quiet)
                {
                    StatusText.Text = "Ready.";
                    StartUploadButton.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                if (!quiet)
                {
                    StatusText.Text = $"Error scanning files: {ex.Message}";
                    StartUploadButton.IsEnabled = true;
                }
            }
        }

        private static string NormalizeTreePathKey(string path)
        {
            try
            {
                return Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .ToLowerInvariant();
            }
            catch
            {
                return (path ?? string.Empty).Trim().ToLowerInvariant();
            }
        }

        /// <summary>
        /// Merge a fresh disk scan into the live tree without rebuilding nodes that still exist,
        /// so expanded folders and selection stay put (Explorer-style).
        /// </summary>
        private void MergeTreeItems(
            ObservableCollection<FileSystemItem> target,
            IReadOnlyList<FileSystemItem> scanned,
            FileSystemItem? parent)
        {
            var existingByPath = new Dictionary<string, FileSystemItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in target)
            {
                existingByPath[NormalizeTreePathKey(item.FullPath)] = item;
            }

            var next = new List<FileSystemItem>(scanned.Count);
            foreach (var fresh in scanned)
            {
                var key = NormalizeTreePathKey(fresh.FullPath);
                if (existingByPath.TryGetValue(key, out var existing))
                {
                    existing.ApplyDiskSnapshot(fresh);
                    if (existing.IsFolder)
                    {
                        MergeTreeItems(existing.Children, fresh.Children.ToList(), existing);
                    }

                    next.Add(existing);
                    existingByPath.Remove(key);
                }
                else
                {
                    fresh.Parent = parent;
                    if (fresh.Children != null)
                    {
                        foreach (var child in fresh.Children)
                        {
                            child.Parent = fresh;
                        }
                    }

                    next.Add(fresh);
                }
            }

            for (var i = target.Count - 1; i >= 0; i--)
            {
                if (!next.Contains(target[i]))
                {
                    target.RemoveAt(i);
                }
            }

            for (var i = 0; i < next.Count; i++)
            {
                var item = next[i];
                var currentIndex = target.IndexOf(item);
                if (currentIndex < 0)
                {
                    target.Insert(Math.Min(i, target.Count), item);
                }
                else if (currentIndex != i)
                {
                    target.Move(currentIndex, i);
                }
            }
        }

        private (HashSet<string> softExactNames, List<string> softPatterns) LoadSoftIgnoreRules()
        {
            var softExactNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var softPatterns = new List<string>();

            try
            {
                string gitIgnorePath = Path.Combine(_projectPath, ".gitignore");
                if (!File.Exists(gitIgnorePath))
                {
                    return (softExactNames, softPatterns);
                }

                foreach (var line in File.ReadAllLines(gitIgnorePath))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith("!"))
                    {
                        continue;
                    }

                    if (trimmed.Contains('*') || trimmed.Contains('?'))
                    {
                        var clean = trimmed.TrimStart('/', '\\');
                        if (!string.IsNullOrWhiteSpace(clean))
                        {
                            softPatterns.Add(clean);
                        }
                    }
                    else
                    {
                        var clean = trimmed.TrimStart('/', '\\').TrimEnd('/', '\\');
                        if (!string.IsNullOrWhiteSpace(clean) && !HardExcludeNames.Contains(clean))
                        {
                            softExactNames.Add(clean);
                        }
                    }
                }
            }
            catch
            {
                // ignore unreadable .gitignore
            }

            return (softExactNames, softPatterns);
        }

        private bool IsSoftIgnoredName(string name, HashSet<string> softExactNames, List<string> softPatterns)
        {
            if (softExactNames.Contains(name))
            {
                return true;
            }

            foreach (var pattern in softPatterns)
            {
                if (MatchesPattern(name, pattern))
                {
                    return true;
                }
            }

            return false;
        }

        private List<FileSystemItem> ScanDirectory(string path)
        {
            var (softExactNames, softPatterns) = LoadSoftIgnoreRules();
            return ScanDirectory(path, softExactNames, softPatterns);
        }

        private List<FileSystemItem> ScanDirectory(string path, HashSet<string> softExactNames, List<string> softPatterns)
        {
            var items = new List<FileSystemItem>();

            try
            {
                var dirInfo = new DirectoryInfo(path);

                foreach (var dir in dirInfo.GetDirectories())
                {
                    if (HardExcludeNames.Contains(dir.Name) || dir.Attributes.HasFlag(FileAttributes.Hidden))
                    {
                        continue;
                    }

                    var softIgnored = IsSoftIgnoredName(dir.Name, softExactNames, softPatterns);
                    // Do not touch live WPF palette brushes here — ScanDirectory runs on a worker thread.
                    var item = new FileSystemItem
                    {
                        Name = dir.Name,
                        FullPath = dir.FullName,
                        IsFolder = true,
                        Icon = "📁",
                        IconColor = ThemeService.Instance.GetTokenBrush(
                            "directUpload.folderIcon",
                            System.Windows.Media.Colors.Orange),
                        GitState = softIgnored ? GitItemState.Ignored : GitItemState.None
                    };

                    item.Children = new ObservableCollection<FileSystemItem>(
                        ScanDirectory(dir.FullName, softExactNames, softPatterns));

                    foreach (var child in item.Children)
                    {
                        child.Parent = item;
                    }

                    // Always show folders (including empty ones created via New Folder).
                    items.Add(item);
                }

                foreach (var file in dirInfo.GetFiles())
                {
                    if (HardExcludeNames.Contains(file.Name) || file.Attributes.HasFlag(FileAttributes.Hidden))
                    {
                        continue;
                    }

                    var softIgnored = IsSoftIgnoredName(file.Name, softExactNames, softPatterns);
                    var item = new FileSystemItem
                    {
                        Name = file.Name,
                        FullPath = file.FullName,
                        IsFolder = false,
                        Icon = "📄",
                        IconColor = ThemeService.Instance.GetTokenBrush(
                            "directUpload.fileIcon",
                            System.Windows.Media.Colors.LightGray),
                        Size = file.Length,
                        GitState = softIgnored ? GitItemState.Ignored : GitItemState.None
                    };
                    items.Add(item);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }

            return items;
        }

        private static void ClearMappedFolderFlags(IEnumerable<FileSystemItem> items)
        {
            foreach (var item in items)
            {
                item.IsMappedFolder = false;
                if (item.Children != null && item.Children.Count > 0)
                {
                    ClearMappedFolderFlags(item.Children);
                }
            }
        }

        private static void MarkMappedFolder(IEnumerable<FileSystemItem> items, string mappedLocalRoot, bool expandPath = true)
        {
            ClearMappedFolderFlags(items);

            string target;
            try
            {
                target = Path.GetFullPath(mappedLocalRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return;
            }

            MarkMappedFolderCore(items, target, expandPath);
        }

        private static bool MarkMappedFolderCore(IEnumerable<FileSystemItem> items, string target, bool expandPath)
        {
            foreach (var item in items)
            {
                if (!item.IsFolder)
                {
                    continue;
                }

                string full;
                try
                {
                    full = Path.GetFullPath(item.FullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                catch
                {
                    continue;
                }

                if (string.Equals(full, target, StringComparison.OrdinalIgnoreCase))
                {
                    item.IsMappedFolder = true;
                    if (expandPath)
                    {
                        item.IsExpanded = true;
                    }

                    return true;
                }

                if (item.Children != null && item.Children.Count > 0 && MarkMappedFolderCore(item.Children, target, expandPath))
                {
                    if (expandPath)
                    {
                        item.IsExpanded = true;
                    }

                    return true;
                }
            }

            return false;
        }

        private async Task ApplyGitOverlayAsync()
        {
            if (string.IsNullOrWhiteSpace(_projectPath) || !Directory.Exists(_projectPath))
            {
                return;
            }

            Dictionary<string, GitItemState> overlay;
            try
            {
                GitService.SetWorkingDirectory(_projectPath);
                overlay = await _gitService.GetWorkingTreeOverlayAsync(_projectPath);
            }
            catch
            {
                return;
            }

            ApplyOverlayToItems(_items, _projectPath, overlay);
            BubbleFolderGitState(_items);
        }

        private static void ApplyOverlayToItems(
            IEnumerable<FileSystemItem> items,
            string projectRoot,
            Dictionary<string, GitItemState> overlay)
        {
            foreach (var item in items)
            {
                string relative;
                try
                {
                    relative = Path.GetRelativePath(projectRoot, item.FullPath).Replace("\\", "/");
                }
                catch
                {
                    relative = item.Name;
                }

                if (overlay.TryGetValue(relative, out var state))
                {
                    item.GitState = state;
                }
                else if (item.GitState != GitItemState.Ignored)
                {
                    // Clear stale bubble/modified state when git no longer reports this path.
                    item.GitState = GitItemState.None;
                }

                if (item.Children != null && item.Children.Count > 0)
                {
                    ApplyOverlayToItems(item.Children, projectRoot, overlay);
                }
            }
        }

        private static void BubbleFolderGitState(IEnumerable<FileSystemItem> items)
        {
            foreach (var item in items)
            {
                if (item.Children == null || item.Children.Count == 0)
                {
                    continue;
                }

                BubbleFolderGitState(item.Children);

                if (!item.IsFolder)
                {
                    continue;
                }

                var bubbled = AggregateChildGitState(item.Children);
                if (bubbled != GitItemState.None)
                {
                    item.GitState = PickDirtierGitState(item.GitState, bubbled);
                }
            }
        }

        private static GitItemState AggregateChildGitState(IEnumerable<FileSystemItem> children)
        {
            var best = GitItemState.None;
            foreach (var child in children)
            {
                if (IsDirtier(child.GitState, best))
                {
                    best = child.GitState;
                }
            }

            return best;
        }

        /// <summary>Higher rank = dirtier / more important for folder inheritance.</summary>
        private static int GitStateRank(GitItemState state) => state switch
        {
            GitItemState.Conflicted => 5,
            GitItemState.Modified => 4,
            GitItemState.Untracked => 3,
            GitItemState.Clean => 2,
            GitItemState.Ignored => 1,
            _ => 0
        };

        private static bool IsDirtier(GitItemState candidate, GitItemState current) =>
            GitStateRank(candidate) > GitStateRank(current);

        private static GitItemState PickDirtierGitState(GitItemState current, GitItemState candidate) =>
            IsDirtier(candidate, current) ? candidate : current;

        private bool MatchesPattern(string name, string pattern)
        {
            // Simple wildcard matching: * matches any sequence, ? matches single char
            // Convert gitignore pattern to regex-like matching
            pattern = pattern.Replace(".", "\\.").Replace("*", ".*").Replace("?", ".");
            
            try
            {
                return System.Text.RegularExpressions.Regex.IsMatch(name, "^" + pattern + "$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isUploading)
            {
                StatusText.Text = "Wait for upload to finish before refreshing.";
                return;
            }

            _ = RefreshFromDiskAsync(preserveSelection: true, quiet: false);
        }

        private Task RefreshFromDiskAsync() => RefreshFromDiskAsync(preserveSelection: true, quiet: false);

        private async Task RefreshFromDiskAsync(bool preserveSelection, bool quiet)
        {
            if (_isUploading || _isRefreshingFromDisk)
            {
                if (!quiet)
                {
                    StatusText.Text = "Wait for upload to finish before refreshing.";
                }

                return;
            }

            if (!quiet)
            {
                RefreshButton.IsEnabled = false;
            }

            _isRefreshingFromDisk = true;
            HashSet<string>? checkedPaths = null;
            try
            {
                // Always keep checkboxes when possible; in-place merge keeps expansion open.
                if (preserveSelection || quiet)
                {
                    checkedPaths = CollectCheckedRelativePaths(_items);
                }

                if (!quiet)
                {
                    StatusText.Text = "Refreshing from disk...";
                }

                await LoadProjectFilesAsync(checkedPaths, quiet);

                if (!quiet)
                {
                    CheckSessionStatus();
                    if (string.Equals(StatusText.Text, "Ready.", StringComparison.Ordinal)
                        || string.Equals(StatusText.Text, "Refreshing from disk...", StringComparison.Ordinal))
                    {
                        StatusText.Text = "Refreshed from disk.";
                    }
                }
            }
            finally
            {
                _isRefreshingFromDisk = false;
                if (!quiet)
                {
                    RefreshButton.IsEnabled = !_isUploading;
                }
            }
        }

        private HashSet<string> CollectCheckedRelativePaths(IEnumerable<FileSystemItem> items)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectCheckedRelativePaths(items, paths);
            return paths;
        }

        private void CollectCheckedRelativePaths(IEnumerable<FileSystemItem> items, HashSet<string> paths)
        {
            var root = !string.IsNullOrEmpty(_projectPath) ? _projectPath : _scanRootPath;
            foreach (var item in items)
            {
                if (item.IsChecked == true && !string.IsNullOrWhiteSpace(item.FullPath) && !string.IsNullOrEmpty(root))
                {
                    try
                    {
                        var relative = Path.GetRelativePath(root, item.FullPath).Replace('\\', '/');
                        if (!string.IsNullOrWhiteSpace(relative) && relative != ".")
                        {
                            paths.Add(relative);
                        }
                    }
                    catch
                    {
                        // Ignore path mapping errors.
                    }
                }

                if (item.Children != null && item.Children.Count > 0)
                {
                    CollectCheckedRelativePaths(item.Children, paths);
                }
            }
        }

        private void RestoreCheckedRelativePaths(IEnumerable<FileSystemItem> items, HashSet<string> checkedPaths)
        {
            var root = !string.IsNullOrEmpty(_projectPath) ? _projectPath : _scanRootPath;
            foreach (var item in items)
            {
                if (item.Children != null && item.Children.Count > 0)
                {
                    RestoreCheckedRelativePaths(item.Children, checkedPaths);
                }

                if (string.IsNullOrEmpty(root) || string.IsNullOrWhiteSpace(item.FullPath))
                {
                    continue;
                }

                try
                {
                    var relative = Path.GetRelativePath(root, item.FullPath).Replace('\\', '/');
                    if (!checkedPaths.Contains(relative))
                    {
                        continue;
                    }

                    // Only restore leaves / empty folders so parent cascade does not over-select.
                    if (!item.IsFolder || item.Children == null || item.Children.Count == 0)
                    {
                        item.IsChecked = true;
                    }
                }
                catch
                {
                    // Ignore.
                }
            }
        }

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _items) item.IsChecked = true;
            UpdateStats();
        }

        private void DeselectAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _items) item.IsChecked = false;
            UpdateStats();
        }

        private void ItemCheckBox_Click(object sender, RoutedEventArgs e)
        {
            // The binding handles the value update, but we need to trigger stats update
            // Also the logic inside FileSystemItem handles cascading checks
            UpdateStats();
        }

        private void FileTreeView_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != Key.V || (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
            {
                return;
            }

            if (!System.Windows.Clipboard.ContainsFileDropList())
            {
                return;
            }

            e.Handled = true;
            _ = PasteClipboardFilesAsync();
        }

        private void FileTreeView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_isUploading)
            {
                return;
            }

            var treeItem = FindParent<TreeViewItem>(e.OriginalSource as DependencyObject);
            if (treeItem?.DataContext is not FileSystemItem item)
            {
                return;
            }

            if (item.IsFolder)
            {
                return;
            }

            // Ignore double-click on checkbox.
            if (FindParent<System.Windows.Controls.CheckBox>(e.OriginalSource as DependencyObject) != null)
            {
                return;
            }

            e.Handled = true;
            OpenLocalFileInEditor(item.FullPath);
        }

        private void FileTreeView_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var treeItem = FindParent<TreeViewItem>(e.OriginalSource as DependencyObject);
            if (treeItem?.DataContext is not FileSystemItem item)
            {
                return;
            }

            treeItem.IsSelected = true;
            treeItem.Focus();

            var actions = new List<AppContextMenuAction>();
            var canPaste = !_isUploading && System.Windows.Clipboard.ContainsFileDropList();

            actions.Add(new AppContextMenuAction
            {
                Id = "new-folder",
                Label = "New Folder",
                IconGlyph = "📁",
                IsEnabled = !_isUploading,
                Execute = _ => CreateLocalFolder(item)
            });
            actions.Add(new AppContextMenuAction
            {
                Id = "new-file",
                Label = "New File",
                IconGlyph = "📄",
                IsEnabled = !_isUploading,
                Execute = _ => _ = CreateLocalFileAsync(item)
            });

            if (item.IsFolder)
            {
                actions.Add(new AppContextMenuAction
                {
                    Id = "open-in-explorer",
                    Label = "Open in Explorer",
                    IconGlyph = "📂",
                    Execute = _ => OpenFolderInExplorer(item.FullPath)
                });
                actions.Add(new AppContextMenuAction
                {
                    Id = "refresh-folder",
                    Label = "Refresh",
                    IconGlyph = "🔄",
                    IsEnabled = !_isUploading,
                    Execute = _ => _ = RefreshFolderAsync(item)
                });
                actions.Add(new AppContextMenuAction
                {
                    Id = "paste-files",
                    Label = "Paste",
                    IconGlyph = "📋",
                    IsEnabled = canPaste,
                    Execute = _ => _ = PasteClipboardFilesAsync()
                });
            }
            else
            {
                actions.Add(new AppContextMenuAction
                {
                    Id = "edit-file",
                    Label = "Edit",
                    IconGlyph = "✏",
                    IsEnabled = !_isUploading,
                    Execute = _ => OpenLocalFileInEditor(item.FullPath)
                });
                actions.Add(new AppContextMenuAction
                {
                    Id = "upload-file",
                    Label = "Upload file",
                    IconGlyph = "🚀",
                    IsEnabled = !_isUploading,
                    Execute = _ => _ = UploadSpecificFilesAsync(new[] { item }, skipConfirm: true)
                });
                actions.Add(new AppContextMenuAction
                {
                    Id = "open-file-location",
                    Label = "Open in Explorer",
                    IconGlyph = "📂",
                    Execute = _ =>
                    {
                        var dir = Path.GetDirectoryName(item.FullPath);
                        if (!string.IsNullOrWhiteSpace(dir))
                        {
                            OpenFolderInExplorer(dir);
                        }
                    }
                });
                actions.Add(new AppContextMenuAction
                {
                    Id = "paste-files",
                    Label = "Paste",
                    IconGlyph = "📋",
                    IsEnabled = canPaste,
                    Execute = _ => _ = PasteClipboardFilesAsync()
                });
            }

            if (IsProjectGitRepository())
            {
                actions.Add(AppContextMenuAction.Separator("git-separator"));
                actions.Add(BuildGitContextMenu(item));
            }

            actions.Add(AppContextMenuAction.Separator("delete-separator"));
            actions.Add(new AppContextMenuAction
            {
                Id = "delete-local",
                Label = "Delete",
                IconGlyph = "🗑",
                IsEnabled = !_isUploading,
                IsDestructive = true,
                Execute = _ => _ = DeleteLocalItemAsync(item)
            });

            if (GlobalContextMenuService.ShowMenu(treeItem, actions, item, PlacementMode.MousePoint))
            {
                e.Handled = true;
            }
        }

        private bool IsProjectGitRepository()
        {
            if (string.IsNullOrWhiteSpace(_projectPath) || !Directory.Exists(_projectPath))
            {
                return false;
            }

            try
            {
                GitService.SetWorkingDirectory(_projectPath);
                return _gitService.IsGitRepository();
            }
            catch
            {
                return Directory.Exists(Path.Combine(_projectPath, ".git"));
            }
        }

        private AppContextMenuAction BuildGitContextMenu(FileSystemItem item)
        {
            var relative = TryGetProjectRelativePath(item.FullPath);
            var hasRelative = !string.IsNullOrWhiteSpace(relative);
            var gitEnabled = !_isUploading && IsProjectGitRepository();

            var children = new List<AppContextMenuAction>
            {
                new()
                {
                    Id = "git-commit-this",
                    Label = item.IsFolder ? "Commit this folder…" : "Commit this file…",
                    IconGlyph = "📝",
                    IsEnabled = gitEnabled && hasRelative,
                    Execute = _ => _ = GitCommitPathAsync(item)
                },
                new()
                {
                    Id = "git-commit-all",
                    Label = "Commit all changes…",
                    IconGlyph = "📦",
                    IsEnabled = gitEnabled,
                    Execute = _ => _ = GitCommitAllAsync()
                },
                AppContextMenuAction.Separator("git-remote-sep"),
                new()
                {
                    Id = "git-push",
                    Label = "Push",
                    IconGlyph = "☁",
                    IsEnabled = gitEnabled,
                    Execute = _ => _ = GitPushAsync()
                },
                new()
                {
                    Id = "git-pull",
                    Label = "Pull",
                    IconGlyph = "⬇",
                    IsEnabled = gitEnabled,
                    Execute = _ => _ = GitPullAsync()
                },
                AppContextMenuAction.Separator("git-ignore-sep"),
                new()
                {
                    Id = "git-ignore",
                    Label = "Add to .gitignore",
                    IconGlyph = "🚫",
                    IsEnabled = gitEnabled && hasRelative,
                    Execute = _ => _ = GitAddToIgnoreAsync(item)
                },
                new()
                {
                    Id = "git-discard",
                    Label = "Discard local changes",
                    IconGlyph = "↺",
                    IsEnabled = gitEnabled && hasRelative && !item.IsFolder,
                    IsDestructive = true,
                    Execute = _ => _ = GitDiscardPathAsync(item)
                }
            };

            return new AppContextMenuAction
            {
                Id = "git-menu",
                Label = "Git",
                IconGlyph = "🌿",
                IsEnabled = gitEnabled,
                Children = children
            };
        }

        private string? TryGetProjectRelativePath(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(_projectPath))
            {
                return null;
            }

            try
            {
                var relative = Path.GetRelativePath(_projectPath, fullPath).Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(relative) || relative == "." || relative.StartsWith("..", StringComparison.Ordinal))
                {
                    return null;
                }

                return relative.TrimStart('/');
            }
            catch
            {
                return null;
            }
        }

        private async Task GitCommitPathAsync(FileSystemItem item)
        {
            if (_isUploading || !IsProjectGitRepository())
            {
                return;
            }

            var relative = TryGetProjectRelativePath(item.FullPath);
            if (string.IsNullOrWhiteSpace(relative))
            {
                ModernMessageBox.Show("This path is outside the project root.", "Git", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            GitService.SetWorkingDirectory(_projectPath);
            if (!await _gitService.HasCommittableChangesUnderPathsAsync(new[] { relative }))
            {
                ModernMessageBox.Show(
                    "No committable changes under this path.\nFiles may be gitignored or already committed.",
                    "Git",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var dialog = new InputDialog(
                $"Commit message for {(item.IsFolder ? "folder" : "file")}:\n{relative}",
                "Commit",
                $"update {Path.GetFileName(relative.TrimEnd('/'))}");
            if (WindowOwnerService.ShowDialogOwned(dialog, this) != true)
            {
                return;
            }

            var message = (dialog.ResponseText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(message))
            {
                message = $"update {Path.GetFileName(relative.TrimEnd('/'))}";
            }

            try
            {
                GitService.SetWorkingDirectory(_projectPath);
                StatusText.Text = $"Committing {relative}…";
                await _gitService.CommitSpecificPathsAsync(new[] { relative }, message);
                StatusText.Text = $"Committed {relative}.";
                await ApplyGitOverlayAsync();
                ModernMessageBox.Show($"Committed:\n{relative}", "Git", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Commit failed.";
                ModernMessageBox.Show($"Commit failed:\n{ex.Message}", "Git", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task GitCommitAllAsync()
        {
            if (_isUploading || !IsProjectGitRepository())
            {
                return;
            }

            try
            {
                GitService.SetWorkingDirectory(_projectPath);
                var changes = await _gitService.GetUncommittedChangesAsync(includeDiff: false);
                if (changes.Count == 0)
                {
                    ModernMessageBox.Show("No uncommitted changes.", "Git", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var dialog = new InputDialog(
                    $"Commit message for {changes.Count} changed file(s):",
                    "Commit all",
                    $"update {AppTimeService.LocalNow:yyyy-MM-dd HH:mm}");
                if (WindowOwnerService.ShowDialogOwned(dialog, this) != true)
                {
                    return;
                }

                var message = (dialog.ResponseText ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(message))
                {
                    message = $"update {AppTimeService.LocalNow:yyyy-MM-dd HH:mm}";
                }

                StatusText.Text = "Committing all changes…";
                await _gitService.CommitChangesAsync(message);
                StatusText.Text = "Committed all changes.";
                await ApplyGitOverlayAsync();
                ModernMessageBox.Show($"Committed {changes.Count} file(s).", "Git", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Commit failed.";
                ModernMessageBox.Show($"Commit failed:\n{ex.Message}", "Git", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task GitPushAsync()
        {
            if (_isUploading || !IsProjectGitRepository())
            {
                return;
            }

            try
            {
                GitService.SetWorkingDirectory(_projectPath);
                StatusText.Text = "Pushing…";
                var result = await _gitService.PushOrSkipAsync();
                StatusText.Text = result == PushExecutionResult.PushedToRemote
                    ? "Push completed."
                    : "Push skipped (no remote).";
                ModernMessageBox.Show(
                    result == PushExecutionResult.PushedToRemote
                        ? "Pushed to remote successfully."
                        : "No remote configured. Push skipped.",
                    "Git",
                    MessageBoxButton.OK,
                    result == PushExecutionResult.PushedToRemote ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Push failed.";
                ModernMessageBox.Show($"Push failed:\n{ex.Message}", "Git", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task GitPullAsync()
        {
            if (_isUploading || !IsProjectGitRepository())
            {
                return;
            }

            try
            {
                GitService.SetWorkingDirectory(_projectPath);
                StatusText.Text = "Pulling…";
                await _gitService.PullAsync();
                StatusText.Text = "Pull completed.";
                await RefreshFromDiskAsync(preserveSelection: true, quiet: false);
                await ApplyGitOverlayAsync();
                ModernMessageBox.Show("Pulled latest changes from remote.", "Git", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Pull failed.";
                ModernMessageBox.Show($"Pull failed:\n{ex.Message}", "Git", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task GitAddToIgnoreAsync(FileSystemItem item)
        {
            if (_isUploading || !IsProjectGitRepository())
            {
                return;
            }

            var relative = TryGetProjectRelativePath(item.FullPath);
            if (string.IsNullOrWhiteSpace(relative))
            {
                ModernMessageBox.Show("Unable to build .gitignore pattern for this path.", "Git", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var ignoreEntry = item.IsFolder
                ? relative.TrimEnd('/') + "/"
                : relative;

            var confirm = ModernMessageBox.ShowWithResult(
                $"Add '{ignoreEntry}' to .gitignore?",
                "Git ignore",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                "Add",
                "Cancel");
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                var gitIgnorePath = Path.Combine(_projectPath, ".gitignore");
                var lines = File.Exists(gitIgnorePath)
                    ? File.ReadAllLines(gitIgnorePath).ToList()
                    : new List<string>();
                var exists = lines.Any(line => string.Equals(line.Trim(), ignoreEntry, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                {
                    lines.Add(ignoreEntry);
                    File.WriteAllLines(gitIgnorePath, lines);
                }

                GitService.SetWorkingDirectory(_projectPath);
                await _gitService.RemovePathFromIndexAsync(ignoreEntry.TrimEnd('/'));
                await RefreshFromDiskAsync(preserveSelection: true, quiet: true);
                await ApplyGitOverlayAsync();

                StatusText.Text = exists
                    ? $"'{ignoreEntry}' already in .gitignore."
                    : $"Added '{ignoreEntry}' to .gitignore.";
                ModernMessageBox.Show(StatusText.Text, "Git", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Failed to update .gitignore:\n{ex.Message}", "Git", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task GitDiscardPathAsync(FileSystemItem item)
        {
            if (_isUploading || item.IsFolder || !IsProjectGitRepository())
            {
                return;
            }

            var relative = TryGetProjectRelativePath(item.FullPath);
            if (string.IsNullOrWhiteSpace(relative))
            {
                return;
            }

            var confirm = ModernMessageBox.ShowWithResult(
                $"Discard local changes to:\n{relative}\n\nThis cannot be undone.",
                "Discard changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                "Discard",
                "Cancel");
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                GitService.SetWorkingDirectory(_projectPath);
                await _gitService.DiscardPathChangesAsync(relative);
                StatusText.Text = $"Discarded changes: {relative}";
                await RefreshFromDiskAsync(preserveSelection: true, quiet: true);
                await ApplyGitOverlayAsync();
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(
                    $"Could not discard changes (file may be untracked):\n{ex.Message}",
                    "Git",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private async Task DeleteLocalItemAsync(FileSystemItem item)
        {
            if (_isUploading || item == null || string.IsNullOrWhiteSpace(item.FullPath))
            {
                return;
            }

            var kind = item.IsFolder ? "folder" : "file";
            var confirm = ModernMessageBox.ShowWithResult(
                $"Delete this {kind}?\n\n{item.Name}\n{item.FullPath}\n\nThis cannot be undone.",
                "Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                "Delete",
                "Cancel");
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                var parentPath = item.Parent?.FullPath
                    ?? Path.GetDirectoryName(item.FullPath)
                    ?? _projectPath;

                if (item.IsFolder)
                {
                    if (Directory.Exists(item.FullPath))
                    {
                        Directory.Delete(item.FullPath, recursive: true);
                    }
                }
                else if (File.Exists(item.FullPath))
                {
                    File.Delete(item.FullPath);
                }

                var parentFolder = item.Parent ?? FindFolderItemByPath(parentPath ?? string.Empty);
                if (parentFolder != null)
                {
                    await RefreshFolderAsync(parentFolder);
                }
                else
                {
                    await RefreshFromDiskAsync(preserveSelection: true, quiet: false);
                }

                StatusText.Text = $"Deleted {item.Name}";
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Delete failed:\n{ex.Message}", "Direct Upload", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string ResolveCreateParentDirectory(FileSystemItem item)
        {
            if (item.IsFolder)
            {
                return item.FullPath;
            }

            return Path.GetDirectoryName(item.FullPath) ?? item.FullPath;
        }

        private FileSystemItem? FindFolderItemByPath(string folderPath)
        {
            string target;
            try
            {
                target = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return null;
            }

            FileSystemItem? Search(IEnumerable<FileSystemItem> items)
            {
                foreach (var entry in items)
                {
                    if (!entry.IsFolder)
                    {
                        continue;
                    }

                    string full;
                    try
                    {
                        full = Path.GetFullPath(entry.FullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    }
                    catch
                    {
                        continue;
                    }

                    if (string.Equals(full, target, StringComparison.OrdinalIgnoreCase))
                    {
                        return entry;
                    }

                    var nested = Search(entry.Children);
                    if (nested != null)
                    {
                        return nested;
                    }
                }

                return null;
            }

            return Search(_items);
        }

        private void CreateLocalFolder(FileSystemItem contextItem)
        {
            if (_isUploading)
            {
                return;
            }

            var parentDir = ResolveCreateParentDirectory(contextItem);
            var dialog = new InputDialog("Enter folder name:", "New Folder", "new-folder");
            if (WindowOwnerService.ShowDialogOwned(dialog, this) != true)
            {
                return;
            }

            var name = (dialog.ResponseText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name)
                || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || name.Contains('/')
                || name.Contains('\\'))
            {
                ModernMessageBox.Show("Enter a valid folder name without path separators.", "New Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var destination = Path.Combine(parentDir, name);
            try
            {
                if (Directory.Exists(destination))
                {
                    ModernMessageBox.Show("A folder with that name already exists.", "New Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Directory.CreateDirectory(destination);
                var refreshTarget = FindFolderItemByPath(parentDir) ?? (contextItem.IsFolder ? contextItem : contextItem.Parent);
                if (refreshTarget != null)
                {
                    refreshTarget.IsExpanded = true;
                    _ = RefreshFolderAsync(refreshTarget).ContinueWith(_ =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            var created = FindFolderItemByPath(destination);
                            if (created != null && created.GitState == GitItemState.None)
                            {
                                created.GitState = GitItemState.Untracked;
                            }
                        });
                    }, TaskScheduler.Default);
                }
                else
                {
                    _ = RefreshFromDiskAsync();
                }

                StatusText.Text = $"Created folder {name}";
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Could not create folder:\n{ex.Message}", "Direct Upload", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task CreateLocalFileAsync(FileSystemItem contextItem)
        {
            if (_isUploading)
            {
                return;
            }

            var parentDir = ResolveCreateParentDirectory(contextItem);
            var dialog = new NewFileDialog();
            if (WindowOwnerService.ShowDialogOwned(dialog, this) != true
                || string.IsNullOrWhiteSpace(dialog.FileName))
            {
                return;
            }

            var fileName = dialog.FileName.Trim();
            var destination = Path.Combine(parentDir, fileName);
            try
            {
                if (File.Exists(destination))
                {
                    ModernMessageBox.Show("A file with that name already exists.", "New File", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                await File.WriteAllTextAsync(destination, FileStarterTemplates.GetStarterContent(fileName));

                var refreshTarget = FindFolderItemByPath(parentDir) ?? (contextItem.IsFolder ? contextItem : contextItem.Parent);
                if (refreshTarget != null)
                {
                    await RefreshFolderAsync(refreshTarget);
                }
                else
                {
                    await RefreshFromDiskAsync();
                }

                StatusText.Text = $"Created file {fileName}";
                OpenLocalFileInEditor(destination);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Could not create file:\n{ex.Message}", "Direct Upload", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenLocalFileInEditor(string fullPath)
        {
            if (LocalEditor == null)
            {
                return;
            }

            if (!LocalEditor.TryOpenFile(fullPath, out var error))
            {
                ModernMessageBox.Show($"Could not open file:\n{error}", "Direct Upload", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            StatusText.Text = $"Editing {Path.GetFileName(fullPath)}";
        }

        private FileSystemItem? ResolvePasteTargetFolder()
        {
            if (FileTreeView.SelectedItem is not FileSystemItem selected)
            {
                return null;
            }

            if (selected.IsFolder)
            {
                return selected;
            }

            return selected.Parent;
        }

        private async Task PasteClipboardFilesAsync()
        {
            if (_isUploading)
            {
                StatusText.Text = "Wait for upload to finish before pasting.";
                return;
            }

            if (!System.Windows.Clipboard.ContainsFileDropList())
            {
                StatusText.Text = "Clipboard has no files to paste.";
                return;
            }

            var target = ResolvePasteTargetFolder();
            if (target == null || string.IsNullOrWhiteSpace(target.FullPath))
            {
                StatusText.Text = "Select a folder (or file) to paste into.";
                return;
            }

            StringCollection? dropList;
            try
            {
                dropList = System.Windows.Clipboard.GetFileDropList();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Clipboard read failed: {ex.Message}";
                return;
            }

            if (dropList == null || dropList.Count == 0)
            {
                StatusText.Text = "Clipboard has no files to paste.";
                return;
            }

            var sources = dropList.Cast<string>()
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (sources.Count == 0)
            {
                StatusText.Text = "Clipboard has no files to paste.";
                return;
            }

            StatusText.Text = $"Pasting into {target.Name}...";
            StartUploadButton.IsEnabled = false;
            RefreshButton.IsEnabled = false;

            var copied = 0;
            var skipped = 0;
            string? error = null;

            try
            {
                await Task.Run(() =>
                {
                    foreach (var source in sources)
                    {
                        try
                        {
                            if (File.Exists(source))
                            {
                                if (TryCopyFileIntoFolder(source, target.FullPath))
                                {
                                    Interlocked.Increment(ref copied);
                                }
                                else
                                {
                                    Interlocked.Increment(ref skipped);
                                }
                            }
                            else if (Directory.Exists(source))
                            {
                                if (TryCopyDirectoryIntoFolder(source, target.FullPath))
                                {
                                    Interlocked.Increment(ref copied);
                                }
                                else
                                {
                                    Interlocked.Increment(ref skipped);
                                }
                            }
                            else
                            {
                                Interlocked.Increment(ref skipped);
                            }
                        }
                        catch (Exception ex)
                        {
                            error = ex.Message;
                            Interlocked.Increment(ref skipped);
                        }
                    }
                });
            }
            finally
            {
                StartUploadButton.IsEnabled = !_isUploading;
                RefreshButton.IsEnabled = !_isUploading;
            }

            await RefreshFolderAsync(target);

            if (!string.IsNullOrWhiteSpace(error))
            {
                StatusText.Text = $"Pasted {copied}, skipped {skipped}. Last error: {error}";
            }
            else if (skipped > 0)
            {
                StatusText.Text = $"Pasted {copied} item(s), skipped {skipped}.";
            }
            else
            {
                StatusText.Text = $"Pasted {copied} item(s) into {target.Name}.";
            }
        }

        private static bool TryCopyFileIntoFolder(string sourceFile, string destinationFolder)
        {
            if (!Directory.Exists(destinationFolder))
            {
                return false;
            }

            var destPath = GetUniqueDestinationPath(destinationFolder, Path.GetFileName(sourceFile), isDirectory: false);
            if (IsSameOrNestedPath(sourceFile, destPath))
            {
                return false;
            }

            File.Copy(sourceFile, destPath, overwrite: false);
            return true;
        }

        private static bool TryCopyDirectoryIntoFolder(string sourceDir, string destinationFolder)
        {
            if (!Directory.Exists(destinationFolder) || !Directory.Exists(sourceDir))
            {
                return false;
            }

            var folderName = new DirectoryInfo(sourceDir).Name;
            var destRoot = GetUniqueDestinationPath(destinationFolder, folderName, isDirectory: true);

            if (IsSameOrNestedPath(sourceDir, destRoot) || IsSameOrNestedPath(destRoot, sourceDir))
            {
                return false;
            }

            CopyDirectoryRecursive(sourceDir, destRoot);
            return true;
        }

        private static void CopyDirectoryRecursive(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var destFile = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: false);
            }

            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                var destSub = Path.Combine(destDir, Path.GetFileName(dir));
                CopyDirectoryRecursive(dir, destSub);
            }
        }

        private static string GetUniqueDestinationPath(string destinationFolder, string name, bool isDirectory)
        {
            var candidate = Path.Combine(destinationFolder, name);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }

            var baseName = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
            var extension = isDirectory ? string.Empty : Path.GetExtension(name);

            for (var i = 1; i < 10_000; i++)
            {
                var nextName = $"{baseName} ({i}){extension}";
                candidate = Path.Combine(destinationFolder, nextName);
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new IOException($"Unable to find a unique name for '{name}'.");
        }

        private static bool IsSameOrNestedPath(string pathA, string pathB)
        {
            try
            {
                var a = Path.GetFullPath(pathA).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var b = Path.GetFullPath(pathB).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var prefix = a + Path.DirectorySeparatorChar;
                return b.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return true;
            }
        }

        private async Task RefreshFolderAsync(FileSystemItem folder)
        {
            if (folder == null || !folder.IsFolder || _isUploading)
            {
                return;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(folder.FullPath) || !Directory.Exists(folder.FullPath))
                {
                    RemoveFolderFromTree(folder);
                    UpdateStats();
                    StatusText.Text = $"Removed missing folder: {folder.Name}";
                    return;
                }

                StatusText.Text = $"Refreshing {folder.Name}...";
                StartUploadButton.IsEnabled = false;
                RefreshButton.IsEnabled = false;

                var wasExpanded = folder.IsExpanded;
                var path = folder.FullPath;
                var children = await Task.Run(() => ScanDirectory(path));

                // Keep nested open folders; only add/remove/update children in place.
                MergeTreeItems(folder.Children, children, folder);
                RefreshTreeThemeBrushes(folder.Children);
                folder.IsExpanded = wasExpanded;
                folder.CheckParentStatus();
                folder.RefreshUploadStateFromChildren();

                if (!string.IsNullOrEmpty(_mappedLocalRoot))
                {
                    MarkMappedFolder(_items, _mappedLocalRoot, expandPath: false);
                }

                await ApplyGitOverlayAsync();
                UpdateStats();
                StatusText.Text = $"Refreshed {folder.Name}.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Refresh failed: {ex.Message}";
                ModernMessageBox.Show(
                    $"Unable to refresh folder:\n{ex.Message}",
                    "Direct Upload",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                StartUploadButton.IsEnabled = !_isUploading;
                RefreshButton.IsEnabled = !_isUploading;
            }
        }

        private void RemoveFolderFromTree(FileSystemItem folder)
        {
            if (folder.Parent != null)
            {
                folder.Parent.Children.Remove(folder);
                folder.Parent.CheckParentStatus();
                folder.Parent.RefreshUploadStateFromChildren();
                return;
            }

            _items.Remove(folder);
        }

        private void OpenFolderInExplorer(string folderPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                {
                    ModernMessageBox.Show(
                        "Folder not found on disk.",
                        "Direct Upload",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{folderPath}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(
                    $"Unable to open Explorer:\n{ex.Message}",
                    "Direct Upload",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T typedParent)
                {
                    return typedParent;
                }

                child = VisualTreeHelper.GetParent(child);
            }

            return null;
        }

        private void UpdateStats()
        {
            int total = 0;
            int selected = 0;

            void Count(IEnumerable<FileSystemItem> list)
            {
                foreach (var item in list)
                {
                    if (!item.IsFolder)
                    {
                        total++;
                        if (item.IsChecked == true) selected++;
                    }
                    if (item.Children != null) Count(item.Children);
                }
            }

            Count(_items);

            TotalFilesText.Text = total.ToString();
            SelectedFilesText.Text = selected.ToString();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isUploading && _cancellationTokenSource != null)
            {
                _cancellationTokenSource.Cancel();
                StatusText.Text = "Stopping upload...";
                StopButton.IsEnabled = false;
                if (CompactStopButton != null)
                {
                    CompactStopButton.IsEnabled = false;
                }
            }
        }

        private async void StartUploadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isUploading)
            {
                return;
            }

            StatusText.Text = "Collecting selected files...";
            var filesToUpload = new List<FileSystemItem>();
            var foldersToCreate = new List<FileSystemItem>();
            CollectSelectedUploadItems(_items, filesToUpload, foldersToCreate);

            if (!filesToUpload.Any() && !foldersToCreate.Any())
            {
                ModernMessageBox.Show("No files or folders selected.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                StatusText.Text = "Ready.";
                return;
            }

            await UploadSpecificFilesAsync(filesToUpload, foldersToCreate, skipConfirm: false);
        }

        private async Task UploadSpecificFilesAsync(IReadOnlyList<FileSystemItem> filesToUpload, bool skipConfirm)
        {
            await UploadSpecificFilesAsync(filesToUpload, Array.Empty<FileSystemItem>(), skipConfirm);
        }

        private async Task UploadSpecificFilesAsync(
            IReadOnlyList<FileSystemItem> filesToUpload,
            IReadOnlyList<FileSystemItem> foldersToCreate,
            bool skipConfirm)
        {
            filesToUpload ??= Array.Empty<FileSystemItem>();
            foldersToCreate ??= Array.Empty<FileSystemItem>();
            if (_isUploading || (filesToUpload.Count == 0 && foldersToCreate.Count == 0))
            {
                return;
            }

            var config = LoadCurrentProjectConfig(out bool hasProject);
            if (!hasProject)
            {
                ModernMessageBox.Show("No project selected. Go to Settings and pick a workspace first.", "Missing Project", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(config.FtpHost))
            {
                ModernMessageBox.Show("No deployment connection is assigned to this project yet. Open Settings → Connection Manager and select a connection.", "Connection Missing", MessageBoxButton.OK, MessageBoxImage.Warning);
                UpdateConnectionInfoBanner(config, skipProjectRefresh: true);
                return;
            }

            ResetUploadIndicators(filesToUpload);

            // Session / Resume Logic
            HashSet<string> uploadedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string sessionPath = Path.Combine(_projectPath, SessionFileName);
            bool resumeSession = false;

            if (!skipConfirm && UseSessionCheck.IsChecked == true && File.Exists(sessionPath))
            {
                CheckSessionStatus();
                var result = ModernMessageBox.Show(
                    "An incomplete upload session was found.\nDo you want to RESUME from where it left off?",
                    "Resume Upload?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result)
                {
                    resumeSession = true;
                    var lines = File.ReadAllLines(sessionPath);
                    foreach (var line in lines)
                    {
                        uploadedFiles.Add(line.Trim());
                    }
                }
                else
                {
                    File.Delete(sessionPath);
                    CheckSessionStatus();
                }
            }
            else if (!skipConfirm && UseSessionCheck.IsChecked == true)
            {
                if (File.Exists(sessionPath))
                {
                    File.Delete(sessionPath);
                }

                File.Create(sessionPath).Close();
                CheckSessionStatus();
            }

            if (!skipConfirm && !resumeSession)
            {
                var folderHint = foldersToCreate.Count > 0 ? $" + {foldersToCreate.Count} folder(s)" : string.Empty;
                var confirm = ModernMessageBox.Show(
                    $"Start upload of {filesToUpload.Count} files{folderHint} to {config.FtpHost}?",
                    "Confirm Upload",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (!confirm)
                {
                    StatusText.Text = "Ready.";
                    return;
                }
            }

            // Start Upload
            _isUploading = true;
            StartUploadButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            ApplyUploadPanelVisibility();
            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            var progressMax = Math.Max(1, filesToUpload.Count + foldersToCreate.Count);
            UploadProgressBar.Value = 0;
            UploadProgressBar.Maximum = progressMax;
            
            // Clear and initialize upload log
            if (UploadLogTextBox != null)
            {
                UploadLogTextBox.Text = $"=== Upload Started at {AppTimeService.LocalNow:yyyy-MM-dd HH:mm:ss} ===" + Environment.NewLine;
                UploadLogTextBox.Text += $"Total files to upload: {filesToUpload.Count}" + Environment.NewLine;
                if (foldersToCreate.Count > 0)
                {
                    UploadLogTextBox.Text += $"Folders to ensure: {foldersToCreate.Count}" + Environment.NewLine;
                }
                UploadLogTextBox.Text += Environment.NewLine;
            }
            
            try
            {
                using (var client = new AsyncFtpClient(config.FtpHost, config.FtpUsername, EncryptionService.Decrypt(config.FtpPassword), config.FtpPort))
                {
                    // Configure timeout for large files (zip files)
                    client.Config.DataConnectionType = FluentFTP.FtpDataConnectionType.AutoPassive;
                    client.Config.ReadTimeout = 300000; // 5 minutes
                    client.Config.DataConnectionReadTimeout = 300000; // 5 minutes
                    client.Config.RetryAttempts = 3;
                    
                    StatusText.Text = "Connecting...";
                    await client.AutoConnect(token);

                    // Prefer mapped remote when uploading under the mapped local folder;
                    // otherwise use the connection profile remote root.
                    string defaultRemoteBase = !string.IsNullOrWhiteSpace(_activeRemoteBasePath)
                        ? _activeRemoteBasePath
                        : NormalizeRemoteBase(config.RemotePath);
                    string profileRemoteBase = !string.IsNullOrWhiteSpace(_profileRemoteBasePath)
                        ? _profileRemoteBasePath
                        : NormalizeRemoteBase(config.RemotePath);

                    int processed = 0;
                    int skipped = 0;
                    int foldersCreated = 0;

                    foreach (var folder in foldersToCreate)
                    {
                        if (token.IsCancellationRequested) break;

                        processed++;
                        Dispatcher.Invoke(() => UploadProgressBar.Value = processed);

                        ResolveUploadPaths(folder.FullPath, profileRemoteBase, defaultRemoteBase,
                            out string relativePath, out string remoteBasePath);
                        string remotePath = CombineRemotePaths(remoteBasePath, relativePath);
                        StatusText.Text = $"Creating folder: {folder.Name}";
                        AddUploadLog($"[{AppTimeService.LocalNow:HH:mm:ss}] Ensure folder: {folder.Name}");
                        AddUploadLog($"  → Remote Path: {remotePath}");
                        await FtpDirectoryEnsure.EnsureAsync(client, remotePath, token);
                        foldersCreated++;
                        AddUploadLog($"  ✓ Folder ready");
                        AddUploadLog("");
                    }

                    foreach (var file in filesToUpload)
                    {
                        if (token.IsCancellationRequested) break;

                        processed++;
                        Dispatcher.Invoke(() => UploadProgressBar.Value = processed);

                        try
                        {
                            ResolveUploadPaths(file.FullPath, profileRemoteBase, defaultRemoteBase,
                                out string relativePath, out string remoteBasePath);

                            // Check Session Skip
                            if (resumeSession && uploadedFiles.Contains(relativePath))
                            {
                                skipped++;
                                StatusText.Text = $"Skipped (Session): {file.Name}";
                                string skippedRemotePath = CombineRemotePaths(remoteBasePath, relativePath);
                                Dispatcher.Invoke(() =>
                                {
                                    file.UploadState = UploadState.Uploaded;
                                    UpdateUploadDetailText(file.Name, 100, file.Size, file.Size, "Already uploaded (session)");
                                    AddUploadLog($"[{AppTimeService.LocalNow:HH:mm:ss}] Skipped (Session): {file.Name}");
                                    AddUploadLog($"  → Remote Path: {skippedRemotePath}");
                                    AddUploadLog($"  ↺ Already uploaded in previous session");
                                    AddUploadLog("");
                                });
                                continue; 
                            }

                            // Combine remote base with relative path properly
                            string remotePath = CombineRemotePaths(remoteBasePath, relativePath);
                            StatusText.Text = $"Uploading ({processed}/{progressMax}): {file.Name}";
                            Dispatcher.Invoke(() => 
                            {
                                file.UploadState = UploadState.InProgress;
                                // Log upload path to console
                                AddUploadLog($"[{AppTimeService.LocalNow:HH:mm:ss}] Uploading: {file.Name}");
                                AddUploadLog($"  → Remote Path: {remotePath}");
                            });

                            long fileSize = file.Size > 0 ? file.Size : GetFileSizeSafe(file.FullPath);
                            var progressHandler = new Progress<FtpProgress>(ftpProgress =>
                            {
                                var transferred = (long)Math.Max(0, ftpProgress.TransferredBytes);
                                var totalBytes = fileSize > 0 ? fileSize : Math.Max(transferred, 1);
                                double percent = fileSize > 0
                                    ? (double)transferred / totalBytes * 100
                                    : Math.Max(ftpProgress.Progress, 0);
                                Dispatcher.Invoke(() =>
                                {
                                    UpdateUploadDetailText(file.Name, percent, transferred, totalBytes);
                                });
                            });

                            Dispatcher.Invoke(() => UpdateUploadDetailText(file.Name, 0, 0, fileSize));

                            // Create parent directories segment-by-segment (nested new folders).
                            await FtpDirectoryEnsure.EnsureParentOfFileAsync(client, remotePath, token);

                            // Upload
                            var existsMode = OverwriteCheck.IsChecked == true ? FtpRemoteExists.Overwrite : FtpRemoteExists.Skip;
                            await client.UploadFile(file.FullPath, remotePath, existsMode, true, FtpVerify.None, progressHandler, token);

                            Dispatcher.Invoke(() =>
                            {
                                file.UploadState = UploadState.Uploaded;
                                UpdateUploadDetailText(file.Name, 100, fileSize, fileSize, "Completed");
                                AddUploadLog($"  ✓ Uploaded successfully!");
                                AddUploadLog("");
                            });

                            // Log to Session File
                            if (UseSessionCheck.IsChecked == true)
                            {
                                try 
                                { 
                                    File.AppendAllText(sessionPath, relativePath + Environment.NewLine);
                                    Dispatcher.Invoke(() => CheckSessionStatus()); // Live update session count
                                } catch { }
                            }
                        }
                        catch (Exception fileEx)
                        {
                            ResolveUploadPaths(file.FullPath, profileRemoteBase, defaultRemoteBase,
                                out string relativePath, out string remoteBasePath);
                            string remotePath = CombineRemotePaths(remoteBasePath, relativePath);
                            var protocol = config.UseSSH ? "SFTP" : "FTP";
                            var detail = RemoteTransferErrorFormatter.Format(
                                fileEx,
                                fileName: file.Name,
                                remotePath: remotePath,
                                protocol: protocol,
                                profileName: config.FtpHost);
                            
                            Dispatcher.Invoke(() =>
                            {
                                file.UploadState = UploadState.Failed;
                                UpdateUploadDetailText(file.Name, 0, 0, 0, "Failed");
                                AddUploadLog($"  ✗ Upload FAILED!");
                                AddUploadLog($"  {detail.Replace(Environment.NewLine, Environment.NewLine + "  ")}");
                                AddUploadLog("");
                            });
                            
                            ModernMessageBox.Show(detail, "Upload Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            break; // Stop upload on error
                        }
                    }

                    if (token.IsCancellationRequested)
                    {
                        StatusText.Text = "Upload Stopped by User 🛑";
                        if (!skipConfirm)
                        {
                            ModernMessageBox.Show("Upload process was stopped.", "Stopped", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                    else
                    {
                        StatusText.Text = "Upload Completed! ✅";
                        // Upload finished successfully, delete session
                        if (!skipConfirm && File.Exists(sessionPath))
                        {
                            File.Delete(sessionPath);
                        }

                        CheckSessionStatus();
                        if (!skipConfirm)
                        {
                            ModernMessageBox.Show(
                                $"Upload Complete!\nUploaded: {processed - skipped - foldersCreated}\nFolders: {foldersCreated}\nSkipped: {skipped}",
                                "Success",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Upload Stopped.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Upload Failed.";
                var protocol = config.UseSSH ? "SFTP" : "FTP";
                var detailedMessage = RemoteTransferErrorFormatter.Format(
                    ex,
                    protocol: protocol,
                    profileName: config.FtpHost);
                AddUploadLog(detailedMessage);
                ModernMessageBox.Show(detailedMessage, "Upload Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isUploading = false;
                StartUploadButton.IsEnabled = true;
                StopButton.IsEnabled = false;
                if (CompactStopButton != null)
                {
                    CompactStopButton.IsEnabled = false;
                }
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                CheckSessionStatus();
                if (UploadDetailText != null)
                {
                    UploadDetailText.Text = string.Empty;
                }

                ApplyUploadPanelVisibility();
            }
        }

        private void CollectSelectedUploadItems(
            IEnumerable<FileSystemItem> items,
            List<FileSystemItem> files,
            List<FileSystemItem> emptyFolders)
        {
            foreach (var item in items)
            {
                if (item.IsFolder)
                {
                    if (item.Children != null && item.Children.Any())
                    {
                        CollectSelectedUploadItems(item.Children, files, emptyFolders);
                    }
                    else if (item.IsChecked == true)
                    {
                        // Empty checked folders must still be created on the remote.
                        emptyFolders.Add(item);
                    }
                }
                else if (item.IsChecked == true)
                {
                    files.Add(item);
                }
            }
        }

        private void CollectSelectedFiles(IEnumerable<FileSystemItem> items, List<FileSystemItem> collector)
        {
            var folders = new List<FileSystemItem>();
            CollectSelectedUploadItems(items, collector, folders);
        }

        private void ResetUploadIndicators(IEnumerable<FileSystemItem> files)
        {
            foreach (var item in files)
            {
                Dispatcher.Invoke(item.ResetUploadState);
            }
        }

        private long GetFileSizeSafe(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    return new FileInfo(path).Length;
                }
            }
            catch { }
            return 0;
        }

        private void UpdateUploadDetailText(string fileName, double percent, long transferred, long total, string? note = null)
        {
            if (UploadDetailText == null) return;
            var percentText = percent >= 0 ? $"{percent:0.##}%" : string.Empty;
            var transferredText = FormatSizeReadable(transferred);
            var totalText = total > 0 ? FormatSizeReadable(total) : "Unknown";
            var message = $"{fileName}: {transferredText} / {totalText}";
            if (!string.IsNullOrEmpty(percentText)) message += $" ({percentText})";
            if (!string.IsNullOrEmpty(note)) message += $" · {note}";
            UploadDetailText.Text = message;
        }

        private string FormatSizeReadable(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int index = 0;
            while (value >= 1024 && index < units.Length - 1)
            {
                value /= 1024;
                index++;
            }
            return $"{value:0.##} {units[index]}";
        }

        private void ResolveUploadPaths(
            string localFullPath,
            string profileRemoteBase,
            string mappedRemoteBase,
            out string relativePath,
            out string remoteBasePath)
        {
            var projectRoot = !string.IsNullOrEmpty(_projectPath) ? _projectPath : _scanRootPath;

            // Mapping local is a subfolder of the project.
            if (!string.IsNullOrEmpty(_mappedLocalRoot) && IsSameOrNestedPath(_mappedLocalRoot, localFullPath))
            {
                relativePath = Path.GetRelativePath(_mappedLocalRoot, localFullPath).Replace("\\", "/");
                remoteBasePath = mappedRemoteBase;
                return;
            }

            relativePath = Path.GetRelativePath(projectRoot, localFullPath).Replace("\\", "/");

            // Outside mapped local folder → profile remote only.
            // When mapping has no distinct local folder (maps whole project), use mapped remote.
            if (!string.IsNullOrEmpty(_mappedLocalRoot))
            {
                remoteBasePath = profileRemoteBase;
            }
            else
            {
                remoteBasePath = mappedRemoteBase;
            }
        }

        private PathMapping? GetPrimaryMapping(ConnectionProfile? profile)
        {
            if (profile?.PathMappings == null) return null;
            return profile.PathMappings.FirstOrDefault(pm =>
                pm != null &&
                (!string.IsNullOrWhiteSpace(pm.LocalPath) || !string.IsNullOrWhiteSpace(pm.RemotePath)));
        }

        private (string localRoot, string remoteRoot) ResolveRoots(ProjectConfig config, PathMapping? mapping)
        {
            // Get profile to use its RemotePath (not legacy config.RemotePath)
            var profile = ResolveConnectionProfile(config.ConnectionProfileId);
            string remoteRoot = NormalizeRemoteBase(profile?.RemotePath ?? config.RemotePath);
            string localRoot = _projectPath;

            if (mapping != null)
            {
                var localSegment = (mapping.LocalPath ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(localSegment) && !string.IsNullOrEmpty(_projectPath))
                {
                    var normalizedSegment = localSegment.Replace("/", System.IO.Path.DirectorySeparatorChar.ToString());
                    var combined = System.IO.Path.GetFullPath(System.IO.Path.Combine(_projectPath, normalizedSegment));
                    if (Directory.Exists(combined))
                    {
                        localRoot = combined;
                    }
                }

                remoteRoot = CombineRemotePaths(remoteRoot, mapping.RemotePath);
            }

            if (string.IsNullOrEmpty(localRoot) && !string.IsNullOrEmpty(_projectPath))
            {
                localRoot = _projectPath;
            }

            return (localRoot, remoteRoot);
        }

        private string NormalizeRemoteBase(string? path)
        {
            var trimmed = (path ?? "/").Trim();
            trimmed = trimmed.Replace("\\", "/");
            
            // Keep host name if present (e.g., "gitdeploy.nitron.pro/public" -> "/gitdeploy.nitron.pro/public/")
            // Don't remove hostname - it's part of the path structure
            
            if (!trimmed.StartsWith("/"))
            {
                trimmed = "/" + trimmed;
            }
            
            // Only add trailing slash if it's a directory path (not a file)
            // For base paths, we want trailing slash
            trimmed = trimmed.TrimEnd('/');
            if (trimmed.Length == 0)
            {
                trimmed = "/";
            }
            // Add trailing slash for base directory paths
            if (!trimmed.EndsWith("/"))
            {
                trimmed += "/";
            }
            return trimmed;
        }

        private string CombineRemotePaths(string baseRemote, string? mappingRemote)
        {
            var normalizedBase = NormalizeRemoteBase(baseRemote);
            if (string.IsNullOrWhiteSpace(mappingRemote) || mappingRemote.Trim() == "/")
            {
                return normalizedBase;
            }

            var trimmed = mappingRemote.Trim();
            if (trimmed.Equals("~", StringComparison.Ordinal))
            {
                return normalizedBase;
            }

            // Always append mapping path to base remote (no absolute override)
            var segment = trimmed.Trim('/');
            if (string.IsNullOrEmpty(segment))
            {
                return normalizedBase;
            }

            // Combine paths
            var combined = normalizedBase.TrimEnd('/') + "/" + segment;
            
            // Normalize but preserve the structure
            // Check if segment looks like a file (has extension and no trailing slash in original)
            bool isFile = !trimmed.EndsWith("/") && trimmed.Contains(".") && 
                         !string.IsNullOrEmpty(Path.GetExtension(trimmed));
            
            // Normalize the combined path
            combined = combined.Replace("\\", "/");
            if (!combined.StartsWith("/"))
            {
                combined = "/" + combined;
            }
            
            if (isFile)
            {
                // For files, don't add trailing slash
                return combined;
            }
            
            // For directories, add trailing slash if mapping had trailing slash
            if (trimmed.EndsWith("/"))
            {
                if (!combined.EndsWith("/"))
                {
                    combined += "/";
                }
            }
            else
            {
                // If mapping didn't have trailing slash, add it for directory paths
                if (!combined.EndsWith("/"))
                {
                    combined += "/";
                }
            }
            
            return combined;
        }

        private bool TryRefreshProjectPath()
        {
            try
            {
                var globalConfig = _configService.LoadGlobalConfig();
                var candidate = globalConfig.LastProjectPath;
                if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate))
                {
                    _projectPath = string.Empty;
                    _scanRootPath = string.Empty;
                    _activeRemoteBasePath = "/";
                    return false;
                }

                if (!string.Equals(_projectPath, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    _projectPath = candidate;
                }

                if (string.IsNullOrEmpty(_scanRootPath))
                {
                    _scanRootPath = _projectPath;
                }

                return true;
            }
            catch
            {
                _projectPath = string.Empty;
                _scanRootPath = string.Empty;
                _activeRemoteBasePath = "/";
                return false;
            }
        }

        private ProjectConfig LoadCurrentProjectConfig(out bool hasProject)
        {
            hasProject = TryRefreshProjectPath();
            if (!hasProject)
            {
                return new ProjectConfig();
            }

            return _configService.LoadProjectConfig(_projectPath);
        }

        private ConnectionProfile? ResolveConnectionProfile(string profileId)
        {
            if (string.IsNullOrWhiteSpace(profileId)) return null;
            try
            {
                var connections = _configService.LoadConnections();
                return connections.FirstOrDefault(c => string.Equals(c.Id, profileId, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return null;
            }
        }

        private void AddUploadLog(string message)
        {
            if (UploadLogTextBox == null) return;
            
            Dispatcher.Invoke(() =>
            {
                UploadLogTextBox.AppendText(message + Environment.NewLine);
                UploadLogTextBox.ScrollToEnd();
            });
        }

        private void UpdateConnectionInfoBanner(
            ProjectConfig? config = null,
            bool skipProjectRefresh = false,
            ConnectionProfile? profileOverride = null,
            PathMapping? mappingOverride = null)
        {
            if (ConnectionInfoText == null)
            {
                return;
            }

            if (!skipProjectRefresh && !TryRefreshProjectPath())
            {
                ConnectionInfoText.Text = "No project selected. Choose a project in Settings.";
                ConnectionInfoText.Foreground = GetThemeBrush("Status.Warning", System.Windows.Media.Brushes.Orange);
                return;
            }

            if (string.IsNullOrWhiteSpace(_projectPath) || !Directory.Exists(_projectPath))
            {
                ConnectionInfoText.Text = "No project selected. Choose a project in Settings.";
                ConnectionInfoText.Foreground = GetThemeBrush("Status.Warning", System.Windows.Media.Brushes.Orange);
                return;
            }

            var effectiveConfig = config ?? _configService.LoadProjectConfig(_projectPath);
            var accentBrush = GetThemeBrush("Status.Info", System.Windows.Media.Brushes.SkyBlue);

            ConnectionProfile? profile = profileOverride ?? ResolveConnectionProfile(effectiveConfig.ConnectionProfileId);
            if (profile != null)
            {
                var protocol = profile.UseSSH ? "SFTP (SSH)" : "FTP";
                var hostText = string.IsNullOrWhiteSpace(profile.Host) ? "Host missing" : $"{profile.Host}:{profile.Port}";
                var baseRemotePath = string.IsNullOrWhiteSpace(profile.RemotePath) ? "/" : profile.RemotePath;
                var mapping = mappingOverride ?? GetPrimaryMapping(profile);
                if (mapping != null)
                {
                    var localLabel = string.IsNullOrWhiteSpace(mapping.LocalPath) ? "(project root)" : mapping.LocalPath;
                    // Combine profile.RemotePath + mapping.RemotePath (same logic as ResolveRoots)
                    var combinedRemotePath = CombineRemotePaths(baseRemotePath, mapping.RemotePath);
                    ConnectionInfoText.Text = $"Active Connection: {profile.Name} · {protocol} · {hostText} · Local '{localLabel}' → Remote '{combinedRemotePath}'";
                }
                else
                {
                    ConnectionInfoText.Text = $"Active Connection: {profile.Name} · {protocol} · {hostText} → {baseRemotePath}";
                }
                ConnectionInfoText.Foreground = accentBrush;
                return;
            }

            if (!string.IsNullOrWhiteSpace(effectiveConfig.FtpHost))
            {
                var protocol = effectiveConfig.UseSSH ? "SFTP (SSH)" : "FTP";
                var port = effectiveConfig.FtpPort <= 0 ? 21 : effectiveConfig.FtpPort;
                var user = string.IsNullOrWhiteSpace(effectiveConfig.FtpUsername) ? "Unknown user" : effectiveConfig.FtpUsername;
                ConnectionInfoText.Text = $"Active Connection: {effectiveConfig.FtpHost}:{port} as {user} ({protocol})";
                ConnectionInfoText.Foreground = accentBrush;
            }
            else
            {
                ConnectionInfoText.Text = "No connection selected. Open Settings → Connection Manager to assign one.";
                ConnectionInfoText.Foreground = GetThemeBrush("Status.Error", System.Windows.Media.Brushes.Salmon);
            }
        }
    }

    public enum UploadState
    {
        Pending,
        InProgress,
        Uploaded,
        Skipped,
        Failed
    }

    public class FileSystemItem : INotifyPropertyChanged
    {
        private bool? _isChecked = false;
        private bool _isExpanded;
        private UploadState _uploadState = UploadState.Pending;
        private GitItemState _gitState = GitItemState.None;
        private bool _isMappedFolder;

        private static System.Windows.Media.Brush GetThemeBrush(string resourceKey, System.Windows.Media.Brush fallback)
        {
            if (string.IsNullOrWhiteSpace(resourceKey))
            {
                return fallback;
            }

            return System.Windows.Application.Current?.TryFindResource(resourceKey) as System.Windows.Media.Brush ?? fallback;
        }

        private static System.Windows.Media.Brush GetTokenOrTheme(string tokenPath, string resourceKey, System.Windows.Media.Brush fallback)
        {
            try
            {
                if (ThemeService.Instance.CurrentTokens.Colors.TryGetValue(tokenPath, out var color))
                {
                    return new SolidColorBrush(color);
                }
            }
            catch
            {
                // ThemeService may not be ready in design-time.
            }

            return GetThemeBrush(resourceKey, fallback);
        }

        public void ApplyThemeColors()
        {
            IconColor = IsFolder
                ? GetTokenOrTheme("directUpload.folderIcon", "Status.Warning", System.Windows.Media.Brushes.Gold)
                : GetTokenOrTheme("directUpload.fileIcon", "Text.Secondary", System.Windows.Media.Brushes.WhiteSmoke);
            OnPropertyChanged(nameof(IconColor));
            OnPropertyChanged(nameof(NameBrush));
            OnPropertyChanged(nameof(GitBadgeBrush));
            OnPropertyChanged(nameof(RowBackground));
        }

        public string Name { get; set; } = "";
        public string FullPath { get; set; } = "";
        public bool IsFolder { get; set; }
        public string Icon { get; set; } = "";
        // Default must stay thread-safe: ScanDirectory constructs items on a worker thread.
        public System.Windows.Media.Brush IconColor { get; set; } = System.Windows.Media.Brushes.White;
        public long Size { get; set; }
        
        public string SizeDisplay => IsFolder ? "" : FormatSize(Size);
        public Visibility SizeVisibility => IsFolder ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>Update disk-backed fields without resetting expand/check UI state.</summary>
        public void ApplyDiskSnapshot(FileSystemItem snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            if (!string.Equals(Name, snapshot.Name, StringComparison.Ordinal))
            {
                Name = snapshot.Name;
                OnPropertyChanged(nameof(Name));
            }

            if (!string.Equals(FullPath, snapshot.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                FullPath = snapshot.FullPath;
                OnPropertyChanged(nameof(FullPath));
            }

            if (Size != snapshot.Size)
            {
                Size = snapshot.Size;
                OnPropertyChanged(nameof(Size));
                OnPropertyChanged(nameof(SizeDisplay));
            }

            if (!string.Equals(Icon, snapshot.Icon, StringComparison.Ordinal))
            {
                Icon = snapshot.Icon;
                OnPropertyChanged(nameof(Icon));
            }

            if (!Equals(IconColor, snapshot.IconColor))
            {
                IconColor = snapshot.IconColor;
                OnPropertyChanged(nameof(IconColor));
            }

            // Soft-ignore from scan; live git overlay will refine other states afterward.
            if (snapshot.GitState == GitItemState.Ignored || GitState == GitItemState.Ignored)
            {
                GitState = snapshot.GitState;
            }
        }

        public ObservableCollection<FileSystemItem> Children { get; set; } = new ObservableCollection<FileSystemItem>();
        public FileSystemItem? Parent { get; set; }

        public GitItemState GitState
        {
            get => _gitState;
            set
            {
                if (_gitState != value)
                {
                    _gitState = value;
                    OnPropertyChanged(nameof(GitState));
                    OnPropertyChanged(nameof(NameBrush));
                    OnPropertyChanged(nameof(GitBadgeText));
                    OnPropertyChanged(nameof(GitBadgeVisibility));
                    OnPropertyChanged(nameof(GitBadgeBrush));
                }
            }
        }

        public bool IsMappedFolder
        {
            get => _isMappedFolder;
            set
            {
                if (_isMappedFolder != value)
                {
                    _isMappedFolder = value;
                    OnPropertyChanged(nameof(IsMappedFolder));
                    OnPropertyChanged(nameof(RowBackground));
                    OnPropertyChanged(nameof(MappedBadgeText));
                    OnPropertyChanged(nameof(MappedBadgeVisibility));
                }
            }
        }

        public System.Windows.Media.Brush NameBrush => GitState switch
        {
            GitItemState.Clean => GetTokenOrTheme("directUpload.gitClean", "Status.Success", System.Windows.Media.Brushes.LightGreen),
            GitItemState.Modified => GetTokenOrTheme("directUpload.gitModified", "Status.Error", System.Windows.Media.Brushes.OrangeRed),
            GitItemState.Untracked => GetTokenOrTheme("directUpload.gitUntracked", "Status.Info", System.Windows.Media.Brushes.DeepSkyBlue),
            GitItemState.Ignored => GetTokenOrTheme("directUpload.gitIgnored", "Text.Muted", System.Windows.Media.Brushes.Gray),
            GitItemState.Conflicted => GetTokenOrTheme("directUpload.gitConflicted", "Status.Warning", System.Windows.Media.Brushes.Orange),
            _ => GetThemeBrush("Text.Secondary", System.Windows.Media.Brushes.WhiteSmoke)
        };

        public string GitBadgeText => GitState switch
        {
            GitItemState.Ignored => "ignored",
            GitItemState.Conflicted => "!",
            GitItemState.Modified => "M",
            GitItemState.Untracked => "?",
            _ => string.Empty
        };

        public Visibility GitBadgeVisibility =>
            string.IsNullOrEmpty(GitBadgeText) ? Visibility.Collapsed : Visibility.Visible;

        public System.Windows.Media.Brush GitBadgeBrush => GitState switch
        {
            GitItemState.Ignored => GetTokenOrTheme("directUpload.gitIgnored", "Text.Muted", System.Windows.Media.Brushes.Gray),
            GitItemState.Conflicted => GetTokenOrTheme("directUpload.gitConflicted", "Status.Warning", System.Windows.Media.Brushes.Orange),
            GitItemState.Modified => GetTokenOrTheme("directUpload.gitModified", "Status.Error", System.Windows.Media.Brushes.OrangeRed),
            GitItemState.Untracked => GetTokenOrTheme("directUpload.gitUntracked", "Status.Info", System.Windows.Media.Brushes.DeepSkyBlue),
            _ => GetThemeBrush("Text.Muted", System.Windows.Media.Brushes.Gray)
        };

        public string MappedBadgeText => IsMappedFolder ? "mapped" : string.Empty;

        public Visibility MappedBadgeVisibility =>
            IsMappedFolder ? Visibility.Visible : Visibility.Collapsed;

        public System.Windows.Media.Brush RowBackground =>
            IsMappedFolder
                ? GetTokenOrTheme(
                    "directUpload.mappedRowBackground",
                    "Status.SuccessSurface",
                    System.Windows.Media.Brushes.DarkSeaGreen)
                : System.Windows.Media.Brushes.Transparent;

        public bool? IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked != value)
                {
                    _isChecked = value;
                    OnPropertyChanged(nameof(IsChecked));

                    // Update children
                    if (_isChecked.HasValue && Children != null)
                    {
                        foreach (var child in Children)
                        {
                            child.SetIsCheckedFromParent(_isChecked.Value);
                        }
                    }

                    // Update parent
                    Parent?.CheckParentStatus();
                }
            }
        }

        public void SetIsCheckedFromParent(bool value)
        {
            if (_isChecked != value)
            {
                _isChecked = value;
                OnPropertyChanged(nameof(IsChecked));
                if (Children != null)
                {
                    foreach (var child in Children)
                    {
                        child.SetIsCheckedFromParent(value);
                    }
                }
            }
        }

        public void CheckParentStatus()
        {
            if (Children == null || !Children.Any()) return;

            bool allChecked = Children.All(x => x.IsChecked == true);
            bool allUnchecked = Children.All(x => x.IsChecked == false);

            if (allChecked)
            {
                _isChecked = true;
            }
            else if (allUnchecked)
            {
                _isChecked = false;
            }
            else
            {
                _isChecked = null; // Indeterminate
            }
            
            OnPropertyChanged(nameof(IsChecked));
            Parent?.CheckParentStatus();
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

        public UploadState UploadState
        {
            get => _uploadState;
            set
            {
                if (_uploadState != value)
                {
                    _uploadState = value;
                    OnPropertyChanged(nameof(UploadState));
                    OnPropertyChanged(nameof(UploadBadgeText));
                    OnPropertyChanged(nameof(UploadBadgeBrush));
                    OnPropertyChanged(nameof(UploadBadgeVisibility));
                    Parent?.RefreshUploadStateFromChildren();
                }
            }
        }

        public string UploadBadgeText => UploadState switch
        {
            UploadState.Pending => string.Empty,
            UploadState.InProgress => "…",
            UploadState.Uploaded => "✓",
            UploadState.Skipped => "↺",
            _ => string.Empty
        };

        public System.Windows.Media.Brush UploadBadgeBrush => UploadState switch
        {
            UploadState.InProgress => GetThemeBrush("Status.Info", System.Windows.Media.Brushes.DeepSkyBlue),
            UploadState.Uploaded => GetThemeBrush("Status.Success", System.Windows.Media.Brushes.LightGreen),
            UploadState.Skipped => GetThemeBrush("Status.Warning", System.Windows.Media.Brushes.Orange),
            _ => System.Windows.Media.Brushes.Transparent
        };

        public Visibility UploadBadgeVisibility => UploadState == UploadState.Pending ? Visibility.Collapsed : Visibility.Visible;

        public void ResetUploadState()
        {
            UploadState = UploadState.Pending;
            foreach (var child in Children)
            {
                child.ResetUploadState();
            }
        }

        public void RefreshUploadStateFromChildren()
        {
            if (Children == null || Children.Count == 0) return;
            bool allUploaded = Children.All(c => c.UploadState == UploadState.Uploaded);
            bool allPending = Children.All(c => c.UploadState == UploadState.Pending);
            bool anyInProgress = Children.Any(c => c.UploadState == UploadState.InProgress);

            if (allUploaded)
            {
                UploadState = UploadState.Uploaded;
            }
            else if (allPending)
            {
                UploadState = UploadState.Pending;
            }
            else if (anyInProgress)
            {
                UploadState = UploadState.InProgress;
            }
            else
            {
                UploadState = UploadState.Skipped;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private string FormatSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
    }
}