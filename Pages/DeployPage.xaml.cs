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
        private bool _compareResultActive;
        private string _compareSourceBranch = string.Empty;
        private string _compareTargetBranch = string.Empty;
        private bool _suppressFileSelectionModal;

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

                if (!ShouldKeepCompareResultsVisible() && StatusText.Text != $"⚠️ You have {uncommitted.Count} uncommitted changes!")
                {
                    StatusText.Text = "Ready...";
                    StatusText.Foreground = GetThemeBrush("Text.Muted", System.Windows.Media.Brushes.LightGray);
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
                            SetActionButton("synced", "✅ SYNCED", "Status.Success", false);
                            StatusText.Text = "Branches are synchronized.";
                            StatusText.Foreground = GetThemeBrush("Status.Success", System.Windows.Media.Brushes.LightGreen);
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
            if (!_isLoaded) return;

            ClearCompareContext(clearList: true);
            UpdateActionButtonState();
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
                StatusText.Text = $"You have {pendingText} pending file(s). Review first, then deploy -> commit -> push.";
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
            _autoRefreshTimer.Tick += (s, e) => LoadGitData();
            _autoRefreshTimer.Start();
            this.Unloaded += (s, e) => _autoRefreshTimer?.Stop();
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
            
            ActionButton.Content = content;
            ActionButton.Tag = tag;
            ActionButton.Background = ResolveBrush(colorResourceOrHex, "#444444");
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
                if (changes.Count == 0)
                {
                    StatusText.Text = "No pending files to send.";
                    StatusText.Foreground = GetThemeBrush("Text.Muted", System.Windows.Media.Brushes.LightGray);
                    DeployButton.IsEnabled = false;
                    DeployButton.Visibility = Visibility.Collapsed;
                    LoadGitData();
                    return;
                }

                var commitWindow = new CommitWindow(changes);
                commitWindow.CommitMessage = $"deploy update {AppTimeService.LocalNow:yyyy-MM-dd HH:mm}";
                commitWindow.ShowDialog();

                if (commitWindow.Confirmed)
                {
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

                    AddLog($"🚀 Step 1/2: Deploying {filesToDeploy.Count} reviewed file(s)...");
                    bool deploySucceeded = await StartDeployProcess(filesToDeploy, isAutoFlow: true, runGitPostSteps: false);
                    if (!deploySucceeded)
                    {
                        AddLog("⛔ Deploy failed. Commit+Push skipped to protect server state.");
                        StatusText.Text = "Deploy failed. Commit was not created.";
                        StatusText.Foreground = GetThemeBrush("Status.Error", System.Windows.Media.Brushes.OrangeRed);
                        return;
                    }

                    AddLog("📝 Step 2/2: Deploy succeeded, committing and pushing...");
                    await _gitService.CommitChangesAsync(commitWindow.CommitMessage);
                    AddLog("✅ Commit completed.");
                    await SyncLocalBranchesIfNeededAsync();
                    bool pushSucceeded = await PushToGithub();
                    AddLog(pushSucceeded ? "✅ Send pipeline finished." : "⚠️ Send pipeline finished with push error.");
                    await AddDeploymentHistoryRecordAsync(filesToDeploy);
                    LoadGitData();
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Send failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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

        private async Task<bool> StartDeployProcess(List<FileChange> filesToDeploy, bool isAutoFlow = false, bool runGitPostSteps = true)
        {
            isDeploying = true;
            DeployButton.IsEnabled = false;
            ActionButton.IsEnabled = false;
            SourceBranchComboBox.IsEnabled = false;
            TargetBranchComboBox.IsEnabled = false;

            try
            {
                AddLog($"🚀 Starting sync pipeline ({filesToDeploy.Count} files)...");
                
                bool ftpRequired = IsFtpDeploymentMode();
                bool hasFtpTarget = HasFtpTargetConfigured();
                if (ftpRequired && !hasFtpTarget)
                {
                    throw new InvalidOperationException("Deployment mode is FTP but no FTP/SFTP connection is configured.");
                }

                // In FTP mode we must really deploy first; Git-only mode can simulate.
                bool deployed = false;
                if (hasFtpTarget)
                {
                    deployed = await UploadFilesAsync(filesToDeploy);
                }
                else
                {
                    await SimulateDeploy(filesToDeploy);
                    deployed = true;
                }

                if (!deployed) throw new Exception("Upload failed.");

                // Sync Branches
                if (runGitPostSteps &&
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
                if (runGitPostSteps && _projectConfig.AutoPush)
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
                
                StatusText.Text = "Deployment finished successfully.";
                StatusText.Foreground = GetThemeBrush("Status.Success", System.Windows.Media.Brushes.LightGreen);

                if (runGitPostSteps)
                {
                    ClearCompareContext(clearList: true);
                }
                
                // NO SUCCESS DIALOG for auto flow
                if (!isAutoFlow)
                {
                    ModernMessageBox.Show("Deployment completed successfully! ✅", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                return true;
            }
            catch (Exception ex)
            {
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
                return false;
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
                LoadGitData();
            }
        }

        private async Task<bool> UploadFilesAsync(List<FileChange> files)
        {
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
                    
                    await client.Connect();
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
            if (LogTextBlock == null) return;

            Dispatcher.Invoke(() =>
            {
                var timestamp = AppTimeService.LocalNow.ToString("HH:mm:ss");
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