using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GitDeployPro.Controls;
using GitDeployPro.Models;
using GitDeployPro.Services;
using MahApps.Metro.Controls;

namespace GitDeployPro.Pages
{
    public partial class BackupHistoryWindow : MetroWindow
    {
        public ObservableCollection<BackupHistoryEntry> History { get; } = BackupHistoryStore.LoadHistory();
        private readonly BackupRestoreValidationService _restoreValidationService = new();
        private bool _isImportValidateRunning;

        public BackupHistoryWindow()
        {
            InitializeComponent();
            DataContext = this;
        }

        private void ClearBackupHistory_Click(object sender, RoutedEventArgs e)
        {
            if (History.Count == 0)
            {
                ModernMessageBox.Show("Backup history is already empty.", "Backup history", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = ModernMessageBox.ShowWithResult(
                "Are you sure you want to clear all backup history records?",
                "Clear backup history",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                "Clear",
                "Cancel");

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            BackupHistoryStore.ClearHistory();
            History.Clear();
            ModernMessageBox.Show("Backup history cleared.", "Backup history", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void ImportValidateFromHistory_Click(object sender, RoutedEventArgs e)
        {
            if (_isImportValidateRunning)
            {
                ModernMessageBox.Show("A history import/validation is already running.", "Backup history", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (sender is not System.Windows.Controls.Button button || button.CommandParameter is not BackupHistoryEntry entry)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(entry.OutputPath) || !File.Exists(entry.OutputPath))
            {
                ModernMessageBox.Show("Selected backup file is missing on disk. Please verify the output path first.", "Backup history", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var schedules = BackupScheduleStore.LoadSchedules();
            var schedule = schedules.FirstOrDefault(s => string.Equals(s.Id, entry.ScheduleId, System.StringComparison.OrdinalIgnoreCase));
            if (schedule == null)
            {
                ModernMessageBox.Show(
                    "Could not find the source backup schedule for this history row. Manual validation needs a schedule to read localhost validation settings.",
                    "Backup history",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!schedule.EnableLocalRestoreValidation)
            {
                ModernMessageBox.Show(
                    "Local restore validation is disabled in this schedule. Enable it in Backup Scheduler first, then run Import/Validate again.",
                    "Backup history",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!BackupRestoreValidationService.TryBuildLocalConnectionInfo(schedule, out _, out var reason))
            {
                ModernMessageBox.Show($"Local validation settings are not ready:\n{reason}", "Backup history", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _isImportValidateRunning = true;
            try
            {
                var result = await _restoreValidationService.ValidateAsync(
                    schedule,
                    entry.OutputPath,
                    progress: null,
                    cancellationToken: CancellationToken.None);

                entry.RestoreValidationEnabled = schedule.EnableLocalRestoreValidation;
                entry.RestoreValidationAttempted = result.IsAttempted;
                entry.RestoreValidationPassed = result.Passed;
                entry.RestoreValidationMessage = result.Message;
                entry.RestoreValidationDatabase = result.ValidationDatabaseName;
                entry.Message = $"{entry.Message} · Manual import/validate: {(result.Passed ? "passed" : "warning")} ({result.Message})";

                BackupHistoryStore.UpdateEntry(entry);
                ReloadHistory();

                if (result.Passed)
                {
                    ModernMessageBox.Show($"Manual import/validate passed.\n{result.Message}", "Backup history", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    ModernMessageBox.Show($"Manual import/validate finished with warning.\n{result.Message}", "Backup history", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (System.Exception ex)
            {
                ModernMessageBox.Show($"Manual import/validate failed:\n{ex.Message}", "Backup history", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isImportValidateRunning = false;
            }
        }

        private void ReloadHistory()
        {
            var latest = BackupHistoryStore.LoadHistory();
            History.Clear();
            foreach (var item in latest)
            {
                History.Add(item);
            }
        }
    }
}

