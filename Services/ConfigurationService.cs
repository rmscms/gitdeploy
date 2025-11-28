using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GitDeployPro.Models;
using Newtonsoft.Json;

namespace GitDeployPro.Services
{
    public class ConfigurationService
    {
        private const string GlobalConfigFile = "global_config.json";
        private const string ProjectConfigFile = ".gitdeploy.config";

        public class GlobalConfig
        {
            public string LastProjectPath { get; set; } = "";
            public List<string> RecentProjects { get; set; } = new List<string>();
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
                    return JsonConvert.DeserializeObject<GlobalConfig>(json) ?? new GlobalConfig();
                }
            }
            catch { }
            return new GlobalConfig();
        }

        public void SaveGlobalConfig(GlobalConfig config)
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, GlobalConfigFile);
                File.WriteAllText(path, JsonConvert.SerializeObject(config, Formatting.Indented));
            }
            catch { }
        }

        public void AddRecentProject(string path)
        {
            var config = LoadGlobalConfig();
            
            // Remove if exists to re-add at top
            if (config.RecentProjects.Contains(path))
            {
                config.RecentProjects.Remove(path);
            }
            
            config.RecentProjects.Insert(0, path);
            
            // Limit to 10 recent projects
            if (config.RecentProjects.Count > 10)
            {
                config.RecentProjects = config.RecentProjects.Take(10).ToList();
            }
            
            config.LastProjectPath = path;
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
                
                File.WriteAllText(path, JsonConvert.SerializeObject(config, Formatting.Indented));
                
                // Hide the config file
                try
                {
                    var fileInfo = new FileInfo(path);
                    fileInfo.Attributes |= FileAttributes.Hidden;
                }
                catch { }
            }
            catch { }
        }
    }
}
