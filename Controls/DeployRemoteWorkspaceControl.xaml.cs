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
using GitDeployPro.Models;
using GitDeployPro.Services;
using GitDeployPro.Services.Remote;
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
        private bool _isEditorOpen;
        private bool _isHostTeardown;
        private bool _autoConnectAttempted;
        private bool _autoConnectInProgress;
        private bool _suppressTabSelectionChanged;
        private bool _editorRecoveryInProgress;
        private bool _editorWarmupInProgress;
        private bool _editorWarmupLoggedReady;
        private int _remoteLoadingDepth;
        private int _editorFontSize = 14;
        private DateTime _lastEditorRecoveryAttemptUtc = DateTime.MinValue;
        private DateTime _lastEditorWarmupAttemptUtc = DateTime.MinValue;
        private string _lastAutoConnectProfileId = string.Empty;
        private static string? _codeEditorHtmlTemplate;

        public ObservableCollection<RemoteTreeNode> RootNodes { get; } = new();
        public ObservableCollection<RemoteEditSession> OpenSessions { get; } = new();
        public IReadOnlyList<int> EditorFontSizes { get; } = EditorFontSizeOptions;
        public event EventHandler<RemoteEditorModeChangedEventArgs>? EditorModeChanged;
        public bool IsEditorOpen => _isEditorOpen;

        public DeployRemoteWorkspaceControl()
        {
            InitializeComponent();
            DataContext = this;
            RemoteTreeView.ItemsSource = RootNodes;
            EditorTabsListBox.ItemsSource = OpenSessions;
            _editorFontSize = EditorFontSizeOptions.Contains(_globalEditorFontSize) ? _globalEditorFontSize : 14;
            EditorFontSizeComboBox.SelectedItem = _editorFontSize;
            EditorFallbackTextBox.FontSize = _editorFontSize;
            EditorFallbackTextBox.IsEnabled = false;
            ShowBrowserMode(notify: false);
            ApplyWorkspacePanelLayout(editorMode: false);
            ResetUploadFeedback();
            UpdateRemoteBrowserVisualState();
            ConfigurationService.ConnectionsChanged += OnConnectionsChanged;
            Unloaded += DeployRemoteWorkspaceControl_Unloaded;
        }

        /// <summary>
        /// Call when the host page is truly leaving — not when the control is temporarily reparented.
        /// </summary>
        public void NotifyHostTeardown()
        {
            _isHostTeardown = true;
            ConfigurationService.ConnectionsChanged -= OnConnectionsChanged;
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
            if (_editorWebEventsBound && EditorWebView?.CoreWebView2 != null)
            {
                EditorWebView.CoreWebView2.WebMessageReceived -= EditorWebView_WebMessageReceived;
                EditorWebView.CoreWebView2.NavigationCompleted -= EditorWebView_NavigationCompleted;
                _editorWebEventsBound = false;
            }

            // Reparenting for overlays also fires Unloaded. Disconnecting there wiped FTP and closed the editor.
            if (!_isHostTeardown)
            {
                return;
            }

            ConfigurationService.ConnectionsChanged -= OnConnectionsChanged;

            if (_remoteService != null && _remoteService.IsConnected)
            {
                await DisconnectAsync();
            }
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

            if (!_editorUsingFallback || !allowRecovery)
            {
                EditorWebView.Visibility = Visibility.Visible;
                EditorFallbackTextBox.Visibility = Visibility.Collapsed;
            }

            try
            {
                await EnsureEditorHtmlTemplateAsync();
                await EditorWebView.EnsureCoreWebView2Async().WaitAsync(TimeSpan.FromMilliseconds(Math.Max(200, waitMs)));
                if (!_editorWebEventsBound && EditorWebView.CoreWebView2 != null)
                {
                    EditorWebView.CoreWebView2.WebMessageReceived += EditorWebView_WebMessageReceived;
                    EditorWebView.CoreWebView2.NavigationCompleted += EditorWebView_NavigationCompleted;
                    _editorWebEventsBound = true;
                }

                if (!_editorWebReady && EditorWebView.CoreWebView2 != null)
                {
                    EditorWebView.Visibility = Visibility.Visible;
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
        }

        private async Task TryRecoverEditorHostAsync()
        {
            if (!_editorUsingFallback || _editorRecoveryInProgress)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (now - _lastEditorRecoveryAttemptUtc < TimeSpan.FromSeconds(6))
            {
                return;
            }

            _editorRecoveryInProgress = true;
            _lastEditorRecoveryAttemptUtc = now;
            try
            {
                await EnsureEditorHostAsync(allowRecovery: true, waitMs: 3000);
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

            var scriptResult = await EditorWebView.CoreWebView2.ExecuteScriptAsync("window.__getValue && window.__getValue()");
            return string.IsNullOrWhiteSpace(scriptResult)
                ? string.Empty
                : JsonSerializer.Deserialize<string>(scriptResult) ?? string.Empty;
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

        private async Task LoadProfilesAsync()
        {
            var previousSelectedId = (ConnectionComboBox.SelectedItem as ConnectionProfile)?.Id
                ?? _currentProfile?.Id;

            var profiles = _configService.LoadConnections()
                .Where(IsDeployRemoteProfile)
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ConnectionComboBox.ItemsSource = profiles;
            if (profiles.Count == 0)
            {
                ConnectionComboBox.SelectedItem = null;
                _currentProfile = null;
                SetStatus("No FTP/SFTP profile found. Create one in Connection Manager.", warning: true);
                return;
            }

            var preferred = profiles.FirstOrDefault(p =>
                !string.IsNullOrWhiteSpace(_projectConfig.ConnectionProfileId) &&
                string.Equals(p.Id, _projectConfig.ConnectionProfileId, StringComparison.OrdinalIgnoreCase));
            var keepCurrent = profiles.FirstOrDefault(p =>
                !string.IsNullOrWhiteSpace(previousSelectedId) &&
                string.Equals(p.Id, previousSelectedId, StringComparison.OrdinalIgnoreCase));
            var selected = preferred ?? keepCurrent ?? profiles.First();
            ConnectionComboBox.SelectedItem = selected;
            _currentProfile = selected;

            if (_remoteService != null && _remoteService.IsConnected && _currentProfile != null &&
                !string.Equals(_remoteService.ProfileId, _currentProfile.Id, StringComparison.OrdinalIgnoreCase))
            {
                await DisconnectAsync();
            }
        }

        private async Task TryAutoConnectAsync()
        {
            if (_autoConnectInProgress || _currentProfile == null)
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
            if (profile == null) return false;
            if (string.IsNullOrWhiteSpace(profile.Host)) return false;
            return profile.DbType == DatabaseType.None;
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

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

            if (_remoteService != null && _remoteService.IsConnected)
            {
                await DisconnectAsync();
                return;
            }

            if (ConnectionComboBox.SelectedItem is not ConnectionProfile profile)
            {
                ModernMessageBox.Show("Select a connection profile first.", "Remote workspace", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await ConnectAsync(profile, showDialogOnError: true, isAutoConnect: false);
        }

        private async Task ConnectAsync(ConnectionProfile profile, bool showDialogOnError, bool isAutoConnect)
        {
            _isBusy = true;
            BeginRemoteLoading(
                isAutoConnect ? "Auto-connecting..." : "Connecting...",
                $"Preparing remote workspace for {profile.Name}...");
            SetStatus(
                isAutoConnect
                    ? $"Auto-connecting to {profile.Name}..."
                    : $"Connecting to {profile.Name} ({profile.Host})...",
                warning: false,
                success: false);
            UpdateUiState();
            try
            {
                _remoteService = profile.UseSSH
                    ? new SftpRemoteFileService()
                    : new FtpRemoteFileService();
                await _remoteService.ConnectAsync(profile);
                if (_remoteService == null || !_remoteService.IsConnected)
                {
                    throw new InvalidOperationException("Remote service reported disconnected after connect.");
                }

                _currentProfile = profile;
                AddLog(isAutoConnect
                    ? $"Transport connected using {(profile.UseSSH ? "SFTP" : "FTP")}; verifying listing..."
                    : $"Transport connected using {(profile.UseSSH ? "SFTP" : "FTP")}; verifying listing...");
                _ = WarmupEditorInBackgroundAsync("FTP connected");
                await LoadRootAsync();

                if (_remoteService == null || !_remoteService.IsConnected)
                {
                    throw new InvalidOperationException("Remote session closed while loading directory listing.");
                }

                SetStatus(
                    isAutoConnect
                        ? $"Auto-connected to {profile.Name} ({profile.Host})."
                        : $"Connected to {profile.Name} ({profile.Host}).",
                    success: true);
            }
            catch (Exception ex)
            {
                await DisposeRemoteServiceQuietlyAsync();
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
                _isBusy = false;
                EndRemoteLoading();
                UpdateUiState();
            }
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

        private async Task LoadRootAsync()
        {
            if (_remoteService == null || !_remoteService.IsConnected || _currentProfile == null)
            {
                throw new InvalidOperationException("Remote connection is not ready for directory listing.");
            }

            _ = WarmupEditorInBackgroundAsync("FTP listing started");
            var root = RemotePathResolver.BuildRemoteRoot(_currentProfile);
            BeginRemoteLoading("Loading remote files...", $"Fetching {root}");
            try
            {
                var entries = await ExecuteRemoteAsync(service => service.ListDirectoryAsync(root), "Load root");
                var nodes = _treeBuilder.BuildNodes(entries);
                RootNodes.Clear();
                foreach (var node in nodes)
                {
                    RootNodes.Add(node);
                }

                SetStatus($"Loaded {RootNodes.Count} item(s) from {root}", success: true);
                AddLog($"Root loaded from {root}");
            }
            finally
            {
                EndRemoteLoading();
                UpdateRemoteBrowserVisualState();
            }
        }

        private async Task LoadChildrenAsync(RemoteTreeNode node)
        {
            if (_remoteService == null || !_remoteService.IsConnected || node == null || !node.IsDirectory)
            {
                return;
            }

            var path = RemotePathResolver.EnsureTrailingSlash(node.FullPath);
            var entries = await ExecuteRemoteAsync(service => service.ListDirectoryAsync(path), $"Expand {node.Name}");
            var children = _treeBuilder.BuildNodes(entries);
            node.Children.Clear();
            foreach (var child in children)
            {
                node.Children.Add(child);
            }

            node.IsLoaded = true;
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

            treeItem.IsSelected = true;
            treeItem.Focus();

            var actions = BuildRemoteContextActions(node);
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
            if (_editSession == null || _remoteService == null || !_remoteService.IsConnected)
            {
                return;
            }

            var active = _editSession;
            var text = await ExecuteRemoteAsync(service => service.OpenTextAsync(active.FilePath), $"Reload {active.FileName}");
            var stat = await ExecuteRemoteAsync(service => service.GetFileStatAsync(active.FilePath), $"Reload stat {active.FileName}");

            active.Content = text;
            active.WorkingContent = text;
            active.OriginalContentHash = ComputeHash(text);
            active.OriginalStat = stat;
            active.IsDirty = false;

            await LoadEditorContentAsync(active.FilePath, text);
            await MarkEditorCleanAsync();
            EditorStatusText.Text = "File reloaded from remote.";
            UpdateUiState();
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

        private IReadOnlyList<AppContextMenuAction> BuildRemoteContextActions(RemoteTreeNode node)
        {
            var actions = new List<AppContextMenuAction>
            {
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
                    Id = "download",
                    Label = node.IsDirectory ? "Download Folder" : "Download File",
                    IconGlyph = "⬇",
                    Execute = _ => _ = DownloadNodeAsync(node)
                },
                AppContextMenuAction.Separator("remote-action-separator"),
                new()
                {
                    Id = "rename",
                    Label = "Rename",
                    IconGlyph = "✏",
                    Execute = _ => _ = RenameNodeAsync(node)
                },
                new()
                {
                    Id = "move",
                    Label = "Move",
                    IconGlyph = "➡",
                    Execute = _ => _ = MoveNodeAsync(node)
                },
                new()
                {
                    Id = "delete",
                    Label = "Delete",
                    IconGlyph = "🗑",
                    IsDestructive = true,
                    Execute = _ => _ = DeleteNodeAsync(node)
                }
            };
            return actions;
        }

        private string ResolveDefaultDownloadRoot()
        {
            var projectRoot = _configService.LoadGlobalConfig().LastProjectPath;
            var mapping = RemotePathResolver.GetPrimaryMapping(_currentProfile);
            return RemotePathResolver.ResolveLocalDownloadRoot(projectRoot, mapping);
        }

        private async Task DownloadNodeAsync(RemoteTreeNode node)
        {
            if (_isBusy || _remoteService == null || !_remoteService.IsConnected || _currentProfile == null)
            {
                return;
            }

            using var dialog = new Forms.FolderBrowserDialog
            {
                Description = "Choose where to download remote files",
                SelectedPath = ResolveDefaultDownloadRoot()
            };

            if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
            {
                return;
            }

            _isBusy = true;
            UpdateUiState();
            try
            {
                var remoteRoot = RemotePathResolver.BuildRemoteRoot(_currentProfile);
                var localTarget = RemotePathResolver.BuildLocalDownloadPath(
                    dialog.SelectedPath,
                    remoteRoot,
                    node.FullPath,
                    node.IsDirectory,
                    node.Name);

                if (node.IsDirectory)
                {
                    await ExecuteRemoteAsync(
                        service => service.DownloadDirectoryAsync(node.FullPath, localTarget),
                        $"Download directory {node.Name}");
                    SetStatus($"Folder downloaded to {localTarget}", success: true);
                    AddLog($"Downloaded folder {node.FullPath} -> {localTarget}");
                }
                else
                {
                    await ExecuteRemoteAsync(
                        service => service.DownloadFileAsync(node.FullPath, localTarget),
                        $"Download file {node.Name}");
                    SetStatus($"File downloaded to {localTarget}", success: true);
                    AddLog($"Downloaded file {node.FullPath} -> {localTarget}");
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Download failed: {ex.Message}", warning: true);
                AddLog($"Download failed: {ex.Message}");
                ModernMessageBox.Show($"Download failed:\n{ex.Message}", "Remote workspace", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isBusy = false;
                UpdateUiState();
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

        private async Task DeleteNodeAsync(RemoteTreeNode node)
        {
            if (_isBusy || _remoteService == null || !_remoteService.IsConnected)
            {
                return;
            }

            var result = ModernMessageBox.ShowWithResult(
                $"Delete '{node.Name}' {(node.IsDirectory ? "folder" : "file")}? This action cannot be undone.",
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
                await ExecuteRemoteAsync(
                    service => service.DeleteAsync(node.FullPath, node.IsDirectory),
                    $"Delete {node.Name}");

                await RemoveDeletedSessionsAsync(node.FullPath, node.IsDirectory);

                // Only drop that item (or refresh its parent folder) — never reload the whole FTP tree.
                if (!TryRemoveNodeFromTree(node))
                {
                    await RefreshContainingFolderAsync(node);
                }
                else
                {
                    UpdateRemoteBrowserVisualState();
                }

                SetStatus($"Deleted {node.Name}", success: true);
                AddLog($"Deleted {node.FullPath}");
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

            // Deleted item was at root — refresh root listing in place without a full disconnect cycle.
            var root = RemotePathResolver.BuildRemoteRoot(_currentProfile);
            var entries = await ExecuteRemoteAsync(service => service.ListDirectoryAsync(root), "Refresh root after delete");
            var nodes = _treeBuilder.BuildNodes(entries);
            RootNodes.Clear();
            foreach (var node in nodes)
            {
                RootNodes.Add(node);
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
            }
        }

        private async void SaveUploadButton_Click(object sender, RoutedEventArgs e)
        {
            await SaveUploadCurrentEditorFileAsync();
        }

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
                EditorStatusText.Text = $"Upload failed: {ex.Message}";
                SetStatus($"Upload failed: {ex.Message}", warning: true);
                FailUploadFeedback($"Upload failed: {ex.Message}");
                AddLog($"Upload failed: {ex.Message}");
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

            _isBusy = true;
            UpdateUiState();
            try
            {
                if (_isEditorOpen && _editSession == null)
                {
                    ShowBrowserMode();
                    await LoadRootAsync();
                    return;
                }

                if (_isEditorOpen && _editSession != null)
                {
                    if (_editSession.IsDirty)
                    {
                        var confirm = ModernMessageBox.ShowWithResult(
                            "Current file has local edits. Refresh will reload file from remote and discard local edits. Continue?",
                            "Refresh editor",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning,
                            primaryText: "Refresh",
                            secondaryText: "Cancel");
                        if (confirm != MessageBoxResult.Yes)
                        {
                            return;
                        }
                    }

                    await ReloadActiveSessionFromRemoteAsync();
                    return;
                }

                await LoadRootAsync();
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

        private void ConnectionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ConnectionComboBox.SelectedItem is ConnectionProfile profile)
            {
                _currentProfile = profile;
                SetStatus($"Selected profile: {profile.Name}", warning: false);
            }
        }

        private async void EditorFontSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (EditorFontSizeComboBox.SelectedItem is not int selected || selected == _editorFontSize)
            {
                return;
            }

            _editorFontSize = selected;
            _globalEditorFontSize = selected;
            await ApplyEditorFontSizeAsync();
            AddLog($"Editor font size: {_editorFontSize}px");
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

        private void UpdateUiState()
        {
            var connected = _remoteService != null && _remoteService.IsConnected;

            ConnectButton.Content = connected ? "Disconnect" : "Connect";
            ConnectButton.IsEnabled = !_isBusy && _currentProfile != null;
            RefreshButton.IsEnabled = !_isBusy;
            ConnectionComboBox.IsEnabled = !_isBusy;
            RemoteTreeView.IsEnabled = connected && !_isBusy;
            // Keep tabs enabled visually (disabled ListBox flashes white). Block clicks while busy.
            EditorTabsListBox.IsEnabled = OpenSessions.Count > 0;
            EditorTabsListBox.IsHitTestVisible = !_isBusy && OpenSessions.Count > 0;
            EditorTabsListBox.Opacity = _isBusy && OpenSessions.Count > 0 ? 0.75 : 1.0;
            if (EditorTabsHostBorder != null)
            {
                EditorTabsHostBorder.Opacity = _isBusy && OpenSessions.Count > 0 ? 0.92 : 1.0;
            }
            CloseEditorOverlayButton.IsEnabled = !_isBusy;
            EditorFontSizeComboBox.IsEnabled = !_isBusy;
            RevertButton.IsEnabled = !_isBusy && _editSession != null && _editSession.IsDirty;
            SaveUploadButton.IsEnabled = !_isBusy && connected && _editSession != null && _editSession.IsDirty;
            EditorFallbackTextBox.IsEnabled = connected && _editSession != null && !_isBusy && _isEditorOpen;
            EditorWebView.IsEnabled = connected && _editSession != null && !_isBusy && _isEditorOpen;
            UpdateSaveUploadButtonAppearance();
            UpdateRemoteBrowserVisualState();
        }

        private void BeginRemoteLoading(string title, string detail)
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

            UpdateRemoteBrowserVisualState();
        }

        private void EndRemoteLoading()
        {
            if (_remoteLoadingDepth > 0)
            {
                _remoteLoadingDepth--;
            }

            if (_remoteLoadingDepth == 0 && RemoteLoadingDetailText != null)
            {
                RemoteLoadingDetailText.Text = "Please wait while we fetch files.";
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
            var hasDirtyChanges = _editSession?.IsDirty == true;
            SaveUploadButton.Content = hasDirtyChanges ? "Save/Upload *" : "Save/Upload";

            var targetStyle = TryFindResource(hasDirtyChanges
                ? "SaveUploadButtonDirtyStyle"
                : "SaveUploadButtonDefaultStyle") as Style;
            if (targetStyle != null && !ReferenceEquals(SaveUploadButton.Style, targetStyle))
            {
                SaveUploadButton.Style = targetStyle;
            }
        }

        private void BeginUploadFeedback(long expectedBytes)
        {
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
                SetUploadHint(
                    $"{FormatSize(progress.BytesTransferred)} / {FormatSize(progress.TotalBytes)} ({Math.Max(0, Math.Min(100, progress.Percent)):0}%)",
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
            ApplyUploadStripTheme(uploading: false, success: success, warning: warning, failed: false);

            if (UploadStatusLabel != null)
            {
                UploadStatusLabel.Text = success ? "DONE" : (warning ? "WARNING" : string.Empty);
            }

            UploadProgressBar.IsIndeterminate = false;
            UploadProgressBar.Value = success || warning ? 100 : 0;
            SetUploadHint(hint, warning: warning, success: success, uploading: false);
        }

        private void FailUploadFeedback(string hint)
        {
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
