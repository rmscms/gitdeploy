using System;
using System.IO;
using Newtonsoft.Json;

namespace GitDeployPro.Models
{
    public class ProjectConfig
    {
        public string FtpHost { get; set; } = "";
        public string FtpUsername { get; set; } = "";
        public string FtpPassword { get; set; } = ""; // Encrypted
        public int FtpPort { get; set; } = 21;
        public string RemotePath { get; set; } = "/";
        public string LocalProjectPath { get; set; } = "";
        
        public string DefaultSourceBranch { get; set; } = "master";
        public string DefaultTargetBranch { get; set; } = "";
        
        public bool UseSSH { get; set; }
        public bool AutoInitGit { get; set; } = true;
        public bool AutoCommit { get; set; } = true;
        public bool AutoPush { get; set; }
        public string[] ExcludePatterns { get; set; } = new string[0];

        [JsonIgnore]
        public string FtpPasswordDecrypted => Services.EncryptionService.Decrypt(FtpPassword);
    }
}
