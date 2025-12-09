using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GitDeployPro.Controls;
using GitDeployPro.Models;
using GitDeployPro.Services;
using GitDeployPro.Windows;
using Forms = System.Windows.Forms;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;

namespace GitDeployPro.Pages
{
    public partial class BackupSchedulerPage : Page, INotifyPropertyChanged
    {
        private readonly ConfigurationService _configService = new();
        private readonly ObservableCollection<WeekdaySelection> _weekdaySelections = new();
        private BackupSchedule? _selectedSchedule;
        private bool _suppressWeekdayPropagation;
        private string _databaseStatus = "Select a schedule to load databases.";
        private bool _isDbLoading;
        private bool _isRunningBackup;
        private MediaBrush _databaseStatusBrush = StatusInfoBrush;
        private readonly DatabaseBackupService _backupService = new();
        private readonly BackupHealthService _healthService = new();
        private readonly NotificationService _notificationService = new();
        private readonly BackupTaskMonitor _taskMonitor = BackupTaskMonitor.Instance;
        private static readonly MediaBrush StatusInfoBrush = MediaBrushes.LightGray;
        private static readonly MediaBrush StatusSuccessBrush = MediaBrushes.LightGreen;
        private static readonly MediaBrush StatusErrorBrush = MediaBrushes.OrangeRed;
        private int _totalTables;
        private int _processedTables;
        private string _currentProgressText = "Idle";
        private DateTime _lastProgressUiUpdateUtc = DateTime.MinValue;
        private CancellationTokenSource? _backupCts;
        private PauseTokenSource? _pauseSource;
        private bool _isBackupPaused;
        private int _existingBackupCount;
        private BackupTaskHandle? _currentTaskHandle;
        private bool _monitorEventsHooked;

        public ObservableCollection<BackupSchedule> Schedules { get; } = new();
        public ObservableCollection<ConnectionProfile> ConnectionProfiles { get; } = new();
        public ObservableCollection<WeekdaySelection> WeekdaySelections => _weekdaySelections;
        public ObservableCollection<string> DatabaseNames { get; } = new();
        public ObservableCollection<BackupRunLogEntry> RunLog { get; } = new();
        public ObservableCollection<BackupHistoryEntry> BackupHistory { get; } = BackupHistoryStore.LoadHistory();
        public List<BackupModeOption> BackupModes { get; } = new()
        {
            new BackupModeOption(BackupMode.Standard, "Standard (safe)"),
            new BackupModeOption(BackupMode.Fast, "Fast (bulk)"),
            new BackupModeOption(BackupMode.ExternalTool, "External (mysqldump)")
        };
        public List<CompressionFormatOption> CompressionFormatOptions { get; } = new()
        {
            new CompressionFormatOption(BackupCompressionFormat.Zip, "ZIP (.zip)"),
            new CompressionFormatOption(BackupCompressionFormat.TarGz, "Tarball (.tar.gz)")
        };
        public List<BackupScheduleFrequency> FrequencyOptions { get; } = Enum.GetValues(typeof(BackupScheduleFrequency)).Cast<BackupScheduleFrequency>().ToList();
        public string DatabaseStatus
        {
            get => _databaseStatus;
            private set => SetProperty(ref _databaseStatus, value);
        }

        private void ReloadHistory()
        {
            var items = BackupHistoryStore.LoadHistory();
            BackupHistory.Clear();
            foreach (var entry in items)
            {
                BackupHistory.Add(entry);
            }
        }

        private void EnsureMonitorSubscriptions()
        {
            if (_monitorEventsHooked) return;
            BackupHistoryStore.HistoryChanged += BackupHistoryStore_HistoryChanged;
            _taskMonitor.PropertyChanged += TaskMonitor_PropertyChanged;
            _taskMonitor.TaskLogCreated += TaskMonitor_TaskLogCreated;
            _monitorEventsHooked = true;
        }

        private void ReleaseMonitorSubscriptions()
        {
            if (!_monitorEventsHooked) return;
            BackupHistoryStore.HistoryChanged -= BackupHistoryStore_HistoryChanged;
            _taskMonitor.PropertyChanged -= TaskMonitor_PropertyChanged;
            _taskMonitor.TaskLogCreated -= TaskMonitor_TaskLogCreated;
            _monitorEventsHooked = false;
        }

