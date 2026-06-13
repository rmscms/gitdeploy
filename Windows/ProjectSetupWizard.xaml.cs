using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FluentFTP;
using GitDeployPro.Controls;
using GitDeployPro.Models;
using GitDeployPro.Services;
using GitDeployPro.Windows;

namespace GitDeployPro.Windows
{
    public partial class ProjectSetupWizard : Window
    {
        private readonly string _projectPath;
        private readonly GitService _gitService;
        private readonly ConfigurationService _configService;
        private readonly ProjectSetupResetService _resetService;
        private List<ConnectionProfile> _connections = new List<ConnectionProfile>();
        private readonly bool _gitDetectedAtStartup;
        private string _lastErrorDetails = string.Empty;

        public bool SetupCompleted { get; private set; }

        public ProjectSetupWizard(string projectPath)
        {
            InitializeComponent();
            _projectPath = projectPath;
            _gitService = new GitService();
            _configService = new ConfigurationService();
            _resetService = new ProjectSetupResetService();
            
            GitService.SetWorkingDirectory(_projectPath);
            
            // Auto-detect if git exists
            _gitDetectedAtStartup = _gitService.IsGitRepository();
            if (_gitDetectedAtStartup)
            {
                LocalGitRadio.Content = "Existing Local Repository (Detected)";
                LocalGitPanel.Visibility = Visibility.Collapsed;
                RemoteGitRadio.IsEnabled = false;
            }
            
            LoadConnectionProfiles();
            UpdateResetHint();
        }

        private void LoadConnectionProfiles()
        {
             _connections = _configService.LoadConnections();
             ConnectionProfileComboBox.ItemsSource = _connections;
             
             if (_connections.Count > 0)
             {
                 ConnectionProfileComboBox.SelectedIndex = 0;
             }
             else
             {
                 PreviewHostText.Text = "-";
                 PreviewProtocolText.Text = "-";
                 PreviewUserText.Text = "-";
                 PreviewPathText.Text = "-";
             }
        }

        private void ConnectionProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
             if (ConnectionProfileComboBox.SelectedItem is ConnectionProfile p)
             {
                 PreviewHostText.Text = $"{p.Host}:{p.Port}";
                 PreviewProtocolText.Text = p.UseSSH ? "SFTP (SSH)" : "FTP";
                 PreviewUserText.Text = p.Username;
                 PreviewPathText.Text = p.RemotePath;
             }
        }

        private void ManageProfilesButton_Click(object sender, RoutedEventArgs e)
        {
             var manager = new ConnectionManagerWindow();
             manager.Owner = this;
             if (manager.ShowDialog() == true)
             {
                 LoadConnectionProfiles();
                 if (manager.SelectedProfile != null)
                 {
                      var selected = _connections.FirstOrDefault(c => c.Id == manager.SelectedProfile.Id);
                      if (selected != null)
                          ConnectionProfileComboBox.SelectedItem = selected;
                 }
             }
        }

        private void GitSource_Checked(object sender, RoutedEventArgs e)
        {
            if (LocalGitPanel == null || RemoteGitPanel == null) return;

            bool canReinitializeGit = !_gitDetectedAtStartup || ResetGitFolderCheckBox.IsChecked == true;

            if (LocalGitRadio.IsChecked == true)
            {
                LocalGitPanel.Visibility = canReinitializeGit ? Visibility.Visible : Visibility.Collapsed;
                RemoteGitPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                LocalGitPanel.Visibility = Visibility.Collapsed;
                RemoteGitPanel.Visibility = Visibility.Visible;
            }
        }

