using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Pages;
using GitDeployPro.Models; // For ProjectConfig
using Newtonsoft.Json;

namespace GitDeployPro.Services
{
    public class GitService
    {
        private static string _workingDirectory = Directory.GetCurrentDirectory();
        private readonly SshAgentService _sshAgentService = new SshAgentService();
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> RepoGates =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> InternalMetadataFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            ".gitdeploy.history",
            ".gitdeploy.config"
        };

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

        public static string WorkingDirectoryPath => _workingDirectory;

        public static void SetWorkingDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                _workingDirectory = path;
            }
        }

        private static SemaphoreSlim GetRepoGate(string workingDirectory)
        {
            string key = NormalizeRepoKey(workingDirectory);
            return RepoGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        }

        private static string NormalizeRepoKey(string workingDirectory)
        {
            try
            {
                return Path.GetFullPath(workingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return (workingDirectory ?? string.Empty).Trim();
            }
        }

        private static string GetIndexLockPath(string workingDirectory)
        {
            return Path.Combine(workingDirectory, ".git", "index.lock");
        }

        /// <summary>
        /// Best-effort removal of a stale .git/index.lock left by a previous git process
        /// (e.g. after timeout/crash or an interrupted add). If the lock is still held,
        /// delete fails and the caller can retry or surface the normal error.
        /// </summary>
        private static bool TryClearIndexLock(string? workingDirectory = null)
        {
            var root = string.IsNullOrWhiteSpace(workingDirectory) ? _workingDirectory : workingDirectory;
            try
            {
                var lockPath = GetIndexLockPath(root);
                if (!File.Exists(lockPath))
                {
                    return true;
                }

                File.Delete(lockPath);
                Debug.WriteLine($"Cleared stale git index.lock at {lockPath}");
                return !File.Exists(lockPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not clear git index.lock: {ex.Message}");
                return false;
            }
        }

        private static bool IsIndexLockError(GitCommandException ex)
        {
            string details = ex.GetDetailedMessage();
            return details.Contains("index.lock", StringComparison.OrdinalIgnoreCase) ||
                   details.Contains("Another git process seems to be running", StringComparison.OrdinalIgnoreCase) ||
                   (details.Contains("Unable to create", StringComparison.OrdinalIgnoreCase) &&
                    details.Contains(".lock", StringComparison.OrdinalIgnoreCase));
        }

        public bool IsGitRepository()
        {
            return Directory.Exists(Path.Combine(_workingDirectory, ".git"));
        }

        public async Task RemovePathFromIndexAsync(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || !IsGitRepository())
                return;

            try
            {
                await RunGitCommandAsync($"rm -r --cached \"{relativePath}\"");
            }
            catch
            {
                // ignore errors if path isn't tracked or doesn't exist
            }
        }

        public async Task DiscardPathChangesAsync(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || !IsGitRepository())
            {
                throw new ArgumentException("A valid repository path is required.", nameof(relativePath));
            }

            var normalized = NormalizeGitPath(relativePath);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                throw new ArgumentException("Path is invalid.", nameof(relativePath));
            }

            await RunGitCommandAsync($"restore --source=HEAD --staged --worktree -- {QuoteGitArgument(normalized)}");
        }

        public async Task InitRepoAsync(List<string> branches, string remoteUrl)
        {
            if (IsGitRepository()) return;

            // 1. Init
            await RunGitCommandAsync("init");

            EnsureGitFolderHidden();

            // Check identity before commit
            await EnsureIdentityConfiguredAsync();

            // 2. Add all files
            await RunGitCommandAsync("add .");

            // 3. Initial commit
            try 
            {
                await RunGitCommandAsync("commit -m \"Initial commit by GitDeploy Pro\"");
            }
            catch (GitCommandException ex) when (IsNothingToCommitError(ex))
            {
                await RunGitCommandAsync("commit --allow-empty -m \"Initial empty commit by GitDeploy Pro\"");
            }

            // 4. Create Branches
            if (branches != null && branches.Any())
            {
                // The current branch after init is usually 'master' or 'main' depending on git config.
                // We rename current branch to the first selected one
                string firstBranch = branches[0];
                await RunGitCommandAsync($"branch -M {firstBranch}");

                // Check if we have a valid commit (HEAD exists)
                bool hasCommits = false;
                try
                {
                    await RunGitCommandAsync("rev-parse HEAD");
                    hasCommits = true;
                }
                catch 
                { 
                    // No commits yet (unborn branch)
                    // Try one last time to force an initial commit allow-empty
                    try
                    {
                        await RunGitCommandAsync("commit --allow-empty -m \"Initial empty commit\"");
                        hasCommits = true;
                    }
                    catch { }
                }

                // Create other branches ONLY if we have a valid commit
                if (hasCommits)
                {
                    for (int i = 1; i < branches.Count; i++)
                    {
                        try 
                        { 
                            await RunGitCommandAsync($"branch {branches[i]}"); 
                        } 
                        catch (GitCommandException ex) when (IsBranchAlreadyExistsError(ex))
                        {
                            // Ignore only "already exists" branch warnings.
                        }
                    }
                }
            }

            // 5. Set Remote
            if (!string.IsNullOrEmpty(remoteUrl))
            {
                await RunGitCommandAsync($"remote add origin {remoteUrl}");
                
                // Try push if remote is set
                await EnsureSshKeyForRemoteAsync("origin", remoteUrl);
                string current = await GetCurrentBranchAsync();
                await RunGitCommandAsync($"push -u origin {current}");
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

        public async Task<int> GetUncommittedCountAsync()
        {
            var changes = await GetUncommittedChangesAsync(includeDiff: false);
            return changes.Count;
        }

        public async Task<bool> HasUncommittedChangesAsync()
        {
            return await GetUncommittedCountAsync() > 0;
        }

        /// <summary>
        /// Builds a relative-path map of working-tree overlay states for UI (Direct Upload explorer).
        /// Tracked clean files come from ls-files; dirty/untracked/ignored from porcelain.
        /// </summary>
        public async Task<Dictionary<string, GitItemState>> GetWorkingTreeOverlayAsync(string? repoRoot = null)
        {
            var result = new Dictionary<string, GitItemState>(StringComparer.OrdinalIgnoreCase);
            var root = string.IsNullOrWhiteSpace(repoRoot) ? _workingDirectory : repoRoot;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(Path.Combine(root, ".git")))
            {
                return result;
            }

            try
            {
                var lsFiles = await RunGitCommandAsync("ls-files -z", root);
                foreach (var path in lsFiles.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    var normalized = NormalizeGitPath(path);
                    if (string.IsNullOrWhiteSpace(normalized) || IsInternalMetadataPath(normalized))
                    {
                        continue;
                    }

                    result[normalized] = GitItemState.Clean;
                }

                var porcelain = await RunGitCommandAsync("status --porcelain=v1 -uall --ignored=matching", root);
                foreach (var rawLine in porcelain.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!TryParsePorcelainLine(rawLine, out var xy, out var path))
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(path) || IsInternalMetadataPath(path))
                    {
                        continue;
                    }

                    result[path] = MapPorcelainToGitItemState(xy);
                }
            }
            catch
            {
                // No git / command failure: leave overlay empty so UI stays neutral.
            }

            return result;
        }

        private static GitItemState MapPorcelainToGitItemState(string xy)
        {
            if (string.IsNullOrEmpty(xy))
            {
                return GitItemState.Modified;
            }

            if (xy == "??")
            {
                return GitItemState.Untracked;
            }

            if (xy == "!!")
            {
                return GitItemState.Ignored;
            }

            // Unmerged / conflict codes
            if (xy is "UU" or "AA" or "DD" or "AU" or "UA" or "DU" or "UD")
            {
                return GitItemState.Conflicted;
            }

            return GitItemState.Modified;
        }

        public async Task<List<string>> GetUntrackedFilesAsync()
        {
            var output = await RunGitCommandAsync("ls-files --others --exclude-standard -z");
            var files = new List<string>();
            foreach (var path in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var normalized = NormalizeGitPath(path);
                if (string.IsNullOrWhiteSpace(normalized) || IsInternalMetadataPath(normalized))
                {
                    continue;
                }

                files.Add(normalized);
            }

            return files;
        }

        public async Task StagePathsAsync(IEnumerable<string> relativePaths)
        {
            var paths = (relativePaths ?? Array.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(NormalizeGitPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (paths.Count == 0)
            {
                return;
            }

            string pathSpecArgs = BuildPathSpecArguments(paths);
            await RunGitCommandAsync($"add -A -- {pathSpecArgs}");
        }

        /// <summary>
        /// Stages every non-ignored untracked file (respects .gitignore). Returns count staged.
        /// </summary>
        public async Task<int> DiscoverAndStageUntrackedAsync()
        {
            var untracked = await GetUntrackedFilesAsync();
            if (untracked.Count == 0)
            {
                return 0;
            }

            await StagePathsAsync(untracked);
            return untracked.Count;
        }

        public async Task<bool> HasCommittableChangesUnderPathsAsync(IEnumerable<string> relativePaths)
        {
            var paths = (relativePaths ?? Array.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(NormalizeGitPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (paths.Count == 0)
            {
                return false;
            }

            var changes = await GetUncommittedChangesAsync(includeDiff: false);
            return changes.Any(change => paths.Any(scope => PathMatchesScope(change.Name, scope)));
        }

        public async Task<List<FileChange>> GetUncommittedChangesAsync(bool includeDiff = true)
        {
            var output = await RunGitCommandAsync("status --porcelain=v1 -uall");
            var changes = new List<FileChange>();

            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (!TryParsePorcelainLine(line, out var status, out var path))
                {
                    continue;
                }

                if (IsInternalMetadataPath(path))
                {
                    continue;
                }
                
                var changeType = ChangeType.Modified;
                if (status.Contains("?")) changeType = ChangeType.Added;
                else if (status.Contains("A")) changeType = ChangeType.Added;
                else if (status.Contains("D")) changeType = ChangeType.Deleted;
                else if (status.Contains("M")) changeType = ChangeType.Modified;

                changes.Add(new FileChange { Name = path, Type = changeType });
            }

            if (!includeDiff || changes.Count == 0)
            {
                return changes;
            }

            var diffMap = await GetUnifiedDiffMapAsync("diff --unified=80");
            var stagedMap = await GetUnifiedDiffMapAsync("diff --cached --unified=80");
            foreach (var kvp in stagedMap)
            {
                diffMap[kvp.Key] = kvp.Value;
            }

            foreach (var change in changes)
            {
                if (diffMap.TryGetValue(change.Name, out var patch))
                {
                    change.DiffPatch = patch;
                    continue;
                }

                if (change.Type == ChangeType.Added)
                {
                    var fullPath = Path.Combine(_workingDirectory, change.Name.Replace("/", Path.DirectorySeparatorChar.ToString()));
                    if (File.Exists(fullPath))
                    {
                        var content = SafeReadFileForDiff(fullPath);
                        change.DiffPatch = DiffParser.BuildAddedFileDiff(change.Name, content);
                    }
                }
            }

            return changes;
        }

        public async Task CommitChangesAsync(string message)
        {
            await RunGitCommandAsync("add .");
            message = message.Replace("\"", "\\\"");
            await RunGitCommandAsync($"commit -m \"{message}\"");
        }

        public async Task CommitSpecificPathsAsync(IEnumerable<string> relativePaths, string message)
        {
            var paths = (relativePaths ?? Array.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(NormalizeGitPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (paths.Count == 0)
            {
                throw new ArgumentException("At least one file path must be provided.", nameof(relativePaths));
            }

            if (!await HasCommittableChangesUnderPathsAsync(paths))
            {
                throw new Exception(GetNoCommittableChangesMessage());
            }

            string pathSpecArgs = BuildPathSpecArguments(paths);
            await StagePathsAsync(paths);

            if (!await HasStagedOrWorkingChangesUnderPathsAsync(paths))
            {
                throw new Exception(GetNoCommittableChangesMessage());
            }

            message = message.Replace("\"", "\\\"");
            try
            {
                await RunGitCommandAsync($"commit -m \"{message}\" --only -- {pathSpecArgs}");
            }
            catch (GitCommandException ex) when (IsNothingToCommitError(ex) || IsChangesNotStagedForCommitError(ex))
            {
                throw new Exception(GetNoCommittableChangesMessage(), ex);
            }
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

        /// <summary>
        /// Undo one file's change introduced by <paramref name="commitHash"/> (restore parent version),
        /// then create a new commit. Does not rewrite history.
        /// </summary>
        public async Task RevertFileFromCommitAsync(string commitHash, CommitFileChangeInfo file, string? commitMessage = null)
        {
            if (string.IsNullOrWhiteSpace(commitHash))
            {
                throw new ArgumentException("Commit hash is required.", nameof(commitHash));
            }

            if (file == null)
            {
                throw new ArgumentNullException(nameof(file));
            }

            var status = await GetUncommittedChangesAsync(includeDiff: false);
            if (status.Count > 0)
            {
                throw new Exception("You have uncommitted changes. Please commit or stash them before rolling back.");
            }

            var path = NormalizeGitPath(string.IsNullOrWhiteSpace(file.Path) ? (file.OldPath ?? string.Empty) : file.Path);
            var oldPath = NormalizeGitPath(file.OldPath ?? string.Empty);
            if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(oldPath))
            {
                throw new ArgumentException("File path is required.", nameof(file));
            }

            var shortHash = commitHash.Length > 8 ? commitHash[..8] : commitHash;
            var message = string.IsNullOrWhiteSpace(commitMessage)
                ? $"Rollback {path} from {shortHash}"
                : commitMessage.Trim().Replace("\"", "\\\"");

            if (file.Type == ChangeType.Added)
            {
                // File did not exist before that commit — remove it.
                var removePath = QuoteGitArgument(path);
                try
                {
                    await RunGitCommandAsync($"rm -f -- {removePath}");
                }
                catch
                {
                    // Already missing on disk; still stage deletion if tracked.
                    await RunGitCommandAsync($"rm --cached -f -- {removePath}");
                }
            }
            else
            {
                // Restore content from parent of the selected commit.
                var restorePath = QuoteGitArgument(!string.IsNullOrWhiteSpace(oldPath) ? oldPath : path);
                await RunGitCommandAsync($"checkout {commitHash}^ -- {restorePath}");

                // Renames: drop the new path if it still exists separately.
                if (!string.IsNullOrWhiteSpace(oldPath)
                    && !string.IsNullOrWhiteSpace(path)
                    && !string.Equals(oldPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await RunGitCommandAsync($"rm -f -- {QuoteGitArgument(path)}");
                    }
                    catch
                    {
                        // Ignore if new path is already gone.
                    }
                }
            }

            try
            {
                await RunGitCommandAsync($"commit -m \"{message}\"");
            }
            catch (GitCommandException ex) when (IsNothingToCommitError(ex))
            {
                throw new Exception("Nothing to roll back for this file (working tree already matches the parent version).");
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
            await EnsureSshKeyForRemoteAsync(remote);
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

        public async Task<PushExecutionResult> PushOrSkipAsync(string remote = "origin")
        {
            string remoteUrl = await GetRemoteUrlAsync(remote);
            if (string.IsNullOrWhiteSpace(remoteUrl))
            {
                return PushExecutionResult.SkippedNoRemote;
            }

            await PushAsync(remote);
            return PushExecutionResult.PushedToRemote;
        }

        public async Task PullAsync(string remote = "origin")
        {
            await EnsureSshKeyForRemoteAsync(remote);
            var branch = await GetCurrentBranchAsync();
            await RunGitCommandAsync($"pull {remote} {branch}");
        }

        public async Task CloneRepositoryAsync(string remoteUrl, string targetDirectory)
        {
            if (string.IsNullOrWhiteSpace(remoteUrl))
                throw new ArgumentException("Remote URL is required.", nameof(remoteUrl));
            if (string.IsNullOrWhiteSpace(targetDirectory))
                throw new ArgumentException("Target directory is required.", nameof(targetDirectory));

            var normalizedTarget = targetDirectory.Trim().Trim('"');
            var targetInfo = new DirectoryInfo(normalizedTarget);
            var parentDirectory = targetInfo.Parent?.FullName;

            if (string.IsNullOrWhiteSpace(parentDirectory))
            {
                throw new InvalidOperationException("Please select a folder that is not the root of a drive.");
            }

            if (!Directory.Exists(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            if (Directory.Exists(normalizedTarget))
            {
                bool isEmpty = !Directory.EnumerateFileSystemEntries(normalizedTarget).Any();
                if (!isEmpty)
                {
                    throw new InvalidOperationException("Target directory must be empty before cloning.");
                }

                Directory.Delete(normalizedTarget);
            }

            if (LooksLikeSshRemote(remoteUrl))
            {
                await _sshAgentService.EnsureDefaultKeyLoadedAsync();
            }

            await RunGitCommandAsync($"clone \"{remoteUrl}\" \"{targetInfo.Name}\"", parentDirectory);

            SetWorkingDirectory(normalizedTarget);
            EnsureGitFolderHidden();
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
            await EnsureSshKeyForRemoteAsync(remote);
            await RunGitCommandAsync($"push {remote} --tags");
        }
        
        // --- Sync Logic ---
        
        public async Task CreateBranchAsync(string branchName)
        {
            if (string.IsNullOrWhiteSpace(branchName)) throw new ArgumentException("Branch name required");
            
            // Create and checkout
            await RunGitCommandAsync($"checkout -b {branchName}");
        }

        public async Task SyncBranchesAsync(string sourceBranch, string targetBranch)
        {
            try
            {
                // 1. Checkout target branch
                await RunGitCommandAsync($"checkout {targetBranch}");
                
                // 2. Merge source into target
                // --no-edit: accept default merge message
                // --allow-unrelated-histories: Force merge even if branches have different roots
                // -X theirs: In case of conflicts, accept changes from source branch (master)
                await RunGitCommandAsync($"merge {sourceBranch} --no-edit --allow-unrelated-histories -X theirs");
            }
            finally
            {
                // 3. Switch back to source branch (ALWAYS)
                await RunGitCommandAsync($"checkout {sourceBranch}");
            }
        }

        // -----------------------------------

        public async Task<List<FileChange>> GetDiffAsync(string sourceBranch, string targetBranch)
        {
            var output = await RunGitCommandAsync($"diff --name-status {targetBranch}..{sourceBranch}");
            var changes = new List<FileChange>();

            var diffMap = await GetUnifiedDiffMapAsync($"diff --unified=80 {targetBranch}..{sourceBranch}");

            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var parts = line.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    var status = parts[0][0];
                    var path = NormalizeGitPath(parts[1]);
                    if (IsInternalMetadataPath(path))
                    {
                        continue;
                    }
                    
                    var changeType = ChangeType.Modified;
                    if (status == 'A') changeType = ChangeType.Added;
                    else if (status == 'D') changeType = ChangeType.Deleted;

                    var change = new FileChange { Name = path, Type = changeType };
                    if (diffMap.TryGetValue(change.Name, out var patch))
                    {
                        change.DiffPatch = patch;
                    }
                    changes.Add(change);
                }
            }

            return changes;
        }

        public async Task<List<CommitInfo>> GetCommitHistoryAsync(int maxCount = 30)
        {
            var entries = await GetCommitHistoryPageAsync(maxCount);
            return entries.Select(x => x.Commit).ToList();
        }

        public async Task<List<CommitHistoryEntry>> GetCommitHistoryPageAsync(int take = 50, string? beforeCommitHash = null)
        {
            var entries = await GetCommitHistoryWithFilesPageAsync(take, beforeCommitHash);
            return entries
                .Select(entry => new CommitHistoryEntry
                {
                    Commit = entry.Commit,
                    ChangedFiles = new List<CommitFileChangeInfo>()
                })
                .ToList();
        }

        public async Task<List<CommitHistoryEntry>> GetCommitHistoryWithFilesPageAsync(int take = 50, string? beforeCommitHash = null)
        {
            try
            {
                int normalizedTake = Math.Clamp(take, 1, 500);
                string revArg = string.IsNullOrWhiteSpace(beforeCommitHash) ? "HEAD" : $"{beforeCommitHash.Trim()}^";
                // Put record separator at the beginning of each commit block so
                // splitting by 0x1E keeps header + file lines in one chunk.
                var format = "%x1E%H%x1F%h%x1F%an%x1F%ad%x1F%s";
                var output = await RunGitCommandAsync($"log {revArg} -n {normalizedTake} --date=iso --pretty=format:{format} --name-status");
                return ParseCommitHistoryWithFilesOutput(output);
            }
            catch
            {
                return new List<CommitHistoryEntry>();
            }
        }

        public async Task<string> GetCommitFileContentAsync(string commitHash, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(commitHash))
            {
                throw new ArgumentException("Commit hash is required.", nameof(commitHash));
            }

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException("Relative path is required.", nameof(relativePath));
            }

            var normalizedPath = NormalizeGitPath(relativePath);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                throw new ArgumentException("Relative path is invalid.", nameof(relativePath));
            }

            try
            {
                return await RunGitCommandAsync($"show {QuoteGitArgument($"{commitHash}:{normalizedPath}")}");
            }
            catch (GitCommandException ex) when (IsPathMissingFromCommit(ex))
            {
                // Deleted files are not available in commit tree; fallback to parent snapshot.
                return await RunGitCommandAsync($"show {QuoteGitArgument($"{commitHash}^:{normalizedPath}")}");
            }
        }

        public async Task<string> GetCommitFileDiffAsync(string commitHash, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(commitHash))
            {
                throw new ArgumentException("Commit hash is required.", nameof(commitHash));
            }

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException("Relative path is required.", nameof(relativePath));
            }

            var normalizedPath = NormalizeGitPath(relativePath);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                throw new ArgumentException("Relative path is invalid.", nameof(relativePath));
            }

            string pathArg = QuoteGitArgument(normalizedPath);
            try
            {
                return await RunGitCommandAsync($"diff --unified=80 {commitHash}^..{commitHash} -- {pathArg}");
            }
            catch
            {
                return await RunGitCommandAsync($"show --unified=80 --pretty=format: {commitHash} -- {pathArg}");
            }
        }

        private async Task<string> RunGitCommandAsync(string arguments, string? workingDirectory = null, TimeSpan? timeout = null)
        {
            var resolvedWorkingDirectory = workingDirectory ?? _workingDirectory;
            var gate = GetRepoGate(resolvedWorkingDirectory);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Clear leftover locks before any command that may touch the index.
                TryClearIndexLock(resolvedWorkingDirectory);

                const int maxAttempts = 3;
                GitCommandException? lastLockError = null;

                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        return await ExecuteGitProcessAsync(arguments, resolvedWorkingDirectory, timeout).ConfigureAwait(false);
                    }
                    catch (GitCommandException ex) when (IsIndexLockError(ex) && attempt < maxAttempts)
                    {
                        lastLockError = ex;
                        Debug.WriteLine($"Git index.lock conflict on attempt {attempt} for '{arguments}'. Clearing and retrying...");
                        TryClearIndexLock(resolvedWorkingDirectory);
                        await Task.Delay(150 * attempt).ConfigureAwait(false);
                        TryClearIndexLock(resolvedWorkingDirectory);
                    }
                }

                throw lastLockError ?? new GitCommandException(
                    "git",
                    arguments,
                    resolvedWorkingDirectory,
                    -1,
                    string.Empty,
                    "Git command failed after index.lock retries.");
            }
            finally
            {
                gate.Release();
            }
        }

        private static async Task<string> ExecuteGitProcessAsync(string arguments, string resolvedWorkingDirectory, TimeSpan? timeout)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = resolvedWorkingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };

            process.StartInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            process.StartInfo.EnvironmentVariables["GIT_ASKPASS"] = "echo";
            process.StartInfo.EnvironmentVariables["SSH_ASKPASS"] = "echo";

            try
            {
                if (!process.Start())
                {
                    throw new GitCommandException("git", arguments, resolvedWorkingDirectory, -1, string.Empty, "Failed to start git process.");
                }

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                TimeSpan commandTimeout = timeout ?? GetDefaultTimeout(arguments);
                using var timeoutCts = new CancellationTokenSource(commandTimeout);

                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    TryTerminateProcess(process);
                    TryClearIndexLock(resolvedWorkingDirectory);
                    string timedOutStdOut = await stdoutTask.ConfigureAwait(false);
                    string timedOutStdErr = await stderrTask.ConfigureAwait(false);
                    throw new GitCommandException(
                        "git",
                        arguments,
                        resolvedWorkingDirectory,
                        -1,
                        timedOutStdOut,
                        timedOutStdErr,
                        $"Git command timed out after {commandTimeout.TotalSeconds:0} seconds.");
                }

                string output = await stdoutTask.ConfigureAwait(false);
                string error = await stderrTask.ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    throw new GitCommandException("git", arguments, resolvedWorkingDirectory, process.ExitCode, output, error);
                }

                return output;
            }
            catch (GitCommandException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new GitCommandException("git", arguments, resolvedWorkingDirectory, -1, string.Empty, ex.Message, "Failed to execute git command.", ex);
            }
        }

        private static TimeSpan GetDefaultTimeout(string arguments)
        {
            if (arguments.StartsWith("clone", StringComparison.OrdinalIgnoreCase))
            {
                return TimeSpan.FromMinutes(60);
            }

            if (arguments.StartsWith("add", StringComparison.OrdinalIgnoreCase) ||
                arguments.StartsWith("diff", StringComparison.OrdinalIgnoreCase))
            {
                return TimeSpan.FromMinutes(30);
            }

            return TimeSpan.FromMinutes(10);
        }

        private static void TryTerminateProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore process termination failures.
            }
        }

        private static bool IsNothingToCommitError(GitCommandException ex)
        {
            string message = ex.GetDetailedMessage();
            return message.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("no changes added to commit", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsChangesNotStagedForCommitError(GitCommandException ex)
        {
            return ex.GetDetailedMessage().Contains("Changes not staged for commit", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetNoCommittableChangesMessage()
        {
            return "No committable changes under this path. Files may be gitignored or already committed.";
        }

        private async Task<bool> HasStagedOrWorkingChangesUnderPathsAsync(IList<string> paths)
        {
            string pathSpecArgs = BuildPathSpecArguments(paths);
            var cached = await RunGitCommandAsync($"diff --cached --name-only -- {pathSpecArgs}");
            if (!string.IsNullOrWhiteSpace(cached.Trim()))
            {
                return true;
            }

            var working = await RunGitCommandAsync($"diff --name-only -- {pathSpecArgs}");
            return !string.IsNullOrWhiteSpace(working.Trim());
        }

        private static bool TryParsePorcelainLine(string line, out string status, out string path)
        {
            status = string.Empty;
            path = string.Empty;
            if (string.IsNullOrWhiteSpace(line) || line.Length < 4)
            {
                return false;
            }

            status = line.Substring(0, 2);
            var pathPart = line.Substring(3).Trim();
            if (pathPart.Contains(" -> "))
            {
                pathPart = pathPart.Split(new[] { " -> " }, StringSplitOptions.None).Last();
            }

            path = NormalizeGitPath(pathPart.Trim('"').TrimEnd('/'));
            return !string.IsNullOrWhiteSpace(path);
        }

        private static bool PathMatchesScope(string filePath, string scopePath)
        {
            var normalizedScope = scopePath.TrimEnd('/');
            if (filePath.Equals(normalizedScope, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return filePath.StartsWith(normalizedScope + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPathMissingFromCommit(GitCommandException ex)
        {
            string message = ex.GetDetailedMessage();
            return message.Contains("does not exist in", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("exists on disk, but not in", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBranchAlreadyExistsError(GitCommandException ex)
        {
            return ex.GetDetailedMessage().Contains("already exists", StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeReadFileForDiff(string fullPath)
        {
            const int maxBytes = 512 * 1024;
            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length > maxBytes)
            {
                return $"[truncated file content: {fileInfo.Length} bytes]";
            }

            return File.ReadAllText(fullPath);
        }

        private async Task EnsureSshKeyForRemoteAsync(string remoteName, string remoteUrlOverride = "")
        {
            string remoteUrl = remoteUrlOverride;
            if (string.IsNullOrWhiteSpace(remoteUrl))
            {
                remoteUrl = await GetRemoteUrlAsync(remoteName);
            }

            if (LooksLikeSshRemote(remoteUrl))
            {
                await _sshAgentService.EnsureDefaultKeyLoadedAsync();
            }
        }

        private static bool LooksLikeSshRemote(string? remoteUrl)
        {
            if (string.IsNullOrWhiteSpace(remoteUrl)) return false;
            var trimmed = remoteUrl.Trim();
            return trimmed.StartsWith("git@", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase);
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

        private async Task EnsureIdentityConfiguredAsync()
        {
            try
            {
                // Check if user.name is set globally or locally
                // If "git config user.name" returns empty or fails, we set a default local identity
                bool identityMissing = false;
                try 
                { 
                    string name = await RunGitCommandAsync("config user.name");
                    if (string.IsNullOrWhiteSpace(name)) identityMissing = true;
                } 
                catch { identityMissing = true; }

                if (identityMissing)
                {
                    // Set local config only for this repository
                    await RunGitCommandAsync("config user.name \"GitDeploy Pro User\"");
                    await RunGitCommandAsync("config user.email \"auto@gitdeploy.pro\"");
                }
            }
            catch { /* Ignore errors, if we can't set config, commit might fail but we tried */ }
        }

        private async Task<Dictionary<string, string>> GetUnifiedDiffMapAsync(string diffArguments)
        {
            try
            {
                var output = await RunGitCommandAsync(diffArguments);
                return DiffParser.SplitByFile(output);
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static string NormalizeGitPath(string path)
        {
            return path.Replace("\\", "/").Trim();
        }

        private static string BuildPathSpecArguments(IEnumerable<string> paths)
        {
            return string.Join(" ", paths.Select(QuoteGitArgument));
        }

        private static string QuoteGitArgument(string value)
        {
            return $"\"{value.Replace("\"", "\\\"")}\"";
        }

        private static bool IsInternalMetadataPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string fileName = Path.GetFileName(path.Trim().Trim('"'));
            return InternalMetadataFiles.Contains(fileName);
        }

        private static List<CommitHistoryEntry> ParseCommitHistoryWithFilesOutput(string output)
        {
            var records = output.Split('\x1E', StringSplitOptions.RemoveEmptyEntries);
            var commits = new List<CommitHistoryEntry>();

            foreach (var rawRecord in records)
            {
                var block = rawRecord.Trim('\r', '\n');
                if (string.IsNullOrWhiteSpace(block))
                {
                    continue;
                }

                var lines = block.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0)
                {
                    continue;
                }

                var headerParts = lines[0].Split('\x1F');
                if (headerParts.Length != 5)
                {
                    continue;
                }

                DateTimeOffset dateOffset;
                if (!DateTimeOffset.TryParse(headerParts[3], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dateOffset))
                {
                    dateOffset = AppTimeService.LocalNowOffset;
                }

                var entry = new CommitHistoryEntry
                {
                    Commit = new CommitInfo
                    {
                        FullHash = headerParts[0],
                        ShortHash = headerParts[1],
                        Author = headerParts[2],
                        Date = dateOffset.LocalDateTime,
                        Message = headerParts[4]
                    },
                    ChangedFiles = new List<CommitFileChangeInfo>()
                };

                for (int i = 1; i < lines.Length; i++)
                {
                    var parsed = ParseNameStatusLine(lines[i]);
                    if (parsed == null || IsInternalMetadataPath(parsed.Path))
                    {
                        continue;
                    }

                    entry.ChangedFiles.Add(parsed);
                }

                commits.Add(entry);
            }

            return commits;
        }

        private static CommitFileChangeInfo? ParseNameStatusLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            var parts = line.Split('\t');
            if (parts.Length < 2)
            {
                return null;
            }

            string statusCode = parts[0].Trim();
            if (string.IsNullOrWhiteSpace(statusCode))
            {
                return null;
            }

            string path;
            string? oldPath = null;
            char statusKind = char.ToUpperInvariant(statusCode[0]);

            if ((statusKind == 'R' || statusKind == 'C') && parts.Length >= 3)
            {
                oldPath = NormalizeGitPath(parts[1]);
                path = NormalizeGitPath(parts[2]);
            }
            else
            {
                path = NormalizeGitPath(parts[1]);
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return new CommitFileChangeInfo
            {
                Path = path,
                OldPath = oldPath,
                StatusCode = statusCode,
                Type = ParseNameStatusChangeType(statusCode)
            };
        }

        private static ChangeType ParseNameStatusChangeType(string statusCode)
        {
            if (string.IsNullOrWhiteSpace(statusCode))
            {
                return ChangeType.Modified;
            }

            return char.ToUpperInvariant(statusCode[0]) switch
            {
                'A' => ChangeType.Added,
                'D' => ChangeType.Deleted,
                _ => ChangeType.Modified
            };
        }
    }

    public class FileChange
    {
        public string Name { get; set; } = "";
        public ChangeType Type { get; set; }
        public string DiffPatch { get; set; } = string.Empty;
    }

    public class CommitInfo
    {
        public string FullHash { get; set; } = "";
        public string ShortHash { get; set; } = "";
        public string Author { get; set; } = "";
        public DateTime Date { get; set; }
        public string Message { get; set; } = "";
    }

    public sealed class CommitHistoryEntry
    {
        public CommitInfo Commit { get; set; } = new CommitInfo();
        public List<CommitFileChangeInfo> ChangedFiles { get; set; } = new List<CommitFileChangeInfo>();
    }

    public sealed class CommitFileChangeInfo
    {
        public string Path { get; set; } = "";
        public string? OldPath { get; set; }
        public string StatusCode { get; set; } = "";
        public ChangeType Type { get; set; } = ChangeType.Modified;
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

    public enum PushExecutionResult
    {
        PushedToRemote,
        SkippedNoRemote
    }

    public class GitCommandException : Exception
    {
        public GitCommandException(
            string command,
            string arguments,
            string workingDirectory,
            int exitCode,
            string standardOutput,
            string standardError,
            string? customMessage = null,
            Exception? innerException = null)
            : base(customMessage ?? BuildMessage(command, arguments, exitCode, standardOutput, standardError), innerException)
        {
            Command = command;
            Arguments = arguments;
            WorkingDirectory = workingDirectory;
            ExitCode = exitCode;
            StandardOutput = standardOutput ?? string.Empty;
            StandardError = standardError ?? string.Empty;
        }

        public string Command { get; }
        public string Arguments { get; }
        public string WorkingDirectory { get; }
        public int ExitCode { get; }
        public string StandardOutput { get; }
        public string StandardError { get; }

        public string GetDetailedMessage()
        {
            var details = !string.IsNullOrWhiteSpace(StandardError) ? StandardError : StandardOutput;
            if (string.IsNullOrWhiteSpace(details))
            {
                details = "No output was returned by git.";
            }

            return details.Trim();
        }

        private static string BuildMessage(string command, string arguments, int exitCode, string standardOutput, string standardError)
        {
            string details = !string.IsNullOrWhiteSpace(standardError) ? standardError : standardOutput;
            if (string.IsNullOrWhiteSpace(details))
            {
                details = "No output was returned by git.";
            }

            return $"{command} {arguments} failed (exit {exitCode}): {details.Trim()}";
        }
    }
}