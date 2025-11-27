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

        public DeployPage()
        {
            InitializeComponent();
            _isLoaded = true;
            _gitService = new GitService();
            _historyService = new HistoryService();
            _configService = new ConfigurationService();
            _projectConfig = new ProjectConfig();
            LoadGitData();
        }

        private async void LoadGitData()
        {
            try
            {
                if (!_gitService.IsGitRepository())
                {
                    StatusText.Text = "⚠️ Git repository not found (Initialize Git in Settings)";
                    StatusText.Foreground = System.Windows.Media.Brushes.Orange;
                    
                    SourceBranchComboBox.Items.Clear();
                    TargetBranchComboBox.Items.Clear();
                    SourceBranchComboBox.IsEnabled = false;
                    TargetBranchComboBox.IsEnabled = false;
                    CompareButton.IsEnabled = false;
                    PushToGithubButton.IsEnabled = false;
                    return;
                }

                // Check for uncommitted changes
                var uncommitted = await _gitService.GetUncommittedChangesAsync();
                if (uncommitted.Count > 0)
                {
                    ShowCommitUI(true, uncommitted.Count);
                }
                else
                {
                    ShowCommitUI(false);
                }

                // Load Project Config
                string defaultSource = "master";
                string defaultTarget = "";
                
                var globalConfig = _configService.LoadGlobalConfig();
                if (!string.IsNullOrEmpty(globalConfig.LastProjectPath))
                {
                    _projectConfig = _configService.LoadProjectConfig(globalConfig.LastProjectPath);
                    if (!string.IsNullOrEmpty(_projectConfig.DefaultSourceBranch))
                        defaultSource = _projectConfig.DefaultSourceBranch;
                    if (!string.IsNullOrEmpty(_projectConfig.DefaultTargetBranch))
                        defaultTarget = _projectConfig.DefaultTargetBranch;
                }

                var branches = await _gitService.GetBranchesAsync();
                var current = await _gitService.GetCurrentBranchAsync();

                SourceBranchComboBox.Items.Clear();
                TargetBranchComboBox.Items.Clear();

                foreach (var branch in branches)
                {
                    bool isSource = branch == defaultSource; 
                    
                    SourceBranchComboBox.Items.Add(new ComboBoxItem { Content = branch, IsSelected = isSource });
                    
                    bool isTarget = branch == defaultTarget;
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

                SourceBranchComboBox.IsEnabled = true;
                TargetBranchComboBox.IsEnabled = true;
                CompareButton.IsEnabled = true;
                PushToGithubButton.IsEnabled = true;

                if (StatusText.Text != $"⚠️ You have {uncommitted.Count} uncommitted changes!")
                {
                    StatusText.Text = "Ready to compare changes...";
                    StatusText.Foreground = System.Windows.Media.Brushes.LightGray;
                }
            }
            catch (Exception ex)
            {
                AddLog($"❌ Error loading Git data: {ex.Message}");
            }
        }

        private void ShowCommitUI(bool show, int count = 0)
        {
            if (show)
            {
                StatusText.Text = $"⚠️ You have {count} uncommitted changes!";
                StatusText.Foreground = System.Windows.Media.Brushes.Orange;
                CompareButton.Content = "📝 REVIEW & COMMIT";
                CompareButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 143, 0));
                CompareButton.Tag = "commit";
                PushToGithubButton.IsEnabled = false; // Disable push until committed
            }
            else
            {
                StatusText.Text = "Ready to compare changes...";
                StatusText.Foreground = System.Windows.Media.Brushes.LightGray;
                CompareButton.Content = "🔍 COMPARE";
                CompareButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 81, 0));
                CompareButton.Tag = "compare";
                PushToGithubButton.IsEnabled = true;
            }
        }

        private void SelectFallbackTargetBranch(List<string> branches)
        {
            int targetIndex = -1;
            targetIndex = branches.IndexOf("master");
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

            if (sender is System.Windows.Controls.ComboBox combo && combo.SelectedItem is ComboBoxItem selected)
            {
                string type = combo.Name == "SourceBranchComboBox" ? "Source" : "Target";
                AddLog($"🔀 {type} Branch selected: {selected.Content}");
            }
        }

        private async void CompareButton_Click(object sender, RoutedEventArgs e)
        {
            if (CompareButton.Tag?.ToString() == "commit")
            {
                await HandleCommit();
                return;
            }

            await HandleCompare();
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

                if (source == null || target == null) return;

                if (source == target)
                {
                    ModernMessageBox.Show("Source and Target branches must be different.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                try
                {
                    CompareButton.IsEnabled = false;
                    CompareButton.Content = "⏳ Comparing...";

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
                        // Open DiffWindow with checkboxes
                        var diffWindow = new DiffWindow(changes);
                        diffWindow.ShowDialog();

                        if (diffWindow.Confirmed)
                        {
                            // User clicked "DEPLOY SELECTED" in DiffWindow
                            var selectedFiles = diffWindow.SelectedFiles;
                            
                            AddLog($"✅ Selection confirmed. Starting deploy for {selectedFiles.Count} files.");
                            
                            // Map back to ViewModel just for displaying list if needed (optional)
                            _fileViewModels = selectedFiles.Select(c => new DeployFileViewModel(c) { IsSelected = true }).ToList();
                            FilesItemsControl.ItemsSource = _fileViewModels;
                            SelectAllCheckBox.IsChecked = true;

                            // START DEPLOY AUTOMATICALLY
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
                    CompareButton.IsEnabled = true;
                    CompareButton.Content = "🔍 COMPARE";
                }
            }
        }

        private async void PushToGithubButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                PushToGithubButton.IsEnabled = false;
                PushToGithubButton.Content = "⏳ Pushing...";
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
                PushToGithubButton.IsEnabled = true;
                PushToGithubButton.Content = "☁️ Push to GitHub";
            }
        }

        private async void DeployButton_Click(object sender, RoutedEventArgs e)
        {
            // This button is now mostly for re-deploying or if user cancelled auto-deploy
            
            if (isDeploying) return;

            // Filter selected files from UI
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
            CompareButton.IsEnabled = false;
            PushToGithubButton.IsEnabled = false;
            SourceBranchComboBox.IsEnabled = false;
            TargetBranchComboBox.IsEnabled = false;

            try
            {
                AddLog($"🚀 Starting deployment process ({filesToDeploy.Count} files)...");
                
                // 1. Upload files
                await SimulateDeploy(filesToDeploy);
                
                // 2. Save History
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

                // 3. Sync Local Branches (Merge Source into Target)
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

                // 4. Auto-Push to GitHub if enabled
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
                isDeploying = false;
                DeployButton.IsEnabled = true; // Re-enable for retry
                CompareButton.IsEnabled = true;
                PushToGithubButton.IsEnabled = true;
                SourceBranchComboBox.IsEnabled = true;
                TargetBranchComboBox.IsEnabled = true;
                DeployProgressBar.Value = 0;
                ProgressText.Text = "Deployment finished!";
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

        // ... SelectAll_Click, AddLog, ClearLogs_Click ...
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
