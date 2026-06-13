using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace GitDeployPro.Models
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum DeployMode
    {
        FtpDeploy,
        GitHubOnly,
        Hybrid
    }

    public class ProjectConfig
    {
        // New Approach: Reference a Connection Profile
        public string ConnectionProfileId { get; set; } = "";

        // Legacy Fields (Kept for backward compatibility if needed, but UI will use ProfileId)
        public string FtpHost { get; set; } = "";
        public string FtpUsername { get; set; } = "";
        public string FtpPassword { get; set; } = ""; // Encrypted
        public int FtpPort { get; set; } = 21;
        public bool UseSSH { get; set; }

        public string RemotePath { get; set; } = "/";
        public string LocalProjectPath { get; set; } = "";
        
        public string DefaultSourceBranch { get; set; } = "master";
        public string DefaultTargetBranch { get; set; } = "";
        public string GitRemoteUrl { get; set; } = "";
        
        public bool AutoInitGit { get; set; } = true;
        public bool AutoCommit { get; set; } = true;
        public bool AutoPush { get; set; }
        public DeployMode DeployMode { get; set; } = DeployMode.FtpDeploy;
        public string[] ExcludePatterns { get; set; } = new string[0];

        [JsonIgnore]
        public string FtpPasswordDecrypted => Services.EncryptionService.Decrypt(FtpPassword);
    }
}