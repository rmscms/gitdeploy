using System;
using System.IO;
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
                
                // Ensure password is encrypted before saving if it's not already (logic handled in UI usually, but good to double check or assume UI sets encrypted prop)
                // In this design, the UI sets the properties on ProjectConfig. 
                // We should ensure we don't double encrypt or save plain text if possible, 
                // but ProjectConfig.FtpPassword is meant to hold the encrypted string.

                File.WriteAllText(path, JsonConvert.SerializeObject(config, Formatting.Indented));
            }
            catch { }
        }
    }
}

