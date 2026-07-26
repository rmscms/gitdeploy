using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives; 
using System.Windows.Media; 
using System.Windows.Threading;
using GitDeployPro.Controls;
using GitDeployPro.Models;
using GitDeployPro.Pages;
using GitDeployPro.Services;
using GitDeployPro.Services.Update;
using GitDeployPro.Windows;
using Button = System.Windows.Controls.Button;
using Forms = System.Windows.Forms;

namespace GitDeployPro
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private ConfigurationService _configService;
        public bool IsSidebarCollapsed => _isSidebarCollapsed;
        private bool _isSidebarCollapsed;
        private const double DefaultSidebarWidth = 240;
        private readonly BackupTaskMonitor _taskMonitor = BackupTaskMonitor.Instance;
        private readonly DispatcherTimer _nextRunTimer;
        private DateTime? _nextRunUtc;
        private string _nextRunCountdownText = "next --";
        private Forms.NotifyIcon? _trayIcon;
        private bool _allowClose;
        private bool _trayHintShown;
        private bool _minimizeToTrayEnabled = true;
        private string _currentRoute = string.Empty;
        private bool _sidebarCollapsedBeforeDeploy;
        private readonly DispatcherTimer _updateCheckTimer;
        private CancellationTokenSource? _updateDownloadCts;
        private readonly AppUpdateService _updateService = new();
        private PendingUpdateState? _readyPendingUpdate;
        private bool _backgroundUpdateInProgress;

        public bool IsBackgroundUpdateInProgress => _backgroundUpdateInProgress;

        public MainWindow()
        {
            using var startupScope = PerformanceSampler.Instance.BeginScope("navigation", "main-window-startup");
            InitializeComponent();
            _configService = new ConfigurationService();
            SetSidebarCollapsed(false);
            LoadRecentProjects();
            _taskMonitor.PropertyChanged += TaskMonitorOnPropertyChanged;
            BackupScheduleStore.SchedulesChanged += BackupScheduleStoreOnSchedulesChanged;
            _nextRunTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _nextRunTimer.Tick += NextRunTimer_Tick;
            _nextRunTimer.Start();
            _updateCheckTimer = new DispatcherTimer { Interval = UpdateOptions.TimerPollInterval };
            _updateCheckTimer.Tick += UpdateCheckTimer_Tick;
            _updateCheckTimer.Start();
            RefreshNextRunTarget();
            RefreshTrayPreference();
            InitializeTrayIcon();
            Closing += MainWindow_Closing;
            Loaded += MainWindow_Loaded;

            NavigateToPage(new DashboardPage(), "dashboard");
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= MainWindow_Loaded;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            if (AppInstallMigrator.TryMigrateAndRelaunchIfNeeded())
            {
                _allowClose = true;
                System.Windows.Application.Current?.Shutdown();
                return;
            }

            // Ensure shortcut/startup stay aligned when already installed.
            DesktopShortcutService.EnsureShortcut(AppInstallPaths.ExecutablePath);
            var global = _configService.LoadGlobalConfig();
            new AutoStartService().RefreshToInstallPath(global.LaunchOnStartup);

            RestorePendingUpdateFooterIfAny();
            await AppUpdateCoordinator.RunAutomaticCheckAsync(this);
        }

        public void RestorePendingUpdateFooterIfAny()
        {
            var pending = _updateService.GetPendingUpdate();
            if (pending == null)
            {
                return;
            }

            if (!Version.TryParse(AppUpdateService.NormalizeVersionString(pending.Version), out var pendingVer) ||
                pendingVer <= _updateService.GetCurrentVersion())
            {
                return;
            }

            ShowUpdateReadyFooter(pending);
        }

        public void StartBackgroundUpdateDownload(UpdateManifest manifest)
        {
            if (manifest == null || _backgroundUpdateInProgress)
            {
                return;
            }

            _ = DownloadUpdateInBackgroundAsync(manifest);
        }

        public void ShowUpdateReadyFooter(PendingUpdateState pending)
        {
            _readyPendingUpdate = pending;
            _backgroundUpdateInProgress = false;
            UpdateFooterBar.Visibility = Visibility.Visible;
            UpdateFooterTitle.Text = $"Update {pending.Version} ready";
            UpdateFooterDetail.Text = "You can keep working. Restart to install the update into LocalAppData.";
            UpdateFooterProgress.IsIndeterminate = false;
            UpdateFooterProgress.Value = 100;
            UpdateFooterCancelButton.Visibility = Visibility.Collapsed;
            UpdateFooterRestartButton.Visibility = Visibility.Visible;
            UpdateFooterLaterButton.Visibility = Visibility.Visible;
        }

        private async Task DownloadUpdateInBackgroundAsync(UpdateManifest manifest)
        {
            _updateDownloadCts?.Cancel();
            _updateDownloadCts = new CancellationTokenSource();
            var token = _updateDownloadCts.Token;
            _backgroundUpdateInProgress = true;
            _readyPendingUpdate = null;

            UpdateFooterBar.Visibility = Visibility.Visible;
            UpdateFooterTitle.Text = $"Downloading {manifest.Version}...";
            UpdateFooterDetail.Text = "You can keep using the app while the update downloads.";
            UpdateFooterProgress.IsIndeterminate = true;
            UpdateFooterProgress.Value = 0;
            UpdateFooterCancelButton.Visibility = Visibility.Visible;
            UpdateFooterRestartButton.Visibility = Visibility.Collapsed;
            UpdateFooterLaterButton.Visibility = Visibility.Collapsed;

            try
            {
                var progress = new Progress<double>(value =>
                {
                    if (value < 0)
                    {
                        UpdateFooterProgress.IsIndeterminate = true;
                        UpdateFooterDetail.Text = "Downloading...";
                        return;
                    }

                    UpdateFooterProgress.IsIndeterminate = false;
                    UpdateFooterProgress.Value = value;
                    UpdateFooterDetail.Text = $"Downloading... {value:0}%";
                });

                var pending = await _updateService.DownloadOnlyAsync(manifest, progress, token);
                ShowUpdateReadyFooter(pending);
            }
            catch (OperationCanceledException)
            {
                HideUpdateFooter();
            }
            catch (Exception ex)
            {
                _backgroundUpdateInProgress = false;
                UpdateFooterTitle.Text = "Update download failed";
                UpdateFooterDetail.Text = ex.Message;
                UpdateFooterProgress.IsIndeterminate = false;
                UpdateFooterCancelButton.Content = "Dismiss";
                UpdateFooterCancelButton.Visibility = Visibility.Visible;
                UpdateFooterRestartButton.Visibility = Visibility.Collapsed;
                UpdateFooterLaterButton.Visibility = Visibility.Collapsed;
            }
        }

        private void HideUpdateFooter()
        {
            _backgroundUpdateInProgress = false;
            _readyPendingUpdate = null;
            UpdateFooterBar.Visibility = Visibility.Collapsed;
            UpdateFooterCancelButton.Content = "Cancel";
        }

        private void UpdateFooterCancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_backgroundUpdateInProgress && _updateDownloadCts != null && !_updateDownloadCts.IsCancellationRequested)
            {
                _updateDownloadCts.Cancel();
                return;
            }

            HideUpdateFooter();
        }

        private void UpdateFooterLaterButton_Click(object sender, RoutedEventArgs e)
        {
            // Keep pending package on disk; just hide the footer for now.
            UpdateFooterBar.Visibility = Visibility.Collapsed;
        }

        private async void UpdateFooterRestartButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateFooterRestartButton.IsEnabled = false;
            UpdateFooterLaterButton.IsEnabled = false;
            try
            {
                UpdateFooterDetail.Text = "Installing update and restarting...";
                await _updateService.ApplyPendingAndRestartAsync();
                _allowClose = true;
                System.Windows.Application.Current?.Shutdown();
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(
                    $"Could not apply update:\n{ex.Message}",
                    "Update",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    owner: this);
                UpdateFooterRestartButton.IsEnabled = true;
                UpdateFooterLaterButton.IsEnabled = true;
            }
        }

        private async void UpdateCheckTimer_Tick(object? sender, EventArgs e)
        {
            await AppUpdateCoordinator.RunAutomaticCheckAsync(this);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string BackupMenuLabel =>
            _taskMonitor.ActiveCount > 0
                ? $"Backup Plans ({_taskMonitor.ActiveCount})"
                : $"Backup Plans • {_nextRunCountdownText}";

        public string BackupMenuBadgeText =>
            _taskMonitor.ActiveCount > 0
                ? _taskMonitor.ActiveCount.ToString()
                : _nextRunCountdownText;

        public System.Windows.Media.Brush BackupMenuBadgeBackground
        {
            get
            {
                if (_taskMonitor.ActiveCount > 0)
                {
                    return ResolveThemeBrush("Accent.Primary", CreateSolidBrush("#2C5CC5", System.Windows.Media.Brushes.SteelBlue));
                }

                if (string.Equals(_nextRunCountdownText, "pending", StringComparison.OrdinalIgnoreCase))
                {
                    return ResolveThemeBrush("Status.Warning", CreateSolidBrush("#D08B16", System.Windows.Media.Brushes.Goldenrod));
                }

                return ResolveThemeBrush("Status.Info", CreateSolidBrush("#2F6FED", System.Windows.Media.Brushes.SteelBlue));
            }
        }

        public System.Windows.Media.Brush BackupMenuBadgeForeground =>
            ResolveThemeBrush("Text.Inverse", System.Windows.Media.Brushes.White);

        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private static System.Windows.Media.Brush ResolveThemeBrush(string key, System.Windows.Media.Brush fallback)
        {
            return System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush ?? fallback;
        }

        private static System.Windows.Media.Brush CreateSolidBrush(string hexColor, System.Windows.Media.Brush fallback)
        {
            try
            {
                var converter = new System.Windows.Media.BrushConverter();
                var brush = converter.ConvertFromString(hexColor) as System.Windows.Media.Brush;
                return brush ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private void NotifyBackupMenuChanged()
        {
            OnPropertyChanged(nameof(BackupMenuLabel));
            OnPropertyChanged(nameof(BackupMenuBadgeText));
            OnPropertyChanged(nameof(BackupMenuBadgeBackground));
            OnPropertyChanged(nameof(BackupMenuBadgeForeground));
        }

        private void TaskMonitorOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BackupTaskMonitor.ActiveCount))
            {
                Dispatcher.Invoke(() =>
                {
                    NotifyBackupMenuChanged();
                    UpdateCountdownLabel();
                });
            }
        }

        private void BackupScheduleStoreOnSchedulesChanged()
        {
            Dispatcher.Invoke(RefreshNextRunTarget);
        }

        private void NextRunTimer_Tick(object? sender, EventArgs e)
        {
            if (_taskMonitor.ActiveCount == 0 && _nextRunUtc.HasValue && _nextRunUtc <= AppTimeService.UtcNow)
            {
                RefreshNextRunTarget();
            }
            else
            {
                UpdateCountdownLabel();
            }
        }

        private void RefreshNextRunTarget()
        {
            var schedules = BackupScheduleStore.LoadSchedules();
            _nextRunUtc = BackupScheduleTimelineService.FindSoonestUpcomingRunUtc(schedules, AppTimeService.UtcNow);
            UpdateCountdownLabel();
        }

        private void UpdateCountdownLabel()
        {
            var text = BackupScheduleTimelineService.BuildCountdownText(_taskMonitor.ActiveCount, _nextRunUtc, AppTimeService.UtcNow);

            if (text != _nextRunCountdownText)
            {
                _nextRunCountdownText = text;
                NotifyBackupMenuChanged();
            }
        }

        private void LoadRecentProjects()
        {
            var config = _configService.LoadGlobalConfig();
            
            // Set current project info in the button
            if (!string.IsNullOrEmpty(config.LastProjectPath))
            {
                string name = System.IO.Path.GetFileName(config.LastProjectPath);
                ProjectNameText.Text = name;
                
                ProjectInitialText.Text = GetProjectInitial(name);
                ProjectAvatarBorder.Background = GetProjectColor(name);

                GitService.SetWorkingDirectory(config.LastProjectPath);
                HistoryService.SetWorkingDirectory(config.LastProjectPath);
            }
            else
            {
                ProjectNameText.Text = "Select Project";
                ProjectInitialText.Text = "?";
                ProjectAvatarBorder.Background = System.Windows.Media.Brushes.Gray;
            }

            // Populate Recent Projects List
            RecentProjectsList.ItemsSource = null;
            if (config.RecentProjects != null && config.RecentProjects.Any())
            {
                var recentItems = config.RecentProjects
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
                    .OrderByDescending(entry => entry.LastOpenedUtc)
                    .Select(entry =>
                    {
                        string name = System.IO.Path.GetFileName(entry.Path);
                        return new
                        {
                            Name = string.IsNullOrWhiteSpace(name) ? entry.Path : name,
                            Path = entry.Path,
                            Initial = GetProjectInitial(name),
                            ColorBrush = GetProjectColor(name)
                        };
                    })
                    .ToList();
                
                RecentProjectsList.ItemsSource = recentItems;
            }
        }

        private string GetProjectInitial(string name)
        {
            if (string.IsNullOrEmpty(name)) return "?";
            return name.Substring(0, 1).ToUpper();
        }

        private SolidColorBrush GetProjectColor(string name)
        {
            if (string.IsNullOrEmpty(name)) return System.Windows.Media.Brushes.Gray;

            int hash = name.GetHashCode();
            
            // Colors (Hex strings)
            var colors = new[] 
            {
                "#3574F0", // Blue
                "#E05555", // Red
                "#579A57", // Green
                "#E59500", // Orange
                "#9B59B6", // Purple
                "#00ACC1", // Cyan
                "#F06292"  // Pink
            };

            int index = Math.Abs(hash) % colors.Length;
            string colorCode = colors[index];
            
            try
            {
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorCode);
                return new SolidColorBrush(color);
            }
            catch
            {
                return System.Windows.Media.Brushes.Gray;
            }
        }

        private void ProjectSelectorBtn_Click(object sender, RoutedEventArgs e)
        {
            ProjectMenuPopup.IsOpen = !ProjectMenuPopup.IsOpen;
        }

        private void SidebarToggleButton_Click(object sender, RoutedEventArgs e)
        {
            SetSidebarCollapsed(!_isSidebarCollapsed);
            LogSidebarAction("ToggleButton");
        }

        private void SidebarToggleButton_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            SidebarToggleButton.Background = System.Windows.Application.Current?.TryFindResource("State.Hover") as System.Windows.Media.Brush
                ?? new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#35373B"));
        }

        private void SidebarToggleButton_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            SidebarToggleButton.Background = System.Windows.Media.Brushes.Transparent;
        }

        public void SetSidebarCollapsed(bool collapsed)
        {
            _isSidebarCollapsed = collapsed;
            SidebarToggleIcon.Text = collapsed ? "☰" : "⮜";
            SidebarToggleButton.ToolTip = collapsed ? "Show Sidebar" : "Hide Sidebar";
            SidebarColumn.Width = collapsed ? new GridLength(0) : new GridLength(DefaultSidebarWidth);
            SidebarPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            Grid.SetColumn(SidebarPanel, 0);
            Grid.SetColumnSpan(SidebarPanel, 1);
            SidebarPanel.Width = double.NaN;
            SidebarPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            System.Windows.Controls.Panel.SetZIndex(SidebarPanel, 0);
        }

        private void ApplyDeploySidebarPolicy(string previousRoute, string route)
        {
            var enteringDeploy = string.Equals(route, "deploy", StringComparison.OrdinalIgnoreCase);
            var leavingDeploy = string.Equals(previousRoute, "deploy", StringComparison.OrdinalIgnoreCase) && !enteringDeploy;

            if (enteringDeploy && !string.Equals(previousRoute, "deploy", StringComparison.OrdinalIgnoreCase))
            {
                _sidebarCollapsedBeforeDeploy = _isSidebarCollapsed;
                SetSidebarCollapsed(true);
                return;
            }

            if (leavingDeploy)
            {
                SetSidebarCollapsed(_sidebarCollapsedBeforeDeploy);
            }
        }

        private void LogSidebarAction(string source)
        {
            System.Diagnostics.Debug.WriteLine($"[Sidebar] action={source}, collapsed={_isSidebarCollapsed}, time={AppTimeService.LocalNow:HH:mm:ss}");
        }

        private void RecentProject_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag != null)
            {
                string path = btn.Tag.ToString();
                SwitchProject(path);
                ProjectMenuPopup.IsOpen = false;
            }
        }

        private void OpenProject_Click(object sender, RoutedEventArgs e)
        {
            ProjectMenuPopup.IsOpen = false;
            
            try 
            {
                using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dialog.Description = "Select Project Folder";
                    dialog.ShowNewFolderButton = true;
                    
                    System.Windows.Forms.DialogResult result = dialog.ShowDialog();
                    
                    if (result == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                    {
                        SwitchProject(dialog.SelectedPath);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Error opening project: {ex.Message}");
            }
        }

        private void OpenExplorer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var config = _configService.LoadGlobalConfig();
                if (!string.IsNullOrEmpty(config.LastProjectPath) && System.IO.Directory.Exists(config.LastProjectPath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = config.LastProjectPath,
                        UseShellExecute = true,
                        Verb = "open"
                    });
                }
                else
                {
                    System.Windows.MessageBox.Show("No project is currently open.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Error opening Explorer: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SwitchProject(string path)
        {
            using var scope = PerformanceSampler.Instance.BeginScope("navigation", "switch-project", path);
            _configService.AddRecentProject(path);
            LoadRecentProjects(); // Refresh name and list

            GitService.SetWorkingDirectory(path);
            HistoryService.SetWorkingDirectory(path);

            CheckAndShowSetupWizard(path);

            NavigateToDashboard();
        }

        private void CheckAndShowSetupWizard(string path)
        {
            // Ensure we're on UI thread and window is loaded
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    // Check if configured
                    var gitService = new GitService();
                    var config = _configService.LoadProjectConfig(path);
                    
                    bool isGitRepo = gitService.IsGitRepository();
                    
                    // If not a git repo OR (is a repo but has no config file), show wizard
                    // BUT: If it is a git repo, we should be careful. Maybe user just opened an existing repo.
                    // Let's only force wizard if:
                    // 1. Not a git repo at all
                    // 2. Is a git repo but we have never seen it (no config) AND it has no remotes (fresh init)?
                    // Actually, the requirement is "Setup Wizard" for "New Projects".
                    // If a folder has .git, it's technically initialized.
                    // If .gitdeploy.config is missing, it just means we haven't configured FTP/Deployment settings.
                    // So, if IsGitRepo is true, we should probably SKIP the wizard or make it optional?
                    // User said: "I opened a project with .git folder but wizard popped up. It shouldn't."

                    bool shouldShowWizard = !isGitRepo;

                    // If it IS a repo, but no config, maybe just let them use it as a Git Client?
                    // Only show wizard if it's NOT a repo.
                    // If they want to configure FTP later, they can use Settings or "Run Setup" button.
                    
                    if (shouldShowWizard)
                    {
                        var wizard = new ProjectSetupWizard(path)
                        {
                            Owner = this
                        };
                        
                        // Ensure owner is visible
                        if (!this.IsVisible) this.Show();

                        wizard.ShowDialog();
                        
                        if (wizard.SetupCompleted)
                        {
                            // Reload everything and refresh Dashboard
                            LoadRecentProjects();
                            NavigateToDashboard();
                        }
                    }
                }
                catch { }
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        public void NavigateToDashboard()
        {
            using var scope = PerformanceSampler.Instance.BeginScope("navigation", "navigate", "dashboard");
            LoadRecentProjects();
            NavigateToPage(new DashboardPage(), "dashboard");
        }

        private void Dashboard_Click(object sender, RoutedEventArgs e) => NavigateToDashboard();
        private void Deploy_Click(object sender, RoutedEventArgs e) => NavigateToPage(new DeployPage(), "deploy");
        private void DirectUpload_Click(object sender, RoutedEventArgs e) => NavigateToPage(new DirectUploadPage(), "direct-upload");
        private void Database_Click(object sender, RoutedEventArgs e) => NavigateToPage(new DatabasePage(), "database");
        private void Terminal_Click(object sender, RoutedEventArgs e) => NavigateToPage(new TerminalPage(), "terminal");
        private void BackupScheduler_Click(object sender, RoutedEventArgs e) => NavigateToPage(new BackupSchedulerPage(), "backup-scheduler");
        private void Git_Click(object sender, RoutedEventArgs e) => NavigateToPage(new GitPage(), "git");
        private void History_Click(object sender, RoutedEventArgs e) => NavigateToPage(new HistoryPage(), "history");
        private void Settings_Click(object sender, RoutedEventArgs e) => NavigateToPage(new SettingsPage(), "settings");

        private void NavigateToPage(Page page, string route)
        {
            using var scope = PerformanceSampler.Instance.BeginScope("navigation", "navigate", route);
            var previousRoute = _currentRoute;
            _currentRoute = route ?? string.Empty;
            ContentFrame.Navigate(page);
            ApplyDeploySidebarPolicy(previousRoute, _currentRoute);
        }
        private void About_Click(object sender, RoutedEventArgs e) => NavigateToPage(new AboutPage(), "about");

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }
        private void Maximize_Click(object sender, RoutedEventArgs e) => this.WindowState = (this.WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
        private void Close_Click(object sender, RoutedEventArgs e)
        {
            RefreshTrayPreference();
            if (_minimizeToTrayEnabled)
            {
                MinimizeToTray();
                return;
            }

            _allowClose = true;
            Close();
        }

        private void InitializeTrayIcon()
        {
            try
            {
                var trayMenu = new Forms.ContextMenuStrip();
                trayMenu.Items.Add("Open", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
                trayMenu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitFromTray));

                _trayIcon = new Forms.NotifyIcon
                {
                    Text = "GitDeploy Pro",
                    Visible = true,
                    ContextMenuStrip = trayMenu,
                    Icon = ResolveTrayIcon()
                };

                _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
                UpdateTrayIconVisibility();
            }
            catch
            {
                _trayIcon = null;
            }
        }

        private static System.Drawing.Icon ResolveTrayIcon()
        {
            // Single-file publish leaves Assembly.Location empty; prefer ProcessPath.
            foreach (var executablePath in new[]
                     {
                         Environment.ProcessPath,
                         Assembly.GetExecutingAssembly().Location
                     })
            {
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    continue;
                }

                try
                {
                    var extracted = System.Drawing.Icon.ExtractAssociatedIcon(executablePath);
                    if (extracted != null)
                    {
                        return extracted;
                    }
                }
                catch
                {
                    // Try next candidate.
                }
            }

            try
            {
                var baseIcon = Path.Combine(AppContext.BaseDirectory, "icon.ico");
                if (File.Exists(baseIcon))
                {
                    return new System.Drawing.Icon(baseIcon);
                }
            }
            catch
            {
                // Try pack resource next.
            }

            try
            {
                var packIcon = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/icon.ico"));
                if (packIcon?.Stream != null)
                {
                    using (packIcon.Stream)
                    {
                        return new System.Drawing.Icon(packIcon.Stream);
                    }
                }
            }
            catch
            {
                // Fall through to default icon.
            }

            return System.Drawing.SystemIcons.Application;
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (_allowClose)
            {
                return;
            }

            RefreshTrayPreference();
            if (_minimizeToTrayEnabled)
            {
                e.Cancel = true;
                MinimizeToTray();
            }
        }

        private void MinimizeToTray()
        {
            try
            {
                ShowInTaskbar = false;
                Hide();

                if (_trayIcon != null && !_trayHintShown)
                {
                    _trayIcon.BalloonTipTitle = "GitDeploy Pro";
                    _trayIcon.BalloonTipText = "App is running in tray. Right-click tray icon for Open/Exit.";
                    _trayIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
                    _trayIcon.ShowBalloonTip(1800);
                    _trayHintShown = true;
                }
            }
            catch
            {
                // Ignore tray transition failures.
            }
        }

        private void RestoreFromTray()
        {
            Show();
            ShowInTaskbar = true;
            WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }

        private void ExitFromTray()
        {
            _allowClose = true;
            Close();
        }

        private void RefreshTrayPreference()
        {
            try
            {
                var config = _configService.LoadGlobalConfig();
                _minimizeToTrayEnabled = config.MinimizeToTray;
            }
            catch
            {
                _minimizeToTrayEnabled = true;
            }

            UpdateTrayIconVisibility();
        }

        private void UpdateTrayIconVisibility()
        {
            if (_trayIcon == null)
            {
                return;
            }

            _trayIcon.Visible = _minimizeToTrayEnabled;
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _taskMonitor.PropertyChanged -= TaskMonitorOnPropertyChanged;
            BackupScheduleStore.SchedulesChanged -= BackupScheduleStoreOnSchedulesChanged;
            _nextRunTimer.Stop();
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        }
    }
}