        public MediaBrush DatabaseStatusBrush
        {
            get => _databaseStatusBrush;
            private set => SetProperty(ref _databaseStatusBrush, value);
        }

        public bool IsDbLoading
        {
            get => _isDbLoading;
            private set => SetProperty(ref _isDbLoading, value);
        }

        public bool IsBackupRunning
        {
            get => _isRunningBackup;
            private set
            {
                if (SetProperty(ref _isRunningBackup, value))
                {
                    OnPropertyChanged(nameof(ShowBackupProgress));
                    if (!value && _isBackupPaused)
                    {
                        IsBackupPaused = false;
                    }
                }
            }
        }

        public int TotalTables
        {
            get => _totalTables;
            private set
            {
                if (SetProperty(ref _totalTables, value))
                {
                    OnPropertyChanged(nameof(ProgressBarMaximum));
                    OnPropertyChanged(nameof(ProgressSummary));
                    OnPropertyChanged(nameof(ShowBackupProgress));
                }
            }
        }

        public int ProcessedTables
        {
            get => _processedTables;
            private set
            {
                if (SetProperty(ref _processedTables, value))
                {
                    OnPropertyChanged(nameof(ProgressSummary));
                    OnPropertyChanged(nameof(ShowBackupProgress));
                }
            }
        }

        public int ProgressBarMaximum => Math.Max(1, TotalTables);

        public string ProgressSummary => TotalTables > 0
            ? $"{ProcessedTables}/{TotalTables} tables"
            : "Calculating tables …";

        public bool ShowBackupProgress => IsBackupRunning || ProcessedTables > 0 || TotalTables > 0;

        public string CurrentProgressText
        {
            get => _currentProgressText;
            private set => SetProperty(ref _currentProgressText, value);
        }

        public string ActiveTasksLabel => $"Active tasks ({_taskMonitor.ActiveCount})";

        public int ExistingBackupCount
        {
            get => _existingBackupCount;
            private set
            {
                if (SetProperty(ref _existingBackupCount, value))
                {
                    OnPropertyChanged(nameof(BackupCountSummary));
                }
            }
        }

        public string BackupCountSummary => SelectedSchedule == null
            ? "No schedule selected."
            : $"Stored backups: {ExistingBackupCount}/{Math.Max(1, SelectedSchedule.RetentionCount)}";
        public bool IsBackupPaused
        {
            get => _isBackupPaused;
            private set
            {
                if (SetProperty(ref _isBackupPaused, value))
                {
                    OnPropertyChanged(nameof(PauseButtonLabel));
                }
            }
        }

        public string PauseButtonLabel => IsBackupPaused ? "Resume" : "Pause";

        public BackupSchedule? SelectedSchedule
        {
            get => _selectedSchedule;
            set
            {
                if (_selectedSchedule == value) return;
                if (_selectedSchedule != null)
                {
                    _selectedSchedule.PropertyChanged -= SelectedSchedule_PropertyChanged;
                }
                _selectedSchedule = value;
                if (_selectedSchedule != null)
                {
                    _selectedSchedule.PropertyChanged += SelectedSchedule_PropertyChanged;
                }
                UpdateExistingBackupCount();
                UpdateWeekdaySelections();
                UpdateFrequencyPanels();
                EnsureScheduleUsesAvailableConnection(_selectedSchedule);
                OnPropertyChanged(nameof(SelectedSchedule));
                OnPropertyChanged(nameof(IsEditorEnabled));
                OnPropertyChanged(nameof(SelectedRunTime));
                OnPropertyChanged(nameof(SelectedScheduleSummary));
                OnPropertyChanged(nameof(BackupCountSummary));

                if (_selectedSchedule == null)
                {
                    DatabaseNames.Clear();
                    UpdateDatabaseStatus(ConnectionProfiles.Count == 0
                        ? "No eligible database connections are configured."
                        : "Select a schedule to load databases.", isInfo: true);
                }
                else
                {
                    _ = RefreshDatabaseListAsync();
                }
            }
        }

        public bool IsEditorEnabled => SelectedSchedule != null;

