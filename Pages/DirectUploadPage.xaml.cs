using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GitDeployPro.Services;
using FluentFTP; 
using GitDeployPro.Controls;
using GitDeployPro.Models;
using GitDeployPro.Windows;

namespace GitDeployPro.Pages
{
    public partial class DirectUploadPage : Page
    {
        private ConfigurationService _configService;
        private string _projectPath = string.Empty;
        private string _scanRootPath = string.Empty;
        private string _activeRemoteBasePath = "/";
        private ObservableCollection<FileSystemItem> _items;
        private bool _isUploading = false;
        private CancellationTokenSource? _cancellationTokenSource;
        private const string SessionFileName = ".gitdeploy.session";

        public DirectUploadPage()
        {
            InitializeComponent();
            _configService = new ConfigurationService();
            _items = new ObservableCollection<FileSystemItem>();
            FileTreeView.ItemsSource = _items;

            Loaded += DirectUploadPage_Loaded;
        }

        private void DetachDirectUploadPage_Click(object sender, RoutedEventArgs e)
        {
            var window = new PageHostWindow(new DirectUploadPage(), "Direct Upload • Detached");
            window.Show();
        }

        private async void DirectUploadPage_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadProjectFilesAsync();
            CheckSessionStatus();
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

        private async Task LoadProjectFilesAsync()
        {
            try
            {
                var config = LoadCurrentProjectConfig(out bool hasProject);
                if (!hasProject)
                {
                    StatusText.Text = "No project selected.";
                    StartUploadButton.IsEnabled = false;
                    UpdateConnectionInfoBanner(null, skipProjectRefresh: true);
                    return;
                }

                var profile = ResolveConnectionProfile(config.ConnectionProfileId);
                var mapping = GetPrimaryMapping(profile);
                var roots = ResolveRoots(config, mapping);
                _scanRootPath = roots.localRoot;
                _activeRemoteBasePath = roots.remoteRoot;

                UpdateConnectionInfoBanner(config, skipProjectRefresh: true, profileOverride: profile, mappingOverride: mapping);

                StatusText.Text = "Scanning files...";
                StartUploadButton.IsEnabled = false;

                _items.Clear();

                var scanRoot = Directory.Exists(_scanRootPath) ? _scanRootPath : _projectPath;

                await Task.Run(() =>
                {
                    var rootItems = ScanDirectory(scanRoot);
                    Dispatcher.Invoke(() =>
                    {
                        foreach (var item in rootItems)
                        {
                            _items.Add(item);
                        }
                    });
                });

                UpdateStats();
                StatusText.Text = "Ready.";
                StartUploadButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error scanning files: {ex.Message}";
                StartUploadButton.IsEnabled = true;
            }
        }

        private List<FileSystemItem> ScanDirectory(string path)
        {
            var items = new List<FileSystemItem>();
            
            // Standard ignore list
            var ignoredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".git", ".vs", "bin", "obj", ".idea", ".vscode",
                ".gitdeploy.config", ".gitdeploy.session", ".gitdeploy.history", "Desktop.ini", "Thumbs.db"
            };

            // Load .gitignore patterns if exists in root
            try
            {
                string gitIgnorePath = Path.Combine(_projectPath, ".gitignore");
                if (File.Exists(gitIgnorePath))
                {
                    var lines = File.ReadAllLines(gitIgnorePath);
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#")) continue;
                        
                        // Simple cleanup for folder matching
                        var clean = trimmed.TrimStart('/', '\\').TrimEnd('/', '\\');
                        if (!string.IsNullOrWhiteSpace(clean))
                        {
                            ignoredNames.Add(clean);
                        }
                    }
                }
            }
            catch { }

            try
            {
                var dirInfo = new DirectoryInfo(path);

                // Directories
                foreach (var dir in dirInfo.GetDirectories())
                {
                    if (ignoredNames.Contains(dir.Name) || dir.Attributes.HasFlag(FileAttributes.Hidden))
                        continue;

                    var item = new FileSystemItem
                    {
                        Name = dir.Name,
                        FullPath = dir.FullName,
                        IsFolder = true,
                        Icon = "📁",
                        IconColor = System.Windows.Media.Brushes.Gold
                    };

                    // Recursive call
                    item.Children = new ObservableCollection<FileSystemItem>(ScanDirectory(dir.FullName));
                    
                    // Hook up parent checking logic
                    foreach(var child in item.Children)
                    {
                        child.Parent = item;
                    }

                    if (item.Children.Any())
                    {
                        items.Add(item);
                    }
                }

                // Files
                foreach (var file in dirInfo.GetFiles())
                {
                    if (ignoredNames.Contains(file.Name) || file.Attributes.HasFlag(FileAttributes.Hidden))
                        continue;

                    var item = new FileSystemItem
                    {
                        Name = file.Name,
                        FullPath = file.FullName,
                        IsFolder = false,
                        Icon = "📄",
                        IconColor = System.Windows.Media.Brushes.WhiteSmoke,
                        Size = file.Length
                    };
                    items.Add(item);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (Exception) { }

            return items;
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            _ = LoadProjectFilesAsync();
            CheckSessionStatus();
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
            }
        }

