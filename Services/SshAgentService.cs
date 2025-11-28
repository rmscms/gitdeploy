using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitDeployPro.Services
{
    public class SshAgentService
    {
        private readonly ConfigurationService _configurationService = new ConfigurationService();
        private static readonly string[] DefaultKeyCandidates = new[]
        {
            "%USERPROFILE%\\.ssh\\id_ed25519",
            "%USERPROFILE%\\.ssh\\id_rsa",
            "%USERPROFILE%\\.ssh\\id_ecdsa"
        };

        public async Task EnsureDefaultKeyLoadedAsync()
        {
            var listResult = await RunSshAddAsync("-l");
            bool agentHasIds = listResult.ExitCode == 0 &&
                               listResult.Output.IndexOf("The agent has no identities", StringComparison.OrdinalIgnoreCase) < 0;

            if (agentHasIds)
            {
                return;
            }

            var config = _configurationService.LoadGlobalConfig();
            var candidates = BuildCandidateList(config);

            foreach (var candidate in candidates)
            {
                var expanded = ExpandPath(candidate);
                if (string.IsNullOrWhiteSpace(expanded) || !File.Exists(expanded))
                {
                    continue;
                }

                var addResult = await RunSshAddAsync($"\"{expanded}\"");
                if (addResult.ExitCode == 0)
                {
                    config.DefaultSshKeyPath = expanded;
                    _configurationService.SaveGlobalConfig(config);
                    return;
                }
            }

            if (listResult.ExitCode != 0 &&
                listResult.Error.IndexOf("Could not open", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                throw new InvalidOperationException("SSH agent is not running. Start the 'ssh-agent' service (or launch Git Bash) and retry.");
            }

            throw new InvalidOperationException("No SSH keys were loaded automatically. Add one with 'ssh-add' or switch the remote to HTTPS.");
        }

        private IEnumerable<string> BuildCandidateList(ConfigurationService.GlobalConfig config)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(config.DefaultSshKeyPath))
            {
                seen.Add(config.DefaultSshKeyPath);
                yield return config.DefaultSshKeyPath;
            }

            foreach (var candidate in DefaultKeyCandidates)
            {
                if (seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }

        private static string ExpandPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            return Environment.ExpandEnvironmentVariables(path);
        }

        private static Task<SshCommandResult> RunSshAddAsync(string arguments)
        {
            return Task.Run(() =>
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ssh-add",
                        Arguments = arguments,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    }
                };

                try
                {
                    process.Start();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("ssh-add could not be started. Ensure Git or OpenSSH is installed.", ex);
                }

                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                return new SshCommandResult
                {
                    ExitCode = process.ExitCode,
                    Output = output,
                    Error = error
                };
            });
        }

        private class SshCommandResult
        {
            public int ExitCode { get; set; }
            public string Output { get; set; } = "";
            public string Error { get; set; } = "";
        }
    }
}

