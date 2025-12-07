using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FluentFTP;
using GitDeployPro.Controls;
using GitDeployPro.Models;
using GitDeployPro.Services;
using GitDeployPro.Windows;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using ListViewItem = System.Windows.Controls.ListViewItem;
using Forms = System.Windows.Forms;

namespace GitDeployPro.Pages
{
    public partial class FtpExplorerPage : Page
    {
        private readonly ConfigurationService _configService = new();
        private readonly ObservableCollection<ConnectionProfile> _profiles = new();
        private readonly ObservableCollection<FtpExplorerItem> _entries = new();

        private ProjectConfig _projectConfig = new();
        private string _projectPath = string.Empty;
        private ConnectionProfile? _currentProfile;
        private PathMapping? _currentMapping;
        private string _currentPath = "/";
        private string _remoteRoot = "/";
        private AsyncFtpClient? _ftpClient;
        private SftpClient? _sftpClient;
        private bool _isBusy;
        private bool _suppressSelectionChanged;
        private string _downloadRoot = string.Empty;
        private bool _downloadPathManuallySet;
        private long _downloadTotalBytes;
        private long _downloadTransferredBytes;
        private bool _overwriteAllConfirmed;
        private bool _skipAllExisting;
        private DownloadSessionInfo? _pendingSession;
        private const string DownloadSessionFileName = ".gitdeploy.download.session";
        private CancellationTokenSource? _downloadCts;
        private bool _isDownloadPaused;

        public FtpExplorerPage()
        {
            InitializeComponent();
            EntriesListView.ItemsSource = _entries;
            ConnectionsCombo.ItemsSource = _profiles;

            Loaded += FtpExplorerPage_Loaded;
            Unloaded += FtpExplorerPage_Unloaded;
            UpdateDownloadPathText();
        }

        private async void FtpExplorerPage_Loaded(object sender, RoutedEventArgs e)
        {
            await InitializeAsync();
        }

        private async void FtpExplorerPage_Unloaded(object sender, RoutedEventArgs e)
        {
            await DisconnectClientsAsync();
        }

        private async Task InitializeAsync()
        {
            LoadProjectContext();
            await LoadConnectionsAsync();
        }

        private void LoadProjectContext()
        {
            var global = _configService.LoadGlobalConfig();
            if (!string.IsNullOrWhiteSpace(global.LastProjectPath) && Directory.Exists(global.LastProjectPath))
            {
                _projectPath = global.LastProjectPath;
                _projectConfig = _configService.LoadProjectConfig(_projectPath);
                HeaderSubtitle.Text = $"Active project: {System.IO.Path.GetFileName(_projectPath)}";
            }
            else
            {
                _projectPath = string.Empty;
                _projectConfig = new ProjectConfig();
                HeaderSubtitle.Text = "No project selected";
            }

            UpdateDownloadRoot(null, force: true);
            UpdateDownloadPathText();
            LoadPendingDownloadSession();
        }

        private void DetachFtpExplorerPage_Click(object sender, RoutedEventArgs e)
        {
            var window = new PageHostWindow(new FtpExplorerPage(), "FTP Explorer • Detached");
            window.Show();
        }

        private async Task LoadConnectionsAsync()
        {
            _profiles.Clear();
            var list = _configService.LoadConnections();
            foreach (var profile in list)
            {
                profile.IsProjectDefault = IsProjectProfile(profile);
                _profiles.Add(profile);
            }

            if (_profiles.Count == 0)
            {
                StatusText.Text = "No connection profiles have been created yet.";
                ConnectionSummaryText.Text = string.Empty;
                MappingSummaryText.Text = string.Empty;
                await DisconnectClientsAsync();
                _entries.Clear();
                _currentProfile = null;
                UpdateConnectButtonState();
                return;
            }

            ConnectionProfile? defaultProfile = null;
            if (!string.IsNullOrWhiteSpace(_projectConfig.ConnectionProfileId))
            {
                defaultProfile = _profiles.FirstOrDefault(p =>
                    string.Equals(p.Id, _projectConfig.ConnectionProfileId, StringComparison.OrdinalIgnoreCase));
            }
            defaultProfile ??= _profiles.FirstOrDefault();

            _suppressSelectionChanged = true;
            ConnectionsCombo.SelectedItem = defaultProfile;
            _suppressSelectionChanged = false;

            if (defaultProfile != null)
            {
                await SwitchProfileAsync(defaultProfile, autoConnect: false);
            }
        }

        private void ApplyProfileSelection(ConnectionProfile profile)
        {
            _currentProfile = profile;
            _currentMapping = GetPrimaryMapping(profile);
            _remoteRoot = BuildRemoteRoot(profile, _currentMapping);
            _currentPath = _remoteRoot;
            PathTextBox.Text = _currentPath;

            ShowHiddenCheckBox.IsChecked = profile.ShowHiddenFiles;
            ConnectionSummaryText.Text = $"{profile.Username}@{profile.Host}:{profile.Port} · {(profile.UseSSH ? "SFTP/SSH" : "FTP")}";

            if (_currentMapping != null && !string.IsNullOrWhiteSpace(_currentMapping.LocalPath))
            {
                MappingSummaryText.Text = $"Map: {_currentMapping.LocalPath} → {_currentMapping.RemotePath}";
            }
            else
            {
                MappingSummaryText.Text = $"Root: {profile.RemotePath}";
            }

            UpdateDownloadRoot(_currentMapping);
            UpdateDownloadPathText();
            UpdateConnectButtonState();
        }

        private async Task SwitchProfileAsync(ConnectionProfile profile, bool autoConnect)
        {
            if (_isBusy) return;

            ApplyProfileSelection(profile);

            if (autoConnect)
            {
                await ConnectAndLoadAsync();
            }
            else
            {
                StatusText.Text = "Profile selected. Click Connect to browse.";
            }
        }