        private async void StartUploadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isUploading) return;

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

            // 1. Collect files
            StatusText.Text = "Collecting selected files...";
            var filesToUpload = new List<FileSystemItem>();
            CollectSelectedFiles(_items, filesToUpload);

            if (!filesToUpload.Any())
            {
                ModernMessageBox.Show("No files selected.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                StatusText.Text = "Ready.";
                return;
            }

            ResetUploadIndicators(filesToUpload);

            // 2. Session / Resume Logic
            HashSet<string> uploadedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string sessionPath = Path.Combine(_projectPath, SessionFileName);
            bool resumeSession = false;

            if (UseSessionCheck.IsChecked == true && File.Exists(sessionPath))
            {
                CheckSessionStatus(); // Update UI
                var result = ModernMessageBox.Show(
                    "An incomplete upload session was found.\nDo you want to RESUME from where it left off?", 
                    "Resume Upload?", 
                    MessageBoxButton.YesNo, 
                    MessageBoxImage.Question);

                if (result) // Yes = Resume
                {
                    resumeSession = true;
                    var lines = File.ReadAllLines(sessionPath);
                    foreach (var line in lines) uploadedFiles.Add(line.Trim());
                }
                else // No = Start Over
                {
                    File.Delete(sessionPath);
                    CheckSessionStatus();
                }
            }
            else if (UseSessionCheck.IsChecked == true)
            {
                // Start fresh session
                if (File.Exists(sessionPath)) File.Delete(sessionPath);
                File.Create(sessionPath).Close();
                CheckSessionStatus();
            }

            // 3. Confirm
            if (!resumeSession)
            {
                var confirm = ModernMessageBox.Show($"Start upload of {filesToUpload.Count} files to {config.FtpHost}?", 
                    "Confirm Upload", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (!confirm) return;
            }

            // 4. Start Upload
            _isUploading = true;
            StartUploadButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            UploadProgressBar.Value = 0;
            UploadProgressBar.Maximum = filesToUpload.Count;
            
            try
            {
                using (var client = new AsyncFtpClient(config.FtpHost, config.FtpUsername, EncryptionService.Decrypt(config.FtpPassword), config.FtpPort))
                {
                    StatusText.Text = "Connecting...";
                    await client.AutoConnect(token);

                    // Use _activeRemoteBasePath which was calculated in LoadProjectFilesAsync
                    // This already includes RemotePath + mapping.RemotePath (if mapping exists)
                    string remoteBasePath = !string.IsNullOrWhiteSpace(_activeRemoteBasePath)
                        ? _activeRemoteBasePath
                        : NormalizeRemoteBase(config.RemotePath);

                    int processed = 0;
                    int skipped = 0;
                    
                    // Define relativeSource before loop
                    var relativeSource = !string.IsNullOrEmpty(_scanRootPath) ? _scanRootPath : _projectPath;

                    foreach (var file in filesToUpload)
                    {
                        if (token.IsCancellationRequested) break;

                        processed++;
                        Dispatcher.Invoke(() => UploadProgressBar.Value = processed);

                        try
                        {
                            // Calculate relative path from scan root (which includes mapping's LocalPath)
                            string relativePath = Path.GetRelativePath(relativeSource, file.FullPath).Replace("\\", "/");
                            
                            // Check Session Skip
                            if (resumeSession && uploadedFiles.Contains(relativePath))
                            {
                                skipped++;
                                StatusText.Text = $"Skipped (Session): {file.Name}";
                                Dispatcher.Invoke(() =>
                                {
                                    file.UploadState = UploadState.Uploaded;
                                    UpdateUploadDetailText(file.Name, 100, file.Size, file.Size, "Already uploaded (session)");
                                });
                                continue; 
                            }

                            // Combine remote base with relative path properly
                            string remotePath = CombineRemotePaths(remoteBasePath, relativePath);
                            StatusText.Text = $"Uploading ({processed}/{filesToUpload.Count}): {file.Name}";
                            Dispatcher.Invoke(() => file.UploadState = UploadState.InProgress);

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

                            // Create directory
                            string remoteDir = Path.GetDirectoryName(remotePath)?.Replace("\\", "/");
                            if (!string.IsNullOrEmpty(remoteDir) && !await client.DirectoryExists(remoteDir, token))
                            {
                                await client.CreateDirectory(remoteDir, token);
                            }

                            // Upload
                            var existsMode = OverwriteCheck.IsChecked == true ? FtpRemoteExists.Overwrite : FtpRemoteExists.Skip;
                            await client.UploadFile(file.FullPath, remotePath, existsMode, true, FtpVerify.None, progressHandler, token);

                            Dispatcher.Invoke(() =>
                            {
                                file.UploadState = UploadState.Uploaded;
                                UpdateUploadDetailText(file.Name, 100, fileSize, fileSize, "Completed");
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
                            Dispatcher.Invoke(() =>
                            {
                                file.UploadState = UploadState.Failed;
                                UpdateUploadDetailText(file.Name, 0, 0, 0, "Failed");
                            });
                            
                            // Calculate remote path same way as upload logic
                            string relativePath = Path.GetRelativePath(relativeSource, file.FullPath).Replace("\\", "/");
                            string remotePath = CombineRemotePaths(remoteBasePath, relativePath);
                            
                            string errorMsg = "Failed to upload: " + file.Name + Environment.NewLine + Environment.NewLine +
                                            "Remote Path: " + remotePath + Environment.NewLine + Environment.NewLine +
                                            "Error: " + fileEx.Message;
                            
                            ModernMessageBox.Show(errorMsg, "Upload Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            break; // Stop upload on error
                        }
                    }

                    if (token.IsCancellationRequested)
                    {
                        StatusText.Text = "Upload Stopped by User 🛑";
                        ModernMessageBox.Show("Upload process was stopped.", "Stopped", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    else
                    {
                        StatusText.Text = "Upload Completed! ✅";
                        // Upload finished successfully, delete session
                        if (File.Exists(sessionPath)) File.Delete(sessionPath);
                        CheckSessionStatus();
                        ModernMessageBox.Show($"Upload Complete!\nUploaded: {processed - skipped}\nSkipped: {skipped}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
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
                var detailedMessage = $"Error while uploading the file to the server.\n\n" +
                                    $"Error Type: {ex.GetType().Name}\n" +
                                    $"Message: {ex.Message}\n\n" +
                                    $"Stack Trace:\n{ex.StackTrace}";
                
                ModernMessageBox.Show(detailedMessage, "Upload Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isUploading = false;
                StartUploadButton.IsEnabled = true;
                StopButton.IsEnabled = false;
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                CheckSessionStatus();
                if (UploadDetailText != null)
                {
                    UploadDetailText.Text = string.Empty;
                }
            }
        }

        private void CollectSelectedFiles(IEnumerable<FileSystemItem> items, List<FileSystemItem> collector)
        {
            foreach (var item in items)
            {
                if (!item.IsFolder && item.IsChecked == true)
                {
                    collector.Add(item);
                }
                
                if (item.Children != null && item.Children.Any())
                {
                    CollectSelectedFiles(item.Children, collector);
                }
            }
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
            if (!trimmed.StartsWith("/"))
            {
                trimmed = "/" + trimmed;
            }
            trimmed = trimmed.TrimEnd('/');
            if (trimmed.Length == 0)
            {
                trimmed = "/";
            }
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

            var combined = normalizedBase.TrimEnd('/') + "/" + segment;
            return NormalizeRemoteBase(combined);
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
                ConnectionInfoText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 183, 77));
                return;
            }

            if (string.IsNullOrWhiteSpace(_projectPath) || !Directory.Exists(_projectPath))
            {
                ConnectionInfoText.Text = "No project selected. Choose a project in Settings.";
                ConnectionInfoText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 183, 77));
                return;
            }

            var effectiveConfig = config ?? _configService.LoadProjectConfig(_projectPath);
            var accentBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(129, 212, 250));

            ConnectionProfile? profile = profileOverride ?? ResolveConnectionProfile(effectiveConfig.ConnectionProfileId);
            if (profile != null)
            {
                var protocol = profile.UseSSH ? "SFTP (SSH)" : "FTP";
                var hostText = string.IsNullOrWhiteSpace(profile.Host) ? "Host missing" : $"{profile.Host}:{profile.Port}";
                var remotePath = string.IsNullOrWhiteSpace(profile.RemotePath) ? "/" : profile.RemotePath;
                var mapping = mappingOverride ?? GetPrimaryMapping(profile);
                if (mapping != null)
                {
                    var localLabel = string.IsNullOrWhiteSpace(mapping.LocalPath) ? "(project root)" : mapping.LocalPath;
                    var remoteLabel = string.IsNullOrWhiteSpace(mapping.RemotePath) ? remotePath : mapping.RemotePath;
                    ConnectionInfoText.Text = $"Active Connection: {profile.Name} · {protocol} · {hostText} · Local '{localLabel}' → Remote '{remoteLabel}'";
                }
                else
                {
                    ConnectionInfoText.Text = $"Active Connection: {profile.Name} · {protocol} · {hostText} → {remotePath}";
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
                ConnectionInfoText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 138, 101));
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

        public string Name { get; set; } = "";
        public string FullPath { get; set; } = "";
        public bool IsFolder { get; set; }
        public string Icon { get; set; } = "";
        public System.Windows.Media.Brush IconColor { get; set; } = System.Windows.Media.Brushes.White;
        public long Size { get; set; }
        
        public string SizeDisplay => IsFolder ? "" : FormatSize(Size);
        public Visibility SizeVisibility => IsFolder ? Visibility.Collapsed : Visibility.Visible;

        public ObservableCollection<FileSystemItem> Children { get; set; } = new ObservableCollection<FileSystemItem>();
        public FileSystemItem? Parent { get; set; }

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
            UploadState.InProgress => System.Windows.Media.Brushes.DeepSkyBlue,
            UploadState.Uploaded => System.Windows.Media.Brushes.LightGreen,
            UploadState.Skipped => System.Windows.Media.Brushes.Orange,
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