using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using GitDeployPro.Controls;
using GitDeployPro.Windows;
using GitDeployPro.Services;
using GitDeployPro.Models;

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
            LoadGitData();
        }

        private async void LoadGitData()
        {
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
                    bool isSource = !string.IsNullOrEmpty(_projectConfig.DefaultSourceBranch) 
                        ? branch == _projectConfig.DefaultSourceBranch 
                        : branch == current;
                    
                    SourceBranchComboBox.Items.Add(new ComboBoxItem { Content = branch, IsSelected = isSource });
                    
                    bool isTarget = !string.IsNullOrEmpty(_projectConfig.DefaultTargetBranch) 
                        ? branch == _projectConfig.DefaultTargetBranch 
                        : false; 
                    
                    TargetBranchComboBox.Items.Add(new ComboBoxItem { Content = branch, IsSelected = isTarget });
                }

                // Fallback logic
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

                if (TargetBranchComboBox.SelectedIndex == -1)
                {
                    SelectFallbackTargetBranch(branches);
                }

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
            }
            catch (Exception ex)
            {
                AddLog($"❌ Error loading Git data: {ex.Message}");
            }
            finally
            {
                _isLoaded = true;
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
                SetActionButton("commit", "📝 REVIEW & COMMIT", "#E65100");
                string pendingText = uncommittedCount >= 0 ? uncommittedCount.ToString() : "some";
                StatusText.Text = $"⚠️ You have {pendingText} uncommitted changes!";
                StatusText.Foreground = System.Windows.Media.Brushes.Orange;
                return;
            }

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

            if (sameBranch)
            {
                SetActionButton("push", "☁️ PUSH TO GITHUB", "#24292E");
                StatusText.Text = "Branches are same. Ready to push to remote.";
                StatusText.Foreground = System.Windows.Media.Brushes.LightGray;
            }
            else
            {
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

        private void SetActionButton(string tag, string content, string colorHex)
        {
            if (ActionButton == null) return;
            
            ActionButton.Content = content;
            ActionButton.Tag = tag;
            try {
                ActionButton.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex));
            } catch { }
            ActionButton.IsEnabled = true;
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
                        FilesItemsControl.ItemsSource = null;
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
                            FilesItemsControl.ItemsSource = _fileViewModels;
                            SelectAllCheckBox.IsChecked = true;

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
            try
            {
                ActionButton.IsEnabled = false;
                ActionButton.Content = "⏳ Pushing...";
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
            finally
            {
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
                
                await SimulateDeploy(filesToDeploy);
                
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
                AddLog($"❌ Error: {ex.Message}");
                StatusText.Text = "Deployment failed.";
                StatusText.Foreground = System.Windows.Media.Brushes.Red;
                ModernMessageBox.Show($"Deployment Failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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

        private async Task SimulateDeploy(List<FileChange> files)
        {
            int total = files.Count;
            int current = 0;

            foreach (var file in files)
            {
                current++;
                AddLog($"📤 Uploading {file.Name}...");
                ProgressText.Text = $"Uploading {current}/{total}: {file.Name}";
                DeployProgressBar.Value = (current * 100) / total;
                await Task.Delay(500); 
                AddLog($"✅ Uploaded {file.Name}");
            }
            
            AddLog("🎉 All files uploaded successfully!");
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
            FilesItemsControl.ItemsSource = null;
            FilesItemsControl.ItemsSource = _fileViewModels;
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
    }
}
