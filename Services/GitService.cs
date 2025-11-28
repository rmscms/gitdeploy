using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GitDeployPro.Pages;
using GitDeployPro.Models; // For ProjectConfig
using Newtonsoft.Json;

namespace GitDeployPro.Services
{
    public class GitService
    {
        private static string _workingDirectory = Directory.GetCurrentDirectory();

        public GitService()
        {
            try
            {
                var configService = new ConfigurationService();
                var globalConfig = configService.LoadGlobalConfig();
                if (!string.IsNullOrEmpty(globalConfig.LastProjectPath) && Directory.Exists(globalConfig.LastProjectPath))
                {
                    _workingDirectory = globalConfig.LastProjectPath;
                }
            }
            catch { }
        }

        public static void SetWorkingDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                _workingDirectory = path;
            }
        }

        public bool IsGitRepository()
        {
            return Directory.Exists(Path.Combine(_workingDirectory, ".git"));
        }

        public async Task InitRepoAsync(List<string> branches, string remoteUrl)
        {
            if (IsGitRepository()) return;

            // 1. Init
            await RunGitCommandAsync("init");

            EnsureGitFolderHidden();

            // 2. Add all files
            await RunGitCommandAsync("add .");

            // 3. Initial commit
            try 
            {
                await RunGitCommandAsync("commit -m \"Initial commit by GitDeploy Pro\"");
            }
            catch 
            {
                try
                {
                    File.WriteAllText(Path.Combine(_workingDirectory, "README.md"), "# Project initialized by GitDeploy Pro");
                    await RunGitCommandAsync("add README.md");
                    await RunGitCommandAsync("commit -m \"Initial commit\"");
                }
                catch { }
            }

            // 4. Create Branches
            if (branches != null && branches.Any())
            {
                // The current branch after init is usually 'master' or 'main' depending on git config.
                // We rename current branch to the first selected one
                string firstBranch = branches[0];
                await RunGitCommandAsync($"branch -M {firstBranch}");

                // Create other branches
                for (int i = 1; i < branches.Count; i++)
                {
                    await RunGitCommandAsync($"branch {branches[i]}");
                }
            }

            // 5. Set Remote
            if (!string.IsNullOrEmpty(remoteUrl))
            {
                await RunGitCommandAsync($"remote add origin {remoteUrl}");
                
                // Try push if remote is set
                try 
                {
                    string current = await GetCurrentBranchAsync();
                    await RunGitCommandAsync($"push -u origin {current}");
                } 
                catch { /* Ignore push errors (e.g. no internet, auth fail) */ }
            }
        }

        public async Task<List<string>> GetBranchesAsync()
        {
            var output = await RunGitCommandAsync("branch --format=%(refname:short)");
            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        public async Task<string> GetCurrentBranchAsync()
        {
            return (await RunGitCommandAsync("rev-parse --abbrev-ref HEAD")).Trim();
        }

        public async Task<string> GetLastCommitHashAsync()
        {
            try
            {
                return (await RunGitCommandAsync("rev-parse HEAD")).Trim();
            }
            catch
            {
                return "";
            }
        }

        public async Task<int> GetTotalCommitsAsync()
        {
            try 
            {
                var output = await RunGitCommandAsync("rev-list --count HEAD");
                if (int.TryParse(output.Trim(), out int count))
                {
                    return count;
                }
            }
            catch { }
            return 0;
        }

        public async Task<BranchStatusInfo> GetBranchStatusAsync()
        {
            var status = new BranchStatusInfo();
            try
            {
                var output = await RunGitCommandAsync("status -sb");
                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var headerLine = lines.FirstOrDefault(l => l.StartsWith("##", StringComparison.OrdinalIgnoreCase));
                if (headerLine == null)
                {
                    status.LocalBranch = await GetCurrentBranchAsync();
                    return status;
                }

                var trimmed = headerLine.Substring(2).Trim();
                string bracketPart = string.Empty;
                int bracketIndex = trimmed.IndexOf('[');
                if (bracketIndex >= 0)
                {
                    bracketPart = trimmed.Substring(bracketIndex).Trim();
                    trimmed = trimmed.Substring(0, bracketIndex).Trim();
                    bracketPart = bracketPart.Trim('[', ']');
                }

                if (trimmed.Contains("..."))
                {
                    var branchParts = trimmed.Split(new[] { "..." }, StringSplitOptions.None);
                    status.LocalBranch = branchParts[0];
                    if (branchParts.Length > 1)
                    {
                        status.RemoteBranch = branchParts[1];
                    }
                }
                else
                {
                    status.LocalBranch = trimmed;
                }

                if (!string.IsNullOrWhiteSpace(bracketPart))
                {
                    var segments = bracketPart.Split(',');
                    foreach (var rawSegment in segments)
                    {
                        var segment = rawSegment.Trim();
                        if (segment.StartsWith("ahead", StringComparison.OrdinalIgnoreCase))
                        {
                            var tokens = segment.Split(' ');
                            if (tokens.Length >= 2 && int.TryParse(tokens[1], out int ahead))
                            {
                                status.AheadCount = ahead;
                            }
                        }
                        else if (segment.StartsWith("behind", StringComparison.OrdinalIgnoreCase))
                        {
                            var tokens = segment.Split(' ');
                            if (tokens.Length >= 2 && int.TryParse(tokens[1], out int behind))
                            {
                                status.BehindCount = behind;
                            }
                        }
                    }
                }

                return status;
            }
            catch
            {
                return status;
            }
        }

        public async Task<List<FileChange>> GetUncommittedChangesAsync()
        {
            var output = await RunGitCommandAsync("status --porcelain");
            var changes = new List<FileChange>();

            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.Length < 4) continue;

                var status = line.Substring(0, 2).Trim();
                var path = line.Substring(3).Trim();
                
                var changeType = ChangeType.Modified;
                if (status.Contains("?")) changeType = ChangeType.Added;
                else if (status.Contains("A")) changeType = ChangeType.Added;
                else if (status.Contains("D")) changeType = ChangeType.Deleted;
                else if (status.Contains("M")) changeType = ChangeType.Modified;

                changes.Add(new FileChange { Name = path, Type = changeType });
            }

            return changes;
        }

        public async Task CommitChangesAsync(string message)
        {
            await RunGitCommandAsync("add .");
            message = message.Replace("\"", "\\\"");
            await RunGitCommandAsync($"commit -m \"{message}\"");
        }
        
        public async Task RevertCommitAsync(string commitHash)
        {
            var status = await GetUncommittedChangesAsync();
            if (status.Count > 0)
            {
                throw new Exception("You have uncommitted changes. Please commit or stash them before rolling back.");
            }

            try 
            {
                await RunGitCommandAsync($"revert --no-edit {commitHash}");
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("conflict"))
                {
                    try { await RunGitCommandAsync("revert --abort"); } catch { }
                    throw new Exception("Conflict detected during rollback. The operation was aborted. Please resolve conflicts manually or try rolling back a more recent commit.");
                }
                throw;
            }
        }

        // --- GitHub / Remote Integration ---

        public async Task<string> GetRemoteUrlAsync(string remote = "origin")
        {
            try
            {
                var url = await RunGitCommandAsync($"remote get-url {remote}");
                return url.Trim();
            }
            catch
            {
                return "";
            }
        }

        public async Task SetRemoteAsync(string url, string remote = "origin")
        {
            try
            {
                // Try to set url first (if remote exists)
                await RunGitCommandAsync($"remote set-url {remote} {url}");
            }
            catch
            {
                // If failed (remote doesn't exist), add it
                await RunGitCommandAsync($"remote add {remote} {url}");
            }
        }

        public async Task PushAsync(string remote = "origin")
        {
            var branch = await GetCurrentBranchAsync();
            try
            {
                try 
                {
                    await RunGitCommandAsync($"push {remote} {branch}");
                }
                catch
                {
                    // If push fails, try setting upstream
                    await RunGitCommandAsync($"push --set-upstream {remote} {branch}");
                }
            }
            finally
            {
                EnsureGitFolderHidden();
            }
        }

        public async Task PullAsync(string remote = "origin")
        {
            var branch = await GetCurrentBranchAsync();
            await RunGitCommandAsync($"pull {remote} {branch}");
        }

        public async Task<List<string>> GetTagsAsync()
        {
            try
            {
                var output = await RunGitCommandAsync("tag --sort=-creatordate");
                return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        public async Task CreateTagAsync(string tagName, string message)
        {
             message = message.Replace("\"", "\\\"");
             await RunGitCommandAsync($"tag -a {tagName} -m \"{message}\"");
        }

        public async Task PushTagsAsync(string remote = "origin")
        {
            await RunGitCommandAsync($"push {remote} --tags");
        }
        
        // --- Sync Logic ---
        
        public async Task SyncBranchesAsync(string sourceBranch, string targetBranch)
        {
            // 1. Checkout target branch
            await RunGitCommandAsync($"checkout {targetBranch}");
            
            // 2. Merge source into target
            // --no-edit: accept default merge message
            // --allow-unrelated-histories: if needed, but risky
            await RunGitCommandAsync($"merge {sourceBranch} --no-edit");
            
            // 3. Switch back to source branch
            await RunGitCommandAsync($"checkout {sourceBranch}");
        }

        // -----------------------------------

        public async Task<List<FileChange>> GetDiffAsync(string sourceBranch, string targetBranch)
        {
            var output = await RunGitCommandAsync($"diff --name-status {targetBranch}..{sourceBranch}");
            var changes = new List<FileChange>();

            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var parts = line.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    var status = parts[0][0];
                    var path = parts[1];
                    
                    var changeType = ChangeType.Modified;
                    if (status == 'A') changeType = ChangeType.Added;
                    else if (status == 'D') changeType = ChangeType.Deleted;

                    changes.Add(new FileChange { Name = path, Type = changeType });
                }
            }

            return changes;
        }

        public async Task<List<CommitInfo>> GetCommitHistoryAsync(int maxCount = 30)
        {
            try
            {
                var format = "%H%x1F%h%x1F%an%x1F%ad%x1F%s";
                var output = await RunGitCommandAsync($"log -n {maxCount} --date=iso --pretty=format:{format}");
                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var commits = new List<CommitInfo>();

                foreach (var line in lines)
                {
                    var parts = line.Split('\x1F');
                    if (parts.Length != 5) continue;

                    DateTimeOffset dateOffset;
                    if (!DateTimeOffset.TryParse(parts[3], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dateOffset))
                    {
                        dateOffset = DateTimeOffset.Now;
                    }

                    commits.Add(new CommitInfo
                    {
                        FullHash = parts[0],
                        ShortHash = parts[1],
                        Author = parts[2],
                        Date = dateOffset.LocalDateTime,
                        Message = parts[4]
                    });
                }

                return commits;
            }
            catch
            {
                return new List<CommitInfo>();
            }
        }

        private async Task<string> RunGitCommandAsync(string arguments)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "git",
                            Arguments = arguments,
                            WorkingDirectory = _workingDirectory,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            StandardOutputEncoding = System.Text.Encoding.UTF8
                        }
                    };

                    process.Start();
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    // Allow non-zero exit for status, remote, init commands
                    if (process.ExitCode != 0 && 
                        !arguments.StartsWith("status") && 
                        !arguments.StartsWith("remote") && 
                        !arguments.StartsWith("push") &&
                        !arguments.StartsWith("init")) 
                    {
                        throw new Exception($"Git Error: {error}");
                    }

                    return output;
                }
                catch (Exception ex)
                {
                    throw new Exception($"Failed to execute git command: {ex.Message}");
                }
            });
        }

        public void EnsureGitFolderHidden()
        {
            try
            {
                var gitPath = Path.Combine(_workingDirectory, ".git");
                if (!Directory.Exists(gitPath)) return;

                var dirInfo = new DirectoryInfo(gitPath);
                if (!dirInfo.Attributes.HasFlag(FileAttributes.Hidden))
                {
                    dirInfo.Attributes |= FileAttributes.Hidden;
                }
            }
            catch
            {
                // Swallow: visual cue only, don't block git operations
            }
        }
    }

    public class FileChange
    {
        public string Name { get; set; } = "";
        public ChangeType Type { get; set; }
    }

    public class CommitInfo
    {
        public string FullHash { get; set; } = "";
        public string ShortHash { get; set; } = "";
        public string Author { get; set; } = "";
        public DateTime Date { get; set; }
        public string Message { get; set; } = "";
    }

    public enum ChangeType
    {
        Added,
        Modified,
        Deleted
    }

    public class BranchStatusInfo
    {
        public string LocalBranch { get; set; } = "";
        public string RemoteBranch { get; set; } = "";
        public int AheadCount { get; set; }
        public int BehindCount { get; set; }
        public bool HasRemote => !string.IsNullOrWhiteSpace(RemoteBranch);
    }
}
