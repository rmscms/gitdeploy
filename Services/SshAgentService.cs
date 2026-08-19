using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GitDeployPro.Services
{
    public sealed class GitSshSetupRequiredException : InvalidOperationException
    {
        public GitSshSetupRequiredException(string remoteUrl, string message)
            : base(message)
        {
            RemoteUrl = remoteUrl ?? string.Empty;
        }

        public string RemoteUrl { get; }
    }

    public sealed class GitSshStatus
    {
        public bool IsReady { get; set; }
        public bool NeedsOperatorSetup { get; set; }
        public string Message { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
        public string? KeyPath { get; set; }
        public string? PublicKey { get; set; }
        public string? SshExe { get; set; }
        public string Host { get; set; } = "github.com";
        public IReadOnlyList<string> DiscoveredKeys { get; set; } = Array.Empty<string>();
    }

    public class SshAgentService
    {
        private readonly ConfigurationService _configurationService = new();
        private static readonly string[] DefaultKeyNames =
        {
            "id_ed25519",
            "id_rsa",
            "id_ecdsa",
            "id_ed25519_sk",
            "id_ecdsa_sk"
        };

        public async Task EnsureDefaultKeyLoadedAsync(string? remoteUrl = null)
        {
            var status = await PrepareForRemoteAsync(remoteUrl ?? string.Empty);
            if (status.IsReady)
            {
                return;
            }

            throw new GitSshSetupRequiredException(
                remoteUrl ?? string.Empty,
                status.Message);
        }

        public async Task<GitSshStatus> PrepareForRemoteAsync(string remoteUrl)
        {
            var status = ProbeLocalState(remoteUrl);
            if (!LooksLikeSshRemote(remoteUrl))
            {
                status.IsReady = true;
                status.NeedsOperatorSetup = false;
                status.Message = "Remote uses HTTPS; SSH key is not required.";
                return status;
            }

            TryAddKeyToAgent(status.KeyPath);

            if (!string.IsNullOrWhiteSpace(status.KeyPath) && File.Exists(status.KeyPath))
            {
                RememberKeyPath(status.KeyPath);
                var test = await TestHostAsync(status.Host, status.KeyPath);
                status.Details = test.Details;
                if (test.IsReady)
                {
                    status.IsReady = true;
                    status.NeedsOperatorSetup = false;
                    status.Message = test.Message;
                    return status;
                }

                // Key exists (CMD-style). Let Git use IdentityFile even if BatchMode test
                // is noisy; only force the helper when GitHub clearly rejected the key.
                if (!test.NeedsOperatorSetup)
                {
                    status.IsReady = true;
                    status.NeedsOperatorSetup = false;
                    status.Message = "SSH private key found. Git will use it for this remote.";
                    return status;
                }

                status.IsReady = false;
                status.NeedsOperatorSetup = true;
                status.Message = test.Message;
                return status;
            }

            status.IsReady = false;
            status.NeedsOperatorSetup = true;
            status.Message = string.IsNullOrWhiteSpace(status.SshExe)
                ? "OpenSSH was not found. Install Git for Windows or Windows OpenSSH, then retry."
                : "No SSH private key was found. Add a key from GitHub/GitLab or generate one here.";
            return status;
        }

        public GitSshStatus ProbeLocalState(string? remoteUrl = null)
        {
            var host = ResolveSshHost(remoteUrl);
            var sshExe = FindSshExecutable();
            var keys = DiscoverPrivateKeys().ToList();
            var preferred = ResolvePreferredKey(keys);
            var pub = ReadPublicKey(preferred);

            return new GitSshStatus
            {
                Host = host,
                SshExe = sshExe,
                KeyPath = preferred,
                PublicKey = pub,
                DiscoveredKeys = keys,
                Message = preferred == null
                    ? "No default SSH key on this PC."
                    : $"Using key: {preferred}",
                Details = sshExe == null ? "ssh.exe not found." : $"ssh: {sshExe}"
            };
        }

        public string? TryGetGitSshCommand()
        {
            var sshExe = FindSshExecutable();
            if (string.IsNullOrWhiteSpace(sshExe))
            {
                return null;
            }

            // Match CMD/Git: let ssh offer default keys and agent identities.
            // Pinning IdentitiesOnly to one file hides a working second key.
            var customKey = LoadConfiguredKeyPath();
            if (!string.IsNullOrWhiteSpace(customKey)
                && File.Exists(customKey)
                && !IsDefaultHomeSshKey(customKey))
            {
                return $"\"{sshExe}\" -i \"{customKey}\" -o StrictHostKeyChecking=accept-new";
            }

            return $"\"{sshExe}\" -o StrictHostKeyChecking=accept-new";
        }

        public void RememberKeyPath(string? keyPath)
        {
            if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
            {
                return;
            }

            var config = _configurationService.LoadGlobalConfig();
            if (string.Equals(config.DefaultSshKeyPath, keyPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            config.DefaultSshKeyPath = keyPath;
            _configurationService.SaveGlobalConfig(config);
        }

        public async Task<GitSshStatus> TestHostAsync(string host, string? keyPath = null)
        {
            var sshExe = FindSshExecutable();
            var targetHost = string.IsNullOrWhiteSpace(host) ? "github.com" : host.Trim();
            var discovered = DiscoverPrivateKeys().ToList();
            var requestedKey = string.IsNullOrWhiteSpace(keyPath) ? null : keyPath.Trim();
            var displayKey = !string.IsNullOrWhiteSpace(requestedKey) && File.Exists(requestedKey)
                ? requestedKey
                : ResolvePreferredKey(discovered);
            var status = new GitSshStatus
            {
                Host = targetHost,
                SshExe = sshExe,
                KeyPath = displayKey,
                PublicKey = ReadPublicKey(displayKey)
            };

            if (string.IsNullOrWhiteSpace(sshExe))
            {
                status.NeedsOperatorSetup = true;
                status.Message = "ssh.exe was not found.";
                return status;
            }

            var attempts = new List<(string? Key, bool IdentitiesOnly)>
            {
                // Same as CMD: ssh offers id_rsa, id_ed25519, agent, etc.
                (null, false)
            };

            foreach (var key in discovered)
            {
                attempts.Add((key, false));
            }

            if (!string.IsNullOrWhiteSpace(requestedKey)
                && File.Exists(requestedKey)
                && !discovered.Contains(requestedKey, StringComparer.OrdinalIgnoreCase))
            {
                attempts.Add((requestedKey, false));
            }

            SshCommandResult? lastRejected = null;
            foreach (var attempt in attempts)
            {
                var result = await RunSshAuthTestAsync(sshExe, targetHost, attempt.Key, attempt.IdentitiesOnly);
                var combined = $"{result.Output}\n{result.Error}";
                status.Details = combined.Trim();

                if (LooksLikeSuccessfulAuth(combined) || result.ExitCode == 0 && !LooksLikeAuthRejected(combined))
                {
                    status.IsReady = true;
                    status.KeyPath = attempt.Key ?? displayKey;
                    status.PublicKey = ReadPublicKey(status.KeyPath);
                    status.Message = $"SSH authenticated to {targetHost}.";
                    RememberKeyPath(status.KeyPath);
                    return status;
                }

                if (LooksLikePassphraseRequired(combined))
                {
                    status.NeedsOperatorSetup = true;
                    status.KeyPath = attempt.Key ?? displayKey;
                    status.PublicKey = ReadPublicKey(status.KeyPath);
                    status.Message = "This key has a passphrase. Start the Windows OpenSSH Authentication Agent and load the key, or generate a key without a passphrase.";
                    return status;
                }

                if (LooksLikeAuthRejected(combined))
                {
                    lastRejected = result;
                    continue;
                }
            }

            if (lastRejected != null)
            {
                var combined = $"{lastRejected.Output}\n{lastRejected.Error}";
                status.Details = combined.Trim();
                status.NeedsOperatorSetup = true;
                status.Message = $"GitHub/Git rejected the SSH keys on this PC for {targetHost}. Copy the public key below into the host SSH keys page, then Test again.";
                return status;
            }

            status.NeedsOperatorSetup = string.IsNullOrWhiteSpace(displayKey);
            status.Message = string.IsNullOrWhiteSpace(status.Details)
                ? $"SSH test to {targetHost} failed."
                : TrimSshError(status.Details);
            return status;
        }

        private static Task<SshCommandResult> RunSshAuthTestAsync(
            string sshExe,
            string host,
            string? identityFile,
            bool identitiesOnly)
        {
            var args = new StringBuilder();
            args.Append("-o BatchMode=yes -o StrictHostKeyChecking=accept-new -o ConnectTimeout=12 ");
            if (!string.IsNullOrWhiteSpace(identityFile) && File.Exists(identityFile))
            {
                args.Append("-i \"").Append(identityFile).Append("\" ");
                if (identitiesOnly)
                {
                    args.Append("-o IdentitiesOnly=yes ");
                }
            }

            args.Append("-T git@").Append(host);
            return RunProcessAsync(sshExe, args.ToString());
        }

        public async Task<string> GenerateEd25519KeyAsync(string comment)
        {
            var keygen = FindSshKeygen();
            if (string.IsNullOrWhiteSpace(keygen))
            {
                throw new InvalidOperationException("ssh-keygen was not found. Install Git for Windows or Windows OpenSSH.");
            }

            var sshDir = GetSshDirectory();
            Directory.CreateDirectory(sshDir);
            var keyPath = Path.Combine(sshDir, "id_ed25519");
            if (File.Exists(keyPath))
            {
                RememberKeyPath(keyPath);
                return keyPath;
            }

            var safeComment = string.IsNullOrWhiteSpace(comment) ? "gitdeploypro" : comment.Trim();
            var result = await Task.Run(() => RunProcess(keygen, new[]
            {
                "-t", "ed25519",
                "-f", keyPath,
                "-N", "",
                "-C", safeComment,
                "-q"
            }));
            if (result.ExitCode != 0 || !File.Exists(keyPath))
            {
                var detail = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(detail)
                        ? "Failed to generate an SSH key."
                        : detail.Trim());
            }

            RememberKeyPath(keyPath);
            return keyPath;
        }

        public static string? ReadPublicKey(string? privateKeyPath)
        {
            if (string.IsNullOrWhiteSpace(privateKeyPath))
            {
                return null;
            }

            var pubPath = privateKeyPath.EndsWith(".pub", StringComparison.OrdinalIgnoreCase)
                ? privateKeyPath
                : privateKeyPath + ".pub";
            if (!File.Exists(pubPath))
            {
                return null;
            }

            try
            {
                return File.ReadAllText(pubPath).Trim();
            }
            catch
            {
                return null;
            }
        }

        public static string GetKeysSettingsUrl(string host)
        {
            var h = (host ?? string.Empty).Trim().ToLowerInvariant();
            if (h.Contains("gitlab", StringComparison.Ordinal))
            {
                return "https://gitlab.com/-/user_settings/ssh_keys";
            }

            if (h.Contains("bitbucket", StringComparison.Ordinal) || h.Contains("atlassian", StringComparison.Ordinal))
            {
                return "https://bitbucket.org/account/settings/ssh-keys/";
            }

            return "https://github.com/settings/keys";
        }

        public static bool LooksLikeSshRemote(string? remoteUrl)
        {
            if (string.IsNullOrWhiteSpace(remoteUrl))
            {
                return false;
            }

            var trimmed = remoteUrl.Trim();
            return trimmed.StartsWith("git@", StringComparison.OrdinalIgnoreCase)
                   || trimmed.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase);
        }

        public static string ResolveSshHost(string? remoteUrl)
        {
            var trimmed = (remoteUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return "github.com";
            }

            var gitAt = Regex.Match(trimmed, @"^git@([^:]+):", RegexOptions.IgnoreCase);
            if (gitAt.Success)
            {
                return gitAt.Groups[1].Value;
            }

            var sshUri = Regex.Match(trimmed, @"^ssh://(?:git@)?([^/]+)/", RegexOptions.IgnoreCase);
            if (sshUri.Success)
            {
                return sshUri.Groups[1].Value;
            }

            return "github.com";
        }

        public static bool LooksLikeSshAuthFailure(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return text.IndexOf("Permission denied (publickey)", StringComparison.OrdinalIgnoreCase) >= 0
                   || text.IndexOf("Could not read from remote repository", StringComparison.OrdinalIgnoreCase) >= 0
                   || text.IndexOf("No SSH keys were loaded", StringComparison.OrdinalIgnoreCase) >= 0
                   || text.IndexOf("ssh-add could not be started", StringComparison.OrdinalIgnoreCase) >= 0
                   || text.IndexOf("Host key verification failed", StringComparison.OrdinalIgnoreCase) >= 0
                   || text.IndexOf("Authentication failed", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public IEnumerable<string> DiscoverPrivateKeys()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var config = _configurationService.LoadGlobalConfig();
            if (!string.IsNullOrWhiteSpace(config.DefaultSshKeyPath) && File.Exists(config.DefaultSshKeyPath))
            {
                if (seen.Add(config.DefaultSshKeyPath))
                {
                    yield return config.DefaultSshKeyPath;
                }
            }

            var sshDir = GetSshDirectory();
            foreach (var name in DefaultKeyNames)
            {
                var path = Path.Combine(sshDir, name);
                if (File.Exists(path) && seen.Add(path))
                {
                    yield return path;
                }
            }
        }

        private static string? _resolvedSsh;
        private static string? _resolvedSshAdd;
        private static string? _resolvedSshKeygen;
        private static bool _toolsResolved;

        public static string? FindSshExecutable()
        {
            EnsureToolsResolved();
            return _resolvedSsh;
        }

        public static string? FindSshKeygen()
        {
            EnsureToolsResolved();
            return _resolvedSshKeygen;
        }

        public static string? FindSshAdd()
        {
            EnsureToolsResolved();
            return _resolvedSshAdd;
        }

        private static void EnsureToolsResolved()
        {
            if (_toolsResolved)
            {
                return;
            }

            _resolvedSsh = FindTool("ssh.exe");
            _resolvedSshAdd = FindTool("ssh-add.exe");
            _resolvedSshKeygen = FindTool("ssh-keygen.exe");
            _toolsResolved = true;
        }

        private string? ResolvePreferredKey(IEnumerable<string> keys)
        {
            return keys.FirstOrDefault(File.Exists);
        }

        private void TryAddKeyToAgent(string? keyPath)
        {
            var sshAdd = FindSshAdd();
            if (string.IsNullOrWhiteSpace(sshAdd))
            {
                return;
            }

            try
            {
                var list = RunProcess(sshAdd, "-l", timeoutMs: 5000);
                var hasIds = list.ExitCode == 0
                             && list.Output.IndexOf("The agent has no identities", StringComparison.OrdinalIgnoreCase) < 0;
                if (hasIds)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(keyPath) && File.Exists(keyPath))
                {
                    RunProcess(sshAdd, $"\"{keyPath}\"", timeoutMs: 6000);
                }
            }
            catch
            {
                // Agent is optional; IdentityFile is the reliable path for the WPF process.
            }
        }

        private static string GetSshDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".ssh");
        }

        private static string? FindTool(string fileName)
        {
            var candidates = new List<string>();

            var git = FindGitUsrBin();
            if (!string.IsNullOrWhiteSpace(git))
            {
                candidates.Add(Path.Combine(git, fileName));
            }

            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            candidates.Add(Path.Combine(system, "OpenSSH", fileName));

            var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var dir in pathDirs)
            {
                try
                {
                    candidates.Add(Path.Combine(dir.Trim().Trim('"'), fileName));
                }
                catch
                {
                    // ignore bad PATH entries
                }
            }

            foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        private string? LoadConfiguredKeyPath()
        {
            try
            {
                var path = _configurationService.LoadGlobalConfig().DefaultSshKeyPath;
                return string.IsNullOrWhiteSpace(path) ? null : path.Trim();
            }
            catch
            {
                return null;
            }
        }

        private static bool IsDefaultHomeSshKey(string keyPath)
        {
            var sshDir = GetSshDirectory();
            var full = Path.GetFullPath(keyPath);
            foreach (var name in DefaultKeyNames)
            {
                var candidate = Path.GetFullPath(Path.Combine(sshDir, name));
                if (string.Equals(full, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string? FindGitUsrBin()
        {
            var roots = new[]
            {
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                @"C:\Program Files",
                @"C:\Program Files (x86)",
                @"C:\laragon\bin"
            };

            foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)))
            {
                foreach (var gitName in new[] { "Git", "git" })
                {
                    var usr = Path.Combine(root!, gitName, "usr", "bin");
                    if (File.Exists(Path.Combine(usr, "ssh.exe")))
                    {
                        return usr;
                    }
                }
            }

            var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var dir in pathDirs)
            {
                try
                {
                    var gitExe = Path.Combine(dir.Trim().Trim('"'), "git.exe");
                    if (!File.Exists(gitExe))
                    {
                        continue;
                    }

                    var gitRoot = Directory.GetParent(Path.GetDirectoryName(gitExe)!);
                    var usr = gitRoot == null ? null : Path.Combine(gitRoot.FullName, "usr", "bin");
                    if (!string.IsNullOrWhiteSpace(usr) && File.Exists(Path.Combine(usr, "ssh.exe")))
                    {
                        return usr;
                    }
                }
                catch
                {
                    // ignore bad PATH entries
                }
            }

            return null;
        }

        private static bool LooksLikeSuccessfulAuth(string text)
        {
            return text.IndexOf("successfully authenticated", StringComparison.OrdinalIgnoreCase) >= 0
                   || text.IndexOf("Welcome to GitLab", StringComparison.OrdinalIgnoreCase) >= 0
                   || Regex.IsMatch(text, @"Hi\s+\S+!", RegexOptions.IgnoreCase);
        }

        private static bool LooksLikeAuthRejected(string text)
        {
            return text.IndexOf("Permission denied (publickey)", StringComparison.OrdinalIgnoreCase) >= 0
                   || text.IndexOf("Permission denied (publickey,keyboard-interactive)", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool LooksLikePassphraseRequired(string text)
        {
            return text.IndexOf("passphrase", StringComparison.OrdinalIgnoreCase) >= 0
                   || text.IndexOf("Enter passphrase", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string TrimSshError(string details)
        {
            var line = details
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0 && !l.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(line) ? details.Trim() : line;
        }

        private static Task<SshCommandResult> RunProcessAsync(string fileName, string arguments)
            => Task.Run(() => RunProcess(fileName, arguments));

        private static SshCommandResult RunProcess(string fileName, string arguments, int timeoutMs = 20000)
        {
            var startInfo = CreateSshStartInfo(fileName);
            startInfo.Arguments = arguments;
            return RunProcessCore(startInfo, timeoutMs);
        }

        private static SshCommandResult RunProcess(string fileName, IReadOnlyList<string> argumentList, int timeoutMs = 20000)
        {
            var startInfo = CreateSshStartInfo(fileName);
            foreach (var argument in argumentList)
            {
                startInfo.ArgumentList.Add(argument);
            }

            return RunProcessCore(startInfo, timeoutMs);
        }

        private static ProcessStartInfo CreateSshStartInfo(string fileName)
        {
            return new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
        }

        private static SshCommandResult RunProcessCore(ProcessStartInfo startInfo, int timeoutMs)
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(timeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore kill failures
                }

                return new SshCommandResult
                {
                    ExitCode = -1,
                    Output = output ?? string.Empty,
                    Error = string.IsNullOrWhiteSpace(error)
                        ? "SSH command timed out."
                        : error
                };
            }

            return new SshCommandResult
            {
                ExitCode = process.ExitCode,
                Output = output ?? string.Empty,
                Error = error ?? string.Empty
            };
        }

        private sealed class SshCommandResult
        {
            public int ExitCode { get; set; }
            public string Output { get; set; } = "";
            public string Error { get; set; } = "";
        }
    }
}