        private async Task ConnectAndLoadAsync()
        {
            if (_currentProfile == null)
            {
                StatusText.Text = "Select or create a connection profile.";
                return;
            }

            try
            {
                _isBusy = true;
                UpdateConnectButtonState();
                StatusText.Text = "Connecting...";
                await DisconnectClientsAsync();

                if (_currentProfile.UseSSH)
                {
                    await ConnectSftpAsync(_currentProfile);
                }
                else
                {
                    await ConnectFtpAsync(_currentProfile);
                }

                AddLog($"Connected to {_currentProfile.Host}:{_currentProfile.Port} ({(_currentProfile.UseSSH ? "SFTP" : "FTP")}).");
                await LoadDirectoryAsync(_currentPath);
                await TryResumePendingSessionAsync(_currentProfile);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Connection failed: {ex.Message}";
                AddLog($"❌ Connection error: {ex}");
                ModernMessageBox.Show($"Unable to connect:\n{ex.Message}", "FTP Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isBusy = false;
                UpdateConnectButtonState();
            }
        }

        private async Task ConnectFtpAsync(ConnectionProfile profile)
        {
            var password = EncryptionService.Decrypt(profile.Password);
            _ftpClient = new AsyncFtpClient(profile.Host, profile.Username, password, profile.Port <= 0 ? 21 : profile.Port)
            {
                Config =
                {
                    DataConnectionType = profile.PassiveMode ? FtpDataConnectionType.AutoPassive : FtpDataConnectionType.AutoActive,
                    SocketKeepAlive = true,
                    EncryptionMode = FtpEncryptionMode.Auto
                }
            };
            await _ftpClient.Connect();
        }

        private async Task ConnectSftpAsync(ConnectionProfile profile)
        {
            await Task.Run(() =>
            {
                var authMethods = new List<AuthenticationMethod>();
                var password = EncryptionService.Decrypt(profile.Password);

                if (!string.IsNullOrWhiteSpace(profile.PrivateKeyPath) && File.Exists(profile.PrivateKeyPath))
                {
                    var keyFile = new PrivateKeyFile(profile.PrivateKeyPath);
                    authMethods.Add(new PrivateKeyAuthenticationMethod(profile.Username, keyFile));
                }

                if (!string.IsNullOrEmpty(password))
                {
                    authMethods.Add(new PasswordAuthenticationMethod(profile.Username, password));
                }

                if (authMethods.Count == 0)
                {
                    throw new InvalidOperationException("Provide an SSH password or private key for this SFTP profile.");
                }

                var info = new ConnectionInfo(
                    profile.Host,
                    profile.Port <= 0 ? 22 : profile.Port,
                    profile.Username,
                    authMethods.ToArray());

                _sftpClient = new SftpClient(info);
                _sftpClient.Connect();
            });
        }

        private async Task LoadDirectoryAsync(string path)
        {
            if (_currentProfile == null)
            {
                return;
            }

            try
            {
                _isBusy = true;
                StatusText.Text = $"Listing {path}...";
                PathTextBox.Text = path;
                _entries.Clear();

                bool showHidden = ShowHiddenCheckBox.IsChecked == true;

                if (_currentProfile.UseSSH)
                {
                    if (_sftpClient == null || !_sftpClient.IsConnected)
                    {
                        await ConnectSftpAsync(_currentProfile);
                    }

                    var entries = await Task.Run(() =>
                        _sftpClient!.ListDirectory(path).ToList());

                    var filtered = entries
                        .Where(e => e.Name != "." && e.Name != "..")
                        .Where(e => showHidden || !IsHiddenName(e.Name))
                        .OrderByDescending(e => e.IsDirectory)
                        .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (!IsRootPath(path))
                    {
                        _entries.Add(FtpExplorerItem.CreateParentEntry(GetParentPath(path)));
                    }

                    foreach (var entry in filtered)
                    {
                        _entries.Add(FtpExplorerItem.FromSftp(entry));
                    }
                }
                else
                {
                    if (_ftpClient == null || !_ftpClient.IsConnected)
                    {
                        await ConnectFtpAsync(_currentProfile);
                    }

                    var listing = await _ftpClient!.GetListing(path, FtpListOption.AllFiles | FtpListOption.Size | FtpListOption.Modify);
                    var filtered = listing
                        .Where(item => showHidden || !IsHiddenName(item.Name))
                        .OrderByDescending(item => item.Type == FtpObjectType.Directory)
                        .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (!IsRootPath(path))
                    {
                        _entries.Add(FtpExplorerItem.CreateParentEntry(GetParentPath(path)));
                    }

                    foreach (var entry in filtered)
                    {
                        _entries.Add(FtpExplorerItem.FromFtp(entry));
                    }
                }

                StatusText.Text = $"Found {_entries.Count} entries.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
                AddLog($"❌ Listing error: {ex}");
            }
            finally
            {
                _isBusy = false;
            }
        }

        private async Task DisconnectClientsAsync()
        {
            if (_ftpClient != null)
            {
                try
                {
                    if (_ftpClient.IsConnected)
                    {
                        await _ftpClient.Disconnect();
                    }
                }
                catch { }
                finally
                {
                    _ftpClient.Dispose();
                    _ftpClient = null;
                }
            }

            if (_sftpClient != null)
            {
                try
                {
                    if (_sftpClient.IsConnected)
                    {
                        _sftpClient.Disconnect();
                    }
                }
                catch { }
                finally
                {
                    _sftpClient.Dispose();
                    _sftpClient = null;
                }
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProfile == null || _isBusy) return;
            await LoadDirectoryAsync(_currentPath);
        }

        private async void UpButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy || string.IsNullOrWhiteSpace(_currentPath)) return;
            if (_currentPath.TrimEnd('/') == _remoteRoot.TrimEnd('/')) return;

            var parent = _currentPath.TrimEnd('/');
            var idx = parent.LastIndexOf('/');
            if (idx <= 0)
            {
                parent = _remoteRoot;
            }
            else
            {
                parent = parent.Substring(0, idx);
                if (!parent.StartsWith("/")) parent = "/" + parent;
            }

            if (string.IsNullOrEmpty(parent))
            {
                parent = _remoteRoot;
            }

            _currentPath = NormalizeRemoteBase(parent);
            await LoadDirectoryAsync(_currentPath);
        }

