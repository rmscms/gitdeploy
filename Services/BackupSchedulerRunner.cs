using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Models;

namespace GitDeployPro.Services
{
    public sealed class BackupSchedulerRunner : IDisposable
    {
        private readonly ConfigurationService _configService = new();
        private readonly DatabaseBackupService _backupService = new();
        private readonly BackupHealthService _healthService = new();
        private readonly NotificationService _notificationService = new();
        private readonly BackupTaskMonitor _taskMonitor = BackupTaskMonitor.Instance;
        private readonly System.Threading.Timer _timer;
        private readonly object _gate = new();
        private bool _disposed;
        private bool _checking;

        public static BackupSchedulerRunner Instance { get; } = new BackupSchedulerRunner();

        private BackupSchedulerRunner()
        {
            _timer = new System.Threading.Timer(async _ => await CheckAsync(), null, Timeout.Infinite, Timeout.Infinite);
            BackupScheduleStore.SchedulesChanged += ForceCheck;
        }

        public void Start()
        {
            _timer.Change(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1));
        }

        public void ForceCheck()
        {
            _timer.Change(TimeSpan.Zero, TimeSpan.FromMinutes(1));
        }

        private async Task CheckAsync()
        {
            if (_disposed) return;

            lock (_gate)
            {
                if (_checking) return;
                _checking = true;
            }

            try
            {
                var schedules = BackupStateStore.LoadState().BackupSchedules ?? new List<BackupSchedule>();
                if (schedules.Count == 0) return;

                foreach (var schedule in schedules.Where(s => s.Enabled))
                {
                    if (schedule.NextRunUtc == null)
                    {
                        BackupSchedulePlanner.RefreshNextRun(schedule);
                        continue;
                    }

                    if (DateTime.UtcNow < schedule.NextRunUtc) continue;

                    await RunScheduleAsync(schedule);
                }
            }
            catch
            {
            }
            finally
            {
                lock (_gate)
                {
                    _checking = false;
                }
            }
        }

        private async Task RunScheduleAsync(BackupSchedule schedule)
        {
            var connections = _configService.LoadConnections();
            var profile = connections.FirstOrDefault(c => string.Equals(c.Id, schedule.ConnectionProfileId, StringComparison.OrdinalIgnoreCase));
            if (profile == null)
            {
                _notificationService.ShowToast("Backup skipped", $"Profile missing for {schedule.Name}.");
                return;
            }

            var history = new BackupHistoryEntry
            {
                ScheduleId = schedule.Id,
                ScheduleName = schedule.Name,
                ConnectionProfileId = schedule.ConnectionProfileId,
                DatabaseName = schedule.DatabaseName,
                StartedUtc = DateTime.UtcNow
            };

            BackupTaskHandle? taskHandle = null;
            try
            {
                taskHandle = _taskMonitor.StartTask(schedule, profile, allowCancel: true, "Scheduled");
                var progress = new Progress<BackupProgressUpdate>(update =>
                {
                    _taskMonitor.UpdateProgress(taskHandle.TaskId, update);
                });
                var result = await _backupService.RunBackupAsync(profile, schedule, progress, taskHandle.Cancellation.Token);
                var health = _healthService.Verify(result.OutputPath, schedule.CompressOutput);

                history.CompletedUtc = DateTime.UtcNow;
                history.Success = true;
                history.OutputPath = result.OutputPath;
                history.FileSizeBytes = result.BytesWritten;
                history.Sha256 = result.Sha256;
                history.HealthPassed = health.IsHealthy;
                history.HealthDetails = health.Details;
                var artifactLabel = schedule.CompressOutput
                    ? (schedule.CompressionFormat == BackupCompressionFormat.TarGz ? "tar.gz" : "zip")
                    : "sql";
                var healthLabel = health.IsHealthy ? "passed" : "FAILED";
                history.Message = $"Created {artifactLabel} ({FormatBytes(result.BytesWritten)}) · Health {healthLabel}.";

                schedule.LastRunUtc = history.CompletedUtc;
                BackupSchedulePlanner.RefreshNextRun(schedule);
                BackupScheduleStore.AddOrUpdate(schedule);
                BackupHistoryStore.AddEntry(history);
                _taskMonitor.CompleteTask(taskHandle.TaskId, $"[{schedule.Name}] Scheduled backup finished ({FormatBytes(result.BytesWritten)}).");

                _notificationService.ShowToast("Backup completed", $"{schedule.Name} finished successfully.");
            }
            catch (OperationCanceledException)
            {
                history.CompletedUtc = DateTime.UtcNow;
                history.Success = false;
                history.Message = "Canceled by user.";
                BackupHistoryStore.AddEntry(history);
                if (taskHandle != null)
                {
                    _taskMonitor.MarkCancelled(taskHandle.TaskId, $"[{schedule.Name}] Scheduled backup canceled.");
                }
            }
            catch (Exception ex)
            {
                history.CompletedUtc = DateTime.UtcNow;
                history.Success = false;
                history.Message = ex.Message;
                BackupHistoryStore.AddEntry(history);
                if (taskHandle != null)
                {
                    _taskMonitor.FailTask(taskHandle.TaskId, $"[{schedule.Name}] Scheduled backup failed: {ex.Message}");
                }
                _notificationService.ShowToast("Backup failed", $"{schedule.Name}: {ex.Message}");
            }
            finally
            {
                taskHandle?.Dispose();
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            var order = Math.Min(units.Length - 1, (int)Math.Floor(Math.Log(bytes, 1024)));
            var adjusted = bytes / Math.Pow(1024, order);
            return $"{adjusted:0.##} {units[order]}";
        }

        public void Dispose()
        {
            _disposed = true;
            _timer.Dispose();
        }
    }
}

