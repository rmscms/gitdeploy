using System;
using System.Collections.Generic;
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

    /// <summary>
    /// How Telegram and deploy shortcuts treat this project (web FTP vs desktop vs agent-only).
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ProjectKind
    {
        WebFtpDeploy,
        WindowsDesktop,
        NoDeploy
    }

    public class ProjectConfig
    {
        // New Approach: Reference a Connection Profile
        public string ConnectionProfileId { get; set; } = "";

        /// <summary>FTP/SFTP profiles assigned to this project. Empty means fall back to <see cref="ConnectionProfileId"/>.</summary>
        public List<string> ConnectionProfileIds { get; set; } = new List<string>();

        /// <summary>True after the user confirms the sync target (or the project has a single FTP).</summary>
        public bool FtpSyncTargetConfirmed { get; set; }

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

        /// <summary>
        /// Web FTP deploy vs Windows desktop vs no deploy — drives Telegram keyboard and deploy hints.
        /// </summary>
        public ProjectKind ProjectKind { get; set; } = ProjectKind.WebFtpDeploy;

        /// <summary>
        /// Extra folders Cursor CLI may access via --add-dir (primary root remains LocalProjectPath).
        /// </summary>
        public List<string> CursorExtraRoots { get; set; } = new List<string>();

        [JsonIgnore]
        public string FtpPasswordDecrypted => Services.EncryptionService.Decrypt(FtpPassword);
    }
}