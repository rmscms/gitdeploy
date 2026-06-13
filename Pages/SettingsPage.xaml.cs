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
using System.Windows.Forms; // For FolderBrowserDialog

namespace GitDeployPro.Pages
{
    public partial class SettingsPage : Page
    {
        private readonly ConfigurationService _configService;
        private readonly GitService _gitService;
        private readonly AutoStartService _autoStartService = new();
        private readonly ProjectSetupResetService _setupResetService = new();
        private static readonly string[] DefaultIgnorePatterns = new[]
        {
            "bin/", "obj/", ".vs/", ".idea/", ".vscode/", ".cursor/", "packages/", "dist/", "build/", "node_modules/", ".env", "*.log", "vendor/", ".gitdeploy.config", ".gitdeploy.history"
        };

        public SettingsPage()
        {
            InitializeComponent();
            _configService = new ConfigurationService();
            _gitService = new GitService();
            LoadSettings();
        }

        private async void LoadSettings()
        {
            try
            {
                var globalConfig = _configService.LoadGlobalConfig();
                if (!string.IsNullOrEmpty(globalConfig.LastProjectPath))
                {
                    await ReloadSettingsForPath(globalConfig.LastProjectPath);
                }
                LaunchOnStartupCheckBox.IsChecked = globalConfig.LaunchOnStartup;
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Error loading settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ReloadSettingsForPath(string path)
        {
            LocalPathTextBox.Text = path;
            var projectConfig = _configService.LoadProjectConfig(path);
            
            // Load Saved Connections
            var connections = _configService.LoadConnections();
            ConnectionProfileComboBox.ItemsSource = connections;

            // Select Saved Profile
            if (!string.IsNullOrEmpty(projectConfig.ConnectionProfileId))
            {
                var selected = connections.FirstOrDefault(c => c.Id == projectConfig.ConnectionProfileId);
                if (selected != null)
                {
                    ConnectionProfileComboBox.SelectedItem = selected;
                    UpdatePreview(selected);
                }
            }
            else if (connections.Count > 0)
            {
                // Optional: Auto-select first if none saved? Or leave empty
                ConnectionProfileComboBox.SelectedIndex = 0;
            }

            AutoInitGitCheckBox.IsChecked = projectConfig.AutoInitGit;
            AutoCommitCheckBox.IsChecked = projectConfig.AutoCommit;
            AutoPushCheckBox.IsChecked = projectConfig.AutoPush;
            
            var gitIgnoreLines = LoadOrCreateGitIgnoreLines(path);
            ExcludePatternsTextBox.Text = string.Join(Environment.NewLine, gitIgnoreLines);
            
            GitService.SetWorkingDirectory(path);
            await LoadGitInfo(projectConfig);
        }

        private async Task LoadGitInfo(ProjectConfig config)
        {
            try
            {
                if (_gitService.IsGitRepository())
                {
                    string remoteUrl = await _gitService.GetRemoteUrlAsync();
                    if (string.IsNullOrWhiteSpace(remoteUrl))
                    {
                        remoteUrl = config.GitRemoteUrl?.Trim() ?? string.Empty;
                    }
                    RemoteUrlTextBox.Text = remoteUrl;

                    var branches = await _gitService.GetBranchesAsync();
                    DefaultSourceBranchComboBox.ItemsSource = branches;
                    DefaultTargetBranchComboBox.ItemsSource = branches;

                    if (branches.Contains(config.DefaultSourceBranch))
                        DefaultSourceBranchComboBox.SelectedItem = config.DefaultSourceBranch;
                    else if (branches.Any())
                        DefaultSourceBranchComboBox.SelectedIndex = 0;

                    var selectedSource = DefaultSourceBranchComboBox.SelectedItem as string ?? config.DefaultSourceBranch;
                    var targetCandidate = ResolveDistinctTargetBranch(selectedSource, config.DefaultTargetBranch, branches);
                    if (branches.Contains(targetCandidate))
                    {
                        DefaultTargetBranchComboBox.SelectedItem = targetCandidate;
                    }
                    else
                    {
                        var firstDistinct = branches.FirstOrDefault(b => !string.Equals(b, selectedSource, StringComparison.OrdinalIgnoreCase));
                        DefaultTargetBranchComboBox.SelectedItem = firstDistinct;
                    }

                    var branchStatus = await _gitService.GetBranchStatusAsync();
                    UpdateGitPushStatusBadge(branchStatus);
                }
                else
                {
                    DefaultSourceBranchComboBox.ItemsSource = null;
                    DefaultTargetBranchComboBox.ItemsSource = null;
                    RemoteUrlTextBox.Text = config.GitRemoteUrl?.Trim() ?? string.Empty;
                    UpdateGitPushStatusBadge(new BranchStatusInfo());
                }
            }
            catch { }
        }

        private void ConnectionProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ConnectionProfileComboBox.SelectedItem is ConnectionProfile profile)
            {
                UpdatePreview(profile);
            }
            else
            {
                PreviewHostText.Text = "-";
                PreviewProtocolText.Text = "-";
                PreviewUserText.Text = "-";
                PreviewPathText.Text = "-";
            }
        }

