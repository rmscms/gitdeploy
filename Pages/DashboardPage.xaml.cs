using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using GitDeployPro.Controls;
using GitDeployPro.Models;
using GitDeployPro.Services;
using System.Windows.Threading;

namespace GitDeployPro.Pages
{
    public partial class DashboardPage : Page
    {
        private readonly GitService _gitService;
        private readonly HistoryService _historyService;
        private readonly ConfigurationService _configService;
        private readonly DispatcherTimer _refreshTimer;
        private readonly ObservableCollection<BackupHistoryEntry> _recentBackupHistory = new();
        private bool _isRefreshing;

        public ObservableCollection<BackupHistoryEntry> RecentBackupHistory => _recentBackupHistory;
        public ObservableCollection<BackupTaskStatus> RunningBackupTasks => BackupTaskMonitor.Instance.ActiveTasks;

        public DashboardPage()
        {
            InitializeComponent();
            DataContext = this;
            _gitService = new GitService();
            _historyService = new HistoryService();
            _configService = new ConfigurationService();
            
            // Initialize timer here to avoid CS8618
            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(1)
            };
            
            LoadRecentBackupHistory();
            UpdateRecentActivityState();
            SetupAutoRefresh();
            this.Loaded += DashboardPage_Loaded;
            this.Unloaded += DashboardPage_Unloaded;

            BackupHistoryStore.HistoryChanged += BackupHistoryStore_HistoryChanged;
            BackupTaskMonitor.Instance.ActiveTasks.CollectionChanged += ActiveTasks_CollectionChanged;
        }

        private void DashboardPage_Loaded(object sender, RoutedEventArgs e)
        {
            LoadDashboardData();
        }

