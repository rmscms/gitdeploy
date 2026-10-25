using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GitDeployPro.Models;
using Newtonsoft.Json;

namespace GitDeployPro.Services
{
    public sealed class BackupPlanTransferService
    {
        public const string FormatId = "gitdeploypro-backup-plan";
        public const int FormatVersion = 1;
        public const string FileFilter = "GitDeploy backup plan (*.gitdeploy.backup-plan.json)|*.gitdeploy.backup-plan.json|JSON (*.json)|*.json";

        private readonly ConfigurationService _config = new();

        public sealed class BackupPlanPackage
        {
            public string Format { get; set; } = FormatId;
            public int SchemaVersion { get; set; } = FormatVersion;
            public DateTime ExportedUtc { get; set; } = DateTime.UtcNow;
            public List<BackupSchedule> Schedules { get; set; } = new();
            public List<ConnectionProfile> Connections { get; set; } = new();
        }

        public sealed class ImportResult
        {
            public int SchedulesAdded { get; set; }
            public int ConnectionsAdded { get; set; }
            public int ConnectionsUpdated { get; set; }
            public List<string> Warnings { get; set; } = new();
        }

        public BackupPlanPackage BuildPackage(IEnumerable<BackupSchedule> schedules)
        {
            var list = (schedules ?? Enumerable.Empty<BackupSchedule>())
                .Where(s => s != null)
                .Select(CloneScheduleForExport)
                .ToList();

            var connections = _config.LoadConnections() ?? new List<ConnectionProfile>();
            var neededIds = new HashSet<string>(
                list.Select(s => s.ConnectionProfileId ?? string.Empty)
                    .Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.OrdinalIgnoreCase);

            var linked = connections
                .Where(c => c != null && neededIds.Contains(c.Id ?? string.Empty))
                .Select(CloneConnection)
                .ToList();

            return new BackupPlanPackage
            {
                Format = FormatId,
                SchemaVersion = FormatVersion,
                ExportedUtc = DateTime.UtcNow,
                Schedules = list,
                Connections = linked
            };
        }

        public void ExportToFile(string filePath, IEnumerable<BackupSchedule> schedules)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("File path is required.", nameof(filePath));
            }

            var package = BuildPackage(schedules);
            if (package.Schedules.Count == 0)
            {
                throw new InvalidOperationException("No schedule selected to export.");
            }

            var json = JsonConvert.SerializeObject(package, Formatting.Indented);
            File.WriteAllText(filePath, json);
        }

        public BackupPlanPackage LoadFromFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                throw new FileNotFoundException("Backup plan file not found.", filePath);
            }

            var json = File.ReadAllText(filePath);
            var package = JsonConvert.DeserializeObject<BackupPlanPackage>(json)
                          ?? throw new InvalidOperationException("Backup plan file is empty or invalid.");

            if (!string.Equals(package.Format, FormatId, StringComparison.OrdinalIgnoreCase)
                && package.SchemaVersion <= 0
                && (package.Schedules == null || package.Schedules.Count == 0))
            {
                throw new InvalidOperationException("Unrecognized backup plan format.");
            }

            package.Schedules ??= new List<BackupSchedule>();
            package.Connections ??= new List<ConnectionProfile>();
            return package;
        }

        public ImportResult ImportPackage(BackupPlanPackage package, bool mergeConnections = true)
        {
            if (package == null)
            {
                throw new ArgumentNullException(nameof(package));
            }

            var result = new ImportResult();
            var existingConnections = _config.LoadConnections() ?? new List<ConnectionProfile>();
            var idMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var incoming in package.Connections ?? new List<ConnectionProfile>())
            {
                if (incoming == null || string.IsNullOrWhiteSpace(incoming.Id))
                {
                    continue;
                }

                var match = existingConnections.FirstOrDefault(c =>
                    string.Equals(c.Id, incoming.Id, StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(c.Name, incoming.Name, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(c.Host, incoming.Host, StringComparison.OrdinalIgnoreCase)));

                if (match != null)
                {
                    idMap[incoming.Id] = match.Id;
                    if (mergeConnections)
                    {
                        // Keep local secrets if incoming password empty after cross-machine export quirks.
                        if (string.IsNullOrWhiteSpace(incoming.Password) && !string.IsNullOrWhiteSpace(match.Password))
                        {
                            incoming.Password = match.Password;
                        }

                        if (string.IsNullOrWhiteSpace(incoming.DbPassword) && !string.IsNullOrWhiteSpace(match.DbPassword))
                        {
                            incoming.DbPassword = match.DbPassword;
                        }

                        incoming.Id = match.Id;
                        var idx = existingConnections.FindIndex(c =>
                            string.Equals(c.Id, match.Id, StringComparison.OrdinalIgnoreCase));
                        if (idx >= 0)
                        {
                            existingConnections[idx] = incoming;
                            result.ConnectionsUpdated++;
                        }
                    }
                }
                else
                {
                    var newId = Guid.NewGuid().ToString("N");
                    idMap[incoming.Id] = newId;
                    incoming.Id = newId;
                    existingConnections.Add(incoming);
                    result.ConnectionsAdded++;
                }
            }

            if (result.ConnectionsAdded > 0 || result.ConnectionsUpdated > 0)
            {
                _config.SaveConnections(existingConnections);
            }

            var existingSchedules = BackupScheduleStore.LoadSchedules()?.ToList() ?? new List<BackupSchedule>();
            foreach (var schedule in package.Schedules ?? new List<BackupSchedule>())
            {
                if (schedule == null)
                {
                    continue;
                }

                var oldConnId = schedule.ConnectionProfileId ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(oldConnId) && idMap.TryGetValue(oldConnId, out var mapped))
                {
                    schedule.ConnectionProfileId = mapped;
                }
                else if (!string.IsNullOrWhiteSpace(oldConnId)
                         && existingConnections.All(c =>
                             !string.Equals(c.Id, oldConnId, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Warnings.Add(
                        $"Schedule \"{schedule.Name}\" references a missing connection; pick a profile after import.");
                }

                // Always assign a fresh schedule id to avoid colliding with local plans.
                schedule.Id = Guid.NewGuid().ToString();
                schedule.LastRunUtc = null;
                schedule.NextRunUtc = null;
                existingSchedules.Add(schedule);
                result.SchedulesAdded++;
            }

            if (result.SchedulesAdded > 0)
            {
                BackupScheduleStore.SaveSchedules(existingSchedules);
            }

            return result;
        }

        private static BackupSchedule CloneScheduleForExport(BackupSchedule source)
        {
            var json = JsonConvert.SerializeObject(source);
            return JsonConvert.DeserializeObject<BackupSchedule>(json) ?? new BackupSchedule();
        }

        private static ConnectionProfile CloneConnection(ConnectionProfile source)
        {
            var json = JsonConvert.SerializeObject(source);
            return JsonConvert.DeserializeObject<ConnectionProfile>(json) ?? new ConnectionProfile();
        }
    }
}