        private void UpdatePreview(ConnectionProfile p)
        {
            PreviewHostText.Text = $"{p.Host}:{p.Port}";
            PreviewProtocolText.Text = p.UseSSH ? "SFTP (SSH)" : "FTP";
            PreviewUserText.Text = p.Username;
            PreviewPathText.Text = p.RemotePath;
        }

        private void ManageConnectionsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var manager = new ConnectionManagerWindow();
                manager.Owner = System.Windows.Application.Current.MainWindow;
                
                if (manager.ShowDialog() == true)
                {
                     // Refresh list
                     var connections = _configService.LoadConnections();
                     ConnectionProfileComboBox.ItemsSource = connections;
                     
                     if (manager.SelectedProfile != null)
                     {
                          // Try to find and select the one that was edited/created
                          var selected = connections.FirstOrDefault(c => c.Id == manager.SelectedProfile.Id);
                          if (selected != null)
                          {
                              ConnectionProfileComboBox.SelectedItem = selected;
                          }
                     }
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Failed to open Connection Manager:\n{ex.ToString()}", "Critical Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BrowseLocalButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select Local Project Folder (Git Repository)";
                dialog.ShowNewFolderButton = true;
                
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    await ReloadSettingsForPath(dialog.SelectedPath);
                }
            }
        }

        private void RefreshBranchesButton_Click(object sender, RoutedEventArgs e)
        {
            _ = LoadGitInfo(new ProjectConfig { 
                DefaultSourceBranch = DefaultSourceBranchComboBox.SelectedItem as string ?? "",
                DefaultTargetBranch = DefaultTargetBranchComboBox.SelectedItem as string ?? ""
            });
        }

        private async void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string projectPath = LocalPathTextBox.Text;
                if (string.IsNullOrWhiteSpace(projectPath))
                {
                    ModernMessageBox.Show("Please select a valid Local Project Path.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!Directory.Exists(projectPath))
                {
                     ModernMessageBox.Show("Local Project Path does not exist.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                     return;
                }

                var selectedProfile = ConnectionProfileComboBox.SelectedItem as ConnectionProfile;
                var ignoreEntries = GetIgnoreEntriesFromTextBox();
                string sourceBranch = DefaultSourceBranchComboBox.SelectedItem as string ?? "master";
                var availableBranches = DefaultSourceBranchComboBox.Items.OfType<string>().ToList();
                if (availableBranches.Count == 0)
                {
                    availableBranches = DefaultTargetBranchComboBox.Items.OfType<string>().ToList();
                }

                string requestedTarget = DefaultTargetBranchComboBox.SelectedItem as string ?? "";
                string targetBranch = ResolveDistinctTargetBranch(sourceBranch, requestedTarget, availableBranches);

                var projectConfig = new ProjectConfig
                {
                    LocalProjectPath = projectPath,
                    
                    // Save Profile ID
                    ConnectionProfileId = selectedProfile?.Id ?? "",
                    
                    // Legacy fallback (optional, can be removed later)
                    FtpHost = selectedProfile?.Host ?? "",
                    FtpPort = selectedProfile?.Port ?? 21,
                    FtpUsername = selectedProfile?.Username ?? "",
                    FtpPassword = selectedProfile?.Password ?? "",
                    UseSSH = selectedProfile?.UseSSH ?? false,
                    RemotePath = selectedProfile?.RemotePath ?? "/",

                    DefaultSourceBranch = sourceBranch,
                    DefaultTargetBranch = targetBranch,
                    GitRemoteUrl = RemoteUrlTextBox.Text?.Trim() ?? string.Empty,
                    
                    AutoInitGit = AutoInitGitCheckBox.IsChecked ?? true,
                    AutoCommit = AutoCommitCheckBox.IsChecked ?? true,
                    AutoPush = AutoPushCheckBox.IsChecked ?? false,
                    DeployMode = selectedProfile != null ? DeployMode.FtpDeploy : DeployMode.GitHubOnly
                };
                projectConfig.ExcludePatterns = ignoreEntries.ToArray();

                _configService.SaveProjectConfig(projectConfig);

                if (!string.Equals(requestedTarget, targetBranch, StringComparison.OrdinalIgnoreCase))
                {
                    DefaultTargetBranchComboBox.SelectedItem = targetBranch;
                    ModernMessageBox.Show(
                        $"Target branch was auto-adjusted to '{targetBranch}' because source and target cannot be the same.",
                        "Branch Auto-Fix",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                bool launchOnStartup = LaunchOnStartupCheckBox.IsChecked == true;

                _configService.UpdateGlobalConfig(cfg =>
                {
                    cfg.LastProjectPath = projectPath;
                    cfg.LaunchOnStartup = launchOnStartup;
                });

                _autoStartService.SetAutoStart(launchOnStartup);
                
                GitService.SetWorkingDirectory(projectPath);

                if (!_gitService.IsGitRepository())
                {
                    var initWindow = new InitGitWindow(RemoteUrlTextBox.Text);
                    initWindow.ShowDialog();

                    if (initWindow.Confirmed)
                    {
                        try
                        {
                            await _gitService.InitRepoAsync(initWindow.SelectedBranches, initWindow.RemoteUrl);
                            ModernMessageBox.Show("Git repository initialized successfully!", "Git Init", MessageBoxButton.OK, MessageBoxImage.Information);
                            await LoadGitInfo(projectConfig);
                        }
                        catch (Exception ex)
                        {
                            ModernMessageBox.Show($"Failed to initialize Git: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(RemoteUrlTextBox.Text))
                    {
                        string currentRemote = await _gitService.GetRemoteUrlAsync();
                        string newRemote = RemoteUrlTextBox.Text.Trim();

                        if (currentRemote != newRemote)
                        {
                            await _gitService.SetRemoteAsync(newRemote);
                            
                            var result = ModernMessageBox.Show(
                                "Remote URL updated! Do you want to push current branch to new remote?", 
                                "Remote Updated", 
                                MessageBoxButton.YesNo, 
                                MessageBoxImage.Question);

                            if (result)
                            {
                                try 
                                {
                                    await _gitService.PushAsync();
                                    ModernMessageBox.Show("Successfully pushed to new remote!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                                }
                                catch (Exception pushEx)
                                {
                                    ModernMessageBox.Show($"Push failed: {pushEx.Message}\nMake sure you have permissions and internet connection.", "Push Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                                }
                            }
                        }
                    }
                }

                WriteGitIgnoreFile(projectPath, ignoreEntries);
                await ApplyGitIgnoreRemovals(projectPath, ignoreEntries);

                ModernMessageBox.Show("Settings saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Error saving settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string[] LoadOrCreateGitIgnoreLines(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath)) return DefaultIgnorePatterns;

            try
            {
                string gitignorePath = Path.Combine(projectPath, ".gitignore");
                if (!File.Exists(gitignorePath))
                {
                    File.WriteAllLines(gitignorePath, DefaultIgnorePatterns);
                    return DefaultIgnorePatterns;
                }

                var lines = File.ReadAllLines(gitignorePath)
                                .Select(l => l.Trim())
                                .Where(l => !string.IsNullOrWhiteSpace(l))
                                .ToList();

                if (lines.Count == 0)
                {
                    File.WriteAllLines(gitignorePath, DefaultIgnorePatterns);
                    return DefaultIgnorePatterns;
                }

                return lines.ToArray();
            }
            catch
            {
                return DefaultIgnorePatterns;
            }
        }

        private List<string> GetIgnoreEntriesFromTextBox()
        {
            return ExcludePatternsTextBox.Text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
        }

        private void WriteGitIgnoreFile(string projectPath, IEnumerable<string> entries)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath)) return;

            try
            {
                string gitignorePath = Path.Combine(projectPath, ".gitignore");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var output = new List<string>();

                void AddLine(string line)
                {
                    if (string.IsNullOrWhiteSpace(line)) return;
                    var trimmed = line.Trim();
                    if (seen.Add(trimmed))
                    {
                        output.Add(trimmed);
                    }
                }

                foreach (var entry in entries) AddLine(entry);
                foreach (var defaults in DefaultIgnorePatterns) AddLine(defaults);

                File.WriteAllLines(gitignorePath, output);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to update .gitignore: {ex.Message}");
            }
        }

        private async Task ApplyGitIgnoreRemovals(string projectPath, IEnumerable<string> entries)
        {
            if (!_gitService.IsGitRepository()) return;

            foreach (var entry in entries)
            {
                var trimmed = entry?.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#")) continue;

                bool looksLikeFolder = trimmed.EndsWith("/") || trimmed.EndsWith("\\");
                if (!looksLikeFolder)
                {
                    var pathGuess = trimmed.Replace("/", "\\");
                    var fullPath = Path.Combine(projectPath, pathGuess);
                    looksLikeFolder = Directory.Exists(fullPath);
                }

                if (!looksLikeFolder) continue;

                var normalized = trimmed.TrimEnd('/', '\\');
                if (string.IsNullOrWhiteSpace(normalized)) continue;

                await _gitService.RemovePathFromIndexAsync(normalized);
            }
        }

        private async void AddGitIgnore_Click(object sender, RoutedEventArgs e)
        {
             string projectPath = LocalPathTextBox.Text;
             if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
             {
                 ModernMessageBox.Show("Please select a valid Local Project Path first.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                 return;
             }

             var entries = GetIgnoreEntriesFromTextBox();
             if (!entries.Any(e => string.Equals(e, ".gitdeploy.config", StringComparison.OrdinalIgnoreCase))) entries.Add(".gitdeploy.config");
             if (!entries.Any(e => string.Equals(e, ".gitdeploy.history", StringComparison.OrdinalIgnoreCase))) entries.Add(".gitdeploy.history");

             ExcludePatternsTextBox.Text = string.Join(Environment.NewLine, entries);
             WriteGitIgnoreFile(projectPath, entries);
             await ApplyGitIgnoreRemovals(projectPath, entries);
             ModernMessageBox.Show(".gitignore updated with app entries!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void UpdateGitPushStatusBadge(BranchStatusInfo status)
        {
            if (GitPushStatusBadge == null || GitPushStatusText == null) return;

            if (status.HasRemote && status.AheadCount > 0)
            {
                GitPushStatusBadge.Visibility = Visibility.Visible;
                GitPushStatusText.Text = $"Push pending: {status.AheadCount} commit(s)";
            }
            else
            {
                GitPushStatusBadge.Visibility = Visibility.Collapsed;
            }
        }

        private static string ResolveDistinctTargetBranch(string sourceBranch, string requestedTarget, List<string> availableBranches)
        {
            string source = (sourceBranch ?? string.Empty).Trim();
            string target = (requestedTarget ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(target) && !string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            {
                return target;
            }

            foreach (var preferred in new[] { "production", "master", "main", "release" })
            {
                if (!string.Equals(source, preferred, StringComparison.OrdinalIgnoreCase) &&
                    availableBranches.Any(branch => string.Equals(branch, preferred, StringComparison.OrdinalIgnoreCase)))
                {
                    return preferred;
                }
            }

            var firstDistinct = availableBranches.FirstOrDefault(branch => !string.Equals(branch, source, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(firstDistinct))
            {
                return firstDistinct;
            }

            return string.IsNullOrWhiteSpace(source) ? "production" : $"{source}-deploy";
        }

        private async void ReSetupButton_Click(object sender, RoutedEventArgs e)
        {
            string path = LocalPathTextBox.Text;
            if (string.IsNullOrWhiteSpace(path)) return;

            await RunSetupWizardAsync(path);
        }

        private async void ResetAndRunSetupButton_Click(object sender, RoutedEventArgs e)
        {
            string path = LocalPathTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                ModernMessageBox.Show("Please select a valid project folder first.", "Reset Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool removeGit = ResetGitInSettingsCheckBox.IsChecked == true;
            bool removeConfig = ResetConfigInSettingsCheckBox.IsChecked == true;
            if (!removeGit && !removeConfig)
            {
                ModernMessageBox.Show("Select at least one reset option before running cleanup.", "Reset Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ProjectSetupResetPreview preview;
            try
            {
                preview = _setupResetService.BuildPreview(path, removeGit, removeConfig);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Unable to prepare reset preview: {ex.Message}", "Reset Setup", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string targetsText = string.Join(Environment.NewLine, preview.Targets.Select(t => $"- {t}"));
            bool firstConfirm = ModernMessageBox.Show(
                $"Project folder:{Environment.NewLine}{preview.ProjectPath}{Environment.NewLine}{Environment.NewLine}Items to remove:{Environment.NewLine}{targetsText}{Environment.NewLine}{Environment.NewLine}Continue?",
                "Reinitialize Setup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                "Continue",
                "Cancel");

            if (!firstConfirm)
            {
                return;
            }

            bool secondConfirm = ModernMessageBox.Show(
                "This operation can remove local git history (.git). Do you want to continue now?",
                "Final Confirmation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                "Reset Now",
                "Cancel");

            if (!secondConfirm)
            {
                return;
            }

            if (sender is System.Windows.Controls.Button button)
            {
                button.IsEnabled = false;
                button.Content = "Resetting...";
            }

            try
            {
                var result = _setupResetService.ResetProject(path, removeGit, removeConfig);
                string removedText = result.RemovedPaths.Count == 0
                    ? "No files were removed."
                    : $"Removed:{Environment.NewLine}{string.Join(Environment.NewLine, result.RemovedPaths.Select(p => $"- {p}"))}";
                string skippedText = result.SkippedPaths.Count == 0
                    ? string.Empty
                    : $"{Environment.NewLine}{Environment.NewLine}Skipped (not found):{Environment.NewLine}{string.Join(Environment.NewLine, result.SkippedPaths.Select(p => $"- {p}"))}";

                ModernMessageBox.Show($"{removedText}{skippedText}", "Reset Completed", MessageBoxButton.OK, MessageBoxImage.Information);
                GitService.SetWorkingDirectory(path);
                await RunSetupWizardAsync(path);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Reset failed: {ex.Message}", "Reset Setup", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (sender is System.Windows.Controls.Button restoreButton)
                {
                    restoreButton.IsEnabled = true;
                    restoreButton.Content = "♻ Reset And Run Wizard";
                }
            }
        }

        private async Task RunSetupWizardAsync(string path)
        {
            var wizard = new ProjectSetupWizard(path)
            {
                Owner = System.Windows.Application.Current.MainWindow
            };

            if (wizard.ShowDialog() == true)
            {
                ModernMessageBox.Show("Project configuration re-setup successfully! 🔄", "Setup Completed", MessageBoxButton.OK, MessageBoxImage.Information);
                await ReloadSettingsForPath(path);
            }
        }
    }
}