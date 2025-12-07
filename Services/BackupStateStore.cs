using System;
using System.Collections.Generic;
using System.IO;
using GitDeployPro.Models;
using Newtonsoft.Json;

namespace GitDeployPro.Services
{
    internal static class BackupStateStore
    {
        private const string BackupStateFile = "backup_state.json";
        private static readonly object Sync = new();
        private static BackupState? _cache;

        internal class BackupState
        {
            public List<BackupSchedule> BackupSchedules { get; set; } = new();
            public List<BackupHistoryEntry> BackupHistory { get; set; } = new();
        }

        private static string GetStatePath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var folder = Path.Combine(appData, "GitDeployPro");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, BackupStateFile);
        }

        public static BackupState LoadState()
        {
            lock (Sync)
            {
                if (_cache != null)
                {
                    return CloneState(_cache);
                }

                BackupState state;
                var path = GetStatePath();
                if (File.Exists(path))
                {
                    try
                    {
                        var json = File.ReadAllText(path);
                        state = JsonConvert.DeserializeObject<BackupState>(json) ?? new BackupState();
                    }
                    catch
                    {
                        state = new BackupState();
                    }
                }
                else
                {
                    state = new BackupState();
                    TryMigrateFromGlobal(state);
                    SaveStateInternal(state);
                }

                EnsureLists(state);
                _cache = CloneState(state);
                return CloneState(state);
            }
        }

        public static void SaveState(BackupState state)
        {
            if (state == null) return;
            lock (Sync)
            {
                EnsureLists(state);
                SaveStateInternal(state);
                _cache = CloneState(state);
            }
        }

        private static void SaveStateInternal(BackupState state)
        {
            try
            {
                var json = JsonConvert.SerializeObject(state, Formatting.Indented);
                File.WriteAllText(GetStatePath(), json);
            }
            catch
            {
            }
        }

        private static BackupState CloneState(BackupState source)
        {
            return new BackupState
            {
                BackupSchedules = source.BackupSchedules.ConvertAll(CloneSchedule),
                BackupHistory = source.BackupHistory.ConvertAll(CloneHistory)
            };
        }

        private static BackupSchedule CloneSchedule(BackupSchedule schedule)
        {
            var json = JsonConvert.SerializeObject(schedule);
            return JsonConvert.DeserializeObject<BackupSchedule>(json) ?? new BackupSchedule();
        }

        private static BackupHistoryEntry CloneHistory(BackupHistoryEntry history)
        {
            var json = JsonConvert.SerializeObject(history);
            return JsonConvert.DeserializeObject<BackupHistoryEntry>(json) ?? new BackupHistoryEntry();
        }

        private static void EnsureLists(BackupState state)
        {
            state.BackupSchedules ??= new List<BackupSchedule>();
            state.BackupHistory ??= new List<BackupHistoryEntry>();
        }

        private static void TryMigrateFromGlobal(BackupState state)
        {
            try
            {
                var configService = new ConfigurationService();
                var config = configService.LoadGlobalConfig();

                if (config.BackupSchedules != null && config.BackupSchedules.Count > 0)
                {
                    state.BackupSchedules = config.BackupSchedules;
                }

                if (config.BackupHistory != null && config.BackupHistory.Count > 0)
                {
                    state.BackupHistory = config.BackupHistory;
                }

                if ((config.BackupSchedules?.Count ?? 0) > 0 || (config.BackupHistory?.Count ?? 0) > 0)
                {
                    configService.UpdateGlobalConfig(cfg =>
                    {
                        cfg.BackupSchedules = new List<BackupSchedule>();
                        cfg.BackupHistory = new List<BackupHistoryEntry>();
                    });
                }
            }
            catch
            {
            }
        }
    }
}

