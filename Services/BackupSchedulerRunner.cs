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
        private readonly BackupRestoreValidationService _restoreValidationService = new();
        private readonly NotificationService _notificationService = new();
        private readonly BackupTaskMonitor _taskMonitor = BackupTaskMonitor.Instance;
        private readonly System.Threading.Timer _timer;
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _failureCounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _runningScheduleKeys = new(StringComparer.OrdinalIgnoreCase);
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
                        BackupScheduleTimelineService.RecalculateNextRun(schedule, AppTimeService.UtcNow);
                        continue;
                    }

                    if (AppTimeService.UtcNow < schedule.NextRunUtc) continue;

                    await RunScheduleAsync(schedule);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BackupSchedulerRunner] Check loop error: {ex}");
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
                ApplyFailureBackoff(schedule, "Missing connection profile.");
                BackupScheduleStore.AddOrUpdate(schedule);
                return;
            }

            var runKey = BuildRunKey(schedule.Id, schedule.ConnectionProfileId);
            lock (_gate)
            {
                if (_runningScheduleKeys.Contains(runKey) || _taskMonitor.IsScheduleRunning(schedule.Id, schedule.ConnectionProfileId))
                {
                    _notificationService.ShowToast("Backup skipped", $"{schedule.Name} is already running.");
                    return;
                }

                _runningScheduleKeys.Add(runKey);
            }

            var history = new BackupHistoryEntry
            {
                ScheduleId = schedule.Id,
                ScheduleName = schedule.Name,
                ConnectionProfileId = schedule.ConnectionProfileId,
                DatabaseName = schedule.DatabaseName,
                StartedUtc = AppTimeService.UtcNow
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
                var hasLocalArtifact = result.HasLocalArtifact &&
                                       !string.IsNullOrWhiteSpace(result.OutputPath) &&
                                       System.IO.File.Exists(result.OutputPath);
                var health = hasLocalArtifact
                    ? _healthService.Verify(result.OutputPath, result.IsCompressed)
                    : new BackupHealthReport
                    {
                        IsHealthy = true,
                        Details = "Remote artifact reference saved. Local health verification skipped."
                    };

                var validationResult = BackupRestoreValidationResult.Skipped("Local restore validation is disabled.");
                if (schedule.EnableLocalRestoreValidation)
                {
                    if (!BackupRestoreValidationService.TryBuildLocalConnectionInfo(schedule, out _, out var validationConfigReason))
                    {
                        validationResult = BackupRestoreValidationResult.Warning($"Validation warning: {validationConfigReason}");
                    }
                    else if (!hasLocalArtifact)
                    {
                        validationResult = BackupRestoreValidationResult.Warning("Validation warning: no local artifact is available for localhost restore validation.");
                    }
                    else
                    {
                        validationResult = await _restoreValidationService.ValidateAsync(
                            schedule,
                            result.OutputPath,
                            progress,
                            taskHandle.Cancellation.Token);
                    }
                }

                if (validationResult.IsAttempted && validationResult.Passed && hasLocalArtifact)
                {
                    if (BackupArtifactNaming.TryMarkAsVerified(result.OutputPath, out var verifiedPath, out _))
                    {
                        result.OutputPath = verifiedPath;
                        if (System.IO.File.Exists(verifiedPath))
                        {
                            result.BytesWritten = new System.IO.FileInfo(verifiedPath).Length;
                        }
                    }
                }

                history.CompletedUtc = AppTimeService.UtcNow;
                history.Success = true;
                history.OutputPath = result.OutputPath;
                history.FileSizeBytes = result.BytesWritten;
                history.Sha256 = result.Sha256;
                history.IsRemoteArtifact = result.IsRemoteArtifact;
                history.HasLocalArtifact = result.HasLocalArtifact;
                history.RemoteArtifactPath = result.RemoteArtifactPath;
                history.RemoteArtifactSizeBytes = result.RemoteArtifactBytes;
                history.RemoteArtifactSha256 = result.RemoteArtifactSha256;
                history.DownloadPolicy = schedule.RemoteDownloadPolicy.ToString();
                history.RemoteArtifactDeletedAfterDownload = result.RemoteArtifactDeleted;
                history.RemoteCleanupMessage = result.RemoteCleanupMessage;
                history.HealthPassed = health.IsHealthy;
                history.HealthDetails = health.Details;
                history.RestoreValidationEnabled = schedule.EnableLocalRestoreValidation;
                history.RestoreValidationAttempted = validationResult.IsAttempted;
                history.RestoreValidationPassed = validationResult.Passed;
                history.RestoreValidationMessage = validationResult.Message;
                history.RestoreValidationDatabase = validationResult.ValidationDatabaseName;
                history.IntegritySampleCapturedUtc = validationResult.IntegritySampling?.CapturedUtc;
                history.IntegritySampleMessage = validationResult.IntegritySampling?.Message ?? string.Empty;
                history.IntegrityTableSamples = validationResult.IntegritySampling?.Tables ?? new List<BackupIntegrityTableSample>();
                var artifactLabel = result.IsRemoteArtifact
                    ? (result.HasLocalArtifact ? "remote+local" : "remote-reference")
                    : (schedule.CompressOutput
                        ? (schedule.CompressionFormat == BackupCompressionFormat.TarGz ? "tar.gz" : "zip")
                        : "sql");
                if (!result.IsRemoteArtifact && schedule.EncryptAtRest)
                {
                    artifactLabel += "+protected";
                }
                var healthLabel = health.IsHealthy ? "passed" : "FAILED";
                var cleanupTag = result.RemoteArtifactDeleted ? " · remote cleaned" : string.Empty;
                var validationTag = validationResult.IsWarning
                    ? $" · {validationResult.Message}"
                    : (validationResult.IsAttempted && validationResult.Passed ? " · Validation passed" : string.Empty);
                var finalSizeLabel = FormatBytes(result.BytesWritten);
                history.Message = $"Created {artifactLabel} ({finalSizeLabel}){cleanupTag} · Health {healthLabel}{validationTag}.";

                schedule.LastRunUtc = history.CompletedUtc;
                ResetFailureBackoff(schedule);
                BackupScheduleTimelineService.RecalculateNextRun(schedule, AppTimeService.UtcNow);
                BackupScheduleStore.AddOrUpdate(schedule);
                BackupHistoryStore.AddEntry(history);
                var validationTaskTag = validationResult.IsWarning ? " with validation warning" : string.Empty;
                _taskMonitor.CompleteTask(taskHandle.TaskId, $"[{schedule.Name}] Scheduled backup finished ({finalSizeLabel}){validationTaskTag}.");

                if (validationResult.IsWarning)
                {
                    _notificationService.ShowToast("Backup completed with validation warning", $"{schedule.Name}: {validationResult.Message}");
                }
                else
                {
                    _notificationService.ShowToast("Backup completed", $"{schedule.Name} finished successfully.");
                }
            }
            catch (OperationCanceledException)
            {
                history.CompletedUtc = AppTimeService.UtcNow;
                history.Success = false;
                history.Message = "Canceled by user.";
                BackupHistoryStore.AddEntry(history);
                ApplyFailureBackoff(schedule, "Canceled by user.");
                BackupScheduleStore.AddOrUpdate(schedule);
                if (taskHandle != null)
                {
                    _taskMonitor.MarkCancelled(taskHandle.TaskId, $"[{schedule.Name}] Scheduled backup canceled.");
                }
            }
            catch (Exception ex)
            {
                history.CompletedUtc = AppTimeService.UtcNow;
                history.Success = false;
                history.Message = ex.Message;
                BackupHistoryStore.AddEntry(history);
                ApplyFailureBackoff(schedule, ex.Message);
                BackupScheduleStore.AddOrUpdate(schedule);
                if (taskHandle != null)
                {
                    _taskMonitor.FailTask(taskHandle.TaskId, $"[{schedule.Name}] Scheduled backup failed: {ex.Message}");
                }
                _notificationService.ShowToast("Backup failed", $"{schedule.Name}: {ex.Message}");
            }
            finally
            {
                taskHandle?.Dispose();
                lock (_gate)
                {
                    _runningScheduleKeys.Remove(runKey);
                }
            }
        }

        private void ApplyFailureBackoff(BackupSchedule schedule, string reason)
        {
            if (schedule == null || string.IsNullOrWhiteSpace(schedule.Id))
            {
                return;
            }

            var attempts = 1;
            lock (_gate)
            {
                _failureCounts.TryGetValue(schedule.Id, out attempts);
                attempts = Math.Min(attempts + 1, 8);
                _failureCounts[schedule.Id] = attempts;
            }

            var delayMinutes = Math.Min(30, (int)Math.Pow(2, attempts - 1));
            schedule.NextRunUtc = AppTimeService.UtcNow.AddMinutes(Math.Max(1, delayMinutes));
            _notificationService.ShowToast("Backup retry scheduled", $"{schedule.Name}: retry in {delayMinutes} min ({reason}).");
        }

        private void ResetFailureBackoff(BackupSchedule schedule)
        {
            if (schedule == null || string.IsNullOrWhiteSpace(schedule.Id))
            {
                return;
            }

            lock (_gate)
            {
                _failureCounts.Remove(schedule.Id);
            }
        }

        private static string BuildRunKey(string scheduleId, string? connectionProfileId)
        {
            return $"{scheduleId}|{connectionProfileId ?? string.Empty}";
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