        public DateTime? SelectedRunTime
        {
            get => SelectedSchedule == null ? (DateTime?)null : DateTime.Today.Add(SelectedSchedule.LocalRunTime);
            set
            {
                if (SelectedSchedule == null || value == null) return;
                SelectedSchedule.LocalRunTime = value.Value.TimeOfDay;
                OnPropertyChanged(nameof(SelectedRunTime));
                OnPropertyChanged(nameof(SelectedScheduleSummary));
            }
        }

        public string SelectedScheduleSummary
        {
            get
            {
                if (SelectedSchedule == null) return "Select or create a schedule to edit.";
                return $"{SelectedSchedule.Frequency} @ {SelectedSchedule.LocalRunTime:hh\\:mm} • retains {SelectedSchedule.RetentionCount} copies";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public BackupSchedulerPage()
        {
            InitializeComponent();
            DataContext = this;
            InitializeWeekdaySelections();
            Loaded += BackupSchedulerPage_Loaded;
            Unloaded += BackupSchedulerPage_Unloaded;
        }

        private void BackupHistoryStore_HistoryChanged()
        {
            Dispatcher.Invoke(ReloadHistory);
        }

        private void TaskMonitor_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BackupTaskMonitor.ActiveCount))
            {
                Dispatcher.Invoke(() => OnPropertyChanged(nameof(ActiveTasksLabel)));
            }
        }

        private void TaskMonitor_TaskLogCreated(string message, bool isError)
        {
            Dispatcher.Invoke(() => AddRunLog(message, isError));
        }

        private void BackupSchedulerPage_Unloaded(object sender, RoutedEventArgs e)
        {
            ReleaseMonitorSubscriptions();
        }

