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
using GitDeployPro.Behaviors;
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
        private readonly List<string> _mappedLocalRoots = new();
        private string _profileRemoteBasePath = "/";
        private string _activeRemoteBasePath = "/";
        private ObservableCollection<FileSystemItem> _items;
        private bool _isUploading = false;
        private readonly TransferMonitorController _transferMonitor = new();
        private string _treeSearchQuery = string.Empty;
        private bool _suppressTreeSearchText;
        private readonly ObservableCollection<LocalTreeSearchHit> _searchHits = new();
        private const int LocalSearchHitLimit = 300;
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
            _transferMonitor.Attach(UploadTransferMonitor);
            _configService = new ConfigurationService();
            _items = new ObservableCollection<FileSystemItem>();
            FileTreeView.ItemsSource = _items;
            if (LocalTreeSearchResults != null)
            {
                LocalTreeSearchResults.ItemsSource = _searchHits;
            }

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

        public Task<bool> TryCloseLocalEditorAsync(bool force = false) =>
            LocalEditor?.TryCloseAsync(force) ?? Task.FromResult(true);

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
                var basedOn = FileTreeView.TryFindResource("App.TreeViewItem.FullRow") as Style;
                var itemStyle = basedOn != null
                    ? new Style(typeof(TreeViewItem), basedOn)
                    : new Style(typeof(TreeViewItem));
                itemStyle.Setters.Add(new Setter(
                    TreeViewItem.IsExpandedProperty,
                    new System.Windows.Data.Binding("IsExpanded") { Mode = BindingMode.TwoWay }));
                itemStyle.Setters.Add(new Setter(
                    System.Windows.Controls.Control.ForegroundProperty,
                    GetThemeBrush("Text.Secondary", System.Windows.Media.Brushes.Gray)));
                itemStyle.Setters.Add(new Setter(
                    FrameworkElement.HorizontalAlignmentProperty,
                    System.Windows.HorizontalAlignment.Stretch));
                itemStyle.Setters.Add(new Setter(
                    System.Windows.Controls.Control.HorizontalContentAlignmentProperty,
                    System.Windows.HorizontalAlignment.Stretch));
                itemStyle.Setters.Add(new Setter(
                    System.Windows.Controls.Control.FontSizeProperty,
                    compact ? 12.0 : 13.0));
                itemStyle.Setters.Add(new Setter(
                    System.Windows.Controls.Control.PaddingProperty,
                    compact ? new Thickness(2, 1, 2, 1) : new Thickness(3, 1, 3, 1)));

                var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
                hoverTrigger.Setters.Add(new Setter(
                    System.Windows.Controls.Control.BackgroundProperty,
                    hoverBrush));
                itemStyle.Triggers.Add(hoverTrigger);

                var selectedTrigger = new Trigger { Property = TreeViewItem.IsSelectedProperty, Value = true };
                selectedTrigger.Setters.Add(new Setter(
                    System.Windows.Controls.Control.BackgroundProperty,
                    hoverBrush));
                itemStyle.Triggers.Add(selectedTrigger);

                var multiTrigger = new DataTrigger
                {
                    Binding = new System.Windows.Data.Binding(nameof(FileSystemItem.IsMultiSelected)),
                    Value = true
                };
                multiTrigger.Setters.Add(new Setter(
                    System.Windows.Controls.Control.BackgroundProperty,
                    selectedBrush));
                itemStyle.Triggers.Add(multiTrigger);

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
                RefreshMappedLocalRoots(profile);
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
                var hadItems = _items.Count > 0;
                var rootItems = await Task.Run(() => ScanDirectory(projectRoot));

                // In-place merge keeps expanded folders / checkboxes / TreeView containers stable.
                MergeTreeItems(_items, rootItems, parent: null);
                // Theme brushes must be applied on the UI thread (live palette brushes are not BG-safe).
                RefreshTreeThemeBrushes(_items);
                ApplyLocalTreeSearch();

                // Mark ALL mapped folders (api, core, …); expand only primary on first populate.
                ApplyMappedFolderBadges(expandPrimary: !hadItems);

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

        private (HashSet<string> anyDepthNames, HashSet<string> rootOnlyNames, List<string> anyDepthPatterns, List<string> rootOnlyPatterns) LoadSoftIgnoreRules()
        {
            var anyDepthNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rootOnlyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var anyDepthPatterns = new List<string>();
            var rootOnlyPatterns = new List<string>();

            try
            {
                string gitIgnorePath = Path.Combine(_projectPath, ".gitignore");
                if (!File.Exists(gitIgnorePath))
                {
                    return (anyDepthNames, rootOnlyNames, anyDepthPatterns, rootOnlyPatterns);
                }

                foreach (var line in File.ReadAllLines(gitIgnorePath))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith("!"))
                    {
                        continue;
                    }

                    bool rooted = trimmed.StartsWith('/') || trimmed.StartsWith('\\');
                    if (trimmed.Contains('*') || trimmed.Contains('?'))
                    {
                        var clean = trimmed.TrimStart('/', '\\');
                        if (!string.IsNullOrWhiteSpace(clean))
                        {
                            (rooted ? rootOnlyPatterns : anyDepthPatterns).Add(clean);
                        }
                    }
                    else
                    {
                        var clean = trimmed.TrimStart('/', '\\').TrimEnd('/', '\\');
                        if (!string.IsNullOrWhiteSpace(clean) && !HardExcludeNames.Contains(clean))
                        {
                            (rooted ? rootOnlyNames : anyDepthNames).Add(clean);
                        }
                    }
                }
            }
            catch
            {
                // ignore unreadable .gitignore
            }

            return (anyDepthNames, rootOnlyNames, anyDepthPatterns, rootOnlyPatterns);
        }

        private bool IsSoftIgnoredName(
            string name,
            bool atProjectRoot,
            HashSet<string> anyDepthNames,
            HashSet<string> rootOnlyNames,
            List<string> anyDepthPatterns,
            List<string> rootOnlyPatterns)
        {
            if (anyDepthNames.Contains(name))
            {
                return true;
            }

            if (atProjectRoot && rootOnlyNames.Contains(name))
            {
                return true;
            }

            foreach (var pattern in anyDepthPatterns)
            {
                if (MatchesPattern(name, pattern))
                {
                    return true;
                }
            }

            if (!atProjectRoot)
            {
                return false;
            }

            foreach (var pattern in rootOnlyPatterns)
            {
                if (MatchesPattern(name, pattern))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsProjectRootDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(_projectPath) || string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                var root = Path.GetFullPath(_projectPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var current = Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(root, current, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private List<FileSystemItem> ScanDirectory(string path)
        {
            var (anyDepthNames, rootOnlyNames, anyDepthPatterns, rootOnlyPatterns) = LoadSoftIgnoreRules();
            return ScanDirectory(path, anyDepthNames, rootOnlyNames, anyDepthPatterns, rootOnlyPatterns);
        }

        private List<FileSystemItem> ScanDirectory(
            string path,
            HashSet<string> anyDepthNames,
            HashSet<string> rootOnlyNames,
            List<string> anyDepthPatterns,
            List<string> rootOnlyPatterns)
        {
            var items = new List<FileSystemItem>();

            try
            {
                var dirInfo = new DirectoryInfo(path);
                var atProjectRoot = IsProjectRootDirectory(path);

                foreach (var dir in dirInfo.GetDirectories())
                {
                    if (HardExcludeNames.Contains(dir.Name) || dir.Attributes.HasFlag(FileAttributes.Hidden))
                    {
                        continue;
                    }

                    var softIgnored = IsSoftIgnoredName(
                        dir.Name,
                        atProjectRoot,
                        anyDepthNames,
                        rootOnlyNames,
                        anyDepthPatterns,
                        rootOnlyPatterns);
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
                        ScanDirectory(dir.FullName, anyDepthNames, rootOnlyNames, anyDepthPatterns, rootOnlyPatterns));

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

                    var softIgnored = IsSoftIgnoredName(
                        file.Name,
                        atProjectRoot,
                        anyDepthNames,
                        rootOnlyNames,
                        anyDepthPatterns,
                        rootOnlyPatterns);
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

        private void RefreshMappedLocalRoots(ConnectionProfile? profile)
        {
            _mappedLocalRoots.Clear();
            if (string.IsNullOrEmpty(_projectPath))
            {
                return;
            }

            foreach (var mapping in RemotePathResolver.GetActiveMappings(profile))
            {
                if (RemotePathResolver.IsProjectRootLocalPath(mapping.LocalPath))
                {
                    continue;
                }

                var segment = RemotePathResolver.NormalizeLocalMappingPath(mapping.LocalPath)
                    .Replace("/", Path.DirectorySeparatorChar.ToString());
                try
                {
                    var full = Path.GetFullPath(Path.Combine(_projectPath, segment))
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (Directory.Exists(full) &&
                        !_mappedLocalRoots.Contains(full, StringComparer.OrdinalIgnoreCase))
                    {
                        _mappedLocalRoots.Add(full);
                    }
                }
                catch
                {
                    // ignore invalid path segments
                }
            }

            // Keep primary root for Direct Upload default expand / legacy helpers.
            if (_mappedLocalRoots.Count > 0 && string.IsNullOrEmpty(_mappedLocalRoot))
            {
                _mappedLocalRoot = _mappedLocalRoots[0];
            }
        }

        private void ApplyMappedFolderBadges(bool expandPrimary)
        {
            ClearMappedFolderFlags(_items);
            if (_mappedLocalRoots.Count == 0)
            {
                return;
            }

            for (var i = 0; i < _mappedLocalRoots.Count; i++)
            {
                var expand = expandPrimary && (
                    !string.IsNullOrEmpty(_mappedLocalRoot)
                        ? string.Equals(_mappedLocalRoots[i], _mappedLocalRoot, StringComparison.OrdinalIgnoreCase)
                        : i == 0);

                MarkMappedFolderCore(_items, _mappedLocalRoots[i], expand);
            }
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
            if (HandleLocalTreeSearchKey(e))
            {
                return;
            }

            if (e.Key == Key.Delete)
            {
                e.Handled = true;
                _ = DeleteSelectedLocalItemsAsync();
                return;
            }

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

        private void FileTreeView_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (e.OriginalSource is System.Windows.Controls.TextBox || TreeNameSearch.ShouldIgnoreTypedSearch(e))
            {
                return;
            }

            e.Handled = true;
            SetLocalTreeSearchQuery(_treeSearchQuery + e.Text);
        }

        private bool HandleLocalTreeSearchKey(System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Escape && !string.IsNullOrEmpty(_treeSearchQuery))
            {
                e.Handled = true;
                SetLocalTreeSearchQuery(string.Empty);
                return true;
            }

            if (e.OriginalSource is System.Windows.Controls.TextBox)
            {
                return false;
            }

            if (e.Key == Key.Back && !string.IsNullOrEmpty(_treeSearchQuery))
            {
                e.Handled = true;
                SetLocalTreeSearchQuery(_treeSearchQuery[..^1]);
                return true;
            }

            return false;
        }

        private void LocalTreeSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressTreeSearchText)
            {
                return;
            }

            SetLocalTreeSearchQuery(LocalTreeSearchBox.Text ?? string.Empty, syncBox: false);
        }

        private void LocalTreeSearchBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                SetLocalTreeSearchQuery(string.Empty);
                FileTreeView.Focus();
                return;
            }

            if (e.Key == Key.Enter && _searchHits.Count > 0)
            {
                e.Handled = true;
                var hit = LocalTreeSearchResults?.SelectedItem as LocalTreeSearchHit ?? _searchHits[0];
                RevealLocalSearchHit(hit);
                return;
            }

            if (e.Key == Key.Down && _searchHits.Count > 0 && LocalTreeSearchResults != null)
            {
                e.Handled = true;
                LocalTreeSearchResults.Focus();
                if (LocalTreeSearchResults.SelectedIndex < 0)
                {
                    LocalTreeSearchResults.SelectedIndex = 0;
                }
            }
        }

        private void LocalTreeSearchResults_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                SetLocalTreeSearchQuery(string.Empty);
                FileTreeView.Focus();
                return;
            }

            if (e.Key == Key.Enter && LocalTreeSearchResults.SelectedItem is LocalTreeSearchHit hit)
            {
                e.Handled = true;
                RevealLocalSearchHit(hit);
            }
        }

        private void LocalTreeSearchResults_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (FindParent<ListBoxItem>(e.OriginalSource as DependencyObject) == null)
            {
                return;
            }

            if (LocalTreeSearchResults.SelectedItem is LocalTreeSearchHit hit)
            {
                e.Handled = true;
                RevealLocalSearchHit(hit);
            }
        }

        private void LocalTreeSearchClear_Click(object sender, RoutedEventArgs e)
        {
            SetLocalTreeSearchQuery(string.Empty);
            FileTreeView.Focus();
        }

        private void SetLocalTreeSearchQuery(string query, bool syncBox = true)
        {
            _treeSearchQuery = query ?? string.Empty;
            if (syncBox && LocalTreeSearchBox != null)
            {
                _suppressTreeSearchText = true;
                LocalTreeSearchBox.Text = _treeSearchQuery;
                LocalTreeSearchBox.CaretIndex = LocalTreeSearchBox.Text.Length;
                _suppressTreeSearchText = false;
            }

            ApplyLocalTreeSearch();
        }

        private void ApplyLocalTreeSearch()
        {
            var query = _treeSearchQuery;
            var active = !string.IsNullOrEmpty(query);
            if (LocalTreeSearchBar != null)
            {
                LocalTreeSearchBar.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            }

            TreeNameSearch.SetSearchActive(FileTreeView, active);
            RebuildLocalSearchHits(query);

            if (FileTreeView != null)
            {
                FileTreeView.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
            }

            if (LocalTreeSearchResults != null)
            {
                LocalTreeSearchResults.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            }

            if (LocalTreeSearchEmpty != null)
            {
                LocalTreeSearchEmpty.Visibility = active && _searchHits.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (active && LocalTreeSearchBox != null && !LocalTreeSearchBox.IsKeyboardFocusWithin)
            {
                LocalTreeSearchBox.Focus();
                LocalTreeSearchBox.CaretIndex = LocalTreeSearchBox.Text.Length;
            }
        }

        private void RebuildLocalSearchHits(string query)
        {
            _searchHits.Clear();
            if (string.IsNullOrEmpty(query))
            {
                return;
            }

            var matches = TreeNameSearch.CollectMatches(
                _items,
                query,
                item => item.Name,
                item => item.Children);

            foreach (var item in matches.Take(LocalSearchHitLimit))
            {
                _searchHits.Add(LocalTreeSearchHit.FromItem(item, query, _projectPath));
            }
        }

        private void RevealLocalSearchHit(LocalTreeSearchHit hit)
        {
            if (hit?.Item == null)
            {
                return;
            }

            var target = hit.Item;
            SetLocalTreeSearchQuery(string.Empty);
            ExpandAncestorsOnly(target);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var container = FindLocalTreeViewItem(FileTreeView, target);
                if (container != null)
                {
                    TreeViewExtendedSelectionBehavior.ClearSelection(FileTreeView);
                    TreeViewExtendedSelectionBehavior.ApplyRightClickSelection(FileTreeView, target);
                    container.IsSelected = true;
                    container.BringIntoView();
                    container.Focus();
                }
            }), DispatcherPriority.Loaded);
        }

        private static void ExpandAncestorsOnly(FileSystemItem item)
        {
            for (var parent = item.Parent; parent != null; parent = parent.Parent)
            {
                parent.IsExpanded = true;
            }
        }

        private static TreeViewItem? FindLocalTreeViewItem(ItemsControl parent, FileSystemItem target)
        {
            if (parent == null || target == null)
            {
                return null;
            }

            parent.UpdateLayout();
            if (parent.ItemContainerGenerator.ContainerFromItem(target) is TreeViewItem direct)
            {
                return direct;
            }

            var ancestors = new Stack<FileSystemItem>();
            for (var node = target; node != null; node = node.Parent)
            {
                ancestors.Push(node);
            }

            ItemsControl current = parent;
            TreeViewItem? last = null;
            while (ancestors.Count > 0)
            {
                var step = ancestors.Pop();
                current.UpdateLayout();
                if (current.ItemContainerGenerator.ContainerFromItem(step) is not TreeViewItem child)
                {
                    return last;
                }

                last = child;
                if (!ReferenceEquals(step, target))
                {
                    child.IsExpanded = true;
                    child.UpdateLayout();
                }

                current = child;
            }

            return last;
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

            treeItem.Focus();

            var selected = TreeViewExtendedSelectionBehavior.GetSelectedItems<FileSystemItem>(FileTreeView);
            if (selected.Count == 0 || !selected.Contains(item))
            {
                selected = new List<FileSystemItem> { item };
            }

            var actions = BuildLocalContextActions(item, selected);
            if (GlobalContextMenuService.ShowMenu(treeItem, actions, item, PlacementMode.MousePoint))
            {
                e.Handled = true;
            }
        }

        private IReadOnlyList<AppContextMenuAction> BuildLocalContextActions(
            FileSystemItem clicked,
            IReadOnlyList<FileSystemItem> selected)
        {
            var actions = new List<AppContextMenuAction>();
            var canPaste = !_isUploading && System.Windows.Clipboard.ContainsFileDropList();
            var multi = selected.Count > 1;
            var target = clicked;

            if (!multi)
            {
                actions.Add(new AppContextMenuAction
                {
                    Id = "new-folder",
                    Label = "New Folder",
                    IconGlyph = "📁",
                    IsEnabled = !_isUploading,
                    Execute = _ => CreateLocalFolder(target)
                });
                actions.Add(new AppContextMenuAction
                {
                    Id = "new-file",
                    Label = "New File",
                    IconGlyph = "📄",
                    IsEnabled = !_isUploading,
                    Execute = _ => _ = CreateLocalFileAsync(target)
                });

                if (target.IsFolder)
                {
                    actions.Add(new AppContextMenuAction
                    {
                        Id = "open-in-explorer",
                        Label = "Open in Explorer",
                        IconGlyph = "📂",
                        Execute = _ => OpenFolderInExplorer(target.FullPath)
                    });
                    actions.Add(new AppContextMenuAction
                    {
                        Id = "refresh-folder",
                        Label = "Refresh",
                        IconGlyph = "🔄",
                        IsEnabled = !_isUploading,
                        Execute = _ => _ = RefreshFolderAsync(target)
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
                        Execute = _ => OpenLocalFileInEditor(target.FullPath)
                    });
                    actions.Add(new AppContextMenuAction
                    {
                        Id = "upload-file",
                        Label = "Upload file",
                        IconGlyph = "🚀",
                        IsEnabled = !_isUploading,
                        Execute = _ => _ = UploadSpecificFilesAsync(new[] { target }, skipConfirm: true)
                    });
                    actions.Add(new AppContextMenuAction
                    {
                        Id = "open-file-location",
                        Label = "Open in Explorer",
                        IconGlyph = "📂",
                        Execute = _ =>
                        {
                            var dir = Path.GetDirectoryName(target.FullPath);
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
                    actions.Add(BuildGitContextMenu(target));
                }

                actions.Add(AppContextMenuAction.Separator("properties-separator"));
                actions.Add(new AppContextMenuAction
                {
                    Id = "properties",
                    Label = "Properties",
                    IconGlyph = "ℹ",
                    Execute = _ => ShowLocalItemProperties(target)
                });
            }
        else
        {
            var files = selected.Where(entry => !entry.IsFolder).ToList();
                if (files.Count > 0)
                {
                    actions.Add(new AppContextMenuAction
                    {
                        Id = "upload-selected",
                        Label = $"Upload {files.Count} file{(files.Count == 1 ? string.Empty : "s")}",
                        IconGlyph = "🚀",
                        IsEnabled = !_isUploading,
                        Execute = _ => _ = UploadSpecificFilesAsync(files, skipConfirm: true)
                    });
                }
            }

            actions.Add(AppContextMenuAction.Separator("delete-separator"));
            actions.Add(new AppContextMenuAction
            {
                Id = "delete-local",
                Label = multi ? $"Delete ({selected.Count} items)" : "Delete",
                IconGlyph = "🗑",
                IsEnabled = !_isUploading,
                IsDestructive = true,
                Execute = _ => _ = DeleteLocalItemsAsync(selected)
            });

            return actions;
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

        private void ShowLocalItemProperties(FileSystemItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.FullPath))
            {
                return;
            }

            var window = new LocalItemPropertiesWindow(item.FullPath, item.IsFolder);
            WindowOwnerService.ShowDialogOwned(window, this);
        }

        private AppContextMenuAction BuildGitContextMenu(FileSystemItem item)
        {
            var relative = TryGetProjectRelativePath(item.FullPath);
            var hasRelative = !string.IsNullOrWhiteSpace(relative);
            var gitEnabled = !_isUploading && IsProjectGitRepository();
            var alreadyIgnored = hasRelative && GitIgnoreFileHelper.IsItemIgnored(
                GitIgnoreFileHelper.ReadLines(_projectPath),
                relative!,
                item.IsFolder);

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
                    Id = alreadyIgnored ? "git-ignore-remove" : "git-ignore",
                    Label = alreadyIgnored ? "Remove from .gitignore" : "Add to .gitignore",
                    IconGlyph = alreadyIgnored ? "✔" : "🚫",
                    IsEnabled = gitEnabled && hasRelative,
                    Execute = _ => _ = alreadyIgnored
                        ? GitRemoveFromIgnoreAsync(item)
                        : GitAddToIgnoreAsync(item)
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

            var ignoreEntry = GitIgnoreFileHelper.BuildEntry(relative, item.IsFolder);
            if (string.IsNullOrWhiteSpace(ignoreEntry))
            {
                ModernMessageBox.Show("Unable to build .gitignore pattern for this path.", "Git", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = ModernMessageBox.ShowWithResult(
                $"Add '{ignoreEntry}' to this project's .gitignore?",
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
                GitIgnoreFileHelper.TryAddEntry(_projectPath, ignoreEntry, out var added);

                GitService.SetWorkingDirectory(_projectPath);
                await _gitService.RemovePathFromIndexAsync(ignoreEntry.TrimStart('/').TrimEnd('/'));
                await RefreshFromDiskAsync(preserveSelection: true, quiet: true);
                await ApplyGitOverlayAsync();

                StatusText.Text = added
                    ? $"Added '{ignoreEntry}' to .gitignore."
                    : $"'{ignoreEntry}' already in .gitignore.";
                ModernMessageBox.Show(StatusText.Text, "Git", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Failed to update .gitignore:\n{ex.Message}", "Git", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task GitRemoveFromIgnoreAsync(FileSystemItem item)
        {
            if (_isUploading || !IsProjectGitRepository())
            {
                return;
            }

            var relative = TryGetProjectRelativePath(item.FullPath);
            if (string.IsNullOrWhiteSpace(relative))
            {
                ModernMessageBox.Show("Unable to match this path in .gitignore.", "Git", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var matching = GitIgnoreFileHelper.FindMatchingLines(
                GitIgnoreFileHelper.ReadLines(_projectPath),
                relative,
                item.IsFolder);
            if (matching.Count == 0)
            {
                ModernMessageBox.Show("This path is not listed in this project's .gitignore.", "Git ignore", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var preview = string.Join("\n", matching.Select(line => line.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
            var confirm = ModernMessageBox.ShowWithResult(
                $"Remove from this project's .gitignore?\n\n{preview}",
                "Git ignore",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                "Remove",
                "Cancel");
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                GitIgnoreFileHelper.TryRemoveMatching(_projectPath, relative, item.IsFolder, out var removed);
                await RefreshFromDiskAsync(preserveSelection: true, quiet: true);
                await ApplyGitOverlayAsync();

                StatusText.Text = removed.Count == 0
                    ? "No matching .gitignore line."
                    : $"Removed from .gitignore: {string.Join(", ", removed)}";
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

        private Task DeleteLocalItemAsync(FileSystemItem item) =>
            DeleteLocalItemsAsync(item == null ? Array.Empty<FileSystemItem>() : new[] { item });

        private async Task DeleteSelectedLocalItemsAsync()
        {
            var selected = TreeViewExtendedSelectionBehavior.GetSelectedItems<FileSystemItem>(FileTreeView);
            await DeleteLocalItemsAsync(selected);
        }

        private async Task DeleteLocalItemsAsync(IReadOnlyList<FileSystemItem> items)
        {
            if (_isUploading || items == null || items.Count == 0)
            {
                return;
            }

            var targets = TreeMultiSelectHelpers.CollapseNestedByPath(
                items.Where(item => item != null && !string.IsNullOrWhiteSpace(item.FullPath)),
                item => item.FullPath,
                item => item.IsFolder,
                Path.DirectorySeparatorChar);

            if (targets.Count == 0)
            {
                return;
            }

            string message;
            if (targets.Count == 1)
            {
                var item = targets[0];
                var kind = item.IsFolder ? "folder" : "file";
                message = $"Delete this {kind}?\n\n{item.Name}\n{item.FullPath}\n\nThis cannot be undone.";
            }
            else
            {
                var preview = string.Join("\n", targets.Take(8).Select(item => "• " + item.Name));
                var extra = targets.Count > 8 ? $"\n… and {targets.Count - 8} more" : string.Empty;
                message = $"Delete {targets.Count} items?\n\n{preview}{extra}\n\nThis cannot be undone.";
            }

            var confirm = ModernMessageBox.ShowWithResult(
                message,
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
                var parentFolders = new List<FileSystemItem>();
                var deletedNames = new List<string>();
                foreach (var item in targets)
                {
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

                    deletedNames.Add(item.Name);
                    var parentPath = item.Parent?.FullPath
                        ?? Path.GetDirectoryName(item.FullPath)
                        ?? _projectPath;
                    var parentFolder = item.Parent ?? FindFolderItemByPath(parentPath ?? string.Empty);
                    if (parentFolder != null && parentFolders.All(existing =>
                            !string.Equals(existing.FullPath, parentFolder.FullPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        parentFolders.Add(parentFolder);
                    }
                }

                if (parentFolders.Count == 0)
                {
                    await RefreshFromDiskAsync(preserveSelection: true, quiet: false);
                }
                else
                {
                    foreach (var parent in parentFolders)
                    {
                        await RefreshFolderAsync(parent);
                    }
                }

                TreeViewExtendedSelectionBehavior.ClearSelection(FileTreeView);
                StatusText.Text = deletedNames.Count == 1
                    ? $"Deleted {deletedNames[0]}"
                    : $"Deleted {deletedNames.Count} items";
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
                ApplyLocalTreeSearch();
                folder.IsExpanded = wasExpanded;
                folder.CheckParentStatus();
                folder.RefreshUploadStateFromChildren();

                if (_mappedLocalRoots.Count > 0)
                {
                    ApplyMappedFolderBadges(expandPrimary: false);
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
            => DependencyObjectAncestors.Find<T>(child);

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

        /// <summary>
        /// If selection includes gitignored items, ask: upload them / skip them / cancel.
        /// Returns false when the user cancels or nothing remains after skip.
        /// </summary>
        private bool TryFilterIgnoredUploadSelection(
            ref List<FileSystemItem> filesToUpload,
            ref List<FileSystemItem> foldersToCreate)
        {
            // Cache gitignore lines once for this selection (can be thousands of files).
            _ignoreLinesCache = string.IsNullOrEmpty(_projectPath)
                ? new List<string>()
                : GitIgnoreFileHelper.ReadLines(_projectPath);

            var ignoredFiles = filesToUpload.Where(IsItemGitIgnored).ToList();
            var ignoredFolders = foldersToCreate.Where(IsItemGitIgnored).ToList();
            _ignoreLinesCache = null;

            if (ignoredFiles.Count == 0 && ignoredFolders.Count == 0)
            {
                return true;
            }

            var samples = ignoredFiles.Concat(ignoredFolders)
                .Select(i =>
                {
                    try
                    {
                        return Path.GetRelativePath(_projectPath, i.FullPath).Replace("\\", "/");
                    }
                    catch
                    {
                        return i.Name;
                    }
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
            var more = (ignoredFiles.Count + ignoredFolders.Count) - samples.Count;
            var sampleText = string.Join("\n• ", samples) + (more > 0 ? $"\n• (+{more} more)" : string.Empty);

            var choice = ModernMessageBox.ShowWithResult(
                $"{ignoredFiles.Count} ignored file(s) and {ignoredFolders.Count} ignored folder(s) are selected.\n\n" +
                $"Examples:\n• {sampleText}\n\n" +
                "Include gitignored / ignored items in this upload?",
                "Ignored files in selection",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question,
                primaryText: "Upload ignored",
                secondaryText: "Skip ignored",
                cancelText: "Cancel",
                context: this);

            if (choice == MessageBoxResult.Cancel || choice == MessageBoxResult.None)
            {
                return false;
            }

            if (choice == MessageBoxResult.No)
            {
                _ignoreLinesCache = string.IsNullOrEmpty(_projectPath)
                    ? new List<string>()
                    : GitIgnoreFileHelper.ReadLines(_projectPath);
                filesToUpload = filesToUpload.Where(f => !IsItemGitIgnored(f)).ToList();
                foldersToCreate = foldersToCreate.Where(f => !IsItemGitIgnored(f)).ToList();
                _ignoreLinesCache = null;

                if (filesToUpload.Count == 0 && foldersToCreate.Count == 0)
                {
                    ModernMessageBox.Show(
                        "Nothing left to upload after skipping ignored items.",
                        "Direct Upload",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information,
                        context: this);
                    return false;
                }
            }

            return true;
        }

        private List<string>? _ignoreLinesCache;

        private bool IsItemGitIgnored(FileSystemItem item)
        {
            // Folder marked ignored (e.g. .venv) → all nested selected files count as ignored.
            for (var cur = item; cur != null; cur = cur.Parent)
            {
                if (cur.GitState == GitItemState.Ignored)
                {
                    return true;
                }
            }

            if (string.IsNullOrEmpty(_projectPath))
            {
                return false;
            }

            try
            {
                var relative = Path.GetRelativePath(_projectPath, item.FullPath).Replace("\\", "/");
                if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith("..", StringComparison.Ordinal))
                {
                    return false;
                }

                var lines = _ignoreLinesCache ?? GitIgnoreFileHelper.ReadLines(_projectPath);
                if (GitIgnoreFileHelper.IsItemIgnored(lines, relative, item.IsFolder))
                {
                    return true;
                }

                return GitIgnoreFileHelper.IsPathUnderIgnoredDirectory(lines, relative);
            }
            catch
            {
                return false;
            }
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
            var files = (filesToUpload ?? Array.Empty<FileSystemItem>()).ToList();
            var folders = (foldersToCreate ?? Array.Empty<FileSystemItem>()).ToList();
            if (_isUploading || (files.Count == 0 && folders.Count == 0))
            {
                return;
            }

            if (!TryFilterIgnoredUploadSelection(ref files, ref folders))
            {
                StatusText.Text = "Ready.";
                return;
            }

            filesToUpload = files;
            foldersToCreate = folders;
            if (filesToUpload.Count == 0 && foldersToCreate.Count == 0)
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
            var forceUpload = OverwriteCheck.IsChecked == true;

            if (forceUpload && !skipConfirm && UseSessionCheck.IsChecked == true)
            {
                // Overwrite always re-sends selected files. Keep a fresh crash log only.
                if (File.Exists(sessionPath))
                {
                    File.Delete(sessionPath);
                }

                File.Create(sessionPath).Close();
                CheckSessionStatus();
            }
            else if (!skipConfirm && UseSessionCheck.IsChecked == true && File.Exists(sessionPath))
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

            var profile = ResolveUploadProfile(config);
            if (profile == null || string.IsNullOrWhiteSpace(profile.Host))
            {
                ModernMessageBox.Show("No deployment connection is assigned to this project yet. Open Settings → Connection Manager and select a connection.", "Connection Missing", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var workers = 1;

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

            Action? pendingDialog = null;
            try
            {
                StatusText.Text = $"Connecting · {workers} workers requested";
                AddUploadLog($"Workers requested: {workers}");
                _transferMonitor.Show(this, $"Upload · {workers} workers requested");
                _transferMonitor.Update(new ParallelTransferProgress
                {
                    Phase = "Preparing",
                    RequestedWorkers = workers,
                    Headline = $"{workers} workers requested · preparing folders",
                    LastLine = "Ensuring remote folders",
                    Sequence = 1
                });

                string defaultRemoteBase = !string.IsNullOrWhiteSpace(_activeRemoteBasePath)
                    ? _activeRemoteBasePath
                    : NormalizeRemoteBase(config.RemotePath);
                string profileRemoteBase = !string.IsNullOrWhiteSpace(_profileRemoteBasePath)
                    ? _profileRemoteBasePath
                    : NormalizeRemoteBase(config.RemotePath);

                int processed = 0;
                int skipped = 0;
                int foldersCreated = 0;
                var skipIfExists = !forceUpload;
                var useSession = UseSessionCheck.IsChecked == true;

                IRemoteFileService? folderService = RemoteFileServiceFactory.Create(profile);
                try
                {
                    await folderService.ConnectAsync(profile, token);

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
                        await folderService.EnsureDirectoryAsync(remotePath, token);
                        foldersCreated++;
                        AddUploadLog($"  ✓ Folder ready");
                        AddUploadLog("");
                    }
                }
                finally
                {
                    try
                    {
                        await folderService.DisconnectAsync();
                    }
                    catch
                    {
                        folderService.Abort();
                    }
                }

                var jobs = new List<RemoteTransferJob>();
                foreach (var file in filesToUpload)
                {
                    if (token.IsCancellationRequested) break;

                    ResolveUploadPaths(file.FullPath, profileRemoteBase, defaultRemoteBase,
                        out string relativePath, out string remoteBasePath);

                    if (!forceUpload && resumeSession && uploadedFiles.Contains(relativePath))
                    {
                        skipped++;
                        processed++;
                        Dispatcher.Invoke(() => UploadProgressBar.Value = processed);
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

                    string remotePath = CombineRemotePaths(remoteBasePath, relativePath);
                    Dispatcher.Invoke(() =>
                    {
                        file.UploadState = UploadState.InProgress;
                        AddUploadLog($"[{AppTimeService.LocalNow:HH:mm:ss}] Queued: {file.Name}");
                        AddUploadLog($"  → Remote Path: {remotePath}");
                    });

                    jobs.Add(new RemoteTransferJob
                    {
                        RemotePath = remotePath,
                        LocalPath = file.FullPath,
                        SizeBytes = file.Size > 0 ? file.Size : GetFileSizeSafe(file.FullPath),
                        Tag = relativePath,
                        Source = file
                    });
                }

                ParallelTransferResult? transferResult = null;
                if (jobs.Count == 0 && !token.IsCancellationRequested)
                {
                    StatusText.Text = skipped > 0
                        ? "Nothing uploaded — files were skipped."
                        : "Nothing to upload.";
                    _transferMonitor.Finish(skipped > 0
                        ? $"Skipped {skipped} (crash recovery). Overwrite forces a re-upload."
                        : "No files queued");
                    if (!skipConfirm && skipped > 0)
                    {
                        pendingDialog = () => ModernMessageBox.Show(
                            $"No files uploaded.\nSkipped: {skipped}\n\nCrash Recovery treated them as already sent.\nTurn Overwrite on to force upload.",
                            "Upload",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                }
                else if (jobs.Count > 1 && !TransferWorkerPrompt.TryAsk(this, "upload", jobs.Count, out workers))
                {
                    StatusText.Text = "Ready.";
                    _transferMonitor.Finish("Cancelled before workers");
                }
                else if (jobs.Count > 0 && !token.IsCancellationRequested)
                {
                    var uploadProgress = new Progress<RemoteUploadProgress>(p =>
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            var detail = p.Detail;
                            var bytes = detail?.BytesTransferred ?? p.BytesTransferred;
                            var total = detail?.TotalBytes ?? p.TotalBytes;
                            var percent = detail?.Percent ?? p.Percent;

                            if (detail != null)
                            {
                                StatusText.Text = $"{detail.ActiveWorkers}/{detail.RequestedWorkers} workers · {detail.Completed}/{detail.Total} files · {detail.Phase}";
                            }
                            else
                            {
                                StatusText.Text = string.IsNullOrWhiteSpace(p.CurrentFileName)
                                    ? "Uploading..."
                                    : p.CurrentFileName;
                            }

                            UploadProgressBar.Maximum = 100;
                            UploadProgressBar.Value = Math.Clamp(percent, 0, 100);

                            if (total > 0)
                            {
                                UploadDetailText.Text = $"{FormatSizeReadable(bytes)} / {FormatSizeReadable(total)} ({percent:0.1}%)";
                            }
                            else if (detail != null)
                            {
                                UploadDetailText.Text = $"{detail.Completed}/{detail.Total} files ({percent:0}%)";
                            }
                            else
                            {
                                UploadDetailText.Text = string.Empty;
                            }
                        }, DispatcherPriority.Background);
                    });

                    transferResult = await ParallelRemoteTransfer.UploadAsync(
                        profile,
                        jobs,
                        workers,
                        uploadProgress,
                        token,
                        skipIfExists,
                        (job, ok, error) =>
                        {
                            var item = job.Source as FileSystemItem;
                            Dispatcher.Invoke(() =>
                            {
                                processed++;
                                UploadProgressBar.Value = Math.Min(progressMax, processed);
                                if (item != null)
                                {
                                    item.UploadState = ok ? UploadState.Uploaded : UploadState.Failed;
                                }

                                if (ok)
                                {
                                    AddUploadLog($"  ✓ {job.DisplayName}");
                                    AddUploadLog("");
                                    if (useSession && !string.IsNullOrWhiteSpace(job.Tag))
                                    {
                                        try
                                        {
                                            File.AppendAllText(sessionPath, job.Tag + Environment.NewLine);
                                            CheckSessionStatus();
                                        }
                                        catch
                                        {
                                        }
                                    }
                                }
                                else
                                {
                                    AddUploadLog($"  ✗ {job.DisplayName}: {error}");
                                    AddUploadLog("");
                                }
                            });
                        },
                        new Progress<ParallelTransferProgress>(_transferMonitor.Update));
                }

                if (token.IsCancellationRequested)
                {
                    _transferMonitor.Finish("Upload cancelled");
                    StatusText.Text = "Upload Stopped by User 🛑";
                    if (!skipConfirm)
                    {
                        pendingDialog = () => ModernMessageBox.Show(
                            "Upload process was stopped.",
                            "Stopped",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                }
                else if (jobs.Count == 0 || transferResult == null)
                {
                    // Skip/cancel already set StatusText and pendingDialog.
                }
                else if (!transferResult.IsComplete)
                {
                    var errorCount = transferResult.Errors.Count;
                    StatusText.Text = "Upload incomplete.";
                    AddUploadLog($"Verify: completed {transferResult.Completed}/{jobs.Count}, failed {errorCount}, workers {transferResult.WorkerCount}");
                    foreach (var error in transferResult.Errors.Take(8))
                    {
                        AddUploadLog($"  • {error}");
                    }

                    _transferMonitor.Finish($"Incomplete: {transferResult.Completed}/{jobs.Count} · {errorCount} failed");
                    var errorPreview = string.Join("\n", transferResult.Errors.Take(6));
                    pendingDialog = () => ModernMessageBox.Show(
                        $"Upload finished with gaps.\nUploaded: {transferResult.Completed}/{jobs.Count}\nWorkers: {transferResult.WorkerCount}\nFailed: {errorCount}\n\n{errorPreview}",
                        "Upload",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                else
                {
                    StatusText.Text = "Upload Completed! ✅";
                    _transferMonitor.Finish($"Done: {transferResult?.Completed ?? 0} files · {transferResult?.WorkerCount ?? workers} workers");
                    if (!skipConfirm && File.Exists(sessionPath))
                    {
                        File.Delete(sessionPath);
                    }

                    CheckSessionStatus();
                    if (!skipConfirm)
                    {
                        var uploadedCount = Math.Max(0, (transferResult?.Completed ?? 0) - (transferResult?.Skipped ?? 0));
                        var skippedCount = skipped + (transferResult?.Skipped ?? 0);
                        pendingDialog = () => ModernMessageBox.Show(
                            $"Upload Complete!\nUploaded: {uploadedCount}\nFolders: {foldersCreated}\nSkipped: {skippedCount}\nWorkers: {workers}",
                            "Success",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
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
                pendingDialog = () => ModernMessageBox.Show(
                    detailedMessage,
                    "Upload Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
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

            pendingDialog?.Invoke();
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
            var profile = ResolveConnectionProfile(LoadCurrentProjectConfig(out _).ConnectionProfileId);
            var mappings = RemotePathResolver.GetActiveMappings(profile);

            if (!string.IsNullOrEmpty(projectRoot) &&
                RemotePathResolver.TryResolveDeployTargetFromFullPath(
                    localFullPath,
                    projectRoot,
                    mappings,
                    profileRemoteBase,
                    out var remoteFullPath,
                    out _,
                    out _))
            {
                // Caller combines remoteBase + relative; "/" means "use base as final path".
                remoteBasePath = remoteFullPath;
                relativePath = "/";
                return;
            }

            // Mapping local is a subfolder of the project (legacy primary fallback).
            if (!string.IsNullOrEmpty(_mappedLocalRoot) && IsSameOrNestedPath(_mappedLocalRoot, localFullPath))
            {
                relativePath = Path.GetRelativePath(_mappedLocalRoot, localFullPath).Replace("\\", "/");
                remoteBasePath = mappedRemoteBase;
                return;
            }

            relativePath = Path.GetRelativePath(projectRoot, localFullPath).Replace("\\", "/");

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
            return RemotePathResolver.GetPrimaryMapping(profile);
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
                if (!RemotePathResolver.IsProjectRootLocalPath(localSegment) && !string.IsNullOrEmpty(_projectPath))
                {
                    var normalizedSegment = RemotePathResolver.NormalizeLocalMappingPath(localSegment)
                        .Replace("/", System.IO.Path.DirectorySeparatorChar.ToString());
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

        private ConnectionProfile? ResolveUploadProfile(ProjectConfig config)
        {
            var profile = ResolveConnectionProfile(config.ConnectionProfileId);
            if (profile != null && !string.IsNullOrWhiteSpace(profile.Host))
            {
                return profile;
            }

            if (string.IsNullOrWhiteSpace(config.FtpHost))
            {
                return null;
            }

            return new ConnectionProfile
            {
                Id = string.IsNullOrWhiteSpace(config.ConnectionProfileId) ? "legacy-project-ftp" : config.ConnectionProfileId,
                Name = config.FtpHost,
                Host = config.FtpHost,
                Username = config.FtpUsername,
                Password = config.FtpPassword,
                Port = config.FtpPort > 0 ? config.FtpPort : (config.UseSSH ? 22 : 21),
                UseSSH = config.UseSSH,
                RemotePath = config.RemotePath,
                PassiveMode = true
            };
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
                    var localLabel = RemotePathResolver.IsProjectRootLocalPath(mapping.LocalPath)
                        ? "(project root)"
                        : mapping.LocalPath;
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

    public sealed class LocalTreeSearchHit
    {
        public FileSystemItem Item { get; init; } = null!;
        public string DisplayPath { get; init; } = string.Empty;
        public string PathPrefix { get; init; } = string.Empty;
        public string PathMatch { get; init; } = string.Empty;
        public string PathSuffix { get; init; } = string.Empty;
        public string Icon { get; init; } = string.Empty;
        public System.Windows.Media.Brush IconColor { get; init; } = System.Windows.Media.Brushes.White;

        public static LocalTreeSearchHit FromItem(FileSystemItem item, string query, string projectRoot)
        {
            var display = BuildDisplayPath(item.FullPath, projectRoot);
            var parts = TreeNameSearch.Split(display, query);
            return new LocalTreeSearchHit
            {
                Item = item,
                DisplayPath = display,
                PathPrefix = parts.Prefix,
                PathMatch = parts.Match,
                PathSuffix = parts.Suffix,
                Icon = item.Icon,
                IconColor = item.IconColor
            };
        }

        private static string BuildDisplayPath(string? fullPath, string? projectRoot)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return "/";
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(projectRoot))
                {
                    var relative = Path.GetRelativePath(projectRoot, fullPath).Replace('\\', '/');
                    if (string.IsNullOrWhiteSpace(relative) || relative == ".")
                    {
                        return "/";
                    }

                    if (relative.StartsWith("..", StringComparison.Ordinal))
                    {
                        return "/" + Path.GetFileName(fullPath);
                    }

                    return "/" + relative.TrimStart('/');
                }
            }
            catch
            {
                // Fall through to file name.
            }

            return "/" + Path.GetFileName(fullPath);
        }
    }

    public class FileSystemItem : INotifyPropertyChanged, ITreeMultiSelectable
    {
        private bool? _isChecked = false;
        private bool _isExpanded;
        private bool _isMultiSelected;
        private UploadState _uploadState = UploadState.Pending;
        private GitItemState _gitState = GitItemState.None;
        private bool _isMappedFolder;
        private bool _isSearchVisible = true;
        private string? _namePrefix;
        private string _nameMatch = string.Empty;
        private string _nameSuffix = string.Empty;

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

        public bool IsSearchVisible
        {
            get => _isSearchVisible;
            set
            {
                if (_isSearchVisible != value)
                {
                    _isSearchVisible = value;
                    OnPropertyChanged(nameof(IsSearchVisible));
                    OnPropertyChanged(nameof(SearchVisibility));
                }
            }
        }

        public Visibility SearchVisibility =>
            _isSearchVisible ? Visibility.Visible : Visibility.Collapsed;

        public string NamePrefix => _namePrefix ?? Name;
        public string NameMatch => _nameMatch;
        public string NameSuffix => _nameSuffix;

        public void ApplySearchVisual(bool visible, TreeNameSearch.NameParts parts, bool expand)
        {
            IsSearchVisible = visible;
            _namePrefix = parts.Prefix;
            if (!string.Equals(_nameMatch, parts.Match, StringComparison.Ordinal))
            {
                _nameMatch = parts.Match ?? string.Empty;
                OnPropertyChanged(nameof(NameMatch));
            }

            if (!string.Equals(_nameSuffix, parts.Suffix, StringComparison.Ordinal))
            {
                _nameSuffix = parts.Suffix ?? string.Empty;
                OnPropertyChanged(nameof(NameSuffix));
            }

            OnPropertyChanged(nameof(NamePrefix));
            if (expand)
            {
                IsExpanded = true;
            }
        }

        public ObservableCollection<FileSystemItem> Children { get; set; } = new ObservableCollection<FileSystemItem>();
        public FileSystemItem? Parent { get; set; }
        public bool IncludeInMultiSelect => true;
        System.Collections.IEnumerable ITreeMultiSelectable.Children => Children;

        public bool IsMultiSelected
        {
            get => _isMultiSelected;
            set
            {
                if (_isMultiSelected != value)
                {
                    _isMultiSelected = value;
                    OnPropertyChanged(nameof(IsMultiSelected));
                }
            }
        }

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
