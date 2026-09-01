using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GitDeployPro.Behaviors;
using GitDeployPro.Models;
using GitDeployPro.Services;
using GitDeployPro.Services.Localization;
using GitDeployPro.Services.Remote;
using GitDeployPro.Services.Theme;
using GitDeployPro.Windows;
using Microsoft.Web.WebView2.Core;
using Forms = System.Windows.Forms;

namespace GitDeployPro.Controls
{
    public sealed class RemoteEditorModeChangedEventArgs : EventArgs
    {
        public RemoteEditorModeChangedEventArgs(bool isOpen, string filePath)
        {
            IsOpen = isOpen;
            FilePath = filePath ?? string.Empty;
        }

        public bool IsOpen { get; }
        public string FilePath { get; }
    }

    public partial class DeployRemoteWorkspaceControl : System.Windows.Controls.UserControl
    {
        private const double EditorSidebarWidth = 430;
        private const double EditorSidebarGap = 12;
        private const double UploadStripHeight = 32;
        private static readonly TimeSpan UploadSuccessHold = TimeSpan.FromSeconds(5.5);
        private static readonly Thickness UploadStripMargin = new(0, 4, 0, 0);
        private static readonly int[] EditorFontSizeOptions = { 10, 12, 14, 16, 18, 20, 22, 24 };
        private static int _globalEditorFontSize = 14;

        private readonly ConfigurationService _configService = new();
        private readonly RemoteTreeBuilder _treeBuilder = new();
        private readonly List<string> _logLines = new();
        private readonly SemaphoreSlim _remoteCommandLock = new(1, 1);
        private IRemoteFileService? _remoteService;
        private ProjectConfig _projectConfig = new();
        private ConnectionProfile? _currentProfile;
        private RemoteEditSession? _editSession;
        private bool _suppressFallbackTextChanged;
        private bool _suppressWebDirtySignal;
        private bool _editorWebReady;
        private bool _editorWebEventsBound;
        private bool _editorUsingFallback;
        private bool _isBusy;
        private bool _isDownloading;
        private CancellationTokenSource? _downloadCts;
        private readonly TransferMonitorController _transferMonitor = new();
        private string _treeSearchQuery = string.Empty;
        private bool _suppressTreeSearchText;
        private int _connectGeneration;
        private CancellationTokenSource? _connectCts;
        private bool _isEditorOpen;
        private bool _isHostTeardown;
        private bool _autoConnectAttempted;
        private bool _autoConnectInProgress;
        private bool _suppressTabSelectionChanged;
        private bool _suppressConnectionSelectionChanged;
        private bool _profilesLoadedAfterVisible;
        private bool _editorRecoveryInProgress;
        private bool _editorWarmupInProgress;
        private bool _editorWarmupLoggedReady;
        private int _remoteLoadingDepth;
        private int _uploadStripAnimToken;
        private DispatcherTimer? _uploadSuccessHideTimer;
        private int _editorFontSize = 14;
        private DateTime _lastEditorRecoveryAttemptUtc = DateTime.MinValue;
        private DateTime _lastEditorWarmupAttemptUtc = DateTime.MinValue;
        private string _lastAutoConnectProfileId = string.Empty;
        private bool _operationLogExpanded;
        private bool _editorFloated;
        private static string? _codeEditorHtmlTemplate;

        public ObservableCollection<RemoteTreeNode> RootNodes { get; } = new();
        public ObservableCollection<RemoteEditSession> OpenSessions { get; } = new();
        public event EventHandler<RemoteEditorModeChangedEventArgs>? EditorModeChanged;
        public event EventHandler? EditorFloatRequested;
        public bool IsEditorOpen => _isEditorOpen;

        public static readonly DependencyProperty RemoteCheckboxModeEnabledProperty =
            DependencyProperty.Register(
                nameof(RemoteCheckboxModeEnabled),
                typeof(bool),
                typeof(DeployRemoteWorkspaceControl),
                new PropertyMetadata(false, OnRemoteCheckboxModeEnabledChanged));

        public bool RemoteCheckboxModeEnabled
        {
            get => (bool)GetValue(RemoteCheckboxModeEnabledProperty);
            set => SetValue(RemoteCheckboxModeEnabledProperty, value);
        }

        private static void OnRemoteCheckboxModeEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DeployRemoteWorkspaceControl control)
            {
                control.ApplyRemoteCheckboxModeUi();
            }
        }

        public DeployRemoteWorkspaceControl()
        {
            InitializeComponent();
            _transferMonitor.Attach(DownloadTransferMonitor);
            DataContext = this;
            RemoteTreeView.ItemsSource = RootNodes;
            EditorTabsListBox.ItemsSource = OpenSessions;
            _editorFontSize = EditorFontSizeOptions.Contains(_globalEditorFontSize) ? _globalEditorFontSize : 14;
            EditorFallbackTextBox.FontSize = _editorFontSize;
            EditorFallbackTextBox.IsEnabled = false;
            ShowBrowserMode(notify: false);
            ApplyWorkspacePanelLayout(editorMode: false);
            ResetUploadFeedback();
            UpdateRemoteBrowserVisualState();
            ConfigurationService.ConnectionsChanged += OnConnectionsChanged;
            ThemeService.Instance.ThemeChanged += OnDeployThemeChanged;
            Loaded += DeployRemoteWorkspaceControl_Loaded;
            Unloaded += DeployRemoteWorkspaceControl_Unloaded;
            ApplyFtpChromeTheme();
            ApplyEditorTabCloseBrush();
            ApplyOperationLogExpandedState();
        }

        private void ToggleOperationLogButton_Click(object sender, RoutedEventArgs e)
        {
            _operationLogExpanded = !_operationLogExpanded;
            ApplyOperationLogExpandedState();
        }

        private void ApplyOperationLogExpandedState()
        {
            if (OperationLogScrollViewer == null || ToggleOperationLogButton == null)
            {
                return;
            }

            OperationLogScrollViewer.Visibility = _operationLogExpanded
                ? Visibility.Visible
                : Visibility.Collapsed;
            ToggleOperationLogButton.Content = Loc.T(_operationLogExpanded ? "deploy.ftpLogHide" : "deploy.ftpLogShow");
        }

        private async void DeployRemoteWorkspaceControl_Loaded(object sender, RoutedEventArgs e)
        {
            // First LoadProfilesAsync often runs while the FTP dock is still Collapsed (ctor/Initialize).
            // Rebind once after the control is in the live visual tree so the combo ItemsSource paints.
            if (_profilesLoadedAfterVisible || _isHostTeardown)
            {
                return;
            }

            _profilesLoadedAfterVisible = true;
            try
            {
                await LoadProfilesAsync();
            }
            catch (Exception ex)
            {
                AddLog($"Initial profile load failed: {ex.Message}");
            }
        }

        private void OnDeployThemeChanged(object? sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => OnDeployThemeChanged(sender, e));
                return;
            }

            ApplyFtpChromeTheme();
            ApplyEditorTabCloseBrush();
            UpdateSaveUploadButtonAppearance();
            ApplyToolbarActionState(FloatEditorButton, _editorFloated);
            RemoteTreeBuilder.ApplyThemeToNodes(RootNodes);
            _ = ApplyEditorThemeAsync();
        }

        private void ApplyFtpChromeTheme()
        {
            var tokens = ThemeService.Instance.CurrentTokens;
            var selection = tokens.GetColor(
                "ftp.chrome.treeSelection",
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2B65D9"));
            var selectionText = tokens.GetColor(
                "ftp.chrome.treeSelectionText",
                System.Windows.Media.Colors.White);
            var overlay = tokens.GetColor(
                "ftp.chrome.overlay",
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#AF151C26"));
            var sk1 = tokens.GetColor(
                "ftp.chrome.skeleton1",
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#213D4F66"));
            var sk2 = tokens.GetColor(
                "ftp.chrome.skeleton2",
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1B3D4F66"));
            var sk3 = tokens.GetColor(
                "ftp.chrome.skeleton3",
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#163D4F66"));

            if (RemoteTreeView?.Resources != null)
            {
                RemoteTreeView.Resources[System.Windows.SystemColors.HighlightBrushKey] = new SolidColorBrush(selection);
                RemoteTreeView.Resources[System.Windows.SystemColors.InactiveSelectionHighlightBrushKey] = new SolidColorBrush(selection);
                RemoteTreeView.Resources[System.Windows.SystemColors.HighlightTextBrushKey] = new SolidColorBrush(selectionText);
                RemoteTreeView.Resources[System.Windows.SystemColors.InactiveSelectionHighlightTextBrushKey] = new SolidColorBrush(selectionText);
            }

            if (RemoteCheckboxModeEnabled)
            {
                ApplyRemoteCheckboxModeUi();
            }

            if (RemoteLoadingOverlay != null)
            {
                RemoteLoadingOverlay.Background = new SolidColorBrush(overlay);
            }

            if (EditorWebView != null)
            {
                var webBg = tokens.GetHex("editor.webviewBackground", "#FF1E1E1E");
                if (!webBg.StartsWith("#", StringComparison.Ordinal))
                {
                    webBg = "#" + webBg;
                }

                try
                {
                    EditorWebView.DefaultBackgroundColor = System.Drawing.ColorTranslator.FromHtml(
                        webBg.Length == 9 ? "#" + webBg[3..] : webBg);
                }
                catch
                {
                    // ignore invalid hex
                }
            }

            // Skeleton bars are anonymous in XAML; tint the loading card children when present.
            if (RemoteLoadingOverlay?.Child is Border card
                && card.Child is StackPanel panel
                && panel.Children.Count > 0
                && panel.Children[^1] is StackPanel skeleton)
            {
                var brushes = new[]
                {
                    new SolidColorBrush(sk1),
                    new SolidColorBrush(sk2),
                    new SolidColorBrush(sk3)
                };
                for (var i = 0; i < skeleton.Children.Count && i < brushes.Length; i++)
                {
                    if (skeleton.Children[i] is Border bar)
                    {
                        bar.Background = brushes[i];
                    }
                }
            }
        }

        private void ApplyEditorTabCloseBrush()
        {
            var tokens = ThemeService.Instance.CurrentTokens;
            Resources["Editor.TabClose"] = tokens.GetBrush(
                "editor.tabClose",
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF95A3BA"));
            Resources["Editor.TabCloseOnAccent"] = tokens.GetBrush(
                "editor.tabCloseOnAccent",
                System.Windows.Media.Colors.White);
        }

        private async Task ApplyEditorThemeAsync()
        {
            if (!_editorWebReady || EditorWebView?.CoreWebView2 == null)
            {
                return;
            }

            var tokens = ThemeService.Instance.CurrentTokens;
            var theme = tokens.MonacoTheme.Replace("\"", "");
            var bg = tokens.GetHex("editor.webviewBackground", "#1E1E1E").Replace("\"", "");
            await EditorWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.__setTheme && window.__setTheme(\"{theme}\", \"{bg}\");");
        }

        /// <summary>
        /// Call when the host page is truly leaving — not when navigating to another app page
        /// or when the control is temporarily reparented.
        /// </summary>
        public void NotifyHostTeardown()
        {
            if (_isHostTeardown)
            {
                return;
            }

            _isHostTeardown = true;
            CancelUploadStripAutoHide();
            ConfigurationService.ConnectionsChanged -= OnConnectionsChanged;
            ThemeService.Instance.ThemeChanged -= OnDeployThemeChanged;
            UnbindEditorWebViewEvents();
            _ = DisconnectIfConnectedAsync();
        }

        private void UnbindEditorWebViewEvents()
        {
            if (!_editorWebEventsBound || EditorWebView?.CoreWebView2 == null)
            {
                return;
            }

            EditorWebView.CoreWebView2.WebMessageReceived -= EditorWebView_WebMessageReceived;
            EditorWebView.CoreWebView2.NavigationCompleted -= EditorWebView_NavigationCompleted;
            _editorWebEventsBound = false;
        }

        private async Task DisconnectIfConnectedAsync()
        {
            if (_remoteService != null && _remoteService.IsConnected)
            {
                await DisconnectAsync();
            }
        }

        private void OnConnectionsChanged(object? sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.InvokeAsync(async () => await ReloadProfilesFromDiskAsync());
                return;
            }

            _ = ReloadProfilesFromDiskAsync();
        }

        private async Task ReloadProfilesFromDiskAsync()
        {
            if (_isHostTeardown)
            {
                return;
            }

            try
            {
                await LoadProfilesAsync();
            }
            catch (Exception ex)
            {
                AddLog($"Profile reload failed: {ex.Message}");
            }
        }

        private async void DeployRemoteWorkspaceControl_Unloaded(object sender, RoutedEventArgs e)
        {
            // Reparenting and leaving Deploy for another app page also fire Unloaded.
            // Keep FTP alive unless the host asked for a real teardown (project switch / exit).
            if (!_isHostTeardown)
            {
                return;
            }

            UnbindEditorWebViewEvents();
            ConfigurationService.ConnectionsChanged -= OnConnectionsChanged;

            await DisconnectIfConnectedAsync();
        }

        public async void Initialize(ProjectConfig? projectConfig)
        {
            _projectConfig = projectConfig ?? new ProjectConfig();
            _autoConnectAttempted = false;
            _autoConnectInProgress = false;
            _lastAutoConnectProfileId = string.Empty;
            _ = WarmupEditorInBackgroundAsync("Page entered");
            await LoadProfilesAsync();
            _ = TryAutoConnectAsync();
        }

        private void ShowBrowserMode(bool notify = true)
        {
            _isEditorOpen = false;
            EditorKeyboardScope.DisarmMonaco(EditorWebView);
            RestoreEditorPanelToDock();
            RemoteBrowserPanel.Visibility = Visibility.Visible;
            RemoteEditorPanel.Visibility = Visibility.Collapsed;
            OperationLogContainer.Visibility = Visibility.Visible;
            ConnectionPanel.Visibility = Visibility.Visible;
            ApplyWorkspacePanelLayout(editorMode: false);
            ResetUploadFeedback();

            if (notify)
            {
                EditorModeChanged?.Invoke(this, new RemoteEditorModeChangedEventArgs(false, _editSession?.FilePath ?? string.Empty));
            }
        }

        private void ShowEditorMode(bool notify = true)
        {
            _isEditorOpen = true;
            // Keep FTP browser layout normal in the right dock; editor is hosted in the center by DeployPage.
            RemoteBrowserPanel.Visibility = Visibility.Visible;
            RemoteEditorPanel.Visibility = Visibility.Visible;
            OperationLogContainer.Visibility = Visibility.Visible;
            ConnectionPanel.Visibility = Visibility.Visible;
            ApplyWorkspacePanelLayout(editorMode: false);

            if (notify)
            {
                EditorModeChanged?.Invoke(this, new RemoteEditorModeChangedEventArgs(true, _editSession?.FilePath ?? string.Empty));
            }
        }

        public void HostEditorIn(Decorator host)
        {
            if (host == null)
            {
                return;
            }

            DetachFrameworkElement(RemoteEditorPanel);
            host.Child = RemoteEditorPanel;
            RemoteEditorPanel.Visibility = Visibility.Visible;
            RemoteEditorPanel.Margin = new Thickness(0);
            RemoteEditorPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            RemoteEditorPanel.VerticalAlignment = VerticalAlignment.Stretch;
            ApplyWorkspacePanelLayout(editorMode: false);
        }

        public void RestoreEditorPanelToDock()
        {
            if (ReferenceEquals(RemoteEditorPanel.Parent, WorkspaceBodyGrid))
            {
                return;
            }

            DetachFrameworkElement(RemoteEditorPanel);
            WorkspaceBodyGrid.Children.Add(RemoteEditorPanel);
            RemoteEditorPanel.Margin = new Thickness(0);
            RemoteEditorPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            RemoteEditorPanel.VerticalAlignment = VerticalAlignment.Stretch;
        }

        private static void DetachFrameworkElement(FrameworkElement element)
        {
            switch (element.Parent)
            {
                case System.Windows.Controls.Panel panel:
                    panel.Children.Remove(element);
                    break;
                case Decorator decorator:
                    decorator.Child = null;
                    break;
                case ContentControl contentControl:
                    contentControl.Content = null;
                    break;
            }
        }

        private void ApplyWorkspacePanelLayout(bool editorMode)
        {
            if (!editorMode)
            {
                Grid.SetRow(WorkspaceBodyGrid, 1);
                Grid.SetRowSpan(WorkspaceBodyGrid, 1);

                ConnectionPanel.Width = double.NaN;
                ConnectionPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
                ConnectionPanel.Margin = new Thickness(0, 0, 0, 10);

                RemoteBrowserPanel.Width = double.NaN;
                RemoteBrowserPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
                RemoteBrowserPanel.Margin = new Thickness(0);

                OperationLogContainer.Width = double.NaN;
                OperationLogContainer.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
                OperationLogContainer.Margin = new Thickness(0, 10, 0, 0);

                RemoteEditorPanel.Margin = new Thickness(0);
                RemoteEditorPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
                return;
            }

            // Let the editor body occupy full height; right sidebar cards stay overlaid on top/bottom.
            Grid.SetRow(WorkspaceBodyGrid, 0);
            Grid.SetRowSpan(WorkspaceBodyGrid, 3);

            ConnectionPanel.Width = EditorSidebarWidth;
            ConnectionPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            ConnectionPanel.Margin = new Thickness(0, 0, 0, 10);

            RemoteBrowserPanel.Width = EditorSidebarWidth;
            RemoteBrowserPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            RemoteBrowserPanel.Margin = new Thickness(0);

            OperationLogContainer.Width = EditorSidebarWidth;
            OperationLogContainer.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            OperationLogContainer.Margin = new Thickness(0, 10, 0, 0);

            RemoteEditorPanel.Margin = new Thickness(0, 0, EditorSidebarWidth + EditorSidebarGap, 0);
            RemoteEditorPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        }

        private async Task EnsureEditorHtmlTemplateAsync()
        {
            if (!string.IsNullOrEmpty(_codeEditorHtmlTemplate))
            {
                return;
            }

            var htmlPath = Path.Combine(AppContext.BaseDirectory, "Resources", "CodeViewer.html");
            if (File.Exists(htmlPath))
            {
                _codeEditorHtmlTemplate = await File.ReadAllTextAsync(htmlPath);
                return;
            }

            var assembly = typeof(DeployRemoteWorkspaceControl).Assembly;
            using var stream = assembly.GetManifestResourceStream("GitDeployPro.Resources.CodeViewer.html");
            if (stream == null)
            {
                throw new FileNotFoundException("Code editor template not found.", htmlPath);
            }

            using var reader = new StreamReader(stream);
            _codeEditorHtmlTemplate = await reader.ReadToEndAsync();
        }

        private async Task EnsureEditorHostAsync(bool allowRecovery = false, int waitMs = 1200)
        {
            if (_editorUsingFallback && !allowRecovery)
            {
                return;
            }

            if (!_editorUsingFallback)
            {
                EditorWebView.Visibility = Visibility.Visible;
                EditorFallbackTextBox.Visibility = Visibility.Collapsed;
            }
            else if (allowRecovery)
            {
                // Keep the simple editor visible, but allow WebView2/Monaco to initialize behind it.
                EditorWebView.Visibility = Visibility.Visible;
            }

            try
            {
                await EnsureEditorHtmlTemplateAsync();
                await EditorWebView.EnsureCoreWebView2Async().WaitAsync(TimeSpan.FromMilliseconds(Math.Max(200, waitMs)));
                if (!_editorWebEventsBound && EditorWebView.CoreWebView2 != null)
                {
                    EditorWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                    EditorWebView.CoreWebView2.WebMessageReceived += EditorWebView_WebMessageReceived;
                    EditorWebView.CoreWebView2.NavigationCompleted += EditorWebView_NavigationCompleted;
                    _editorWebEventsBound = true;
                }

                if (!_editorWebReady && EditorWebView.CoreWebView2 != null)
                {
                    ApplyFtpChromeTheme();
                    EditorWebView.CoreWebView2.NavigateToString(_codeEditorHtmlTemplate ?? "<html><body>Editor template missing.</body></html>");
                }
            }
            catch (TimeoutException)
            {
                if (!allowRecovery)
                {
                    EnableEditorFallback("Code editor initialization timed out. Fallback editor is active.");
                }
            }
            catch (Exception ex)
            {
                if (!allowRecovery)
                {
                    EnableEditorFallback($"Code editor fallback active: {ex.Message}");
                }
            }
        }

        private async void EditorWebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                EnableEditorFallback("Code editor failed to initialize. Fallback editor is active.");
                return;
            }

            _editorWebReady = true;
            await SetEditorEditableAsync(true);
            await ApplyEditorFontSizeAsync();
            await ApplyEditorThemeAsync();

            if (_editorUsingFallback)
            {
                await PromoteFallbackToWebEditorAsync();
                return;
            }

            if (_editSession != null)
            {
                await LoadEditorContentAsync(_editSession.FilePath, _editSession.WorkingContent);
            }
        }

        private async void EditorWebView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var message = JsonSerializer.Deserialize<WebMessage>(e.WebMessageAsJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (message?.Type == "ready")
                {
                    _editorWebReady = true;
                    await SetEditorEditableAsync(true);
                    await ApplyEditorFontSizeAsync();
                    await EnableEditorRemoteRefreshMenuAsync();

                    if (_editorUsingFallback)
                    {
                        await PromoteFallbackToWebEditorAsync();
                        return;
                    }

                    if (_editSession != null)
                    {
                        await LoadEditorContentAsync(_editSession.FilePath, _editSession.WorkingContent);
                    }

                    return;
                }

                if (string.Equals(message?.Type, "refreshFromRemote", StringComparison.OrdinalIgnoreCase))
                {
                    _ = RefreshActiveEditorFromRemoteAsync();
                    return;
                }

                if (message?.Type == "dirty" && !_suppressWebDirtySignal)
                {
                    await SyncDirtyStateFromEditorAsync();
                }
            }
            catch
            {
                // Ignore malformed editor messages.
            }
        }

        private void EnableEditorFallback(string reason)
        {
            if (!_editorUsingFallback)
            {
                AddLog(reason);
            }

            _editorUsingFallback = true;
            _editorWebReady = false;
            EditorWebView.Visibility = Visibility.Collapsed;
            EditorFallbackTextBox.Visibility = Visibility.Visible;
            _lastEditorRecoveryAttemptUtc = DateTime.MinValue;
            // Retry promotion more aggressively after each open while fallback is active.
            _ = TryRecoverEditorHostAsync();
        }

        private async Task TryRecoverEditorHostAsync()
        {
            if (!_editorUsingFallback || _editorRecoveryInProgress)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (now - _lastEditorRecoveryAttemptUtc < TimeSpan.FromSeconds(2))
            {
                return;
            }

            _editorRecoveryInProgress = true;
            _lastEditorRecoveryAttemptUtc = now;
            try
            {
                await EnsureEditorHostAsync(allowRecovery: true, waitMs: 8000);
                // Give navigation/ready message a moment, then promote if possible.
                var waitStart = DateTime.UtcNow;
                while (!_editorWebReady && DateTime.UtcNow - waitStart < TimeSpan.FromSeconds(8))
                {
                    await Task.Delay(120);
                }

                if (_editorWebReady)
                {
                    await PromoteFallbackToWebEditorAsync();
                }
            }
            catch
            {
                // Keep fallback editor active.
            }
            finally
            {
                _editorRecoveryInProgress = false;
            }
        }

        private async Task WarmupEditorInBackgroundAsync(string reason)
        {
            if (_editorWebReady || _editorWarmupInProgress)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (now - _lastEditorWarmupAttemptUtc < TimeSpan.FromSeconds(6))
            {
                return;
            }

            _editorWarmupInProgress = true;
            _lastEditorWarmupAttemptUtc = now;
            try
            {
                await EnsureEditorHostAsync(allowRecovery: true, waitMs: 12000);
                if (_editorWebReady && !_editorWarmupLoggedReady)
                {
                    AddLog($"Code editor preloaded in background ({reason}).");
                    _editorWarmupLoggedReady = true;
                }
            }
            catch
            {
                // Warmup is best-effort only. Runtime fallback remains available.
            }
            finally
            {
                _editorWarmupInProgress = false;
            }
        }

        private async Task PromoteFallbackToWebEditorAsync()
        {
            if (!_editorUsingFallback || !_editorWebReady || EditorWebView?.CoreWebView2 == null)
            {
                return;
            }

            _editorUsingFallback = false;
            EditorFallbackTextBox.Visibility = Visibility.Collapsed;
            EditorWebView.Visibility = Visibility.Visible;

            if (_editSession == null)
            {
                return;
            }

            var fallbackBuffer = EditorFallbackTextBox.Text ?? _editSession.WorkingContent ?? string.Empty;
            _editSession.WorkingContent = fallbackBuffer;
            _editSession.IsDirty = !string.Equals(fallbackBuffer, _editSession.Content, StringComparison.Ordinal);

            await LoadEditorContentAsync(_editSession.FilePath, _editSession.WorkingContent);
            ApplyEditorDirtyState(_editSession.IsDirty);
            AddLog($"Code editor ready. Monaco enabled for {_editSession.FileName}.");
        }

        private async Task SyncDirtyStateFromEditorAsync()
        {
            if (_editSession == null)
            {
                return;
            }

            var content = await GetCurrentEditorContentAsync();
            _editSession.WorkingContent = content;
            ApplyEditorDirtyState(!string.Equals(content, _editSession.Content, StringComparison.Ordinal));
        }

        private async Task LoadEditorContentAsync(string filePath, string content)
        {
            if (_editorUsingFallback)
            {
                _suppressFallbackTextChanged = true;
                EditorFallbackTextBox.Text = content ?? string.Empty;
                _suppressFallbackTextChanged = false;
                _ = TryRecoverEditorHostAsync();
                return;
            }

            await EnsureEditorHostAsync(waitMs: 1200);
            if (_editorUsingFallback)
            {
                _suppressFallbackTextChanged = true;
                EditorFallbackTextBox.Text = content ?? string.Empty;
                _suppressFallbackTextChanged = false;
                _ = TryRecoverEditorHostAsync();
                return;
            }

            if (!_editorWebReady)
            {
                var waitStart = DateTime.UtcNow;
                while (!_editorWebReady && DateTime.UtcNow - waitStart < TimeSpan.FromMilliseconds(500))
                {
                    await Task.Delay(60);
                }
            }

            if (!_editorWebReady || EditorWebView?.CoreWebView2 == null)
            {
                EnableEditorFallback("Code editor was not ready in time. Fallback editor is active.");
                _suppressFallbackTextChanged = true;
                EditorFallbackTextBox.Text = content ?? string.Empty;
                _suppressFallbackTextChanged = false;
                _ = TryRecoverEditorHostAsync();
                return;
            }

            var payload = JsonSerializer.Serialize(new
            {
                type = "load",
                filePath,
                content = content ?? string.Empty
            });

            _suppressWebDirtySignal = true;
            try
            {
                await EditorWebView.CoreWebView2.ExecuteScriptAsync($"window.__loadCode && window.__loadCode({payload});");
                await SetEditorEditableAsync(true);
                await EditorWebView.CoreWebView2.ExecuteScriptAsync("window.__markClean && window.__markClean();");
                await EditorWebView.CoreWebView2.ExecuteScriptAsync("window.__focusEditor && window.__focusEditor();");
            }
            catch (Exception ex)
            {
                EnableEditorFallback($"Code editor runtime issue: {ex.Message}");
                _suppressFallbackTextChanged = true;
                EditorFallbackTextBox.Text = content ?? string.Empty;
                _suppressFallbackTextChanged = false;
            }
            finally
            {
                _suppressWebDirtySignal = false;
            }
        }

        private async Task<string> GetCurrentEditorContentAsync()
        {
            if (_editorUsingFallback)
            {
                return EditorFallbackTextBox.Text ?? string.Empty;
            }

            await EnsureEditorHostAsync(waitMs: 800);
            if (_editorUsingFallback || !_editorWebReady || EditorWebView?.CoreWebView2 == null)
            {
                return EditorFallbackTextBox.Text ?? string.Empty;
            }

            try
            {
                var scriptTask = EditorWebView.CoreWebView2.ExecuteScriptAsync("window.__getValue && window.__getValue()");
                var completed = await Task.WhenAny(scriptTask, Task.Delay(TimeSpan.FromSeconds(2)));
                if (completed != scriptTask)
                {
                    return EditorFallbackTextBox.Text ?? string.Empty;
                }

                var scriptResult = await scriptTask;
                return string.IsNullOrWhiteSpace(scriptResult) || scriptResult == "null"
                    ? (EditorFallbackTextBox.Text ?? string.Empty)
                    : JsonSerializer.Deserialize<string>(scriptResult) ?? string.Empty;
            }
            catch
            {
                return EditorFallbackTextBox.Text ?? string.Empty;
            }
        }

        private async Task SetEditorEditableAsync(bool enabled)
        {
            if (_editorUsingFallback)
            {
                EditorFallbackTextBox.IsReadOnly = !enabled;
                return;
            }

            if (!_editorWebReady || EditorWebView?.CoreWebView2 == null)
            {
                return;
            }

            await EditorWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.__setEditable && window.__setEditable({enabled.ToString().ToLowerInvariant()});");
        }

        private async Task ApplyEditorFontSizeAsync()
        {
            EditorFallbackTextBox.FontSize = _editorFontSize;

            if (_editorUsingFallback || !_editorWebReady || EditorWebView?.CoreWebView2 == null)
            {
                return;
            }

            try
            {
                await EditorWebView.CoreWebView2.ExecuteScriptAsync(
                    $"window.__setFontSize && window.__setFontSize({_editorFontSize});");
            }
            catch
            {
                // Keep current size if editor script is not ready yet.
            }
        }

        private async Task MarkEditorCleanAsync()
        {
            if (_editorUsingFallback)
            {
                return;
            }

            if (!_editorWebReady || EditorWebView?.CoreWebView2 == null)
            {
                return;
            }

            await EditorWebView.CoreWebView2.ExecuteScriptAsync("window.__markClean && window.__markClean();");
        }

        private void ApplyEditorDirtyState(bool isDirty)
        {
            if (_editSession == null)
            {
                return;
            }

            _editSession.IsDirty = isDirty;
            if (isDirty)
            {
                ResetUploadFeedback();
            }
            EditorStatusText.Text = _editSession.IsDirty
                ? "Modified locally. Save/Upload to apply."
                : "No local changes.";
            UpdateUiState();
        }

        public void NotifyProjectConfigChanged(ProjectConfig? projectConfig = null)
        {
            if (projectConfig != null)
            {
                _projectConfig = projectConfig;
            }

            _ = LoadProfilesAsync();
        }

        private async Task LoadProfilesAsync()
        {
            var previousSelectedId = GetComboSelectedProfile()?.Id
                ?? _currentProfile?.Id;

            var profiles = _configService.LoadConnections()
                .Where(IsDeployRemoteProfile)
                .ToList();
            var defaultId = ProjectFtpAssignments.GetDefaultId(_projectConfig);
            var items = profiles
                .Select(p => new FtpProfileListItem(
                    p,
                    isProjectDefault: string.Equals(p.Id, defaultId, StringComparison.OrdinalIgnoreCase),
                    isAssigned: ProjectFtpAssignments.IsAssigned(_projectConfig, p.Id)))
                .OrderByDescending(p => p.IsProjectDefault)
                .ThenByDescending(p => p.IsFavorite)
                .ThenByDescending(p => p.IsAssigned)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _suppressConnectionSelectionChanged = true;
            try
            {
                ConnectionComboBox.ItemsSource = items;
                if (items.Count == 0)
                {
                    ConnectionComboBox.SelectedItem = null;
                    _currentProfile = null;
                    SetStatus("No FTP/SFTP profile found. Create one in Connection Manager.", warning: true);
                    UpdateChangeDefaultButton();
                    return;
                }

                var keepCurrent = items.FirstOrDefault(p =>
                    !string.IsNullOrWhiteSpace(previousSelectedId) &&
                    string.Equals(p.Profile.Id, previousSelectedId, StringComparison.OrdinalIgnoreCase));
                var preferred = items.FirstOrDefault(p => p.IsProjectDefault);

                // Keep the currently selected/connected profile in place. Do not jump it to the top.
                // First load (nothing selected yet) still prefers the project default.
                var selected = keepCurrent ?? preferred;
                ConnectionComboBox.SelectedItem = selected;
                _currentProfile = selected?.Profile;

                if (selected == null && string.IsNullOrWhiteSpace(defaultId))
                {
                    SetStatus("No FTP profile assigned to this project. Select one to connect.", warning: false);
                }
            }
            finally
            {
                _suppressConnectionSelectionChanged = false;
                UpdateChangeDefaultButton();
            }

            if (items.Count == 0)
            {
                return;
            }

            if (_remoteService != null && _remoteService.IsConnected &&
                (_currentProfile == null ||
                 !string.Equals(_remoteService.ProfileId, _currentProfile.Id, StringComparison.OrdinalIgnoreCase)))
            {
                await DisconnectAsync();
            }
        }

        private void ConnectionComboBox_DropDownOpened(object sender, EventArgs e)
        {
            // WPF scrolls the dropdown to the selected (often connected) item, hiding default/favorites above it.
            // Keep list order stable and always open from the top so those stay visible.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Dispatcher.BeginInvoke(new Action(ScrollFtpComboDropdownToTop), DispatcherPriority.ContextIdle);
            }), DispatcherPriority.Loaded);
        }

        private void ScrollFtpComboDropdownToTop()
        {
            if (ConnectionComboBox == null || !ConnectionComboBox.IsDropDownOpen)
            {
                return;
            }

            var popup = ConnectionComboBox.Template?.FindName("PART_Popup", ConnectionComboBox) as System.Windows.Controls.Primitives.Popup;
            var scrollViewer = FindDescendant<ScrollViewer>(popup?.Child);
            if (scrollViewer != null)
            {
                scrollViewer.ScrollToHome();
                return;
            }

            if (ConnectionComboBox.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement first)
            {
                first.BringIntoView();
            }
        }

        private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
        {
            if (root == null)
            {
                return null;
            }

            if (root is T match)
            {
                return match;
            }

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var found = FindDescendant<T>(VisualTreeHelper.GetChild(root, i));
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private ConnectionProfile? GetComboSelectedProfile()
        {
            return (ConnectionComboBox.SelectedItem as FtpProfileListItem)?.Profile;
        }

        private void SelectComboProfile(string? profileId)
        {
            if (string.IsNullOrWhiteSpace(profileId))
            {
                return;
            }

            var match = ConnectionComboBox.Items
                .OfType<FtpProfileListItem>()
                .FirstOrDefault(p => string.Equals(p.Profile.Id, profileId, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                ConnectionComboBox.SelectedItem = match;
            }
        }

        private void UpdateChangeDefaultButton()
        {
            if (ChangeDefaultFtpButton == null)
            {
                return;
            }

            var assignedCount = ProjectFtpAssignments.GetAssignedIds(_projectConfig).Count;
            ChangeDefaultFtpButton.IsEnabled = !_isBusy && assignedCount > 1;
        }

        private void ChangeDefaultFtpButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
            {
                return;
            }

            var assigned = ProjectFtpAssignments.ResolveAssignedProfiles(
                _projectConfig,
                _configService.LoadConnections());
            if (assigned.Count < 2)
            {
                return;
            }

            if (!ProjectFtpTargetWindow.TryPick(
                    Window.GetWindow(this),
                    assigned,
                    ProjectFtpAssignments.GetDefaultId(_projectConfig),
                    out var selectedId))
            {
                return;
            }

            ProjectFtpAssignments.SetDefault(_projectConfig, selectedId, confirmed: true);
            var defaultProfile = assigned.FirstOrDefault(p =>
                string.Equals(p.Id, selectedId, StringComparison.OrdinalIgnoreCase));
            ProjectFtpAssignments.CopyLegacyFields(_projectConfig, defaultProfile);
            _configService.SaveProjectConfig(_projectConfig);
            NotifyProjectConfigChanged();
        }

        private bool HasAssignedProjectProfile()
        {
            var defaultId = ProjectFtpAssignments.GetDefaultId(_projectConfig);
            return !string.IsNullOrWhiteSpace(defaultId)
                   && _currentProfile != null
                   && string.Equals(
                       _currentProfile.Id,
                       defaultId,
                       StringComparison.OrdinalIgnoreCase);
        }

        private async Task TryAutoConnectAsync()
        {
            // Only auto-connect the project's assigned FTP/SFTP profile — never the first profile in the list.
            if (_autoConnectInProgress || _currentProfile == null || !HasAssignedProjectProfile())
            {
                return;
            }

            if (_remoteService != null &&
                _remoteService.IsConnected &&
                string.Equals(_remoteService.ProfileId, _currentProfile.Id, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_autoConnectAttempted &&
                string.Equals(_lastAutoConnectProfileId, _currentProfile.Id, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _autoConnectInProgress = true;
            _autoConnectAttempted = true;
            _lastAutoConnectProfileId = _currentProfile.Id;

            try
            {
                SetStatus($"Auto-connecting to {_currentProfile.Name}...", warning: false);
                AddLog($"Auto-connect attempt for {_currentProfile.Name}...");
                await ConnectAsync(_currentProfile, showDialogOnError: false, isAutoConnect: true);
            }
            finally
            {
                _autoConnectInProgress = false;
            }
        }

        private bool IsDeployRemoteProfile(ConnectionProfile profile)
        {
            return ConnectionProfileFilters.IsRemoteFileProfile(profile);
        }

        private static bool ShouldRetryRemoteOperation(Exception ex)
        {
            var message = ex.Message?.ToLowerInvariant() ?? string.Empty;
            return message.Contains("not connected", StringComparison.Ordinal) ||
                   message.Contains("connection refused", StringComparison.Ordinal) ||
                   message.Contains("connection reset", StringComparison.Ordinal) ||
                   message.Contains("build data connection", StringComparison.Ordinal) ||
                   message.Contains("425", StringComparison.Ordinal);
        }

        private async Task<bool> ReconnectSilentlyAsync()
        {
            if (_currentProfile == null)
            {
                return false;
            }

            try
            {
                if (_remoteService != null)
                {
                    await _remoteService.DisconnectAsync();
                }
            }
            catch
            {
                // Ignore cleanup failure before reconnect.
            }

            try
            {
                _remoteService = _currentProfile.UseSSH
                    ? new SftpRemoteFileService()
                    : new FtpRemoteFileService();
                await _remoteService.ConnectAsync(_currentProfile);
                if (_remoteService == null || !_remoteService.IsConnected)
                {
                    throw new InvalidOperationException("Remote service reported disconnected after connect.");
                }

                var root = RemotePathResolver.BuildRemoteRoot(_currentProfile);
                await _remoteService.ListDirectoryAsync(root);
                AddLog("Connection restored.");
                SetStatus($"Connection restored: {_currentProfile.Name}", success: true);
                return true;
            }
            catch (Exception reconnectEx)
            {
                await DisposeRemoteServiceQuietlyAsync();
                AddLog($"Reconnect failed: {reconnectEx.Message}");
                SetStatus($"Reconnect failed: {FormatConnectionFailureMessage(reconnectEx)}", warning: true);
                return false;
            }
        }

        private async Task<T> ExecuteRemoteAsync<T>(Func<IRemoteFileService, Task<T>> operation, string operationLabel)
        {
            if (_remoteService == null || _currentProfile == null)
            {
                throw new InvalidOperationException("Remote connection is not ready.");
            }

            await _remoteCommandLock.WaitAsync();
            try
            {
                try
                {
                    return await operation(_remoteService);
                }
                catch (Exception ex) when (ShouldRetryRemoteOperation(ex))
                {
                    AddLog($"{operationLabel} failed. Reconnecting...");
                    var reconnected = await ReconnectSilentlyAsync();
                    if (!reconnected || _remoteService == null)
                    {
                        throw;
                    }

                    return await operation(_remoteService);
                }
            }
            finally
            {
                _remoteCommandLock.Release();
            }
        }

        private async Task ExecuteRemoteAsync(Func<IRemoteFileService, Task> operation, string operationLabel)
        {
            await ExecuteRemoteAsync(
                async service =>
                {
                    await operation(service);
                    return true;
                },
                operationLabel);
        }

        private async void ConnectionToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
            {
                return;
            }

            if (_remoteService != null && _remoteService.IsConnected)
            {
                await DisconnectAsync();
                return;
            }

            if (GetComboSelectedProfile() is not ConnectionProfile profile)
            {
                ModernMessageBox.Show("Select a connection profile first.", "Remote workspace", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await ConnectAsync(profile, showDialogOnError: true, isAutoConnect: false);
        }

        private async void EditConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
            {
                return;
            }

            var manager = new ConnectionManagerWindow(ProjectFtpAssignments.GetDefaultId(_projectConfig))
            {
                Owner = Window.GetWindow(this)
            };
            manager.ShowDialog();

            var previousId = GetComboSelectedProfile()?.Id;
            await LoadProfilesAsync();

            if (!string.IsNullOrWhiteSpace(previousId))
            {
                SelectComboProfile(previousId);
            }

            if (_remoteService == null || !_remoteService.IsConnected)
            {
                _ = TryAutoConnectAsync();
            }
        }

        private async Task ConnectAsync(ConnectionProfile profile, bool showDialogOnError, bool isAutoConnect)
        {
            ResetConnectCancellation();
            var generation = ++_connectGeneration;
            _connectCts = new CancellationTokenSource();
            var token = _connectCts.Token;

            _isBusy = true;
            BeginRemoteLoading(
                isAutoConnect ? "Auto-connecting..." : "Connecting...",
                $"Preparing remote workspace for {profile.Name}...",
                canCancel: true);
            SetStatus(
                isAutoConnect
                    ? $"Auto-connecting to {profile.Name}..."
                    : $"Connecting to {profile.Name} ({profile.Host})...",
                warning: false,
                success: false);
            UpdateUiState();
            try
            {
                if (_remoteService != null)
                {
                    AbortRemoteService();
                }

                token.ThrowIfCancellationRequested();
                ThrowIfConnectStale(generation);

                _remoteService = profile.UseSSH
                    ? new SftpRemoteFileService()
                    : new FtpRemoteFileService();
                await _remoteService.ConnectAsync(profile, token);
                ThrowIfConnectStale(generation);
                if (_remoteService == null || !_remoteService.IsConnected)
                {
                    throw new InvalidOperationException("Remote service reported disconnected after connect.");
                }

                token.ThrowIfCancellationRequested();

                _currentProfile = profile;
                AddLog(isAutoConnect
                    ? $"Transport connected using {(profile.UseSSH ? "SFTP" : "FTP")}; verifying listing..."
                    : $"Transport connected using {(profile.UseSSH ? "SFTP" : "FTP")}; verifying listing...");
                _ = WarmupEditorInBackgroundAsync("FTP connected");
                await LoadRootAsync(token);
                ThrowIfConnectStale(generation);

                if (_remoteService == null || !_remoteService.IsConnected)
                {
                    throw new InvalidOperationException("Remote session closed while loading directory listing.");
                }

                token.ThrowIfCancellationRequested();

                SetStatus(
                    isAutoConnect
                        ? $"Auto-connected to {profile.Name} ({profile.Host})."
                        : $"Connected to {profile.Name} ({profile.Host}).",
                    success: true);
            }
            catch (Exception ex) when (IsConnectCanceled(ex, token) || generation != _connectGeneration)
            {
                AbortRemoteService();
                if (generation == _connectGeneration)
                {
                    SetStatus("Connection cancelled.", warning: true);
                    AddLog("Connection cancelled by user.");
                }
            }
            catch (Exception ex)
            {
                AbortRemoteService();
                if (generation != _connectGeneration)
                {
                    return;
                }
                var failureMessage = FormatConnectionFailureMessage(ex);
                SetStatus(
                    isAutoConnect
                        ? $"Auto-connect failed: {failureMessage}"
                        : $"Connection failed: {failureMessage}",
                    warning: true);
                AddLog(isAutoConnect
                    ? $"Auto-connect failed: {ex.Message}"
                    : $"Connection failed: {ex.Message}");

                if (showDialogOnError)
                {
                    ModernMessageBox.Show($"Failed to connect:\n{failureMessage}", "Remote workspace", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                if (generation == _connectGeneration)
                {
                    _isBusy = false;
                    ResetConnectCancellation();
                    EndRemoteLoading();
                    UpdateUiState();
                }
            }
        }

        private void CancelRemoteLoadingButton_Click(object sender, RoutedEventArgs e)
        {
            if (_connectCts == null || _connectCts.IsCancellationRequested)
            {
                return;
            }

            _connectGeneration++;
            if (CancelRemoteLoadingButton != null)
            {
                CancelRemoteLoadingButton.IsEnabled = false;
            }

            try
            {
                _connectCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Connect finished while the click was processed.
            }

            AbortRemoteService();
            _isBusy = false;
            ForceCloseRemoteLoading();
            SetStatus("Connection cancelled.", warning: true);
            AddLog("Connection cancelled by user.");
            UpdateUiState();
        }

        private void ThrowIfConnectStale(int generation)
        {
            if (generation != _connectGeneration)
            {
                throw new OperationCanceledException();
            }
        }

        private void AbortRemoteService()
        {
            var service = _remoteService;
            _remoteService = null;
            if (service == null)
            {
                return;
            }

            try
            {
                service.Abort();
            }
            catch
            {
                // Ignore abort races.
            }
        }

        private void ForceCloseRemoteLoading()
        {
            _remoteLoadingDepth = 0;
            if (CancelRemoteLoadingButton != null)
            {
                CancelRemoteLoadingButton.Visibility = Visibility.Collapsed;
                CancelRemoteLoadingButton.IsEnabled = true;
            }

            UpdateRemoteBrowserVisualState();
        }

        private void ResetConnectCancellation()
        {
            var cts = _connectCts;
            _connectCts = null;
            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Dispose();
            }
            catch
            {
                // Ignore dispose races with a late cancel click.
            }
        }

        private static bool IsConnectCanceled(Exception ex, CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return true;
            }

            return ex is OperationCanceledException;
        }

        private async Task DisposeRemoteServiceQuietlyAsync()
        {
            if (_remoteService == null)
            {
                return;
            }

            try
            {
                await _remoteService.DisconnectAsync();
            }
            catch
            {
                // Ignore cleanup failures after a failed connect.
            }
            finally
            {
                _remoteService = null;
            }
        }

        private static string FormatConnectionFailureMessage(Exception ex)
        {
            var message = ex.Message ?? string.Empty;
            if (ex is OperationCanceledException ||
                message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("unable to connect", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("no route to host", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("network is unreachable", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("actively refused", StringComparison.OrdinalIgnoreCase))
            {
                return $"{message} Host unreachable — check VPN/network.";
            }

            return message;
        }

        private async Task DisconnectAsync()
        {
            _isBusy = true;
            UpdateUiState();
            try
            {
                if (_remoteService != null)
                {
                    await _remoteService.DisconnectAsync();
                }

                _remoteService = null;
                RootNodes.Clear();
                _suppressFallbackTextChanged = true;
                EditorFallbackTextBox.Text = string.Empty;
                _suppressFallbackTextChanged = false;
                EditorFallbackTextBox.IsEnabled = false;
                OpenSessions.Clear();
                _editSession = null;
                _suppressTabSelectionChanged = true;
                EditorTabsListBox.SelectedItem = null;
                _suppressTabSelectionChanged = false;
                EditorPathText.Text = "Select a remote file to edit";
                EditorStatusText.Text = "Disconnected.";
                ShowBrowserMode();
                SetStatus("Disconnected.", warning: false);
                AddLog("Disconnected.");
                UpdateRemoteBrowserVisualState();
            }
            finally
            {
                _isBusy = false;
                UpdateUiState();
            }
        }

        private async void CloseEditorOverlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (!await TryCloseEditorViewAsync(promptUnsaved: true))
            {
                return;
            }
        }

        public async Task<bool> TryCloseEditorViewAsync(bool promptUnsaved)
        {
            if (promptUnsaved)
            {
                await CaptureActiveSessionBufferAsync();
                var dirtySessions = OpenSessions.Where(session => session.IsDirty).ToList();
                if (dirtySessions.Count > 0)
                {
                    var result = ModernMessageBox.ShowWithResult(
                        dirtySessions.Count == 1
                            ? "There are unsaved remote changes. Upload before closing editor?"
                            : $"There are {dirtySessions.Count} tabs with unsaved remote changes. Upload all before closing editor?",
                        "Unsaved changes",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Warning,
                        primaryText: dirtySessions.Count == 1 ? "Upload & Close" : "Upload All & Close",
                        secondaryText: "Close Without Upload",
                        cancelText: "Keep Editing");

                    if (result == MessageBoxResult.Cancel || result == MessageBoxResult.None)
                    {
                        return false;
                    }

                    if (result == MessageBoxResult.Yes)
                    {
                        foreach (var dirtySession in dirtySessions)
                        {
                            await ActivateSessionAsync(dirtySession, captureCurrentBuffer: true);
                            var uploadSucceeded = await SaveUploadCurrentEditorFileAsync();
                            if (!uploadSucceeded)
                            {
                                ModernMessageBox.Show(
                                    $"Upload failed for '{dirtySession.FileName}'. Editor stays open so you can retry.",
                                    "Upload failed",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Warning);
                                return false;
                            }
                        }
                    }
                }
            }

            OpenSessions.Clear();
            _editSession = null;
            SyncTabSelection(null);
            EditorPathText.Text = "Select a remote file to edit";
            EditorStatusText.Text = "No file loaded.";
            ShowBrowserMode();
            UpdateUiState();
            return true;
        }

        private async Task LoadRootAsync(CancellationToken cancellationToken = default)
        {
            if (_remoteService == null || !_remoteService.IsConnected || _currentProfile == null)
            {
                throw new InvalidOperationException("Remote connection is not ready for directory listing.");
            }

            _ = WarmupEditorInBackgroundAsync("FTP listing started");
            var root = RemotePathResolver.BuildRemoteRoot(_currentProfile);
            BeginRemoteLoading(
                "Loading remote files...",
                $"Fetching {root}",
                canCancel: _connectCts != null && !_connectCts.IsCancellationRequested);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entries = await ExecuteRemoteAsync(
                    service => service.ListDirectoryAsync(root, cancellationToken),
                    "Load root");
                cancellationToken.ThrowIfCancellationRequested();
                var nodes = _treeBuilder.BuildNodes(entries);
                PopulateRootTree(root, nodes);
                SetStatus($"Loaded {nodes.Count} item(s) from {root}", success: true);
                AddLog($"Root loaded from {root}");
                UpdateMappingBanner();
            }
            finally
            {
                EndRemoteLoading();
                UpdateRemoteBrowserVisualState();
            }
        }

        /// <summary>
        /// Always wrap remote listing under a clickable root folder so New File/Folder
        /// works at the mapped (or profile) remote root.
        /// </summary>
        private void PopulateRootTree(string remoteRoot, IReadOnlyList<RemoteTreeNode> children)
        {
            var rootNode = _treeBuilder.CreateRootFolderNode(remoteRoot);
            rootNode.Children.Clear();
            foreach (var child in children)
            {
                rootNode.Children.Add(child);
            }

            rootNode.IsLoaded = true;
            rootNode.IsExpanded = true;

            RootNodes.Clear();
            RootNodes.Add(rootNode);
            WireParentLinks(RootNodes);
            ApplyRemoteTreeSearch();

            // Force-expand after the TreeViewItem container exists.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (RootNodes.Count == 0)
                {
                    return;
                }

                RootNodes[0].IsExpanded = true;
                if (RemoteTreeView.ItemContainerGenerator.ContainerFromItem(RootNodes[0]) is TreeViewItem tvi)
                {
                    tvi.IsExpanded = true;
                    tvi.UpdateLayout();
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private RemoteTreeNode? FindNodeByPath(string? remotePath)
        {
            if (string.IsNullOrWhiteSpace(remotePath))
            {
                return null;
            }

            var target = remotePath.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(target))
            {
                target = "/";
            }

            foreach (var node in EnumerateNodes(RootNodes))
            {
                var path = (node.FullPath ?? string.Empty).TrimEnd('/');
                if (string.IsNullOrWhiteSpace(path))
                {
                    path = "/";
                }

                if (string.Equals(path, target, StringComparison.OrdinalIgnoreCase))
                {
                    return node;
                }
            }

            return null;
        }

        /// <summary>
        /// Refresh one folder's children in place (same idea as delete) — no full FTP tree restart.
        /// </summary>
        private async Task RefreshFolderInPlaceAsync(string folderPath)
        {
            var folder = FindNodeByPath(folderPath);
            if (folder == null && RootNodes.Count > 0 && IsBrowseRootNode(RootNodes[0]))
            {
                var rootPath = (RootNodes[0].FullPath ?? string.Empty).TrimEnd('/');
                var wanted = (folderPath ?? string.Empty).TrimEnd('/');
                if (string.IsNullOrWhiteSpace(wanted)) wanted = "/";
                if (string.IsNullOrWhiteSpace(rootPath)) rootPath = "/";
                if (string.Equals(rootPath, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    folder = RootNodes[0];
                }
            }

            if (folder == null)
            {
                // Last resort: refresh browse-root children only.
                if (RootNodes.Count > 0)
                {
                    folder = RootNodes[0];
                }
                else
                {
                    return;
                }
            }

            folder.IsExpanded = true;
            folder.IsSelected = true;
            await LoadChildrenAsync(folder);
            folder.IsExpanded = true;
            folder.IsSelected = true;
            UpdateRemoteBrowserVisualState();
        }

        private async Task RefreshFolderFromUiAsync(RemoteTreeNode node)
        {
            if (_isBusy || _remoteService == null || !_remoteService.IsConnected || node == null)
            {
                return;
            }

            var folder = node.IsDirectory ? node : FindParentNode(node);
            if (folder == null || !folder.IsDirectory)
            {
                return;
            }

            _isBusy = true;
            UpdateUiState();
            try
            {
                await RefreshFolderInPlaceAsync(folder.FullPath);
                SetStatus($"Refreshed {folder.Name}", success: true);
                AddLog($"Refreshed folder {folder.FullPath}");
            }
            catch (Exception ex)
            {
                SetStatus($"Refresh failed: {ex.Message}", warning: true);
                AddLog($"Folder refresh failed: {ex.Message}");
            }
            finally
            {
                _isBusy = false;
                UpdateUiState();
            }
        }

        private bool IsBrowseRootNode(RemoteTreeNode node)
        {
            if (node == null || _currentProfile == null)
            {
                return false;
            }

            var root = RemotePathResolver.BuildRemoteRoot(_currentProfile).TrimEnd('/');
            var path = (node.FullPath ?? string.Empty).TrimEnd('/');
            if (string.IsNullOrWhiteSpace(path))
            {
                path = "/";
            }

            if (string.IsNullOrWhiteSpace(root))
            {
                root = "/";
            }

            return string.Equals(root, path, StringComparison.OrdinalIgnoreCase);
        }

        private async Task LoadChildrenAsync(RemoteTreeNode node)
        {
            if (_remoteService == null || !_remoteService.IsConnected || node == null || !node.IsDirectory)
            {
                return;
            }

            var path = RemotePathResolver.EnsureTrailingSlash(node.FullPath);
            var selectedPath = EnumerateNodes(node.Children).FirstOrDefault(child => child.IsSelected && !child.IsPlaceholder)?.FullPath;
            var entries = await ExecuteRemoteAsync(service => service.ListDirectoryAsync(path), $"Expand {node.Name}");
            var children = _treeBuilder.BuildNodes(entries);
            MergeFolderChildren(node, children);
            node.IsLoaded = true;
            ApplyRemoteTreeSearch();

            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                var restored = FindNodeByPath(selectedPath);
                if (restored != null)
                {
                    restored.IsSelected = true;
                }
                else
                {
                    node.IsSelected = true;
                }
            }
        }

        private static string NormalizeNodePath(string? remotePath)
        {
            var target = (remotePath ?? string.Empty).TrimEnd('/');
            return string.IsNullOrWhiteSpace(target) ? "/" : target;
        }

        /// <summary>
        /// Replace a folder's listing without dropping already-loaded nested folders,
        /// so refresh does not collapse the tree or send the user back to the root.
        /// </summary>
        private static void MergeFolderChildren(RemoteTreeNode folder, IReadOnlyList<RemoteTreeNode> freshChildren)
        {
            var existingByPath = folder.Children
                .Where(child => !child.IsPlaceholder)
                .GroupBy(child => NormalizeNodePath(child.FullPath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var next = new List<RemoteTreeNode>(freshChildren.Count);
            foreach (var fresh in freshChildren)
            {
                var key = NormalizeNodePath(fresh.FullPath);
                if (fresh.IsDirectory
                    && existingByPath.TryGetValue(key, out var existing)
                    && existing.IsDirectory
                    && existing.IsLoaded)
                {
                    existing.Name = fresh.Name;
                    existing.SizeBytes = fresh.SizeBytes;
                    existing.SizeLabel = fresh.SizeLabel;
                    existing.ModifiedLabel = fresh.ModifiedLabel;
                    next.Add(existing);
                }
                else
                {
                    next.Add(fresh);
                }
            }

            folder.Children.Clear();
            foreach (var child in next)
            {
                folder.Children.Add(child);
            }

            WireParentLinks(folder.Children, folder);
        }

        private static void WireParentLinks(IEnumerable<RemoteTreeNode> nodes, RemoteTreeNode? parent = null)
        {
            foreach (var node in nodes)
            {
                node.Parent = parent;
                if (node.Children.Count > 0)
                {
                    WireParentLinks(node.Children, node);
                }
            }
        }

        private async Task RefreshSessionOriginalStatAsync(RemoteEditSession session)
        {
            if (session == null || _remoteService == null || !_remoteService.IsConnected)
            {
                return;
            }

            try
            {
                var stat = await ExecuteRemoteAsync(
                    service => service.GetFileStatAsync(session.FilePath),
                    $"Stat {session.FileName}");

                if (!OpenSessions.Contains(session))
                {
                    return;
                }

                session.OriginalStat = stat;
            }
            catch (Exception ex)
            {
                AddLog($"Background stat failed for {session.FileName}: {ex.Message}");
            }
        }

        private async void RemoteTreeItem_Expanded(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            if (e.OriginalSource is not TreeViewItem treeItem) return;
            if (treeItem.DataContext is not RemoteTreeNode node) return;
            if (!node.IsDirectory || node.IsLoaded) return;
            e.Handled = true;

            try
            {
                await LoadChildrenAsync(node);
            }
            catch (Exception ex)
            {
                AddLog($"Expand failed for {node.Name}: {ex.Message}");
                SetStatus($"Expand failed: {ex.Message}", warning: true);
            }
        }

        private void RemoteTreeView_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (HandleRemoteTreeSearchKey(e))
            {
                return;
            }

            if (e.Key != Key.Delete)
            {
                return;
            }

            e.Handled = true;
            _ = DeleteSelectedRemoteNodesAsync();
        }

        private void RemoteTreeView_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (e.OriginalSource is System.Windows.Controls.TextBox || TreeNameSearch.ShouldIgnoreTypedSearch(e))
            {
                return;
            }

            e.Handled = true;
            SetRemoteTreeSearchQuery(_treeSearchQuery + e.Text);
        }

        private bool HandleRemoteTreeSearchKey(System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Escape && !string.IsNullOrEmpty(_treeSearchQuery))
            {
                e.Handled = true;
                SetRemoteTreeSearchQuery(string.Empty);
                return true;
            }

            if (e.OriginalSource is System.Windows.Controls.TextBox)
            {
                return false;
            }

            if (e.Key == Key.Back && !string.IsNullOrEmpty(_treeSearchQuery))
            {
                e.Handled = true;
                SetRemoteTreeSearchQuery(_treeSearchQuery[..^1]);
                return true;
            }

            return false;
        }

        private void RemoteTreeSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressTreeSearchText)
            {
                return;
            }

            SetRemoteTreeSearchQuery(RemoteTreeSearchBox.Text ?? string.Empty, syncBox: false);
        }

        private void RemoteTreeSearchClear_Click(object sender, RoutedEventArgs e)
        {
            SetRemoteTreeSearchQuery(string.Empty);
            RemoteTreeView.Focus();
        }

        private void SetRemoteTreeSearchQuery(string query, bool syncBox = true)
        {
            _treeSearchQuery = query ?? string.Empty;
            if (syncBox && RemoteTreeSearchBox != null)
            {
                _suppressTreeSearchText = true;
                RemoteTreeSearchBox.Text = _treeSearchQuery;
                RemoteTreeSearchBox.CaretIndex = RemoteTreeSearchBox.Text.Length;
                _suppressTreeSearchText = false;
            }

            ApplyRemoteTreeSearch();
        }

        private void ApplyRemoteTreeSearch()
        {
            var query = _treeSearchQuery;
            var active = !string.IsNullOrEmpty(query);
            if (RemoteTreeSearchBar != null)
            {
                RemoteTreeSearchBar.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            }

            TreeNameSearch.SetSearchActive(RemoteTreeView, active);
            TreeNameSearch.Apply(
                RootNodes,
                query,
                node => node.Name,
                node => node.Children,
                (node, visible, parts, expand) => node.ApplySearchVisual(visible, parts, expand));
        }

        private async void RemoteTreeView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_isBusy)
            {
                return;
            }

            var item = FindParent<TreeViewItem>(e.OriginalSource as DependencyObject);
            if (item?.DataContext is not RemoteTreeNode node)
            {
                return;
            }

            if (node.IsDirectory || node.IsPlaceholder)
            {
                return;
            }

            e.Handled = true;
            await OpenFileAsync(node);
        }

        private void RemoteTreeView_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isBusy || _remoteService == null || !_remoteService.IsConnected)
            {
                return;
            }

            var treeItem = FindParent<TreeViewItem>(e.OriginalSource as DependencyObject);
            if (treeItem?.DataContext is not RemoteTreeNode node || node.IsPlaceholder)
            {
                return;
            }

            treeItem.Focus();

            var selected = TreeViewExtendedSelectionBehavior.GetSelectedItems<RemoteTreeNode>(RemoteTreeView);
            if (selected.Count == 0 || !selected.Contains(node))
            {
                selected = new List<RemoteTreeNode> { node };
            }

            var actions = BuildRemoteContextActions(node, selected);
            if (GlobalContextMenuService.ShowMenu(treeItem, actions, node, PlacementMode.MousePoint))
            {
                e.Handled = true;
            }
        }

        private RemoteEditSession? FindSessionByPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var normalized = path.TrimEnd('/');
            return OpenSessions.FirstOrDefault(session =>
                string.Equals(session.FilePath.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase));
        }

        private async Task CaptureActiveSessionBufferAsync()
        {
            if (_editSession == null || !_isEditorOpen)
            {
                return;
            }

            var content = await GetCurrentEditorContentAsync();
            _editSession.WorkingContent = content;
            _editSession.IsDirty = !string.Equals(content, _editSession.Content, StringComparison.Ordinal);
        }

        private void SyncTabSelection(RemoteEditSession? session)
        {
            _suppressTabSelectionChanged = true;
            EditorTabsListBox.SelectedItem = session;
            _suppressTabSelectionChanged = false;
        }

        private async Task ActivateSessionAsync(RemoteEditSession session, bool captureCurrentBuffer)
        {
            if (session == null)
            {
                return;
            }

            if (captureCurrentBuffer && _editSession != null && !ReferenceEquals(_editSession, session))
            {
                await CaptureActiveSessionBufferAsync();
            }

            _editSession = session;
            if (string.IsNullOrEmpty(session.WorkingContent) && !string.IsNullOrEmpty(session.Content))
            {
                session.WorkingContent = session.Content;
            }

            await LoadEditorContentAsync(session.FilePath, session.WorkingContent);
            EditorPathText.Text = session.FilePath;
            EditorStatusText.Text = session.IsDirty
                ? "Modified locally. Save/Upload to apply."
                : "No local changes.";
            SyncTabSelection(session);
            ShowEditorMode();
            UpdateUiState();
            if (!_isBusy)
            {
                FocusEditorSurface();
            }
        }

        private async void EditorTabsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressTabSelectionChanged || _isBusy)
            {
                return;
            }

            if (EditorTabsListBox.SelectedItem is not RemoteEditSession session)
            {
                return;
            }

            if (_editSession != null && ReferenceEquals(session, _editSession))
            {
                return;
            }

            try
            {
                await ActivateSessionAsync(session, captureCurrentBuffer: true);
            }
            catch (Exception ex)
            {
                SetStatus($"Tab switch failed: {ex.Message}", warning: true);
                AddLog($"Tab switch failed: {ex.Message}");
                SyncTabSelection(_editSession);
            }
        }

        private async void CloseTabButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: RemoteEditSession session })
            {
                return;
            }

            e.Handled = true;
            await CloseSessionTabAsync(session, promptUnsaved: true);
        }

        private async Task<bool> CloseSessionTabAsync(RemoteEditSession session, bool promptUnsaved)
        {
            if (session == null)
            {
                return true;
            }

            if (promptUnsaved && session.IsDirty)
            {
                var result = ModernMessageBox.ShowWithResult(
                    $"'{session.FileName}' has unsaved remote changes. Upload before closing this tab?",
                    "Unsaved tab",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning,
                    primaryText: "Upload & Close",
                    secondaryText: "Close Without Upload",
                    cancelText: "Keep Editing");

                if (result == MessageBoxResult.Cancel || result == MessageBoxResult.None)
                {
                    return false;
                }

                if (result == MessageBoxResult.Yes)
                {
                    if (_editSession == null || !ReferenceEquals(_editSession, session))
                    {
                        await ActivateSessionAsync(session, captureCurrentBuffer: true);
                    }

                    var uploaded = await SaveUploadCurrentEditorFileAsync();
                    if (!uploaded)
                    {
                        ModernMessageBox.Show(
                            "Upload failed. The tab remains open so you can retry.",
                            "Upload failed",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return false;
                    }
                }
            }

            var closeIndex = OpenSessions.IndexOf(session);
            var wasActive = _editSession != null && ReferenceEquals(_editSession, session);
            OpenSessions.Remove(session);

            if (OpenSessions.Count == 0)
            {
                _editSession = null;
                SyncTabSelection(null);
                EditorPathText.Text = "Select a remote file to edit";
                EditorStatusText.Text = "No file loaded.";
                ShowBrowserMode();
                UpdateUiState();
                return true;
            }

            if (wasActive)
            {
                var nextIndex = Math.Max(0, Math.Min(closeIndex, OpenSessions.Count - 1));
                var nextSession = OpenSessions[nextIndex];
                await ActivateSessionAsync(nextSession, captureCurrentBuffer: false);
                return true;
            }

            UpdateUiState();
            return true;
        }

        private async Task ReloadActiveSessionFromRemoteAsync()
        {
            if (_editSession == null)
            {
                return;
            }

            await ReloadSessionFromRemoteAsync(_editSession);
        }

        private async void EditorRefreshButton_Click(object sender, RoutedEventArgs e)
            => await RefreshActiveEditorFromRemoteAsync();

        private async Task RefreshActiveEditorFromRemoteAsync()
        {
            if (_isBusy || !_isEditorOpen || _editSession == null)
            {
                return;
            }

            if (_remoteService == null || !_remoteService.IsConnected)
            {
                SetStatus("Connect to FTP first.", warning: true);
                return;
            }

            if (_editSession.IsDirty)
            {
                var confirm = ModernMessageBox.ShowWithResult(
                    Loc.T("deploy.refreshEditor.confirm"),
                    Loc.T("deploy.refreshEditor.title"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    primaryText: Loc.T("common.refresh"),
                    secondaryText: Loc.T("common.cancel"));
                if (confirm != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            _isBusy = true;
            UpdateUiState();
            try
            {
                var fileName = _editSession.FileName;
                await ReloadActiveSessionFromRemoteAsync();
                SetStatus($"Reloaded {fileName} from remote.", success: true);
                AddLog($"Reloaded {fileName} from remote.");
            }
            catch (Exception ex)
            {
                SetStatus($"Refresh failed: {ex.Message}", warning: true);
                AddLog($"Editor refresh failed: {ex.Message}");
            }
            finally
            {
                _isBusy = false;
                UpdateUiState();
            }
        }

        private async Task EnableEditorRemoteRefreshMenuAsync()
        {
            if (EditorWebView?.CoreWebView2 == null)
            {
                return;
            }

            var label = (Loc.T("common.refresh") ?? "Refresh")
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);
            await EditorWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.__setRemoteRefreshEnabled && window.__setRemoteRefreshEnabled(true, \"{label}\");");
        }

        public async Task ReloadSessionsMatchingRemotePathAsync(string remotePath)
        {
            if (string.IsNullOrWhiteSpace(remotePath) || OpenSessions.Count == 0)
            {
                return;
            }

            var target = NormalizeSessionPath(remotePath);
            var matches = OpenSessions
                .Where(session => string.Equals(NormalizeSessionPath(session.FilePath), target, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var session in matches)
            {
                if (session.IsDirty)
                {
                    AddLog($"Skipped editor reload for {session.FilePath} (unsaved changes).");
                    continue;
                }

                await ReloadSessionFromRemoteAsync(session);
            }
        }

        private static string NormalizeSessionPath(string? path)
        {
            var normalized = (path ?? string.Empty).Replace('\\', '/').TrimEnd('/');
            return string.IsNullOrWhiteSpace(normalized) ? "/" : normalized;
        }

        private async Task ReloadSessionFromRemoteAsync(RemoteEditSession session)
        {
            if (session == null || _remoteService == null || !_remoteService.IsConnected)
            {
                return;
            }

            var text = await ExecuteRemoteAsync(service => service.OpenTextAsync(session.FilePath), $"Reload {session.FileName}");
            var stat = await ExecuteRemoteAsync(service => service.GetFileStatAsync(session.FilePath), $"Reload stat {session.FileName}");

            session.Content = text;
            session.WorkingContent = text;
            session.OriginalContentHash = ComputeHash(text);
            session.OriginalStat = stat;
            session.IsDirty = false;

            if (ReferenceEquals(_editSession, session) && _isEditorOpen)
            {
                await LoadEditorContentAsync(session.FilePath, text);
                await MarkEditorCleanAsync();
                EditorStatusText.Text = "File reloaded from remote.";
            }

            UpdateUiState();
        }

        private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
            => DependencyObjectAncestors.Find<T>(child);

        private IReadOnlyList<AppContextMenuAction> BuildRemoteContextActions(RemoteTreeNode node)
            => BuildRemoteContextActions(node, new[] { node });

        private IReadOnlyList<AppContextMenuAction> BuildRemoteContextActions(
            RemoteTreeNode node,
            IReadOnlyList<RemoteTreeNode> selected)
        {
            var checkedActions = BuildCheckedDownloadContextActions();
            var items = selected.Count > 0 ? selected : new[] { node };
            var multi = items.Count > 1;
            var deletable = items.Where(item => !IsBrowseRootNode(item) && !item.IsPlaceholder).ToList();
            var canDelete = deletable.Count > 0;

            if (multi)
            {
                var multiActions = new List<AppContextMenuAction>();
                if (checkedActions.Count > 0)
                {
                    multiActions.AddRange(checkedActions);
                    multiActions.Add(AppContextMenuAction.Separator("remote-checked-separator"));
                }

                multiActions.Add(new AppContextMenuAction
                {
                    Id = "download",
                    Label = $"Download ({deletable.Count} items)",
                    IconGlyph = "⬇",
                    IsEnabled = canDelete,
                    Execute = _ => _ = DownloadNodesAsync(deletable)
                });
                multiActions.Add(AppContextMenuAction.Separator("remote-action-separator"));
                multiActions.Add(new AppContextMenuAction
                {
                    Id = "delete",
                    Label = $"Delete ({deletable.Count} items)",
                    IconGlyph = "🗑",
                    IsDestructive = true,
                    IsEnabled = canDelete,
                    Execute = _ => _ = DeleteNodesAsync(deletable)
                });
                return multiActions;
            }

            var isRoot = IsBrowseRootNode(node);
            var menuActions = new List<AppContextMenuAction>();
            if (checkedActions.Count > 0)
            {
                menuActions.AddRange(checkedActions);
                menuActions.Add(AppContextMenuAction.Separator("remote-checked-separator-single"));
            }

            menuActions.AddRange(new List<AppContextMenuAction>
            {
                new()
                {
                    Id = "refresh",
                    Label = "Refresh",
                    IconGlyph = "↻",
                    IsVisible = node.IsDirectory,
                    Execute = _ => _ = RefreshFolderFromUiAsync(node)
                },
                new()
                {
                    Id = "open",
                    Label = "Open",
                    IconGlyph = "📂",
                    IsEnabled = !node.IsDirectory,
                    Execute = _ => _ = OpenFileAsync(node)
                },
                new()
                {
                    Id = "new-folder",
                    Label = "New Folder",
                    IconGlyph = "📁",
                    Execute = _ => _ = CreateRemoteFolderAsync(node)
                },
                new()
                {
                    Id = "new-file",
                    Label = "New File",
                    IconGlyph = "📄",
                    Execute = _ => _ = CreateRemoteFileAsync(node)
                },
                new()
                {
                    Id = "download",
                    Label = node.IsDirectory ? "Download Folder" : "Download File",
                    IconGlyph = "⬇",
                    IsEnabled = !isRoot,
                    Execute = _ => _ = DownloadNodeAsync(node)
                },
                AppContextMenuAction.Separator("remote-action-separator"),
                new()
                {
                    Id = "rename",
                    Label = "Rename",
                    IconGlyph = "✏",
                    IsEnabled = !isRoot,
                    Execute = _ => _ = RenameNodeAsync(node)
                },
                new()
                {
                    Id = "move",
                    Label = "Move",
                    IconGlyph = "➡",
                    IsEnabled = !isRoot,
                    Execute = _ => _ = MoveNodeAsync(node)
                },
                new()
                {
                    Id = "delete",
                    Label = "Delete",
                    IconGlyph = "🗑",
                    IsDestructive = true,
                    IsEnabled = !isRoot,
                    Execute = _ => _ = DeleteNodeAsync(node)
                }
            });

            if (node.IsDirectory)
            {
                menuActions.Add(AppContextMenuAction.Separator("remote-mapping-separator"));
                menuActions.Add(new AppContextMenuAction
                {
                    Id = "mapping",
                    Label = "Mapping",
                    IconGlyph = "↔",
                    Execute = _ => _ = OpenPathMappingModalAsync(node)
                });
            }

            menuActions.Add(AppContextMenuAction.Separator("remote-properties-separator"));
            menuActions.Add(new AppContextMenuAction
            {
                Id = "permissions",
                Label = "Permissions",
                IconGlyph = "🔐",
                IsEnabled = !node.IsPlaceholder,
                Execute = _ => ShowRemotePermissions(node)
            });
            menuActions.Add(new AppContextMenuAction
            {
                Id = "properties",
                Label = "Properties",
                IconGlyph = "ℹ",
                IsEnabled = !node.IsPlaceholder,
                Execute = _ => ShowRemoteItemProperties(node)
            });

            return menuActions;
        }

        private string ResolveRemoteParentDirectory(RemoteTreeNode node)
        {
            var root = RemotePathResolver.BuildRemoteRoot(_currentProfile);
            if (node.IsDirectory)
            {
                return RemotePathResolver.EnsureTrailingSlash(node.FullPath).TrimEnd('/');
            }

            return RemotePathResolver.GetParentDirectory(node.FullPath, root).TrimEnd('/');
        }

        private void ShowRemoteItemProperties(RemoteTreeNode node)
        {
            if (node == null || node.IsPlaceholder)
            {
                return;
            }

            if (_remoteService == null || !_remoteService.IsConnected)
            {
                ModernMessageBox.Show(
                    "Connect an FTP/SFTP profile first.",
                    "Properties",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var protocol = _remoteService.UsesSsh ? "SFTP" : "FTP";
            var location = RemotePathResolver.GetDirectoryPath(node.FullPath);
            if (string.IsNullOrWhiteSpace(location))
            {
                location = "/";
            }

            var path = node.FullPath;
            var isFolder = node.IsDirectory;
            var itemName = node.Name;
            var snapshot = new LocalItemPropertiesWindow.RemoteSnapshot
            {
                Name = node.Name,
                FullPath = node.FullPath,
                Location = location,
                IsFolder = node.IsDirectory,
                Protocol = protocol,
                SizeBytes = node.SizeBytes,
                ModifiedLabel = node.ModifiedLabel,
                LoadLive = token => LoadRemotePropertiesLiveAsync(path, isFolder, itemName, token)
            };

            var window = new LocalItemPropertiesWindow(snapshot);
            WindowOwnerService.ShowDialogOwned(window, this);
        }

        private void ShowRemotePermissions(RemoteTreeNode node)
        {
            if (node == null || node.IsPlaceholder)
            {
                return;
            }

            if (_remoteService == null || !_remoteService.IsConnected)
            {
                ModernMessageBox.Show(
                    "Connect an FTP/SFTP profile first.",
                    "Permissions",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var protocol = _remoteService.UsesSsh ? "SFTP" : "FTP";
            var path = node.FullPath;
            var window = new RemotePermissionsWindow(
                node.Name,
                path,
                node.IsDirectory,
                protocol,
                token => ExecuteRemoteAsync(
                    service => service.GetUnixPermissionsAsync(path, token),
                    $"Read permissions {node.Name}"),
                (mode, token) => ExecuteRemoteAsync(
                    service => service.SetUnixPermissionsAsync(path, mode, token),
                    $"Set permissions {node.Name}"));
            WindowOwnerService.ShowDialogOwned(window, this);
        }

        private async Task<LocalItemPropertiesWindow.RemoteLiveRefresh> LoadRemotePropertiesLiveAsync(
            string path,
            bool isFolder,
            string itemName,
            CancellationToken token)
        {
            RemoteFileStat? stat = null;
            IReadOnlyList<RemoteDirectoryEntry>? children = null;
            try
            {
                stat = await ExecuteRemoteAsync(
                    service => service.GetFileStatAsync(path, token),
                    $"Properties {itemName}");
            }
            catch
            {
                // Keep listing snapshot if STAT is unsupported.
            }

            if (isFolder)
            {
                children = await ExecuteRemoteAsync(
                    service => service.ListDirectoryAsync(path, token),
                    $"Properties list {itemName}");
            }

            return new LocalItemPropertiesWindow.RemoteLiveRefresh
            {
                Stat = stat,
                Children = children
            };
        }

        private async Task OpenPathMappingModalAsync(RemoteTreeNode node)
        {
            if (_currentProfile == null)
            {
                ModernMessageBox.Show(
                    "Connect an FTP/SFTP profile first.",
                    "Path Mapping",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (node == null || !node.IsDirectory)
            {
                return;
            }

            var rootBefore = RemotePathResolver.BuildRemoteRoot(_currentProfile);
            var modal = new PathMappingModal(_currentProfile, node.FullPath);
            WindowOwnerService.ShowDialogOwned(modal, this);
            var changed = modal.MappingsChanged;
            UpdateMappingBanner();
            if (!changed || _currentProfile == null)
            {
                return;
            }

            var rootAfter = RemotePathResolver.BuildRemoteRoot(_currentProfile);
            if (!string.Equals(
                    rootBefore.TrimEnd('/'),
                    rootAfter.TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase)
                && _remoteService != null
                && _remoteService.IsConnected)
            {
                try
                {
                    await LoadRootAsync();
                }
                catch (Exception ex)
                {
                    AddLog($"Mapping saved, but refresh failed: {ex.Message}");
                }
            }
        }

        private async Task CreateRemoteFolderAsync(RemoteTreeNode node)
        {
            if (_isBusy || _remoteService == null || !_remoteService.IsConnected || _currentProfile == null)
            {
                return;
            }

            var dialog = new InputDialog("Enter folder name:", "New Folder", "new-folder")
            {
                Owner = Window.GetWindow(this)
            };

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

            var parent = ResolveRemoteParentDirectory(node);
            var destination = RemotePathResolver.CombineRemotePaths(parent, name);

            _isBusy = true;
            UpdateUiState();
            try
            {
                await ExecuteRemoteAsync(
                    service => service.EnsureDirectoryAsync(destination),
                    $"Create folder {name}");

                // Refresh only the parent folder — do not restart the whole FTP tree.
                var parentNode = node.IsDirectory ? node : FindParentNode(node) ?? FindNodeByPath(parent);
                if (parentNode != null)
                {
                    parentNode.IsExpanded = true;
                    await LoadChildrenAsync(parentNode);
                    parentNode.IsExpanded = true;
                    UpdateRemoteBrowserVisualState();
                }
                else
                {
                    await RefreshFolderInPlaceAsync(parent);
                }

                SetStatus($"Created folder {name}", success: true);
                AddLog($"Created folder {destination}");
            }
            catch (Exception ex)
            {
                SetStatus($"Create folder failed: {ex.Message}", warning: true);
                AddLog($"Create folder failed: {ex.Message}");
                ModernMessageBox.Show($"Could not create folder:\n{ex.Message}", "Remote workspace", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isBusy = false;
                UpdateUiState();
            }
        }

        private async Task CreateRemoteFileAsync(RemoteTreeNode node)
        {
            if (_isBusy || _remoteService == null || !_remoteService.IsConnected || _currentProfile == null)
            {
                return;
            }

            var dialog = new NewFileDialog();
            if (WindowOwnerService.ShowDialogOwned(dialog, this) != true
                || string.IsNullOrWhiteSpace(dialog.FileName))
            {
                return;
            }

            var fileName = dialog.FileName.Trim();
            var parent = ResolveRemoteParentDirectory(node);
            var destination = RemotePathResolver.CombineRemotePaths(parent, fileName).TrimEnd('/');

            _isBusy = true;
            UpdateUiState();
            try
            {
                var starter = FileStarterTemplates.GetStarterContent(fileName);
                await ExecuteRemoteAsync(
                    service => service.UploadTextFileAsync(destination, starter),
                    $"Create file {fileName}");

                var parentNode = node.IsDirectory ? node : FindParentNode(node) ?? FindNodeByPath(parent);
                RemoteTreeNode? createdNode = null;
                if (parentNode != null)
                {
                    parentNode.IsExpanded = true;
                    await LoadChildrenAsync(parentNode);
                    parentNode.IsExpanded = true;
                    createdNode = FindNodeByPath(destination);
                    UpdateRemoteBrowserVisualState();
                }
                else
                {
                    await RefreshFolderInPlaceAsync(parent);
                    createdNode = FindNodeByPath(destination);
                }

                SetStatus($"Created file {fileName}", success: true);
                AddLog($"Created file {destination}");

                createdNode ??= new RemoteTreeNode
                {
                    Name = fileName,
                    FullPath = destination,
                    IsDirectory = false,
                    IconGlyph = "📄"
                };

                _isBusy = false;
                UpdateUiState();
                await OpenFileAsync(createdNode);
            }
            catch (Exception ex)
            {
                SetStatus($"Create file failed: {ex.Message}", warning: true);
                AddLog($"Create file failed: {ex.Message}");
                ModernMessageBox.Show($"Could not create file:\n{ex.Message}", "Remote workspace", MessageBoxButton.OK, MessageBoxImage.Error);
                _isBusy = false;
                UpdateUiState();
            }
        }

        private string ResolveDefaultDownloadRoot()
        {
            var projectRoot = _configService.LoadGlobalConfig().LastProjectPath;
            var mapping = RemotePathResolver.GetPrimaryMapping(_currentProfile);
            return RemotePathResolver.ResolveLocalDownloadRoot(projectRoot, mapping);
        }

        private void ToggleRemoteCheckboxModeButton_Click(object sender, RoutedEventArgs e)
        {
            RemoteCheckboxModeEnabled = !RemoteCheckboxModeEnabled;
        }

        private void RemoteSelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            SetAllRemoteChecked(true);
        }

        private void RemoteDeselectAllButton_Click(object sender, RoutedEventArgs e)
        {
            SetAllRemoteChecked(false);
        }

        private void DownloadCheckedButton_Click(object sender, RoutedEventArgs e)
        {
            var targets = GetCheckedDownloadTargets();
            if (targets.Count == 0)
            {
                ModernMessageBox.Show(
                    Loc.T("deploy.remoteDownloadNothingChecked"),
                    Loc.T("deploy.remoteDownloadChecked"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information,
                    context: this);
                return;
            }

            _ = DownloadNodesAsync(targets);
        }

        private void RemoteItemCheckBox_Click(object sender, RoutedEventArgs e)
        {
            UpdateRemoteCheckedDownloadUi();
        }

        private void ApplyRemoteCheckboxModeUi()
        {
            var enabled = RemoteCheckboxModeEnabled;
            var bulkVisibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            if (RemoteSelectAllButton != null)
            {
                RemoteSelectAllButton.Visibility = bulkVisibility;
            }

            if (RemoteDeselectAllButton != null)
            {
                RemoteDeselectAllButton.Visibility = bulkVisibility;
            }

            if (DownloadCheckedButton != null)
            {
                DownloadCheckedButton.Visibility = bulkVisibility;
            }

            if (ToggleRemoteCheckboxModeButton != null)
            {
                ToggleRemoteCheckboxModeButton.ToolTip = enabled
                    ? Loc.T("deploy.remoteCheckboxMode.on")
                    : Loc.T("deploy.tip.remoteCheckboxMode");
                var tokens = ThemeService.Instance.CurrentTokens;
                var activeColor = tokens.GetColor(
                    "ftp.chrome.checkboxModeActive",
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E3A5F"));
                ToggleRemoteCheckboxModeButton.Background = enabled
                    ? new SolidColorBrush(activeColor)
                    : System.Windows.Media.Brushes.Transparent;
            }

            if (!enabled)
            {
                ClearRemoteCheckedSelection();
            }

            UpdateRemoteCheckedDownloadUi();
        }

        private void ClearRemoteCheckedSelection()
        {
            foreach (var node in RootNodes)
            {
                node.ClearChecked();
            }
        }

        private void SetAllRemoteChecked(bool value)
        {
            foreach (var root in RootNodes)
            {
                if (IsBrowseRootNode(root))
                {
                    foreach (var child in root.Children.Where(c => !c.IsPlaceholder))
                    {
                        child.IsChecked = value;
                    }
                }
                else
                {
                    root.IsChecked = value;
                }
            }

            UpdateRemoteCheckedDownloadUi();
        }

        private static void CollectCheckedDownloadTargets(
            IEnumerable<RemoteTreeNode> nodes,
            ICollection<RemoteTreeNode> folders,
            ICollection<RemoteTreeNode> files)
        {
            foreach (var node in nodes)
            {
                if (node.IsPlaceholder)
                {
                    continue;
                }

                if (node.IsDirectory)
                {
                    var realChildren = node.Children.Where(child => !child.IsPlaceholder).ToList();
                    if (realChildren.Count > 0)
                    {
                        CollectCheckedDownloadTargets(realChildren, folders, files);
                    }
                    else if (node.IsChecked == true)
                    {
                        folders.Add(node);
                    }
                }
                else if (node.IsChecked == true)
                {
                    files.Add(node);
                }
            }
        }

        private IReadOnlyList<RemoteTreeNode> GetCheckedDownloadTargets()
        {
            var folders = new List<RemoteTreeNode>();
            var files = new List<RemoteTreeNode>();
            CollectCheckedDownloadTargets(RootNodes, folders, files);
            var combined = folders.Concat(files)
                .Where(node => !IsBrowseRootNode(node))
                .ToList();
            return TreeMultiSelectHelpers.CollapseNestedByPath(
                combined,
                node => node.FullPath,
                node => node.IsDirectory);
        }

        private int CountCheckedDownloadTargets()
        {
            return GetCheckedDownloadTargets().Count;
        }

        private void UpdateRemoteCheckedDownloadUi()
        {
            if (!RemoteCheckboxModeEnabled || DownloadCheckedButton == null)
            {
                return;
            }

            var count = CountCheckedDownloadTargets();
            DownloadCheckedButton.IsEnabled = count > 0;
            DownloadCheckedButton.ToolTip = count > 0
                ? $"{Loc.T("deploy.remoteDownloadChecked")} ({count})"
                : Loc.T("deploy.remoteDownloadChecked");
        }

        private IReadOnlyList<AppContextMenuAction> BuildCheckedDownloadContextActions()
        {
            if (!RemoteCheckboxModeEnabled)
            {
                return Array.Empty<AppContextMenuAction>();
            }

            var checkedTargets = GetCheckedDownloadTargets();
            if (checkedTargets.Count == 0)
            {
                return Array.Empty<AppContextMenuAction>();
            }

            return new[]
            {
                new AppContextMenuAction
                {
                    Id = "download-checked",
                    Label = $"{Loc.T("deploy.remoteDownloadChecked")} ({checkedTargets.Count})",
                    IconGlyph = "⬇",
                    Execute = _ => _ = DownloadNodesAsync(checkedTargets)
                }
            };
        }

        private Task DownloadNodeAsync(RemoteTreeNode node) =>
            DownloadNodesAsync(node == null ? Array.Empty<RemoteTreeNode>() : new[] { node });

        private async Task DownloadNodesAsync(IReadOnlyList<RemoteTreeNode> nodes)
        {
            if (_isDownloading || _remoteService == null || !_remoteService.IsConnected || _currentProfile == null)
            {
                return;
            }

            var targets = TreeMultiSelectHelpers.CollapseNestedByPath(
                nodes.Where(node => node != null && !node.IsPlaceholder && !IsBrowseRootNode(node)),
                node => node.FullPath,
                node => node.IsDirectory);
            if (targets.Count == 0)
            {
                return;
            }

            var projectRoot = _configService.LoadGlobalConfig().LastProjectPath;
            var planned = new List<(RemoteTreeNode Node, string LocalTarget)>();
            var skipped = new List<string>();
            foreach (var node in targets)
            {
                if (!RemotePathResolver.TryResolveDownloadTargetFromRemotePath(
                        node.FullPath,
                        _currentProfile,
                        projectRoot,
                        node.IsDirectory,
                        node.Name,
                        out var localTarget,
                        out _))
                {
                    skipped.Add(node.FullPath);
                    continue;
                }

                planned.Add((node, localTarget));
            }

            if (planned.Count == 0)
            {
                var message = skipped.Count > 0
                    ? Loc.T("deploy.remoteDownloadUnmapped")
                    : "Nothing to download.";
                SetStatus(message, warning: true);
                AddLog(message);
                ModernMessageBox.Show(
                    message,
                    Loc.T("deploy.remoteDownloadChecked"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    context: this);
                return;
            }

            if (skipped.Count > 0)
            {
                var skipMessage = $"{Loc.T("deploy.remoteDownloadUnmapped")} ({skipped.Count})";
                SetStatus(skipMessage, warning: true);
                AddLog(skipMessage);
                foreach (var path in skipped.Take(5))
                {
                    AddLog($"  • skipped {path}");
                }
            }

            var existing = planned
                .Where(item => LocalDownloadTargetExists(item.LocalTarget, item.Node.IsDirectory))
                .Select(item => item.LocalTarget)
                .ToList();
            if (existing.Count > 0 && !ConfirmDownloadOverwrite(existing))
            {
                return;
            }

            var askWorkers = targets.Count > 1 || targets.Any(node => node.IsDirectory);
            var workers = 1;
            if (askWorkers && !TransferWorkerPrompt.TryAsk(
                    this,
                    "download",
                    Math.Max(targets.Count, 2),
                    out workers,
                    forceAsk: true))
            {
                SetStatus("Download cancelled.", warning: true);
                return;
            }

            _isDownloading = true;
            _downloadCts?.Dispose();
            _downloadCts = new CancellationTokenSource();
            var token = _downloadCts.Token;
            _transferMonitor.Show(this, $"Download · {workers} workers requested");
            ShowDownloadProgress($"Planning with {workers} workers requested…", 0, indeterminate: true);
            _transferMonitor.Update(new ParallelTransferProgress
            {
                Phase = "Planning",
                RequestedWorkers = workers,
                Headline = $"{workers} workers requested · listing files…",
                LastLine = "Planning remote files",
                Sequence = 1
            });
            try
            {
                var jobs = new List<RemoteTransferJob>();
                foreach (var item in planned)
                {
                    token.ThrowIfCancellationRequested();
                    if (item.Node.IsDirectory)
                    {
                        ShowDownloadProgress($"Planning folder {item.Node.Name}…", 0, indeterminate: true);
                        var plannedFiles = await ExecuteRemoteAsync(
                            service => service.PlanDownloadDirectoryAsync(item.Node.FullPath, item.LocalTarget, token),
                            $"Plan {item.Node.Name}");
                        jobs.AddRange(plannedFiles);
                        AddLog($"Planned folder {item.Node.FullPath} → {plannedFiles.Count} files");
                        _transferMonitor.Update(new ParallelTransferProgress
                        {
                            Phase = "Planning",
                            RequestedWorkers = workers,
                            Total = jobs.Count,
                            Headline = $"{workers} workers requested · {jobs.Count} files listed",
                            LastLine = $"{item.Node.Name}: {plannedFiles.Count} files",
                            Sequence = jobs.Count + 1
                        });
                    }
                    else
                    {
                        var localDir = Path.GetDirectoryName(item.LocalTarget);
                        if (!string.IsNullOrWhiteSpace(localDir))
                        {
                            Directory.CreateDirectory(localDir);
                        }

                        jobs.Add(new RemoteTransferJob
                        {
                            RemotePath = item.Node.FullPath,
                            LocalPath = item.LocalTarget,
                            SizeBytes = item.Node.SizeBytes
                        });
                    }
                }

                if (jobs.Count == 0)
                {
                    SetStatus("Nothing to download (empty folder).", success: true);
                    AddLog("Download plan was empty.");
                    return;
                }

                workers = Math.Clamp(workers, 1, Math.Min(TransferWorkerPrompt.MaxWorkers, Math.Max(1, jobs.Count)));
                AddLog($"Download: {jobs.Count} files with {workers} worker(s) (requested kept unless fewer files)");
                ShowDownloadProgress($"{workers} workers · {jobs.Count} files · connecting…", 0, indeterminate: true);
                var progress = new Progress<RemoteDownloadProgress>(ApplyDownloadProgress);
                var result = await ParallelRemoteTransfer.DownloadAsync(
                    _currentProfile,
                    jobs,
                    workers,
                    progress,
                    token,
                    new Progress<ParallelTransferProgress>(_transferMonitor.Update));

                if (!result.IsComplete)
                {
                    var parts = new List<string>();
                    if (result.Missing.Count > 0)
                    {
                        parts.Add($"{result.Missing.Count} missing locally");
                    }

                    if (result.Errors.Count > 0)
                    {
                        parts.Add($"{result.Errors.Count} errors");
                    }

                    var summary = string.Join(", ", parts);
                    SetStatus($"Download incomplete: {result.Completed}/{jobs.Count} — {summary}", warning: true);
                    AddLog($"Download verify failed: {summary}");
                    foreach (var error in result.Errors.Take(8))
                    {
                        AddLog($"  • {error}");
                    }

                    foreach (var missing in result.Missing.Take(8))
                    {
                        AddLog($"  • missing {missing.RemotePath}");
                    }

                    _transferMonitor.Finish($"Incomplete: {result.Completed}/{jobs.Count} · {summary}");
                    ModernMessageBox.Show(
                        $"Download finished with gaps.\nCompleted: {result.Completed}/{jobs.Count}\nWorkers used: {result.WorkerCount}\n{summary}",
                        "Remote workspace",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                SetStatus(
                    $"Downloaded {result.Completed} files with {result.WorkerCount} live workers",
                    success: true);
                var destinationHint = planned
                    .Select(item => item.LocalTarget)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(2)
                    .ToList();
                var destinationText = destinationHint.Count == 1
                    ? destinationHint[0]
                    : $"{destinationHint.Count} mapped destinations";
                AddLog($"Download verified: {result.Completed}/{jobs.Count} files, {result.WorkerCount} workers → {destinationText}");
                _transferMonitor.Finish($"Done: {result.Completed} files · {result.WorkerCount} workers");
            }
            catch (OperationCanceledException)
            {
                SetStatus("Download cancelled.", warning: true);
                AddLog("Download cancelled.");
                _transferMonitor.Finish("Cancelled");
            }
            catch (Exception ex)
            {
                SetStatus($"Download failed: {ex.Message}", warning: true);
                AddLog($"Download failed: {ex.Message}");
                _transferMonitor.Finish($"Failed: {ex.Message}");
                ModernMessageBox.Show($"Download failed:\n{ex.Message}", "Remote workspace", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isDownloading = false;
                _downloadCts?.Dispose();
                _downloadCts = null;
                HideDownloadProgress();
            }
        }

        private static bool LocalDownloadTargetExists(string path, bool isDirectory)
        {
            return isDirectory ? Directory.Exists(path) : File.Exists(path);
        }

        private bool ConfirmDownloadOverwrite(IReadOnlyList<string> existingPaths)
        {
            var preview = string.Join(Environment.NewLine, existingPaths.Take(5));
            var extra = existingPaths.Count > 5
                ? $"{Environment.NewLine}… and {existingPaths.Count - 5} more"
                : string.Empty;
            var result = ModernMessageBox.ShowWithResult(
                $"This destination already exists. Overwrite?{Environment.NewLine}{Environment.NewLine}{preview}{extra}",
                "Download",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                primaryText: "Overwrite",
                secondaryText: "Cancel",
                context: this);
            return result == MessageBoxResult.Yes;
        }

        private void ShowDownloadProgress(string fileName, double percent, bool indeterminate)
        {
            if (DownloadProgressPanel == null)
            {
                return;
            }

            DownloadProgressPanel.Visibility = Visibility.Visible;
            DownloadProgressFileText.Text = string.IsNullOrWhiteSpace(fileName) ? "Downloading..." : fileName;
            DownloadProgressPercentText.Text = indeterminate ? "" : $"{percent:0}%";
            DownloadProgressBar.IsIndeterminate = indeterminate;
            DownloadProgressBar.Value = Math.Clamp(percent, 0, 100);
        }

        private void ApplyDownloadProgress(RemoteDownloadProgress progress)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => ApplyDownloadProgress(progress));
                return;
            }

            ShowDownloadProgress(
                progress.CurrentFileName,
                progress.Percent,
                progress.IsIndeterminate);
            _transferMonitor.Update(progress.Detail);
        }

        private void HideDownloadProgress()
        {
            if (DownloadProgressPanel == null)
            {
                return;
            }

            DownloadProgressPanel.Visibility = Visibility.Collapsed;
            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressBar.Value = 0;
            DownloadProgressFileText.Text = string.Empty;
            DownloadProgressPercentText.Text = string.Empty;
        }

        private void FloatDownloadReportButton_Click(object sender, RoutedEventArgs e)
        {
            _transferMonitor.Show(this, "Download report");
        }

        private void CancelDownloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isDownloading)
            {
                return;
            }

            _downloadCts?.Cancel();
            if (DownloadProgressFileText != null)
            {
                DownloadProgressFileText.Text = "Cancelling...";
            }
        }

        private async Task RenameNodeAsync(RemoteTreeNode node)
        {
            if (_isBusy || _remoteService == null || !_remoteService.IsConnected || _currentProfile == null)
            {
                return;
            }

            var dialog = new InputDialog("Rename", $"Enter a new name for '{node.Name}':", node.Name)
            {
                Owner = Window.GetWindow(this)
            };

            if (WindowOwnerService.ShowDialogOwned(dialog, this) != true)
            {
                return;
            }

            var newName = (dialog.ResponseText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, node.Name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _isBusy = true;
            UpdateUiState();
            try
            {
                var root = RemotePathResolver.BuildRemoteRoot(_currentProfile);
                var parent = RemotePathResolver.GetParentDirectory(node.FullPath, root);
                var destinationPath = RemotePathResolver.CombineRemotePaths(parent, newName);
                await ExecuteRemoteAsync(
                    service => service.RenameAsync(node.FullPath, destinationPath),
                    $"Rename {node.Name}");

                ApplyPathRenameToOpenSessions(node.FullPath, destinationPath, node.IsDirectory);
                await LoadRootAsync();
                SetStatus($"Renamed to {newName}", success: true);
                AddLog($"Renamed {node.FullPath} -> {destinationPath}");
            }
            catch (Exception ex)
            {
                SetStatus($"Rename failed: {ex.Message}", warning: true);
                AddLog($"Rename failed: {ex.Message}");
                ModernMessageBox.Show($"Rename failed:\n{ex.Message}", "Remote workspace", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isBusy = false;
                UpdateUiState();
            }
        }

        private async Task MoveNodeAsync(RemoteTreeNode node)
        {
            if (_isBusy || _remoteService == null || !_remoteService.IsConnected || _currentProfile == null)
            {
                return;
            }

            if (node.IsPlaceholder)
            {
                return;
            }

            var root = RemotePathResolver.BuildRemoteRoot(_currentProfile);
            var picker = new RemoteFolderPickerWindow(
                _remoteService,
                root,
                node.Name,
                node.FullPath,
                node.IsDirectory)
            {
                Owner = Window.GetWindow(this)
            };

            if (WindowOwnerService.ShowDialogOwned(picker, this) != true
                || string.IsNullOrWhiteSpace(picker.SelectedFolderPath))
            {
                return;
            }

            var destinationFolder = RemotePathResolver.EnsureTrailingSlash(picker.SelectedFolderPath).TrimEnd('/');
            if (string.IsNullOrWhiteSpace(destinationFolder))
            {
                destinationFolder = "/";
            }

            if (node.IsDirectory && RemoteTreeBuilder.IsBlockedPath(destinationFolder, node.FullPath))
            {
                ModernMessageBox.Show(
                    "Cannot move a folder into itself or one of its subfolders.",
                    "Remote workspace",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var currentParent = RemotePathResolver.GetParentDirectory(node.FullPath, root).TrimEnd('/');
            if (string.Equals(currentParent, destinationFolder, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("Item is already in the selected folder.", warning: true);
                return;
            }

            var destinationPath = RemotePathResolver.CombineRemotePaths(destinationFolder, node.Name);
            var sourceNormalized = RemotePathResolver.NormalizeRemoteBase(node.FullPath).TrimEnd('/');
            var destinationNormalized = RemotePathResolver.NormalizeRemoteBase(destinationPath).TrimEnd('/');
            if (string.Equals(sourceNormalized, destinationNormalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _isBusy = true;
            UpdateUiState();
            try
            {
                await ExecuteRemoteAsync(
                    service => service.RenameAsync(node.FullPath, destinationPath),
                    $"Move {node.Name}");

                ApplyPathRenameToOpenSessions(node.FullPath, destinationPath, node.IsDirectory);
                await LoadRootAsync();
                SetStatus($"Moved to {destinationFolder}", success: true);
                AddLog($"Moved {node.FullPath} -> {destinationPath}");
            }
            catch (Exception ex)
            {
                SetStatus($"Move failed: {ex.Message}", warning: true);
                AddLog($"Move failed: {ex.Message}");
                ModernMessageBox.Show($"Move failed:\n{ex.Message}", "Remote workspace", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isBusy = false;
                UpdateUiState();
            }
        }

        private Task DeleteNodeAsync(RemoteTreeNode node) =>
            DeleteNodesAsync(node == null ? Array.Empty<RemoteTreeNode>() : new[] { node });

        private async Task DeleteSelectedRemoteNodesAsync()
        {
            if (_isBusy)
            {
                return;
            }

            var selected = TreeViewExtendedSelectionBehavior.GetSelectedItems<RemoteTreeNode>(RemoteTreeView);
            await DeleteNodesAsync(selected);
        }

        private async Task DeleteNodesAsync(IReadOnlyList<RemoteTreeNode> nodes)
        {
            if (_isBusy || _remoteService == null || !_remoteService.IsConnected || nodes == null)
            {
                return;
            }

            var targets = TreeMultiSelectHelpers.CollapseNestedByPath(
                nodes.Where(node => node != null && !node.IsPlaceholder && !IsBrowseRootNode(node)),
                node => node.FullPath,
                node => node.IsDirectory);
            if (targets.Count == 0)
            {
                return;
            }

            string message;
            if (targets.Count == 1)
            {
                var node = targets[0];
                message = $"Delete '{node.Name}' {(node.IsDirectory ? "folder" : "file")}? This action cannot be undone.";
            }
            else
            {
                var preview = string.Join("\n", targets.Take(8).Select(node => "• " + node.Name));
                var extra = targets.Count > 8 ? $"\n… and {targets.Count - 8} more" : string.Empty;
                message = $"Delete {targets.Count} items?\n\n{preview}{extra}\n\nThis action cannot be undone.";
            }

            var result = ModernMessageBox.ShowWithResult(
                message,
                "Confirm delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                primaryText: "Delete",
                secondaryText: "Cancel");
            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            _isBusy = true;
            UpdateUiState();
            try
            {
                foreach (var node in targets)
                {
                    await ExecuteRemoteAsync(
                        service => service.DeleteAsync(node.FullPath, node.IsDirectory),
                        $"Delete {node.Name}");

                    await RemoveDeletedSessionsAsync(node.FullPath, node.IsDirectory);

                    if (!TryRemoveNodeFromTree(node))
                    {
                        await RefreshContainingFolderAsync(node);
                    }
                }

                UpdateRemoteBrowserVisualState();
                TreeViewExtendedSelectionBehavior.ClearSelection(RemoteTreeView);
                SetStatus(
                    targets.Count == 1 ? $"Deleted {targets[0].Name}" : $"Deleted {targets.Count} items",
                    success: true);
                AddLog(targets.Count == 1
                    ? $"Deleted {targets[0].FullPath}"
                    : $"Deleted {targets.Count} remote items");
            }
            catch (Exception ex)
            {
                SetStatus($"Delete failed: {ex.Message}", warning: true);
                AddLog($"Delete failed: {ex.Message}");
                ModernMessageBox.Show($"Delete failed:\n{ex.Message}", "Remote workspace", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isBusy = false;
                UpdateUiState();
            }
        }

        private bool TryRemoveNodeFromTree(RemoteTreeNode node)
        {
            if (node == null)
            {
                return false;
            }

            if (RootNodes.Remove(node))
            {
                return true;
            }

            foreach (var candidate in EnumerateNodes(RootNodes))
            {
                if (candidate.Children.Remove(node))
                {
                    return true;
                }
            }

            return false;
        }

        private async Task RefreshContainingFolderAsync(RemoteTreeNode deletedNode)
        {
            if (deletedNode == null || _currentProfile == null)
            {
                return;
            }

            var parent = FindParentNode(deletedNode);
            if (parent != null)
            {
                if (parent.IsLoaded || parent.IsExpanded)
                {
                    await LoadChildrenAsync(parent);
                }

                UpdateRemoteBrowserVisualState();
                return;
            }

            // Deleted item was at root — refresh root listing in place without a full tree restart.
            if (RootNodes.Count > 0)
            {
                await RefreshFolderInPlaceAsync(RootNodes[0].FullPath);
            }

            UpdateRemoteBrowserVisualState();
        }

        private RemoteTreeNode? FindParentNode(RemoteTreeNode target)
        {
            foreach (var node in EnumerateNodes(RootNodes))
            {
                if (node.Children.Contains(target))
                {
                    return node;
                }
            }

            return null;
        }

        private void ApplyPathRenameToOpenSessions(string oldPath, string newPath, bool isDirectory)
        {
            if (OpenSessions.Count == 0)
            {
                return;
            }

            var oldNormalized = oldPath.TrimEnd('/');
            var oldPrefix = oldNormalized + "/";
            var newNormalized = newPath.TrimEnd('/');

            foreach (var session in OpenSessions)
            {
                var sessionPath = session.FilePath.TrimEnd('/');
                if (!isDirectory)
                {
                    if (!string.Equals(sessionPath, oldNormalized, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    session.FilePath = newPath;
                    session.FileName = Path.GetFileName(newPath.Replace("\\", "/"));
                    continue;
                }

                if (string.Equals(sessionPath, oldNormalized, StringComparison.OrdinalIgnoreCase))
                {
                    session.FilePath = newNormalized;
                    session.FileName = Path.GetFileName(newNormalized.Replace("\\", "/"));
                    continue;
                }

                if (sessionPath.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var suffix = sessionPath[oldPrefix.Length..];
                    var updatedPath = $"{newNormalized}/{suffix}".Replace("\\", "/");
                    session.FilePath = updatedPath;
                    session.FileName = Path.GetFileName(updatedPath);
                }
            }

            if (_editSession != null)
            {
                EditorPathText.Text = _editSession.FilePath;
            }

            EditorTabsListBox.Items.Refresh();
        }

        private async Task RemoveDeletedSessionsAsync(string deletedPath, bool isDirectory)
        {
            if (OpenSessions.Count == 0)
            {
                return;
            }

            var normalized = deletedPath.TrimEnd('/');
            var prefix = normalized + "/";
            var removedSessions = OpenSessions
                .Where(session =>
                {
                    var sessionPath = session.FilePath.TrimEnd('/');
                    if (!isDirectory)
                    {
                        return string.Equals(sessionPath, normalized, StringComparison.OrdinalIgnoreCase);
                    }

                    return string.Equals(sessionPath, normalized, StringComparison.OrdinalIgnoreCase)
                           || sessionPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
            if (removedSessions.Count == 0)
            {
                return;
            }

            var removedActive = _editSession != null && removedSessions.Contains(_editSession);
            foreach (var session in removedSessions)
            {
                OpenSessions.Remove(session);
            }

            if (OpenSessions.Count == 0)
            {
                _editSession = null;
                SyncTabSelection(null);
                EditorPathText.Text = "Select a remote file to edit";
                EditorStatusText.Text = "No file loaded.";
                ShowBrowserMode();
                return;
            }

            if (removedActive)
            {
                await ActivateSessionAsync(OpenSessions[0], captureCurrentBuffer: false);
            }
            else
            {
                UpdateUiState();
            }
        }

        private async Task OpenFileAsync(RemoteTreeNode node)
        {
            if (_isBusy)
            {
                return;
            }

            if (_remoteService == null || !_remoteService.IsConnected)
            {
                SetStatus("Remote connection is not ready. Trying to reconnect...", warning: true);
                AddLog($"Open requested for {node.FullPath}, but connection is not ready. Reconnect started.");
                var reconnected = await ReconnectSilentlyAsync();
                if (!reconnected || _remoteService == null || !_remoteService.IsConnected)
                {
                    var connectionMessage = "Cannot open file because remote connection is not available.";
                    EditorStatusText.Text = connectionMessage;
                    SetStatus(connectionMessage, warning: true);
                    ModernMessageBox.Show(connectionMessage, "Remote workspace", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            _isBusy = true;
            UpdateUiState();
            try
            {
                var openTimer = Stopwatch.StartNew();
                long downloadMs = 0;
                long editorMs = 0;
                var existingSession = FindSessionByPath(node.FullPath);
                if (existingSession != null)
                {
                    if (_editSession != null && ReferenceEquals(existingSession, _editSession))
                    {
                        SyncTabSelection(existingSession);
                        ShowEditorMode();
                        return;
                    }

                    await ActivateSessionAsync(existingSession, captureCurrentBuffer: true);
                    AddLog($"Focused {existingSession.FilePath}");
                    return;
                }

                await CaptureActiveSessionBufferAsync();
                ResetUploadFeedback();
                EditorStatusText.Text = $"Loading {node.Name}...";
                AddLog($"Opening {node.FullPath}...");

                var stepTimer = Stopwatch.StartNew();
                var text = await ExecuteRemoteAsync(
                    service => service.OpenTextAsync(node.FullPath),
                    $"Download {node.Name}");
                downloadMs = stepTimer.ElapsedMilliseconds;
                AddLog($"Downloaded {node.Name} ({text.Length} chars).");

                var newSession = new RemoteEditSession
                {
                    FilePath = node.FullPath,
                    FileName = node.Name,
                    Content = text,
                    WorkingContent = text,
                    OriginalContentHash = ComputeHash(text),
                    OriginalStat = new RemoteFileStat
                    {
                        FullPath = node.FullPath,
                        Exists = true,
                        IsDirectory = false,
                        SizeBytes = Encoding.UTF8.GetByteCount(text),
                        ModifiedUtc = AppTimeService.UtcNow
                    },
                    IsDirty = false,
                    LoadedAtUtc = AppTimeService.UtcNow
                };
                OpenSessions.Add(newSession);
                _ = RefreshSessionOriginalStatAsync(newSession);
                stepTimer.Restart();
                await ActivateSessionAsync(newSession, captureCurrentBuffer: false);
                editorMs = stepTimer.ElapsedMilliseconds;
                EditorStatusText.Text = "File loaded.";
                AddLog($"Opened {newSession.FilePath}");
                AddLog($"Open timing: total {openTimer.ElapsedMilliseconds} ms (download {downloadMs} ms, editor {editorMs} ms, stat background).");
            }
            catch (Exception ex)
            {
                var errorMessage = $"Open failed: {ex.Message}";
                ModernMessageBox.Show($"Could not open file:\n{errorMessage}", "Remote workspace", MessageBoxButton.OK, MessageBoxImage.Error);
                EditorStatusText.Text = errorMessage;
                SetStatus(errorMessage, warning: true);
                AddLog(errorMessage);
            }
            finally
            {
                _isBusy = false;
                UpdateUiState();
                if (_isEditorOpen)
                {
                    FocusEditorSurface();
                }
            }
        }

        private void EditorWebView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            FocusEditorSurface();
        }

        private void EditorWebView_GotFocus(object sender, RoutedEventArgs e)
        {
            FocusEditorSurface(monacoOnly: true);
        }

        private void FocusEditorSurface(bool monacoOnly = false)
        {
            if (!_isEditorOpen)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_editorUsingFallback)
                {
                    EditorKeyboardScope.ArmTextBox(EditorFallbackTextBox);
                    if (!monacoOnly && EditorFallbackTextBox.IsEnabled)
                    {
                        EditorFallbackTextBox.Focus();
                    }

                    return;
                }

                if (EditorWebView != null && EditorWebView.IsEnabled && EditorWebView.Visibility == Visibility.Visible)
                {
                    EditorKeyboardScope.ArmMonaco(EditorWebView);
                    if (!monacoOnly)
                    {
                        EditorWebView.Focus();
                    }

                    if (EditorWebView.CoreWebView2 != null)
                    {
                        _ = EditorWebView.CoreWebView2.ExecuteScriptAsync("window.__focusEditor && window.__focusEditor();");
                    }
                }
            }), DispatcherPriority.Input);
        }

        private async void SaveUploadButton_Click(object sender, RoutedEventArgs e)
        {
            await SaveUploadCurrentEditorFileAsync();
        }

        private void FloatEditorButton_Click(object sender, RoutedEventArgs e)
        {
            EditorFloatRequested?.Invoke(this, EventArgs.Empty);
        }

        public void SetEditorFloated(bool floated)
        {
            if (FloatEditorButton == null)
            {
                return;
            }

            _editorFloated = floated;
            FloatEditorButton.Content = floated ? "⊟" : "⧉";
            FloatEditorButton.ToolTip = Loc.T(floated ? "deploy.tip.dockEditor" : "deploy.tip.floatEditor");
            ApplyToolbarActionState(FloatEditorButton, floated);
        }

        public string GetActiveEditorPath() => _editSession?.FilePath ?? string.Empty;

        private async Task<bool> SaveUploadCurrentEditorFileAsync()
        {
            if (_isBusy || _editSession == null || !_editSession.IsDirty || _remoteService == null || !_remoteService.IsConnected)
            {
                return false;
            }

            _isBusy = true;
            UpdateUiState();
            try
            {
                var localContent = await GetCurrentEditorContentAsync();
                var expectedSize = Encoding.UTF8.GetByteCount(localContent);
                var before = await ExecuteRemoteAsync(
                    service => service.GetFileStatAsync(_editSession.FilePath),
                    $"Pre-upload stat {_editSession.FileName}");

                EditorStatusText.Text = "Uploading...";
                BeginUploadFeedback(expectedSize);
                var uploadProgress = new Progress<RemoteUploadProgress>(UpdateUploadFeedback);
                await ExecuteRemoteAsync(
                    service => service.UploadTextFileAsync(_editSession.FilePath, localContent, uploadProgress),
                    $"Upload {_editSession.FileName}");
                var after = await ExecuteRemoteAsync(
                    service => service.GetFileStatAsync(_editSession.FilePath),
                    $"Post-upload stat {_editSession.FileName}");

                var warnings = new List<string>();
                if (!after.Exists)
                {
                    warnings.Add("remote file not found after upload");
                }
                else if (!after.IsDirectory && after.SizeBytes != expectedSize)
                {
                    warnings.Add($"size mismatch (expected {expectedSize}, got {after.SizeBytes})");
                }

                var timestampUnchanged = before.Exists &&
                                         before.ModifiedUtc.HasValue &&
                                         after.ModifiedUtc.HasValue &&
                                         before.ModifiedUtc.Value == after.ModifiedUtc.Value;
                if (timestampUnchanged)
                {
                    var remoteContent = await ExecuteRemoteAsync(
                        service => service.OpenTextAsync(_editSession.FilePath),
                        $"Verify content {_editSession.FileName}");
                    if (string.Equals(ComputeHash(remoteContent), ComputeHash(localContent), StringComparison.Ordinal))
                    {
                        warnings.Add("timestamp unchanged after upload");
                    }
                    else
                    {
                        warnings.Add("timestamp unchanged and remote content does not match uploaded content");
                    }
                }

                _editSession.Content = localContent;
                _editSession.WorkingContent = localContent;
                _editSession.OriginalContentHash = ComputeHash(localContent);
                _editSession.OriginalStat = after;
                _editSession.IsDirty = false;
                await MarkEditorCleanAsync();
                UpdateUiState();

                if (warnings.Count == 0)
                {
                    EditorStatusText.Text = "Upload successful.";
                    SetStatus($"Saved and uploaded {_editSession.FileName}.", success: true);
                    CompleteUploadFeedback($"Upload completed for {_editSession.FileName}.", success: true);
                    AddLog($"Uploaded {_editSession.FilePath}");
                }
                else
                {
                    var warningText = string.Join("; ", warnings);
                    EditorStatusText.Text = $"Upload completed with warning: {warningText}";
                    SetStatus($"Upload warning: {warningText}", warning: true);
                    CompleteUploadFeedback($"Upload warning: {warningText}", warning: true);
                    AddLog($"Upload warning for {_editSession.FilePath}: {warningText}");
                }

                await RefreshNodeMetadataAsync(_editSession.FilePath, after);
                return true;
            }
            catch (Exception ex)
            {
                var protocol = _currentProfile == null
                    ? null
                    : (_currentProfile.UseSSH ? "SFTP" : "FTP");
                var detail = RemoteTransferErrorFormatter.Format(
                    ex,
                    fileName: _editSession?.FileName,
                    remotePath: _editSession?.FilePath,
                    protocol: protocol,
                    profileName: _currentProfile?.Name);
                var summary = RemoteTransferErrorFormatter.FormatSummary(ex, _editSession?.FileName);
                EditorStatusText.Text = detail;
                SetStatus(summary, warning: true);
                FailUploadFeedback(detail);
                AddLog(detail);
                return false;
            }
            finally
            {
                _isBusy = false;
                UpdateUiState();
            }
        }

        private async Task RefreshNodeMetadataAsync(string path, RemoteFileStat stat)
        {
            if (string.IsNullOrWhiteSpace(path) || !stat.Exists)
            {
                return;
            }

            foreach (var node in EnumerateNodes(RootNodes))
            {
                if (!string.Equals(node.FullPath.TrimEnd('/'), path.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                node.SizeBytes = stat.SizeBytes;
                node.SizeLabel = stat.IsDirectory ? "dir" : FormatSize(stat.SizeBytes);
                node.ModifiedLabel = stat.ModifiedUtc.HasValue
                    ? AppTimeService.FormatLocalFromUtc(stat.ModifiedUtc)
                    : "—";
                return;
            }

            await Task.CompletedTask;
        }

        private IEnumerable<RemoteTreeNode> EnumerateNodes(IEnumerable<RemoteTreeNode> nodes)
        {
            foreach (var node in nodes)
            {
                yield return node;
                foreach (var child in EnumerateNodes(node.Children))
                {
                    yield return child;
                }
            }
        }

        private void RevertButton_Click(object sender, RoutedEventArgs e)
        {
            if (_editSession == null || _isBusy)
            {
                return;
            }

            _ = RevertCurrentEditorBufferAsync();
        }

        private async Task RevertCurrentEditorBufferAsync()
        {
            if (_editSession == null)
            {
                return;
            }

            try
            {
                await LoadEditorContentAsync(_editSession.FilePath, _editSession.Content);
                _editSession.WorkingContent = _editSession.Content;
                _editSession.IsDirty = false;
                EditorStatusText.Text = "Changes reverted.";
                await MarkEditorCleanAsync();
                UpdateUiState();
            }
            catch (Exception ex)
            {
                EditorStatusText.Text = $"Revert failed: {ex.Message}";
                SetStatus($"Revert failed: {ex.Message}", warning: true);
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

            await LoadProfilesAsync();

            if (_remoteService == null || !_remoteService.IsConnected)
            {
                _ = TryAutoConnectAsync();
                return;
            }

            if (_isEditorOpen && _editSession != null)
            {
                await RefreshActiveEditorFromRemoteAsync();
                return;
            }

            _isBusy = true;
            UpdateUiState();
            try
            {
                if (_isEditorOpen && _editSession == null)
                {
                    ShowBrowserMode();
                }

                await RefreshSelectedFolderInPlaceAsync();
            }
            catch (Exception ex)
            {
                SetStatus($"Refresh failed: {ex.Message}", warning: true);
                AddLog($"Refresh failed: {ex.Message}");
            }
            finally
            {
                _isBusy = false;
                UpdateUiState();
            }
        }

        private RemoteTreeNode? ResolveFolderToRefresh()
        {
            var selected = EnumerateNodes(RootNodes).FirstOrDefault(node => node.IsSelected && !node.IsPlaceholder);
            if (selected == null)
            {
                return RootNodes.Count > 0 ? RootNodes[0] : null;
            }

            if (selected.IsDirectory)
            {
                return selected;
            }

            return FindParentNode(selected) ?? (RootNodes.Count > 0 ? RootNodes[0] : null);
        }

        private async Task RefreshSelectedFolderInPlaceAsync()
        {
            var folder = ResolveFolderToRefresh();
            if (folder == null)
            {
                await LoadRootAsync();
                return;
            }

            await RefreshFolderInPlaceAsync(folder.FullPath);
            SetStatus($"Refreshed {folder.Name}", success: true);
            AddLog($"Refreshed folder {folder.FullPath}");
        }

        private async void ConnectionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressConnectionSelectionChanged || _isBusy)
            {
                return;
            }

            if (GetComboSelectedProfile() is not ConnectionProfile profile)
            {
                return;
            }

            var alreadyConnectedToSame =
                _remoteService != null &&
                _remoteService.IsConnected &&
                string.Equals(_remoteService.ProfileId, profile.Id, StringComparison.OrdinalIgnoreCase);

            if (alreadyConnectedToSame)
            {
                _currentProfile = profile;
                return;
            }

            _currentProfile = profile;
            SetStatus($"Switching to {profile.Name}...", warning: false);
            AddLog($"Profile selected: {profile.Name}");

            try
            {
                if (_remoteService != null)
                {
                    await DisconnectAsync();
                }

                _autoConnectAttempted = false;
                _lastAutoConnectProfileId = string.Empty;
                await ConnectAsync(profile, showDialogOnError: false, isAutoConnect: true);
            }
            catch (Exception ex)
            {
                SetStatus($"Switch failed: {ex.Message}", warning: true);
                AddLog($"Profile switch failed: {ex.Message}");
            }
        }

        private void EditorFallbackTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressFallbackTextChanged || _editSession == null)
            {
                return;
            }

            var buffer = EditorFallbackTextBox.Text ?? string.Empty;
            _editSession.WorkingContent = buffer;
            ApplyEditorDirtyState(!string.Equals(buffer, _editSession.Content, StringComparison.Ordinal));
        }

        private void UpdateMappingBanner()
        {
            if (MappingBanner == null || MappingBannerText == null)
            {
                return;
            }

            var mapping = RemotePathResolver.GetPrimaryMapping(_currentProfile);
            if (mapping == null)
            {
                MappingBanner.Visibility = Visibility.Collapsed;
                MappingBannerText.Text = string.Empty;
                return;
            }

            var localLabel = RemotePathResolver.IsProjectRootLocalPath(mapping.LocalPath)
                ? "(project root)"
                : mapping.LocalPath.Trim();
            var remoteLabel = string.IsNullOrWhiteSpace(mapping.RemotePath) ? "/" : mapping.RemotePath.Trim();
            var root = _currentProfile != null
                ? RemotePathResolver.BuildRemoteRoot(_currentProfile)
                : remoteLabel;

            MappingBannerText.Text =
                $"Path mapping is active: local `{localLabel}` → remote `{remoteLabel}` (browsing `{root}`).";
            MappingBanner.Visibility = Visibility.Visible;
        }

        private void UpdateUiState()
        {
            var connected = _remoteService != null && _remoteService.IsConnected;

            if (EditConnectionButton != null)
            {
                EditConnectionButton.IsEnabled = !_isBusy;
            }

            UpdateChangeDefaultButton();

            if (ConnectionToggleButton != null)
            {
                ConnectionToggleButton.IsEnabled = !_isBusy && (connected || _currentProfile != null || ConnectionComboBox.SelectedItem != null);
                ConnectionToggleButton.Opacity = 1.0;
                ConnectionToggleButton.Content = connected ? "✕" : "⚡";
                ConnectionToggleButton.ToolTip = connected
                    ? Loc.T("deploy.tip.disconnect")
                    : Loc.T("deploy.tip.connect");
            }

            if (RefreshButton != null)
            {
                RefreshButton.IsEnabled = !_isBusy;
            }

            ConnectionComboBox.IsEnabled = !_isBusy;
            RemoteTreeView.IsEnabled = connected && !_isBusy;
            UpdateMappingBanner();
            // Keep tabs enabled visually (disabled ListBox flashes white). Block clicks while busy.
            EditorTabsListBox.IsEnabled = OpenSessions.Count > 0;
            EditorTabsListBox.IsHitTestVisible = !_isBusy && OpenSessions.Count > 0;
            EditorTabsListBox.Opacity = _isBusy && OpenSessions.Count > 0 ? 0.75 : 1.0;
            if (EditorTabsHostBorder != null)
            {
                EditorTabsHostBorder.Opacity = _isBusy && OpenSessions.Count > 0 ? 0.92 : 1.0;
            }
            CloseEditorOverlayButton.IsEnabled = !_isBusy;
            if (EditorRefreshButton != null)
            {
                EditorRefreshButton.IsEnabled = !_isBusy && _editSession != null && connected;
            }
            RevertButton.IsEnabled = !_isBusy && _editSession != null && _editSession.IsDirty;
            SaveUploadButton.IsEnabled = !_isBusy && connected && _editSession != null && _editSession.IsDirty;
            EditorFallbackTextBox.IsEnabled = connected && _editSession != null && !_isBusy && _isEditorOpen;
            EditorWebView.IsEnabled = connected && _editSession != null && !_isBusy && _isEditorOpen;
            UpdateSaveUploadButtonAppearance();
            UpdateRemoteBrowserVisualState();
        }

        private void BeginRemoteLoading(string title, string detail, bool canCancel = false)
        {
            _remoteLoadingDepth++;
            if (RemoteLoadingTitleText != null)
            {
                RemoteLoadingTitleText.Text = string.IsNullOrWhiteSpace(title) ? "Loading remote files..." : title;
            }

            if (RemoteLoadingDetailText != null)
            {
                RemoteLoadingDetailText.Text = string.IsNullOrWhiteSpace(detail)
                    ? "Please wait while we fetch files."
                    : detail;
            }

            if (RemoteLoadingProgressBar != null)
            {
                RemoteLoadingProgressBar.IsIndeterminate = true;
            }

            if (CancelRemoteLoadingButton != null)
            {
                if (canCancel)
                {
                    CancelRemoteLoadingButton.Visibility = Visibility.Visible;
                    CancelRemoteLoadingButton.IsEnabled = true;
                }
                else if (_connectCts == null)
                {
                    CancelRemoteLoadingButton.Visibility = Visibility.Collapsed;
                }
            }

            UpdateRemoteBrowserVisualState();
        }

        private void EndRemoteLoading()
        {
            if (_remoteLoadingDepth > 0)
            {
                _remoteLoadingDepth--;
            }

            if (_remoteLoadingDepth == 0)
            {
                if (RemoteLoadingDetailText != null)
                {
                    RemoteLoadingDetailText.Text = "Please wait while we fetch files.";
                }

                if (CancelRemoteLoadingButton != null)
                {
                    CancelRemoteLoadingButton.Visibility = Visibility.Collapsed;
                    CancelRemoteLoadingButton.IsEnabled = true;
                }
            }

            UpdateRemoteBrowserVisualState();
        }

        private void UpdateRemoteBrowserVisualState()
        {
            var connected = _remoteService != null && _remoteService.IsConnected;
            var isLoading = _remoteLoadingDepth > 0;

            if (RemoteLoadingOverlay != null)
            {
                RemoteLoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
            }

            if (RemoteEmptyState != null)
            {
                var showEmpty = !isLoading && connected && RootNodes.Count == 0;
                RemoteEmptyState.Visibility = showEmpty ? Visibility.Visible : Visibility.Collapsed;
            }

            if (RemoteEmptyHintText != null)
            {
                RemoteEmptyHintText.Text = connected
                    ? "Remote directory is empty or waiting for refresh."
                    : "Connect to load remote files.";
            }
        }

        private void UpdateSaveUploadButtonAppearance()
        {
            if (SaveUploadButton == null)
            {
                return;
            }

            var hasDirtyChanges = _editSession?.IsDirty == true;
            SaveUploadButton.Content = "⬆";
            ApplyToolbarActionState(SaveUploadButton, hasDirtyChanges);
            ApplyToolbarActionState(RevertButton, hasDirtyChanges);
        }

        private static void ApplyToolbarActionState(System.Windows.Controls.Button? button, bool active)
        {
            if (button == null)
            {
                return;
            }

            button.Tag = active ? "active" : null;
            if (!active)
            {
                button.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
                button.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
                button.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
                return;
            }

            var tokens = ThemeService.Instance.CurrentTokens;
            button.Foreground = tokens.GetBrush(
                "editor.actionActiveForeground",
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E6B84D"));
            button.Background = tokens.GetBrush(
                "editor.actionActiveBackground",
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1A1408"));
            button.BorderBrush = tokens.GetBrush(
                "editor.actionActiveBorder",
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E6B84D"));
        }

        private void BeginUploadFeedback(long expectedBytes)
        {
            CancelUploadStripAutoHide();
            ShowUploadStripImmediate();
            ApplyUploadStripTheme(uploading: true, success: false, warning: false, failed: false);

            if (UploadStatusLabel != null)
            {
                UploadStatusLabel.Text = "UPLOADING";
            }

            UploadProgressBar.IsIndeterminate = expectedBytes <= 0;
            UploadProgressBar.Value = 0;
            SetUploadHint("Uploading...", warning: false, success: false, uploading: true);
        }

        private void UpdateUploadFeedback(RemoteUploadProgress progress)
        {
            if (progress == null)
            {
                return;
            }

            UploadProgressBar.IsIndeterminate = progress.IsIndeterminate;
            if (!progress.IsIndeterminate)
            {
                var value = Math.Max(0, Math.Min(100, progress.Percent));
                UploadProgressBar.Value = value;
            }

            if (progress.TotalBytes > 0)
            {
                var fileHint = string.IsNullOrWhiteSpace(progress.CurrentFileName)
                    ? string.Empty
                    : $"{progress.CurrentFileName} · ";
                SetUploadHint(
                    $"{fileHint}{FormatSize(progress.BytesTransferred)} / {FormatSize(progress.TotalBytes)} ({Math.Max(0, Math.Min(100, progress.Percent)):0}%)",
                    warning: false,
                    success: false,
                    uploading: true);
            }
            else
            {
                SetUploadHint("Uploading...", warning: false, success: false, uploading: true);
            }
        }

        private void CompleteUploadFeedback(string hint, bool success = false, bool warning = false)
        {
            CancelUploadStripAutoHide();
            ApplyUploadStripTheme(uploading: false, success: success, warning: warning, failed: false);

            if (UploadStatusLabel != null)
            {
                UploadStatusLabel.Text = success ? "DONE" : (warning ? "WARNING" : string.Empty);
            }

            UploadProgressBar.IsIndeterminate = false;
            UploadProgressBar.Value = success || warning ? 100 : 0;
            SetUploadHint(hint, warning: warning, success: success, uploading: false);

            if (success)
            {
                AnimateUploadStripShow();
                ScheduleUploadSuccessHide();
            }
            else
            {
                ShowUploadStripImmediate();
            }
        }

        private void FailUploadFeedback(string hint)
        {
            CancelUploadStripAutoHide();
            ShowUploadStripImmediate();
            ExpandUploadStripForDetail();
            ApplyUploadStripTheme(uploading: false, success: false, warning: false, failed: true);

            if (UploadStatusLabel != null)
            {
                UploadStatusLabel.Text = "FAILED";
            }

            UploadProgressBar.IsIndeterminate = false;
            UploadProgressBar.Value = 0;
            SetUploadHint(hint, warning: true, success: false, uploading: false);
        }

        private void ResetUploadFeedback()
        {
            CancelUploadStripAutoHide();
            StopUploadStripAnimations();
            SetUploadStripVisible(false);
            RestoreUploadStripLayout();
            ApplyUploadStripTheme(uploading: false, success: false, warning: false, failed: false);

            if (UploadStatusLabel != null)
            {
                UploadStatusLabel.Text = string.Empty;
            }

            UploadProgressBar.IsIndeterminate = false;
            UploadProgressBar.Value = 0;
            UploadProgressHintText.Text = string.Empty;
            UploadProgressHintText.Foreground = FindBrush("Text.Muted", System.Windows.Media.Brushes.LightGray);
        }

        private void SetUploadStripVisible(bool visible)
        {
            if (UploadStatusStrip != null)
            {
                UploadStatusStrip.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void ShowUploadStripImmediate()
        {
            StopUploadStripAnimations();
            RestoreUploadStripLayout();
            SetUploadStripVisible(true);
        }

        private void RestoreUploadStripLayout()
        {
            if (UploadStatusStrip == null)
            {
                return;
            }

            UploadStatusStrip.Opacity = 1;
            UploadStatusStrip.Height = UploadStripHeight;
            UploadStatusStrip.MinHeight = UploadStripHeight;
            UploadStatusStrip.Margin = UploadStripMargin;
            UploadStatusStrip.ClipToBounds = true;
            if (UploadProgressHintText != null)
            {
                UploadProgressHintText.TextWrapping = TextWrapping.NoWrap;
                UploadProgressHintText.TextTrimming = TextTrimming.CharacterEllipsis;
            }
        }

        private void ExpandUploadStripForDetail()
        {
            if (UploadStatusStrip == null)
            {
                return;
            }

            UploadStatusStrip.BeginAnimation(FrameworkElement.HeightProperty, null);
            UploadStatusStrip.Height = double.NaN;
            UploadStatusStrip.MinHeight = UploadStripHeight;
            UploadStatusStrip.ClipToBounds = false;
            if (UploadProgressHintText != null)
            {
                UploadProgressHintText.TextWrapping = TextWrapping.Wrap;
                UploadProgressHintText.TextTrimming = TextTrimming.None;
            }
        }

        private void StopUploadStripAnimations()
        {
            if (UploadStatusStrip == null)
            {
                return;
            }

            UploadStatusStrip.BeginAnimation(UIElement.OpacityProperty, null);
            UploadStatusStrip.BeginAnimation(FrameworkElement.HeightProperty, null);
            UploadStatusStrip.BeginAnimation(FrameworkElement.MarginProperty, null);
        }

        private void CancelUploadStripAutoHide()
        {
            _uploadStripAnimToken++;
            if (_uploadSuccessHideTimer != null)
            {
                _uploadSuccessHideTimer.Stop();
                _uploadSuccessHideTimer.Tick -= UploadSuccessHideTimer_Tick;
            }
        }

        private void ScheduleUploadSuccessHide()
        {
            if (_uploadSuccessHideTimer != null)
            {
                _uploadSuccessHideTimer.Stop();
                _uploadSuccessHideTimer.Tick -= UploadSuccessHideTimer_Tick;
            }

            var token = _uploadStripAnimToken;
            _uploadSuccessHideTimer ??= new DispatcherTimer();
            _uploadSuccessHideTimer.Interval = UploadSuccessHold;
            _uploadSuccessHideTimer.Tick += UploadSuccessHideTimer_Tick;
            _uploadSuccessHideTimer.Tag = token;
            _uploadSuccessHideTimer.Start();
        }

        private void UploadSuccessHideTimer_Tick(object? sender, EventArgs e)
        {
            if (_uploadSuccessHideTimer != null)
            {
                _uploadSuccessHideTimer.Stop();
                _uploadSuccessHideTimer.Tick -= UploadSuccessHideTimer_Tick;
            }

            var token = _uploadSuccessHideTimer?.Tag is int t ? t : _uploadStripAnimToken;
            if (token != _uploadStripAnimToken)
            {
                return;
            }

            AnimateUploadStripHide(token);
        }

        private void AnimateUploadStripShow()
        {
            if (UploadStatusStrip == null)
            {
                return;
            }

            StopUploadStripAnimations();
            UploadStatusStrip.Height = UploadStripHeight;
            UploadStatusStrip.Margin = UploadStripMargin;
            UploadStatusStrip.Opacity = 0;
            SetUploadStripVisible(true);

            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            UploadStatusStrip.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        }

        private void AnimateUploadStripHide(int token)
        {
            if (UploadStatusStrip == null || UploadStatusStrip.Visibility != Visibility.Visible)
            {
                return;
            }

            var fromHeight = UploadStatusStrip.ActualHeight > 0 ? UploadStatusStrip.ActualHeight : UploadStripHeight;
            var fadeOut = new DoubleAnimation(UploadStatusStrip.Opacity, 0, TimeSpan.FromMilliseconds(420))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            var collapse = new DoubleAnimation(fromHeight, 0, TimeSpan.FromMilliseconds(420))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            var marginOut = new ThicknessAnimation(UploadStatusStrip.Margin, new Thickness(0), TimeSpan.FromMilliseconds(420))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            fadeOut.Completed += (_, _) =>
            {
                if (token != _uploadStripAnimToken)
                {
                    return;
                }

                ResetUploadFeedback();
            };
            UploadStatusStrip.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            UploadStatusStrip.BeginAnimation(FrameworkElement.HeightProperty, collapse);
            UploadStatusStrip.BeginAnimation(FrameworkElement.MarginProperty, marginOut);
        }

        /// <summary>
        /// Color psychology: blue = in-progress/info; green = success/completion (single hue, no blue+green mix);
        /// amber = caution; red = failure.
        /// </summary>
        private void ApplyUploadStripTheme(bool uploading, bool success, bool warning, bool failed)
        {
            System.Windows.Media.Brush border;
            System.Windows.Media.Brush surface;
            System.Windows.Media.Brush accent;
            System.Windows.Media.Brush label;

            if (success)
            {
                border = FindBrush("Status.Success", System.Windows.Media.Brushes.LightGreen);
                surface = FindBrush("Status.SuccessSurface", FindBrush("Surface.Input", System.Windows.Media.Brushes.DimGray));
                accent = border;
                label = border;
            }
            else if (warning)
            {
                border = FindBrush("Status.Warning", System.Windows.Media.Brushes.Orange);
                surface = FindBrush("Status.WarningSurface", FindBrush("Surface.Input", System.Windows.Media.Brushes.DimGray));
                accent = border;
                label = border;
            }
            else if (failed)
            {
                border = FindBrush("Status.Error", System.Windows.Media.Brushes.OrangeRed);
                surface = FindBrush("Status.ErrorSurface", FindBrush("Surface.Input", System.Windows.Media.Brushes.DimGray));
                accent = border;
                label = border;
            }
            else if (uploading)
            {
                border = FindBrush("Status.Info", FindBrush("Accent.Primary", System.Windows.Media.Brushes.DodgerBlue));
                surface = FindBrush("Status.InfoSurface", FindBrush("Surface.Input", System.Windows.Media.Brushes.DimGray));
                accent = FindBrush("Accent.Primary", System.Windows.Media.Brushes.DodgerBlue);
                label = accent;
            }
            else
            {
                border = FindBrush("Border.Subtle", System.Windows.Media.Brushes.Gray);
                surface = FindBrush("Surface.Input", System.Windows.Media.Brushes.DimGray);
                accent = FindBrush("Accent.Primary", System.Windows.Media.Brushes.DodgerBlue);
                label = FindBrush("Text.Muted", System.Windows.Media.Brushes.LightGray);
            }

            if (UploadStatusStrip != null)
            {
                UploadStatusStrip.BorderBrush = border;
                UploadStatusStrip.Background = surface;
            }

            UploadProgressBar.Foreground = accent;
            UploadProgressBar.Background = FindBrush("Border.Subtle", System.Windows.Media.Brushes.Gray);

            if (UploadStatusLabel != null)
            {
                UploadStatusLabel.Foreground = label;
            }
        }

        private static System.Windows.Media.Brush FindBrush(string key, System.Windows.Media.Brush fallback)
        {
            return System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush ?? fallback;
        }

        private void SetUploadHint(string text, bool warning, bool success, bool uploading = false)
        {
            UploadProgressHintText.Text = text ?? string.Empty;

            if (warning)
            {
                UploadProgressHintText.Foreground = FindBrush("Status.Warning", System.Windows.Media.Brushes.Orange);
                return;
            }

            if (success)
            {
                UploadProgressHintText.Foreground = FindBrush("Status.Success", System.Windows.Media.Brushes.LightGreen);
                return;
            }

            if (uploading)
            {
                UploadProgressHintText.Foreground = FindBrush("Text.Primary", System.Windows.Media.Brushes.White);
                return;
            }

            UploadProgressHintText.Foreground = FindBrush("Text.Muted", System.Windows.Media.Brushes.LightGray);
        }

        private void SetStatus(string text, bool warning = false, bool success = false)
        {
            StatusTextBlock.Text = text;
            if (warning)
            {
                StatusTextBlock.Foreground = System.Windows.Application.Current?.TryFindResource("Status.Warning") as System.Windows.Media.Brush
                    ?? System.Windows.Media.Brushes.Orange;
                return;
            }

            if (success)
            {
                StatusTextBlock.Foreground = System.Windows.Application.Current?.TryFindResource("Status.Success") as System.Windows.Media.Brush
                    ?? System.Windows.Media.Brushes.LightGreen;
                return;
            }

            StatusTextBlock.Foreground = System.Windows.Application.Current?.TryFindResource("Text.Muted") as System.Windows.Media.Brush
                ?? System.Windows.Media.Brushes.LightGray;
        }

        private void AddLog(string message)
        {
            var line = $"[{AppTimeService.LocalNow:HH:mm:ss}] {message}";
            _logLines.Add(line);
            while (_logLines.Count > 12)
            {
                _logLines.RemoveAt(0);
            }

            OperationLogText.Text = string.Join(Environment.NewLine, _logLines);
        }

        private sealed class FtpProfileListItem
        {
            public FtpProfileListItem(ConnectionProfile profile, bool isProjectDefault, bool isAssigned)
            {
                Profile = profile;
                IsProjectDefault = isProjectDefault;
                IsAssigned = isAssigned;
            }

            public ConnectionProfile Profile { get; }
            public bool IsProjectDefault { get; }
            public bool IsAssigned { get; }
            public bool IsFavorite => Profile?.IsFavorite == true;
            public string Name => Profile?.Name ?? string.Empty;
            public string Host => Profile?.Host ?? string.Empty;

            public override string ToString() => Name;
        }

        private sealed class WebMessage
        {
            public string? Type { get; set; }
            public bool? Value { get; set; }
        }

        private static string ComputeHash(string text)
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
            var hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash);
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            var order = Math.Min(units.Length - 1, (int)Math.Floor(Math.Log(bytes, 1024)));
            var adjusted = bytes / Math.Pow(1024, order);
            return $"{adjusted:0.##} {units[order]}";
        }
    }
}
