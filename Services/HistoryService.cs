using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using Newtonsoft.Json;

namespace GitDeployPro.Services
{
    public class HistoryService
    {
        private static string _workingDirectory = Directory.GetCurrentDirectory();
        private string HistoryFile => Path.Combine(_workingDirectory, ".gitdeploy.history");

        public HistoryService()
        {
            // Initialize with last known project path if available
            try
            {
                var configService = new ConfigurationService();
                var globalConfig = configService.LoadGlobalConfig();
                if (!string.IsNullOrEmpty(globalConfig.LastProjectPath) && Directory.Exists(globalConfig.LastProjectPath))
                {
                    _workingDirectory = globalConfig.LastProjectPath;
                }
            }
            catch { }
        }

        public static void SetWorkingDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                _workingDirectory = path;
            }
        }

        public List<DeploymentRecord> GetHistory()
        {
            try
            {
                if (File.Exists(HistoryFile))
                {
                    var json = File.ReadAllText(HistoryFile);
                    var list = JsonConvert.DeserializeObject<List<DeploymentRecord>>(json);
                    return list?.OrderByDescending(x => x.Date).ToList() ?? new List<DeploymentRecord>();
                }
            }
            catch { }
            return new List<DeploymentRecord>();
        }

        public void AddRecord(DeploymentRecord record)
        {
            var list = GetHistory();
            record.Id = list.Count + 1;
            list.Add(record);
            SaveHistory(list);
        }

        private void SaveHistory(List<DeploymentRecord> list)
        {
            try
            {
                var json = JsonConvert.SerializeObject(list, Formatting.Indented);
                File.WriteAllText(HistoryFile, json);
                
                // Hide the history file
                try
                {
                    var fileInfo = new FileInfo(HistoryFile);
                    fileInfo.Attributes |= FileAttributes.Hidden;
                }
                catch { }
            }
            catch { }
        }

        public DeploymentRecord? GetLastDeploy()
        {
            var list = GetHistory();
            return list.FirstOrDefault();
        }
    }

    public class DeploymentRecord
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public DateTime Date { get; set; }
        public int FilesCount { get; set; }
        public string Branch { get; set; } = "";
        public string Status { get; set; } = ""; // Success, Failed
        public List<string> Files { get; set; } = new List<string>();
        public string CommitHash { get; set; } = "";

        [JsonIgnore]
        public string Icon => Status == "Success" ? "✅" : "❌";
        
        [JsonIgnore]
        public SolidColorBrush StatusColor => Status == "Success" 
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 125, 50)) 
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(198, 40, 40));

        [JsonIgnore]
        public string Details => $"Date: {Date:yyyy/MM/dd - HH:mm} | Files: {FilesCount} | Branch: {Branch}";
    }
}
