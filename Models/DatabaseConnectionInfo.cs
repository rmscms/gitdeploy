using System;

namespace GitDeployPro.Models
{
    public class DatabaseConnectionInfo
    {
        public string Name { get; set; } = "Database";
        public DatabaseType DbType { get; set; } = DatabaseType.MySQL;
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 3306;
        public string Username { get; set; } = "root";
        public string Password { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public bool IsLocal { get; set; }
        public bool UseSshTunnel { get; set; }
        public string SshHost { get; set; } = "127.0.0.1";
        public int SshPort { get; set; } = 22;
        public string SshUsername { get; set; } = string.Empty;
        public string SshPassword { get; set; } = string.Empty;
        public string SshPrivateKeyPath { get; set; } = string.Empty;
        public string SourceId { get; set; } = Guid.NewGuid().ToString();
    }
}

