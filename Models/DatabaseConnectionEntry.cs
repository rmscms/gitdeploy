using System;
using GitDeployPro.Services;

namespace GitDeployPro.Models
{
    public class DatabaseConnectionEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public DatabaseType DbType { get; set; } = DatabaseType.MySQL;
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 3306;
        public string Username { get; set; } = "root";
        public string Password { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public bool IsLocal { get; set; }
        public bool IsFromProfile { get; set; }
        public bool UseSshTunnel { get; set; }
        public string SshHost { get; set; } = "127.0.0.1";
        public int SshPort { get; set; } = 22;
        public string SshUsername { get; set; } = string.Empty;
        public string SshPassword { get; set; } = string.Empty;
        public string SshPrivateKeyPath { get; set; } = string.Empty;

        public string TypeIcon => DbType switch
        {
            DatabaseType.MariaDB => "🛢️",
            DatabaseType.MySQL => "🐬",
            DatabaseType.PostgreSQL => "🐘",
            DatabaseType.SQLServer => "🗄️",
            _ => "🛢️"
        };

        public bool SupportsCurrentVersion => DbType == DatabaseType.MySQL || DbType == DatabaseType.MariaDB;

        public DatabaseConnectionInfo ToConnectionInfo()
        {
            return new DatabaseConnectionInfo
            {
                Name = (Name ?? string.Empty).Trim(),
                DbType = DbType,
                Host = string.IsNullOrWhiteSpace(Host) ? "127.0.0.1" : Host.Trim(),
                Port = Port,
                Username = (Username ?? string.Empty).Trim(),
                Password = Password,
                DatabaseName = DatabaseName?.Trim() ?? string.Empty,
                IsLocal = IsLocal,
                UseSshTunnel = UseSshTunnel,
                SshHost = string.IsNullOrWhiteSpace(SshHost) ? "127.0.0.1" : SshHost.Trim(),
                SshPort = SshPort,
                SshUsername = (SshUsername ?? string.Empty).Trim(),
                SshPassword = SshPassword,
                SshPrivateKeyPath = SshPrivateKeyPath?.Trim() ?? string.Empty,
                SourceId = Id
            };
        }

        public static DatabaseConnectionEntry CreateLocalDefault()
        {
            return new DatabaseConnectionEntry
            {
                Id = "local-default",
                Name = "Localhost",
                Description = "127.0.0.1:3306 · root",
                DbType = DatabaseType.MySQL,
                Host = "127.0.0.1",
                Port = 3306,
                Username = "root",
                IsLocal = true,
                UseSshTunnel = false,
                IsFromProfile = false
            };
        }

        public static DatabaseConnectionEntry FromProfile(ConnectionProfile profile)
        {
            return new DatabaseConnectionEntry
            {
                Id = profile.Id ?? Guid.NewGuid().ToString(),
                Name = string.IsNullOrWhiteSpace(profile.Name) ? "Database Connection" : profile.Name.Trim(),
                Description = $"{profile.DbHost}:{(profile.DbPort <= 0 ? 3306 : profile.DbPort)} · {profile.DbUsername}",
                DbType = profile.DbType == DatabaseType.None ? DatabaseType.MySQL : profile.DbType,
                Host = string.IsNullOrWhiteSpace(profile.DbHost) ? "127.0.0.1" : profile.DbHost.Trim(),
                Port = profile.DbPort <= 0 ? 3306 : profile.DbPort,
                Username = string.IsNullOrWhiteSpace(profile.DbUsername) ? "root" : profile.DbUsername.Trim(),
                Password = EncryptionService.Decrypt(profile.DbPassword),
                DatabaseName = profile.DbName?.Trim() ?? string.Empty,
                IsLocal = false,
                UseSshTunnel = profile.UseSSH,
                SshHost = string.IsNullOrWhiteSpace(profile.Host) ? "127.0.0.1" : profile.Host.Trim(),
                SshPort = profile.Port <= 0 ? 22 : profile.Port,
                SshUsername = string.IsNullOrWhiteSpace(profile.Username) ? "root" : profile.Username.Trim(),
                SshPassword = EncryptionService.Decrypt(profile.Password),
                SshPrivateKeyPath = profile.PrivateKeyPath ?? string.Empty,
                IsFromProfile = true
            };
        }
    }
}

