using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace GitDeployPro.Services.AgentApi
{
    public static class AgentScopes
    {
        public const string Read = "read";
        public const string Deploy = "deploy";
        public const string Upload = "upload";
        public const string Terminal = "terminal";

        public static readonly string[] All = { Read, Deploy, Upload, Terminal };
    }

    public sealed class AgentApiKey
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Name { get; set; } = "";

        /// <summary>SHA-256 (hex) of the key. The plain key is shown once at creation and never stored.</summary>
        public string Hash { get; set; } = "";

        public string Prefix { get; set; } = "";
        public List<string> Scopes { get; set; } = new();

        /// <summary>Project paths this key may touch. Empty = every project known to GitDeploy.</summary>
        public List<string> Projects { get; set; } = new();

        /// <summary>"ask" = a human approves each deploy / upload / terminal session in the app. "auto" = no prompt.</summary>
        public string Approval { get; set; } = "ask";

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? LastUsedUtc { get; set; }

        [JsonIgnore]
        public string ScopesText => string.Join(", ", Scopes);

        [JsonIgnore]
        public string ProjectsText => Projects.Count == 0 ? "all projects" : $"{Projects.Count} project(s)";

        [JsonIgnore]
        public string Summary => $"{Prefix}…  ·  {ScopesText}  ·  {Approval}  ·  {ProjectsText}";

        public bool Has(string scope) => Scopes.Contains(scope, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>API keys live in %AppData%\GitDeployPro\agent_api_keys.json (hashes only).</summary>
    public sealed class AgentApiKeyStore
    {
        public static AgentApiKeyStore Instance { get; } = new();

        private readonly object _gate = new();
        private readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GitDeployPro",
            "agent_api_keys.json");

        public event EventHandler? Changed;

        public List<AgentApiKey> List()
        {
            lock (_gate)
            {
                return Load();
            }
        }

        /// <summary>Creates a key and returns the plain text — the only time it is ever visible.</summary>
        public string Create(string name, IEnumerable<string> scopes, IEnumerable<string>? projects, string approval)
        {
            var raw = "gdp_" + Base64Url(RandomNumberGenerator.GetBytes(32));
            var key = new AgentApiKey
            {
                Name = string.IsNullOrWhiteSpace(name) ? "agent" : name.Trim(),
                Hash = HashOf(raw),
                Prefix = raw[..8],
                Scopes = scopes.Where(s => AgentScopes.All.Contains(s)).Distinct().ToList(),
                Projects = (projects ?? Array.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList(),
                Approval = string.Equals(approval, "auto", StringComparison.OrdinalIgnoreCase) ? "auto" : "ask"
            };

            lock (_gate)
            {
                var all = Load();
                all.Add(key);
                Save(all);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return raw;
        }

        public void Revoke(string id)
        {
            lock (_gate)
            {
                var all = Load();
                if (all.RemoveAll(k => string.Equals(k.Id, id, StringComparison.OrdinalIgnoreCase)) > 0)
                {
                    Save(all);
                }
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        public AgentApiKey? Authenticate(string? token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            var hash = Encoding.UTF8.GetBytes(HashOf(token.Trim()));
            lock (_gate)
            {
                var all = Load();
                foreach (var key in all)
                {
                    if (CryptographicOperations.FixedTimeEquals(hash, Encoding.UTF8.GetBytes(key.Hash ?? "")))
                    {
                        key.LastUsedUtc = DateTime.UtcNow;
                        Save(all);
                        return key;
                    }
                }
            }

            return null;
        }

        private List<AgentApiKey> Load()
        {
            try
            {
                if (File.Exists(_path))
                {
                    return JsonConvert.DeserializeObject<List<AgentApiKey>>(File.ReadAllText(_path)) ?? new();
                }
            }
            catch
            {
                // corrupt file → treated as empty (keys must be re-created)
            }

            return new();
        }

        private void Save(List<AgentApiKey> keys)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonConvert.SerializeObject(keys, Formatting.Indented));
        }

        private static string HashOf(string raw) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
