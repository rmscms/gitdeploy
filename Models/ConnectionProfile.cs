using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace GitDeployPro.Models
{
    public enum DatabaseType
    {
        None,
        MySQL,
        MariaDB,
        PostgreSQL,
        SQLServer,
        MongoDB,
        Redis,
        SQLite
    }

    public class ConnectionProfile : INotifyPropertyChanged
    {
        private string _name = "New Connection";

        public string Id { get; set; } = Guid.NewGuid().ToString();
        
        public string Name 
        { 
            get => _name;
            set { _name = value; OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(DisplayName)); }
        }

        // --- FTP/SSH Connection ---
        public string Host { get; set; } = "";
        public int Port { get; set; } = 21;
        public string Username { get; set; } = "";
        public string Password { get; set; } = ""; // Encrypted
        public bool UseSSH { get; set; } = false; // False = FTP, True = SFTP/SSH
        public string PrivateKeyPath { get; set; } = "";
        
        // --- Advanced FTP/SSH ---
        public string RemotePath { get; set; } = "/";
        public string WebServerUrl { get; set; } = "http://";
        public bool PassiveMode { get; set; } = true;
        public bool ShowHiddenFiles { get; set; } = true;
        public int KeepAliveSeconds { get; set; } = 300;
        public List<PathMapping> PathMappings { get; set; } = new List<PathMapping>();
        private bool _isProjectDefault;
        public bool IsProjectDefault
        {
            get => _isProjectDefault;
            set
            {
                if (_isProjectDefault != value)
                {
                    _isProjectDefault = value;
                    OnPropertyChanged(nameof(IsProjectDefault));
                }
            }
        }

        // --- Database Configuration ---
        public DatabaseType DbType { get; set; } = DatabaseType.None;
        public string DbHost { get; set; } = "127.0.0.1"; // Default to localhost (via SSH Tunnel)
        public int DbPort { get; set; } = 3306;
        public string DbUsername { get; set; } = "root";
        public string DbPassword { get; set; } = ""; // Encrypted
        public string DbName { get; set; } = "";

        // For UI Display
        public string DisplayName => $"{Name} ({Host})";
        
        public string ProtocolIcon 
        {
            get 
            {
                if (DbType != DatabaseType.None) return "🛢️";
                return UseSSH ? "🔒" : "📂"; 
            }
        }

        public string ProtocolBadgeText => UseSSH ? "S" : "F";
        public string ProtocolBadgeColor => UseSSH ? "#7C4DFF" : "#34C759";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}