using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GitDeployPro.Controls;
using GitDeployPro.Windows;
using GitDeployPro.Services;
using GitDeployPro.Services.Remote;
using GitDeployPro.Services.Theme;
using GitDeployPro.Models;
using FluentFTP;

namespace GitDeployPro.Pages
{
    public partial class DeployPage : Page
    {
        private enum RemoteWorkspaceLayoutMode
        {
            Wide,
            Medium,
            Narrow
        }

        private const double RemoteWideBreakpoint = 1500;
        private const double RemoteMediumBreakpoint = 1100;
        private const double RemoteNarrowBreakpoint = 900;
        private const double RailWidth = 40;
        private const double SplitterWidth = 8;
        private const double MainColumnMinWidth = 280;
        private const double RemoteDockMinWidth = 260;
        private const double LeftDockMinWidth = 220;
        private const double CenterContentMinHeight = 180;

        private bool isDeploying = false;
        private GitService _gitService;
        private HistoryService _historyService;
        private ConfigurationService _configService;
        private List<DeployFileViewModel> _fileViewModels = new List<DeployFileViewModel>();
        private bool _isLoaded = false;
        private ProjectConfig _projectConfig;
        private BranchStatusInfo _branchStatus = new BranchStatusInfo();
        private DispatcherTimer _autoRefreshTimer = new DispatcherTimer();
        private bool _isRefreshingGit;
        private int _cachedUncommittedCount = -1;
        private int _cachedTotalCommits = -1;
        private bool _compareResultActive;
        private string _compareSourceBranch = string.Empty;
        private string _compareTargetBranch = string.Empty;
        private bool _suppressFileSelectionModal;
        private bool _isRemoteWorkspaceCollapsed = true;
        private bool _isDirectUploadDockCollapsed = true;
        private bool _isBottomDockCollapsed;
        private bool _bottomTerminalTabActive;
        private bool _logsStripVisible;
        private bool _logsStripAutoShownForDeploy;
        private bool _isResizingBottomDock;
        private readonly List<DeployTerminalSession> _deployTerminalSessions = new();
        private readonly List<NewTerminalPickerItem> _newTerminalPickerItems = new();
        private string? _activeDeployTerminalSessionId;
        private double _bottomDockResizeStartY;
        private double _bottomDockResizeStartHeight;
        private string _remoteWorkspaceProjectPath = string.Empty;
        private bool _isRemoteEditorOverlayActive;
        private GridLength _remotePanelLastWidth = new GridLength(420);
        private GridLength _leftDockLastWidth = new GridLength(340);
        private GridLength _bottomDockLastHeight = new GridLength(0);
        private const double BottomDockTerminalHeightRatio = 0.35;
        private const double BottomDockCollapsedHeight = 33;
        private const double BottomDockMinExpandedHeight = 160;
        private RemoteWorkspaceLayoutMode _remoteLayoutMode = RemoteWorkspaceLayoutMode.Wide;
        private bool _compactPanelOpenedByUser;
        private bool _isBranchDetailsExpanded;
        private bool _isPortrait;
        private bool _suppressDeployThemeComboChange;
        private int _autoRefreshTickCount;
        private DateTime _lastBranchRefreshUtc = DateTime.MinValue;
        private static readonly TimeSpan BranchRefreshInterval = TimeSpan.FromSeconds(60);
        private const int FullRefreshTickInterval = 6;

        public DeployPage()
        {
            InitializeComponent();
            _isLoaded = false;
            _gitService = new GitService();
            _historyService = new HistoryService();
            _configService = new ConfigurationService();
            _projectConfig = new ProjectConfig();
            DeployRemoteWorkspace.EditorModeChanged += DeployRemoteWorkspace_EditorModeChanged;
            if (DirectUploadDock != null)
            {
                DirectUploadDock.EditorModeChanged += DirectUploadDock_EditorModeChanged;
                DirectUploadDock.UploadActionsPanelVisibilityChanged += DirectUploadDock_UploadActionsPanelVisibilityChanged;
            }
            Loaded += DeployPage_Loaded;
            SizeChanged += DeployPage_SizeChanged;
            Unloaded += DeployPage_Unloaded;
            LoadGitData(includeExpensiveOperations: true, refreshBranches: true);
            SetupAutoRefreshTimer();
        }

        private void DeployPage_Loaded(object sender, RoutedEventArgs e)
        {
            _isBottomDockCollapsed = false;
            ApplyWorkspaceLayout(force: true);
            ShowBottomLogsTab();
            UpdateUploadActionsToggleButton();
            ApplyBranchDetailsVisibility();
            UpdateBranchSummaryUi();
            InitializeDeployThemePicker();
        }

        private void DeployPage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            CaptureDockSizes();

            var mode = DetermineRemoteLayoutMode();
            var portrait = ActualHeight > 0 && ActualWidth > 0 && ActualHeight > ActualWidth;
            if (mode != _remoteLayoutMode || portrait != _isPortrait)
            {
                ApplyWorkspaceLayout(force: true);
                return;
            }

            ApplyPortraitBranchRowTweaks();
            ShrinkSideDocksToFitIfNeeded();
            ClampOpenDockWidthsToAvailableSpace();
            if (!_isBottomDockCollapsed)
            {
                ApplyBottomDockLayout(resetHeight: false);
            }
        }

        private void DeployPage_Unloaded(object sender, RoutedEventArgs e)
        {
            DeployRemoteWorkspace.NotifyHostTeardown();
            DeployRemoteWorkspace.EditorModeChanged -= DeployRemoteWorkspace_EditorModeChanged;
            if (DirectUploadDock != null)
            {
                DirectUploadDock.EditorModeChanged -= DirectUploadDock_EditorModeChanged;
                DirectUploadDock.UploadActionsPanelVisibilityChanged -= DirectUploadDock_UploadActionsPanelVisibilityChanged;
                DirectUploadDock.TryCloseLocalEditor(force: true);
            }

            _ = DisposeAllDeployTerminalSessionsAsync();
            DetachDeployThemeHandlers();

            Loaded -= DeployPage_Loaded;
            SizeChanged -= DeployPage_SizeChanged;
            Unloaded -= DeployPage_Unloaded;
            if (_autoRefreshTimer != null)
            {
                _autoRefreshTimer.Stop();
                _autoRefreshTimer.Tick -= AutoRefreshTimer_Tick;
            }
        }

        private void DeployRemoteWorkspace_EditorModeChanged(object? sender, RemoteEditorModeChangedEventArgs e)
        {
            // Host editor in the center Deploy area; FTP tree stays as in-control sidebar.
            // Unloaded disconnect is gated by NotifyHostTeardown so reparent is safe.
            if (e.IsOpen)
            {
                DirectUploadDock?.TryCloseLocalEditor(force: true);
            }

            SetRemoteEditorOverlay(e.IsOpen);
        }

        private void DirectUploadDock_EditorModeChanged(object? sender, LocalEditorModeChangedEventArgs e)
        {
            SetLocalDirectUploadEditorOverlay(e.IsOpen);
        }

        private void SetLocalDirectUploadEditorOverlay(bool enable)
        {
            if (DirectUploadDock == null || CenterEditorOverlayHost == null)
            {
                return;
            }

            if (enable)
            {
                if (_isRemoteEditorOverlayActive)
                {
                    SetRemoteEditorOverlay(false);
                }

                DirectUploadDock.HostLocalEditorIn(CenterEditorOverlayHost);
                CenterEditorOverlayHost.Visibility = Visibility.Visible;
                System.Windows.Controls.Panel.SetZIndex(CenterEditorOverlayHost, 40);
                return;
            }

            DirectUploadDock.RestoreLocalEditorHome();
            if (!_isRemoteEditorOverlayActive)
            {
                CenterEditorOverlayHost.Child = null;
                CenterEditorOverlayHost.Visibility = Visibility.Collapsed;
            }
        }

        private void DetachDeployPage_Click(object sender, RoutedEventArgs e)
        {
            var window = new PageHostWindow(new DeployPage(), "Deploy • Detached");
            WindowOwnerService.ShowOwned(window, this);
        }

        private async void ToggleRemoteWorkspaceButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isRemoteEditorOverlayActive || DeployRemoteWorkspace.IsEditorOpen)
            {
                if (!await DeployRemoteWorkspace.TryCloseEditorViewAsync(promptUnsaved: true))
                {
                    return;
                }

                ApplyWorkspaceLayout(force: true);
                return;
            }

            _isRemoteWorkspaceCollapsed = !_isRemoteWorkspaceCollapsed;
            if (!_isRemoteWorkspaceCollapsed)
            {
                _compactPanelOpenedByUser = true;
            }
            else if (_isRemoteWorkspaceCollapsed && _isDirectUploadDockCollapsed)
            {
                _compactPanelOpenedByUser = false;
            }

            ApplyWorkspaceLayout(force: true);
        }

        private void OpenBottomTerminalButton_Click(object sender, RoutedEventArgs e)
        {
            // PhpStorm-style: same tool icon toggles the tool window closed.
            if (_bottomTerminalTabActive && !_isBottomDockCollapsed)
            {
                CaptureBottomDockHeight();
                _isBottomDockCollapsed = true;
                ApplyBottomDockLayout(resetHeight: true);
                UpdateBottomCollapseButtonUi();
                UpdateBottomTerminalRailUi();
                return;
            }

            ShowBottomTerminalTab();
            _ = EnsureDeployTerminalSessionAsync();
        }

        private void OpenBottomLogsButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_bottomTerminalTabActive && _logsStripVisible && !_isBottomDockCollapsed)
            {
                CaptureBottomDockHeight();
                _isBottomDockCollapsed = true;
                ApplyBottomDockLayout(resetHeight: true);
                UpdateBottomCollapseButtonUi();
                UpdateBottomTerminalRailUi();
                return;
            }

            if (_bottomTerminalTabActive && !_isBottomDockCollapsed)
            {
                // Keep terminal sessions alive; toggle deploy-log strip above them.
                _logsStripVisible = !_logsStripVisible;
                _isBottomDockCollapsed = false;
                if (_logsStripVisible)
                {
                    EnsureBottomDockTallEnoughForSplit();
                }

                ApplyBottomContentLayout();
                ApplyBottomDockLayout(resetHeight: false);
                UpdateBottomTabButtonStyles();
                UpdateBottomCollapseButtonUi();
                return;
            }

            ShowBottomLogsTab();
        }

        private void ToggleDirectUploadDockButton_Click(object sender, RoutedEventArgs e)
        {
            _isDirectUploadDockCollapsed = !_isDirectUploadDockCollapsed;
            if (!_isDirectUploadDockCollapsed)
            {
                _compactPanelOpenedByUser = true;
            }
            else if (_isRemoteWorkspaceCollapsed && _isDirectUploadDockCollapsed)
            {
                _compactPanelOpenedByUser = false;
            }

            ApplyWorkspaceLayout(force: true);
        }

        private void CollapseDirectUploadDock_Click(object sender, RoutedEventArgs e)
        {
            _isDirectUploadDockCollapsed = true;
            ApplyWorkspaceLayout(force: true);
        }

        private void RefreshDirectUploadDock_Click(object sender, RoutedEventArgs e)
        {
            _ = DirectUploadDock.RefreshFromDiskPublicAsync();
        }

        private void ToggleUploadActionsButton_Click(object sender, RoutedEventArgs e)
        {
            DirectUploadDock?.ToggleUploadActionsPanel();
            UpdateUploadActionsToggleButton();
        }

        private void DirectUploadDock_UploadActionsPanelVisibilityChanged(object? sender, EventArgs e)
        {
            UpdateUploadActionsToggleButton();
        }

        private void UpdateUploadActionsToggleButton()
        {
            if (ToggleUploadActionsButton == null || DirectUploadDock == null)
            {
                return;
            }

            var pinned = DirectUploadDock.IsUploadActionsPanelPinned;
            var visible = DirectUploadDock.IsUploadActionsPanelVisible;
            ToggleUploadActionsButton.Opacity = pinned || visible ? 1.0 : 0.7;
            ToggleUploadActionsButton.ToolTip = pinned
                ? "Hide upload actions"
                : "Upload actions";
        }

        private void ToggleBottomDockButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isBottomDockCollapsed)
            {
                CaptureBottomDockHeight();
            }

            _isBottomDockCollapsed = !_isBottomDockCollapsed;
            ApplyBottomDockLayout(resetHeight: true);
            UpdateBottomCollapseButtonUi();
            UpdateBottomTerminalRailUi();
        }

        private void BottomLogsTabButton_Click(object sender, RoutedEventArgs e) => ShowBottomLogsTab();

        private void ShowBottomLogsTab()
        {
            _bottomTerminalTabActive = false;
            _logsStripVisible = true;
            _isBottomDockCollapsed = false;
            ApplyBottomContentLayout();
            ApplyBottomDockLayout(resetHeight: false);
            UpdateBottomTabButtonStyles();
            UpdateBottomCollapseButtonUi();
        }

        private void ShowBottomTerminalTab()
        {
            _bottomTerminalTabActive = true;
            _isBottomDockCollapsed = false;
            if (!isDeploying)
            {
                _logsStripVisible = false;
            }

            // Prefer ~35% of the page when opening Terminal.
            var preferred = GetPreferredTerminalHeight();
            if (_bottomDockLastHeight.Value < preferred * 0.9)
            {
                _bottomDockLastHeight = new GridLength(preferred);
            }

            ApplyBottomContentLayout();
            ApplyBottomDockLayout(resetHeight: true);
            UpdateBottomTabButtonStyles();
            UpdateBottomCollapseButtonUi();
        }

        private async void NewTerminalTabButton_Click(object sender, RoutedEventArgs e)
        {
            ShowBottomTerminalTab();
            await OpenLocalDeployTerminalAsync();
        }

        private void NewTerminalMenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (NewTerminalPickerPopup == null)
            {
                return;
            }

            if (NewTerminalPickerPopup.IsOpen)
            {
                NewTerminalPickerPopup.IsOpen = false;
                return;
            }

            RebuildNewTerminalPickerItems();
            ApplyNewTerminalPickerFilter(string.Empty);
            if (NewTerminalSearchBox != null)
            {
                NewTerminalSearchBox.Text = string.Empty;
            }

            NewTerminalPickerPopup.IsOpen = true;
        }

        private void NewTerminalPickerPopup_Opened(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (NewTerminalSearchBox == null)
                {
                    return;
                }

                NewTerminalSearchBox.Focus();
                Keyboard.Focus(NewTerminalSearchBox);
                NewTerminalSearchBox.SelectAll();
            }), DispatcherPriority.Input);
        }

        private void RebuildNewTerminalPickerItems()
        {
            _newTerminalPickerItems.Clear();
            _newTerminalPickerItems.Add(new NewTerminalPickerItem
            {
                Title = "Local Terminal",
                Subtitle = "cmd / PowerShell on this PC",
                IsLocal = true
            });

            var projectProfile = GetActiveConnectionProfile();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (projectProfile != null)
            {
                seen.Add(projectProfile.Id);
                _newTerminalPickerItems.Add(new NewTerminalPickerItem
                {
                    Title = projectProfile.Name,
                    Subtitle = BuildConnectionSubtitle(projectProfile, isProjectDefault: true),
                    Profile = projectProfile
                });
            }

            try
            {
                foreach (var conn in _configService.LoadConnections().OrderBy(c => c.Name))
                {
                    if (conn == null || string.IsNullOrWhiteSpace(conn.Id) || !seen.Add(conn.Id))
                    {
                        continue;
                    }

                    _newTerminalPickerItems.Add(new NewTerminalPickerItem
                    {
                        Title = conn.Name,
                        Subtitle = BuildConnectionSubtitle(conn, isProjectDefault: false),
                        Profile = conn
                    });
                }
            }
            catch
            {
                // Local option still available.
            }
        }

        private static string BuildConnectionSubtitle(ConnectionProfile profile, bool isProjectDefault)
        {
            var host = string.IsNullOrWhiteSpace(profile.Host) ? "server" : profile.Host.Trim();
            var prefix = isProjectDefault ? "Project server · " : string.Empty;
            return $"{prefix}{host}:{profile.Port}";
        }

        private void ApplyNewTerminalPickerFilter(string? query)
        {
            if (NewTerminalPickerList == null)
            {
                return;
            }

            var q = (query ?? string.Empty).Trim();
            IEnumerable<NewTerminalPickerItem> filtered = _newTerminalPickerItems;
            if (!string.IsNullOrEmpty(q))
            {
                filtered = _newTerminalPickerItems.Where(item =>
                    (item.Title?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (item.Subtitle?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (item.Profile?.Host?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (item.Profile?.Username?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            var list = filtered.ToList();
            NewTerminalPickerList.ItemsSource = list;
            NewTerminalPickerList.SelectedIndex = list.Count > 0 ? 0 : -1;
        }

        private void NewTerminalSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (NewTerminalSearchHint != null)
            {
                NewTerminalSearchHint.Visibility = string.IsNullOrEmpty(NewTerminalSearchBox?.Text)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            ApplyNewTerminalPickerFilter(NewTerminalSearchBox?.Text);
        }

        private void NewTerminalSearchBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                NewTerminalPickerPopup.IsOpen = false;
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter)
            {
                _ = AcceptSelectedNewTerminalPickerItemAsync();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Down && NewTerminalPickerList?.Items.Count > 0)
            {
                NewTerminalPickerList.Focus();
                if (NewTerminalPickerList.SelectedIndex < 0)
                {
                    NewTerminalPickerList.SelectedIndex = 0;
                }

                e.Handled = true;
            }
        }

        private void NewTerminalPickerList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                _ = AcceptSelectedNewTerminalPickerItemAsync();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                NewTerminalPickerPopup.IsOpen = false;
                e.Handled = true;
            }
        }

        private void NewTerminalPickerList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            _ = AcceptSelectedNewTerminalPickerItemAsync();
        }

        private void NewTerminalPickerItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBoxItem { DataContext: NewTerminalPickerItem item })
            {
                NewTerminalPickerList.SelectedItem = item;
                _ = AcceptSelectedNewTerminalPickerItemAsync();
                e.Handled = true;
            }
        }

        private async Task AcceptSelectedNewTerminalPickerItemAsync()
        {
            if (NewTerminalPickerList?.SelectedItem is not NewTerminalPickerItem item)
            {
                return;
            }

            NewTerminalPickerPopup.IsOpen = false;
            ShowBottomTerminalTab();

            if (item.IsLocal || item.Profile == null)
            {
                await OpenLocalDeployTerminalAsync();
                return;
            }

            await OpenServerDeployTerminalAsync(item.Profile);
        }

        private sealed class NewTerminalPickerItem
        {
            public string Title { get; init; } = string.Empty;
            public string Subtitle { get; init; } = string.Empty;
            public bool IsLocal { get; init; }
            public ConnectionProfile? Profile { get; init; }
            public Visibility SubtitleVisibility =>
                string.IsNullOrWhiteSpace(Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        }

        private async Task EnsureDeployTerminalSessionAsync()
        {
            if (_deployTerminalSessions.Count > 0)
            {
                ActivateDeployTerminalSession(_activeDeployTerminalSessionId ?? _deployTerminalSessions[0].Id);
                return;
            }

            await OpenLocalDeployTerminalAsync();
        }

        private async Task OpenLocalDeployTerminalAsync()
        {
            var title = NextLocalTerminalTitle();
            var control = CreateDeployTerminalControl();
            var session = AddDeployTerminalSession(title, control, isLocal: true, profile: null);
            ActivateDeployTerminalSession(session.Id);
            try
            {
                await control.ConnectLocal();
                await control.FocusTerminalAsync();
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Failed to open local terminal:\n{ex.Message}", "Terminal", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task OpenServerDeployTerminalAsync(ConnectionProfile profile)
        {
            if (profile == null)
            {
                return;
            }

            var control = CreateDeployTerminalControl();
            var session = AddDeployTerminalSession(profile.Name, control, isLocal: false, profile);
            ActivateDeployTerminalSession(session.Id);
            try
            {
                var password = EncryptionService.Decrypt(profile.Password);
                await control.ConnectAsync(profile.Host, profile.Username, password, profile.Port);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Failed to open server terminal:\n{ex.Message}", "Terminal", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private TerminalControl CreateDeployTerminalControl()
        {
            var control = new TerminalControl
            {
                ShowCommandBar = false,
                Visibility = Visibility.Collapsed
            };
            control.SetProjectPath(_projectConfig?.LocalProjectPath ?? string.Empty);
            if (control.DetachButton != null)
            {
                control.DetachButton.Visibility = Visibility.Collapsed;
            }

            return control;
        }

        private string NextLocalTerminalTitle()
        {
            var localCount = _deployTerminalSessions.Count(s => s.IsLocal);
            return localCount == 0 ? "Local" : $"Local ({localCount + 1})";
        }

        private DeployTerminalSession AddDeployTerminalSession(
            string title,
            TerminalControl control,
            bool isLocal,
            ConnectionProfile? profile)
        {
            var session = new DeployTerminalSession
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = title,
                Control = control,
                IsLocal = isLocal,
                Profile = profile
            };

            DeployTerminalHost.Children.Add(control);
            _deployTerminalSessions.Add(session);
            RebuildTerminalSessionTabs();
            return session;
        }

        private void ActivateDeployTerminalSession(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            _activeDeployTerminalSessionId = sessionId;
            _bottomTerminalTabActive = true;
            if (!isDeploying)
            {
                _logsStripVisible = false;
            }

            foreach (var session in _deployTerminalSessions)
            {
                session.Control.Visibility = session.Id == sessionId
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            _isBottomDockCollapsed = false;
            ApplyBottomContentLayout();
            ApplyBottomDockLayout(resetHeight: false);
            UpdateBottomTabButtonStyles();
            UpdateBottomCollapseButtonUi();
        }

        private async void CloseDeployTerminalSession_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not FrameworkElement { Tag: string sessionId })
            {
                return;
            }

            var session = _deployTerminalSessions.FirstOrDefault(s => s.Id == sessionId);
            if (session == null)
            {
                return;
            }

            var index = _deployTerminalSessions.IndexOf(session);
            _deployTerminalSessions.Remove(session);
            DeployTerminalHost.Children.Remove(session.Control);
            try
            {
                await session.Control.DisposeTerminalAsync();
            }
            catch
            {
                // Best-effort dispose.
            }

            if (_deployTerminalSessions.Count == 0)
            {
                _activeDeployTerminalSessionId = null;
                if (_bottomTerminalTabActive && !isDeploying)
                {
                    ShowBottomLogsTab();
                }
                else
                {
                    RebuildTerminalSessionTabs();
                    ApplyBottomContentLayout();
                    UpdateBottomTabButtonStyles();
                }

                return;
            }

            if (string.Equals(_activeDeployTerminalSessionId, sessionId, StringComparison.Ordinal))
            {
                var next = _deployTerminalSessions[Math.Max(0, Math.Min(index, _deployTerminalSessions.Count - 1))];
                ActivateDeployTerminalSession(next.Id);
            }
            else
            {
                RebuildTerminalSessionTabs();
            }
        }

        private void RebuildTerminalSessionTabs()
        {
            if (TerminalSessionTabsPanel == null)
            {
                return;
            }

            TerminalSessionTabsPanel.Children.Clear();
            foreach (var session in _deployTerminalSessions)
            {
                var isActive = _bottomTerminalTabActive
                    && string.Equals(session.Id, _activeDeployTerminalSessionId, StringComparison.Ordinal);
                var sessionId = session.Id;

                var tab = new Border
                {
                    Tag = sessionId,
                    Height = 26,
                    Margin = new Thickness(0, 0, 1, 0),
                    Padding = new Thickness(8, 0, 2, 0),
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new Thickness(0, 0, 0, 2),
                    BorderBrush = isActive
                        ? (System.Windows.Media.Brush)FindResource("Status.Warning")
                        : System.Windows.Media.Brushes.Transparent,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = session.IsLocal
                        ? "Local terminal session"
                        : $"Server: {session.Title}"
                };

                var content = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                content.Children.Add(new TextBlock
                {
                    Text = session.Title,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0),
                    FontSize = 11,
                    FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = (System.Windows.Media.Brush)FindResource(
                        isActive ? "Text.Primary" : "Text.Secondary")
                });
                var close = new System.Windows.Controls.Button
                {
                    Content = "×",
                    Tag = sessionId,
                    Width = 18,
                    Height = 18,
                    FontSize = 12,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 0, 2, 0),
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = (System.Windows.Media.Brush)FindResource("Text.Muted"),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = "Close session"
                };
                close.Click += CloseDeployTerminalSession_Click;
                content.Children.Add(close);
                tab.Child = content;
                tab.MouseLeftButtonUp += (_, e) =>
                {
                    if (e.OriginalSource is DependencyObject source
                        && FindParentButton(source) != null)
                    {
                        return;
                    }

                    ActivateDeployTerminalSession(sessionId);
                };
                TerminalSessionTabsPanel.Children.Add(tab);
            }
        }

        private static System.Windows.Controls.Button? FindParentButton(DependencyObject? node)
        {
            while (node != null)
            {
                if (node is System.Windows.Controls.Button button)
                {
                    return button;
                }

                node = VisualTreeHelper.GetParent(node);
            }

            return null;
        }

        private async Task DisposeAllDeployTerminalSessionsAsync()
        {
            var sessions = _deployTerminalSessions.ToList();
            _deployTerminalSessions.Clear();
            _activeDeployTerminalSessionId = null;
            TerminalSessionTabsPanel?.Children.Clear();
            DeployTerminalHost?.Children.Clear();

            foreach (var session in sessions)
            {
                try
                {
                    await session.Control.DisposeTerminalAsync();
                }
                catch
                {
                    // Ignore teardown errors.
                }
            }
        }

        private sealed class DeployTerminalSession
        {
            public string Id { get; init; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public TerminalControl Control { get; init; } = null!;
            public bool IsLocal { get; init; }
            public ConnectionProfile? Profile { get; init; }
        }

        private void ShowDeployLogsForActiveDeploy()
        {
            _logsStripVisible = true;
            _logsStripAutoShownForDeploy = true;
            _isBottomDockCollapsed = false;

            if (_bottomTerminalTabActive)
            {
                EnsureBottomDockTallEnoughForSplit();
            }
            else
            {
                _bottomTerminalTabActive = false;
            }

            ApplyBottomContentLayout();
            ApplyBottomDockLayout(resetHeight: true);
            UpdateBottomTabButtonStyles();
            UpdateBottomCollapseButtonUi();
        }

        private void HideDeployLogsAfterDeploy()
        {
            if (!_logsStripAutoShownForDeploy)
            {
                return;
            }

            _logsStripAutoShownForDeploy = false;
            _logsStripVisible = false;

            // Keep terminal open if it was the active workspace.
            if (!_bottomTerminalTabActive)
            {
                // Was logs-only during deploy: collapse strip to tab bar after finish.
                _isBottomDockCollapsed = true;
            }

            ApplyBottomContentLayout();
            ApplyBottomDockLayout(resetHeight: true);
            UpdateBottomTabButtonStyles();
            UpdateBottomCollapseButtonUi();
        }

        private void EnsureBottomDockTallEnoughForSplit()
        {
            var minSplit = Math.Max(GetPreferredTerminalHeight(), ActualHeight > 0 ? ActualHeight * 0.42 : 320);
            if (_bottomDockLastHeight.Value < minSplit)
            {
                _bottomDockLastHeight = new GridLength(minSplit);
            }
        }

        private void ApplyBottomContentLayout()
        {
            var showTerminal = _bottomTerminalTabActive;
            var showLogs = !_bottomTerminalTabActive || _logsStripVisible || isDeploying;

            if (showLogs && showTerminal)
            {
                DeployLogsRow.Height = new GridLength(1, GridUnitType.Star);
                DeployTerminalRow.Height = new GridLength(2, GridUnitType.Star);
                BottomLogsPanel.Visibility = Visibility.Visible;
                BottomTerminalPanel.Visibility = Visibility.Visible;
            }
            else if (showLogs)
            {
                DeployLogsRow.Height = new GridLength(1, GridUnitType.Star);
                DeployTerminalRow.Height = new GridLength(0);
                BottomLogsPanel.Visibility = Visibility.Visible;
                BottomTerminalPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                DeployLogsRow.Height = new GridLength(0);
                DeployTerminalRow.Height = new GridLength(1, GridUnitType.Star);
                BottomLogsPanel.Visibility = Visibility.Collapsed;
                BottomTerminalPanel.Visibility = Visibility.Visible;
            }

            ClearLogsButton.Visibility = showLogs ? Visibility.Visible : Visibility.Collapsed;
        }

        private double GetPreferredTerminalHeight()
        {
            var pageHeight = ActualHeight > 0 ? ActualHeight : 800;
            return Math.Max(BottomDockMinExpandedHeight, pageHeight * BottomDockTerminalHeightRatio);
        }

        private void UpdateBottomTabButtonStyles()
        {
            var logsFocused = !_bottomTerminalTabActive && _logsStripVisible && !_isBottomDockCollapsed;
            BottomLogsTabButton.Style = (Style)FindResource(logsFocused
                ? "DeployDockTabButtonActiveStyle"
                : "DeployDockTabButtonStyle");

            RebuildTerminalSessionTabs();
            UpdateBottomTerminalRailUi();
        }

        private void UpdateBottomTerminalRailUi()
        {
            var warningSurface = FindResource("Status.WarningSurface") as System.Windows.Media.Brush
                ?? System.Windows.Media.Brushes.Transparent;
            var transparent = System.Windows.Media.Brushes.Transparent;

            if (OpenBottomTerminalButton != null)
            {
                var terminalOpen = _bottomTerminalTabActive && !_isBottomDockCollapsed;
                OpenBottomTerminalButton.Opacity = terminalOpen ? 1.0 : 0.75;
                OpenBottomTerminalButton.Background = terminalOpen ? warningSurface : transparent;
                OpenBottomTerminalButton.ToolTip = terminalOpen
                    ? "Hide Terminal"
                    : "Terminal";
            }

            if (OpenBottomLogsButton != null)
            {
                var logsOpen = (!_bottomTerminalTabActive || _logsStripVisible || isDeploying) && !_isBottomDockCollapsed;
                OpenBottomLogsButton.Opacity = logsOpen ? 1.0 : 0.75;
                OpenBottomLogsButton.Background = logsOpen && !_bottomTerminalTabActive ? warningSurface : transparent;
                OpenBottomLogsButton.ToolTip = logsOpen && !_bottomTerminalTabActive
                    ? "Hide Deploy Logs"
                    : "Deploy Logs";
            }
        }

        private void UpdateBottomCollapseButtonUi()
        {
            if (BottomCollapseButton == null)
            {
                return;
            }

            BottomCollapseButton.Content = _isBottomDockCollapsed ? "⬆" : "−";
            BottomCollapseButton.ToolTip = _isBottomDockCollapsed ? "Expand bottom panel" : "Collapse bottom panel";
        }

        private void ApplyBottomDockLayout(bool resetHeight = true)
        {
            if (BottomDockShell == null)
            {
                return;
            }

            BottomDockShell.Visibility = Visibility.Visible;
            BottomDockResizeGrip.Visibility = _isBottomDockCollapsed ? Visibility.Collapsed : Visibility.Visible;

            if (_isBottomDockCollapsed)
            {
                BottomDockShell.Height = BottomDockCollapsedHeight;
                BottomDockShell.MinHeight = BottomDockCollapsedHeight;
                SyncCenterContentBottomInset(BottomDockCollapsedHeight);
                return;
            }

            BottomDockShell.MinHeight = BottomDockMinExpandedHeight;

            double height;
            if (!resetHeight && BottomDockShell.ActualHeight > 40)
            {
                height = BottomDockShell.ActualHeight;
            }
            else if (_bottomDockLastHeight.Value > 40)
            {
                height = _bottomDockLastHeight.Value;
            }
            else if (_bottomTerminalTabActive)
            {
                height = GetPreferredTerminalHeight();
            }
            else
            {
                height = Math.Max(BottomDockMinExpandedHeight, GetPreferredTerminalHeight() * 0.7);
            }

            height = ClampBottomDockHeight(height);
            BottomDockShell.Height = height;
            _bottomDockLastHeight = new GridLength(height);
            SyncCenterContentBottomInset(height);
        }

        private double ClampBottomDockHeight(double height)
        {
            var max = ActualHeight > 0
                ? Math.Max(BottomDockMinExpandedHeight, ActualHeight - CenterContentMinHeight)
                : 900;
            return Math.Max(BottomDockMinExpandedHeight, Math.Min(height, max));
        }

        private void SyncCenterContentBottomInset(double dockHeight)
        {
            if (CenterContentHost != null)
            {
                CenterContentHost.Margin = new Thickness(0, 0, 0, Math.Max(0, dockHeight));
            }
        }

        private void CaptureBottomDockHeight()
        {
            if (_isBottomDockCollapsed || BottomDockShell == null)
            {
                return;
            }

            var height = BottomDockShell.ActualHeight > 40 ? BottomDockShell.ActualHeight : BottomDockShell.Height;
            if (height > 40)
            {
                _bottomDockLastHeight = new GridLength(height);
            }
        }

        private void BottomDockResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_isBottomDockCollapsed)
            {
                return;
            }

            _isResizingBottomDock = true;
            _bottomDockResizeStartY = e.GetPosition(DeployShellRoot).Y;
            _bottomDockResizeStartHeight = BottomDockShell.ActualHeight > 0
                ? BottomDockShell.ActualHeight
                : BottomDockShell.Height;
            BottomDockResizeGrip.CaptureMouse();
            e.Handled = true;
        }

        private void BottomDockResizeGrip_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_isResizingBottomDock || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            var currentY = e.GetPosition(DeployShellRoot).Y;
            var next = ClampBottomDockHeight(_bottomDockResizeStartHeight + (_bottomDockResizeStartY - currentY));
            BottomDockShell.Height = next;
            _bottomDockLastHeight = new GridLength(next);
            SyncCenterContentBottomInset(next);
            e.Handled = true;
        }

        private void BottomDockResizeGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            EndBottomDockResize();
            e.Handled = true;
        }

        private void BottomDockResizeGrip_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e) => EndBottomDockResize();

        private void EndBottomDockResize()
        {
            if (!_isResizingBottomDock)
            {
                return;
            }

            _isResizingBottomDock = false;
            if (BottomDockResizeGrip.IsMouseCaptured)
            {
                BottomDockResizeGrip.ReleaseMouseCapture();
            }

            CaptureBottomDockHeight();
        }

        private void SetRemoteEditorOverlay(bool enable)
        {
            if (enable)
            {
                _isRemoteEditorOverlayActive = true;
                _isRemoteWorkspaceCollapsed = false;

                // Keep FTP on the right dock; only the editor panel moves to center.
                if (!ReferenceEquals(RemoteWorkspaceContainer.Child, DeployRemoteWorkspace))
                {
                    CenterEditorOverlayHost.Child = null;
                    RemoteWorkspaceContainer.Child = DeployRemoteWorkspace;
                }

                if (_remotePanelLastWidth.Value < 360)
                {
                    _remotePanelLastWidth = new GridLength(420);
                }

                ApplyRemoteDockLayout(resetWidth: true);
                DeployRemoteWorkspace.HostEditorIn(CenterEditorOverlayHost);
                CenterEditorOverlayHost.Visibility = Visibility.Visible;
                System.Windows.Controls.Panel.SetZIndex(CenterEditorOverlayHost, 40);
                ToggleRemoteWorkspaceButton.Content = "✕";
                ToggleRemoteWorkspaceButton.ToolTip = "Close editor";
                UpdateRemoteToggleButtonUi();
                return;
            }

            _isRemoteEditorOverlayActive = false;
            DeployRemoteWorkspace.RestoreEditorPanelToDock();
            CenterEditorOverlayHost.Child = null;
            CenterEditorOverlayHost.Visibility = Visibility.Collapsed;

            if (!ReferenceEquals(RemoteWorkspaceContainer.Child, DeployRemoteWorkspace))
            {
                RemoteWorkspaceContainer.Child = DeployRemoteWorkspace;
            }

            ApplyWorkspaceLayout(force: true);
        }

        private void CaptureDockSizes()
        {
            if (LeftDockColumn.Width.IsAbsolute && LeftDockColumn.Width.Value > 0)
            {
                _leftDockLastWidth = LeftDockColumn.Width;
            }

            if (DeployRemotePanelColumn.Width.IsAbsolute && DeployRemotePanelColumn.Width.Value > 0)
            {
                _remotePanelLastWidth = DeployRemotePanelColumn.Width;
            }

            CaptureBottomDockHeight();
        }

        private void ApplyWorkspaceLayout(bool force = false)
        {
            if (_isRemoteEditorOverlayActive)
            {
                // Editor in center + FTP dock on the right — keep both visible.
                _isRemoteWorkspaceCollapsed = false;
                ApplyLeftDockLayout(resetWidth: false);
                ApplyRemoteDockLayout(resetWidth: false);
                CenterEditorOverlayHost.Visibility = Visibility.Visible;
                if (CenterEditorOverlayHost.Child == null)
                {
                    DeployRemoteWorkspace.HostEditorIn(CenterEditorOverlayHost);
                }

                UpdateRemoteToggleButtonUi();
                UpdateDirectUploadToggleUi();
                return;
            }

            CaptureDockSizes();

            var mode = DetermineRemoteLayoutMode();
            var previousMode = _remoteLayoutMode;
            _isPortrait = ActualHeight > 0 && ActualWidth > 0 && ActualHeight > ActualWidth;

            if (mode == RemoteWorkspaceLayoutMode.Wide)
            {
                _compactPanelOpenedByUser = false;
            }
            else if (previousMode == RemoteWorkspaceLayoutMode.Wide && !_isRemoteEditorOverlayActive)
            {
                if (!_compactPanelOpenedByUser)
                {
                    _isRemoteWorkspaceCollapsed = true;
                    _isDirectUploadDockCollapsed = true;
                }
            }

            _remoteLayoutMode = mode;
            ShrinkSideDocksToFitIfNeeded();
            ApplyLeftDockLayout(resetWidth: force || previousMode != mode);
            ApplyBottomDockLayout(resetHeight: force);
            ApplyRemoteDockLayout(resetWidth: force || previousMode != mode);

            UpdateRemoteToggleButtonUi();
            UpdateDirectUploadToggleUi();
            UpdateBottomCollapseButtonUi();
            ApplyPortraitBranchRowTweaks();
        }

        private void ApplyPortraitBranchRowTweaks()
        {
            DeployMainColumn.MinWidth = MainColumnMinWidth;
        }

        private void InitializeDeployThemePicker()
        {
            if (DeployThemeComboBox == null)
            {
                return;
            }

            ThemeService.Instance.Initialize();
            ThemeService.Instance.ThemesChanged -= ThemeService_ThemesChanged;
            ThemeService.Instance.ThemesChanged += ThemeService_ThemesChanged;
            ThemeService.Instance.ThemeChanged -= ThemeService_ThemeChanged;
            ThemeService.Instance.ThemeChanged += ThemeService_ThemeChanged;

            _suppressDeployThemeComboChange = true;
            try
            {
                DeployThemeComboBox.ItemsSource = ThemeService.Instance.Themes.ToList();
                var saved = _configService.ResolveAppThemeId();
                var theme = ThemeService.Instance.FindTheme(saved) ?? ThemeService.Instance.Themes[0];
                DeployThemeComboBox.SelectedItem = theme;
                // Keep combo in sync with the already-applied app theme (do not re-apply unless needed).
                if (!string.Equals(ThemeService.Instance.CurrentThemeId, theme.Id, StringComparison.OrdinalIgnoreCase))
                {
                    ThemeService.Instance.ApplyTheme(theme.Id);
                }
            }
            finally
            {
                _suppressDeployThemeComboChange = false;
            }
        }

        private void ThemeService_ThemesChanged(object? sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => ThemeService_ThemesChanged(sender, e));
                return;
            }

            if (DeployThemeComboBox == null)
            {
                return;
            }

            var selectedId = (DeployThemeComboBox.SelectedItem as AppThemeInfo)?.Id
                             ?? ThemeService.Instance.CurrentThemeId;
            _suppressDeployThemeComboChange = true;
            try
            {
                DeployThemeComboBox.ItemsSource = ThemeService.Instance.Themes.ToList();
                DeployThemeComboBox.SelectedItem = ThemeService.Instance.FindTheme(selectedId)
                                                  ?? ThemeService.Instance.Themes.FirstOrDefault();
            }
            finally
            {
                _suppressDeployThemeComboChange = false;
            }
        }

        private void ThemeService_ThemeChanged(object? sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => ThemeService_ThemeChanged(sender, e));
                return;
            }

            // Refresh status badge brushes bound on DeployFileViewModel.
            if (FilesListBox != null)
            {
                var view = System.Windows.Data.CollectionViewSource.GetDefaultView(FilesListBox.ItemsSource);
                view?.Refresh();
            }
        }

        private void DeployThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressDeployThemeComboChange)
            {
                return;
            }

            var themeId = DeployThemeComboBox?.SelectedItem is AppThemeInfo info
                ? info.Id
                : DeployThemeComboBox?.SelectedValue as string;

            if (string.IsNullOrWhiteSpace(themeId))
            {
                return;
            }

            ThemeService.Instance.ApplyTheme(themeId);
            _configService.SetAppThemeId(themeId);
        }

        private void DetachDeployThemeHandlers()
        {
            ThemeService.Instance.ThemesChanged -= ThemeService_ThemesChanged;
            ThemeService.Instance.ThemeChanged -= ThemeService_ThemeChanged;
        }

        private void BranchSummaryButton_Click(object sender, RoutedEventArgs e)
        {
            _isBranchDetailsExpanded = !_isBranchDetailsExpanded;
            ApplyBranchDetailsVisibility();
        }

        private void ApplyBranchDetailsVisibility()
        {
            if (BranchDetailsPanel != null)
            {
                BranchDetailsPanel.Visibility = _isBranchDetailsExpanded
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (BranchSummaryChevron != null)
            {
                BranchSummaryChevron.Text = _isBranchDetailsExpanded ? " ▴" : " ▾";
            }
        }

        private void UpdateBranchSummaryUi()
        {
            if (BranchSummaryText == null)
            {
                return;
            }

            var source = (SourceBranchComboBox?.SelectedItem as ComboBoxItem)?.Content?.ToString();
            var target = (TargetBranchComboBox?.SelectedItem as ComboBoxItem)?.Content?.ToString();
            if (string.IsNullOrWhiteSpace(source))
            {
                source = "—";
            }

            if (string.IsNullOrWhiteSpace(target))
            {
                target = "—";
            }

            BranchSummaryText.Text = $"{source} → {target}";
        }

        private void ApplyLeftDockLayout(bool resetWidth = true)
        {
            ResetLeftDockPlacement();

            if (_isDirectUploadDockCollapsed)
            {
                ClearLeftDockColumnConstraints();
                LeftDockColumn.Width = new GridLength(0);
                LeftSplitterColumn.Width = new GridLength(0);
                LeftDockContainer.Visibility = Visibility.Collapsed;
                LeftDockSplitter.Visibility = Visibility.Collapsed;
                return;
            }

            // Prefer real column + splitter whenever the shell can fit it.
            if (CanFitLeftColumnDock())
            {
                var maxWidth = ComputeLeftDockMaxWidth(remoteOpen: !_isRemoteWorkspaceCollapsed && _remoteLayoutMode != RemoteWorkspaceLayoutMode.Narrow);
                var width = _leftDockLastWidth.Value > 0 ? _leftDockLastWidth.Value : 340;
                width = Math.Max(LeftDockMinWidth, Math.Min(width, Math.Min(640, maxWidth)));

                if (resetWidth || !LeftDockColumn.Width.IsAbsolute || LeftDockColumn.Width.Value <= 0)
                {
                    LeftDockColumn.Width = new GridLength(width);
                }
                else
                {
                    var current = LeftDockColumn.Width.Value;
                    if (current > maxWidth || current < LeftDockMinWidth)
                    {
                        LeftDockColumn.Width = new GridLength(Math.Max(LeftDockMinWidth, Math.Min(current, maxWidth)));
                    }
                }

                _leftDockLastWidth = LeftDockColumn.Width;
                LeftDockColumn.MinWidth = LeftDockMinWidth;
                LeftDockColumn.MaxWidth = Math.Max(LeftDockMinWidth, maxWidth);
                LeftSplitterColumn.Width = new GridLength(SplitterWidth);
                LeftDockContainer.Visibility = Visibility.Visible;
                LeftDockSplitter.Visibility = Visibility.Visible;
                System.Windows.Controls.Panel.SetZIndex(LeftDockContainer, 1);
                System.Windows.Controls.Panel.SetZIndex(LeftDockSplitter, 50);
                return;
            }

            // Tiny shell: left drawer overlay (never full-page).
            ClearLeftDockColumnConstraints();
            LeftDockColumn.Width = new GridLength(0);
            LeftSplitterColumn.Width = new GridLength(0);
            LeftDockSplitter.Visibility = Visibility.Collapsed;
            Grid.SetColumn(LeftDockContainer, 0);
            Grid.SetColumnSpan(LeftDockContainer, 7);
            LeftDockContainer.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            LeftDockContainer.Width = Math.Min(420, Math.Max(LeftDockMinWidth, GetEffectiveShellWidth() * 0.42));
            System.Windows.Controls.Panel.SetZIndex(LeftDockContainer, 14);
            LeftDockContainer.Visibility = Visibility.Visible;
        }

        private void ApplyRemoteDockLayout(bool resetWidth = true)
        {
            // Medium used to hide the splitter; keep a real column dock whenever it fits.
            if (_remoteLayoutMode == RemoteWorkspaceLayoutMode.Narrow)
            {
                ApplyNarrowRemoteLayout();
                return;
            }

            ApplyWideRemoteLayout(resetWidth);
        }

        private void ResetLeftDockPlacement()
        {
            Grid.SetColumn(LeftDockContainer, 1);
            Grid.SetColumnSpan(LeftDockContainer, 1);
            LeftDockContainer.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            LeftDockContainer.Width = double.NaN;
            System.Windows.Controls.Panel.SetZIndex(LeftDockContainer, 1);
        }

        private double GetEffectiveShellWidth()
        {
            var width = ActualWidth;
            if (width <= 0 && System.Windows.Application.Current?.MainWindow != null)
            {
                width = System.Windows.Application.Current.MainWindow.ActualWidth;
            }

            return width;
        }

        private bool CanFitColumnRemoteDock(bool leftOpen)
        {
            var width = GetEffectiveShellWidth();
            if (width <= 0)
            {
                return true;
            }

            var mainMin = BothSideDocksOpen() ? Math.Min(MainColumnMinWidth, 220) : MainColumnMinWidth;
            var needed = (RailWidth * 2) + mainMin + SplitterWidth + RemoteDockMinWidth;
            if (leftOpen)
            {
                needed += LeftDockMinWidth + SplitterWidth;
            }

            return width >= needed;
        }

        private bool CanFitLeftColumnDock()
        {
            var width = GetEffectiveShellWidth();
            if (width <= 0)
            {
                return true;
            }

            var mainMin = BothSideDocksOpen() ? Math.Min(MainColumnMinWidth, 220) : MainColumnMinWidth;
            var needed = (RailWidth * 2) + mainMin + LeftDockMinWidth + SplitterWidth;
            if (!_isRemoteWorkspaceCollapsed && _remoteLayoutMode != RemoteWorkspaceLayoutMode.Narrow)
            {
                needed += RemoteDockMinWidth + SplitterWidth;
            }

            return width >= needed;
        }

        private bool BothSideDocksOpen() => !_isDirectUploadDockCollapsed && !_isRemoteWorkspaceCollapsed;

        /// <summary>
        /// When both side docks are open, shrink them to minimum widths if preferred sizes no longer fit.
        /// </summary>
        private void ShrinkSideDocksToFitIfNeeded()
        {
            if (!BothSideDocksOpen())
            {
                return;
            }

            var width = GetEffectiveShellWidth();
            if (width <= 0)
            {
                return;
            }

            var mainMin = Math.Min(MainColumnMinWidth, 220);
            var availableForDocks = width - ((RailWidth * 2) + mainMin + (SplitterWidth * 2));
            if (availableForDocks < LeftDockMinWidth + RemoteDockMinWidth)
            {
                // Still pin to mins — center may scroll; both docks stay open together.
                _leftDockLastWidth = new GridLength(LeftDockMinWidth);
                _remotePanelLastWidth = new GridLength(RemoteDockMinWidth);
                return;
            }

            var leftWant = _leftDockLastWidth.Value > 0 ? _leftDockLastWidth.Value : 340;
            var remoteWant = _remotePanelLastWidth.Value > 0 ? _remotePanelLastWidth.Value : 420;
            if (leftWant + remoteWant <= availableForDocks)
            {
                return;
            }

            // Preferred widths are too wide: snap both to the configured minimums.
            _leftDockLastWidth = new GridLength(LeftDockMinWidth);
            _remotePanelLastWidth = new GridLength(RemoteDockMinWidth);
        }

        private double ComputeRemoteDockMaxWidth(bool leftOpen)
        {
            var width = GetEffectiveShellWidth();
            if (width <= 0)
            {
                return 720;
            }

            var reserved = (RailWidth * 2) + MainColumnMinWidth + SplitterWidth;
            if (leftOpen)
            {
                var leftWidth = LeftDockColumn.Width.IsAbsolute && LeftDockColumn.Width.Value > 0
                    ? LeftDockColumn.Width.Value
                    : LeftDockMinWidth;
                reserved += leftWidth + SplitterWidth;
            }

            return Math.Max(RemoteDockMinWidth, width - reserved);
        }

        private double ComputeLeftDockMaxWidth(bool remoteOpen)
        {
            var width = GetEffectiveShellWidth();
            if (width <= 0)
            {
                return 640;
            }

            var reserved = (RailWidth * 2) + MainColumnMinWidth + SplitterWidth;
            if (remoteOpen)
            {
                var remoteWidth = DeployRemotePanelColumn.Width.IsAbsolute && DeployRemotePanelColumn.Width.Value > 0
                    ? DeployRemotePanelColumn.Width.Value
                    : RemoteDockMinWidth;
                reserved += remoteWidth + SplitterWidth;
            }

            return Math.Max(LeftDockMinWidth, width - reserved);
        }

        private void ClearLeftDockColumnConstraints()
        {
            LeftDockColumn.MinWidth = 0;
            LeftDockColumn.MaxWidth = double.PositiveInfinity;
        }

        private void ClearRemoteDockColumnConstraints()
        {
            DeployRemotePanelColumn.MinWidth = 0;
            DeployRemotePanelColumn.MaxWidth = double.PositiveInfinity;
        }

        private void ClampOpenDockWidthsToAvailableSpace()
        {
            if (!_isDirectUploadDockCollapsed &&
                LeftDockColumn.Width.IsAbsolute &&
                LeftDockColumn.Width.Value > 0 &&
                LeftDockSplitter.Visibility == Visibility.Visible)
            {
                var maxLeft = ComputeLeftDockMaxWidth(remoteOpen: !_isRemoteWorkspaceCollapsed && RemoteDockSplitter.Visibility == Visibility.Visible);
                LeftDockColumn.MaxWidth = Math.Max(LeftDockMinWidth, maxLeft);
                if (LeftDockColumn.Width.Value > maxLeft)
                {
                    LeftDockColumn.Width = new GridLength(Math.Max(LeftDockMinWidth, maxLeft));
                    _leftDockLastWidth = LeftDockColumn.Width;
                }
            }

            if (!_isRemoteWorkspaceCollapsed &&
                DeployRemotePanelColumn.Width.IsAbsolute &&
                DeployRemotePanelColumn.Width.Value > 0 &&
                RemoteDockSplitter.Visibility == Visibility.Visible)
            {
                var maxRemote = ComputeRemoteDockMaxWidth(leftOpen: !_isDirectUploadDockCollapsed && LeftDockSplitter.Visibility == Visibility.Visible);
                DeployRemotePanelColumn.MaxWidth = Math.Max(RemoteDockMinWidth, maxRemote);
                if (DeployRemotePanelColumn.Width.Value > maxRemote)
                {
                    DeployRemotePanelColumn.Width = new GridLength(Math.Max(RemoteDockMinWidth, maxRemote));
                    _remotePanelLastWidth = DeployRemotePanelColumn.Width;
                }
            }
        }

        private RemoteWorkspaceLayoutMode DetermineRemoteLayoutMode()
        {
            var width = GetEffectiveShellWidth();
            if (width <= 0)
            {
                return _remoteLayoutMode;
            }

            // Column dock + visible splitter whenever the budget fits (covers typical "medium" sizes).
            // Also keep column mode when both docks can share the shell at minimum widths.
            if (CanFitColumnRemoteDock(leftOpen: false) || CanFitColumnRemoteDock(leftOpen: true))
            {
                return width >= RemoteWideBreakpoint
                    ? RemoteWorkspaceLayoutMode.Wide
                    : RemoteWorkspaceLayoutMode.Medium;
            }

            return RemoteWorkspaceLayoutMode.Narrow;
        }

        private void ApplyWideRemoteLayout(bool resetWidth = true)
        {
            ResetRemoteDockPlacement();
            System.Windows.Controls.Panel.SetZIndex(RemoteWorkspaceContainer, 1);

            if (_isRemoteWorkspaceCollapsed)
            {
                ClearRemoteDockColumnConstraints();
                DeployRemotePanelColumn.Width = new GridLength(0);
                DeployRemoteSplitterColumn.Width = new GridLength(0);
                RemoteDockSplitter.Visibility = Visibility.Collapsed;
                RemoteWorkspaceContainer.Visibility = Visibility.Collapsed;
                return;
            }

            var maxWidth = ComputeRemoteDockMaxWidth(leftOpen: !_isDirectUploadDockCollapsed);
            var width = _remotePanelLastWidth.Value > 0 ? _remotePanelLastWidth.Value : 420;
            width = Math.Max(RemoteDockMinWidth, Math.Min(width, Math.Min(720, maxWidth)));

            if (resetWidth || !DeployRemotePanelColumn.Width.IsAbsolute || DeployRemotePanelColumn.Width.Value <= 0)
            {
                DeployRemotePanelColumn.Width = new GridLength(width);
            }
            else if (DeployRemotePanelColumn.Width.Value > maxWidth || DeployRemotePanelColumn.Width.Value < RemoteDockMinWidth)
            {
                DeployRemotePanelColumn.Width = new GridLength(Math.Max(RemoteDockMinWidth, Math.Min(DeployRemotePanelColumn.Width.Value, maxWidth)));
            }

            _remotePanelLastWidth = DeployRemotePanelColumn.Width;
            DeployRemotePanelColumn.MinWidth = RemoteDockMinWidth;
            DeployRemotePanelColumn.MaxWidth = Math.Max(RemoteDockMinWidth, maxWidth);
            DeployRemoteSplitterColumn.Width = new GridLength(SplitterWidth);
            RemoteDockSplitter.Visibility = Visibility.Visible;
            RemoteWorkspaceContainer.Visibility = Visibility.Visible;
            System.Windows.Controls.Panel.SetZIndex(RemoteDockSplitter, 50);
        }

        private void ApplyNarrowRemoteLayout()
        {
            ClearRemoteDockColumnConstraints();
            DeployRemotePanelColumn.Width = new GridLength(0);
            DeployRemoteSplitterColumn.Width = new GridLength(0);
            RemoteDockSplitter.Visibility = Visibility.Collapsed;

            if (_isRemoteWorkspaceCollapsed)
            {
                RemoteWorkspaceContainer.Visibility = Visibility.Collapsed;
                ResetRemoteDockPlacement();
                return;
            }

            // Drawer overlay — keep ~15% of the page visible so it never feels full-screen with no handle.
            var shellWidth = GetEffectiveShellWidth();
            var overlayWidth = Math.Min(shellWidth * 0.85, Math.Max(RemoteDockMinWidth, shellWidth - (RailWidth * 2) - 120));

            Grid.SetColumn(RemoteWorkspaceContainer, 0);
            Grid.SetColumnSpan(RemoteWorkspaceContainer, 7);
            RemoteWorkspaceContainer.Margin = new Thickness(0, 0, RailWidth, 0);
            RemoteWorkspaceContainer.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            RemoteWorkspaceContainer.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
            RemoteWorkspaceContainer.Width = overlayWidth;
            System.Windows.Controls.Panel.SetZIndex(RemoteWorkspaceContainer, 16);
            RemoteWorkspaceContainer.Visibility = Visibility.Visible;
        }

        private void ResetRemoteDockPlacement()
        {
            Grid.SetColumn(RemoteWorkspaceContainer, 5);
            Grid.SetColumnSpan(RemoteWorkspaceContainer, 1);
            RemoteWorkspaceContainer.Margin = new Thickness(0);
            RemoteWorkspaceContainer.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            RemoteWorkspaceContainer.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
            RemoteWorkspaceContainer.Width = double.NaN;
        }

        private void UpdateRemoteToggleButtonUi()
        {
            if (_isRemoteEditorOverlayActive)
            {
                ToggleRemoteWorkspaceButton.Content = "✕";
                ToggleRemoteWorkspaceButton.ToolTip = "Close editor";
                return;
            }

            ToggleRemoteWorkspaceButton.Content = "🗂";
            ToggleRemoteWorkspaceButton.ToolTip = _isRemoteWorkspaceCollapsed
                ? "Show FTP / Remote Host"
                : "Hide FTP / Remote Host";
            ToggleRemoteWorkspaceButton.Opacity = _isRemoteWorkspaceCollapsed ? 0.75 : 1.0;
        }

        private void UpdateDirectUploadToggleUi()
        {
            ToggleDirectUploadDockButton.Opacity = _isDirectUploadDockCollapsed ? 0.75 : 1.0;
            ToggleDirectUploadDockButton.ToolTip = _isDirectUploadDockCollapsed
                ? "Show Direct Upload"
                : "Hide Direct Upload";
        }

        private async void LoadGitData(bool includeExpensiveOperations = true, bool refreshBranches = true)
        {
            if (_isRefreshingGit) return;

            using var perfScope = PerformanceSampler.Instance.BeginScope(
                "deploy",
                "load-git-data",
                includeExpensiveOperations ? "full" : "light");

            _isRefreshingGit = true;
            _isLoaded = false;
            try
            {
                if (!_gitService.IsGitRepository())
                {
                    StatusText.Text = "⚠️ Git repository not found (Initialize Git in Settings)";
                    StatusText.Foreground = GetThemeBrush("Status.Warning", System.Windows.Media.Brushes.Orange);
                    DisableAllButtons();
                    return;
                }

                // Load Project Config
                var globalConfig = _configService.LoadGlobalConfig();
                if (!string.IsNullOrEmpty(globalConfig.LastProjectPath))
                {
                    _projectConfig = _configService.LoadProjectConfig(globalConfig.LastProjectPath);
                }
                if (!string.Equals(_remoteWorkspaceProjectPath, _projectConfig.LocalProjectPath, StringComparison.OrdinalIgnoreCase))
                {
                    _remoteWorkspaceProjectPath = _projectConfig.LocalProjectPath ?? string.Empty;
                    DeployRemoteWorkspace?.Initialize(_projectConfig);
                }

                // Check changes & commits
                var previousUncommittedCount = _cachedUncommittedCount;
                var uncommittedCount = await _gitService.GetUncommittedCountAsync();
                var totalCommits = await _gitService.GetTotalCommitsAsync();
                _cachedUncommittedCount = uncommittedCount;
                _cachedTotalCommits = totalCommits;

                if (refreshBranches || ShouldRefreshBranchSelectors())
                {
                    var branches = await _gitService.GetBranchesAsync();
                    var current = await _gitService.GetCurrentBranchAsync();
                    PopulateBranchSelectors(branches, current);
                    _lastBranchRefreshUtc = AppTimeService.UtcNow;
                }

                await RefreshBranchStatusAsync();

                // Determine Button State
                UpdateActionButtonState(_cachedUncommittedCount, _cachedTotalCommits);

                SourceBranchComboBox.IsEnabled = true;
                TargetBranchComboBox.IsEnabled = true;
                if (ShouldKeepCompareResultsVisible())
                {
                    FilesListBox.ItemsSource = _fileViewModels;
                    ConfigureCompareSyncButton();
                    DeployButton.Visibility = Visibility.Visible;
                    DeployButton.IsEnabled = _fileViewModels.Any(x => x.IsSelected);
                }
                else
                {
                    DeployButton.IsEnabled = false; // Initially disabled
                    DeployButton.Visibility = Visibility.Collapsed;
                    if (_compareResultActive)
                    {
                        ClearCompareContext(clearList: true);
                    }
                }

                if (!ShouldKeepCompareResultsVisible() && StatusText.Text != $"⚠️ You have {uncommittedCount} uncommitted changes!")
                {
                    StatusText.Text = "Ready...";
                    StatusText.Foreground = GetThemeBrush("Text.Muted", System.Windows.Media.Brushes.LightGray);
                }
                
                // Log only when uncommitted count changes to avoid spam.
                if (uncommittedCount > 0 && uncommittedCount != previousUncommittedCount)
                {
                    AddLog($"[DEBUG] Found {uncommittedCount} uncommitted changes.");
                }

                // Expensive compare diff check is intentionally limited to full refreshes.
                if (includeExpensiveOperations &&
                    SourceBranchComboBox.SelectedItem is ComboBoxItem src &&
                    TargetBranchComboBox.SelectedItem is ComboBoxItem tgt)
                {
                    string? s = src.Content?.ToString();
                    string? t = tgt.Content?.ToString();
                    
                    if (uncommittedCount == 0 && !string.IsNullOrEmpty(s) && !string.IsNullOrEmpty(t) && s != t)
                    {
                        var diff = await _gitService.GetDiffAsync(s, t);
                        if (diff.Count == 0)
                        {
                            SetActionButton("synced", "✅ SYNCED", "Status.Success", false);
                            StatusText.Text = "Branches are synchronized.";
                            StatusText.Foreground = GetThemeBrush("Status.Success", System.Windows.Media.Brushes.LightGreen);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                perfScope.Fail(ex);
                AddLog($"❌ Error loading Git data: {ex.Message}");
            }
            finally
            {
                _isLoaded = true;
                _isRefreshingGit = false;
            }
        }

        private bool ShouldRefreshBranchSelectors()
        {
            if (SourceBranchComboBox.Items.Count == 0 || TargetBranchComboBox.Items.Count == 0)
            {
                return true;
            }

            return (AppTimeService.UtcNow - _lastBranchRefreshUtc) >= BranchRefreshInterval;
        }

        private void PopulateBranchSelectors(IReadOnlyList<string> branches, string currentBranch)
        {
            var selectedSource = (SourceBranchComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
            var selectedTarget = (TargetBranchComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
            var preferredSource = !string.IsNullOrWhiteSpace(selectedSource)
                ? selectedSource
                : (!string.IsNullOrWhiteSpace(_projectConfig.DefaultSourceBranch) ? _projectConfig.DefaultSourceBranch : currentBranch);
            var preferredTarget = !string.IsNullOrWhiteSpace(selectedTarget)
                ? selectedTarget
                : _projectConfig.DefaultTargetBranch;

            SourceBranchComboBox.Items.Clear();
            TargetBranchComboBox.Items.Clear();

            foreach (var branch in branches)
            {
                SourceBranchComboBox.Items.Add(new ComboBoxItem
                {
                    Content = branch,
                    IsSelected = string.Equals(branch, preferredSource, StringComparison.OrdinalIgnoreCase)
                });

                TargetBranchComboBox.Items.Add(new ComboBoxItem
                {
                    Content = branch,
                    IsSelected = string.Equals(branch, preferredTarget, StringComparison.OrdinalIgnoreCase)
                });
            }

            if (TargetBranchComboBox.SelectedIndex == -1)
            {
                SelectFallbackTargetBranch(branches.ToList());
            }

            if (SourceBranchComboBox.SelectedIndex == -1 && SourceBranchComboBox.Items.Count > 0)
            {
                for (int i = 0; i < SourceBranchComboBox.Items.Count; i++)
                {
                    if ((SourceBranchComboBox.Items[i] as ComboBoxItem)?.Content?.ToString() == currentBranch)
                    {
                        SourceBranchComboBox.SelectedIndex = i;
                        break;
                    }
                }

                if (SourceBranchComboBox.SelectedIndex == -1)
                {
                    SourceBranchComboBox.SelectedIndex = 0;
                }
            }

            UpdateBranchSummaryUi();
        }

        private void DisableAllButtons()
        {
            SourceBranchComboBox.Items.Clear();
            TargetBranchComboBox.Items.Clear();
            SourceBranchComboBox.IsEnabled = false;
            TargetBranchComboBox.IsEnabled = false;
            UpdateBranchSummaryUi();
            if (ActionButton != null) ActionButton.IsEnabled = false;
            if (DeployButton != null)
            {
                DeployButton.IsEnabled = false;
                DeployButton.Visibility = Visibility.Collapsed;
            }
            if (DeployPushBadge != null) DeployPushBadge.Visibility = Visibility.Collapsed;
        }

        private void SelectFallbackTargetBranch(List<string> branches)
        {
            int targetIndex = -1;
            targetIndex = branches.IndexOf("production");
            if (targetIndex == -1) targetIndex = branches.IndexOf("master");
            if (targetIndex == -1) targetIndex = branches.IndexOf("main");
            
            if (targetIndex != -1 && TargetBranchComboBox.Items.Count > targetIndex)
            {
                TargetBranchComboBox.SelectedIndex = targetIndex;
            }
            else if (TargetBranchComboBox.Items.Count > 0)
            {
                TargetBranchComboBox.SelectedIndex = 0;
            }
        }

        private async Task RefreshBranchStatusAsync()
        {
            if (!_gitService.IsGitRepository())
            {
                _branchStatus = new BranchStatusInfo();
                UpdatePushBadgeUi();
                return;
            }

            _branchStatus = await _gitService.GetBranchStatusAsync();
            UpdatePushBadgeUi();
        }

        private void BranchComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateBranchSummaryUi();
            if (!_isLoaded) return;

            ClearCompareContext(clearList: true);
            UpdateActionButtonState();
        }

        private async void RollbackButton_Click(object sender, RoutedEventArgs e)
        {
            if (isDeploying)
            {
                ModernMessageBox.Show("Wait for the current deploy to finish before rolling back.", "Rollback", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                RollbackButton.IsEnabled = false;
                StatusText.Text = "Opening rollback preview…";
                StatusText.Foreground = GetThemeBrush("Text.Muted", System.Windows.Media.Brushes.LightGray);

                var canRedeploy = GetActiveConnectionProfile() != null;
                var preview = new RollbackPreviewWindow(_gitService, canRedeploy);
                WindowOwnerService.ShowDialogOwned(preview, this);
                if (!preview.Confirmed || preview.Entry?.Commit == null)
                {
                    StatusText.Text = "Rollback cancelled.";
                    return;
                }

                var entry = preview.Entry;
                var filesForRedeploy = new List<CommitFileChangeInfo>();

                if (preview.Scope == RollbackScope.SingleFile)
                {
                    if (preview.SelectedFile == null)
                    {
                        StatusText.Text = "Rollback cancelled (no file selected).";
                        return;
                    }

                    var filePath = string.IsNullOrWhiteSpace(preview.SelectedFile.Path)
                        ? preview.SelectedFile.OldPath
                        : preview.SelectedFile.Path;
                    AddLog($"↩ Rolling back file {filePath} from {entry.Commit.ShortHash}");
                    await _gitService.RevertFileFromCommitAsync(entry.Commit.FullHash, preview.SelectedFile);
                    AddLog("✅ File rollback commit created.");
                    filesForRedeploy.Add(preview.SelectedFile);
                }
                else
                {
                    AddLog($"↩ Rolling back commit {entry.Commit.ShortHash}: {entry.Commit.Message}");
                    await _gitService.RevertCommitAsync(entry.Commit.FullHash);
                    AddLog("✅ Git revert completed.");
                    filesForRedeploy.AddRange(entry.ChangedFiles ?? new List<CommitFileChangeInfo>());
                }

                await SyncLocalBranchesIfNeededAsync();
                var pushOk = await PushToGithub();
                AddLog(pushOk
                    ? "✅ Rollback pushed to remote."
                    : "⚠️ Rollback committed locally; push had issues.");

                if (preview.RedeployRequested)
                {
                    await RedeployRollbackFilesAsync(filesForRedeploy, pushOk);
                }
                else
                {
                    StatusText.Text = pushOk
                        ? "Rollback completed (Git + push)."
                        : "Rollback completed locally; push had issues.";
                    StatusText.Foreground = GetThemeBrush(
                        pushOk ? "Status.Success" : "Status.Warning",
                        pushOk ? System.Windows.Media.Brushes.LightGreen : System.Windows.Media.Brushes.Orange);
                }

                LoadGitData(includeExpensiveOperations: true, refreshBranches: true);
            }
            catch (Exception ex)
            {
                AddLog($"❌ Rollback failed: {ex.Message}");
                ModernMessageBox.Show($"Rollback failed:\n{ex.Message}", "Rollback", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Rollback failed.";
                StatusText.Foreground = GetThemeBrush("Status.Error", System.Windows.Media.Brushes.OrangeRed);
            }
            finally
            {
                RollbackButton.IsEnabled = true;
            }
        }

        private async Task RedeployRollbackFilesAsync(IReadOnlyList<CommitFileChangeInfo> changedFiles, bool pushOk)
        {
            var filesToDeploy = changedFiles
                .Where(f => f.Type != ChangeType.Added)
                .Select(f => new FileChange
                {
                    Name = string.IsNullOrWhiteSpace(f.Path) ? (f.OldPath ?? string.Empty) : f.Path,
                    Type = f.Type == ChangeType.Deleted ? ChangeType.Added : ChangeType.Modified
                })
                .Where(f => !string.IsNullOrWhiteSpace(f.Name))
                .ToList();

            var restoredFromDelete = changedFiles
                .Where(f => f.Type == ChangeType.Deleted)
                .Select(f => new FileChange
                {
                    Name = string.IsNullOrWhiteSpace(f.OldPath) ? f.Path : f.OldPath!,
                    Type = ChangeType.Added
                })
                .Where(f => !string.IsNullOrWhiteSpace(f.Name));

            filesToDeploy = filesToDeploy
                .Concat(restoredFromDelete)
                .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (filesToDeploy.Count == 0)
            {
                AddLog("ℹ️ No FTP-redeployable files after rollback (likely only new files were undone).");
                StatusText.Text = "Rollback completed in Git. Nothing to redeploy.";
                StatusText.Foreground = GetThemeBrush("Status.Success", System.Windows.Media.Brushes.LightGreen);
                return;
            }

            AddLog($"🚀 Redeploying {filesToDeploy.Count} rolled-back file(s) to FTP…");
            var deployResult = await StartDeployProcess(filesToDeploy, isAutoFlow: true, runGitPostSteps: false);
            if (deployResult.HasFatalError || deployResult.IsCompleteFailure)
            {
                AddLog("⛔ FTP redeploy failed after rollback. Git rollback is already done.");
                StatusText.Text = "Rollback done in Git; FTP redeploy failed.";
                StatusText.Foreground = GetThemeBrush("Status.Warning", System.Windows.Media.Brushes.Orange);
                return;
            }

            AddLog("✅ Rollback redeploy finished.");
            StatusText.Text = pushOk
                ? "Rollback completed (Git + FTP)."
                : "Rollback completed locally + FTP; remote push had issues.";
            StatusText.Foreground = GetThemeBrush("Status.Success", System.Windows.Media.Brushes.LightGreen);
        }

        private void UpdateActionButtonState(int uncommittedCount = -1, int totalCommits = -1)
        {
            if (ActionButton == null) return;

            if (uncommittedCount == -1) uncommittedCount = _cachedUncommittedCount;
            if (totalCommits == -1) totalCommits = _cachedTotalCommits;

            if (uncommittedCount > 0)
            {
                // Primary action: review list first, then one-click send
                SetActionButton("commit", "📝 COMMIT && REVIEW", "Accent.Secondary", true);
                string pendingText = uncommittedCount >= 0 ? uncommittedCount.ToString() : "some";
                StatusText.Text = SkipReviewCheckBox?.IsChecked == true
                    ? $"You have {pendingText} pending file(s). Skip review is on — Deploy → Commit + Push runs immediately."
                    : $"You have {pendingText} pending file(s). Review first, then deploy -> commit -> push.";
                StatusText.Foreground = GetThemeBrush("Status.Warning", System.Windows.Media.Brushes.Orange);
                return;
            }

            if (totalCommits == 0)
            {
                SetActionButton("idle", "⏸ NOTHING TO COMMIT", "Surface.Input", false);
                StatusText.Text = "No commit available yet.";
                StatusText.Foreground = GetThemeBrush("Text.Muted", System.Windows.Media.Brushes.Gray);
                return;
            }

            // If source/target selection is invalid
            if (SourceBranchComboBox.SelectedItem is not ComboBoxItem sourceItem ||
                TargetBranchComboBox.SelectedItem is not ComboBoxItem targetItem)
            {
                SetBranchSelectionRequiredState();
                return;
            }

            string? source = sourceItem.Content?.ToString();
            string? target = targetItem.Content?.ToString();

            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            {
                SetBranchSelectionRequiredState();
                return;
            }

            bool sameBranch = (source == target);

            // Priority 2 & 3: Push Pending or Sync
            if (sameBranch)
            {
                bool remoteReady = _branchStatus != null && _branchStatus.HasRemote;
                
                if (!remoteReady)
                {
                    bool hasSavedRemote = !string.IsNullOrWhiteSpace(_projectConfig?.GitRemoteUrl);
                    if (hasSavedRemote)
                    {
                        SetActionButton("push", "☁️ CONNECT + PUSH", "Surface.Shell", true);
                        StatusText.Text = "Remote origin is missing in this repo. App will restore it from saved settings.";
                        StatusText.Foreground = GetThemeBrush("Status.Warning", System.Windows.Media.Brushes.Orange);
                    }
                    else
                    {
                        SetActionButton("push", "☁️ PUSH TO GITHUB", "Surface.Input", false);
                        StatusText.Text = "No remote repository configured. Set Remote URL in Settings.";
                        StatusText.Foreground = GetThemeBrush("Text.Muted", System.Windows.Media.Brushes.Gray);
                    }
                    return;
                }

                // Remote exists
                int ahead = _branchStatus != null ? _branchStatus.AheadCount : 0;
                
                if (ahead > 0)
                {
                    string pushLabel = $"☁️ PUSH ({ahead})";
                    SetActionButton("push", pushLabel, "Surface.Shell", true);
                    StatusText.Text = "You have commits pending push.";
                    StatusText.Foreground = GetThemeBrush("Status.Warning", System.Windows.Media.Brushes.Orange);
                }
                else
                {
                    SetActionButton("push", "✅ NOTHING TO PUSH", "Status.Success", false); 
                    StatusText.Text = "Branch is up to date with remote.";
                    StatusText.Foreground = GetThemeBrush("Text.Muted", System.Windows.Media.Brushes.LightGray);
                }
            }
            else
            {
                // Different branches (Source != Target)
                if (ActionButton.Tag?.ToString() == "synced")
                {
                     return;
                }

                SetActionButton("compare", "🔍 COMPARE", "Status.Warning");
                StatusText.Text = "Ready to compare branches...";
                StatusText.Foreground = GetThemeBrush("Text.Muted", System.Windows.Media.Brushes.LightGray);
            }
        }

        private void SetBranchSelectionRequiredState()
        {
            if (ActionButton == null) return;

            ActionButton.Content = "Select Branches";
            ActionButton.Tag = null;
            ActionButton.IsEnabled = false;
            ActionButton.Background = ResolveBrush("Surface.Input", "#444444");

            StatusText.Text = "Select source and target branches to continue.";
            StatusText.Foreground = GetThemeBrush("Status.Warning", System.Windows.Media.Brushes.Orange);
        }

        private void UpdatePushBadgeUi()
        {
            if (DeployPushBadge == null || DeployPushBadgeText == null) return;

            if (_branchStatus != null && _branchStatus.HasRemote && _branchStatus.AheadCount > 0)
            {
                DeployPushBadge.Visibility = Visibility.Visible;
                DeployPushBadgeText.Text = $"Push pending: {_branchStatus.AheadCount} commit(s)";
            }
            else
            {
                DeployPushBadge.Visibility = Visibility.Collapsed;
            }
        }

        private void SetupAutoRefreshTimer()
        {
            _autoRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10) // Refresh every 10 seconds for better UX
            };
            _autoRefreshTimer.Tick += AutoRefreshTimer_Tick;
            _autoRefreshTimer.Start();
        }

        private void AutoRefreshTimer_Tick(object? sender, EventArgs e)
        {
            _autoRefreshTickCount++;
            bool runFullRefresh = _autoRefreshTickCount % FullRefreshTickInterval == 0;
            LoadGitData(includeExpensiveOperations: runFullRefresh, refreshBranches: runFullRefresh);
        }

        private System.Windows.Media.Brush GetThemeBrush(string resourceKey, System.Windows.Media.Brush fallback)
        {
            if (string.IsNullOrWhiteSpace(resourceKey))
            {
                return fallback;
            }

            return System.Windows.Application.Current?.TryFindResource(resourceKey) as System.Windows.Media.Brush ?? fallback;
        }

        private System.Windows.Media.Brush ResolveBrush(string resourceKeyOrHex, string fallbackHex)
        {
            if (!string.IsNullOrWhiteSpace(resourceKeyOrHex))
            {
                if (System.Windows.Application.Current?.TryFindResource(resourceKeyOrHex) is System.Windows.Media.Brush themedBrush)
                {
                    return themedBrush;
                }

                if (resourceKeyOrHex.StartsWith("#", StringComparison.Ordinal))
                {
                    try
                    {
                        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(resourceKeyOrHex);
                        return new SolidColorBrush(color);
                    }
                    catch
                    {
                        // Ignore and fallback.
                    }
                }
            }

            try
            {
                var fallbackColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(fallbackHex);
                return new SolidColorBrush(fallbackColor);
            }
            catch
            {
                return System.Windows.Media.Brushes.Gray;
            }
        }

        private void SetActionButton(string tag, string content, string colorResourceOrHex, bool isEnabled = true)
        {
            if (ActionButton == null) return;
            
            ActionButton.Tag = tag;
            ActionButton.Background = ResolveBrush(colorResourceOrHex, "#444444");
            ActionButton.IsEnabled = isEnabled;

            var isCommitAction = string.Equals(tag, "commit", StringComparison.OrdinalIgnoreCase);
            if (SkipReviewCheckBox != null)
            {
                SkipReviewCheckBox.Visibility = isCommitAction ? Visibility.Visible : Visibility.Collapsed;
            }

            if (isCommitAction && SkipReviewCheckBox?.IsChecked == true)
            {
                ActionButton.Content = "⚡ DEPLOY → COMMIT + PUSH";
            }
            else
            {
                ActionButton.Content = content;
            }
        }

        private void SkipReviewCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (ActionButton?.Tag is not string tag
                || !string.Equals(tag, "commit", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ActionButton.Content = SkipReviewCheckBox?.IsChecked == true
                ? "⚡ DEPLOY → COMMIT + PUSH"
                : "📝 COMMIT && REVIEW";
        }

        private async void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            if (ActionButton.Tag == null) return;
            string action = ActionButton.Tag.ToString();

            if (action == "commit")
            {
                await HandleCommit();
            }
            else if (action == "push")
            {
                await PushToGithub();
            }
            else if (action == "compare")
            {
                await HandleCompare();
            }
        }

        private async Task HandleCommit()
        {
            try
            {
                var changes = await _gitService.GetUncommittedChangesAsync();
                if (changes.Count == 0)
                {
                    StatusText.Text = "No pending files to send.";
                    StatusText.Foreground = GetThemeBrush("Text.Muted", System.Windows.Media.Brushes.LightGray);
                    DeployButton.IsEnabled = false;
                    DeployButton.Visibility = Visibility.Collapsed;
                    LoadGitData();
                    return;
                }

                var defaultMessage = $"deploy update {AppTimeService.LocalNow:yyyy-MM-dd HH:mm}";

                // Skip review modal → same as modal "Deploy → Commit + Push".
                if (SkipReviewCheckBox?.IsChecked == true)
                {
                    AddLog("⚡ Skip review enabled — running Deploy → Commit + Push directly.");
                    await RunDeployCommitPushPipelineAsync(changes, defaultMessage);
                    return;
                }

                var commitWindow = new CommitWindow(changes);
                commitWindow.CommitMessage = defaultMessage;
                WindowOwnerService.ShowDialogOwned(commitWindow, this);

                if (!commitWindow.Confirmed)
                {
                    return;
                }

                if (commitWindow.SyncWithoutDeployRequested)
                {
                    string selectedSyncPath = commitWindow.SyncWithoutDeployPath?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(selectedSyncPath))
                    {
                        ModernMessageBox.Show("No file was selected for sync.", "Sync", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    AddLog($"🔄 Sync-only mode requested for file: {selectedSyncPath} (no FTP deploy).");
                    await _gitService.CommitSpecificPathsAsync(new[] { selectedSyncPath }, commitWindow.CommitMessage);
                    AddLog($"✅ Commit completed for selected file: {selectedSyncPath}");
                    await SyncLocalBranchesIfNeededAsync();
                    bool pushSucceededSyncOnly = await PushToGithub();
                    AddLog(pushSucceededSyncOnly
                        ? "✅ Single-file sync-only pipeline finished."
                        : "⚠️ Single-file sync-only pipeline finished with push error.");
                    StatusText.Text = pushSucceededSyncOnly
                        ? "Selected file synced without deploy."
                        : "Selected file synced locally; push had issues.";
                    StatusText.Foreground = GetThemeBrush(
                        pushSucceededSyncOnly ? "Status.Success" : "Status.Warning",
                        pushSucceededSyncOnly ? System.Windows.Media.Brushes.LightGreen : System.Windows.Media.Brushes.Orange);
                    LoadGitData();
                    return;
                }

                await RunDeployCommitPushPipelineAsync(changes, commitWindow.CommitMessage);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Send failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task RunDeployCommitPushPipelineAsync(List<FileChange> changes, string commitMessage)
        {
            var filesToDeploy = changes
                .Select(c => new FileChange { Name = c.Name, Type = c.Type, DiffPatch = c.DiffPatch })
                .ToList();

            _fileViewModels = filesToDeploy.Select(c => new DeployFileViewModel(c) { IsSelected = true }).ToList();
            FilesListBox.ItemsSource = _fileViewModels;
            SelectAllCheckBox.IsChecked = true;
            SelectFileSilently(-1);
            DeployButton.IsEnabled = false;
            DeployButton.Visibility = Visibility.Collapsed;
            ClearCompareContext(clearList: false);

            AddLog($"🚀 Step 1/2: Deploying {filesToDeploy.Count} file(s)...");
            var deployResult = await StartDeployProcess(filesToDeploy, isAutoFlow: true, runGitPostSteps: false);
            if (deployResult.HasFatalError || deployResult.IsCompleteFailure)
            {
                AddLog("⛔ Deploy failed. Commit+Push skipped to protect server state.");
                StatusText.Text = "Deploy failed. Commit was not created.";
                StatusText.Foreground = GetThemeBrush("Status.Error", System.Windows.Media.Brushes.OrangeRed);
                return;
            }

            if (deployResult.IsPartialSuccess)
            {
                var continueAfterWarning = ConfirmContinueGitAfterPartialDeploy(deployResult);
                if (!continueAfterWarning)
                {
                    AddLog("⏸ Commit+Push skipped by user after partial deploy warning.");
                    StatusText.Text = "Partial deploy completed. Commit skipped by user.";
                    StatusText.Foreground = GetThemeBrush("Status.Warning", System.Windows.Media.Brushes.Orange);
                    return;
                }
            }

            var message = string.IsNullOrWhiteSpace(commitMessage)
                ? $"deploy update {AppTimeService.LocalNow:yyyy-MM-dd HH:mm}"
                : commitMessage.Trim();

            AddLog("📝 Step 2/2: Deploy succeeded, committing and pushing...");
            await _gitService.CommitChangesAsync(message);
            AddLog("✅ Commit completed.");
            await SyncLocalBranchesIfNeededAsync();
            bool pushSucceeded = await PushToGithub();
            AddLog(pushSucceeded ? "✅ Send pipeline finished." : "⚠️ Send pipeline finished with push error.");
            await AddDeploymentHistoryRecordAsync(filesToDeploy);
            LoadGitData();
        }

        private async Task HandleCompare()
        {
            using var scope = PerformanceSampler.Instance.BeginScope("deploy", "compare-branches");
            if (SourceBranchComboBox.SelectedItem is ComboBoxItem sourceItem && 
                TargetBranchComboBox.SelectedItem is ComboBoxItem targetItem)
            {
                string? source = sourceItem.Content?.ToString();
                string? target = targetItem.Content?.ToString();

                try
                {
                    ActionButton.IsEnabled = false;
                    ActionButton.Content = "⏳ Processing...";

                    var changes = await _gitService.GetDiffAsync(source, target);

                    if (changes.Count == 0)
                    {
                        StatusText.Text = "No changes to deploy.";
                        DeployButton.IsEnabled = false;
                        DeployButton.Visibility = Visibility.Collapsed;
                        ClearCompareContext(clearList: true);
                    }
                    else
                    {
                        _fileViewModels = changes.Select(c => new DeployFileViewModel(c) { IsSelected = true }).ToList();
                        FilesListBox.ItemsSource = _fileViewModels;
                        SelectAllCheckBox.IsChecked = true;
                        SelectFileSilently(-1);
                        ConfigureCompareSyncButton();
                        DeployButton.IsEnabled = _fileViewModels.Count > 0;
                        DeployButton.Visibility = _fileViewModels.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                        SetCompareContext(source, target);

                        StatusText.Text = $"Review {_fileViewModels.Count} file(s), then click SYNC.";
                        StatusText.Foreground = GetThemeBrush("Text.Muted", System.Windows.Media.Brushes.LightGray);
                        AddLog($"🔍 Compare ready: {_fileViewModels.Count} file(s) loaded in current page.");
                    }
                }
                catch (Exception ex)
                {
                    scope.Fail(ex);
                    ModernMessageBox.Show($"Git Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    ActionButton.IsEnabled = true;
                    UpdateActionButtonState();
                }
            }
        }

        private async Task<bool> PushToGithub()
        {
            if (ActionButton == null) return false;

            ActionButton.IsEnabled = false;
            ActionButton.Content = "⏳ Pushing...";
            try
            {
                if (!await EnsureOriginRemoteReadyAsync())
                {
                    AddLog("ℹ️ No remote configured. Completed in local-sync mode.");
                    StatusText.Text = "Completed in local-sync mode (no remote).";
                    StatusText.Foreground = GetThemeBrush("Status.Success", System.Windows.Media.Brushes.LightGreen);
                    return true;
                }

                AddLog("☁️ Pushing changes to GitHub...");

                var pushResult = await _gitService.PushOrSkipAsync();
                if (pushResult == PushExecutionResult.SkippedNoRemote)
                {
                    AddLog("ℹ️ No remote configured. Completed in local-sync mode.");
                    StatusText.Text = "Completed in local-sync mode (no remote).";
                    StatusText.Foreground = GetThemeBrush("Status.Success", System.Windows.Media.Brushes.LightGreen);
                    return true;
                }
                
                AddLog("✅ Successfully pushed to GitHub!");
                return true;
            }
            catch (GitCommandException gitEx) when (IsMissingOriginError(gitEx))
            {
                AddLog("ℹ️ Remote not found. Completed in local-sync mode.");
                StatusText.Text = "Completed in local-sync mode (origin missing).";
                StatusText.Foreground = GetThemeBrush("Status.Success", System.Windows.Media.Brushes.LightGreen);
                return true;
            }
            catch (Exception ex)
            {
                AddLog($"❌ Push failed: {ex.Message}");
                ModernMessageBox.Show($"Push failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            finally
            {
                await RefreshBranchStatusAsync();
                ActionButton.IsEnabled = true;
                UpdateActionButtonState();
            }
        }

        private async void DeployButton_Click(object sender, RoutedEventArgs e)
        {
            if (isDeploying) return;

            var selectedFiles = _fileViewModels.Where(x => x.IsSelected).Select(x => new FileChange { Name = x.Name, Type = x.Type }).ToList();
            
            if (selectedFiles.Count == 0)
            {
                ModernMessageBox.Show("Please select at least one file to sync.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await StartDeployProcess(selectedFiles);
        }

        private async Task<DeployExecutionResult> StartDeployProcess(List<FileChange> filesToDeploy, bool isAutoFlow = false, bool runGitPostSteps = true)
        {
            using var scope = PerformanceSampler.Instance.BeginScope("deploy", "sync-pipeline", $"files={filesToDeploy?.Count ?? 0}");
            isDeploying = true;
            DeployButton.IsEnabled = false;
            ActionButton.IsEnabled = false;
            SourceBranchComboBox.IsEnabled = false;
            TargetBranchComboBox.IsEnabled = false;
            var deployResult = new DeployExecutionResult
            {
                TotalSelected = filesToDeploy?.Count ?? 0
            };

            try
            {
                ShowDeployLogsForActiveDeploy();
                AddLog($"🚀 Starting sync pipeline ({filesToDeploy.Count} files)...");
                
                bool ftpRequired = IsFtpDeploymentMode();
                bool hasFtpTarget = HasFtpTargetConfigured();
                if (ftpRequired && !hasFtpTarget)
                {
                    throw new InvalidOperationException("Deployment mode is FTP but no FTP/SFTP connection is configured.");
                }

                // In FTP mode we must really deploy first; Git-only mode can simulate.
                if (hasFtpTarget)
                {
                    deployResult = await UploadFilesAsync(filesToDeploy);
                }
                else
                {
                    deployResult = await SimulateDeploy(filesToDeploy);
                }

                LogDeploySummary(deployResult);
                if (deployResult.HasFatalError)
                {
                    throw new InvalidOperationException(deployResult.FatalErrorMessage ?? "Upload failed due to a fatal FTP error.");
                }

                var continueGitSteps = runGitPostSteps;
                if (runGitPostSteps && deployResult.IsPartialSuccess)
                {
                    continueGitSteps = ConfirmContinueGitAfterPartialDeploy(deployResult);
                    if (!continueGitSteps)
                    {
                        AddLog("⏸ Git sync/push skipped by user after partial deploy warning.");
                    }
                }

                // Sync Branches
                if (continueGitSteps &&
                    SourceBranchComboBox.SelectedItem is ComboBoxItem sourceItem && 
                    TargetBranchComboBox.SelectedItem is ComboBoxItem targetItem)
                {
                    string? source = sourceItem.Content?.ToString();
                    string? target = targetItem.Content?.ToString();
                    
                    if (!string.IsNullOrEmpty(source) && !string.IsNullOrEmpty(target) && source != target)
                    {
                        AddLog($"🔄 Syncing branches: merging {source} into {target}...");
                        try
                        {
                            await _gitService.SyncBranchesAsync(source, target);
                            AddLog("✅ Branches synced successfully!");
                            
                            // Force button update to reflect synced state
                            _cachedUncommittedCount = 0; 
                            
                            Dispatcher.Invoke(() =>
                            {
                                SetActionButton("synced", "✅ SYNCED", "Status.Success", false);
                                StatusText.Text = "Branches are synchronized.";
                                StatusText.Foreground = GetThemeBrush("Status.Success", System.Windows.Media.Brushes.LightGreen);
                            });
                        }
                        catch (Exception syncEx)
                        {
                            AddLog($"⚠️ Branch sync failed: {syncEx.Message}");
                        }
                    }
                }

                // Auto-Push
                if (continueGitSteps && _projectConfig.AutoPush)
                {
                    if (await EnsureOriginRemoteReadyAsync())
                    {
                        try
                        {
                            AddLog("☁️ Auto-pushing to GitHub...");
                            var pushResult = await _gitService.PushOrSkipAsync();
                            if (pushResult == PushExecutionResult.PushedToRemote)
                            {
                                AddLog("✅ Successfully pushed to GitHub!");
                            }
                            else
                            {
                                AddLog("ℹ️ Auto-push skipped (no remote). Local sync already completed.");
                            }
                        }
                        catch (Exception pushEx)
                        {
                            AddLog($"⚠️ Auto-push failed: {pushEx.Message}");
                        }
                    }
                    else
                    {
                        AddLog("ℹ️ Auto-push skipped (no remote). Local sync already completed.");
                    }
                }

                if (deployResult.IsFullSuccess)
                {
                    StatusText.Text = "Deployment finished successfully.";
                    StatusText.Foreground = GetThemeBrush("Status.Success", System.Windows.Media.Brushes.LightGreen);
                }
                else if (deployResult.IsPartialSuccess)
                {
                    StatusText.Text = $"Deployment finished with warnings ({deployResult.UploadedCount} uploaded, {deployResult.FailedItems.Count} failed).";
                    StatusText.Foreground = GetThemeBrush("Status.Warning", System.Windows.Media.Brushes.Orange);
                }
                else if (deployResult.IsCompleteFailure)
                {
                    StatusText.Text = "Deployment finished with errors (no files uploaded).";
                    StatusText.Foreground = GetThemeBrush("Status.Error", System.Windows.Media.Brushes.OrangeRed);
                }

                if (runGitPostSteps)
                {
                    ClearCompareContext(clearList: true);
                }
                
                // NO SUCCESS DIALOG for auto flow
                if (!isAutoFlow)
                {
                    if (deployResult.IsFullSuccess)
                    {
                        ModernMessageBox.Show("Deployment completed successfully! ✅", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else if (deployResult.IsPartialSuccess || deployResult.IsCompleteFailure)
                    {
                        ModernMessageBox.Show(BuildPartialDeployMessage(deployResult), "Deploy Result", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }

                return deployResult;
            }
            catch (Exception ex)
            {
                scope.Fail(ex);
                AddLog($"❌ Error: {ex}");
                StatusText.Text = "Deployment failed.";
                StatusText.Foreground = GetThemeBrush("Status.Error", System.Windows.Media.Brushes.Red);
                var detailed = ex.ToString();
                try
                {
                    System.Windows.Clipboard.SetText(detailed);
                }
                catch
                {
                    // Clipboard might fail in some contexts; ignore.
                }
                // ALWAYS show error dialog
                ModernMessageBox.Show($"Deployment Failed:\n\n{detailed}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                deployResult.HasFatalError = true;
                deployResult.FatalErrorMessage = ex.Message;
                return deployResult;
            }
            finally
            {
                _gitService.EnsureGitFolderHidden();
                isDeploying = false;
                if (_compareResultActive)
                {
                    ConfigureCompareSyncButton();
                }
                DeployButton.IsEnabled = _compareResultActive && _fileViewModels.Any(x => x.IsSelected);
                DeployButton.Visibility = _compareResultActive ? Visibility.Visible : Visibility.Collapsed;
                ActionButton.IsEnabled = true;
                SourceBranchComboBox.IsEnabled = true;
                TargetBranchComboBox.IsEnabled = true;
                DeployProgressBar.Value = 0;
                ProgressText.Text = "Deployment finished!";
                HideDeployLogsAfterDeploy();
                LoadGitData();
            }
        }

        private async Task<DeployExecutionResult> UploadFilesAsync(List<FileChange> files)
        {
            var result = new DeployExecutionResult
            {
                TotalSelected = files?.Count ?? 0
            };

            try
            {
                var profile = GetActiveConnectionProfile();
                string ftpHost = profile?.Host ?? _projectConfig.FtpHost;
                string ftpUser = profile?.Username ?? _projectConfig.FtpUsername;
                int ftpPort = (profile?.Port ?? 0) > 0 ? profile!.Port : _projectConfig.FtpPort;
                string ftpPassword = profile != null ? EncryptionService.Decrypt(profile.Password) : _projectConfig.FtpPasswordDecrypted;

                AddLog($"🔌 Connecting to {ftpHost}...");
                
                using (var client = new AsyncFtpClient(ftpHost, ftpUser, ftpPassword, ftpPort))
                {
                    // Configure timeout for large files (zip files)
                    client.Config.DataConnectionType = FluentFTP.FtpDataConnectionType.AutoPassive;
                    client.Config.ReadTimeout = 300000; // 5 minutes
                    client.Config.DataConnectionReadTimeout = 300000; // 5 minutes
                    client.Config.RetryAttempts = 3;
                    
                    try
                    {
                        await client.Connect();
                    }
                    catch (Exception connectEx)
                    {
                        result.HasFatalError = true;
                        result.FatalErrorMessage = $"FTP connect/login failed: {connectEx.Message}";
                        AddLog($"❌ {result.FatalErrorMessage}");
                        return result;
                    }

                    AddLog("✅ Connected!");

                    int total = files.Count;
                    int current = 0;

                    var mapping = GetPrimaryMapping(profile);
                    // Use profile RemotePath, not legacy config.RemotePath
                    var defaultRemoteBase = NormalizeRemoteBase(profile?.RemotePath ?? _projectConfig.RemotePath);
                    var mappedRemoteBase = mapping != null
                        ? CombineRemotePaths(defaultRemoteBase, mapping.RemotePath)
                        : defaultRemoteBase;
                    var mappingLocalSegment = NormalizeLocalMappingSegment(mapping?.LocalPath);

                    foreach (var file in files)
                    {
                        current++;
                        if (file.Type == ChangeType.Deleted)
                        {
                            result.SkippedCount++;
                            AddLog($"⏭ Skipped delete sync for {file.Name} (remote delete is not enabled in deploy pipeline).");
                            continue;
                        }

                        string localPath = System.IO.Path.Combine(_projectConfig.LocalProjectPath, file.Name);
                        bool isLocalDirectory = System.IO.Directory.Exists(localPath);
                        bool isLocalFile = System.IO.File.Exists(localPath);
                        if (!isLocalFile && !isLocalDirectory)
                        {
                            result.FailedItems.Add(new DeployFailedItem(file.Name, "Local file missing."));
                            AddLog($"⚠️ Missing local file: {file.Name}");
                            continue;
                        }

                        string relativePath = file.Name.Replace("\\", "/").TrimEnd('/');
                        string remoteBaseToUse = defaultRemoteBase;
                        string relativeRemote = relativePath;

                        if (!string.IsNullOrEmpty(mappingLocalSegment))
                        {
                            var prefix = mappingLocalSegment.EndsWith("/")
                                ? mappingLocalSegment
                                : mappingLocalSegment + "/";

                            if (relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            {
                                remoteBaseToUse = mappedRemoteBase;
                                relativeRemote = relativePath.Substring(prefix.Length);
                                if (string.IsNullOrWhiteSpace(relativeRemote))
                                {
                                    relativeRemote = Path.GetFileName(relativePath);
                                }
                            }
                        }

                        string remotePath = $"{remoteBaseToUse.TrimEnd('/')}/{relativeRemote}";

                        if (isLocalDirectory)
                        {
                            try
                            {
                                AddLog($"📁 Ensuring remote folder {file.Name}...");
                                ProgressText.Text = $"Creating folder {current}/{total}: {file.Name}";
                                DeployProgressBar.Value = (current * 100) / total;
                                await FtpDirectoryEnsure.EnsureAsync(client, remotePath);
                                result.UploadedCount++;
                                AddLog($"✅ Folder ready {file.Name}");
                            }
                            catch (Exception dirEx)
                            {
                                result.FailedItems.Add(new DeployFailedItem(file.Name, dirEx.Message));
                                AddLog($"❌ Folder failed {file.Name}: {dirEx.Message}");
                            }

                            continue;
                        }

                        try
                        {
                            AddLog($"📤 Uploading {file.Name}...");
                            ProgressText.Text = $"Uploading {current}/{total}: {file.Name}";
                            DeployProgressBar.Value = (current * 100) / total;

                            await FtpDirectoryEnsure.EnsureParentOfFileAsync(client, remotePath);
                            await client.UploadFile(localPath, remotePath, FtpRemoteExists.Overwrite, createRemoteDir: true);
                            result.UploadedCount++;
                            AddLog($"✅ Uploaded {file.Name}");
                        }
                        catch (Exception fileEx)
                        {
                            if (IsPermissionDeniedError(fileEx))
                            {
                                result.FailedItems.Add(new DeployFailedItem(file.Name, fileEx.Message, isPermissionDenied: true));
                                AddLog($"⚠️ Permission denied for {file.Name}. Continuing with remaining files.");
                                continue;
                            }

                            result.HasFatalError = true;
                            result.FatalErrorMessage = $"Fatal FTP transfer error on {file.Name}: {fileEx.Message}";
                            AddLog($"❌ {result.FatalErrorMessage}");
                            break;
                        }
                    }
                }

                if (!result.HasFatalError)
                {
                    if (result.FailedItems.Count == 0)
                    {
                        AddLog("🎉 All files uploaded successfully!");
                    }
                    else
                    {
                        AddLog($"⚠️ Upload completed with warnings. Uploaded: {result.UploadedCount}, Failed: {result.FailedItems.Count}");
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                result.HasFatalError = true;
                result.FatalErrorMessage = $"Upload failed: {ex.GetType().Name} - {ex.Message}";
                AddLog($"❌ Upload Error: {ex.Message}");
                return result;
            }
        }

        private ConnectionProfile? GetActiveConnectionProfile()
        {
            if (string.IsNullOrWhiteSpace(_projectConfig.ConnectionProfileId)) return null;
            try
            {
                var connections = _configService.LoadConnections();
                return connections.FirstOrDefault(c => string.Equals(c.Id, _projectConfig.ConnectionProfileId, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return null;
            }
        }

        private PathMapping? GetPrimaryMapping(ConnectionProfile? profile)
        {
            if (profile?.PathMappings == null) return null;
            return profile.PathMappings.FirstOrDefault(pm =>
                pm != null &&
                (!string.IsNullOrWhiteSpace(pm.LocalPath) || !string.IsNullOrWhiteSpace(pm.RemotePath)));
        }

        private string NormalizeLocalMappingSegment(string? localPath)
        {
            if (string.IsNullOrWhiteSpace(localPath)) return string.Empty;
            var normalized = localPath.Trim().Trim('\\', '/');
            normalized = normalized.Replace("\\", "/");
            return normalized;
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

        private async Task<DeployExecutionResult> SimulateDeploy(List<FileChange> files)
        {
            var result = new DeployExecutionResult
            {
                TotalSelected = files?.Count ?? 0
            };

            int total = files.Count;
            int current = 0;

            foreach (var file in files)
            {
                current++;
                if (file.Type == ChangeType.Deleted)
                {
                    result.SkippedCount++;
                    AddLog($"[SIMULATION] ⏭ Skipping delete for {file.Name}");
                    continue;
                }

                AddLog($"[SIMULATION] 📤 Uploading {file.Name}...");
                ProgressText.Text = $"Simulating {current}/{total}: {file.Name}";
                DeployProgressBar.Value = (current * 100) / total;
                await Task.Delay(200); 
                AddLog($"[SIMULATION] ✅ Uploaded {file.Name}");
                result.UploadedCount++;
            }
            
            AddLog("🎉 Simulation complete!");
            return result;
        }

        private bool ConfirmContinueGitAfterPartialDeploy(DeployExecutionResult result)
        {
            var message = BuildPartialDeployMessage(result) +
                          "\n\nSome files failed to upload. Continue with Git commit/sync/push anyway?";
            return ModernMessageBox.Show(
                message,
                "Partial Deploy Warning",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
        }

        private void LogDeploySummary(DeployExecutionResult result)
        {
            AddLog("📊 Deploy summary:");
            AddLog($"   Selected: {result.TotalSelected}");
            AddLog($"   Uploaded: {result.UploadedCount}");
            AddLog($"   Skipped: {result.SkippedCount}");
            AddLog($"   Failed: {result.FailedItems.Count}");
            if (result.HasFatalError && !string.IsNullOrWhiteSpace(result.FatalErrorMessage))
            {
                AddLog($"   Fatal: {result.FatalErrorMessage}");
            }

            foreach (var failed in result.FailedItems.Take(5))
            {
                AddLog($"   - {failed.Path}: {failed.Reason}");
            }

            if (result.FailedItems.Count > 5)
            {
                AddLog($"   ... and {result.FailedItems.Count - 5} more failed file(s).");
            }
        }

        private static bool IsPermissionDeniedError(Exception ex)
        {
            var message = ex.ToString();
            return message.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("550", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildPartialDeployMessage(DeployExecutionResult result)
        {
            var lines = new List<string>
            {
                $"Selected: {result.TotalSelected}",
                $"Uploaded: {result.UploadedCount}",
                $"Skipped: {result.SkippedCount}",
                $"Failed: {result.FailedItems.Count}"
            };

            if (result.FailedItems.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("Failed files:");
                foreach (var failed in result.FailedItems.Take(8))
                {
                    lines.Add($"- {failed.Path}: {failed.Reason}");
                }

                if (result.FailedItems.Count > 8)
                {
                    lines.Add($"... and {result.FailedItems.Count - 8} more.");
                }
            }

            if (result.HasFatalError && !string.IsNullOrWhiteSpace(result.FatalErrorMessage))
            {
                lines.Add(string.Empty);
                lines.Add($"Fatal error: {result.FatalErrorMessage}");
            }

            return string.Join(Environment.NewLine, lines);
        }

        private sealed class DeployExecutionResult
        {
            public int TotalSelected { get; set; }
            public int UploadedCount { get; set; }
            public int SkippedCount { get; set; }
            public List<DeployFailedItem> FailedItems { get; } = new();
            public bool HasFatalError { get; set; }
            public string FatalErrorMessage { get; set; } = string.Empty;
            public bool IsFullSuccess => !HasFatalError && FailedItems.Count == 0;
            public bool IsPartialSuccess => !HasFatalError && FailedItems.Count > 0 && UploadedCount > 0;
            public bool IsCompleteFailure => !HasFatalError && FailedItems.Count > 0 && UploadedCount == 0;
        }

        private sealed class DeployFailedItem
        {
            public DeployFailedItem(string path, string reason, bool isPermissionDenied = false)
            {
                Path = path ?? string.Empty;
                Reason = reason ?? string.Empty;
                IsPermissionDenied = isPermissionDenied;
            }

            public string Path { get; }
            public string Reason { get; }
            public bool IsPermissionDenied { get; }
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (SelectAllCheckBox.IsChecked == true)
            {
                foreach (var file in _fileViewModels) file.IsSelected = true;
            }
            else
            {
                foreach (var file in _fileViewModels) file.IsSelected = false;
            }
            FilesListBox.Items.Refresh();
            DeployButton.IsEnabled = _compareResultActive && _fileViewModels.Any(x => x.IsSelected);
        }

        private void FilesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressFileSelectionModal) return;
            if (FilesListBox?.SelectedItem is DeployFileViewModel vm)
            {
                OpenCodeViewer(vm);
                SelectFileSilently(-1);
            }
        }

        private void OpenCodeViewer(DeployFileViewModel vm)
        {
            try
            {
                var diffContent = string.IsNullOrWhiteSpace(vm.DiffText)
                    ? $"No diff available for {vm.Name}."
                    : vm.DiffText;
                var owner = Window.GetWindow(this);
                var viewer = new ReadOnlyDiffWindow(vm.Name, vm.StatusText, diffContent);
                PositionPreviewWindow(viewer, owner);
                if (owner != null) viewer.Owner = owner;
                viewer.ShowDialog();
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Unable to open viewer: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenCodeButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is DeployFileViewModel vm)
            {
                _suppressFileSelectionModal = true;
                try
                {
                    OpenCodeViewer(vm);
                }
                finally
                {
                    _suppressFileSelectionModal = false;
                }
                SelectFileSilently(-1);
                e.Handled = true;
            }
        }

        private void SetCompareContext(string? sourceBranch, string? targetBranch)
        {
            _compareResultActive = true;
            _compareSourceBranch = sourceBranch?.Trim() ?? string.Empty;
            _compareTargetBranch = targetBranch?.Trim() ?? string.Empty;
        }

        private void ClearCompareContext(bool clearList)
        {
            _compareResultActive = false;
            _compareSourceBranch = string.Empty;
            _compareTargetBranch = string.Empty;

            if (clearList)
            {
                _fileViewModels.Clear();
                FilesListBox.ItemsSource = null;
            }

            DeployButton.IsEnabled = false;
            DeployButton.Visibility = Visibility.Collapsed;
        }

        private bool ShouldKeepCompareResultsVisible()
        {
            if (!_compareResultActive || _fileViewModels.Count == 0)
            {
                return false;
            }

            string selectedSource = (SourceBranchComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;
            string selectedTarget = (TargetBranchComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;

            return string.Equals(selectedSource, _compareSourceBranch, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(selectedTarget, _compareTargetBranch, StringComparison.OrdinalIgnoreCase);
        }

        private void SelectFileSilently(int index)
        {
            _suppressFileSelectionModal = true;
            try
            {
                FilesListBox.SelectedIndex = index;
            }
            finally
            {
                _suppressFileSelectionModal = false;
            }
        }

        private void ConfigureCompareSyncButton()
        {
            if (DeployButton == null) return;

            if (IsFtpDeploymentMode())
            {
                DeployButton.Content = "🔄 FTP DEPLOY + SYNC";
                DeployButton.ToolTip = "Upload selected files to FTP/SFTP first, then sync branches.";
            }
            else
            {
                DeployButton.Content = "🔄 SYNC";
                DeployButton.ToolTip = "Sync selected branch changes locally.";
            }
        }

        private bool IsFtpDeploymentMode()
        {
            return _projectConfig != null &&
                   (_projectConfig.DeployMode == DeployMode.FtpDeploy || _projectConfig.DeployMode == DeployMode.Hybrid);
        }

        private bool HasFtpTargetConfigured()
        {
            if (!string.IsNullOrWhiteSpace(_projectConfig?.FtpHost))
            {
                return true;
            }

            var profile = GetActiveConnectionProfile();
            return profile != null && !string.IsNullOrWhiteSpace(profile.Host);
        }

        private static void PositionPreviewWindow(Window preview, Window? owner)
        {
            if (preview == null || owner == null) return;

            preview.WindowStartupLocation = WindowStartupLocation.Manual;
            var workArea = SystemParameters.WorkArea;

            double desiredLeft = owner.Left + owner.Width + 14;
            double left = desiredLeft;
            if (left + preview.Width > workArea.Right)
            {
                left = owner.Left - preview.Width - 14;
            }
            if (left < workArea.Left)
            {
                left = Math.Max(workArea.Left, owner.Left + 24);
            }

            double top = owner.Top + 24;
            if (top + preview.Height > workArea.Bottom)
            {
                top = Math.Max(workArea.Top, workArea.Bottom - preview.Height - 12);
            }
            if (top < workArea.Top)
            {
                top = workArea.Top + 8;
            }

            preview.Left = left;
            preview.Top = top;
        }

        private void AddLog(string message)
        {
            if (LogTextBox == null) return;

            Dispatcher.Invoke(() =>
            {
                var timestamp = AppTimeService.LocalNow.ToString("HH:mm:ss");
                var newLog = $"[{timestamp}] {message}\n";

                if (LogTextBox.Text == "Waiting for deployment...")
                {
                    LogTextBox.Text = newLog;
                }
                else
                {
                    LogTextBox.Text += newLog;
                }

                LogTextBox.CaretIndex = LogTextBox.Text.Length;
                LogTextBox.ScrollToEnd();
            });
        }

        private void LogCopyMenu_Click(object sender, RoutedEventArgs e)
        {
            if (LogTextBox == null)
            {
                return;
            }

            if (LogTextBox.SelectionLength > 0)
            {
                LogTextBox.Copy();
            }
            else if (!string.IsNullOrEmpty(LogTextBox.Text))
            {
                System.Windows.Clipboard.SetText(LogTextBox.Text);
            }
        }

        private void LogSelectAllMenu_Click(object sender, RoutedEventArgs e)
        {
            LogTextBox?.SelectAll();
            LogTextBox?.Focus();
        }

        private async Task AddDeploymentHistoryRecordAsync(List<FileChange> filesToDeploy)
        {
            string commitHash = await _gitService.GetLastCommitHashAsync();
            var record = new DeploymentRecord
            {
                Title = $"Deploy {SourceBranchComboBox.Text} to {TargetBranchComboBox.Text}",
                Date = AppTimeService.LocalNow,
                FilesCount = filesToDeploy.Count,
                Branch = SourceBranchComboBox.Text,
                Status = "Success",
                Files = filesToDeploy.Select(x => x.Name).ToList(),
                CommitHash = commitHash
            };
            _historyService.AddRecord(record);
            AddLog("🗂 Deployment recorded in history (after commit).");
        }

        private async Task SyncLocalBranchesIfNeededAsync()
        {
            if (SourceBranchComboBox.SelectedItem is not ComboBoxItem sourceItem ||
                TargetBranchComboBox.SelectedItem is not ComboBoxItem targetItem)
            {
                return;
            }

            string? source = sourceItem.Content?.ToString();
            string? target = targetItem.Content?.ToString();
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            {
                return;
            }

            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            AddLog($"🔄 Syncing local branches: {source} -> {target} ...");
            try
            {
                await _gitService.SyncBranchesAsync(source, target);
                AddLog("✅ Local branch sync completed.");
            }
            catch (Exception syncEx)
            {
                AddLog($"⚠️ Local branch sync failed: {syncEx.Message}");
            }
        }

        private async Task<bool> EnsureOriginRemoteReadyAsync()
        {
            string remoteUrl = await _gitService.GetRemoteUrlAsync();
            if (!string.IsNullOrWhiteSpace(remoteUrl))
            {
                return true;
            }

            string savedRemote = _projectConfig?.GitRemoteUrl?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(savedRemote))
            {
                AddLog($"🔧 Remote origin missing. Restoring from saved URL: {savedRemote}");
                await _gitService.SetRemoteAsync(savedRemote);
                remoteUrl = await _gitService.GetRemoteUrlAsync();
                if (!string.IsNullOrWhiteSpace(remoteUrl))
                {
                    AddLog("✅ Remote origin restored automatically.");
                    return true;
                }
            }

            AddLog("⚠️ Push skipped: no 'origin' remote configured.");
            StatusText.Text = "Deploy+Commit done. Push skipped (origin not configured).";
            StatusText.Foreground = GetThemeBrush("Status.Warning", System.Windows.Media.Brushes.Orange);
            return false;
        }

        private static bool IsMissingOriginError(GitCommandException ex)
        {
            string details = ex.GetDetailedMessage();
            return details.Contains("'origin' does not appear to be a git repository", StringComparison.OrdinalIgnoreCase) ||
                   details.Contains("No such remote", StringComparison.OrdinalIgnoreCase) ||
                   details.Contains("No configured push destination", StringComparison.OrdinalIgnoreCase);
        }

        private void ClearLogs_Click(object sender, RoutedEventArgs e)
        {
            if (LogTextBox != null)
            {
                LogTextBox.Text = "Waiting for deployment...";
                AddLog("🗑️ Logs cleared");
            }
        }

        private void NewBranchButton_Click(object sender, RoutedEventArgs e)
        {
            var inputDialog = new InputDialog("Create New Branch", "Enter new branch name:");
            if (WindowOwnerService.ShowDialogOwned(inputDialog, this) == true)
            {
                string newBranch = inputDialog.ResponseText.Trim();
                if (string.IsNullOrWhiteSpace(newBranch)) return;

                CreateNewBranch(newBranch);
            }
        }

        private async void CreateNewBranch(string branchName)
        {
            try
            {
                await _gitService.CreateBranchAsync(branchName);
                ModernMessageBox.Show($"Branch '{branchName}' created successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                LoadGitData(); // Refresh UI
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Failed to create branch: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}