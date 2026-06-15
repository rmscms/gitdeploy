using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GitDeployPro.Controls;
using GitDeployPro.Models;
using GitDeployPro.Services;
using MahApps.Metro.Controls;

namespace GitDeployPro.Windows
{
    public partial class BackupIntegritySamplingWindow : MetroWindow, INotifyPropertyChanged
    {
        private readonly BackupHistoryEntry _sourceEntry;
        private readonly BackupRestoreValidationService _restoreValidationService = new();
        private readonly BackupIntegritySamplingService _integritySamplingService = new();
        private BackupHistoryEntry? _latestExistingBackupEntry;
        private BackupSchedule? _sourceSchedule;
        private BackupIntegrityTableSample? _selectedTableSample;
        private CancellationTokenSource? _selectedRowLoadCts;
        private bool _isImportRunning;
        private bool _isRefreshRunning;
        private bool _importProgressVisible;
        private bool _isImportProgressIndeterminate = true;
        private double _importProgressValue;
        private double _importProgressMaximum = 100;
        private string _importStatusLine = "Ready.";

        public BackupIntegritySamplingWindow(BackupHistoryEntry sourceEntry, BackupIntegritySamplingSnapshot? liveSnapshot = null)
        {
            if (sourceEntry == null)
            {
                throw new ArgumentNullException(nameof(sourceEntry));
            }

            _sourceEntry = sourceEntry;
            InitializeComponent();
            DataContext = this;

            try
            {
                _latestExistingBackupEntry = FindLatestExistingBackupEntry(sourceEntry);
                _sourceSchedule = ResolveScheduleForEntry(_latestExistingBackupEntry ?? sourceEntry);

                var displayEntry = _latestExistingBackupEntry ?? sourceEntry;
                var snapshot = liveSnapshot ?? BuildSnapshotFromHistory(displayEntry);
                ContextLine = BuildContextLine(displayEntry);
                LastBackupLine = BuildLastBackupLine(displayEntry);

                SetLatestBackupPathLines();
                SetSnapshot(snapshot, displayEntry);

                ImportStatusLine = _latestExistingBackupEntry == null
                    ? "No backup file was found on disk for this project context."
                    : "Ready to import latest backup into configured localhost database.";
            }
            catch (Exception ex)
            {
                ContextLine = BuildContextLine(sourceEntry);
                LastBackupLine = BuildLastBackupLine(sourceEntry);
                SummaryMessage = $"Integrity panel initialization warning: {ex.Message}";
                CapturedAtLine = "Captured at: —";
                LatestBackupFileLine = "Latest available backup file: unavailable.";
                LatestBackupPathLine = "Path: —";
                ImportStatusLine = $"Initialization warning: {ex.Message}";
            }

            OnPropertyChanged(nameof(ContextLine));
            OnPropertyChanged(nameof(LastBackupLine));
            OnPropertyChanged(nameof(SummaryMessage));
            OnPropertyChanged(nameof(CapturedAtLine));
            OnPropertyChanged(nameof(LatestBackupFileLine));
            OnPropertyChanged(nameof(LatestBackupPathLine));
            OnPropertyChanged(nameof(ImportButtonText));
            OnPropertyChanged(nameof(RefreshButtonText));
            OnPropertyChanged(nameof(CanImportLatestBackup));
            OnPropertyChanged(nameof(CanRefreshSnapshot));

            _ = RefreshSnapshotFromConfiguredDatabaseAsync(interactive: false);
        }

        public string ContextLine { get; private set; } = string.Empty;
        public string LastBackupLine { get; private set; } = string.Empty;
        public string CapturedAtLine { get; private set; } = string.Empty;
        public string SummaryMessage { get; private set; } = string.Empty;
        public string LatestBackupFileLine { get; private set; } = "Latest available backup file: —";
        public string LatestBackupPathLine { get; private set; } = "Path: —";
        public ObservableCollection<BackupIntegrityTableSample> TableSamples { get; } = new();
        public ObservableCollection<BackupIntegrityCellValue> SelectedRowValues { get; } = new();
        public event PropertyChangedEventHandler? PropertyChanged;

        public string TableCountLabel => $"{TableSamples.Count} table(s)";
        public string ImportButtonText => _isImportRunning ? "Importing..." : "Import latest backup";
        public string RefreshButtonText => _isRefreshRunning ? "Refreshing..." : "Refresh";
        public bool CanImportLatestBackup =>
            !_isImportRunning &&
            !_isRefreshRunning &&
            _latestExistingBackupEntry != null &&
            _sourceSchedule != null &&
            _sourceSchedule.EnableLocalRestoreValidation &&
            !string.IsNullOrWhiteSpace(_latestExistingBackupEntry.OutputPath) &&
            SafeFileExists(_latestExistingBackupEntry.OutputPath);

