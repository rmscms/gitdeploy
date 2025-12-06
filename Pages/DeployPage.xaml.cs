using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using GitDeployPro.Controls;
using GitDeployPro.Windows;
using GitDeployPro.Services;
using GitDeployPro.Models;
using FluentFTP;

namespace GitDeployPro.Pages
{
    public partial class DeployPage : Page
    {
        private bool isDeploying = false;
        private GitService _gitService;
        private HistoryService _historyService;
        private ConfigurationService _configService;
        private List<DeployFileViewModel> _fileViewModels = new List<DeployFileViewModel>();
        private bool _isLoaded = false;
        private ProjectConfig _projectConfig;
        private BranchStatusInfo _branchStatus = new BranchStatusInfo();
        private DispatcherTimer _autoRefreshTimer;
        private bool _isRefreshingGit;
        private int _cachedUncommittedCount = -1;
        private int _cachedTotalCommits = -1;

        public DeployPage()
        {
            InitializeComponent();
            _isLoaded = false;
            _gitService = new GitService();
            _historyService = new HistoryService();
            _configService = new ConfigurationService();
            _projectConfig = new ProjectConfig();
            _autoRefreshTimer = new DispatcherTimer(); // Initialize explicitly
            LoadGitData();
            SetupAutoRefreshTimer();
        }

        private void DetachDeployPage_Click(object sender, RoutedEventArgs e)
        {
            var window = new PageHostWindow(new DeployPage(), "Deploy • Detached");
            window.Show();
        }

