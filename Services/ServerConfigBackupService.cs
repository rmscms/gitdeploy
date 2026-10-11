using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GitDeployPro.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GitDeployPro.Services
{
    public sealed class ServerConfigBackupService
    {
        private const string FormatId = "gitdeploypro-server-backup";
        private const int FormatVersion = 1;
        private const int SaltSize = 16;
        private const int NonceSize = 12;
        private const int KeySize = 32;
        private const int Pbkdf2Iterations = 120_000;

        private readonly ConfigurationService _configService = new();

        public sealed class ServerBackupPayload
        {
            public List<ConnectionProfile> Connections { get; set; } = new();
            public List<SessionFolder> SessionFolders { get; set; } = new();
            public ProjectConfig? ProjectConfig { get; set; }
            public string? ProjectPath { get; set; }
        }

        public void ExportToFile(string filePath, string password, string? projectPath)
        {
            if (string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException("Set a server backup password in Settings first.");
            }

            var payload = new ServerBackupPayload
            {
                Connections = _configService.LoadConnections(),
                SessionFolders = _configService.LoadSessionFolders(),
                ProjectConfig = string.IsNullOrWhiteSpace(projectPath)
                    ? null
                    : _configService.LoadProjectConfig(projectPath),
                ProjectPath = projectPath
            };

            var plainJson = JsonConvert.SerializeObject(payload, Formatting.Indented);
            var envelope = EncryptEnvelope(plainJson, password);
            File.WriteAllText(filePath, envelope, Encoding.UTF8);
        }

        public ServerBackupPayload ImportFromFile(string filePath, string password)
        {
            if (string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException("Set a server backup password in Settings first.");
            }

            var envelope = File.ReadAllText(filePath, Encoding.UTF8);
            var plainJson = DecryptEnvelope(envelope, password);
            var payload = JsonConvert.DeserializeObject<ServerBackupPayload>(plainJson)
                          ?? throw new InvalidOperationException("Backup file is empty or invalid.");
            return payload;
        }

        public void ApplyImport(ServerBackupPayload payload, bool mergeConnections, string? projectPath)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            var incoming = payload.Connections ?? new List<ConnectionProfile>();
            if (mergeConnections)
            {
                foreach (var profile in incoming)
                {
                    if (profile == null || string.IsNullOrWhiteSpace(profile.Id))
                    {
                        continue;
                    }

                    _configService.AddOrUpdateConnection(profile);
                }
            }
            else
            {
                _configService.SaveConnections(incoming.Where(p => p != null).ToList());
            }

            if (payload.SessionFolders != null && payload.SessionFolders.Count > 0)
            {
                _configService.SaveSessionFolders(payload.SessionFolders);
            }

            if (payload.ProjectConfig != null && !string.IsNullOrWhiteSpace(projectPath))
            {
                payload.ProjectConfig.LocalProjectPath = projectPath;
                _configService.SaveProjectConfig(payload.ProjectConfig);
            }
        }

        private static string EncryptEnvelope(string plainJson, string password)
        {
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var key = DeriveKey(password, salt);
            var plainBytes = Encoding.UTF8.GetBytes(plainJson);
            var cipher = new byte[plainBytes.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(key, tag.Length))
            {
                aes.Encrypt(nonce, plainBytes, cipher, tag);
            }

            var doc = new JObject
            {
                ["format"] = FormatId,
                ["version"] = FormatVersion,
                ["exportedUtc"] = DateTime.UtcNow.ToString("o"),
                ["salt"] = Convert.ToBase64String(salt),
                ["nonce"] = Convert.ToBase64String(nonce),
                ["tag"] = Convert.ToBase64String(tag),
                ["cipher"] = Convert.ToBase64String(cipher)
            };
            return doc.ToString(Formatting.Indented);
        }

        private static string DecryptEnvelope(string envelopeJson, string password)
        {
            var doc = JObject.Parse(envelopeJson);
            if (!string.Equals(doc.Value<string>("format"), FormatId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("This file is not a GitDeployPro server backup.");
            }

            var salt = Convert.FromBase64String(doc.Value<string>("salt") ?? string.Empty);
            var nonce = Convert.FromBase64String(doc.Value<string>("nonce") ?? string.Empty);
            var tag = Convert.FromBase64String(doc.Value<string>("tag") ?? string.Empty);
            var cipher = Convert.FromBase64String(doc.Value<string>("cipher") ?? string.Empty);
            var key = DeriveKey(password, salt);
            var plain = new byte[cipher.Length];
            using (var aes = new AesGcm(key, tag.Length))
            {
                aes.Decrypt(nonce, cipher, tag, plain);
            }

            return Encoding.UTF8.GetString(plain);
        }

        private static byte[] DeriveKey(string password, byte[] salt)
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                Pbkdf2Iterations,
                HashAlgorithmName.SHA256,
                KeySize);
        }
    }
}