        public bool CanRefreshSnapshot =>
            !_isImportRunning &&
            !_isRefreshRunning &&
            _sourceSchedule != null &&
            _sourceSchedule.EnableLocalRestoreValidation;

        public bool ImportProgressVisible
        {
            get => _importProgressVisible;
            private set => SetProperty(ref _importProgressVisible, value);
        }

        public bool IsImportProgressIndeterminate
        {
            get => _isImportProgressIndeterminate;
            private set => SetProperty(ref _isImportProgressIndeterminate, value);
        }

        public double ImportProgressValue
        {
            get => _importProgressValue;
            private set => SetProperty(ref _importProgressValue, value);
        }

        public double ImportProgressMaximum
        {
            get => _importProgressMaximum;
            private set => SetProperty(ref _importProgressMaximum, value);
        }

        public string ImportStatusLine
        {
            get => _importStatusLine;
            private set => SetProperty(ref _importStatusLine, value);
        }

        public BackupIntegrityTableSample? SelectedTableSample
        {
            get => _selectedTableSample;
            set
            {
                if (!SetProperty(ref _selectedTableSample, value))
                {
                    return;
                }

                RefreshSelectedRowValues();
                OnPropertyChanged(nameof(SelectedTableTitle));
                OnPropertyChanged(nameof(SelectedTableInfoLine));
                OnPropertyChanged(nameof(SelectedTablePkLine));
                OnPropertyChanged(nameof(SelectedLastRowStatus));
                _ = LoadLatestRowForSelectedTableAsync(value);
            }
        }

        public string SelectedTableTitle => SelectedTableSample == null
            ? "Select a table from the left list"
            : $"Latest row snapshot • {SelectedTableSample.TableName}";

        public string SelectedTableInfoLine => SelectedTableSample == null
            ? "Choose one of the largest tables to inspect the latest imported record."
            : $"Approx. rows: {SelectedTableSample.ApproxRowCount:N0} • Data: {FormatBytes(SelectedTableSample.DataBytes)} • Index: {FormatBytes(SelectedTableSample.IndexBytes)} • Total: {SelectedTableSample.TotalBytesLabel}";

        public string SelectedTablePkLine => SelectedTableSample == null
            ? string.Empty
            : $"Primary key: {(string.IsNullOrWhiteSpace(SelectedTableSample.PrimaryKeySummary) ? "—" : SelectedTableSample.PrimaryKeySummary)}";

        public string SelectedLastRowStatus => SelectedTableSample == null
            ? string.Empty
            : SelectedTableSample.LastRowStatus;

