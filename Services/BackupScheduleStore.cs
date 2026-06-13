using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using GitDeployPro.Models;

namespace GitDeployPro.Services
{
    public static class BackupScheduleStore
    {
        public static event Action? SchedulesChanged;

        public static ObservableCollection<BackupSchedule> LoadSchedules()
        {
            var state = BackupStateStore.LoadState();
            return new ObservableCollection<BackupSchedule>(state.BackupSchedules.Select(CloneSchedule));
        }

        public static void SaveSchedules(IEnumerable<BackupSchedule> schedules)
        {
            var state = BackupStateStore.LoadState();
            state.BackupSchedules = schedules?.Select(CloneSchedule).ToList() ?? new List<BackupSchedule>();
            BackupStateStore.SaveState(state);
            SchedulesChanged?.Invoke();
        }

        public static void AddOrUpdate(BackupSchedule schedule)
        {
            if (schedule == null) return;

            var state = BackupStateStore.LoadState();
            var existing = state.BackupSchedules.FirstOrDefault(s => s.Id == schedule.Id);
            if (existing != null)
            {
                state.BackupSchedules.Remove(existing);
            }
            state.BackupSchedules.Add(CloneSchedule(schedule));
            BackupStateStore.SaveState(state);

            SchedulesChanged?.Invoke();
        }

        public static void Delete(string scheduleId)
        {
            if (string.IsNullOrWhiteSpace(scheduleId)) return;

            var state = BackupStateStore.LoadState();
            var existing = state.BackupSchedules.FirstOrDefault(s =>
                string.Equals(s.Id, scheduleId, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                state.BackupSchedules.Remove(existing);
                BackupStateStore.SaveState(state);
            }

            SchedulesChanged?.Invoke();
        }

        private static BackupSchedule CloneSchedule(BackupSchedule source)
        {
            return new BackupSchedule
            {
                Id = string.IsNullOrWhiteSpace(source.Id) ? Guid.NewGuid().ToString() : source.Id,
                Name = source.Name ?? string.Empty,
                ConnectionProfileId = source.ConnectionProfileId ?? string.Empty,
                DatabaseName = source.DatabaseName ?? string.Empty,
                Enabled = source.Enabled,
                Frequency = source.Frequency,
                LocalRunTime = source.LocalRunTime,
                DaysOfWeek = source.DaysOfWeek?.ToList() ?? new List<DayOfWeek>(),
                DayOfMonth = source.DayOfMonth,
                CustomIntervalMinutes = source.CustomIntervalMinutes,
                OutputDirectory = source.OutputDirectory ?? string.Empty,
                CompressOutput = source.CompressOutput,
                CompressionFormat = source.CompressionFormat,
                RetentionCount = source.RetentionCount,
                EncryptAtRest = source.EncryptAtRest,
                BackupMode = source.BackupMode,
                RemoteDownloadPolicy = source.RemoteDownloadPolicy,
                RemoteOutputDirectory = source.RemoteOutputDirectory ?? "/tmp/gitdeploypro-backups",
                DeleteRemoteArtifactAfterDownload = source.DeleteRemoteArtifactAfterDownload,
                EnableLocalRestoreValidation = source.EnableLocalRestoreValidation,
                LocalValidationProfileId = string.IsNullOrWhiteSpace(source.LocalValidationProfileId)
                    ? BackupRestoreValidationDefaults.LocalhostDefaultProfileId
                    : source.LocalValidationProfileId,
                LocalValidationHost = string.IsNullOrWhiteSpace(source.LocalValidationHost) ? "127.0.0.1" : source.LocalValidationHost,
                LocalValidationPort = source.LocalValidationPort <= 0 ? 3306 : source.LocalValidationPort,
                LocalValidationDatabaseName = source.LocalValidationDatabaseName ?? string.Empty,
                LocalValidationUsername = string.IsNullOrWhiteSpace(source.LocalValidationUsername) ? "root" : source.LocalValidationUsername,
                LocalValidationPassword = source.LocalValidationPassword ?? string.Empty,
                LocalValidationCharset = string.IsNullOrWhiteSpace(source.LocalValidationCharset) ? "auto" : source.LocalValidationCharset,
                LocalValidationCollation = string.IsNullOrWhiteSpace(source.LocalValidationCollation) ? "auto" : source.LocalValidationCollation,
                LastRunUtc = source.LastRunUtc,
                NextRunUtc = source.NextRunUtc
            };
        }
    }
}