        private async void EntriesListView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_isBusy) return;
            if (EntriesListView.SelectedItem is not FtpExplorerItem entry) return;

            if (entry.IsParentLink)
            {
                _currentPath = entry.FullPath;
            }
            else if (entry.IsDirectory)
            {
                _currentPath = EnsureTrailingSlash(entry.FullPath);
            }
            else
            {
                return;
            }

            await LoadDirectoryAsync(_currentPath);
        }

        private async void ConnectionsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionChanged) return;
            if (ConnectionsCombo.SelectedItem is ConnectionProfile profile)
            {
                await SwitchProfileAsync(profile, autoConnect: false);
            }
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            if (_currentProfile == null)
            {
                ModernMessageBox.Show("Select a connection profile first.", "FTP Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await ConnectAndLoadAsync();
        }

        private async void ShowHiddenCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isBusy || _currentProfile == null) return;
            await LoadDirectoryAsync(_currentPath);
        }

        private void BrowseDownloadPath_Click(object sender, RoutedEventArgs e) => SelectDownloadFolder();

        private async void DownloadSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
            {
                ModernMessageBox.Show("A download is already in progress.", "Download", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var selected = _entries
                .Where(it => it.IsSelected && !it.IsParentLink)
                .Select(CloneEntry)
                .ToList();
            if (!selected.Any())
            {
                ModernMessageBox.Show("Select at least one file or folder to download.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await DownloadEntriesAsync(selected);
        }

        private async void DownloadRowButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
            {
                ModernMessageBox.Show("A download is already in progress.", "Download", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if ((sender as FrameworkElement)?.Tag is FtpExplorerItem item && !item.IsParentLink)
            {
                await DownloadEntriesAsync(new List<FtpExplorerItem> { CloneEntry(item) });
            }
        }

        private async void DownloadMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
            {
                ModernMessageBox.Show("A download is already in progress.", "Download", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (EntriesListView.SelectedItem is FtpExplorerItem item && !item.IsParentLink)
            {
                await DownloadEntriesAsync(new List<FtpExplorerItem> { CloneEntry(item) });
            }
        }

        private void SelectAllCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.CheckBox cb)
            {
                if (cb.IsChecked == true)
                {
                    ApplySelectionToAll(true);
                    if (ClearSelectionCheckBox != null)
                    {
                        ClearSelectionCheckBox.IsChecked = false;
                    }
                }
                else
                {
                    ApplySelectionToAll(false);
                }
            }
        }

        private void ClearSelectionCheckBox_Click(object sender, RoutedEventArgs e)
        {
            ApplySelectionToAll(false);
            if (sender is System.Windows.Controls.CheckBox cb)
            {
                cb.IsChecked = false;
            }
            if (SelectAllCheckBox != null)
            {
                SelectAllCheckBox.IsChecked = false;
            }
        }

        private void ApplySelectionToAll(bool isSelected)
        {
            foreach (var entry in _entries)
            {
                if (entry.IsParentLink) continue;
                entry.IsSelected = isSelected;
            }
        }

        private void UpdateDownloadButtonsState()
        {
            if (DownloadSelectedButton == null || PauseDownloadButton == null) return;

            DownloadSelectedButton.IsEnabled = !_isBusy && !_isDownloadPaused;
            PauseDownloadButton.IsEnabled = _isBusy || _isDownloadPaused;
            PauseDownloadButton.Content = _isDownloadPaused ? "Resume" : "Pause";
        }

        private void UpdateConnectButtonState()
        {
            if (ConnectButton == null) return;
            ConnectButton.IsEnabled = _currentProfile != null && !_isBusy;
        }

        private async void PauseDownloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isDownloadPaused)
            {
                _isDownloadPaused = false;
                UpdateDownloadButtonsState();
                await ResumePendingDownloadAsync();
                return;
            }

            if (!_isBusy)
            {
                return;
            }

            _isDownloadPaused = true;
            _downloadCts?.Cancel();
            StatusText.Text = "Pausing current download...";
            UpdateDownloadButtonsState();
        }

        private async void ManageConnections_Click(object sender, RoutedEventArgs e)
        {
            var manager = new ConnectionManagerWindow
            {
                Owner = Window.GetWindow(this)
            };
            manager.ShowDialog();

            string? targetId = _currentProfile?.Id;
            await LoadConnectionsAsync();

            if (!string.IsNullOrWhiteSpace(targetId))
            {
                var match = _profiles.FirstOrDefault(p => string.Equals(p.Id, targetId, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    _suppressSelectionChanged = true;
                    ConnectionsCombo.SelectedItem = match;
                    _suppressSelectionChanged = false;
                    await SwitchProfileAsync(match, autoConnect: false);
                }
            }
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            LogTextBox.Clear();
        }

        private Task<bool> ConfirmDownloadAsync(int itemCount)
        {
            while (true)
            {
                var choice = ModernMessageBox.ShowWithResult(
                    $"Download {itemCount} item(s) to:\n{GetDownloadRoot()}",
                    "Confirm Download",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question,
                    "Download",
                    "Change Path",
                    "Cancel");

                if (choice == MessageBoxResult.Yes)
                {
                    return Task.FromResult(true);
                }

                if (choice == MessageBoxResult.No)
                {
                    if (!SelectDownloadFolder())
                    {
                        return Task.FromResult(false);
                    }
                    continue;
                }

                return Task.FromResult(false);
            }
        }

        private bool SelectDownloadFolder()
        {
            try
            {
                using var dialog = new Forms.FolderBrowserDialog
                {
                    Description = "Choose download destination",
                    SelectedPath = GetDownloadRoot()
                };

                if (dialog.ShowDialog() == Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                {
                    _downloadRoot = dialog.SelectedPath;
                    _downloadPathManuallySet = true;
                    UpdateDownloadPathText();
                    return true;
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Unable to browse folders:\n{ex.Message}", "Browse Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return false;
        }

        private void SaveDownloadSession(DownloadSessionInfo info)
        {
            if (string.IsNullOrWhiteSpace(_projectPath)) return;
            var path = Path.Combine(_projectPath, DownloadSessionFileName);
            var json = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            _pendingSession = info;
        }

        private void ClearDownloadSession()
        {
            if (string.IsNullOrWhiteSpace(_projectPath)) return;
            var path = System.IO.Path.Combine(_projectPath, DownloadSessionFileName);
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }

            _pendingSession = null;
        }

        private void LoadPendingDownloadSession()
        {
            if (string.IsNullOrWhiteSpace(_projectPath))
            {
                _pendingSession = null;
                return;
            }

            var path = System.IO.Path.Combine(_projectPath, DownloadSessionFileName);
            if (!File.Exists(path))
            {
                _pendingSession = null;
                return;
            }

            try
            {
                var json = File.ReadAllText(path);
                _pendingSession = JsonSerializer.Deserialize<DownloadSessionInfo>(json);
            }
            catch
            {
                _pendingSession = null;
            }
        }

        private async Task TryResumePendingSessionAsync(ConnectionProfile profile)
        {
            if (_pendingSession == null || _pendingSession.Items == null || _pendingSession.Items.Count == 0)
            {
                return;
            }

            if (!string.Equals(_pendingSession.ProfileId, profile.Id, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var result = ModernMessageBox.ShowWithResult(
                $"Resume downloading {_pendingSession.Items.Count} item(s) to:\n{_pendingSession.DownloadRoot}",
                "Resume Download",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question,
                "Resume",
                "Discard",
                "Cancel");

            if (result == MessageBoxResult.Yes)
            {
                if (!string.IsNullOrWhiteSpace(_pendingSession.DownloadRoot))
                {
                    _downloadRoot = _pendingSession.DownloadRoot;
                    _downloadPathManuallySet = true;
                    UpdateDownloadPathText();
                }

                await DownloadEntriesAsync(null, skipConfirmation: true, fromResume: true);
            }
            else if (result == MessageBoxResult.No)
            {
                ClearDownloadSession();
            }
        }

        private async Task ResumePendingDownloadAsync()
        {
            if (_currentProfile == null)
            {
                ModernMessageBox.Show("Select the original connection profile first.", "Resume Download", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_pendingSession == null)
            {
                LoadPendingDownloadSession();
            }

            if (_pendingSession == null || _pendingSession.Items == null || _pendingSession.Items.Count == 0)
            {
                StatusText.Text = "No pending download to resume.";
                return;
            }

            if (!string.Equals(_pendingSession.ProfileId, _currentProfile.Id, StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = "Switch to the original connection profile to resume.";
                return;
            }

            if (!string.IsNullOrWhiteSpace(_pendingSession.DownloadRoot))
            {
                _downloadRoot = _pendingSession.DownloadRoot;
                _downloadPathManuallySet = true;
                UpdateDownloadPathText();
            }

            await DownloadEntriesAsync(null, skipConfirmation: true, fromResume: true);
        }

        private void ResetDownloadProgress(long totalBytes, long alreadyTransferred = 0)
        {
            _downloadTotalBytes = Math.Max(0, totalBytes);
            _downloadTransferredBytes = Math.Max(0, Math.Min(_downloadTotalBytes, alreadyTransferred));
            UpdateDownloadProgressText();
            if (DownloadProgressBar != null)
            {
                DownloadProgressBar.Value = CalculatePercent();
            }
            if (DownloadProgressDetailText != null)
            {
                DownloadProgressDetailText.Text = string.Empty;
            }
        }

        private void AdvanceDownloadProgress(long bytesDelta)
        {
            _downloadTransferredBytes += Math.Max(0, bytesDelta);
            if (_downloadTransferredBytes > _downloadTotalBytes && _downloadTotalBytes > 0)
            {
                _downloadTotalBytes = _downloadTransferredBytes;
            }
            UpdateDownloadProgressText();
            if (DownloadProgressBar != null)
            {
                DownloadProgressBar.Value = CalculatePercent();
            }
        }

        private double CalculatePercent()
        {
            if (_downloadTotalBytes <= 0) return 0;
            return Math.Min(100, (double)_downloadTransferredBytes / _downloadTotalBytes * 100);
        }

        private void UpdateDownloadProgressText()
        {
            if (DownloadProgressText == null) return;
            if (_downloadTotalBytes <= 0)
            {
                DownloadProgressText.Text = _isBusy ? "Preparing download…" : "Waiting for download…";
                return;
            }

            var percent = CalculatePercent();
            DownloadProgressText.Text = $"{FtpExplorerItem.FormatSizeReadable(_downloadTransferredBytes)} / {FtpExplorerItem.FormatSizeReadable(_downloadTotalBytes)} ({percent:0}%)";
        }

        private void UpdateFileDetail(string relativePath, long downloadedBytes, long totalBytes)
        {
            if (DownloadProgressDetailText == null) return;
            var totalDisplay = totalBytes > 0 ? FtpExplorerItem.FormatSizeReadable(totalBytes) : "Unknown";
            DownloadProgressDetailText.Text = $"{relativePath} · {FtpExplorerItem.FormatSizeReadable(downloadedBytes)} / {totalDisplay}";
        }

        private void CompleteDownloadProgress()
        {
            _downloadTransferredBytes = _downloadTotalBytes;
            if (DownloadProgressBar != null)
            {
                DownloadProgressBar.Value = 100;
            }
            if (DownloadProgressText != null)
            {
                DownloadProgressText.Text = "Download complete.";
            }
            if (DownloadProgressDetailText != null)
            {
                DownloadProgressDetailText.Text = string.Empty;
            }
        }

        private async Task<FileConflictChoice> ResolveFileConflictAsync(string localPath)
        {
            if (_overwriteAllConfirmed)
            {
                return FileConflictChoice.Overwrite;
            }

            if (_skipAllExisting)
            {
                return FileConflictChoice.Skip;
            }

            return await Dispatcher.InvokeAsync(() =>
            {
                var dialog = new FileConflictDialog(localPath)
                {
                    Owner = Window.GetWindow(this)
                };
                var result = dialog.ShowDialog();
                var choice = result == true ? dialog.Choice : FileConflictChoice.Cancel;

                if (choice == FileConflictChoice.OverwriteAll)
                {
                    _overwriteAllConfirmed = true;
                    return FileConflictChoice.Overwrite;
                }

                if (choice == FileConflictChoice.SkipAll)
                {
                    _skipAllExisting = true;
                    return FileConflictChoice.Skip;
                }

                return choice;
            });
        }

        private FtpExplorerItem CloneEntry(FtpExplorerItem source)
        {
            return new FtpExplorerItem
            {
                Name = source.Name,
                Type = source.Type,
                SizeDisplay = source.SizeDisplay,
                ModifiedDisplay = source.ModifiedDisplay,
                Owner = source.Owner,
                Group = source.Group,
                Permissions = source.Permissions,
                FullPath = source.FullPath,
                IsDirectory = source.IsDirectory,
                IsLink = source.IsLink,
                Icon = source.Icon,
                IconColor = source.IconColor,
                IsParentLink = source.IsParentLink,
                SizeBytes = source.SizeBytes
            };
        }

        private void AddLog(string message)
        {
            var ts = DateTime.Now.ToString("HH:mm:ss");
            LogTextBox.AppendText($"[{ts}] {message}{Environment.NewLine}");
            LogTextBox.ScrollToEnd();
        }

        private bool IsHiddenName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return name.StartsWith(".");
        }

        private bool IsProjectProfile(ConnectionProfile profile)
        {
            if (profile == null || _projectConfig == null) return false;
            return !string.IsNullOrWhiteSpace(_projectConfig.ConnectionProfileId) &&
                   string.Equals(profile.Id, _projectConfig.ConnectionProfileId, StringComparison.OrdinalIgnoreCase);
        }

        private string NormalizeRemoteBase(string? path)
        {
            var trimmed = (path ?? "/").Replace("\\", "/").Trim();
            if (!trimmed.StartsWith("/"))
            {
                trimmed = "/" + trimmed;
            }
            if (!trimmed.EndsWith("/"))
            {
                trimmed += "/";
            }
            return trimmed;
        }

        private string EnsureTrailingSlash(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "/";
            var normalized = path.Replace("\\", "/");
            if (!normalized.StartsWith("/"))
            {
                normalized = "/" + normalized;
            }
            if (!normalized.EndsWith("/"))
            {
                normalized += "/";
            }
            return normalized;
        }

        private PathMapping? GetPrimaryMapping(ConnectionProfile profile)
        {
            return profile.PathMappings?
                .FirstOrDefault(pm =>
                    pm != null &&
                    (!string.IsNullOrWhiteSpace(pm.LocalPath) || !string.IsNullOrWhiteSpace(pm.RemotePath)));
        }

        private string BuildRemoteRoot(ConnectionProfile profile, PathMapping? mapping)
        {
            var baseRemote = NormalizeRemoteBase(profile.RemotePath);
            if (mapping == null || string.IsNullOrWhiteSpace(mapping.RemotePath))
            {
                return baseRemote;
            }
            return CombineRemotePaths(baseRemote, mapping.RemotePath);
        }

        private string CombineRemotePaths(string baseRemote, string? mappingRemote)
        {
            var normalizedBase = NormalizeRemoteBase(baseRemote);
            if (string.IsNullOrWhiteSpace(mappingRemote))
            {
                return normalizedBase;
            }

            var trimmed = mappingRemote.Replace("\\", "/").Trim('/');
            if (string.IsNullOrEmpty(trimmed))
            {
                return normalizedBase;
            }

            var combined = normalizedBase.TrimEnd('/') + "/" + trimmed;
            return NormalizeRemoteBase(combined);
        }

        private bool IsRootPath(string path)
        {
            var current = (path ?? "/").TrimEnd('/');
            var root = (_remoteRoot ?? "/").TrimEnd('/');
            return string.Equals(current, root, StringComparison.OrdinalIgnoreCase);
        }

        private string GetParentPath(string path)
        {
            var trimmed = path.TrimEnd('/');
            var idx = trimmed.LastIndexOf('/');
            if (idx <= 0)
            {
                return _remoteRoot;
            }
            var parent = trimmed.Substring(0, idx);
            if (string.IsNullOrWhiteSpace(parent))
            {
                parent = "/";
            }
            return EnsureTrailingSlash(parent);
        }

        private void EntriesListView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var listItem = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
            if (listItem != null)
            {
                listItem.IsSelected = true;
            }
        }

        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null && current is not T)
            {
                current = VisualTreeHelper.GetParent(current);
            }
            return current as T;
        }

        private async void RenameMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            if (EntriesListView.SelectedItem is not FtpExplorerItem entry || entry.IsParentLink)
            {
                return;
            }

            var dialog = new InputDialog("Rename", $"Enter a new name for '{entry.Name}':", entry.Name)
            {
                Owner = Window.GetWindow(this)
            };

            if (dialog.ShowDialog() == true)
            {
                var newName = dialog.ResponseText?.Trim();
                if (!string.IsNullOrWhiteSpace(newName) && !string.Equals(newName, entry.Name, StringComparison.OrdinalIgnoreCase))
                {
                    await RenameEntryAsync(entry, newName);
                    await LoadDirectoryAsync(_currentPath);
                }
            }
        }

        private async void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            if (EntriesListView.SelectedItem is not FtpExplorerItem entry || entry.IsParentLink)
            {
                return;
            }

            var result = ModernMessageBox.ShowWithResult(
                $"Delete '{entry.Name}' {(entry.IsDirectory ? "folder" : "file")}? This cannot be undone.",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                "Delete",
                "Cancel");

            if (result == MessageBoxResult.Yes)
            {
                await DeleteEntryAsync(entry);
                await LoadDirectoryAsync(_currentPath);
            }
        }

        private async Task RenameEntryAsync(FtpExplorerItem entry, string newName)
        {
            try
            {
                _isBusy = true;
                StatusText.Text = $"Renaming {entry.Name}...";
                var parent = GetDirectoryFromPath(entry.FullPath);
                var newPath = $"{parent}{newName}";

                if (_currentProfile?.UseSSH == true)
                {
                    await Task.Run(() => _sftpClient?.RenameFile(entry.FullPath, newPath));
                }
                else
                {
                    if (_ftpClient == null || !_ftpClient.IsConnected)
                    {
                        await ConnectFtpAsync(_currentProfile!);
                    }
                    await _ftpClient!.Rename(entry.FullPath, newPath);
                }

                AddLog($"✏️ Renamed '{entry.Name}' to '{newName}'.");
                StatusText.Text = "Rename successful.";
            }
            catch (Exception ex)
            {
                AddLog($"❌ Rename error: {ex.Message}");
                StatusText.Text = $"Rename failed: {ex.Message}";
                ModernMessageBox.Show($"Unable to rename:\n{ex.Message}", "Rename Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isBusy = false;
            }
        }

        private async Task DeleteEntryAsync(FtpExplorerItem entry)
        {
            try
            {
                _isBusy = true;
                StatusText.Text = $"Deleting {entry.Name}...";

                if (_currentProfile?.UseSSH == true)
                {
                    await Task.Run(() =>
                    {
                        if (_sftpClient == null || !_sftpClient.IsConnected)
                        {
                            throw new InvalidOperationException("SFTP client not connected.");
                        }

                        if (entry.IsDirectory)
                        {
                            DeleteSftpDirectoryRecursive(entry.FullPath);
                            _sftpClient.DeleteDirectory(entry.FullPath);
                        }
                        else
                        {
                            _sftpClient.DeleteFile(entry.FullPath);
                        }
                    });
                }
                else
                {
                    if (_ftpClient == null || !_ftpClient.IsConnected)
                    {
                        await ConnectFtpAsync(_currentProfile!);
                    }

                    if (entry.IsDirectory)
                    {
                        await _ftpClient!.DeleteDirectory(entry.FullPath, FtpListOption.Recursive);
                    }
                    else
                    {
                        await _ftpClient!.DeleteFile(entry.FullPath);
                    }
                }

                AddLog($"🗑 Deleted '{entry.Name}'.");
                StatusText.Text = "Delete successful.";
            }
            catch (Exception ex)
            {
                AddLog($"❌ Delete error: {ex.Message}");
                StatusText.Text = $"Delete failed: {ex.Message}";
                ModernMessageBox.Show($"Unable to delete:\n{ex.Message}", "Delete Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isBusy = false;
            }
        }

        private void DeleteSftpDirectoryRecursive(string path)
        {
            if (_sftpClient == null) return;
            var entries = _sftpClient.ListDirectory(path);
            foreach (var entry in entries)
            {
                if (entry.Name == "." || entry.Name == "..") continue;
                if (entry.IsDirectory)
                {
                    DeleteSftpDirectoryRecursive(entry.FullName);
                    _sftpClient.DeleteDirectory(entry.FullName);
                }
                else
                {
                    _sftpClient.DeleteFile(entry.FullName);
                }
            }
        }

        private string GetDirectoryFromPath(string fullPath)
        {
            var normalized = fullPath.Replace("\\", "/");
            var idx = normalized.LastIndexOf('/');
            if (idx < 0)
            {
                return "/";
            }
            var dir = normalized.Substring(0, idx + 1);
            if (!dir.StartsWith("/"))
            {
                dir = "/" + dir;
            }
            return dir;
        }

        private void UpdateDownloadRoot(PathMapping? mapping, bool force = false)
        {
            if (_downloadPathManuallySet && !force)
            {
                return;
            }

            var baseLocal = !string.IsNullOrWhiteSpace(_projectPath)
                ? _projectPath
                : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

            if (mapping != null && !string.IsNullOrWhiteSpace(mapping.LocalPath))
            {
                var trimmed = mapping.LocalPath.Trim().TrimStart('\\', '/');
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    baseLocal = System.IO.Path.Combine(baseLocal, trimmed.Replace("/", "\\"));
                }
            }

            _downloadRoot = baseLocal;
        }

        private void UpdateDownloadPathText()
        {
            if (DownloadPathTextBox == null) return;
            var path = GetDownloadRoot();
            DownloadPathTextBox.Text = path;
        }

        private string GetDownloadRoot()
        {
            if (string.IsNullOrWhiteSpace(_downloadRoot))
            {
                _downloadRoot = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            }
            return _downloadRoot;
        }

        private async Task<DownloadPlan> BuildDownloadPlanAsync(IEnumerable<FtpExplorerItem> entries, string destinationRoot, CancellationToken cancellationToken)
        {
            var plan = new DownloadPlan();
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsDirectory && !entry.IsParentLink)
                {
                    var remoteDir = EnsureLeadingSlash(entry.FullPath);
                    var localDir = Path.Combine(destinationRoot, GetRelativeRemotePath(remoteDir).Replace("/", "\\"));
                    plan.Directories.Add(localDir);
                    if (_currentProfile?.UseSSH == true)
                    {
                        if (_sftpClient == null || !_sftpClient.IsConnected)
                        {
                            await ConnectSftpAsync(_currentProfile);
                        }
                        await Task.Run(() => BuildPlanForSftpRecursive(remoteDir, destinationRoot, plan, cancellationToken), cancellationToken);
                    }
                    else
                    {
                        if (_ftpClient == null || !_ftpClient.IsConnected)
                        {
                            await ConnectFtpAsync(_currentProfile!);
                        }
                        await BuildPlanForFtpRecursiveAsync(remoteDir, destinationRoot, plan, cancellationToken);
                    }
                }
                else if (!entry.IsParentLink)
                {
                    AddFileToPlan(entry.FullPath, destinationRoot, entry.SizeBytes, plan);
                }
            }

            return plan;
        }

        private async Task BuildPlanForFtpRecursiveAsync(string remotePath, string destinationRoot, DownloadPlan plan, CancellationToken token)
        {
            if (_ftpClient == null) return;
            var listing = await _ftpClient.GetListing(remotePath, FtpListOption.AllFiles | FtpListOption.Size | FtpListOption.Modify);
            foreach (var item in listing)
            {
                token.ThrowIfCancellationRequested();
                if (item.Type == FtpObjectType.Directory)
                {
                    if (item.Name == "." || item.Name == "..") continue;
                    var localDir = Path.Combine(destinationRoot, GetRelativeRemotePath(item.FullName).Replace("/", "\\"));
                    plan.Directories.Add(localDir);
                    await BuildPlanForFtpRecursiveAsync(item.FullName, destinationRoot, plan, token);
                }
                else if (item.Type == FtpObjectType.File)
                {
                    AddFileToPlan(item.FullName, destinationRoot, item.Size, plan);
                }
            }
        }

        private void BuildPlanForSftpRecursive(string remotePath, string destinationRoot, DownloadPlan plan, CancellationToken token)
        {
            if (_sftpClient == null) return;
            var entries = _sftpClient.ListDirectory(remotePath);
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Name == "." || entry.Name == "..") continue;
                if (entry.IsDirectory)
                {
                    var childRemote = entry.FullName;
                    var localDir = Path.Combine(destinationRoot, GetRelativeRemotePath(childRemote).Replace("/", "\\"));
                    plan.Directories.Add(localDir);
                    BuildPlanForSftpRecursive(childRemote, destinationRoot, plan, token);
                }
                else
                {
                    AddFileToPlan(entry.FullName, destinationRoot, entry.Length, plan);
                }
            }
        }

        private void AddFileToPlan(string remotePath, string destinationRoot, long sizeBytes, DownloadPlan plan)
        {
            var relative = GetRelativeRemotePath(remotePath);
            var localPath = Path.Combine(destinationRoot, relative.Replace("/", "\\"));
            var directory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                plan.Directories.Add(directory);
            }

            plan.Files.Add(new RemoteDownloadItem
            {
                RemotePath = remotePath,
                LocalPath = localPath,
                RelativePath = relative,
                SizeBytes = sizeBytes
            });
        }

        private void EnsureLocalDirectories(DownloadPlan plan)
        {
            foreach (var dir in plan.Directories)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch { }
            }
        }

        private DownloadPlan BuildPlanFromSession(DownloadSessionInfo session)
        {
            var plan = new DownloadPlan();
            foreach (var entry in session.Items)
            {
                var relative = entry.LocalRelativePath;
                var localPath = Path.Combine(session.DownloadRoot, relative.Replace("/", "\\"));
                plan.Directories.Add(Path.GetDirectoryName(localPath) ?? session.DownloadRoot);
                plan.Files.Add(new RemoteDownloadItem
                {
                    RemotePath = entry.RemotePath,
                    LocalPath = localPath,
                    RelativePath = relative,
                    SizeBytes = entry.SizeBytes
                });
            }
            return plan;
        }

        private async Task DownloadEntriesAsync(IList<FtpExplorerItem>? entries, bool skipConfirmation = false, bool fromResume = false)
        {
            if (_isBusy && !fromResume)
            {
                ModernMessageBox.Show("Please wait for the current operation to finish.", "Busy", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!fromResume)
            {
                if (entries == null || entries.Count == 0) return;
            }
            else
            {
                if (_pendingSession == null || _pendingSession.Items == null || _pendingSession.Items.Count == 0)
                {
                    StatusText.Text = "No pending download to resume.";
                    return;
                }
            }

            if (!skipConfirmation && !fromResume)
            {
                var confirmed = await ConfirmDownloadAsync(entries.Count);
                if (!confirmed) return;
            }

            var destinationRoot = GetDownloadRoot();
            if (string.IsNullOrWhiteSpace(destinationRoot))
            {
                ModernMessageBox.Show("Select a valid download destination first.", "Download Path", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                Directory.CreateDirectory(destinationRoot);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Unable to create destination folder:\n{ex.Message}", "Download Path", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var cts = new CancellationTokenSource();
            _downloadCts = cts;
            _isDownloadPaused = false;
            UpdateDownloadButtonsState();

            DownloadPlan plan;
            DownloadSessionInfo session;

            try
            {
                _isBusy = true;
                UpdateDownloadButtonsState();

                if (fromResume && _pendingSession != null)
                {
                    session = _pendingSession;
                    if (!string.IsNullOrWhiteSpace(session.DownloadRoot))
                    {
                        destinationRoot = session.DownloadRoot;
                        _downloadRoot = destinationRoot;
                        UpdateDownloadPathText();
                    }
                    plan = BuildPlanFromSession(session);
                    var alreadyTransferred = session.Items.Where(i => i.Completed).Sum(i => i.SizeBytes) + session.CurrentFileBytes;
                    ResetDownloadProgress(plan.TotalBytes, alreadyTransferred);
                    StatusText.Text = "Resuming download...";
                }
                else
                {
                    StatusText.Text = "Preparing download plan...";
                    if (entries == null)
                    {
                        throw new InvalidOperationException("No entries specified for download.");
                    }
                    plan = await BuildDownloadPlanAsync(entries, destinationRoot, cts.Token);
                    if (plan.Files.Count == 0)
                    {
                        StatusText.Text = "No files to download.";
                        return;
                    }

                    session = new DownloadSessionInfo
                    {
                        ProfileId = _currentProfile?.Id,
                        DownloadRoot = destinationRoot,
                        CurrentIndex = 0,
                        CurrentFileBytes = 0,
                        Items = plan.Files.Select(f => f.ToSessionEntry()).ToList()
                    };

                    _pendingSession = session;
                    SaveDownloadSession(session);
                    ResetDownloadProgress(plan.TotalBytes);
                    StatusText.Text = $"Downloading {plan.Files.Count} item(s)...";
                }

                EnsureLocalDirectories(plan);
                _overwriteAllConfirmed = false;
                _skipAllExisting = false;

                if (session.Items.Count != plan.Files.Count)
                {
                    var lookup = plan.Files.ToDictionary(f => EnsureLeadingSlash(f.RemotePath), StringComparer.OrdinalIgnoreCase);
                    plan.Files.Clear();
                    foreach (var item in session.Items)
                    {
                        if (lookup.TryGetValue(EnsureLeadingSlash(item.RemotePath), out var match))
                        {
                            plan.Files.Add(match);
                        }
                        else
                        {
                            var derivedLocal = Path.Combine(session.DownloadRoot, item.LocalRelativePath.Replace("/", "\\"));
                            plan.Files.Add(new RemoteDownloadItem
                            {
                                RemotePath = item.RemotePath,
                                LocalPath = derivedLocal,
                                RelativePath = item.LocalRelativePath,
                                SizeBytes = item.SizeBytes
                            });
                        }
                    }
                }

                for (int i = session.CurrentIndex; i < session.Items.Count && i < plan.Files.Count; i++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var sessionEntry = session.Items[i];
                    var target = plan.Files[i];

                    if (sessionEntry.Completed)
                    {
                        continue;
                    }

                    await DownloadSingleFileAsync(target, sessionEntry, session, cts.Token);

                    sessionEntry.Completed = true;
                    sessionEntry.DownloadedBytes = sessionEntry.SizeBytes;
                    session.CurrentIndex = i + 1;
                    session.CurrentFileBytes = 0;
                    SaveDownloadSession(session);
                }

                ClearDownloadSession();
                CompleteDownloadProgress();
                StatusText.Text = $"Downloaded {plan.Files.Count} item(s) to {destinationRoot}.";
                AddLog($"⬇ Downloaded {plan.Files.Count} item(s) → {destinationRoot}");
            }
            catch (OperationCanceledException)
            {
                if (_isDownloadPaused)
                {
                    StatusText.Text = "Download paused.";
                    AddLog("⏸ Download paused by user.");
                }
                else
                {
                    StatusText.Text = "Download cancelled.";
                    AddLog("⚠️ Download cancelled.");
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Download failed: {ex.Message}";
                AddLog($"❌ Download error: {ex}");
                ModernMessageBox.Show($"Download failed:\n{ex.Message}", "Download Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isBusy = false;
                _downloadCts?.Dispose();
                _downloadCts = null;
                UpdateDownloadButtonsState();

                if (entries != null)
                {
                    foreach (var entry in entries)
                    {
                        entry.IsSelected = false;
                    }
                }

                foreach (var original in _entries.Where(it => it.IsSelected))
                {
                    original.IsSelected = false;
                }
            }
        }

        private async Task DownloadSingleFileAsync(RemoteDownloadItem file, DownloadSessionEntry entry, DownloadSessionInfo session, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            var directory = Path.GetDirectoryName(file.LocalPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            bool resumeContinuation = entry.DownloadedBytes > 0 && File.Exists(file.LocalPath);
            if (resumeContinuation)
            {
                var localLength = new FileInfo(file.LocalPath).Length;
                if (entry.DownloadedBytes > localLength)
                {
                    entry.DownloadedBytes = localLength;
                    session.CurrentFileBytes = localLength;
                }
            }
            bool proceed = resumeContinuation || await EnsureLocalTargetAsync(file.LocalPath, false);
            if (!proceed)
            {
                var remaining = entry.SizeBytes - entry.DownloadedBytes;
                if (remaining > 0)
                {
                    AdvanceDownloadProgress(remaining);
                }
                entry.Completed = true;
                entry.DownloadedBytes = entry.SizeBytes;
                SaveDownloadSession(session);
                return;
            }

            if (_currentProfile?.UseSSH == true)
            {
                await DownloadFileViaSftpAsync(file, entry, session, token);
            }
            else
            {
                if (_ftpClient == null || !_ftpClient.IsConnected)
                {
                    await ConnectFtpAsync(_currentProfile ?? throw new InvalidOperationException("No active profile."));
                }

                await DownloadFileViaFtpAsync(file, entry, session, token);
            }

            entry.Completed = true;
            entry.DownloadedBytes = entry.SizeBytes;
            SaveDownloadSession(session);
        }

        private async Task DownloadFileViaFtpAsync(RemoteDownloadItem file, DownloadSessionEntry entry, DownloadSessionInfo session, CancellationToken token)
        {
            if (_ftpClient == null) throw new InvalidOperationException("FTP client not connected.");

            var startPosition = Math.Max(0, entry.DownloadedBytes);
            await using var remoteStream = await _ftpClient.OpenRead(file.RemotePath, FtpDataType.Binary, startPosition, true, token);
            await using var localStream = new FileStream(file.LocalPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
            localStream.Seek(startPosition, SeekOrigin.Begin);
            await CopyStreamWithProgressAsync(remoteStream, localStream, file, entry, session, token);
        }

        private async Task DownloadFileViaSftpAsync(RemoteDownloadItem file, DownloadSessionEntry entry, DownloadSessionInfo session, CancellationToken token)
        {
            if (_sftpClient == null) throw new InvalidOperationException("SFTP client not connected.");

            var startPosition = Math.Max(0, entry.DownloadedBytes);
            using var remoteStream = _sftpClient.OpenRead(file.RemotePath);
            if (remoteStream.CanSeek && startPosition > 0)
            {
                remoteStream.Seek(startPosition, SeekOrigin.Begin);
            }

            await using var localStream = new FileStream(file.LocalPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
            localStream.Seek(startPosition, SeekOrigin.Begin);
            await CopyStreamWithProgressAsync(remoteStream, localStream, file, entry, session, token);
        }

        private async Task CopyStreamWithProgressAsync(Stream source, Stream destination, RemoteDownloadItem file, DownloadSessionEntry entry, DownloadSessionInfo session, CancellationToken token)
        {
            var buffer = new byte[64 * 1024];
            int read;
            var lastSave = DateTime.UtcNow;
            UpdateFileDetail(file.RelativePath, entry.DownloadedBytes, file.SizeBytes);

            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), token)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), token);
                entry.DownloadedBytes += read;
                session.CurrentFileBytes = entry.DownloadedBytes;
                AdvanceDownloadProgress(read);
                UpdateFileDetail(file.RelativePath, entry.DownloadedBytes, file.SizeBytes);

                if ((DateTime.UtcNow - lastSave).TotalSeconds >= 1)
                {
                    SaveDownloadSession(session);
                    lastSave = DateTime.UtcNow;
                }
            }

            SaveDownloadSession(session);
        }

        private async Task<bool> EnsureLocalTargetAsync(string localPath, bool isDirectory)
        {
            bool exists = isDirectory ? Directory.Exists(localPath) : File.Exists(localPath);
            if (!exists) return true;

            var decision = await ResolveFileConflictAsync(localPath);
            switch (decision)
            {
                case FileConflictChoice.Overwrite:
                    try
                    {
                        if (isDirectory)
                        {
                            Directory.Delete(localPath, true);
                        }
                        else
                        {
                            File.Delete(localPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        throw new IOException($"Unable to overwrite '{localPath}': {ex.Message}", ex);
                    }
                    return true;

                case FileConflictChoice.Skip:
                    return false;

                case FileConflictChoice.Cancel:
                    throw new OperationCanceledException();

                default:
                    return false;
            }
        }

        private string GetRelativeRemotePath(string fullPath)
        {
            var normalized = EnsureLeadingSlash(fullPath).TrimEnd('/');
            var root = EnsureLeadingSlash(_remoteRoot ?? "/").TrimEnd('/');
            if (normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                var relative = normalized.Substring(root.Length).TrimStart('/');
                return string.IsNullOrWhiteSpace(relative) ? Path.GetFileName(normalized) ?? normalized : relative;
            }
            return normalized.TrimStart('/');
        }

        private string EnsureLeadingSlash(string path)
        {
            var normalized = (path ?? "/").Replace("\\", "/");
            if (!normalized.StartsWith("/"))
            {
                normalized = "/" + normalized;
            }
            return normalized;
        }
    }

    internal class DownloadSessionInfo
    {
        public string? ProfileId { get; set; }
        public string DownloadRoot { get; set; } = string.Empty;
        public int CurrentIndex { get; set; }
        public long CurrentFileBytes { get; set; }
        public List<DownloadSessionEntry> Items { get; set; } = new();
    }

    internal class DownloadSessionEntry
    {
        public string RemotePath { get; set; } = string.Empty;
        public string LocalRelativePath { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public long DownloadedBytes { get; set; }
        public bool Completed { get; set; }
    }

    internal class DownloadPlan
    {
        public List<RemoteDownloadItem> Files { get; } = new List<RemoteDownloadItem>();
        public HashSet<string> Directories { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public long TotalBytes => Files.Sum(f => Math.Max(0, f.SizeBytes));
    }

    internal class RemoteDownloadItem
    {
        public string RemotePath { get; set; } = string.Empty;
        public string LocalPath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public long SizeBytes { get; set; }

        public DownloadSessionEntry ToSessionEntry() => new DownloadSessionEntry
        {
            RemotePath = RemotePath,
            LocalRelativePath = RelativePath,
            SizeBytes = SizeBytes,
            DownloadedBytes = 0,
            Completed = false
        };
    }
}