        private void EnableFtp_Checked(object sender, RoutedEventArgs e)
        {
            if (FtpConfigPanel == null) return;
            FtpConfigPanel.Visibility = (EnableFtpCheck.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ResetOption_Checked(object sender, RoutedEventArgs e)
        {
            UpdateResetHint();

            if (!_gitDetectedAtStartup)
            {
                return;
            }

            bool gitResetRequested = ResetGitFolderCheckBox.IsChecked == true;
            RemoteGitRadio.IsEnabled = gitResetRequested;
            if (!gitResetRequested)
            {
                LocalGitRadio.IsChecked = true;
                LocalGitPanel.Visibility = Visibility.Collapsed;
            }
            else if (LocalGitRadio.IsChecked == true)
            {
                LocalGitPanel.Visibility = Visibility.Visible;
            }
        }

        private void UpdateResetHint()
        {
            bool removeGit = ResetGitFolderCheckBox?.IsChecked == true;
            bool removeConfig = ResetConfigFileCheckBox?.IsChecked == true;

            if (!removeGit && !removeConfig)
            {
                ResetHintText.Text = "No reset selected.";
                return;
            }

            var targets = new List<string>();
            if (removeGit) targets.Add(".git");
            if (removeConfig) targets.Add(".gitdeploy.config");

            ResetHintText.Text = $"Selected for cleanup: {string.Join(", ", targets)}";
        }

        private bool ConfirmReinitializeReset()
        {
            bool removeGit = ResetGitFolderCheckBox.IsChecked == true;
            bool removeConfig = ResetConfigFileCheckBox.IsChecked == true;
            if (!removeGit && !removeConfig)
            {
                return true;
            }

            var preview = _resetService.BuildPreview(_projectPath, removeGit, removeConfig);
            string items = string.Join(Environment.NewLine, preview.Targets.Select(path => $"- {path}"));
            bool firstConfirm = ModernMessageBox.Show(
                $"Project folder:{Environment.NewLine}{preview.ProjectPath}{Environment.NewLine}{Environment.NewLine}Items to remove:{Environment.NewLine}{items}{Environment.NewLine}{Environment.NewLine}Continue?",
                "Reinitialize Project Setup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                "Continue",
                "Cancel");

            if (!firstConfirm)
            {
                return false;
            }

            return ModernMessageBox.Show(
                "This will remove selected setup artifacts from this project folder. Do you want to run it now?",
                "Final Confirmation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                "Reset Now",
                "Cancel");
        }

        private void SetBusyState(bool isBusy)
        {
            OverlayGrid.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
            CancelButton.IsEnabled = !isBusy;
            FinishButton.IsEnabled = !isBusy;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void AddLog(string message)
        {
            Dispatcher.Invoke(() =>
            {
                if (string.IsNullOrWhiteSpace(LogText.Text) || LogText.Text == "Initializing...")
                {
                    LogText.Text = message;
                }
                else
                {
                    LogText.Text += Environment.NewLine + message;
                }

                OverlayLogScrollViewer?.ScrollToEnd();
            });
        }

        private async void Finish_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!ConfirmReinitializeReset())
                {
                    return;
                }

                SetBusyState(true);
                _lastErrorDetails = string.Empty;
                CopyErrorDetailsButton.Visibility = Visibility.Collapsed;
                LogText.Text = "Initializing...";

                bool removeGit = ResetGitFolderCheckBox.IsChecked == true;
                bool removeConfig = ResetConfigFileCheckBox.IsChecked == true;
                if (removeGit || removeConfig)
                {
                    AddLog("Applying project cleanup options...");
                    var resetResult = _resetService.ResetProject(_projectPath, removeGit, removeConfig);

                    if (resetResult.RemovedPaths.Count > 0)
                    {
                        AddLog($"Removed: {string.Join(", ", resetResult.RemovedPaths.Select(Path.GetFileName))}");
                    }

                    if (resetResult.SkippedPaths.Count > 0)
                    {
                        AddLog($"Skipped (not found): {string.Join(", ", resetResult.SkippedPaths.Select(Path.GetFileName))}");
                    }

                    GitService.SetWorkingDirectory(_projectPath);
                    await Task.Delay(200);
                }

                string sourceBranch = NormalizeBranchName(SourceBranchCombo.Text, "master");
                string targetBranch = ResolveDistinctTargetBranch(sourceBranch, TargetBranchCombo.Text);
                if (!string.Equals(targetBranch, TargetBranchCombo.Text?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    TargetBranchCombo.Text = targetBranch;
                    AddLog($"Target branch adjusted to '{targetBranch}' to keep deploy flow stable.");
                }

                // 1. Git Setup
                if (!_gitService.IsGitRepository())
                {
                    if (LocalGitRadio.IsChecked == true)
                    {
                        string branch = NormalizeBranchName(LocalBranchName.Text, sourceBranch);
                        
                        AddLog("Creating .gitignore...");
                        CreateGitIgnore(_projectPath);

                        AddLog("Initializing local repository...");
                        await Task.Delay(500); // UI Refresh
                        
                        var initBranches = new List<string> { branch };
                        if (!string.IsNullOrEmpty(targetBranch) && !string.Equals(targetBranch, branch, StringComparison.OrdinalIgnoreCase))
                        {
                            initBranches.Add(targetBranch);
                        }
                        
                        await _gitService.InitRepoAsync(initBranches, "");
                    }
                    else
                    {
                        string remote = RemoteUrlBox.Text.Trim();
                        if (string.IsNullOrEmpty(remote))
                        {
                            ModernMessageBox.Show("Please enter Remote URL.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                            SetBusyState(false);
                            return;
                        }
                        
                        AddLog("Creating .gitignore...");
                        CreateGitIgnore(_projectPath);

                        AddLog("Initializing & Connecting to Remote...");
                        var initBranches = new List<string> { sourceBranch };
                        if (!string.IsNullOrEmpty(targetBranch) && !string.Equals(targetBranch, sourceBranch, StringComparison.OrdinalIgnoreCase))
                        {
                            initBranches.Add(targetBranch);
                        }
                        
                        await _gitService.InitRepoAsync(initBranches, remote);
                        
                        AddLog("Pulling changes from remote...");
                        try
                        {
                            await _gitService.PullAsync();
                        }
                        catch (Exception pullEx)
                        {
                            AddLog($"Remote pull warning: {pullEx.Message}");
                        }
                    }
                }
                else
                {
                    AddLog("Verifying Git configuration...");
                    CreateGitIgnore(_projectPath);
                }

                // 2. Create Project Config
                AddLog("Saving project configuration...");
                string detectedRemoteUrl = string.Empty;
                try
                {
                    detectedRemoteUrl = (await _gitService.GetRemoteUrlAsync()).Trim();
                }
                catch
                {
                    detectedRemoteUrl = string.Empty;
                }

                if (string.IsNullOrWhiteSpace(detectedRemoteUrl) && RemoteGitRadio.IsChecked == true)
                {
                    detectedRemoteUrl = RemoteUrlBox.Text?.Trim() ?? string.Empty;
                }

                var config = new ProjectConfig
                {
                    LocalProjectPath = _projectPath,
                    DefaultSourceBranch = sourceBranch,
                    DefaultTargetBranch = targetBranch,
                    GitRemoteUrl = detectedRemoteUrl,
                    
                    AutoInitGit = true,
                    AutoCommit = true,
                    DeployMode = EnableFtpCheck.IsChecked == true ? DeployMode.FtpDeploy : DeployMode.GitHubOnly
                };

                if (EnableFtpCheck.IsChecked == true)
                {
                    var selectedProfile = ConnectionProfileComboBox.SelectedItem as ConnectionProfile;
                    if (selectedProfile == null)
                    {
                         ModernMessageBox.Show("Please select a connection profile.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                         SetBusyState(false);
                         return;
                    }

                    config.ConnectionProfileId = selectedProfile.Id;
                    
                    // Legacy fields backup
                    config.FtpHost = selectedProfile.Host;
                    config.FtpPort = selectedProfile.Port;
                    config.FtpUsername = selectedProfile.Username;
                    config.FtpPassword = selectedProfile.Password;
                    config.UseSSH = selectedProfile.UseSSH;
                    config.RemotePath = selectedProfile.RemotePath;
                }

                _configService.SaveProjectConfig(config);

                // 3. Initial Commit (if needed)
                AddLog("Checking for changes...");
                int pendingChanges = await _gitService.GetUncommittedCountAsync();
                if (pendingChanges > 0)
                {
                    AddLog($"Committing {pendingChanges} changes...");
                    await _gitService.CommitChangesAsync("Initial setup by GitDeploy Pro");
                }

                AddLog("Setup Complete!");
                await Task.Delay(500);

                SetupCompleted = true;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                SetBusyState(false);
                _lastErrorDetails = BuildErrorDetails(ex);
                CopyErrorDetailsButton.Visibility = Visibility.Visible;
                AddLog($"Setup failed: {ex.Message}");
                ModernMessageBox.Show($"Setup failed: {GetUserFacingError(ex)}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string GetUserFacingError(Exception ex)
        {
            if (ex is GitCommandException gitEx)
            {
                return $"{gitEx.Command} {gitEx.Arguments} failed (exit {gitEx.ExitCode}). {gitEx.GetDetailedMessage()}";
            }

            return ex.Message;
        }

        private static string BuildErrorDetails(Exception ex)
        {
            if (ex is GitCommandException gitEx)
            {
                return
                    $"Command: {gitEx.Command} {gitEx.Arguments}{Environment.NewLine}" +
                    $"Working Directory: {gitEx.WorkingDirectory}{Environment.NewLine}" +
                    $"Exit Code: {gitEx.ExitCode}{Environment.NewLine}{Environment.NewLine}" +
                    $"STDERR:{Environment.NewLine}{gitEx.StandardError}{Environment.NewLine}{Environment.NewLine}" +
                    $"STDOUT:{Environment.NewLine}{gitEx.StandardOutput}";
            }

            return ex.ToString();
        }

        private void CopyErrorDetailsButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_lastErrorDetails))
            {
                ModernMessageBox.Show("No error details available yet.", "Copy Details", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                System.Windows.Clipboard.SetText(_lastErrorDetails);
                ModernMessageBox.Show("Error details copied to clipboard.", "Copy Details", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Unable to copy details: {ex.Message}", "Copy Details", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static string NormalizeBranchName(string? branchName, string fallback)
        {
            string normalized = (branchName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = fallback;
            }

            return string.IsNullOrWhiteSpace(normalized) ? "master" : normalized;
        }

        private static string ResolveDistinctTargetBranch(string sourceBranch, string? requestedTarget)
        {
            string source = NormalizeBranchName(sourceBranch, "master");
            string candidate = NormalizeBranchName(requestedTarget, "production");

            if (!string.Equals(source, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }

            foreach (var fallback in new[] { "production", "master", "main", "release" })
            {
                if (!string.Equals(source, fallback, StringComparison.OrdinalIgnoreCase))
                {
                    return fallback;
                }
            }

            return source + "-deploy";
        }

        private void CreateGitIgnore(string path)
        {
            try
            {
                string ignoreFile = Path.Combine(path, ".gitignore");
                if (!File.Exists(ignoreFile))
                {
                    var defaults = new[]
                    {
                        "bin/", "obj/", ".vs/", ".idea/", ".vscode/", ".cursor/", "dist/", "build/", ".gitdeploy.config", ".gitdeploy.history", "*.log", "node_modules/", "vendor/"
                    };
                    File.WriteAllLines(ignoreFile, defaults);
                }
            }
            catch { }
        }
    }
}