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

namespace GitDeployPro.Pages
{
    public partial class DirectUploadPage : Page
    {
        private ConfigurationService _configService;
        private string _projectPath;
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

        private async void DirectUploadPage_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadProjectFilesAsync();
            CheckSessionStatus();
        }

        private void CheckSessionStatus()
        {
            if (string.IsNullOrEmpty(_projectPath)) return;

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
                var globalConfig = _configService.LoadGlobalConfig();
                _projectPath = globalConfig.LastProjectPath;

                if (string.IsNullOrEmpty(_projectPath) || !Directory.Exists(_projectPath))
                {
                    StatusText.Text = "No project selected.";
                    return;
                }

                StatusText.Text = "Scanning files...";
                StartUploadButton.IsEnabled = false;

                _items.Clear();

                await Task.Run(() =>
                {
                    var rootItems = ScanDirectory(_projectPath);
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

            var config = _configService.LoadProjectConfig(_projectPath);
            if (string.IsNullOrEmpty(config.FtpHost))
            {
                ModernMessageBox.Show("FTP Configuration is missing.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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

                    string remoteBasePath = config.RemotePath;
                    if (!remoteBasePath.EndsWith("/")) remoteBasePath += "/";

                    int processed = 0;
                    int skipped = 0;

                    foreach (var file in filesToUpload)
                    {
                        if (token.IsCancellationRequested) break;

                        processed++;
                        Dispatcher.Invoke(() => UploadProgressBar.Value = processed);

                        string relativePath = Path.GetRelativePath(_projectPath, file.FullPath).Replace("\\", "/");
                        
                        // Check Session Skip
                        if (resumeSession && uploadedFiles.Contains(relativePath))
                        {
                            skipped++;
                            StatusText.Text = $"Skipped (Session): {file.Name}";
                            continue; 
                        }

                        string remotePath = remoteBasePath + relativePath;
                        StatusText.Text = $"Uploading ({processed}/{filesToUpload.Count}): {file.Name}";

                        // Create directory
                        string remoteDir = Path.GetDirectoryName(remotePath)?.Replace("\\", "/");
                        if (!string.IsNullOrEmpty(remoteDir) && !await client.DirectoryExists(remoteDir, token))
                        {
                            await client.CreateDirectory(remoteDir, token);
                        }

                        // Upload
                        var existsMode = OverwriteCheck.IsChecked == true ? FtpRemoteExists.Overwrite : FtpRemoteExists.Skip;
                        await client.UploadFile(file.FullPath, remotePath, existsMode, true, FtpVerify.None, null, token);

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
                ModernMessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isUploading = false;
                StartUploadButton.IsEnabled = true;
                StopButton.IsEnabled = false;
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                CheckSessionStatus();
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
    }

    public class FileSystemItem : INotifyPropertyChanged
    {
        private bool? _isChecked = false;
        private bool _isExpanded;

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