        private async void LoadGitData()
        {
            if (_isRefreshingGit) return;
            _isRefreshingGit = true;
            _isLoaded = false;
            try
            {
                if (!_gitService.IsGitRepository())
                {
                    StatusText.Text = "⚠️ Git repository not found (Initialize Git in Settings)";
                    StatusText.Foreground = System.Windows.Media.Brushes.Orange;
                    DisableAllButtons();
                    return;
                }

                // Load Project Config
                var globalConfig = _configService.LoadGlobalConfig();
                if (!string.IsNullOrEmpty(globalConfig.LastProjectPath))
                {
                    _projectConfig = _configService.LoadProjectConfig(globalConfig.LastProjectPath);
                }

                // Check changes & commits
                var uncommitted = await _gitService.GetUncommittedChangesAsync();
                var totalCommits = await _gitService.GetTotalCommitsAsync();
                _cachedUncommittedCount = uncommitted.Count;
                _cachedTotalCommits = totalCommits;

                // Check Branches
                var branches = await _gitService.GetBranchesAsync();
                var current = await _gitService.GetCurrentBranchAsync();

                SourceBranchComboBox.Items.Clear();
                TargetBranchComboBox.Items.Clear();

                foreach (var branch in branches)
                {
                    // Source Logic
                    bool isSource = !string.IsNullOrEmpty(_projectConfig.DefaultSourceBranch) 
                        ? branch == _projectConfig.DefaultSourceBranch 
                        : branch == current;
                    
                    SourceBranchComboBox.Items.Add(new ComboBoxItem { Content = branch, IsSelected = isSource });
                    
                    // Target Logic - Add all branches, try to select default
                    bool isTarget = !string.IsNullOrEmpty(_projectConfig.DefaultTargetBranch) 
                        ? branch == _projectConfig.DefaultTargetBranch 
                        : false; 
                    
                    TargetBranchComboBox.Items.Add(new ComboBoxItem { Content = branch, IsSelected = isTarget });
                }

                // If no default target selected, try fallback
                if (TargetBranchComboBox.SelectedIndex == -1)
                {
                    SelectFallbackTargetBranch(branches);
                }

                // Fallback logic for Source
                if (SourceBranchComboBox.SelectedIndex == -1 && SourceBranchComboBox.Items.Count > 0)
                {
                    for(int i=0; i<SourceBranchComboBox.Items.Count; i++)
                    {
                        if ((SourceBranchComboBox.Items[i] as ComboBoxItem)?.Content?.ToString() == current)
                        {
                            SourceBranchComboBox.SelectedIndex = i;
                            break;
                        }
                    }
                    if (SourceBranchComboBox.SelectedIndex == -1) SourceBranchComboBox.SelectedIndex = 0;
                }

                await RefreshBranchStatusAsync();

                // Determine Button State
                UpdateActionButtonState(_cachedUncommittedCount, _cachedTotalCommits);

                SourceBranchComboBox.IsEnabled = true;
                TargetBranchComboBox.IsEnabled = true;
                DeployButton.IsEnabled = false; // Initially disabled

                if (StatusText.Text != $"⚠️ You have {uncommitted.Count} uncommitted changes!")
                {
                    StatusText.Text = "Ready...";
                    StatusText.Foreground = System.Windows.Media.Brushes.LightGray;
                }
                
                // LOG UNCOMMITTED FOR DEBUG
                if (uncommitted.Count > 0) AddLog($"[DEBUG] Found {uncommitted.Count} uncommitted changes.");

                // --- NEW: Check if branches are already synced ---
                if (SourceBranchComboBox.SelectedItem is ComboBoxItem src && 
                    TargetBranchComboBox.SelectedItem is ComboBoxItem tgt)
                {
                    string? s = src.Content?.ToString();
                    string? t = tgt.Content?.ToString();
                    
                    // Check uncommitted again to be safe
                    if (uncommitted.Count == 0 && !string.IsNullOrEmpty(s) && !string.IsNullOrEmpty(t) && s != t)
                    {
                        var diff = await _gitService.GetDiffAsync(s, t);
                        if (diff.Count == 0)
                        {
                            SetActionButton("synced", "✅ SYNCED", "#2E7D32", false);
                            StatusText.Text = "Branches are synchronized.";
                            StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog($"❌ Error loading Git data: {ex.Message}");
            }
            finally
            {
                _isLoaded = true;
                _isRefreshingGit = false;
            }
        }

        private void DisableAllButtons()
        {
            SourceBranchComboBox.Items.Clear();
            TargetBranchComboBox.Items.Clear();
            SourceBranchComboBox.IsEnabled = false;
            TargetBranchComboBox.IsEnabled = false;
            if (ActionButton != null) ActionButton.IsEnabled = false;
            if (DeployButton != null) DeployButton.IsEnabled = false;
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
            if (!_isLoaded) return;

            if (DeployButton != null) DeployButton.IsEnabled = false;
            UpdateActionButtonState();
        }

        private void UpdateActionButtonState(int uncommittedCount = -1, int totalCommits = -1)
        {
            if (ActionButton == null) return;

            if (uncommittedCount == -1) uncommittedCount = _cachedUncommittedCount;
            if (totalCommits == -1) totalCommits = _cachedTotalCommits;

            if (totalCommits == 0)
            {
                SetActionButton("commit", "📝 REVIEW & COMMIT", "#E65100");
                StatusText.Text = "⚠️ Create the initial commit before deploying.";
                StatusText.Foreground = System.Windows.Media.Brushes.Orange;
                return;
            }

            if (uncommittedCount > 0)
            {
                // Priority 1: If there are uncommitted changes, button MUST be "REVIEW & COMMIT"
                SetActionButton("commit", "📝 REVIEW & COMMIT", "#E65100", true); // Force Enabled
                string pendingText = uncommittedCount >= 0 ? uncommittedCount.ToString() : "some";
                StatusText.Text = $"⚠️ You have {pendingText} uncommitted changes!";
                StatusText.Foreground = System.Windows.Media.Brushes.Orange;
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
                    // Case 1: No Remote
                    SetActionButton("push", "☁️ PUSH TO GITHUB", "#555555", false); // Disabled or Gray
                    StatusText.Text = "No remote repository configured. Nothing to deploy/push.";
                    StatusText.Foreground = System.Windows.Media.Brushes.Gray;
                    return;
                }

                // Remote exists
                int ahead = _branchStatus != null ? _branchStatus.AheadCount : 0;
                
                if (ahead > 0)
                {
                    string pushLabel = $"☁️ PUSH ({ahead})";
                    SetActionButton("push", pushLabel, "#24292E", true);
                    StatusText.Text = "You have commits pending push.";
                    StatusText.Foreground = System.Windows.Media.Brushes.Orange;
                }
                else
                {
                    // No commits to push -> Check for Synced State from ActionButton Tag or assume synced
                    // BUT we just confirmed uncommittedCount == 0.
                    
                    // If we previously set it to "synced" because of deployment success, keep it?
                    // Or revert to "NOTHING TO PUSH"? 
                    // User wants "Synced" if synced.
                    // But here source==target, so they are definitionally synced locally.
                    // The question is sync with REMOTE.
                    
                    // Let's stick to "NOTHING TO PUSH" for same branch, unless user prefers "Synced".
                    // "Synced" usually implies source vs target branches.
                    // Here source == target, so "Nothing to Push" is more accurate for remote sync.
                    
                    SetActionButton("push", "✅ NOTHING TO PUSH", "#2E7D32", false); 
                    StatusText.Text = "Branch is up to date with remote.";
                    StatusText.Foreground = System.Windows.Media.Brushes.LightGray;
                }
            }
            else
            {
                // Different branches (Source != Target)
                // We rely on the explicit check in LoadGitData to set "synced" state initially.
                // If we are here, it means LoadGitData hasn't set it OR we need to determine state.
                
                // CRITICAL FIX: Do NOT block updates if tag was "synced" but now we have changes.
                // We already checked uncommitted > 0 at the top. If we are here, uncommitted == 0.
                // So if tag is "synced", it means we are likely still synced.
                // UNLESS a push happened on source that made them diverge?
                // Re-checking Diff here is expensive.
                // We will trust the "synced" tag if set by LoadGitData/Deploy success, 
                // UNLESS uncommitted changes appeared (handled above).
                
                if (ActionButton.Tag?.ToString() == "synced")
                {
                     return;
                }

                SetActionButton("compare", "🔍 COMPARE", "#E65100");
                StatusText.Text = "Ready to compare branches...";
                StatusText.Foreground = System.Windows.Media.Brushes.LightGray;
            }
        }

        private void SetBranchSelectionRequiredState()
        {
            if (ActionButton == null) return;

            ActionButton.Content = "Select Branches";
            ActionButton.Tag = null;
            ActionButton.IsEnabled = false;
            try
            {
                ActionButton.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#444444"));
            }
            catch { }

            StatusText.Text = "Select source and target branches to continue.";
            StatusText.Foreground = System.Windows.Media.Brushes.Orange;
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
            _autoRefreshTimer.Tick += (s, e) => LoadGitData();
            _autoRefreshTimer.Start();
            this.Unloaded += (s, e) => _autoRefreshTimer?.Stop();
        }

        private void SetActionButton(string tag, string content, string colorHex, bool isEnabled = true)
        {
            if (ActionButton == null) return;
            
            ActionButton.Content = content;
            ActionButton.Tag = tag;
            try {
                ActionButton.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex));
            } catch { }
            ActionButton.IsEnabled = isEnabled;
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
                var commitWindow = new CommitWindow(changes);
                commitWindow.ShowDialog();

                if (commitWindow.Confirmed)
                {
                    AddLog("📝 Committing changes...");
                    await _gitService.CommitChangesAsync(commitWindow.CommitMessage);
                    AddLog("✅ Changes committed successfully!");
                    
                    LoadGitData(); 
                    ModernMessageBox.Show("Changes committed successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Commit Failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task HandleCompare()
        {
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
                        ModernMessageBox.Show("No changes found between branches.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                        StatusText.Text = "No changes to deploy.";
                        DeployButton.IsEnabled = false;
                        _fileViewModels.Clear();
                        FilesListBox.ItemsSource = null;
                        UpdateDiffViewerFromSelection();
                    }
                    else
                    {
                        var diffWindow = new DiffWindow(changes);
                        diffWindow.ShowDialog();

                        if (diffWindow.Confirmed)
                        {
                            var selectedFiles = diffWindow.SelectedFiles;
                            AddLog($"✅ Selection confirmed. Starting deploy for {selectedFiles.Count} files.");
                            
                            _fileViewModels = selectedFiles.Select(c => new DeployFileViewModel(c) { IsSelected = true }).ToList();
                            FilesListBox.ItemsSource = _fileViewModels;
                            SelectAllCheckBox.IsChecked = true;
                            FilesListBox.SelectedIndex = _fileViewModels.Count > 0 ? 0 : -1;
                            UpdateDiffViewerFromSelection();

                            await StartDeployProcess(selectedFiles);
                        }
                        else
                        {
                            StatusText.Text = "❌ Comparison/Deploy cancelled.";
                            StatusText.Foreground = System.Windows.Media.Brushes.Orange;
                            DeployButton.IsEnabled = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    ModernMessageBox.Show($"Git Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    ActionButton.IsEnabled = true;
                    UpdateActionButtonState();
                }
            }
        }

        private async Task PushToGithub()
        {
            if (ActionButton == null) return;

            ActionButton.IsEnabled = false;
            ActionButton.Content = "⏳ Pushing...";
            try
            {
                AddLog("☁️ Pushing changes to GitHub...");

                await _gitService.PushAsync();
                
                AddLog("✅ Successfully pushed to GitHub!");
                ModernMessageBox.Show("Successfully pushed changes to GitHub! ✅", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AddLog($"❌ Push failed: {ex.Message}");
                ModernMessageBox.Show($"Push failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            await RefreshBranchStatusAsync();
            ActionButton.IsEnabled = true;
            UpdateActionButtonState();
        }

        private async void DeployButton_Click(object sender, RoutedEventArgs e)
        {
            if (isDeploying) return;

            var selectedFiles = _fileViewModels.Where(x => x.IsSelected).Select(x => new FileChange { Name = x.Name, Type = x.Type }).ToList();
            
            if (selectedFiles.Count == 0)
            {
                ModernMessageBox.Show("Please select at least one file to deploy.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await StartDeployProcess(selectedFiles);
        }

        private async Task StartDeployProcess(List<FileChange> filesToDeploy)
        {
            isDeploying = true;
            DeployButton.IsEnabled = false;
            ActionButton.IsEnabled = false;
            SourceBranchComboBox.IsEnabled = false;
            TargetBranchComboBox.IsEnabled = false;

            try
            {
                AddLog($"🚀 Starting deployment process ({filesToDeploy.Count} files)...");
                
                // Use REAL Deploy if FTP is configured, otherwise Simulate
                bool deployed = false;
                if (!string.IsNullOrEmpty(_projectConfig.FtpHost))
                {
                    deployed = await UploadFilesAsync(filesToDeploy);
                }
                else
                {
                    await SimulateDeploy(filesToDeploy);
                    deployed = true;
                }

                if (!deployed) throw new Exception("Upload failed.");

                string commitHash = await _gitService.GetLastCommitHashAsync();
                
                var record = new DeploymentRecord
                {
                    Title = $"Deploy {SourceBranchComboBox.Text} to {TargetBranchComboBox.Text}",
                    Date = DateTime.Now,
                    FilesCount = filesToDeploy.Count,
                    Branch = SourceBranchComboBox.Text,
                    Status = "Success",
                    Files = filesToDeploy.Select(x => x.Name).ToList(),
                    CommitHash = commitHash
                };
                _historyService.AddRecord(record);

                // Sync Branches
                if (SourceBranchComboBox.SelectedItem is ComboBoxItem sourceItem && 
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
                            // We assume sync worked, so diff is empty.
                            // UpdateActionButtonState won't know this unless we refresh, 
                            // but Refresh is called in finally block via LoadGitData().
                            // However, LoadGitData might take a moment.
                            // We can manually set button state here for better UX.
                            
                            Dispatcher.Invoke(() =>
                            {
                                SetActionButton("synced", "✅ SYNCED", "#2E7D32", false);
                                StatusText.Text = "Branches are synchronized.";
                                StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
                            });
                        }
                        catch (Exception syncEx)
                        {
                            AddLog($"⚠️ Branch sync failed: {syncEx.Message}");
                        }
                    }
                }

                // Auto-Push
                if (_projectConfig.AutoPush)
                {
                    AddLog("☁️ Auto-pushing to GitHub...");
                    try
                    {
                        await _gitService.PushAsync();
                        AddLog("✅ Successfully pushed to GitHub!");
                    }
                    catch (Exception pushEx)
                    {
                        AddLog($"⚠️ Auto-push failed: {pushEx.Message}");
                    }
                }
                
                StatusText.Text = "Deployment finished successfully.";
                StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
                
                ModernMessageBox.Show("Deployment completed successfully! ✅", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AddLog($"❌ Error: {ex}");
                StatusText.Text = "Deployment failed.";
                StatusText.Foreground = System.Windows.Media.Brushes.Red;
                var detailed = ex.ToString();
                try
                {
                    System.Windows.Clipboard.SetText(detailed);
                }
                catch
                {
                    // Clipboard might fail in some contexts; ignore.
                }
                ModernMessageBox.Show($"Deployment Failed:\n\n{detailed}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _gitService.EnsureGitFolderHidden();
                isDeploying = false;
                DeployButton.IsEnabled = true; 
                ActionButton.IsEnabled = true;
                SourceBranchComboBox.IsEnabled = true;
                TargetBranchComboBox.IsEnabled = true;
                DeployProgressBar.Value = 0;
                ProgressText.Text = "Deployment finished!";
                LoadGitData();
            }
        }

        private async Task<bool> UploadFilesAsync(List<FileChange> files)
        {
            try
            {
                AddLog($"🔌 Connecting to {_projectConfig.FtpHost}...");
                
                using (var client = new AsyncFtpClient(_projectConfig.FtpHost, _projectConfig.FtpUsername, _projectConfig.FtpPasswordDecrypted, _projectConfig.FtpPort))
                {
                    await client.Connect();
                    AddLog("✅ Connected!");

                    int total = files.Count;
                    int current = 0;

                    var profile = GetActiveConnectionProfile();
                    var mapping = GetPrimaryMapping(profile);
                    var defaultRemoteBase = NormalizeRemoteBase(_projectConfig.RemotePath);
                    var mappedRemoteBase = mapping != null
                        ? CombineRemotePaths(defaultRemoteBase, mapping.RemotePath)
                        : defaultRemoteBase;
                    var mappingLocalSegment = NormalizeLocalMappingSegment(mapping?.LocalPath);

                    foreach (var file in files)
                    {
                        current++;
                        if (file.Type == ChangeType.Deleted)
                        {
                            continue;
                        }

                        string localPath = System.IO.Path.Combine(_projectConfig.LocalProjectPath, file.Name);
                        if (!System.IO.File.Exists(localPath)) continue;

                        string relativePath = file.Name.Replace("\\", "/");
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
                        
                        string remoteDir = System.IO.Path.GetDirectoryName(remotePath)?.Replace("\\", "/");
                        if (!string.IsNullOrEmpty(remoteDir))
                        {
                             if (!await client.DirectoryExists(remoteDir))
                             {
                                 await client.CreateDirectory(remoteDir); 
                             }
                        }

                        AddLog($"📤 Uploading {file.Name}...");
                        ProgressText.Text = $"Uploading {current}/{total}: {file.Name}";
                        DeployProgressBar.Value = (current * 100) / total;

                        await client.UploadFile(localPath, remotePath, FtpRemoteExists.Overwrite);
                        AddLog($"✅ Uploaded {file.Name}");
                    }
                }
                AddLog("🎉 All files uploaded successfully!");
                return true;
            }
            catch (Exception ex)
            {
                AddLog($"❌ Upload Error: {ex.Message}");
                throw new InvalidOperationException($"Upload failed: {ex.GetType().Name} - {ex.Message}", ex);
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
            if (trimmed.StartsWith("///"))
            {
                return NormalizeRemoteBase(trimmed.Substring(2));
            }

            var segment = trimmed.Trim('/');
            if (string.IsNullOrEmpty(segment))
            {
                return normalizedBase;
            }

            var combined = normalizedBase.TrimEnd('/') + "/" + segment;
            return NormalizeRemoteBase(combined);
        }

        private async Task SimulateDeploy(List<FileChange> files)
        {
            int total = files.Count;
            int current = 0;

            foreach (var file in files)
            {
                current++;
                AddLog($"[SIMULATION] 📤 Uploading {file.Name}...");
                ProgressText.Text = $"Simulating {current}/{total}: {file.Name}";
                DeployProgressBar.Value = (current * 100) / total;
                await Task.Delay(200); 
                AddLog($"[SIMULATION] ✅ Uploaded {file.Name}");
            }
            
            AddLog("🎉 Simulation complete!");
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
            UpdateDiffViewerFromSelection();
        }

        private void FilesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateDiffViewerFromSelection();
        }

        private void UpdateDiffViewerFromSelection()
        {
            if (FileDiffViewer == null) return;

            if (FilesListBox?.SelectedItem is DeployFileViewModel vm)
            {
                FileDiffViewer.Title = vm.Name;
                FileDiffViewer.Status = vm.StatusText;
                FileDiffViewer.FilePath = vm.Name;
                FileDiffViewer.DiffText = vm.DiffText;
            }
            else
            {
                FileDiffViewer.Title = "Diff preview";
                FileDiffViewer.Status = string.Empty;
                FileDiffViewer.FilePath = string.Empty;
                FileDiffViewer.DiffText = string.Empty;
            }
        }

        private void FilesListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (FilesListBox?.SelectedItem is DeployFileViewModel vm)
            {
                OpenCodeViewer(vm);
            }
        }

        private void OpenCodeViewer(DeployFileViewModel vm)
        {
            try
            {
                var absolutePath = ResolveAbsolutePath(vm.Name);
                var content = File.Exists(absolutePath) ? File.ReadAllText(absolutePath) : vm.DiffText ?? string.Empty;
                var viewer = new CodeViewerWindow(vm.Name, content, absolutePath)
                {
                    Owner = Window.GetWindow(this)
                };
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
                OpenCodeViewer(vm);
                e.Handled = true;
            }
        }

        private string ResolveAbsolutePath(string relativePath)
        {
            var root = _projectConfig?.LocalProjectPath;
            if (string.IsNullOrWhiteSpace(root))
            {
                root = GitService.WorkingDirectoryPath;
            }

            var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
            return string.IsNullOrWhiteSpace(root)
                ? normalized
                : Path.Combine(root, normalized);
        }

        private void AddLog(string message)
        {
            if (LogTextBlock == null) return;

            Dispatcher.Invoke(() =>
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss");
                var newLog = $"[{timestamp}] {message}\n";
                
                if (LogTextBlock.Text == "Waiting for deployment...")
                {
                    LogTextBlock.Text = newLog;
                }
                else
                {
                    LogTextBlock.Text += newLog;
                }

                LogScrollViewer?.ScrollToEnd();
            });
        }

        private void ClearLogs_Click(object sender, RoutedEventArgs e)
        {
            if (LogTextBlock != null)
            {
                LogTextBlock.Text = "Waiting for deployment...";
                AddLog("🗑️ Logs cleared");
            }
        }

        private void NewBranchButton_Click(object sender, RoutedEventArgs e)
        {
            var inputDialog = new InputDialog("Create New Branch", "Enter new branch name:");
            if (inputDialog.ShowDialog() == true)
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