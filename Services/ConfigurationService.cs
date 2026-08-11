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
        private const string SessionFoldersFile = "session_folders.json"; // Session Manager folder structure
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

            /// <summary>
            /// Saved-commands UI: "dock" (strip above terminal) or "float" (helper tool window).
            /// </summary>
            public string TerminalPresetsUiMode { get; set; } = "dock";

            /// <summary>Terminal command autocomplete (ghost text + Tab).</summary>
            public bool TerminalAutocompleteEnabled { get; set; } = true;

            /// <summary>Highlight unknown commands in terminal (prefix not in dictionary).</summary>
            public bool TerminalDictionaryModeEnabled { get; set; }

            /// <summary>Suggestion catalog for terminal autocomplete (global + per-project).</summary>
            public List<TerminalSuggestion> TerminalSuggestions { get; set; } = new();

            public List<BackupSchedule> BackupSchedules { get; set; } = new();
            public List<BackupHistoryEntry> BackupHistory { get; set; } = new();
            public bool LaunchOnStartup { get; set; }

            /// <summary>
            /// One-time notice after migrating portable EXE into the install folder.
            /// </summary>
            public bool HasShownInstallMigrationNotice { get; set; }

            /// <summary>
            /// Mirror of HKCU install directory for About/Settings display.
            /// </summary>
            public string InstallDirectory { get; set; } = "";

            public bool ShowBackupSchedulerLocalhostWarning { get; set; } = true;
            public bool MinimizeToTray { get; set; } = true;
            public bool EnablePerformanceSampling { get; set; }
            public DateTime? LastUpdateCheckUtc { get; set; }

            /// <summary>Last version for which the What's New modal was shown.</summary>
            public string LastSeenWhatsNewVersion { get; set; } = "";

            /// <summary>
            /// App-wide theme id (default | dark | custom pack ids). Persists across pages and restarts.
            /// </summary>
            public string AppThemeId { get; set; } = "dark";

            /// <summary>
            /// UI language code: <c>en</c> (default) or <c>fa</c>.
            /// </summary>
            public string UiLanguage { get; set; } = "en";

            /// <summary>
            /// Legacy alias kept in sync with <see cref="AppThemeId"/> for older configs / Deploy UI.
            /// </summary>
            public string DeployThemeId { get; set; } = "default";

            /// <summary>
            /// Custom theme pack filenames under %AppData%\GitDeployPro\Themes\.
            /// </summary>
            public List<string> CustomThemeFiles { get; set; } = new();
        }

        public class RecentProjectEntry
        {
            public string Path { get; set; } = "";
            public DateTime LastOpenedUtc { get; set; } = AppTimeService.UtcNow;
        }

        // --- Connection Profiles Management ---

        public static event EventHandler? ConnectionsChanged;

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
                ConnectionsChanged?.Invoke(null, EventArgs.Empty);
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

        // --- Session Folders Management (Tree Structure) ---

        public List<SessionFolder> LoadSessionFolders()
        {
            try
            {
                var path = Path.Combine(GetAppDataPath(), SessionFoldersFile);
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var list = JsonConvert.DeserializeObject<List<SessionFolder>>(json);
                    return list ?? new List<SessionFolder>();
                }
            }
            catch { }
            return new List<SessionFolder>();
        }

        public void SaveSessionFolders(List<SessionFolder> folders)
        {
            try
            {
                var path = Path.Combine(GetAppDataPath(), SessionFoldersFile);
                File.WriteAllText(path, JsonConvert.SerializeObject(folders, Formatting.Indented));
            }
            catch { }
        }

        public void AddOrUpdateSessionFolder(SessionFolder folder)
        {
            var folders = LoadSessionFolders();
            var existing = folders.FirstOrDefault(x => x.Id == folder.Id);
            
            if (existing != null)
            {
                folders.Remove(existing);
            }
            folders.Add(folder);
            SaveSessionFolders(folders);
        }

        public void DeleteSessionFolder(string id)
        {
            var folders = LoadSessionFolders();
            var existing = folders.FirstOrDefault(x => x.Id == id);
            if (existing != null)
            {
                // Move all connections from this folder to root (null folder)
                var connections = LoadConnections();
                foreach (var conn in connections.Where(c => c.FolderId == id))
                {
                    conn.FolderId = null;
                }
                SaveConnections(connections);

                // Delete child folders (move them to parent or root)
                var childFolders = folders.Where(f => f.ParentFolderId == id).ToList();
                foreach (var child in childFolders)
                {
                    child.ParentFolderId = existing.ParentFolderId;
                }
                SaveSessionFolders(folders);

                folders.Remove(existing);
                SaveSessionFolders(folders);
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
                var now = AppTimeService.UtcNow;
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
            config.TerminalSuggestions ??= new List<TerminalSuggestion>();
            config.BackupSchedules ??= new List<BackupSchedule>();
            config.BackupHistory ??= new List<BackupHistoryEntry>();
            MigrateAppThemeId(config, token);
            return config;
        }

        /// <summary>
        /// Prefer AppThemeId; migrate from DeployThemeId when the new field was absent.
        /// Keeps both ids aligned for backward compatibility.
        /// </summary>
        private static void MigrateAppThemeId(GlobalConfig config, JToken token)
        {
            var appThemeToken = token["AppThemeId"];
            var hasAppTheme = appThemeToken != null && appThemeToken.Type != JTokenType.Null
                              && !string.IsNullOrWhiteSpace(appThemeToken.ToString());

            if (!hasAppTheme && !string.IsNullOrWhiteSpace(config.DeployThemeId))
            {
                config.AppThemeId = config.DeployThemeId;
            }

            if (string.IsNullOrWhiteSpace(config.AppThemeId))
            {
                config.AppThemeId = "dark";
            }

            config.DeployThemeId = config.AppThemeId;
        }

        public string ResolveAppThemeId()
        {
            var config = LoadGlobalConfig();
            return string.IsNullOrWhiteSpace(config.AppThemeId) ? "dark" : config.AppThemeId;
        }

        public void SetAppThemeId(string themeId)
        {
            var id = string.IsNullOrWhiteSpace(themeId) ? "dark" : themeId.Trim();
            UpdateGlobalConfig(cfg =>
            {
                cfg.AppThemeId = id;
                cfg.DeployThemeId = id;
            });
        }

        public void SaveGlobalConfig(GlobalConfig config)
        {
            try
            {
            config ??= new GlobalConfig();
            config.RecentProjects ??= new List<RecentProjectEntry>();
            config.TerminalPresets ??= new List<TerminalCommandPreset>();
            config.TerminalSuggestions ??= new List<TerminalSuggestion>();
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
                existing.LastOpenedUtc = AppTimeService.UtcNow;
            }
            else
            {
                config.RecentProjects.Add(new RecentProjectEntry
                {
                    Path = normalizedPath,
                    LastOpenedUtc = AppTimeService.UtcNow
                });
            }

            config.RecentProjects = config.RecentProjects
                .OrderByDescending(p => p.LastOpenedUtc)
                .Take(10)
                .ToList();

            config.LastProjectPath = normalizedPath;
            SaveGlobalConfig(config);
        }

        /// <summary>
        /// Removes a project from the app recent list / last project only.
        /// Does not delete any files on disk.
        /// </summary>
        /// <returns>The next LastProjectPath after removal (may be empty).</returns>
        public string RemoveRecentProject(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return LoadGlobalConfig().LastProjectPath ?? string.Empty;
            }

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

            config.RecentProjects = config.RecentProjects
                .Where(p => !string.Equals(p.Path, normalizedPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.LastOpenedUtc)
                .Take(10)
                .ToList();

            if (string.Equals(config.LastProjectPath, normalizedPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(TryNormalizePath(config.LastProjectPath), normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                config.LastProjectPath = config.RecentProjects.FirstOrDefault()?.Path ?? string.Empty;
            }

            SaveGlobalConfig(config);
            return config.LastProjectPath ?? string.Empty;
        }

        private static string TryNormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        public void UpdateGlobalConfig(Action<GlobalConfig> update)
        {
            if (update == null) return;

            var config = LoadGlobalConfig();
            update(config);
            SaveGlobalConfig(config);
        }

        // Project Config Management
        public bool HasProjectConfigFile(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                return false;
            }

            try
            {
                return File.Exists(Path.Combine(projectPath, ProjectConfigFile));
            }
            catch
            {
                return false;
            }
        }

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
                    var config = JsonConvert.DeserializeObject<ProjectConfig>(json) ?? new ProjectConfig();
                    config.LocalProjectPath = string.IsNullOrWhiteSpace(config.LocalProjectPath) ? projectPath : config.LocalProjectPath;
                    if (string.IsNullOrWhiteSpace(config.DefaultSourceBranch))
                    {
                        config.DefaultSourceBranch = "master";
                    }

                    if (string.IsNullOrWhiteSpace(config.DefaultTargetBranch) ||
                        string.Equals(config.DefaultSourceBranch, config.DefaultTargetBranch, StringComparison.OrdinalIgnoreCase))
                    {
                        config.DefaultTargetBranch = ResolveDefaultTargetBranch(config.DefaultSourceBranch);
                    }

                    return config;
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
                if (string.IsNullOrWhiteSpace(config.DefaultSourceBranch))
                {
                    config.DefaultSourceBranch = "master";
                }

                if (string.IsNullOrWhiteSpace(config.DefaultTargetBranch) ||
                    string.Equals(config.DefaultSourceBranch, config.DefaultTargetBranch, StringComparison.OrdinalIgnoreCase))
                {
                    config.DefaultTargetBranch = ResolveDefaultTargetBranch(config.DefaultSourceBranch);
                }

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

        private static string ResolveDefaultTargetBranch(string sourceBranch)
        {
            foreach (var candidate in new[] { "production", "master", "main", "release" })
            {
                if (!string.Equals(sourceBranch, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            return string.IsNullOrWhiteSpace(sourceBranch) ? "production" : $"{sourceBranch}-deploy";
        }
    }
}