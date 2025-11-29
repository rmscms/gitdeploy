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
        private const string ProjectConfigFile = ".gitdeploy.config";

        public class GlobalConfig
        {
            public string LastProjectPath { get; set; } = "";
            public List<RecentProjectEntry> RecentProjects { get; set; } = new List<RecentProjectEntry>();
            public string DefaultSshKeyPath { get; set; } = "";
        }

        public class RecentProjectEntry
        {
            public string Path { get; set; } = "";
            public DateTime LastOpenedUtc { get; set; } = DateTime.UtcNow;
        }

        // Global Config Management
        public GlobalConfig LoadGlobalConfig()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, GlobalConfigFile);
                if (File.Exists(path))
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
                    return config;
                }
            }
            catch { }
            return new GlobalConfig();
        }

        public void SaveGlobalConfig(GlobalConfig config)
        {
            try
            {
                config ??= new GlobalConfig();
                config.RecentProjects ??= new List<RecentProjectEntry>();

                var path = Path.Combine(AppContext.BaseDirectory, GlobalConfigFile);
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