        private void BackupSchedulerPage_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureMonitorSubscriptions();
            LoadConnectionProfiles();
            LoadSchedules();
            ReloadHistory();
            UpdateDatabaseStatus("Select a schedule to load databases.", isInfo: true);
        }

        private void LoadConnectionProfiles()
        {
            ConnectionProfiles.Clear();
            var filtered = _configService
                .LoadConnections()
                .Where(p => p.DbType != DatabaseType.None)
                .ToList();

            foreach (var profile in filtered)
            {
                ConnectionProfiles.Add(profile);
            }

            if (SelectedSchedule != null)
            {
                EnsureScheduleUsesAvailableConnection(SelectedSchedule);
                _ = RefreshDatabaseListAsync();
            }
            else
            {
                DatabaseNames.Clear();
                UpdateDatabaseStatus(ConnectionProfiles.Count == 0
                    ? "No database connections configured yet."
                    : "Select a schedule to start configuring backups.", isInfo: true);
            }
        }

        private void LoadSchedules()
        {
            Schedules.Clear();
            foreach (var schedule in BackupScheduleStore.LoadSchedules())
            {
                EnsureScheduleUsesAvailableConnection(schedule);
                RefreshNextRunEstimate(schedule);
                Schedules.Add(schedule);
            }

            if (Schedules.Count > 0)
            {
                SelectedSchedule = Schedules[0];
            }
            else
            {
                SelectedSchedule = null;
            }
        }

        private void InitializeWeekdaySelections()
        {
            _weekdaySelections.Clear();
            foreach (DayOfWeek day in Enum.GetValues(typeof(DayOfWeek)))
            {
                var option = new WeekdaySelection(day, day.ToString().Substring(0, 3).ToUpperInvariant());
                option.PropertyChanged += WeekdaySelection_PropertyChanged;
                _weekdaySelections.Add(option);
            }
        }

        private void UpdateWeekdaySelections()
        {
            _suppressWeekdayPropagation = true;
            try
            {
                foreach (var option in _weekdaySelections)
                {
                    option.IsSelected = SelectedSchedule?.DaysOfWeek?.Contains(option.Day) ?? false;
                }
            }
            finally
            {
                _suppressWeekdayPropagation = false;
            }
        }

        private void WeekdaySelection_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_suppressWeekdayPropagation || SelectedSchedule == null) return;
            if (e.PropertyName != nameof(WeekdaySelection.IsSelected)) return;

            var selectedDays = _weekdaySelections
                .Where(opt => opt.IsSelected)
                .Select(opt => opt.Day)
                .ToList();
            SelectedSchedule.DaysOfWeek = selectedDays;
        }

        private void SelectedSchedule_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BackupSchedule.Frequency))
            {
                UpdateFrequencyPanels();
            }

            if (e.PropertyName == nameof(BackupSchedule.LocalRunTime))
            {
                OnPropertyChanged(nameof(SelectedRunTime));
            }

            if (e.PropertyName == nameof(BackupSchedule.ConnectionProfileId))
            {
                EnsureScheduleUsesAvailableConnection(SelectedSchedule);
                _ = RefreshDatabaseListAsync();
            }

            if (SelectedSchedule != null &&
                (e.PropertyName == nameof(BackupSchedule.Frequency) ||
                 e.PropertyName == nameof(BackupSchedule.LocalRunTime) ||
                 e.PropertyName == nameof(BackupSchedule.DaysOfWeek) ||
                 e.PropertyName == nameof(BackupSchedule.DayOfMonth) ||
                 e.PropertyName == nameof(BackupSchedule.CustomIntervalMinutes) ||
                 e.PropertyName == nameof(BackupSchedule.Enabled)))
            {
                RefreshNextRunEstimate(SelectedSchedule);
            }

            if (e.PropertyName == nameof(BackupSchedule.OutputDirectory) ||
                e.PropertyName == nameof(BackupSchedule.Name))
            {
                UpdateExistingBackupCount();
            }

            if (e.PropertyName == nameof(BackupSchedule.RetentionCount))
            {
                OnPropertyChanged(nameof(BackupCountSummary));
            }

            OnPropertyChanged(nameof(SelectedScheduleSummary));
        }

        private void UpdateFrequencyPanels()
        {
            if (SelectedSchedule == null)
            {
                WeeklyPanelCard.Visibility = Visibility.Collapsed;
                MonthlyPanelCard.Visibility = Visibility.Collapsed;
                CustomPanelCard.Visibility = Visibility.Collapsed;
                return;
            }

            WeeklyPanelCard.Visibility = SelectedSchedule.Frequency == BackupScheduleFrequency.Weekly
                ? Visibility.Visible
                : Visibility.Collapsed;
            MonthlyPanelCard.Visibility = SelectedSchedule.Frequency == BackupScheduleFrequency.Monthly
                ? Visibility.Visible
                : Visibility.Collapsed;
            CustomPanelCard.Visibility = SelectedSchedule.Frequency == BackupScheduleFrequency.CustomInterval
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void UpdateExistingBackupCount()
        {
            if (SelectedSchedule == null)
            {
                ExistingBackupCount = 0;
                return;
            }

            try
            {
                var root = DatabaseBackupService.GetScheduleRoot(SelectedSchedule);
                if (!Directory.Exists(root))
                {
                    ExistingBackupCount = 0;
                    return;
                }

                var count = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly).Count();
                ExistingBackupCount = count;
            }
            catch
            {
                ExistingBackupCount = 0;
            }
        }

        private void EnsureScheduleUsesAvailableConnection(BackupSchedule? schedule)
        {
            if (schedule == null) return;

            var profile = GetProfileById(schedule.ConnectionProfileId);
            if (profile == null)
            {
                schedule.ConnectionProfileId = ConnectionProfiles.FirstOrDefault()?.Id ?? string.Empty;
                profile = GetProfileById(schedule.ConnectionProfileId);
            }

            if (profile != null &&
                string.IsNullOrWhiteSpace(schedule.DatabaseName) &&
                !string.IsNullOrWhiteSpace(profile.DbName))
            {
                schedule.DatabaseName = profile.DbName;
            }
        }

        private ConnectionProfile? GetSelectedProfile()
        {
            return GetProfileById(SelectedSchedule?.ConnectionProfileId);
        }

        private ConnectionProfile? GetProfileById(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            return ConnectionProfiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        private async Task RefreshDatabaseListAsync(bool userRequested = false)
        {
            if (IsDbLoading) return;

            if (ConnectionProfiles.Count == 0)
            {
                DatabaseNames.Clear();
                UpdateDatabaseStatus("No database connections configured yet.", isInfo: true);
                return;
            }

            if (SelectedSchedule == null)
            {
                DatabaseNames.Clear();
                UpdateDatabaseStatus("Select a schedule to load databases.", isInfo: true);
                return;
            }

            var profile = GetSelectedProfile();
            if (profile == null)
            {
                DatabaseNames.Clear();
                UpdateDatabaseStatus("The selected connection profile is no longer available.", isError: true);
                return;
            }

            if (profile.DbType == DatabaseType.None)
            {
                DatabaseNames.Clear();
                UpdateDatabaseStatus("This connection does not have database details configured.", isInfo: true);
                return;
            }

            try
            {
                IsDbLoading = true;
                UpdateDatabaseStatus("Connecting to database server…", isInfo: true);
                DatabaseNames.Clear();

                var info = BuildDatabaseConnectionInfo(profile);
                await using var dbClient = new DatabaseClient();
                await dbClient.ConnectAsync(info);
                var dbs = await dbClient.GetDatabasesAsync();

                DatabaseNames.Clear();
                foreach (var db in dbs)
                {
                    DatabaseNames.Add(db);
                }

                if (DatabaseNames.Count == 0)
                {
                    UpdateDatabaseStatus("No databases were returned by the server.", isInfo: true);
                }
                else
                {
                    UpdateDatabaseStatus($"Loaded {DatabaseNames.Count} database{(DatabaseNames.Count == 1 ? string.Empty : "s")}.");
                    if (string.IsNullOrWhiteSpace(SelectedSchedule.DatabaseName) ||
                        !DatabaseNames.Any(db => string.Equals(db, SelectedSchedule.DatabaseName, StringComparison.OrdinalIgnoreCase)))
                    {
                        SelectedSchedule.DatabaseName = DatabaseNames[0];
                    }
                }
            }
            catch (Exception ex)
            {
                DatabaseNames.Clear();
                UpdateDatabaseStatus($"Failed to load: {ex.Message}", isError: true);
                if (userRequested)
                {
                    ModernMessageBox.Show($"Unable to load databases:\n{ex.Message}", "Backup Scheduler", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                IsDbLoading = false;
            }
        }

        private DatabaseConnectionInfo BuildDatabaseConnectionInfo(ConnectionProfile profile)
        {
            var entry = DatabaseConnectionEntry.FromProfile(profile);
            if (SelectedSchedule != null && !string.IsNullOrWhiteSpace(SelectedSchedule.DatabaseName))
            {
                entry.DatabaseName = SelectedSchedule.DatabaseName;
            }
            return entry.ToConnectionInfo();
        }

        private void UpdateDatabaseStatus(string message, bool isError = false, bool isInfo = false)
        {
            DatabaseStatus = message;
            if (isError)
            {
                DatabaseStatusBrush = StatusErrorBrush;
            }
            else if (isInfo)
            {
                DatabaseStatusBrush = StatusInfoBrush;
            }
            else
            {
                DatabaseStatusBrush = StatusSuccessBrush;
            }
        }

        private void AddRunLog(string message, bool isError = false)
        {
            RunLog.Insert(0, new BackupRunLogEntry
            {
                Timestamp = DateTime.Now,
                Message = message,
                IsError = isError
            });

            const int maxEntries = 200;
            if (RunLog.Count > maxEntries)
            {
                RunLog.RemoveAt(RunLog.Count - 1);
            }

        }

        private void ResetProgress()
        {
            TotalTables = 0;
            ProcessedTables = 0;
            CurrentProgressText = "Preparing backup …";
        }

        private void HandleBackupProgress(BackupProgressUpdate? update)
        {
            if (update == null) return;
            var now = DateTime.UtcNow;
            var isHeavyStage = string.IsNullOrWhiteSpace(update.Stage) ||
                               update.Stage == "TableStart" ||
                               update.Stage == "TableComplete" ||
                               update.Stage == "Compressing";
            if (!isHeavyStage && now - _lastProgressUiUpdateUtc < TimeSpan.FromMilliseconds(200))
            {
                return;
            }
            _lastProgressUiUpdateUtc = now;

            if (!string.IsNullOrWhiteSpace(update.Message) && isHeavyStage)
            {
                AddRunLog(update.Message);
            }

            if (!string.IsNullOrWhiteSpace(update.Message))
            {
                CurrentProgressText = update.Message;
            }

            if (update.TotalTables > 0)
            {
                TotalTables = update.TotalTables;
            }

            if (update.ProcessedTables >= 0)
            {
                ProcessedTables = update.ProcessedTables;
            }
        }

        private void AddSchedule_Click(object sender, RoutedEventArgs e)
        {
            var schedule = new BackupSchedule
            {
                Name = $"Backup plan {Schedules.Count + 1}",
                ConnectionProfileId = ConnectionProfiles.FirstOrDefault()?.Id ?? string.Empty,
                OutputDirectory = GetDefaultOutputDirectory(),
                DatabaseName = ConnectionProfiles.FirstOrDefault()?.DbName ?? string.Empty
            };

            Schedules.Add(schedule);
            SelectedSchedule = schedule;
            RefreshNextRunEstimate(schedule);
        }

        private void DuplicateSchedule_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedSchedule == null) return;
            var clone = CloneSchedule(SelectedSchedule);
            clone.Name += " (copy)";
            Schedules.Add(clone);
            SelectedSchedule = clone;
            RefreshNextRunEstimate(clone);
        }

        private async void RefreshDatabases_Click(object sender, RoutedEventArgs e)
        {
            await RefreshDatabaseListAsync(true);
        }

        private void DeleteSchedule_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedSchedule == null) return;
            if (ModernMessageBox.ShowWithResult($"Delete '{SelectedSchedule.Name}'?", "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning, "Delete", "Cancel") == MessageBoxResult.Yes)
            {
                var toRemove = SelectedSchedule;
                var scheduleId = toRemove.Id;
                Schedules.Remove(toRemove);
                BackupScheduleStore.SaveSchedules(Schedules);
                if (Schedules.Count > 0)
                {
                    SelectedSchedule = Schedules[0];
                }
                else
                {
                    SelectedSchedule = null;
                }
            }
        }

        private void SaveSchedules_Click(object sender, RoutedEventArgs e)
        {
            BackupScheduleStore.SaveSchedules(Schedules);
            ModernMessageBox.Show("Schedules saved successfully.", "Backup Scheduler", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OpenHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new BackupHistoryWindow();
            window.Owner = Window.GetWindow(this);
            window.ShowDialog();
        }

        private async void RunNow_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedSchedule == null)
            {
                ModernMessageBox.Show("Select a schedule first.", "Backup Scheduler", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (IsBackupRunning)
            {
                ModernMessageBox.Show("A backup is already running.", "Backup Scheduler", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var profile = GetSelectedProfile();
            if (profile == null)
            {
                ModernMessageBox.Show("The selected connection profile is unavailable.", "Backup Scheduler", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(SelectedSchedule.DatabaseName))
            {
                ModernMessageBox.Show("Choose a database before running the backup.", "Backup Scheduler", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(SelectedSchedule.OutputDirectory))
            {
                SelectedSchedule.OutputDirectory = GetDefaultOutputDirectory();
            }

            IsBackupRunning = true;
            ResetProgress();
            RunNowButton.IsEnabled = false;
            var previousContent = RunNowButton.Content;
            RunNowButton.Content = "Running…";
            _backupCts = new CancellationTokenSource();
            _pauseSource = new PauseTokenSource();
            IsBackupPaused = false;
            _currentTaskHandle = _taskMonitor.StartTask(SelectedSchedule, profile, allowCancel: true, "Manual");
            _taskMonitor.AttachPauseToken(_currentTaskHandle.TaskId, _pauseSource);

            var historyEntry = new BackupHistoryEntry
            {
                ScheduleId = SelectedSchedule.Id,
                ScheduleName = SelectedSchedule.Name,
                ConnectionProfileId = SelectedSchedule.ConnectionProfileId,
                DatabaseName = SelectedSchedule.DatabaseName,
                StartedUtc = DateTime.UtcNow
            };

            AddRunLog($"Starting backup '{SelectedSchedule.Name}' ({SelectedSchedule.DatabaseName}) …");
            var progress = new Progress<BackupProgressUpdate>(update =>
            {
                HandleBackupProgress(update);
                if (_currentTaskHandle != null)
                {
                    _taskMonitor.UpdateProgress(_currentTaskHandle.TaskId, update);
                }
            });

            try
            {
                var result = await _backupService.RunBackupAsync(profile, SelectedSchedule, progress, _backupCts.Token, _pauseSource);
                var sizeLabel = FormatBytes(result.BytesWritten);
                AddRunLog($"Backup completed ({sizeLabel}) → {result.OutputPath}");
                CurrentProgressText = $"Backup completed ({sizeLabel})";
                ProcessedTables = TotalTables;
                var health = _healthService.Verify(result.OutputPath, SelectedSchedule.CompressOutput);
                if (health.IsHealthy)
                {
                    AddRunLog("Health check passed.");
                }
                else
                {
                    AddRunLog($"Health check issues: {health.Details}", true);
                }
                if (_currentTaskHandle != null)
                {
                    _taskMonitor.CompleteTask(_currentTaskHandle.TaskId, $"[{SelectedSchedule.Name}] Backup completed ({sizeLabel}).");
                }
                historyEntry.CompletedUtc = DateTime.UtcNow;
                historyEntry.Success = true;
                historyEntry.OutputPath = result.OutputPath;
                historyEntry.FileSizeBytes = result.BytesWritten;
                historyEntry.Sha256 = result.Sha256;
                historyEntry.HealthPassed = health.IsHealthy;
                historyEntry.HealthDetails = health.Details;
                var healthLabel = health.IsHealthy ? "passed" : "FAILED";
                historyEntry.Message = $"Manual run ({sizeLabel}) · Health {healthLabel}.";
                BackupHistoryStore.AddEntry(historyEntry);
                _notificationService.ShowToast("Backup finished", $"{SelectedSchedule.Name} completed.");
                ReloadHistory();
                SelectedSchedule.LastRunUtc = DateTime.UtcNow;
                RefreshNextRunEstimate(SelectedSchedule);
                BackupScheduleStore.SaveSchedules(Schedules);
                OnPropertyChanged(nameof(SelectedScheduleSummary));
            }
            catch (OperationCanceledException)
            {
                AddRunLog("Backup canceled by user.", true);
                historyEntry.CompletedUtc = DateTime.UtcNow;
                historyEntry.Success = false;
                historyEntry.Message = "Canceled by user.";
                BackupHistoryStore.AddEntry(historyEntry);
                if (_currentTaskHandle != null)
                {
                    _taskMonitor.MarkCancelled(_currentTaskHandle.TaskId, $"[{SelectedSchedule.Name}] Backup canceled.");
                }
                ReloadHistory();
            }
            catch (Exception ex)
            {
                AddRunLog($"Backup failed: {ex.Message}", true);
                CurrentProgressText = "Backup failed.";
                historyEntry.CompletedUtc = DateTime.UtcNow;
                historyEntry.Success = false;
                historyEntry.Message = ex.Message;
                BackupHistoryStore.AddEntry(historyEntry);
                if (_currentTaskHandle != null)
                {
                    _taskMonitor.FailTask(_currentTaskHandle.TaskId, $"[{SelectedSchedule.Name}] Backup failed: {ex.Message}");
                }
                _notificationService.ShowToast("Backup failed", $"{SelectedSchedule.Name}: {ex.Message}");
                ModernMessageBox.Show($"Backup failed:\n{ex.Message}", "Backup Scheduler", MessageBoxButton.OK, MessageBoxImage.Error);
                ReloadHistory();
            }
            finally
            {
                IsBackupRunning = false;
                RunNowButton.Content = previousContent;
                RunNowButton.IsEnabled = true;
                _pauseSource?.Resume();
                _pauseSource = null;
                _backupCts?.Dispose();
                _backupCts = null;
                IsBackupPaused = false;
                _currentTaskHandle?.Dispose();
                _currentTaskHandle = null;
                UpdateExistingBackupCount();
                OnPropertyChanged(nameof(BackupCountSummary));
            }
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (!IsBackupRunning || _pauseSource == null)
            {
                return;
            }

            if (!IsBackupPaused)
            {
                _pauseSource.Pause();
                IsBackupPaused = true;
                AddRunLog("Backup paused by user.");
                CurrentProgressText = "Paused — waiting to resume …";
            }
            else
            {
                _pauseSource.Resume();
                IsBackupPaused = false;
                AddRunLog("Backup resumed.");
                CurrentProgressText = "Resuming backup …";
            }
        }

        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedSchedule == null) return;
            using var dialog = new Forms.FolderBrowserDialog
            {
                SelectedPath = string.IsNullOrWhiteSpace(SelectedSchedule.OutputDirectory)
                    ? GetDefaultOutputDirectory()
                    : SelectedSchedule.OutputDirectory,
                Description = "Select backup destination"
            };

            if (dialog.ShowDialog() == Forms.DialogResult.OK)
            {
                SelectedSchedule.OutputDirectory = dialog.SelectedPath;
                UpdateExistingBackupCount();
                OnPropertyChanged(nameof(BackupCountSummary));
            }
        }

        private void OpenOutputFolder_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedSchedule == null)
            {
                ModernMessageBox.Show("Select a schedule first.", "Backup Scheduler", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var targetPath = SelectedSchedule.OutputDirectory;
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                targetPath = GetDefaultOutputDirectory();
            }

            try
            {
                Directory.CreateDirectory(targetPath);
                var info = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{targetPath}\"",
                    UseShellExecute = true
                };
                Process.Start(info);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Unable to open folder:\n{ex.Message}", "Backup Scheduler", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenTasks_Click(object sender, RoutedEventArgs e)
        {
            var window = new BackupTasksWindow
            {
                Owner = Window.GetWindow(this)
            };
            window.Show();
        }

        private string GetDefaultOutputDirectory()
        {
            var projectPath = _configService.LoadGlobalConfig().LastProjectPath;
            if (!string.IsNullOrWhiteSpace(projectPath) && System.IO.Directory.Exists(projectPath))
            {
                return System.IO.Path.Combine(projectPath, "Backups");
            }
            return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }

        private BackupSchedule CloneSchedule(BackupSchedule source)
        {
            return new BackupSchedule
            {
                Name = source.Name,
                ConnectionProfileId = source.ConnectionProfileId,
                DatabaseName = source.DatabaseName,
                Enabled = source.Enabled,
                Frequency = source.Frequency,
                LocalRunTime = source.LocalRunTime,
                DaysOfWeek = source.DaysOfWeek?.ToList() ?? new List<DayOfWeek>(),
                DayOfMonth = source.DayOfMonth,
                CustomIntervalMinutes = source.CustomIntervalMinutes,
                OutputDirectory = source.OutputDirectory,
                CompressOutput = source.CompressOutput,
                RetentionCount = source.RetentionCount
            };
        }

        private void OpenInWindow_Click(object sender, RoutedEventArgs e)
        {
            var window = new PageHostWindow(new BackupSchedulerPage(), "Backup Scheduler • Detached");
            window.Show();
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            var order = Math.Min(units.Length - 1, (int)Math.Floor(Math.Log(bytes, 1024)));
            var adjusted = bytes / Math.Pow(1024, order);
            return $"{adjusted:0.##} {units[order]}";
        }

        private void RefreshNextRunEstimate(BackupSchedule? schedule)
        {
            if (schedule == null)
            {
                return;
            }

            schedule.NextRunUtc = BackupSchedulePlanner.CalculateNextRunUtc(schedule, DateTime.UtcNow);
        }

        protected virtual void OnPropertyChanged(string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(storage, value))
            {
                return false;
            }

            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        public class WeekdaySelection : INotifyPropertyChanged
        {
            private bool _isSelected;

            public DayOfWeek Day { get; }
            public string Label { get; }

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value) return;
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }

            public WeekdaySelection(DayOfWeek day, string label)
            {
                Day = day;
                Label = label;
            }

            public event PropertyChangedEventHandler? PropertyChanged;
        }

        public class BackupModeOption
        {
            public BackupMode Mode { get; }
            public string Label { get; }

            public BackupModeOption(BackupMode mode, string label)
            {
                Mode = mode;
                Label = label;
            }

            public override string ToString() => Label;
        }

        public class CompressionFormatOption
        {
            public BackupCompressionFormat Format { get; }
            public string Label { get; }

            public CompressionFormatOption(BackupCompressionFormat format, string label)
            {
                Format = format;
                Label = label;
            }

            public override string ToString() => Label;
        }

    }
}