        private async void ImportLatestBackup_Click(object sender, RoutedEventArgs e)
        {
            if (_isImportRunning || _isRefreshRunning)
            {
                return;
            }

            _latestExistingBackupEntry = FindLatestExistingBackupEntry(_sourceEntry);
            _sourceSchedule = ResolveScheduleForEntry(_latestExistingBackupEntry ?? _sourceEntry);
            SetLatestBackupPathLines();
            OnPropertyChanged(nameof(CanImportLatestBackup));
            if (_latestExistingBackupEntry == null)
            {
                ModernMessageBox.Show(
                    "No backup artifact was found on disk for this project context.",
                    "Backup integrity sampling",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(_latestExistingBackupEntry.OutputPath) || !File.Exists(_latestExistingBackupEntry.OutputPath))
            {
                ModernMessageBox.Show(
                    "Latest backup path is unavailable on disk. Please run backup again and retry import.",
                    "Backup integrity sampling",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (_sourceSchedule == null)
            {
                ModernMessageBox.Show(
                    "Could not resolve source schedule for this backup item.",
                    "Backup integrity sampling",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!_sourceSchedule.EnableLocalRestoreValidation)
            {
                ModernMessageBox.Show(
                    "Local restore validation is disabled for this schedule. Enable it in Backup Scheduler first.",
                    "Backup integrity sampling",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!BackupRestoreValidationService.TryBuildLocalConnectionInfo(_sourceSchedule, out _, out var reason))
            {
                ModernMessageBox.Show(
                    $"Localhost settings are not ready:\n{reason}",
                    "Backup integrity sampling",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _isImportRunning = true;
            ImportProgressVisible = true;
            IsImportProgressIndeterminate = true;
            ImportProgressMaximum = 100;
            ImportProgressValue = 0;
            ImportStatusLine = "Preparing import/validate flow...";
            OnPropertyChanged(nameof(ImportButtonText));
            OnPropertyChanged(nameof(RefreshButtonText));
            OnPropertyChanged(nameof(CanImportLatestBackup));
            OnPropertyChanged(nameof(CanRefreshSnapshot));

            try
            {
                var progress = new Progress<BackupProgressUpdate>(HandleImportProgress);
                var result = await _restoreValidationService.ValidateAsync(
                    _sourceSchedule,
                    _latestExistingBackupEntry.OutputPath,
                    progress,
                    CancellationToken.None);

                _latestExistingBackupEntry.RestoreValidationEnabled = _sourceSchedule.EnableLocalRestoreValidation;
                _latestExistingBackupEntry.RestoreValidationAttempted = result.IsAttempted;
                _latestExistingBackupEntry.RestoreValidationPassed = result.Passed;
                _latestExistingBackupEntry.RestoreValidationMessage = result.Message;
                _latestExistingBackupEntry.RestoreValidationDatabase = result.ValidationDatabaseName;
                _latestExistingBackupEntry.IntegritySampleCapturedUtc = result.IntegritySampling?.CapturedUtc;
                _latestExistingBackupEntry.IntegritySampleMessage = result.IntegritySampling?.Message ?? string.Empty;
                _latestExistingBackupEntry.IntegrityTableSamples = result.IntegritySampling?.Tables ?? new List<BackupIntegrityTableSample>();
                _latestExistingBackupEntry.Message = AppendManualImportResult(_latestExistingBackupEntry.Message, result);

                BackupHistoryStore.UpdateEntry(_latestExistingBackupEntry);

                if (result.IntegritySampling != null)
                {
                    SetSnapshot(result.IntegritySampling, _latestExistingBackupEntry);
                }

                await RefreshSnapshotFromConfiguredDatabaseAsync(interactive: false);
                ImportStatusLine = string.IsNullOrWhiteSpace(result.Message)
                    ? ImportStatusLine
                    : result.Message;
                IsImportProgressIndeterminate = false;
                ImportProgressMaximum = 1;
                ImportProgressValue = 1;

                ModernMessageBox.Show(
                    result.Passed
                        ? $"Import/validate completed successfully.\n{result.Message}"
                        : $"Import/validate finished with warning.\n{result.Message}",
                    "Backup integrity sampling",
                    MessageBoxButton.OK,
                    result.Passed ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                ImportStatusLine = $"Import failed: {ex.Message}";
                IsImportProgressIndeterminate = false;
                ImportProgressMaximum = 1;
                ImportProgressValue = 0;
                ModernMessageBox.Show(
                    $"Import/validate failed:\n{ex.Message}",
                    "Backup integrity sampling",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _isImportRunning = false;
                ImportProgressVisible = false;
                OnPropertyChanged(nameof(ImportButtonText));
                OnPropertyChanged(nameof(RefreshButtonText));
                OnPropertyChanged(nameof(CanImportLatestBackup));
                OnPropertyChanged(nameof(CanRefreshSnapshot));
            }
        }

        private async void RefreshSnapshot_Click(object sender, RoutedEventArgs e)
        {
            if (_isImportRunning || _isRefreshRunning)
            {
                return;
            }

            _isRefreshRunning = true;
            ImportProgressVisible = true;
            IsImportProgressIndeterminate = true;
            ImportProgressValue = 0;
            ImportProgressMaximum = 100;
            ImportStatusLine = "Refreshing integrity sampling from localhost database...";
            OnPropertyChanged(nameof(RefreshButtonText));
            OnPropertyChanged(nameof(ImportButtonText));
            OnPropertyChanged(nameof(CanRefreshSnapshot));
            OnPropertyChanged(nameof(CanImportLatestBackup));

            try
            {
                await RefreshSnapshotFromConfiguredDatabaseAsync(interactive: true);
            }
            finally
            {
                _isRefreshRunning = false;
                ImportProgressVisible = false;
                IsImportProgressIndeterminate = false;
                OnPropertyChanged(nameof(RefreshButtonText));
                OnPropertyChanged(nameof(ImportButtonText));
                OnPropertyChanged(nameof(CanRefreshSnapshot));
                OnPropertyChanged(nameof(CanImportLatestBackup));
            }
        }

        private void HandleImportProgress(BackupProgressUpdate? update)
        {
            if (update == null)
            {
                return;
            }

            ImportProgressVisible = true;
            if (update.TotalTables > 0)
            {
                IsImportProgressIndeterminate = false;
                ImportProgressMaximum = update.TotalTables;
                ImportProgressValue = Math.Min(update.ProcessedTables, update.TotalTables);
            }
            else
            {
                IsImportProgressIndeterminate = true;
            }

            ImportStatusLine = BuildProgressText(update);
        }

        private async Task RefreshSnapshotFromConfiguredDatabaseAsync(bool interactive)
        {
            if (!TryResolveLocalValidationContext(out var info, out var configuredDb, out var reason))
            {
                ImportStatusLine = $"Refresh skipped: {reason}";
                if (interactive)
                {
                    ModernMessageBox.Show(
                        $"Refresh skipped:\n{reason}",
                        "Backup integrity sampling",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                return;
            }

            try
            {
                await using var client = new DatabaseClient();
                await client.ConnectAsync(info);

                if (!await client.DatabaseExistsAsync(configuredDb))
                {
                    ImportStatusLine = $"Refresh warning: configured localhost database `{configuredDb}` does not exist.";
                    return;
                }

                var snapshot = await _integritySamplingService.CaptureAsync(
                    client,
                    configuredDb,
                    topTables: 0,
                    cancellationToken: CancellationToken.None,
                    includeLatestRows: false);
                var referenceEntry = _latestExistingBackupEntry ?? _sourceEntry;
                SetSnapshot(snapshot, referenceEntry);

                if (_latestExistingBackupEntry != null)
                {
                    _latestExistingBackupEntry.IntegritySampleCapturedUtc = snapshot.CapturedUtc;
                    _latestExistingBackupEntry.IntegritySampleMessage = snapshot.Message ?? string.Empty;
                    _latestExistingBackupEntry.IntegrityTableSamples = snapshot.Tables ?? new List<BackupIntegrityTableSample>();
                    BackupHistoryStore.UpdateEntry(_latestExistingBackupEntry);
                }

                IsImportProgressIndeterminate = false;
                ImportProgressMaximum = 1;
                ImportProgressValue = 1;
                ImportStatusLine = snapshot.HasData
                    ? $"Refreshed from `{configuredDb}` ({(snapshot.Tables?.Count ?? 0)} table sample(s))."
                    : (string.IsNullOrWhiteSpace(snapshot.Message)
                        ? $"Refreshed from `{configuredDb}` but no base tables were found."
                        : snapshot.Message);
            }
            catch (Exception ex)
            {
                ImportStatusLine = $"Refresh failed: {ex.Message}";
                if (interactive)
                {
                    ModernMessageBox.Show(
                        $"Refresh failed:\n{ex.Message}",
                        "Backup integrity sampling",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }

        private void SetSnapshot(BackupIntegritySamplingSnapshot snapshot, BackupHistoryEntry referenceEntry)
        {
            var capturedUtc = snapshot.CapturedUtc != default
                ? snapshot.CapturedUtc
                : referenceEntry.IntegritySampleCapturedUtc ?? default;
            CapturedAtLine = capturedUtc == default
                ? "Captured at: —"
                : $"Captured at: {AppTimeService.FormatLocalFromUtc(capturedUtc)}";
            OnPropertyChanged(nameof(CapturedAtLine));

            SummaryMessage = string.IsNullOrWhiteSpace(snapshot.Message)
                ? string.IsNullOrWhiteSpace(referenceEntry.IntegritySampleMessage)
                    ? "Snapshot generated from latest localhost validation import."
                    : referenceEntry.IntegritySampleMessage
                : snapshot.Message;
            OnPropertyChanged(nameof(SummaryMessage));

            TableSamples.Clear();
            var ordered = (snapshot.Tables ?? new List<BackupIntegrityTableSample>())
                .OrderBy(x => x.Rank <= 0 ? int.MaxValue : x.Rank)
                .ThenByDescending(x => x.TotalBytes)
                .ToList();
            foreach (var table in ordered)
            {
                TableSamples.Add(table);
            }

            OnPropertyChanged(nameof(TableCountLabel));
            SelectedTableSample = null;
            RefreshSelectedRowValues();
            OnPropertyChanged(nameof(SelectedTableTitle));
            OnPropertyChanged(nameof(SelectedTableInfoLine));
            OnPropertyChanged(nameof(SelectedTablePkLine));
            OnPropertyChanged(nameof(SelectedLastRowStatus));
        }

        private async Task LoadLatestRowForSelectedTableAsync(BackupIntegrityTableSample? selected)
        {
            if (selected == null || _isImportRunning || _isRefreshRunning)
            {
                return;
            }

            _selectedRowLoadCts?.Cancel();
            _selectedRowLoadCts?.Dispose();
            var cts = new CancellationTokenSource();
            _selectedRowLoadCts = cts;

            selected.LastRowValues.Clear();
            selected.LastRowStatus = "Loading latest row...";
            OnPropertyChanged(nameof(SelectedLastRowStatus));

            try
            {
                if (!TryResolveLocalValidationContext(out var info, out var configuredDb, out var reason))
                {
                    selected.LastRowStatus = $"Latest row load skipped: {reason}";
                    RefreshSelectedRowValues();
                    OnPropertyChanged(nameof(SelectedTablePkLine));
                    OnPropertyChanged(nameof(SelectedLastRowStatus));
                    return;
                }

                await using var client = new DatabaseClient();
                await client.ConnectAsync(info);
                await _integritySamplingService.LoadLatestRowAsync(client, configuredDb, selected, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                selected.LastRowValues.Clear();
                selected.LastRowStatus = $"Latest row load failed: {ex.Message}";
            }
            finally
            {
                if (_selectedRowLoadCts == cts)
                {
                    _selectedRowLoadCts = null;
                }

                if (!cts.IsCancellationRequested && ReferenceEquals(SelectedTableSample, selected))
                {
                    RefreshSelectedRowValues();
                    OnPropertyChanged(nameof(SelectedTablePkLine));
                    OnPropertyChanged(nameof(SelectedLastRowStatus));
                }

                cts.Dispose();
            }
        }

        private bool TryResolveLocalValidationContext(
            out DatabaseConnectionInfo info,
            out string configuredDb,
            out string reason)
        {
            info = new DatabaseConnectionInfo();
            configuredDb = string.Empty;
            reason = string.Empty;

            _latestExistingBackupEntry ??= FindLatestExistingBackupEntry(_sourceEntry);
            _sourceSchedule ??= ResolveScheduleForEntry(_latestExistingBackupEntry ?? _sourceEntry);
            if (_sourceSchedule == null)
            {
                reason = "source schedule is unavailable.";
                return false;
            }

            if (!_sourceSchedule.EnableLocalRestoreValidation)
            {
                reason = "local restore validation is disabled.";
                return false;
            }

            if (!BackupRestoreValidationService.TryBuildLocalConnectionInfo(_sourceSchedule, out info, out var localReason))
            {
                reason = localReason;
                return false;
            }

            configuredDb = (_sourceSchedule.LocalValidationDatabaseName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(configuredDb))
            {
                reason = "local validation database name is empty.";
                return false;
            }

            return true;
        }

        private void RefreshSelectedRowValues()
        {
            SelectedRowValues.Clear();
            if (SelectedTableSample?.LastRowValues == null || SelectedTableSample.LastRowValues.Count == 0)
            {
                return;
            }

            foreach (var cell in SelectedTableSample.LastRowValues)
            {
                SelectedRowValues.Add(cell);
            }
        }

        private void SetLatestBackupPathLines()
        {
            if (_latestExistingBackupEntry == null || string.IsNullOrWhiteSpace(_latestExistingBackupEntry.OutputPath))
            {
                LatestBackupFileLine = "Latest available backup file: not found on disk.";
                LatestBackupPathLine = "Path: —";
            }
            else
            {
                var path = _latestExistingBackupEntry.OutputPath.Trim();
                string fileName;
                try
                {
                    fileName = Path.GetFileName(path);
                }
                catch
                {
                    fileName = string.Empty;
                }
                LatestBackupFileLine = $"Latest available backup file: {(string.IsNullOrWhiteSpace(fileName) ? path : fileName)}";
                LatestBackupPathLine = $"Path: {path}";
            }

            OnPropertyChanged(nameof(LatestBackupFileLine));
            OnPropertyChanged(nameof(LatestBackupPathLine));
        }

        private static BackupHistoryEntry? FindLatestExistingBackupEntry(BackupHistoryEntry contextEntry)
        {
            var items = BackupHistoryStore.LoadHistory();
            if (items.Count == 0)
            {
                return null;
            }

            var existing = items.Where(x =>
                x.Success &&
                !string.IsNullOrWhiteSpace(x.OutputPath) &&
                SafeFileExists(x.OutputPath));

            if (!string.IsNullOrWhiteSpace(contextEntry.ScheduleId))
            {
                var bySchedule = existing.FirstOrDefault(x =>
                    string.Equals(x.ScheduleId, contextEntry.ScheduleId, StringComparison.OrdinalIgnoreCase));
                if (bySchedule != null)
                {
                    return bySchedule;
                }
            }

            if (!string.IsNullOrWhiteSpace(contextEntry.ConnectionProfileId))
            {
                var byProfile = existing.FirstOrDefault(x =>
                    string.Equals(x.ConnectionProfileId, contextEntry.ConnectionProfileId, StringComparison.OrdinalIgnoreCase));
                if (byProfile != null)
                {
                    return byProfile;
                }
            }

            if (!string.IsNullOrWhiteSpace(contextEntry.DatabaseName))
            {
                var byDatabase = existing.FirstOrDefault(x =>
                    string.Equals(x.DatabaseName, contextEntry.DatabaseName, StringComparison.OrdinalIgnoreCase));
                if (byDatabase != null)
                {
                    return byDatabase;
                }
            }

            return existing.FirstOrDefault();
        }

        private static bool SafeFileExists(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                return File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        private static BackupSchedule? ResolveScheduleForEntry(BackupHistoryEntry? entry)
        {
            if (entry == null)
            {
                return null;
            }

            var schedules = BackupScheduleStore.LoadSchedules();
            if (!string.IsNullOrWhiteSpace(entry.ScheduleId))
            {
                var byId = schedules.FirstOrDefault(x =>
                    string.Equals(x.Id, entry.ScheduleId, StringComparison.OrdinalIgnoreCase));
                if (byId != null)
                {
                    return byId;
                }
            }

            return schedules.FirstOrDefault(x =>
                string.Equals(x.ConnectionProfileId, entry.ConnectionProfileId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.DatabaseName, entry.DatabaseName, StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildProgressText(BackupProgressUpdate update)
        {
            if (!string.IsNullOrWhiteSpace(update.Message))
            {
                return update.Message;
            }

            return update.Stage switch
            {
                "ValidationPrepare" => "Preparing localhost validation environment...",
                "ValidationImport" => "Importing backup into localhost database...",
                "ValidationCheck" => "Running validation checks...",
                "ValidationSampling" => "Collecting integrity samples...",
                "ValidationCleanup" => "Finalizing import workflow...",
                "ValidationDone" => "Validation completed successfully.",
                "ValidationWarning" => "Validation finished with warning.",
                _ => "Import/validate is running..."
            };
        }

        private static string BuildContextLine(BackupHistoryEntry entry)
        {
            var schedule = string.IsNullOrWhiteSpace(entry.ScheduleName) ? "Unknown schedule" : entry.ScheduleName;
            var database = string.IsNullOrWhiteSpace(entry.DatabaseName) ? "Unknown database" : entry.DatabaseName;
            return $"Schedule: {schedule} • Database: {database}";
        }

        private static string BuildLastBackupLine(BackupHistoryEntry entry)
        {
            var finished = string.IsNullOrWhiteSpace(entry.CompletedLocalDisplay)
                ? entry.StartedLocalDisplay
                : entry.CompletedLocalDisplay;
            var when = string.IsNullOrWhiteSpace(finished) ? "—" : finished;
            return $"Latest successful imported backup: {when}";
        }

        private static BackupIntegritySamplingSnapshot BuildSnapshotFromHistory(BackupHistoryEntry entry)
        {
            return new BackupIntegritySamplingSnapshot
            {
                DatabaseName = entry.DatabaseName,
                CapturedUtc = entry.IntegritySampleCapturedUtc ?? AppTimeService.UtcNow,
                Message = entry.IntegritySampleMessage,
                Tables = entry.IntegrityTableSamples ?? new List<BackupIntegrityTableSample>()
            };
        }

        private static string AppendManualImportResult(string currentMessage, BackupRestoreValidationResult result)
        {
            var status = result.Passed ? "passed" : "warning";
            var segment = $"Manual import/validate: {status} ({result.Message})";
            if (string.IsNullOrWhiteSpace(currentMessage))
            {
                return segment;
            }

            return $"{currentMessage} · {segment}";
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            var order = Math.Min(units.Length - 1, (int)Math.Floor(Math.Log(bytes, 1024)));
            var adjusted = bytes / Math.Pow(1024, order);
            return $"{adjusted:0.##} {units[order]}";
        }

        private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(storage, value))
            {
                return false;
            }

            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
