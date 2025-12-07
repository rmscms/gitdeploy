using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GitDeployPro.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GitDeployPro.Services
{
    public class ConfigurationService
    {
        private const string GlobalConfigFile = "global_config.json";
        private const string ConnectionsFile = "connections.json"; // New file for stored connections
        private const string ProjectConfigFile = ".gitdeploy.config";

        private string GetAppDataPath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "GitDeployPro");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }
            return folder;
        }

        public class GlobalConfig
        {
            public string LastProjectPath { get; set; } = "";
            public List<RecentProjectEntry> RecentProjects { get; set; } = new();
            public string DefaultSshKeyPath { get; set; } = "";
            public List<TerminalCommandPreset> TerminalPresets { get; set; } = new();
            public List<BackupSchedule> BackupSchedules { get; set; } = new();
            public List<BackupHistoryEntry> BackupHistory { get; set; } = new();
            public bool LaunchOnStartup { get; set; }
        }

        public class RecentProjectEntry
        {
            public string Path { get; set; } = "";
            public DateTime LastOpenedUtc { get; set; } = DateTime.UtcNow;
        }

        // --- Connection Profiles Management ---

        public List<ConnectionProfile> LoadConnections()
        {
            try
            {
                var path = Path.Combine(GetAppDataPath(), ConnectionsFile);
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var list = JsonConvert.DeserializeObject<List<ConnectionProfile>>(json);
                    if (list != null)
                    {
                        foreach (var profile in list)
                        {
                            profile.PathMappings ??= new List<PathMapping>();
                        }
                    }
                    return list ?? new List<ConnectionProfile>();
                }
            }
            catch { }
            return new List<ConnectionProfile>();
        }

        public void SaveConnections(List<ConnectionProfile> profiles)
        {
            try
            {
                var path = Path.Combine(GetAppDataPath(), ConnectionsFile);
                File.WriteAllText(path, JsonConvert.SerializeObject(profiles, Formatting.Indented));
            }
            catch { }
        }

        public void AddOrUpdateConnection(ConnectionProfile profile)
        {
            var connections = LoadConnections();
            var existing = connections.FirstOrDefault(x => x.Id == profile.Id);
            
            if (existing != null)
            {
                connections.Remove(existing);
            }
            connections.Add(profile);
            SaveConnections(connections);
        }

        public void DeleteConnection(string id)
        {
            var connections = LoadConnections();
            var existing = connections.FirstOrDefault(x => x.Id == id);
            if (existing != null)
            {
                connections.Remove(existing);
                SaveConnections(connections);
            }
        }

        // --- Global Config Management ---
        public GlobalConfig LoadGlobalConfig()
        {
            try
            {
                // Try AppData first
                var appDataPath = Path.Combine(GetAppDataPath(), GlobalConfigFile);
                if (File.Exists(appDataPath))
                {
                    return LoadConfigFromFile(appDataPath);
                }

                // Fallback to local directory (migration logic)
                var localPath = Path.Combine(AppContext.BaseDirectory, GlobalConfigFile);
                if (File.Exists(localPath))
                {
                    var config = LoadConfigFromFile(localPath);
                    // Save to AppData immediately to migrate
                    SaveGlobalConfig(config);
                    return config;
                }
            }
            catch { }
            return new GlobalConfig();
        }

        private GlobalConfig LoadConfigFromFile(string path)
        {
            var json = File.ReadAllText(path);
            var token = JToken.Parse(json);
            var config = token.ToObject<GlobalConfig>() ?? new GlobalConfig();

            if ((config.RecentProjects == null || config.RecentProjects.Count == 0) &&
                token["RecentProjects"] is JArray legacyArray)
            {
                var now = DateTime.UtcNow;
                int offset = 0;
                var migrated = legacyArray
                    .Where(t => t.Type == JTokenType.String)
                    .Select(t => t.Value<string>() ?? string.Empty)
                    .Where(pathValue => !string.IsNullOrWhiteSpace(pathValue))
                    .Select(pathValue => new RecentProjectEntry
                    {
                        Path = pathValue,
                        LastOpenedUtc = now.AddSeconds(-(offset++))
                    })
                    .ToList();

                if (migrated.Count > 0)
                {
                    config.RecentProjects = migrated;
                }
            }

            config.RecentProjects ??= new List<RecentProjectEntry>();
            config.TerminalPresets ??= new List<TerminalCommandPreset>();
            config.BackupSchedules ??= new List<BackupSchedule>();
            config.BackupHistory ??= new List<BackupHistoryEntry>();
            return config;
        }

        public void SaveGlobalConfig(GlobalConfig config)
        {
            try
            {
            config ??= new GlobalConfig();
            config.RecentProjects ??= new List<RecentProjectEntry>();
            config.TerminalPresets ??= new List<TerminalCommandPreset>();
            config.BackupSchedules ??= new List<BackupSchedule>();
            config.BackupHistory ??= new List<BackupHistoryEntry>();

                var path = Path.Combine(GetAppDataPath(), GlobalConfigFile);
                File.WriteAllText(path, JsonConvert.SerializeObject(config, Formatting.Indented));
            }
            catch { }
        }

        public void AddRecentProject(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            var config = LoadGlobalConfig();
            config.RecentProjects ??= new List<RecentProjectEntry>();

            string normalizedPath;
            try
            {
                normalizedPath = Path.GetFullPath(path);
            }
            catch
            {
                normalizedPath = path;
            }

            var existing = config.RecentProjects.FirstOrDefault(p =>
                string.Equals(p.Path, normalizedPath, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.LastOpenedUtc = DateTime.UtcNow;
            }
            else
            {
                config.RecentProjects.Add(new RecentProjectEntry
                {
                    Path = normalizedPath,
                    LastOpenedUtc = DateTime.UtcNow
                });
            }

            config.RecentProjects = config.RecentProjects
                .OrderByDescending(p => p.LastOpenedUtc)
                .Take(10)
                .ToList();

            config.LastProjectPath = normalizedPath;
            SaveGlobalConfig(config);
        }

        public void UpdateGlobalConfig(Action<GlobalConfig> update)
        {
            if (update == null) return;

            var config = LoadGlobalConfig();
            update(config);
            SaveGlobalConfig(config);
        }

        // Project Config Management
        public ProjectConfig LoadProjectConfig(string projectPath)
        {
            try
            {
                if (string.IsNullOrEmpty(projectPath) || !Directory.Exists(projectPath))
                    return new ProjectConfig();

                var path = Path.Combine(projectPath, ProjectConfigFile);
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    return JsonConvert.DeserializeObject<ProjectConfig>(json) ?? new ProjectConfig();
                }
            }
            catch { }
            
            // Return default config with the path set
            return new ProjectConfig { LocalProjectPath = projectPath };
        }

        public void SaveProjectConfig(ProjectConfig config)
        {
            try
            {
                if (string.IsNullOrEmpty(config.LocalProjectPath) || !Directory.Exists(config.LocalProjectPath))
                    return;

                var path = Path.Combine(config.LocalProjectPath, ProjectConfigFile);
                
                // Ensure we can write to it if it exists (handle Hidden/ReadOnly)
                if (File.Exists(path))
                {
                    var fi = new FileInfo(path);
                    FileAttributes attributes = fi.Attributes;
                    bool changed = false;

                    if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                    {
                        attributes &= ~FileAttributes.ReadOnly;
                        changed = true;
                    }
                    if ((attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
                    {
                        attributes &= ~FileAttributes.Hidden;
                        changed = true;
                    }

                    if (changed)
                    {
                        fi.Attributes = attributes;
                    }
                }

                File.WriteAllText(path, JsonConvert.SerializeObject(config, Formatting.Indented));
                
                // Hide the config file
                try
                {
                    var fileInfo = new FileInfo(path);
                    fileInfo.Attributes |= FileAttributes.Hidden;
                }
                catch { }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save project config: {ex.Message}");
            }
        }
    }
}