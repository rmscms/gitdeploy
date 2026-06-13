using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GitDeployPro.Controls;
using GitDeployPro.Models;
using GitDeployPro.Services;
using GitDeployPro.Windows;
using Renci.SshNet;
using Renci.SshNet.Common;
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
        private readonly BackupRestoreValidationService _restoreValidationService = new();
        private readonly NotificationService _notificationService = new();
        private readonly BackupTaskMonitor _taskMonitor = BackupTaskMonitor.Instance;
        private string _localValidationStatus = "Local validation settings are not tested yet.";
        private MediaBrush _localValidationStatusBrush = StatusInfoBrush;
        private bool _isLocalValidationBusy;
        private static MediaBrush ResolveThemeBrush(string resourceKey, MediaBrush fallback)
        {
            if (string.IsNullOrWhiteSpace(resourceKey))
            {
                return fallback;
            }

            return System.Windows.Application.Current?.TryFindResource(resourceKey) as MediaBrush ?? fallback;
        }

        private static MediaBrush StatusInfoBrush => ResolveThemeBrush("Text.Muted", MediaBrushes.LightGray);
        private static MediaBrush StatusSuccessBrush => ResolveThemeBrush("Status.Success", MediaBrushes.LightGreen);
        private static MediaBrush StatusErrorBrush => ResolveThemeBrush("Status.Error", MediaBrushes.OrangeRed);
        private int _totalTables;
        private int _processedTables;
        private string _currentProgressText = "Idle";
        private string _currentProgressStage = "Idle";
        private DateTime _lastProgressUiUpdateUtc = DateTime.MinValue;
        private CancellationTokenSource? _backupCts;
        private PauseTokenSource? _pauseSource;
        private bool _isBackupPaused;
        private int _existingBackupCount;
        private BackupTaskHandle? _currentTaskHandle;
        private bool _monitorEventsHooked;
        private int _currentWizardStep = 1;
        private bool _isReadinessBusy;
        private bool _readinessDirty = true;
        private int _databaseRefreshVersion;
        private bool _databaseRefreshPending;
        private bool _databaseRefreshPendingUserRequested;
        private string _artifactFilesStatus = "Select a schedule to load backup files.";
        private ProjectBackupFileItem? _selectedProjectBackupFile;
        private string _customIntervalHoursText = "24";
        private string _customIntervalMinutesPartText = "0";
        private bool _isSyncingCustomIntervalEditor;
        private bool _hasUnsavedScheduleChanges;
        private static readonly Regex ArtifactNameRegex = new(
            @"^(?<db>.+)_(?<stamp>\d{2}_\d{2}_\d{2}_\d{2}_\d{2})(?:_(?<seq>\d+))?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex VerifiedWithSequenceRegex = new(
            @"^(?<core>.+)_verify_(?<seq>\d+)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly string[] KnownArtifactTails =
        {
            ".sql.gz.protected",
            ".tar.gz.protected",
            ".zip.protected",
            ".sql.protected",
            ".sql.gz",
            ".tar.gz",
            ".zip",
            ".sql"
        };

        public ObservableCollection<BackupSchedule> Schedules { get; } = new();
        public ObservableCollection<ConnectionProfile> ConnectionProfiles { get; } = new();
        public ObservableCollection<WeekdaySelection> WeekdaySelections => _weekdaySelections;
        public ObservableCollection<string> DatabaseNames { get; } = new();
        public ObservableCollection<BackupRunLogEntry> RunLog { get; } = new();
        public ObservableCollection<BackupHistoryEntry> BackupHistory { get; } = BackupHistoryStore.LoadHistory();
        public ObservableCollection<ProjectBackupFileItem> ProjectBackupFiles { get; } = new();
        public ObservableCollection<ReadinessCheckItem> ReadinessChecks { get; } = new();
        public List<BackupModeOption> BackupModes { get; } = new()
        {
            new BackupModeOption(BackupMode.Standard, "Standard (safe)"),
            new BackupModeOption(BackupMode.Fast, "Fast (bulk)"),
            new BackupModeOption(BackupMode.ExternalTool, "External (mysqldump)"),
            new BackupModeOption(BackupMode.RemoteSshMysqldump, "SSH Remote mysqldump (stream)"),
            new BackupModeOption(BackupMode.RemoteSshFileBuild, "SSH Remote file build (server-side)")
        };
        public List<RemoteDownloadPolicyOption> RemoteDownloadPolicies { get; } = new()
        {
            new RemoteDownloadPolicyOption(RemoteArtifactDownloadPolicy.ManualReference, "Manual (keep on server, save reference)"),
            new RemoteDownloadPolicyOption(RemoteArtifactDownloadPolicy.AutoDownload, "Auto download via SFTP after build")
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

        public string LocalValidationStatus
        {
            get => _localValidationStatus;
            private set => SetProperty(ref _localValidationStatus, value);
        }

        public MediaBrush LocalValidationStatusBrush
        {
            get => _localValidationStatusBrush;
            private set => SetProperty(ref _localValidationStatusBrush, value);
        }

        public bool IsLocalValidationBusy
        {
            get => _isLocalValidationBusy;
            private set => SetProperty(ref _isLocalValidationBusy, value);
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
                    OnPropertyChanged(nameof(IsProgressIndeterminate));
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
                    OnPropertyChanged(nameof(IsProgressIndeterminate));
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
            : GetNonTableProgressSummary();

        public bool ShowBackupProgress => IsBackupRunning || ProcessedTables > 0 || TotalTables > 0;

        public bool IsProgressIndeterminate => IsBackupRunning && TotalTables <= 0;

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

        public string ArtifactFilesStatus
        {
            get => _artifactFilesStatus;
            private set => SetProperty(ref _artifactFilesStatus, value);
        }

        public ProjectBackupFileItem? SelectedProjectBackupFile
        {
            get => _selectedProjectBackupFile;
            set => SetProperty(ref _selectedProjectBackupFile, value);
        }

        public string ArtifactFilesPathLabel
        {
            get
            {
                if (SelectedSchedule == null)
                {
                    return "Path: -";
                }

                return $"Path: {ResolveScheduleArtifactsRoot(SelectedSchedule)}";
            }
        }

        public int CurrentWizardStep
        {
            get => _currentWizardStep;
            private set
            {
                if (SetProperty(ref _currentWizardStep, value))
                {
                    OnPropertyChanged(nameof(WizardStepTitle));
                    OnPropertyChanged(nameof(WizardStepHint));
                    UpdateWizardUiState();
                }
            }
        }

        public string WizardStepTitle => $"Step {CurrentWizardStep} of 4";

        public string WizardStepHint => CurrentWizardStep switch
        {
            1 => "Choose connection and target database.",
            2 => "Define mode, cadence, and output settings.",
            3 => "All readiness checks must pass.",
            _ => "Confirm summary and run backup."
        };

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
                SyncCustomIntervalEditorFromSchedule();
                EnsureScheduleUsesAvailableConnection(_selectedSchedule);
                OnPropertyChanged(nameof(SelectedSchedule));
                OnPropertyChanged(nameof(IsEditorEnabled));
                OnPropertyChanged(nameof(SelectedRunTime));
                OnPropertyChanged(nameof(SelectedScheduleSummary));
                OnPropertyChanged(nameof(IsRemoteFileMode));
                OnPropertyChanged(nameof(BackupCountSummary));
                OnPropertyChanged(nameof(ArtifactFilesPathLabel));

                if (_selectedSchedule == null)
                {
                    DatabaseNames.Clear();
                    UpdateDatabaseStatus(ConnectionProfiles.Count == 0
                        ? "No eligible database connections are configured."
                        : "Select a schedule to load databases.", isInfo: true);
                }
                else
                {
                    InvalidateDatabaseCache("Loading databases for selected schedule …");
                    _ = RefreshDatabaseListAsync();
                }

                ResetReadinessChecks();
                UpdateLocalValidationStatus("Local validation settings are not tested yet.", isInfo: true);
                RefreshProjectBackupFiles();
                CurrentWizardStep = 1;
                UpdateWizardUiState();
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

        public string CustomIntervalHoursText
        {
            get => _customIntervalHoursText;
            set
            {
                if (SetProperty(ref _customIntervalHoursText, value))
                {
                    ApplyCustomIntervalFromEditorIfValid();
                    OnPropertyChanged(nameof(CustomIntervalNormalizedLabel));
                }
            }
        }

        public string CustomIntervalMinutesPartText
        {
            get => _customIntervalMinutesPartText;
            set
            {
                if (SetProperty(ref _customIntervalMinutesPartText, value))
                {
                    ApplyCustomIntervalFromEditorIfValid();
                    OnPropertyChanged(nameof(CustomIntervalNormalizedLabel));
                }
            }
        }

        public string CustomIntervalNormalizedLabel
        {
            get
            {
                if (SelectedSchedule == null)
                {
                    return "Applied interval: -";
                }

                var total = Math.Max(0, SelectedSchedule.CustomIntervalMinutes);
                var hours = total / 60;
                var minutes = total % 60;
                return $"Applied interval: {total} min ({hours}h {minutes}m)";
            }
        }

        public string SelectedScheduleSummary
        {
            get
            {
                if (SelectedSchedule == null) return "Select or create a schedule to edit.";
                var encryptionTag = SelectedSchedule.EncryptAtRest ? " • encrypted" : string.Empty;
                var remoteTag = SelectedSchedule.BackupMode == BackupMode.RemoteSshFileBuild
                    ? $" • remote path: {SelectedSchedule.RemoteOutputDirectory} • {SelectedSchedule.RemoteDownloadPolicy}" +
                      (SelectedSchedule.DeleteRemoteArtifactAfterDownload ? " • remote cleanup: ON" : string.Empty)
                    : string.Empty;
                var validationTag = SelectedSchedule.EnableLocalRestoreValidation
                    ? $" • localhost validation: ON ({SelectedSchedule.LocalValidationHost}:{SelectedSchedule.LocalValidationPort}/{SelectedSchedule.LocalValidationDatabaseName})"
                    : string.Empty;
                return $"{SelectedSchedule.BackupMode} • {SelectedSchedule.Frequency} @ {SelectedSchedule.LocalRunTime:hh\\:mm} • retains {SelectedSchedule.RetentionCount} copies{encryptionTag}{remoteTag}{validationTag}";
            }
        }

        public bool IsRemoteFileMode => SelectedSchedule?.BackupMode == BackupMode.RemoteSshFileBuild;

        public bool HasUnsavedScheduleChanges
        {
            get => _hasUnsavedScheduleChanges;
            private set
            {
                if (SetProperty(ref _hasUnsavedScheduleChanges, value))
                {
                    OnPropertyChanged(nameof(SaveChangesButtonText));
                }
            }
        }

        public string SaveChangesButtonText => HasUnsavedScheduleChanges
            ? "⚠ Save changes"
            : "💾 Save changes";

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
            CurrentWizardStep = 1;
            UpdateWizardUiState();
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
                InvalidateDatabaseCache("Refreshing databases from updated profiles …");
                _ = RefreshDatabaseListAsync();
            }
            else
            {
                DatabaseNames.Clear();
                UpdateDatabaseStatus(ConnectionProfiles.Count == 0
                    ? "No database connections configured yet."
                    : "Select a schedule to start configuring backups.", isInfo: true);
            }

            UpdateLocalValidationStatus("Local validation settings are not tested yet.", isInfo: true);
            ResetReadinessChecks();
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

            HasUnsavedScheduleChanges = false;
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
                OnPropertyChanged(nameof(CustomIntervalNormalizedLabel));
            }

            if (e.PropertyName == nameof(BackupSchedule.CustomIntervalMinutes))
            {
                SyncCustomIntervalEditorFromSchedule();
                OnPropertyChanged(nameof(CustomIntervalNormalizedLabel));
            }

            if (e.PropertyName == nameof(BackupSchedule.LocalRunTime))
            {
                OnPropertyChanged(nameof(SelectedRunTime));
            }

            if (e.PropertyName == nameof(BackupSchedule.ConnectionProfileId))
            {
                EnsureScheduleUsesAvailableConnection(SelectedSchedule);
                CurrentWizardStep = 1;
                ResetReadinessChecks();
                InvalidateDatabaseCache("Connection profile changed. Reloading databases …");
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
                OnPropertyChanged(nameof(ArtifactFilesPathLabel));
                RefreshProjectBackupFiles(silent: true);
            }

            if (e.PropertyName == nameof(BackupSchedule.RetentionCount))
            {
                OnPropertyChanged(nameof(BackupCountSummary));
            }

            if (e.PropertyName == nameof(BackupSchedule.ConnectionProfileId) ||
                e.PropertyName == nameof(BackupSchedule.DatabaseName) ||
                e.PropertyName == nameof(BackupSchedule.OutputDirectory) ||
                e.PropertyName == nameof(BackupSchedule.BackupMode) ||
                e.PropertyName == nameof(BackupSchedule.RetentionCount) ||
                e.PropertyName == nameof(BackupSchedule.CompressOutput) ||
                e.PropertyName == nameof(BackupSchedule.EncryptAtRest) ||
                e.PropertyName == nameof(BackupSchedule.CompressionFormat) ||
                e.PropertyName == nameof(BackupSchedule.RemoteOutputDirectory) ||
                e.PropertyName == nameof(BackupSchedule.RemoteDownloadPolicy) ||
                e.PropertyName == nameof(BackupSchedule.DeleteRemoteArtifactAfterDownload) ||
                e.PropertyName == nameof(BackupSchedule.EnableLocalRestoreValidation) ||
                e.PropertyName == nameof(BackupSchedule.LocalValidationHost) ||
                e.PropertyName == nameof(BackupSchedule.LocalValidationPort) ||
                e.PropertyName == nameof(BackupSchedule.LocalValidationDatabaseName) ||
                e.PropertyName == nameof(BackupSchedule.LocalValidationUsername) ||
                e.PropertyName == nameof(BackupSchedule.LocalValidationPassword) ||
                e.PropertyName == nameof(BackupSchedule.LocalValidationCharset) ||
                e.PropertyName == nameof(BackupSchedule.LocalValidationCollation))
            {
                _readinessDirty = true;
            }

            if (e.PropertyName == nameof(BackupSchedule.EnableLocalRestoreValidation) ||
                e.PropertyName == nameof(BackupSchedule.LocalValidationHost) ||
                e.PropertyName == nameof(BackupSchedule.LocalValidationPort) ||
                e.PropertyName == nameof(BackupSchedule.LocalValidationDatabaseName) ||
                e.PropertyName == nameof(BackupSchedule.LocalValidationUsername) ||
                e.PropertyName == nameof(BackupSchedule.LocalValidationPassword) ||
                e.PropertyName == nameof(BackupSchedule.LocalValidationCharset) ||
                e.PropertyName == nameof(BackupSchedule.LocalValidationCollation))
            {
                UpdateLocalValidationStatus("Local validation settings are not tested yet.", isInfo: true);
            }

            if (e.PropertyName == nameof(BackupSchedule.BackupMode))
            {
                OnPropertyChanged(nameof(IsRemoteFileMode));
                if (SelectedSchedule != null &&
                    SelectedSchedule.BackupMode == BackupMode.RemoteSshFileBuild &&
                    string.IsNullOrWhiteSpace(SelectedSchedule.RemoteOutputDirectory))
                {
                    SelectedSchedule.RemoteOutputDirectory = "/tmp/gitdeploypro-backups";
                }
            }

            if (ShouldMarkUnsavedScheduleChange(e.PropertyName))
            {
                HasUnsavedScheduleChanges = true;
            }

            OnPropertyChanged(nameof(SelectedScheduleSummary));
        }

        private static bool ShouldMarkUnsavedScheduleChange(string? propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                return false;
            }

            return propertyName == nameof(BackupSchedule.Name) ||
                   propertyName == nameof(BackupSchedule.ConnectionProfileId) ||
                   propertyName == nameof(BackupSchedule.DatabaseName) ||
                   propertyName == nameof(BackupSchedule.Enabled) ||
                   propertyName == nameof(BackupSchedule.Frequency) ||
                   propertyName == nameof(BackupSchedule.LocalRunTime) ||
                   propertyName == nameof(BackupSchedule.DaysOfWeek) ||
                   propertyName == nameof(BackupSchedule.DayOfMonth) ||
                   propertyName == nameof(BackupSchedule.CustomIntervalMinutes) ||
                   propertyName == nameof(BackupSchedule.OutputDirectory) ||
                   propertyName == nameof(BackupSchedule.CompressOutput) ||
                   propertyName == nameof(BackupSchedule.CompressionFormat) ||
                   propertyName == nameof(BackupSchedule.RetentionCount) ||
                   propertyName == nameof(BackupSchedule.EncryptAtRest) ||
                   propertyName == nameof(BackupSchedule.BackupMode) ||
                   propertyName == nameof(BackupSchedule.RemoteDownloadPolicy) ||
                   propertyName == nameof(BackupSchedule.RemoteOutputDirectory) ||
                   propertyName == nameof(BackupSchedule.DeleteRemoteArtifactAfterDownload) ||
                   propertyName == nameof(BackupSchedule.EnableLocalRestoreValidation) ||
                   propertyName == nameof(BackupSchedule.LocalValidationHost) ||
                   propertyName == nameof(BackupSchedule.LocalValidationPort) ||
                   propertyName == nameof(BackupSchedule.LocalValidationDatabaseName) ||
                   propertyName == nameof(BackupSchedule.LocalValidationUsername) ||
                   propertyName == nameof(BackupSchedule.LocalValidationPassword) ||
                   propertyName == nameof(BackupSchedule.LocalValidationCharset) ||
                   propertyName == nameof(BackupSchedule.LocalValidationCollation);
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
            UpdateWizardUiState();
        }

        private void ResetReadinessChecks()
        {
            _readinessDirty = true;
            ReadinessChecks.Clear();
            ReadinessChecks.Add(new ReadinessCheckItem(
                "Pending",
                "Readiness checks are pending.",
                "Move to step 3 and run checks.",
                "Run readiness checks before backup.",
                StatusInfoBrush));
            OnPropertyChanged(nameof(ReadinessChecks));
        }

        private void UpdateWizardUiState()
        {
            if (!IsLoaded)
            {
                return;
            }

            if (Step1Panel == null)
            {
                return;
            }

            Step1Panel.Visibility = CurrentWizardStep == 1 ? Visibility.Visible : Visibility.Collapsed;
            Step2Panel.Visibility = CurrentWizardStep == 2 ? Visibility.Visible : Visibility.Collapsed;
            Step3Panel.Visibility = CurrentWizardStep == 3 ? Visibility.Visible : Visibility.Collapsed;
            Step4Panel.Visibility = CurrentWizardStep == 4 ? Visibility.Visible : Visibility.Collapsed;

            PreviousStepButton.IsEnabled = CurrentWizardStep > 1;
            NextStepButton.IsEnabled = CurrentWizardStep < 4;
            NextStepButton.Content = CurrentWizardStep == 3 ? "Go to confirm →" : "Next →";

            UpdateStepButtonVisual(Step1Button, 1);
            UpdateStepButtonVisual(Step2Button, 2);
            UpdateStepButtonVisual(Step3Button, 3);
            UpdateStepButtonVisual(Step4Button, 4);
        }

        private void UpdateStepButtonVisual(System.Windows.Controls.Button button, int step)
        {
            var activeBackground = ResolveThemeBrush("Accent.Primary", MediaBrushes.SteelBlue);
            var doneBackground = ResolveThemeBrush("Status.SuccessSurface", MediaBrushes.DarkSeaGreen);
            var idleBackground = ResolveThemeBrush("Surface.Input", MediaBrushes.DimGray);
            var activeBorder = ResolveThemeBrush("Accent.Primary", MediaBrushes.SteelBlue);
            var idleBorder = ResolveThemeBrush("Border.Subtle", MediaBrushes.Gray);
            var primaryText = ResolveThemeBrush("Text.Primary", MediaBrushes.WhiteSmoke);
            var inverseText = ResolveThemeBrush("Text.Inverse", MediaBrushes.Black);

            if (step == CurrentWizardStep)
            {
                button.Background = activeBackground;
                button.BorderBrush = activeBorder;
                button.Foreground = inverseText;
                return;
            }

            if (step < CurrentWizardStep)
            {
                button.Background = doneBackground;
                button.BorderBrush = ResolveThemeBrush("Status.Success", MediaBrushes.SeaGreen);
                button.Foreground = primaryText;
                return;
            }

            button.Background = idleBackground;
            button.BorderBrush = idleBorder;
            button.Foreground = primaryText;
        }

        private bool ValidateStepOne(out string message)
        {
            if (SelectedSchedule == null)
            {
                message = "Select or create a schedule first.";
                return false;
            }

            if (GetSelectedProfile() == null)
            {
                message = "Select a valid connection profile.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(SelectedSchedule.DatabaseName))
            {
                message = "Choose a target database before continuing.";
                return false;
            }

            message = string.Empty;
            return true;
        }

        private bool ValidateStepTwo(out string message)
        {
            if (SelectedSchedule == null)
            {
                message = "Select or create a schedule first.";
                return false;
            }

            if (SelectedSchedule.Frequency == BackupScheduleFrequency.CustomInterval)
            {
                if (!TryBuildCustomIntervalMinutesFromEditor(out var totalMinutes, out var customIntervalError))
                {
                    message = customIntervalError;
                    return false;
                }

                if (SelectedSchedule.CustomIntervalMinutes != totalMinutes)
                {
                    SelectedSchedule.CustomIntervalMinutes = totalMinutes;
                }
            }

            if (SelectedSchedule.RetentionCount < 1)
            {
                message = "Retention count must be at least 1.";
                return false;
            }

            var requiresLocalOutput = SelectedSchedule.BackupMode != BackupMode.RemoteSshFileBuild ||
                                      SelectedSchedule.RemoteDownloadPolicy == RemoteArtifactDownloadPolicy.AutoDownload;
            if (requiresLocalOutput && string.IsNullOrWhiteSpace(SelectedSchedule.OutputDirectory))
            {
                message = IsRemoteFileMode
                    ? "Choose a local output directory for optional auto-download artifacts."
                    : "Choose an output directory before continuing.";
                return false;
            }

            if (IsRemoteFileMode && string.IsNullOrWhiteSpace(SelectedSchedule.RemoteOutputDirectory))
            {
                message = "Set a remote output directory for server-side backup build.";
                return false;
            }

            if (IsRemoteFileMode &&
                SelectedSchedule.DeleteRemoteArtifactAfterDownload &&
                SelectedSchedule.RemoteDownloadPolicy != RemoteArtifactDownloadPolicy.AutoDownload)
            {
                message = "Remote cleanup requires Auto download policy. Set policy to Auto or disable cleanup.";
                return false;
            }

            if (IsRemoteFileMode &&
                SelectedSchedule.DeleteRemoteArtifactAfterDownload &&
                !IsSafeRemoteCleanupDirectory(SelectedSchedule.RemoteOutputDirectory, out var cleanupDirMessage))
            {
                message = cleanupDirMessage;
                return false;
            }

            if (SelectedSchedule.EnableLocalRestoreValidation)
            {
                EnsureLocalValidationDefaults(SelectedSchedule);
                if (!BackupRestoreValidationService.TryBuildLocalConnectionInfo(SelectedSchedule, out _, out var localValidationReason))
                {
                    message = localValidationReason;
                    return false;
                }

                if (SelectedSchedule.BackupMode == BackupMode.RemoteSshFileBuild &&
                    SelectedSchedule.RemoteDownloadPolicy != RemoteArtifactDownloadPolicy.AutoDownload)
                {
                    message = "Local restore validation requires a local artifact. For remote-file mode, set download policy to Auto.";
                    return false;
                }
            }

            message = string.Empty;
            return true;
        }

        private static bool IsSafeRemoteCleanupDirectory(string remoteDirectory, out string message)
        {
            var normalized = (remoteDirectory ?? string.Empty).Replace('\\', '/').Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(normalized) || !normalized.StartsWith("/", StringComparison.Ordinal))
            {
                message = "Remote cleanup requires an absolute Linux output path.";
                return false;
            }

            var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
            {
                message = "Remote cleanup requires a dedicated subdirectory (example: /tmp/gitdeploypro-backups).";
                return false;
            }

            message = string.Empty;
            return true;
        }

        private void SyncCustomIntervalEditorFromSchedule()
        {
            _isSyncingCustomIntervalEditor = true;
            try
            {
                var totalMinutes = Math.Max(0, SelectedSchedule?.CustomIntervalMinutes ?? 0);
                var hours = totalMinutes / 60;
                var minutes = totalMinutes % 60;
                CustomIntervalHoursText = hours.ToString(CultureInfo.InvariantCulture);
                CustomIntervalMinutesPartText = minutes.ToString(CultureInfo.InvariantCulture);
            }
            finally
            {
                _isSyncingCustomIntervalEditor = false;
            }

            OnPropertyChanged(nameof(CustomIntervalNormalizedLabel));
        }

        private void ApplyCustomIntervalFromEditorIfValid()
        {
            if (_isSyncingCustomIntervalEditor || SelectedSchedule == null)
            {
                return;
            }

            if (!TryBuildCustomIntervalMinutesFromEditor(out var totalMinutes, out _))
            {
                return;
            }

            if (SelectedSchedule.CustomIntervalMinutes != totalMinutes)
            {
                SelectedSchedule.CustomIntervalMinutes = totalMinutes;
            }
        }

        private bool TryBuildCustomIntervalMinutesFromEditor(out int totalMinutes, out string error)
        {
            totalMinutes = 0;

            if (!TryParseIntervalPart(CustomIntervalHoursText, out var hours))
            {
                error = "Custom interval hour must be a valid number.";
                return false;
            }

            if (!TryParseIntervalPart(CustomIntervalMinutesPartText, out var minutes))
            {
                error = "Custom interval minute must be a valid number.";
                return false;
            }

            if (hours < 0 || hours > 168)
            {
                error = "Hour must be between 0 and 168.";
                return false;
            }

            if (minutes < 0 || minutes > 59)
            {
                error = "Minute must be between 0 and 59.";
                return false;
            }

            totalMinutes = (hours * 60) + minutes;
            if (totalMinutes < 5)
            {
                error = "Custom interval must be at least 5 minutes.";
                return false;
            }

            if (totalMinutes > 10080)
            {
                error = "Custom interval cannot exceed 10080 minutes (7 days).";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static bool TryParseIntervalPart(string? value, out int parsed)
        {
            parsed = 0;
            var trimmed = value?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return false;
            }

            return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.CurrentCulture, out parsed) ||
                   int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
        }

        private bool ValidateStepThree(out string message)
        {
            if (ReadinessChecks.Count == 0 || _readinessDirty)
            {
                message = "Run readiness checks before moving to confirmation.";
                return false;
            }

            var failed = ReadinessChecks.FirstOrDefault(c => c.IsFailure);
            if (failed != null)
            {
                message = $"Readiness blocked: {failed.Title}.";
                return false;
            }

            message = string.Empty;
            return true;
        }

        private async Task<bool> RunReadinessChecksAsync(bool interactive)
        {
            if (_isReadinessBusy)
            {
                return false;
            }

            _isReadinessBusy = true;
            try
            {
                ReadinessChecks.Clear();

                if (SelectedSchedule == null)
                {
                    ReadinessChecks.Add(ReadinessCheckItem.Fail("Schedule", "No schedule selected.", "Select a schedule in Step 1."));
                    _readinessDirty = false;
                    return false;
                }

                var profile = GetSelectedProfile();
                if (profile == null)
                {
                    ReadinessChecks.Add(ReadinessCheckItem.Fail("Connection profile", "Profile is missing or deleted.", "Pick another profile in Step 1."));
                    _readinessDirty = false;
                    return false;
                }

                if (string.IsNullOrWhiteSpace(SelectedSchedule.DatabaseName))
                {
                    ReadinessChecks.Add(ReadinessCheckItem.Fail("Database", "Database name is empty.", "Pick a valid database in Step 1."));
                }
                else
                {
                    ReadinessChecks.Add(ReadinessCheckItem.Pass("Database", SelectedSchedule.DatabaseName, "Database is selected."));
                }

                var info = BuildDatabaseConnectionInfo(profile);
                bool dbConnected = false;
                IReadOnlyList<string> availableDatabases = Array.Empty<string>();
                try
                {
                    await using var dbClient = new DatabaseClient();
                    await dbClient.ConnectAsync(info);
                    availableDatabases = await dbClient.GetDatabasesAsync();
                    dbConnected = true;
                    ReadinessChecks.Add(ReadinessCheckItem.Pass("Database reachability", "Connection established successfully.", "Server and credentials look valid."));
                }
                catch (Exception ex)
                {
                    ReadinessChecks.Add(ReadinessCheckItem.Fail("Database reachability", ex.Message, "Check host/port credentials and firewall."));
                }

                if (profile.UseSSH)
                {
                    var sshHint = string.IsNullOrWhiteSpace(profile.Host)
                        ? "SSH host is not configured."
                        : $"{profile.Username}@{profile.Host}:{profile.Port}";
                    if (dbConnected)
                    {
                        ReadinessChecks.Add(ReadinessCheckItem.Pass("SSH tunnel", $"Tunnel path is usable ({sshHint}).", "SSH path is ready."));
                    }
                    else
                    {
                        ReadinessChecks.Add(ReadinessCheckItem.Warning("SSH tunnel", $"Unable to confirm SSH tunnel health ({sshHint}).", "Validate SSH credentials and server access."));
                    }
                }
                else
                {
                    ReadinessChecks.Add(ReadinessCheckItem.Warning("SSH path", "Profile does not use SSH.", "SSH-first mode requires an SSH-enabled profile."));
                }

                if (!string.IsNullOrWhiteSpace(SelectedSchedule.DatabaseName) && dbConnected)
                {
                    var hasDb = availableDatabases.Any(db => string.Equals(db, SelectedSchedule.DatabaseName, StringComparison.OrdinalIgnoreCase));
                    if (hasDb)
                    {
                        ReadinessChecks.Add(ReadinessCheckItem.Pass("Database validity", "Database exists on server.", "Ready for backup."));
                    }
                    else
                    {
                        ReadinessChecks.Add(ReadinessCheckItem.Fail("Database validity", "Selected database is not found on the server.", "Refresh database list and choose a valid database."));
                    }
                }

                if (SelectedSchedule.BackupMode == BackupMode.ExternalTool)
                {
                    var hasMysqldump = IsLocalCommandAvailable("mysqldump");
                    if (hasMysqldump)
                    {
                        ReadinessChecks.Add(ReadinessCheckItem.Pass("Tool availability", "mysqldump found locally.", "External backup mode is ready."));
                    }
                    else
                    {
                        ReadinessChecks.Add(ReadinessCheckItem.Fail("Tool availability", "mysqldump not found on this machine.", "Install MySQL client tools or switch backup mode."));
                    }
                }
                else if (SelectedSchedule.BackupMode == BackupMode.RemoteSshMysqldump)
                {
                    if (!profile.UseSSH)
                    {
                        ReadinessChecks.Add(ReadinessCheckItem.Fail("SSH mode requirements", "Remote mode requires an SSH-enabled profile.", "Enable SSH in the selected connection profile."));
                    }
                    else
                    {
                        var remoteProbe = await ProbeRemoteSshMysqldumpAsync(profile, SelectedSchedule.DatabaseName);
                        if (!remoteProbe.SshAuthenticated)
                        {
                            ReadinessChecks.Add(ReadinessCheckItem.Fail("SSH authentication", remoteProbe.SshDetails, "Fix SSH username/password or private key in connection profile."));
                        }
                        else
                        {
                            ReadinessChecks.Add(ReadinessCheckItem.Pass("SSH authentication", remoteProbe.SshDetails, "SSH credentials are valid."));
                        }

                        if (!remoteProbe.MysqldumpAvailable)
                        {
                            ReadinessChecks.Add(ReadinessCheckItem.Fail("Remote tool availability", remoteProbe.MysqldumpDetails, "Install mysqldump on remote server or switch backup mode."));
                        }
                        else
                        {
                            ReadinessChecks.Add(ReadinessCheckItem.Pass("Remote tool availability", remoteProbe.MysqldumpDetails, "Remote mysqldump is available."));
                        }

                        if (!remoteProbe.DumpCommandReady)
                        {
                            ReadinessChecks.Add(ReadinessCheckItem.Fail("Remote dump permissions", remoteProbe.DumpReadinessDetails, "Fix database user permissions for routines/triggers or adjust backup options."));
                        }
                        else
                        {
                            ReadinessChecks.Add(ReadinessCheckItem.Pass("Remote dump permissions", remoteProbe.DumpReadinessDetails, "Remote mysqldump can run with current credentials."));
                        }
                    }
                }
                else if (SelectedSchedule.BackupMode == BackupMode.RemoteSshFileBuild)
                {
                    if (!profile.UseSSH)
                    {
                        ReadinessChecks.Add(ReadinessCheckItem.Fail("SSH mode requirements", "Remote file mode requires an SSH-enabled profile.", "Enable SSH in the selected connection profile."));
                    }
                    else
                    {
                        var remoteDir = string.IsNullOrWhiteSpace(SelectedSchedule.RemoteOutputDirectory)
                            ? "/tmp/gitdeploypro-backups"
                            : SelectedSchedule.RemoteOutputDirectory.Trim();
                        var remoteProbe = await ProbeRemoteSshFileBuildAsync(profile, SelectedSchedule.DatabaseName, remoteDir);

                        if (!remoteProbe.SshAuthenticated)
                        {
                            ReadinessChecks.Add(ReadinessCheckItem.Fail("SSH authentication", remoteProbe.SshDetails, "Fix SSH username/password or private key in connection profile."));
                        }
                        else
                        {
                            ReadinessChecks.Add(ReadinessCheckItem.Pass("SSH authentication", remoteProbe.SshDetails, "SSH credentials are valid."));
                        }

                        if (!remoteProbe.MysqldumpAvailable)
                        {
                            ReadinessChecks.Add(ReadinessCheckItem.Fail("mysqldump availability", remoteProbe.MysqldumpDetails, "Install mysqldump on remote server."));
                        }
                        else
                        {
                            ReadinessChecks.Add(ReadinessCheckItem.Pass("mysqldump availability", remoteProbe.MysqldumpDetails, "Remote mysqldump is available."));
                        }

                        if (!remoteProbe.GzipAvailable)
                        {
                            ReadinessChecks.Add(ReadinessCheckItem.Fail("gzip availability", remoteProbe.GzipDetails, "Install gzip on remote server."));
                        }
                        else
                        {
                            ReadinessChecks.Add(ReadinessCheckItem.Pass("gzip availability", remoteProbe.GzipDetails, "Remote gzip is available."));
                        }

                        if (!remoteProbe.RemotePathWritable)
                        {
                            ReadinessChecks.Add(ReadinessCheckItem.Fail("Remote output path", remoteProbe.RemotePathDetails, "Set a writable remote path in Step 2."));
                        }
                        else
                        {
                            ReadinessChecks.Add(ReadinessCheckItem.Pass("Remote output path", remoteProbe.RemotePathDetails, "Remote directory is writable."));
                        }

                        if (!remoteProbe.DumpCommandReady)
                        {
                            ReadinessChecks.Add(ReadinessCheckItem.Fail("Remote dump dry-run", remoteProbe.DumpReadinessDetails, "Fix database credentials/permissions before running backup."));
                        }
                        else
                        {
                            ReadinessChecks.Add(ReadinessCheckItem.Pass("Remote dump dry-run", remoteProbe.DumpReadinessDetails, "Remote dump can run with current credentials."));
                        }

                        if (SelectedSchedule.RemoteDownloadPolicy == RemoteArtifactDownloadPolicy.AutoDownload)
                        {
                            ReadinessChecks.Add(ReadinessCheckItem.Pass("Download policy", "Auto download enabled via SFTP.", "Artifact will be downloaded after remote build."));
                        }
                        else
                        {
                            ReadinessChecks.Add(ReadinessCheckItem.Warning("Download policy", "Manual reference mode is active.", "Artifact stays on server until operator downloads it."));
                        }

                        if (SelectedSchedule.DeleteRemoteArtifactAfterDownload)
                        {
                            if (SelectedSchedule.RemoteDownloadPolicy == RemoteArtifactDownloadPolicy.AutoDownload)
                            {
                                ReadinessChecks.Add(ReadinessCheckItem.Pass("Remote cleanup policy", "Remote artifact cleanup is enabled after verified download.", "Cleanup will remove only the generated artifact file."));
                            }
                            else
                            {
                                ReadinessChecks.Add(ReadinessCheckItem.Fail("Remote cleanup policy", "Cleanup is enabled but download policy is not Auto.", "Use Auto download policy when remote cleanup is enabled."));
                            }
                        }
                    }
                }
                else
                {
                    ReadinessChecks.Add(ReadinessCheckItem.Pass("Runtime capability", "Managed mode has no external binary dependency.", "Ready to run."));
                }

                if (SelectedSchedule.EncryptAtRest)
                {
                    if (OperatingSystem.IsWindows())
                    {
                        ReadinessChecks.Add(ReadinessCheckItem.Pass("Encryption capability", "Windows DPAPI is available.", "Backups will be protected for current user scope."));
                    }
                    else
                    {
                        ReadinessChecks.Add(ReadinessCheckItem.Fail("Encryption capability", "Encrypt-at-rest requires Windows DPAPI.", "Disable encryption or run scheduler on Windows."));
                    }
                }

                var requiresLocalOutput = SelectedSchedule.BackupMode != BackupMode.RemoteSshFileBuild ||
                                          SelectedSchedule.RemoteDownloadPolicy == RemoteArtifactDownloadPolicy.AutoDownload;
                if (requiresLocalOutput)
                {
                    if (TryCheckOutputDirectory(SelectedSchedule.OutputDirectory, out var outputMessage, out var outputHint, out var availableBytes))
                    {
                        ReadinessChecks.Add(ReadinessCheckItem.Pass("Output path write access", outputMessage, outputHint));

                        if (availableBytes < 512L * 1024 * 1024)
                        {
                            ReadinessChecks.Add(ReadinessCheckItem.Warning("Free space", $"Only {FormatBytes(availableBytes)} free space detected.", "Keep at least 512 MB free for stable backup operations."));
                        }
                        else
                        {
                            ReadinessChecks.Add(ReadinessCheckItem.Pass("Free space", $"{FormatBytes(availableBytes)} free space available.", "Disk capacity is sufficient."));
                        }
                    }
                    else
                    {
                        ReadinessChecks.Add(ReadinessCheckItem.Fail("Output path write access", outputMessage, outputHint));
                    }
                }
                else
                {
                    ReadinessChecks.Add(ReadinessCheckItem.Pass("Local output path", "Manual remote mode does not require local file output.", "Optional local path checks skipped."));
                }

                if (SelectedSchedule.EnableLocalRestoreValidation)
                {
                    if (!BackupRestoreValidationService.TryBuildLocalConnectionInfo(SelectedSchedule, out _, out var localValidationReason))
                    {
                        ReadinessChecks.Add(ReadinessCheckItem.Fail("Localhost-only policy", localValidationReason, "Use localhost/127.0.0.1 and valid local credentials in Step 1."));
                    }
                    else
                    {
                        ReadinessChecks.Add(ReadinessCheckItem.Pass("Localhost-only policy", "Validation target is localhost-safe.", "Localhost safety rules are satisfied."));
                        var inspect = await _restoreValidationService.InspectConfiguredDatabaseAsync(SelectedSchedule, CancellationToken.None);
                        if (!inspect.IsSuccess)
                        {
                            ReadinessChecks.Add(ReadinessCheckItem.Fail("Local validation DB", inspect.Message, "Fix validation DB settings in Step 1."));
                        }
                        else if (!inspect.DatabaseExists && !string.IsNullOrWhiteSpace(inspect.ConfiguredDatabase))
                        {
                            ReadinessChecks.Add(ReadinessCheckItem.Warning(
                                "Local validation DB",
                                $"Configured DB '{inspect.ConfiguredDatabase}' does not exist yet.",
                                $"Run 'Test + create DB' to create it with {inspect.EffectiveCharset}/{inspect.EffectiveCollation}."));
                        }
                        else
                        {
                            ReadinessChecks.Add(ReadinessCheckItem.Pass(
                                "Local validation DB",
                                string.IsNullOrWhiteSpace(inspect.ConfiguredDatabase)
                                    ? "Validation DB name is empty; runtime will generate temporary DB only."
                                    : $"Configured DB exists: {inspect.ConfiguredDatabase}.",
                                "Validation storage target is ready."));
                        }

                        var probeResult = await _restoreValidationService.ProbeEnvironmentAsync(
                            SelectedSchedule,
                            CancellationToken.None,
                            ensureConfiguredDatabase: false);
                        if (probeResult.IsSuccess)
                        {
                            ReadinessChecks.Add(ReadinessCheckItem.Pass("Localhost validation access", probeResult.Message, "Connection and temp DB permissions are ready."));
                        }
                        else
                        {
                            ReadinessChecks.Add(ReadinessCheckItem.Fail("Localhost validation access", probeResult.Message, "Start local DB service or fix localhost credentials."));
                        }
                    }

                    var hasLocalArtifactAtCompletion =
                        SelectedSchedule.BackupMode != BackupMode.RemoteSshFileBuild ||
                        SelectedSchedule.RemoteDownloadPolicy == RemoteArtifactDownloadPolicy.AutoDownload;
                    if (hasLocalArtifactAtCompletion)
                    {
                        ReadinessChecks.Add(ReadinessCheckItem.Pass("Validation artifact source", "Backup mode provides local artifact for restore validation.", "Validation import can run after backup."));
                    }
                    else
                    {
                        ReadinessChecks.Add(ReadinessCheckItem.Fail("Validation artifact source", "Current mode does not produce a local artifact.", "Use Auto download in remote-file mode to enable validation."));
                    }
                }

                _readinessDirty = false;
                var failed = ReadinessChecks.Any(c => c.IsFailure);
                if (interactive)
                {
                    AddRunLog(failed
                        ? "Readiness check completed with blocking issues."
                        : "Readiness check completed successfully.");
                }
                return !failed;
            }
            finally
            {
                _isReadinessBusy = false;
            }
        }

        private static bool TryCheckOutputDirectory(string? directory, out string details, out string hint, out long availableBytes)
        {
            availableBytes = 0;
            if (string.IsNullOrWhiteSpace(directory))
            {
                details = "Output directory is empty.";
                hint = "Select a destination folder in Step 2.";
                return false;
            }

            try
            {
                Directory.CreateDirectory(directory);
                var probeFile = Path.Combine(directory, $".write-check-{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probeFile, "ok");
                File.Delete(probeFile);

                var root = Path.GetPathRoot(Path.GetFullPath(directory));
                if (!string.IsNullOrWhiteSpace(root))
                {
                    var drive = new DriveInfo(root);
                    if (drive.IsReady)
                    {
                        availableBytes = drive.AvailableFreeSpace;
                    }
                }

                details = $"Writable folder: {directory}";
                hint = "Path permissions are valid.";
                return true;
            }
            catch (Exception ex)
            {
                details = ex.Message;
                hint = "Use a local writable path or adjust file permissions.";
                return false;
            }
        }

        private static bool IsLocalCommandAvailable(string command)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "where",
                        Arguments = command,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };

                process.Start();
                process.WaitForExit(5000);
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private static async Task<RemoteSshProbeResult> ProbeRemoteSshMysqldumpAsync(ConnectionProfile profile, string databaseName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var entry = DatabaseConnectionEntry.FromProfile(profile);
                    var sshHost = string.IsNullOrWhiteSpace(entry.SshHost) ? "127.0.0.1" : entry.SshHost;
                    var sshPort = entry.SshPort <= 0 ? 22 : entry.SshPort;
                    var sshUser = string.IsNullOrWhiteSpace(entry.SshUsername) ? "root" : entry.SshUsername;

                    var authMethods = new List<AuthenticationMethod>();
                    if (!string.IsNullOrWhiteSpace(entry.SshPrivateKeyPath) && File.Exists(entry.SshPrivateKeyPath))
                    {
                        var keyFile = new PrivateKeyFile(entry.SshPrivateKeyPath);
                        authMethods.Add(new PrivateKeyAuthenticationMethod(sshUser, keyFile));
                    }

                    if (!string.IsNullOrWhiteSpace(entry.SshPassword))
                    {
                        authMethods.Add(new PasswordAuthenticationMethod(sshUser, entry.SshPassword));
                    }

                    if (authMethods.Count == 0)
                    {
                        return new RemoteSshProbeResult(
                            false,
                            false,
                            "No SSH authentication method is configured.",
                            "mysqldump check skipped because SSH login failed.",
                            false,
                            "mysqldump check skipped because SSH login failed.");
                    }

                    var connectionInfo = new ConnectionInfo(sshHost, sshPort, sshUser, authMethods.ToArray());
                    using var ssh = new SshClient(connectionInfo);
                    ssh.Connect();

                    using var cmd = ssh.CreateCommand("if command -v mysqldump >/dev/null 2>&1; then echo READY; else echo MISSING; fi");
                    var output = (cmd.Execute() ?? string.Empty).Trim();
                    var hasMysqldump = string.Equals(output, "READY", StringComparison.OrdinalIgnoreCase);
                    var detail = hasMysqldump
                        ? "mysqldump found on remote server."
                        : "mysqldump was not found on remote server.";

                    var canRunDump = false;
                    var dumpDetails = "Remote dump test was not executed.";
                    if (hasMysqldump && !string.IsNullOrWhiteSpace(databaseName))
                    {
                        var dbHost = string.IsNullOrWhiteSpace(entry.Host) ? "127.0.0.1" : entry.Host;
                        var dbPort = entry.Port <= 0 ? 3306 : entry.Port;
                        var dbUser = string.IsNullOrWhiteSpace(entry.Username) ? "root" : entry.Username;
                        var passwordPrefix = string.IsNullOrWhiteSpace(entry.Password)
                            ? string.Empty
                            : $"MYSQL_PWD={EscapeForShellLiteral(entry.Password)} ";
                        var dumpCheckCommand =
                            $"{passwordPrefix}mysqldump --single-transaction --quick --routines --triggers --no-data --host={EscapeForShellLiteral(dbHost)} --port={dbPort} --user={EscapeForShellLiteral(dbUser)} --databases {EscapeForShellLiteral(databaseName)} > /dev/null 2>&1";
                        using var dumpCheck = ssh.CreateCommand(dumpCheckCommand);
                        dumpCheck.CommandTimeout = TimeSpan.FromMinutes(2);
                        dumpCheck.Execute();
                        canRunDump = dumpCheck.ExitStatus == 0;
                        dumpDetails = canRunDump
                            ? "Dry-run mysqldump (--no-data) succeeded."
                            : $"Dry-run mysqldump failed (exit {dumpCheck.ExitStatus}).";
                    }

                    return new RemoteSshProbeResult(
                        true,
                        hasMysqldump,
                        $"SSH connected successfully to {sshUser}@{sshHost}:{sshPort}.",
                        detail,
                        canRunDump,
                        dumpDetails);
                }
                catch (SshAuthenticationException ex)
                {
                    return new RemoteSshProbeResult(
                        false,
                        false,
                        ex.Message,
                        "mysqldump check skipped because SSH login failed.",
                        false,
                        "mysqldump check skipped because SSH login failed.");
                }
                catch (Exception ex)
                {
                    return new RemoteSshProbeResult(
                        false,
                        false,
                        ex.Message,
                        "mysqldump check skipped because SSH probe failed.",
                        false,
                        "mysqldump check skipped because SSH probe failed.");
                }
            });
        }

        private static async Task<RemoteSshFileProbeResult> ProbeRemoteSshFileBuildAsync(ConnectionProfile profile, string databaseName, string remoteOutputDirectory)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var entry = DatabaseConnectionEntry.FromProfile(profile);
                    var sshHost = string.IsNullOrWhiteSpace(entry.SshHost) ? "127.0.0.1" : entry.SshHost;
                    var sshPort = entry.SshPort <= 0 ? 22 : entry.SshPort;
                    var sshUser = string.IsNullOrWhiteSpace(entry.SshUsername) ? "root" : entry.SshUsername;

                    var authMethods = new List<AuthenticationMethod>();
                    if (!string.IsNullOrWhiteSpace(entry.SshPrivateKeyPath) && File.Exists(entry.SshPrivateKeyPath))
                    {
                        var keyFile = new PrivateKeyFile(entry.SshPrivateKeyPath);
                        authMethods.Add(new PrivateKeyAuthenticationMethod(sshUser, keyFile));
                    }

                    if (!string.IsNullOrWhiteSpace(entry.SshPassword))
                    {
                        authMethods.Add(new PasswordAuthenticationMethod(sshUser, entry.SshPassword));
                    }

                    if (authMethods.Count == 0)
                    {
                        return new RemoteSshFileProbeResult(
                            false,
                            "No SSH authentication method is configured.",
                            false,
                            "mysqldump check skipped because SSH login failed.",
                            false,
                            "gzip check skipped because SSH login failed.",
                            false,
                            "Remote path check skipped because SSH login failed.",
                            false,
                            "Dry-run skipped because SSH login failed.");
                    }

                    var connectionInfo = new ConnectionInfo(sshHost, sshPort, sshUser, authMethods.ToArray());
                    using var ssh = new SshClient(connectionInfo);
                    ssh.Connect();

                    var hasMysqldump = CommandSuccess(ssh, "command -v mysqldump >/dev/null 2>&1");
                    var hasGzip = CommandSuccess(ssh, "command -v gzip >/dev/null 2>&1");

                    var normalizedOutput = string.IsNullOrWhiteSpace(remoteOutputDirectory)
                        ? "/tmp/gitdeploypro-backups"
                        : remoteOutputDirectory.Trim();
                    var pathProbe = EscapeForShellLiteral(normalizedOutput);
                    var writeProbeFile = EscapeForShellLiteral($"{normalizedOutput.TrimEnd('/')}/.gdp_write_probe_{Guid.NewGuid():N}");
                    var pathWritable = CommandSuccess(
                        ssh,
                        $"mkdir -p {pathProbe} >/dev/null 2>&1 && touch {writeProbeFile} >/dev/null 2>&1 && rm -f {writeProbeFile} >/dev/null 2>&1");

                    var dumpReady = false;
                    var dumpDetails = "Remote dry-run was not executed.";
                    if (hasMysqldump && hasGzip && pathWritable && !string.IsNullOrWhiteSpace(databaseName))
                    {
                        var dbHost = string.IsNullOrWhiteSpace(entry.Host) ? "127.0.0.1" : entry.Host;
                        var dbPort = entry.Port <= 0 ? 3306 : entry.Port;
                        var dbUser = string.IsNullOrWhiteSpace(entry.Username) ? "root" : entry.Username;
                        var passwordPrefix = string.IsNullOrWhiteSpace(entry.Password)
                            ? string.Empty
                            : $"MYSQL_PWD={EscapeForShellLiteral(entry.Password)} ";
                        var tmpSql = $"{normalizedOutput.TrimEnd('/')}/.gdp_probe_{Guid.NewGuid():N}.sql";
                        var tmpGz = tmpSql + ".gz";
                        var dryRunCommand =
                            $"{passwordPrefix}mysqldump --single-transaction --quick --routines --triggers --no-data --host={EscapeForShellLiteral(dbHost)} --port={dbPort} --user={EscapeForShellLiteral(dbUser)} --databases {EscapeForShellLiteral(databaseName)} --result-file={EscapeForShellLiteral(tmpSql)} && " +
                            $"gzip -1 -f {EscapeForShellLiteral(tmpSql)} && " +
                            $"rm -f {EscapeForShellLiteral(tmpGz)}";
                        using var dryRun = ssh.CreateCommand(dryRunCommand);
                        dryRun.CommandTimeout = TimeSpan.FromMinutes(2);
                        dryRun.Execute();
                        dumpReady = dryRun.ExitStatus == 0;
                        var stderr = string.IsNullOrWhiteSpace(dryRun.Error) ? string.Empty : dryRun.Error.Trim();
                        dumpDetails = dumpReady
                            ? "Dry-run remote file build succeeded."
                            : string.IsNullOrWhiteSpace(stderr)
                                ? $"Dry-run failed (exit {dryRun.ExitStatus})."
                                : $"Dry-run failed (exit {dryRun.ExitStatus}): {stderr}";
                    }

                    return new RemoteSshFileProbeResult(
                        true,
                        $"SSH connected successfully to {sshUser}@{sshHost}:{sshPort}.",
                        hasMysqldump,
                        hasMysqldump ? "mysqldump found on remote server." : "mysqldump was not found on remote server.",
                        hasGzip,
                        hasGzip ? "gzip found on remote server." : "gzip was not found on remote server.",
                        pathWritable,
                        pathWritable ? $"Remote path is writable: {normalizedOutput}" : $"Remote path is not writable: {normalizedOutput}",
                        dumpReady,
                        dumpDetails);
                }
                catch (SshAuthenticationException ex)
                {
                    return new RemoteSshFileProbeResult(
                        false,
                        ex.Message,
                        false,
                        "mysqldump check skipped because SSH login failed.",
                        false,
                        "gzip check skipped because SSH login failed.",
                        false,
                        "Remote path check skipped because SSH login failed.",
                        false,
                        "Dry-run skipped because SSH login failed.");
                }
                catch (Exception ex)
                {
                    return new RemoteSshFileProbeResult(
                        false,
                        ex.Message,
                        false,
                        "mysqldump check skipped because SSH probe failed.",
                        false,
                        "gzip check skipped because SSH probe failed.",
                        false,
                        "Remote path check skipped because SSH probe failed.",
                        false,
                        "Dry-run skipped because SSH probe failed.");
                }
            });
        }

        private static bool CommandSuccess(SshClient ssh, string commandText)
        {
            using var cmd = ssh.CreateCommand(commandText);
            cmd.CommandTimeout = TimeSpan.FromSeconds(20);
            cmd.Execute();
            return cmd.ExitStatus == 0;
        }

        private static string EscapeForShellLiteral(string value)
        {
            var safe = value ?? string.Empty;
            return $"'{safe.Replace("'", "'\"'\"'")}'";
        }

        private sealed record RemoteSshProbeResult(
            bool SshAuthenticated,
            bool MysqldumpAvailable,
            string SshDetails,
            string MysqldumpDetails,
            bool DumpCommandReady,
            string DumpReadinessDetails);

        private sealed record RemoteSshFileProbeResult(
            bool SshAuthenticated,
            string SshDetails,
            bool MysqldumpAvailable,
            string MysqldumpDetails,
            bool GzipAvailable,
            string GzipDetails,
            bool RemotePathWritable,
            string RemotePathDetails,
            bool DumpCommandReady,
            string DumpReadinessDetails);

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

                var count = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Count(IsKnownArtifactFile);
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

            if (string.IsNullOrWhiteSpace(schedule.RemoteOutputDirectory))
            {
                schedule.RemoteOutputDirectory = "/tmp/gitdeploypro-backups";
            }
            EnsureLocalValidationDefaults(schedule);

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

        private static void EnsureLocalValidationDefaults(BackupSchedule schedule)
        {
            if (schedule == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(schedule.LocalValidationHost))
            {
                schedule.LocalValidationHost = "127.0.0.1";
            }

            if (schedule.LocalValidationPort <= 0)
            {
                schedule.LocalValidationPort = 3306;
            }

            if (string.IsNullOrWhiteSpace(schedule.LocalValidationUsername))
            {
                schedule.LocalValidationUsername = "root";
            }

            if (string.IsNullOrWhiteSpace(schedule.LocalValidationCharset))
            {
                schedule.LocalValidationCharset = "auto";
            }

            if (string.IsNullOrWhiteSpace(schedule.LocalValidationCollation))
            {
                schedule.LocalValidationCollation = "auto";
            }

            if (string.IsNullOrWhiteSpace(schedule.LocalValidationDatabaseName))
            {
                var seed = string.IsNullOrWhiteSpace(schedule.DatabaseName) ? "project" : schedule.DatabaseName;
                var safe = new string(seed.Where(ch => char.IsLetterOrDigit(ch) || ch == '_').ToArray());
                if (string.IsNullOrWhiteSpace(safe))
                {
                    safe = "project";
                }

                schedule.LocalValidationDatabaseName = $"gdp_local_{safe.ToLowerInvariant()}";
            }
        }

        private void UpdateLocalValidationStatus(string message, bool isError = false, bool isInfo = false)
        {
            LocalValidationStatus = message;
            if (isError)
            {
                LocalValidationStatusBrush = StatusErrorBrush;
            }
            else if (isInfo)
            {
                LocalValidationStatusBrush = StatusInfoBrush;
            }
            else
            {
                LocalValidationStatusBrush = StatusSuccessBrush;
            }
        }

        private void InvalidateDatabaseCache(string statusMessage)
        {
            Interlocked.Increment(ref _databaseRefreshVersion);
            DatabaseNames.Clear();
            if (!string.IsNullOrWhiteSpace(statusMessage))
            {
                UpdateDatabaseStatus(statusMessage, isInfo: true);
            }
        }

        private bool IsDatabaseRefreshCurrent(int requestVersion, BackupSchedule? scheduleSnapshot, string profileIdSnapshot)
        {
            return requestVersion == _databaseRefreshVersion
                   && ReferenceEquals(scheduleSnapshot, SelectedSchedule)
                   && string.Equals(profileIdSnapshot, SelectedSchedule?.ConnectionProfileId, StringComparison.Ordinal);
        }

        private async Task RefreshDatabaseListAsync(bool userRequested = false)
        {
            var requestVersion = Interlocked.Increment(ref _databaseRefreshVersion);
            if (IsDbLoading)
            {
                _databaseRefreshPending = true;
                _databaseRefreshPendingUserRequested |= userRequested;
                return;
            }

            var scheduleSnapshot = SelectedSchedule;
            var profileIdSnapshot = scheduleSnapshot?.ConnectionProfileId ?? string.Empty;

            if (ConnectionProfiles.Count == 0)
            {
                if (IsDatabaseRefreshCurrent(requestVersion, scheduleSnapshot, profileIdSnapshot))
                {
                    DatabaseNames.Clear();
                    UpdateDatabaseStatus("No database connections configured yet.", isInfo: true);
                }
                return;
            }

            if (scheduleSnapshot == null)
            {
                if (IsDatabaseRefreshCurrent(requestVersion, null, profileIdSnapshot))
                {
                    DatabaseNames.Clear();
                    UpdateDatabaseStatus("Select a schedule to load databases.", isInfo: true);
                }
                return;
            }

            var profile = ConnectionProfiles.FirstOrDefault(p => string.Equals(p.Id, profileIdSnapshot, StringComparison.Ordinal));
            if (profile == null)
            {
                if (IsDatabaseRefreshCurrent(requestVersion, scheduleSnapshot, profileIdSnapshot))
                {
                    DatabaseNames.Clear();
                    UpdateDatabaseStatus("The selected connection profile is no longer available.", isError: true);
                }
                return;
            }

            if (profile.DbType == DatabaseType.None)
            {
                if (IsDatabaseRefreshCurrent(requestVersion, scheduleSnapshot, profileIdSnapshot))
                {
                    DatabaseNames.Clear();
                    UpdateDatabaseStatus("This connection does not have database details configured.", isInfo: true);
                }
                return;
            }

            try
            {
                IsDbLoading = true;
                if (IsDatabaseRefreshCurrent(requestVersion, scheduleSnapshot, profileIdSnapshot))
                {
                    UpdateDatabaseStatus("Connecting to database server…", isInfo: true);
                    DatabaseNames.Clear();
                }

                var info = BuildDatabaseConnectionInfo(profile, scheduleSnapshot.DatabaseName);
                await using var dbClient = new DatabaseClient();
                await dbClient.ConnectAsync(info);
                var dbs = await dbClient.GetDatabasesAsync();

                if (!IsDatabaseRefreshCurrent(requestVersion, scheduleSnapshot, profileIdSnapshot))
                {
                    return;
                }

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
                    if (string.IsNullOrWhiteSpace(scheduleSnapshot.DatabaseName) ||
                        !DatabaseNames.Any(db => string.Equals(db, scheduleSnapshot.DatabaseName, StringComparison.OrdinalIgnoreCase)))
                    {
                        scheduleSnapshot.DatabaseName = DatabaseNames[0];
                    }
                }
            }
            catch (Exception ex)
            {
                if (!IsDatabaseRefreshCurrent(requestVersion, scheduleSnapshot, profileIdSnapshot))
                {
                    return;
                }

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
                if (_databaseRefreshPending)
                {
                    var pendingUserRequested = _databaseRefreshPendingUserRequested;
                    _databaseRefreshPending = false;
                    _databaseRefreshPendingUserRequested = false;
                    await RefreshDatabaseListAsync(pendingUserRequested);
                }
            }
        }

        private DatabaseConnectionInfo BuildDatabaseConnectionInfo(ConnectionProfile profile, string? databaseName = null)
        {
            var entry = DatabaseConnectionEntry.FromProfile(profile);
            var resolvedDatabase = databaseName;
            if (string.IsNullOrWhiteSpace(resolvedDatabase) &&
                SelectedSchedule != null &&
                !string.IsNullOrWhiteSpace(SelectedSchedule.DatabaseName))
            {
                resolvedDatabase = SelectedSchedule.DatabaseName;
            }

            if (!string.IsNullOrWhiteSpace(resolvedDatabase))
            {
                entry.DatabaseName = resolvedDatabase;
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


        private async void TestLocalValidationProfile_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedSchedule == null)
            {
                return;
            }

            if (IsLocalValidationBusy)
            {
                return;
            }

            if (!SelectedSchedule.EnableLocalRestoreValidation)
            {
                UpdateLocalValidationStatus("Enable localhost restore validation first.", isInfo: true);
                return;
            }

            IsLocalValidationBusy = true;
            try
            {
                EnsureLocalValidationDefaults(SelectedSchedule);
                UpdateLocalValidationStatus("Testing localhost connection and checking configured DB …", isInfo: true);

                var inspect = await _restoreValidationService.InspectConfiguredDatabaseAsync(SelectedSchedule, CancellationToken.None);
                if (!inspect.IsSuccess)
                {
                    UpdateLocalValidationStatus($"Localhost validation failed: {inspect.Message}", isError: true);
                    AddRunLog($"Localhost validation test failed: {inspect.Message}", true);
                    return;
                }

                if (!inspect.DatabaseExists && !string.IsNullOrWhiteSpace(inspect.ConfiguredDatabase))
                {
                    var confirm = ModernMessageBox.ShowWithResult(
                        $"Database '{inspect.ConfiguredDatabase}' was not found.\nCreate it now with charset '{inspect.EffectiveCharset}' and collation '{inspect.EffectiveCollation}'?",
                        "Create local validation database",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question,
                        "Create",
                        "Skip");

                    if (confirm == MessageBoxResult.Yes)
                    {
                        var ensureDb = await _restoreValidationService.EnsureConfiguredDatabaseAsync(SelectedSchedule, CancellationToken.None);
                        if (!ensureDb.IsSuccess)
                        {
                            UpdateLocalValidationStatus($"Localhost validation failed: {ensureDb.Message}", isError: true);
                            AddRunLog($"Localhost validation DB create failed: {ensureDb.Message}", true);
                            return;
                        }
                    }
                    else
                    {
                        UpdateLocalValidationStatus("Validation DB creation skipped by operator.", isInfo: true);
                        AddRunLog("Localhost validation DB creation skipped.");
                        return;
                    }
                }

                var probe = await _restoreValidationService.ProbeEnvironmentAsync(SelectedSchedule, CancellationToken.None, ensureConfiguredDatabase: false);
                if (probe.IsSuccess)
                {
                    if (!string.IsNullOrWhiteSpace(probe.EffectiveCharset))
                    {
                        SelectedSchedule.LocalValidationCharset = probe.EffectiveCharset;
                    }

                    if (!string.IsNullOrWhiteSpace(probe.EffectiveCollation))
                    {
                        SelectedSchedule.LocalValidationCollation = probe.EffectiveCollation;
                    }

                    UpdateLocalValidationStatus(probe.Message);
                    AddRunLog("Localhost validation test passed.");
                }
                else
                {
                    UpdateLocalValidationStatus($"Localhost validation failed: {probe.Message}", isError: true);
                    AddRunLog($"Localhost validation test failed: {probe.Message}", true);
                }
            }
            finally
            {
                IsLocalValidationBusy = false;
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
            _currentProgressStage = "Preparing";
            OnPropertyChanged(nameof(ProgressSummary));
        }

        private string GetNonTableProgressSummary()
        {
            if (!IsBackupRunning &&
                string.Equals(_currentProgressStage, "Failed", StringComparison.Ordinal))
            {
                return "Failed";
            }

            if (!IsBackupRunning &&
                string.Equals(_currentProgressStage, "Cancelled", StringComparison.Ordinal))
            {
                return "Cancelled";
            }

            if (!IsBackupRunning &&
                string.Equals(_currentProgressStage, "Completed", StringComparison.Ordinal))
            {
                return "Completed";
            }

            if (!IsBackupRunning)
            {
                return "Ready";
            }

            return _currentProgressStage switch
            {
                "Connecting" => "Connecting …",
                "Preparing" => "Analyzing structure …",
                "ExternalDumpStart" or "RemoteDumpStart" => "Starting dump stream …",
                "ExternalDumpStreaming" or "RemoteDumpStreaming" => "Streaming backup data …",
                "RemoteFilePrepare" => "Preparing remote build …",
                "RemoteFileDumping" => "Building SQL file on server …",
                "RemoteFileCompressing" => "Compressing artifact on server …",
                "RemoteFileFinalizing" or "RemoteFileBuilt" => "Finalizing remote artifact …",
                "RemoteFileDownloadStart" or "RemoteFileDownloading" => "Downloading remote artifact …",
                "RemoteFileDownloadVerified" => "Download verified …",
                "ValidationPrepare" => "Preparing localhost validation …",
                "ValidationImport" => "Importing into localhost validation DB …",
                "ValidationCheck" => "Running localhost validation checks …",
                "ValidationCleanup" => "Cleaning localhost validation resources …",
                "ValidationDone" => "Localhost validation passed …",
                "ValidationWarning" => "Localhost validation warning …",
                "Finalizing" => "Finalizing artifact …",
                "Compressing" => "Compressing backup …",
                "Encrypting" => "Encrypting file …",
                "Completed" => "Completed",
                "Failed" => "Failed",
                "Cancelled" => "Cancelled",
                _ => "Preparing backup …"
            };
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

            if (!string.IsNullOrWhiteSpace(update.Stage) &&
                !string.Equals(_currentProgressStage, update.Stage, StringComparison.Ordinal))
            {
                _currentProgressStage = update.Stage;
                OnPropertyChanged(nameof(ProgressSummary));
            }

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

        private void StepButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button button ||
                button.Tag is not string tag ||
                !int.TryParse(tag, out var targetStep))
            {
                return;
            }

            _ = NavigateToStepAsync(targetStep, interactive: true);
        }

        private async void NextStep_Click(object sender, RoutedEventArgs e)
        {
            await NavigateToStepAsync(CurrentWizardStep + 1, interactive: true);
        }

        private async void PreviousStep_Click(object sender, RoutedEventArgs e)
        {
            await NavigateToStepAsync(CurrentWizardStep - 1, interactive: false);
        }

        private async void RefreshReadiness_Click(object sender, RoutedEventArgs e)
        {
            await RunReadinessChecksAsync(interactive: true);
        }

        private async Task NavigateToStepAsync(int targetStep, bool interactive)
        {
            targetStep = Math.Max(1, Math.Min(4, targetStep));
            if (targetStep == CurrentWizardStep)
            {
                return;
            }

            if (targetStep > CurrentWizardStep)
            {
                if (CurrentWizardStep == 1 && !ValidateStepOne(out var stepOneMessage))
                {
                    if (interactive) ModernMessageBox.Show(stepOneMessage, "Backup Scheduler", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (CurrentWizardStep == 2 && !ValidateStepTwo(out var stepTwoMessage))
                {
                    if (interactive) ModernMessageBox.Show(stepTwoMessage, "Backup Scheduler", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (CurrentWizardStep == 3)
                {
                    if (_readinessDirty)
                    {
                        await RunReadinessChecksAsync(interactive);
                    }

                    if (!ValidateStepThree(out var readinessMessage))
                    {
                        if (interactive) ModernMessageBox.Show(readinessMessage, "Backup Scheduler", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }

                if (!TryPersistSchedules(showConfirmation: false))
                {
                    return;
                }
            }

            CurrentWizardStep = targetStep;
            if (CurrentWizardStep == 3 && _readinessDirty)
            {
                await RunReadinessChecksAsync(interactive: false);
            }

            UpdateWizardUiState();
        }

        private void AddSchedule_Click(object sender, RoutedEventArgs e)
        {
            var schedule = new BackupSchedule
            {
                Name = $"Backup plan {Schedules.Count + 1}",
                Enabled = true,
                ConnectionProfileId = ConnectionProfiles.FirstOrDefault()?.Id ?? string.Empty,
                OutputDirectory = GetDefaultOutputDirectory(),
                DatabaseName = ConnectionProfiles.FirstOrDefault()?.DbName ?? string.Empty,
                RemoteOutputDirectory = "/tmp/gitdeploypro-backups",
                RemoteDownloadPolicy = RemoteArtifactDownloadPolicy.ManualReference,
                DeleteRemoteArtifactAfterDownload = false,
                EnableLocalRestoreValidation = false,
                LocalValidationHost = "127.0.0.1",
                LocalValidationPort = 3306,
                LocalValidationDatabaseName = string.Empty,
                LocalValidationUsername = "root",
                LocalValidationPassword = string.Empty,
                LocalValidationCharset = "auto",
                LocalValidationCollation = "auto"
            };
            EnsureLocalValidationDefaults(schedule);

            Schedules.Add(schedule);
            SelectedSchedule = schedule;
            RefreshNextRunEstimate(schedule);
            HasUnsavedScheduleChanges = true;
            CurrentWizardStep = 1;
            UpdateWizardUiState();
        }

        private void DuplicateSchedule_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedSchedule == null) return;
            var clone = CloneSchedule(SelectedSchedule);
            clone.Name += " (copy)";
            Schedules.Add(clone);
            SelectedSchedule = clone;
            RefreshNextRunEstimate(clone);
            HasUnsavedScheduleChanges = true;
            CurrentWizardStep = 1;
            UpdateWizardUiState();
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
                HasUnsavedScheduleChanges = false;
                if (Schedules.Count > 0)
                {
                    SelectedSchedule = Schedules[0];
                }
                else
                {
                    SelectedSchedule = null;
                }

                CurrentWizardStep = 1;
                UpdateWizardUiState();
            }
        }

        private void SaveSchedules_Click(object sender, RoutedEventArgs e)
        {
            TryPersistSchedules(showConfirmation: true);
        }

        private bool TryPersistSchedules(bool showConfirmation)
        {
            try
            {
                BackupScheduleStore.SaveSchedules(Schedules);
                if (showConfirmation)
                {
                    ModernMessageBox.Show("Schedules saved successfully.", "Backup Scheduler", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                HasUnsavedScheduleChanges = false;
                return true;
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Could not save schedules:\n{ex.Message}", "Backup Scheduler", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
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

            if (CurrentWizardStep != 4)
            {
                ModernMessageBox.Show("Complete steps 1 to 4 before running backup.", "Backup Scheduler", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (IsBackupRunning)
            {
                ModernMessageBox.Show("A backup is already running.", "Backup Scheduler", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_taskMonitor.IsScheduleRunning(SelectedSchedule.Id, SelectedSchedule.ConnectionProfileId))
            {
                ModernMessageBox.Show("This schedule is already running in another task.", "Backup Scheduler", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_readinessDirty || ReadinessChecks.Count == 0)
            {
                await RunReadinessChecksAsync(interactive: true);
            }

            if (!ValidateStepThree(out var readinessMessage))
            {
                ModernMessageBox.Show(readinessMessage, "Backup Scheduler", MessageBoxButton.OK, MessageBoxImage.Warning);
                CurrentWizardStep = 3;
                UpdateWizardUiState();
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
                _currentProgressStage = "Completed";
                OnPropertyChanged(nameof(ProgressSummary));
                var hasLocalArtifact = result.HasLocalArtifact && !string.IsNullOrWhiteSpace(result.OutputPath) && File.Exists(result.OutputPath);
                var health = hasLocalArtifact
                    ? _healthService.Verify(result.OutputPath, result.IsCompressed)
                    : new BackupHealthReport
                    {
                        IsHealthy = true,
                        Details = "Remote artifact reference saved. Local health verification skipped."
                    };
                if (health.IsHealthy)
                {
                    AddRunLog("Health check passed.");
                }
                else
                {
                    AddRunLog($"Health check issues: {health.Details}", true);
                }

                var validationResult = BackupRestoreValidationResult.Skipped("Local restore validation is disabled.");
                if (SelectedSchedule.EnableLocalRestoreValidation)
                {
                    if (!BackupRestoreValidationService.TryBuildLocalConnectionInfo(SelectedSchedule, out _, out var validationConfigReason))
                    {
                        validationResult = BackupRestoreValidationResult.Warning($"Validation warning: {validationConfigReason}");
                    }
                    else if (!hasLocalArtifact)
                    {
                        validationResult = BackupRestoreValidationResult.Warning("Validation warning: no local artifact is available for localhost restore validation.");
                    }
                    else
                    {
                        AddRunLog("Running localhost restore validation …");
                        validationResult = await _restoreValidationService.ValidateAsync(
                            SelectedSchedule,
                            result.OutputPath,
                            progress,
                            _backupCts?.Token ?? CancellationToken.None);
                    }

                    AddRunLog(validationResult.Message, validationResult.IsWarning);
                }

                if (validationResult.IsAttempted && validationResult.Passed && hasLocalArtifact)
                {
                    if (BackupArtifactNaming.TryMarkAsVerified(result.OutputPath, out var verifiedPath, out var verifyMessage))
                    {
                        result.OutputPath = verifiedPath;
                        if (File.Exists(verifiedPath))
                        {
                            result.BytesWritten = new FileInfo(verifiedPath).Length;
                        }

                        AddRunLog(verifyMessage);
                    }
                    else
                    {
                        AddRunLog("Could not add verify tag to artifact filename.", true);
                    }
                }

                var finalSizeLabel = FormatBytes(result.BytesWritten);

                if (_currentTaskHandle != null)
                {
                    var validationTag = validationResult.IsWarning ? " with validation warning" : string.Empty;
                    _taskMonitor.CompleteTask(_currentTaskHandle.TaskId, $"[{SelectedSchedule.Name}] Backup completed ({finalSizeLabel}){validationTag}.");
                }
                historyEntry.CompletedUtc = DateTime.UtcNow;
                historyEntry.Success = true;
                historyEntry.OutputPath = result.OutputPath;
                historyEntry.FileSizeBytes = result.BytesWritten;
                historyEntry.Sha256 = result.Sha256;
                historyEntry.IsRemoteArtifact = result.IsRemoteArtifact;
                historyEntry.HasLocalArtifact = result.HasLocalArtifact;
                historyEntry.RemoteArtifactPath = result.RemoteArtifactPath;
                historyEntry.RemoteArtifactSizeBytes = result.RemoteArtifactBytes;
                historyEntry.RemoteArtifactSha256 = result.RemoteArtifactSha256;
                historyEntry.DownloadPolicy = SelectedSchedule.RemoteDownloadPolicy.ToString();
                historyEntry.RemoteArtifactDeletedAfterDownload = result.RemoteArtifactDeleted;
                historyEntry.RemoteCleanupMessage = result.RemoteCleanupMessage;
                historyEntry.HealthPassed = health.IsHealthy;
                historyEntry.HealthDetails = health.Details;
                historyEntry.RestoreValidationEnabled = SelectedSchedule.EnableLocalRestoreValidation;
                historyEntry.RestoreValidationAttempted = validationResult.IsAttempted;
                historyEntry.RestoreValidationPassed = validationResult.Passed;
                historyEntry.RestoreValidationMessage = validationResult.Message;
                historyEntry.RestoreValidationDatabase = validationResult.ValidationDatabaseName;
                var healthLabel = health.IsHealthy ? "passed" : "FAILED";
                var artifactLabel = result.IsRemoteArtifact
                    ? (result.HasLocalArtifact ? "Remote build + local download" : "Remote build (manual reference)")
                    : "Local artifact";
                var cleanupTag = result.RemoteArtifactDeleted ? " · remote cleaned" : string.Empty;
                var validationTagMessage = validationResult.IsWarning
                    ? $" · {validationResult.Message}"
                    : (validationResult.IsAttempted && validationResult.Passed ? " · Validation passed" : string.Empty);
                historyEntry.Message = $"Manual run ({finalSizeLabel}) · {artifactLabel}{cleanupTag} · Health {healthLabel}{validationTagMessage}.";
                BackupHistoryStore.AddEntry(historyEntry);
                if (validationResult.IsWarning)
                {
                    _notificationService.ShowToast("Backup finished with validation warning", $"{SelectedSchedule.Name}: {validationResult.Message}");
                }
                else
                {
                    _notificationService.ShowToast("Backup finished", $"{SelectedSchedule.Name} completed.");
                }
                ReloadHistory();
                SelectedSchedule.LastRunUtc = DateTime.UtcNow;
                RefreshNextRunEstimate(SelectedSchedule);
                BackupScheduleStore.SaveSchedules(Schedules);
                OnPropertyChanged(nameof(SelectedScheduleSummary));
            }
            catch (OperationCanceledException)
            {
                AddRunLog("Backup canceled by user.", true);
                _currentProgressStage = "Cancelled";
                OnPropertyChanged(nameof(ProgressSummary));
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
                _currentProgressStage = "Failed";
                OnPropertyChanged(nameof(ProgressSummary));
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
                RefreshProjectBackupFiles(silent: true);
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
                OnPropertyChanged(nameof(ArtifactFilesPathLabel));
                RefreshProjectBackupFiles();
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

        private string ResolveScheduleOutputDirectory(BackupSchedule schedule)
        {
            if (!string.IsNullOrWhiteSpace(schedule.OutputDirectory))
            {
                return schedule.OutputDirectory.Trim();
            }

            return GetDefaultOutputDirectory();
        }

        private string ResolveScheduleArtifactsRoot(BackupSchedule schedule)
        {
            var scheduleRoot = DatabaseBackupService.GetScheduleRoot(schedule);
            if (!string.IsNullOrWhiteSpace(scheduleRoot))
            {
                return scheduleRoot;
            }

            return ResolveScheduleOutputDirectory(schedule);
        }

        private void RefreshProjectBackupFiles(bool silent = false)
        {
            ProjectBackupFiles.Clear();
            SelectedProjectBackupFile = null;

            if (SelectedSchedule == null)
            {
                ArtifactFilesStatus = "Select a schedule to load backup files.";
                return;
            }

            var outputDirectory = ResolveScheduleOutputDirectory(SelectedSchedule);
            var scheduleRoot = ResolveScheduleArtifactsRoot(SelectedSchedule);

            try
            {
                var candidateRoots = new[] { scheduleRoot, outputDirectory }
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var existingRoots = candidateRoots
                    .Where(Directory.Exists)
                    .ToList();

                if (existingRoots.Count == 0)
                {
                    ArtifactFilesStatus = "No backup folder found yet for this project path.";
                    return;
                }

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var files = new List<FileInfo>();
                foreach (var root in existingRoots)
                {
                    foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        if (!IsKnownArtifactFile(path))
                        {
                            continue;
                        }

                        if (!seen.Add(path))
                        {
                            continue;
                        }

                        files.Add(new FileInfo(path));
                    }
                }

                var items = files
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .Take(200)
                    .Select(BuildProjectBackupFileItem)
                    .ToList();

                foreach (var item in items)
                {
                    ProjectBackupFiles.Add(item);
                }

                ArtifactFilesStatus = items.Count == 0
                    ? "No backup artifacts found in schedule folders."
                    : $"{items.Count} artifact(s) loaded.";
            }
            catch (Exception ex)
            {
                ArtifactFilesStatus = "Unable to scan backup files.";
                if (!silent)
                {
                    AddRunLog($"Backup artifacts scan failed: {ex.Message}", true);
                }
            }
        }

        private static bool IsKnownArtifactFile(string filePath)
        {
            var fileName = Path.GetFileName(filePath);
            return KnownArtifactTails.Any(tail => fileName.EndsWith(tail, StringComparison.OrdinalIgnoreCase));
        }

        private static (string baseName, string tailExtension) SplitKnownArtifactTail(string fileName)
        {
            foreach (var tail in KnownArtifactTails)
            {
                if (fileName.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
                {
                    return (fileName[..^tail.Length], tail);
                }
            }

            var extension = Path.GetExtension(fileName);
            if (string.IsNullOrWhiteSpace(extension))
            {
                return (fileName, string.Empty);
            }

            return (fileName[..^extension.Length], extension);
        }

        private static bool TryParseArtifactBaseName(string baseName, out string databaseName, out DateTime capturedAt)
        {
            databaseName = baseName;
            capturedAt = default;

            var match = ArtifactNameRegex.Match(baseName);
            if (!match.Success)
            {
                return false;
            }

            var stamp = match.Groups["stamp"].Value;
            if (!DateTime.TryParseExact(stamp, "yy_MM_dd_HH_mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out capturedAt))
            {
                return false;
            }

            databaseName = match.Groups["db"].Value;
            return true;
        }

        private static ProjectBackupFileItem BuildProjectBackupFileItem(FileInfo file)
        {
            var (baseName, tailExtension) = SplitKnownArtifactTail(file.Name);
            var isVerified = false;
            if (baseName.EndsWith("_verify", StringComparison.OrdinalIgnoreCase))
            {
                baseName = baseName[..^"_verify".Length];
                isVerified = true;
            }
            else
            {
                var verifiedSequence = VerifiedWithSequenceRegex.Match(baseName);
                if (verifiedSequence.Success)
                {
                    baseName = verifiedSequence.Groups["core"].Value;
                    isVerified = true;
                }
            }

            var databaseName = baseName;
            var capturedAtDisplay = "-";
            if (TryParseArtifactBaseName(baseName, out var parsedDatabaseName, out var capturedAt))
            {
                databaseName = parsedDatabaseName;
                capturedAtDisplay = capturedAt.ToString("yyyy-MM-dd HH:mm");
            }

            var artifactType = string.IsNullOrWhiteSpace(tailExtension)
                ? Path.GetExtension(file.Name).TrimStart('.').ToUpperInvariant()
                : tailExtension.TrimStart('.').ToUpperInvariant();

            return new ProjectBackupFileItem
            {
                FileName = file.Name,
                DatabaseName = string.IsNullOrWhiteSpace(databaseName) ? "-" : databaseName,
                CapturedAt = capturedAtDisplay,
                ArtifactType = string.IsNullOrWhiteSpace(artifactType) ? "-" : artifactType,
                VerifiedLabel = isVerified ? "Yes" : "No",
                SizeLabel = FormatBytes(file.Length),
                LastModified = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                FullPath = file.FullName,
                IsVerified = isVerified
            };
        }

        private void OpenArtifactsModal_Click(object sender, RoutedEventArgs e)
        {
            RefreshProjectBackupFiles();
            var window = new BackupArtifactsWindow(this)
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
        }

        private void OpenArtifactExplorer_Click(object sender, RoutedEventArgs e)
        {
            OpenArtifactExplorerCore();
        }

        public void RefreshArtifactsForWindow()
        {
            RefreshProjectBackupFiles();
        }

        public void OpenArtifactExplorerFromWindow()
        {
            OpenArtifactExplorerCore();
        }

        private void OpenArtifactExplorerCore()
        {
            if (SelectedSchedule == null)
            {
                ModernMessageBox.Show("Select a schedule first.", "Backup Scheduler", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var targetPath = ResolveScheduleArtifactsRoot(SelectedSchedule);
            try
            {
                Directory.CreateDirectory(targetPath);

                var arguments = SelectedProjectBackupFile != null && File.Exists(SelectedProjectBackupFile.FullPath)
                    ? $"/select,\"{SelectedProjectBackupFile.FullPath}\""
                    : $"\"{targetPath}\"";

                var info = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = arguments,
                    UseShellExecute = true
                };
                Process.Start(info);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Unable to open File Explorer:\n{ex.Message}", "Backup Scheduler", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenConnectionManager_Click(object sender, RoutedEventArgs e)
        {
            var manager = new ConnectionManagerWindow
            {
                Owner = Window.GetWindow(this)
            };

            manager.ShowDialog();
            LoadConnectionProfiles();

            if (SelectedSchedule != null)
            {
                EnsureScheduleUsesAvailableConnection(SelectedSchedule);
                _ = RefreshDatabaseListAsync();
            }
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
                EncryptAtRest = source.EncryptAtRest,
                CompressionFormat = source.CompressionFormat,
                RetentionCount = source.RetentionCount,
                BackupMode = source.BackupMode,
                RemoteDownloadPolicy = source.RemoteDownloadPolicy,
                RemoteOutputDirectory = source.RemoteOutputDirectory,
                DeleteRemoteArtifactAfterDownload = source.DeleteRemoteArtifactAfterDownload,
                EnableLocalRestoreValidation = source.EnableLocalRestoreValidation,
                LocalValidationProfileId = source.LocalValidationProfileId,
                LocalValidationHost = source.LocalValidationHost,
                LocalValidationPort = source.LocalValidationPort,
                LocalValidationDatabaseName = source.LocalValidationDatabaseName,
                LocalValidationUsername = source.LocalValidationUsername,
                LocalValidationPassword = source.LocalValidationPassword,
                LocalValidationCharset = source.LocalValidationCharset,
                LocalValidationCollation = source.LocalValidationCollation
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

            BackupScheduleTimelineService.RecalculateNextRun(schedule, DateTime.UtcNow);
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

        public class ReadinessCheckItem
        {
            public string StatusLabel { get; }
            public string Title { get; }
            public string Details { get; }
            public string Hint { get; }
            public MediaBrush StatusBrush { get; }
            public bool IsFailure => string.Equals(StatusLabel, "Fail", StringComparison.OrdinalIgnoreCase);

            public ReadinessCheckItem(string statusLabel, string title, string details, string hint, MediaBrush statusBrush)
            {
                StatusLabel = statusLabel;
                Title = title;
                Details = details;
                Hint = hint;
                StatusBrush = statusBrush;
            }

            public static ReadinessCheckItem Pass(string title, string details, string hint)
                => new(
                    "Pass",
                    title,
                    details,
                    hint,
                    ResolveThemeBrush("Status.Success", MediaBrushes.LightGreen));

            public static ReadinessCheckItem Warning(string title, string details, string hint)
                => new(
                    "Warning",
                    title,
                    details,
                    hint,
                    ResolveThemeBrush("Status.Warning", MediaBrushes.Goldenrod));

            public static ReadinessCheckItem Fail(string title, string details, string hint)
                => new(
                    "Fail",
                    title,
                    details,
                    hint,
                    ResolveThemeBrush("Status.Error", MediaBrushes.OrangeRed));
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

        public class RemoteDownloadPolicyOption
        {
            public RemoteArtifactDownloadPolicy Policy { get; }
            public string Label { get; }

            public RemoteDownloadPolicyOption(RemoteArtifactDownloadPolicy policy, string label)
            {
                Policy = policy;
                Label = label;
            }

            public override string ToString() => Label;
        }

        public class ProjectBackupFileItem
        {
            public string FileName { get; init; } = string.Empty;
            public string DatabaseName { get; init; } = string.Empty;
            public string CapturedAt { get; init; } = string.Empty;
            public string ArtifactType { get; init; } = string.Empty;
            public string VerifiedLabel { get; init; } = string.Empty;
            public string SizeLabel { get; init; } = string.Empty;
            public string LastModified { get; init; } = string.Empty;
            public string FullPath { get; init; } = string.Empty;
            public bool IsVerified { get; init; }
        }

    }
}

