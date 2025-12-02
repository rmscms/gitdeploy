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
        public string SourceId { get; set; } = Guid.NewGuid().ToString();
    }
}

