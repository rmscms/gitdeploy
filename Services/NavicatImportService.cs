using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using GitDeployPro.Models;

namespace GitDeployPro.Services
{
    public sealed class NavicatImportService
    {
        public NavicatImportResult Import(string xmlPath)
        {
            if (string.IsNullOrWhiteSpace(xmlPath))
            {
                throw new ArgumentException("Path is required.", nameof(xmlPath));
            }

            if (!File.Exists(xmlPath))
            {
                throw new FileNotFoundException("Navicat export not found.", xmlPath);
            }

            var doc = XDocument.Load(xmlPath);
            var result = new NavicatImportResult();

            var connections = doc.Root?.Elements("Connection") ?? Enumerable.Empty<XElement>();
            foreach (var node in connections)
            {
                var profile = CreateProfile(node, result);
                if (profile != null)
                {
                    result.Profiles.Add(profile);
                }
            }

            return result;
        }

        private static ConnectionProfile? CreateProfile(XElement node, NavicatImportResult result)
        {
            var name = GetAttr(node, "ConnectionName");
            if (string.IsNullOrWhiteSpace(name))
            {
                result.Warnings.Add("Skipped a connection without a name.");
                return null;
            }

            var dbType = ParseDbType(GetAttr(node, "ConnType"));
            if (dbType == DatabaseType.None)
            {
                result.Warnings.Add($"Skipped '{name}' because the database type '{GetAttr(node, "ConnType")}' is not supported.");
                return null;
            }

            var useSsh = ParseBool(GetAttr(node, "SSH"));
            var host = useSsh ? GetAttr(node, "SSH_Host") : GetAttr(node, "Host");
            var port = useSsh ? ParseInt(GetAttr(node, "SSH_Port"), useSsh ? 22 : 21) : ParseInt(GetAttr(node, "Port"), 21);
            var username = useSsh ? GetAttr(node, "SSH_UserName") : GetAttr(node, "UserName");
            var sshPrivateKey = GetAttr(node, "SSH_PrivateKey");

            if (string.IsNullOrWhiteSpace(host))
            {
                result.Warnings.Add($"Skipped '{name}' because host was empty.");
                return null;
            }

            var profile = new ConnectionProfile
            {
                Name = name.Trim(),
                UseSSH = useSsh,
                Host = host.Trim(),
                Port = port,
                Username = username.Trim(),
                Password = EncryptionService.Encrypt(string.Empty),
                PrivateKeyPath = sshPrivateKey ?? string.Empty,
                PassiveMode = true,
                ShowHiddenFiles = true,
                KeepAliveSeconds = 300,
                DbType = dbType,
                DbHost = GetAttr(node, "Host").Trim(),
                DbPort = ParseInt(GetAttr(node, "Port"), 3306),
                DbUsername = GetAttr(node, "UserName").Trim(),
                DbPassword = EncryptionService.Encrypt(string.Empty),
                DbName = ResolveDatabaseName(node)
            };

            if (profile.UseSSH && profile.Port == 0)
            {
                profile.Port = 22;
            }

            if (profile.Port == 0)
            {
                profile.Port = profile.UseSSH ? 22 : 21;
            }

            if (string.IsNullOrWhiteSpace(profile.Username))
            {
                profile.Username = profile.UseSSH ? "root" : "ftp";
            }

            return profile;
        }

        private static string ResolveDatabaseName(XElement node)
        {
            var direct = GetAttr(node, "Database");
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct.Trim();
            }

            var advanceNode = node.Element("Advance");
            if (advanceNode != null)
            {
                var value = GetAttr(advanceNode, "Database");
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return string.Empty;
        }

        private static DatabaseType ParseDbType(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return DatabaseType.None;

            return value.Trim().ToUpperInvariant() switch
            {
                "MYSQL" => DatabaseType.MySQL,
                "MARIADB" => DatabaseType.MariaDB,
                "POSTGRESQL" => DatabaseType.PostgreSQL,
                "SQLSERVER" => DatabaseType.SQLServer,
                "MONGODB" => DatabaseType.MongoDB,
                "REDIS" => DatabaseType.Redis,
                "SQLITE" => DatabaseType.SQLite,
                _ => DatabaseType.None
            };
        }

        private static string GetAttr(XElement node, string name) => node.Attribute(name)?.Value ?? string.Empty;

        private static bool ParseBool(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("1", StringComparison.OrdinalIgnoreCase);
        }

        private static int ParseInt(string? value, int fallback)
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            return fallback;
        }
    }

    public sealed class NavicatImportResult
    {
        public List<ConnectionProfile> Profiles { get; } = new();
        public List<string> Warnings { get; } = new();
    }
}