        private void DashboardPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.Stop();
            BackupHistoryStore.HistoryChanged -= BackupHistoryStore_HistoryChanged;
            BackupTaskMonitor.Instance.ActiveTasks.CollectionChanged -= ActiveTasks_CollectionChanged;
        }

        private async void LoadDashboardData()
        {
            if (_isRefreshing) return;
            _isRefreshing = true;

            try
            {
                LoadRecentBackupHistory();

                // Reset UI placeholders
                ChangedFilesCount.Text = "-";
                CommitsCount.Text = "-";
                LastDeployText.Text = "-";

                // 1. Project Info
                string currentPath = "Please select or setup a project";
                var globalConfig = _configService.LoadGlobalConfig();
                var hasSelectedProject =
                    !string.IsNullOrWhiteSpace(globalConfig.LastProjectPath) &&
                    Directory.Exists(globalConfig.LastProjectPath);

                if (hasSelectedProject)
                {
                    currentPath = globalConfig.LastProjectPath!;
                    GitService.SetWorkingDirectory(currentPath);
                }

                ProjectPathText.Text = $"Path: {currentPath}";
                var projectConfig = hasSelectedProject ? _configService.LoadProjectConfig(currentPath) : null;
                var activeConnection = ResolveActiveConnection(projectConfig);

                var isGitRepository = hasSelectedProject && _gitService.IsGitRepository();
                var gitRemoteUrl = projectConfig?.GitRemoteUrl ?? string.Empty;
                if (isGitRepository)
                {
                    var detectedRemote = await _gitService.GetRemoteUrlAsync();
                    if (!string.IsNullOrWhiteSpace(detectedRemote))
                    {
                        gitRemoteUrl = detectedRemote;
                    }
                }

                UpdateSetupStatus(hasSelectedProject, isGitRepository, gitRemoteUrl, projectConfig, activeConnection);

                if (!isGitRepository)
                {
                    CurrentBranchText.Text = "Not a Git Repository";
                    UpdatePushStatusBadge(new BranchStatusInfo());
                    
                    ChangedFilesCount.Text = "N/A";
                    CommitsCount.Text = "N/A";
                    LastDeployText.Text = hasSelectedProject ? "N/A" : "No Project";
                    
                    return;
                }

                // 2. Git Stats
                var branch = await _gitService.GetCurrentBranchAsync();
                CurrentBranchText.Text = $"Current Branch: {branch}";

                var commits = await _gitService.GetTotalCommitsAsync();
                CommitsCount.Text = commits.ToString();

                var changesCount = await _gitService.GetUncommittedCountAsync();
                ChangedFilesCount.Text = changesCount.ToString();

                var branchStatus = await _gitService.GetBranchStatusAsync();
                UpdatePushStatusBadge(branchStatus);

                // 3. History Stats
                var lastDeploy = _historyService.GetLastDeploy();
                if (lastDeploy != null)
                {
                    LastDeployText.Text = $"{lastDeploy.Date:MM/dd HH:mm}";
                }
                else
                {
                    LastDeployText.Text = "Never";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        private void RunSetupWizard_Click(object sender, RoutedEventArgs e)
        {
             var globalConfig = _configService.LoadGlobalConfig();
             string path = globalConfig.LastProjectPath;
             
             if (string.IsNullOrEmpty(path))
             {
                 path = Directory.GetCurrentDirectory();
             }

             if (string.IsNullOrEmpty(path)) return;

            var wizard = new GitDeployPro.Windows.ProjectSetupWizard(path)
            {
                Owner = System.Windows.Application.Current.MainWindow
            };
            
            if (wizard.ShowDialog() == true)
            {
                LoadDashboardData();
                ModernMessageBox.Show("Project re-configured successfully!", "Setup Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }


        private void QuickDeploy_Click(object sender, RoutedEventArgs e)
        {
            NavigationService?.Navigate(new DeployPage());
        }

        private void RefreshGit_Click(object sender, RoutedEventArgs e)
        {
            LoadDashboardData();
            ModernMessageBox.Show("Dashboard data refreshed!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            NavigationService?.Navigate(new SettingsPage());
        }

        private void OpenLocalTerminal_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var globalConfig = _configService.LoadGlobalConfig();
                var projectPath = globalConfig.LastProjectPath;
                if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
                {
                    ModernMessageBox.Show(
                        "Please select a valid project first.",
                        "Local Terminal",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                var terminalWindow = new GitDeployPro.Windows.TerminalWindow(projectPath)
                {
                    Title = "Local Terminal"
                };
                WindowOwnerService.ShowOwned(terminalWindow, this);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(
                    $"Failed to open local terminal: {ex.Message}",
                    "Local Terminal",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void UpdatePushStatusBadge(BranchStatusInfo status)
        {
            if (PushStatusBadge == null || PushStatusText == null) return;

            if (status.HasRemote && status.AheadCount > 0)
            {
                PushStatusBadge.Visibility = Visibility.Visible;
                PushStatusText.Text = $"Push pending: {status.AheadCount} commit(s)";
            }
            else
            {
                PushStatusBadge.Visibility = Visibility.Collapsed;
            }
        }

        private void SetupAutoRefresh()
        {
            // _refreshTimer is already initialized in constructor
            _refreshTimer.Tick += (s, e) => LoadDashboardData();
            _refreshTimer.Start();
        }

        private void BackupHistoryStore_HistoryChanged()
        {
            Dispatcher.Invoke(() =>
            {
                LoadRecentBackupHistory();
            });
        }

        private void ActiveTasks_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            Dispatcher.Invoke(UpdateRecentActivityState);
        }

        private void LoadRecentBackupHistory()
        {
            var recentItems = BackupHistoryStore.LoadHistory().Take(5).ToList();
            _recentBackupHistory.Clear();
            foreach (var item in recentItems)
            {
                _recentBackupHistory.Add(item);
            }

            UpdateRecentActivityState();
        }

        private ConnectionProfile? ResolveActiveConnection(ProjectConfig? projectConfig)
        {
            if (projectConfig == null || string.IsNullOrWhiteSpace(projectConfig.ConnectionProfileId))
            {
                return null;
            }

            var profiles = _configService.LoadConnections();
            return profiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, projectConfig.ConnectionProfileId, StringComparison.OrdinalIgnoreCase));
        }

        private void UpdateSetupStatus(
            bool hasSelectedProject,
            bool isGitRepository,
            string gitRemoteUrl,
            ProjectConfig? projectConfig,
            ConnectionProfile? activeConnection)
        {
            var hasGitRemote = !string.IsNullOrWhiteSpace(gitRemoteUrl);
            var gitReady = hasSelectedProject && isGitRepository && hasGitRemote;
            var gitText = gitReady ? "Git: Configured" : "Git: Not Configured";
            var gitDetails = !hasSelectedProject
                ? "No active project selected."
                : !isGitRepository
                    ? "Repository not initialized (.git missing)."
                    : hasGitRemote
                        ? $"Remote: {ShortenRemote(gitRemoteUrl)}"
                        : "Remote origin is not configured.";

            var hasFtpTarget =
                !string.IsNullOrWhiteSpace(activeConnection?.Host) ||
                !string.IsNullOrWhiteSpace(projectConfig?.FtpHost);
            var ftpReady = hasSelectedProject && hasFtpTarget;
            var ftpText = ftpReady ? "FTP/SFTP: Configured" : "FTP/SFTP: Not Configured";
            var ftpDetails = !hasSelectedProject
                ? "No active project selected."
                : !hasFtpTarget
                    ? "No connection profile or legacy FTP host configured."
                    : activeConnection != null
                        ? $"Profile: {activeConnection.Name} ({(activeConnection.UseSSH ? "SFTP" : "FTP")})"
                        : $"Legacy host: {projectConfig?.FtpHost}";

            ApplyStatusBadge(ProjectGitStatusBadge, ProjectGitStatusText, gitReady, gitText, gitDetails);
            ApplyStatusBadge(ProjectFtpStatusBadge, ProjectFtpStatusText, ftpReady, ftpText, ftpDetails);
        }

        private void ApplyStatusBadge(Border badge, TextBlock label, bool isSuccess, string badgeText, string details)
        {
            label.Text = badgeText;
            if (TryFindResource(isSuccess ? "App.StatusBadge.Success" : "App.StatusBadge.Warning") is Style badgeStyle)
            {
                badge.Style = badgeStyle;
            }

            if (ReferenceEquals(label, ProjectGitStatusText))
            {
                ProjectGitStatusDetailText.Text = details;
            }
            else
            {
                ProjectFtpStatusDetailText.Text = details;
            }
        }

        private string ShortenRemote(string remote)
        {
            if (string.IsNullOrWhiteSpace(remote))
            {
                return "-";
            }

            var normalized = remote.Trim();
            if (normalized.Length <= 48)
            {
                return normalized;
            }

            return $"{normalized[..22]}...{normalized[^20..]}";
        }

        private void UpdateRecentActivityState()
        {
            if (RunningBackupsSection == null ||
                BackupHistorySection == null ||
                RecentActivityEmptyState == null)
            {
                return;
            }

            var hasRunning = RunningBackupTasks.Count > 0;
            var hasHistory = RecentBackupHistory.Count > 0;

            RunningBackupsSection.Visibility = hasRunning ? Visibility.Visible : Visibility.Collapsed;
            BackupHistorySection.Visibility = hasHistory ? Visibility.Visible : Visibility.Collapsed;
            RecentActivityEmptyState.Visibility = (!hasRunning && !hasHistory) ? Visibility.Visible : Visibility.Collapsed;

            if (RunningBackupsTitleText != null)
            {
                RunningBackupsTitleText.Text = hasRunning
                    ? $"Live Backups ({RunningBackupTasks.Count})"
                    : "Live Backups";
            }
        }
    }
}
