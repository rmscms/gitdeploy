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
        private List<ConnectionProfile> _connections;

        public bool SetupCompleted { get; private set; }

        public ProjectSetupWizard(string projectPath)
        {
            InitializeComponent();
            _projectPath = projectPath;
            _gitService = new GitService();
            _configService = new ConfigurationService();
            
            GitService.SetWorkingDirectory(_projectPath);
            
            // Auto-detect if git exists
            if (_gitService.IsGitRepository())
            {
                LocalGitRadio.Content = "Existing Local Repository (Detected)";
                LocalGitPanel.Visibility = Visibility.Collapsed;
                RemoteGitRadio.IsEnabled = false;
            }
            
            LoadConnectionProfiles();
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

            if (LocalGitRadio.IsChecked == true)
            {
                LocalGitPanel.Visibility = Visibility.Visible;
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

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void AddLog(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogText.Text = message;
            });
        }

        private async void Finish_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // UI Busy State
                OverlayGrid.Visibility = Visibility.Visible;
                CancelButton.IsEnabled = false;
                FinishButton.IsEnabled = false;

                // 1. Git Setup
                if (!_gitService.IsGitRepository())
                {
                    if (LocalGitRadio.IsChecked == true)
                    {
                        string branch = LocalBranchName.Text.Trim();
                        if (string.IsNullOrEmpty(branch)) branch = "master";
                        
                        AddLog("Creating .gitignore...");
                        CreateGitIgnore(_projectPath);

                        AddLog("Initializing local repository...");
                        await Task.Delay(500); // UI Refresh
                        
                        var initBranches = new List<string> { branch };
                        string targetBranch = TargetBranchCombo.Text.Trim();
                        if (!string.IsNullOrEmpty(targetBranch) && targetBranch != branch)
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
                            OverlayGrid.Visibility = Visibility.Collapsed;
                            CancelButton.IsEnabled = true;
                            FinishButton.IsEnabled = true;
                            return;
                        }
                        
                        AddLog("Creating .gitignore...");
                        CreateGitIgnore(_projectPath);

                        AddLog("Initializing & Connecting to Remote...");
                        var initBranches = new List<string> { "master" };
                        string targetBranch = TargetBranchCombo.Text.Trim();
                        if (!string.IsNullOrEmpty(targetBranch) && targetBranch != "master")
                        {
                             initBranches.Add(targetBranch);
                        }
                        
                        await _gitService.InitRepoAsync(initBranches, remote);
                        
                        AddLog("Pulling changes from remote...");
                        try { await _gitService.PullAsync(); } catch { }
                    }
                }
                else
                {
                    AddLog("Verifying Git configuration...");
                    CreateGitIgnore(_projectPath);
                }

                // 2. Create Project Config
                AddLog("Saving project configuration...");
                var config = new ProjectConfig
                {
                    LocalProjectPath = _projectPath,
                    DefaultSourceBranch = SourceBranchCombo.Text,
                    DefaultTargetBranch = TargetBranchCombo.Text,
                    
                    AutoInitGit = true,
                    AutoCommit = true
                };

                if (EnableFtpCheck.IsChecked == true)
                {
                    var selectedProfile = ConnectionProfileComboBox.SelectedItem as ConnectionProfile;
                    if (selectedProfile == null)
                    {
                         ModernMessageBox.Show("Please select a connection profile.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                         OverlayGrid.Visibility = Visibility.Collapsed;
                         CancelButton.IsEnabled = true;
                         FinishButton.IsEnabled = true;
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
                var changes = await _gitService.GetUncommittedChangesAsync();
                if (changes.Count > 0)
                {
                    AddLog($"Committing {changes.Count} changes...");
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
                OverlayGrid.Visibility = Visibility.Collapsed;
                CancelButton.IsEnabled = true;
                FinishButton.IsEnabled = true;
                ModernMessageBox.Show($"Setup failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
                        "bin/", "obj/", ".vs/", ".gitdeploy.config", ".gitdeploy.history", "*.log", "node_modules/", "vendor/"
                    };
                    File.WriteAllLines(ignoreFile, defaults);
                }
            }
            catch { }
        }
    }
}