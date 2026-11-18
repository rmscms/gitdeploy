using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Controls;
using GitDeployPro.Models;
using GitDeployPro.Services.Remote;
using GitDeployPro.Services.Telegram;
using Newtonsoft.Json.Linq;

namespace GitDeployPro.Services.AgentApi
{
    // ------------------------------------------------------------------------------------------
    // Tool catalogue — one definition drives REST routing, OpenAPI and MCP tools/list.
    // ------------------------------------------------------------------------------------------

    public sealed record AgentToolParam(string Name, string Type, string Description, bool Required = false);

    public sealed record AgentTool(
        string Name,
        string Description,
        string Scope,
        string Method,
        string Path,
        params AgentToolParam[] Params)
    {
        public JObject InputSchema()
        {
            var props = new JObject();
            var required = new JArray();
            foreach (var p in Params)
            {
                var prop = new JObject { ["description"] = p.Description };
                if (p.Type == "string[]")
                {
                    prop["type"] = "array";
                    prop["items"] = new JObject { ["type"] = "string" };
                }
                else
                {
                    prop["type"] = p.Type;
                }

                props[p.Name] = prop;
                if (p.Required)
                {
                    required.Add(p.Name);
                }
            }

            var schema = new JObject { ["type"] = "object", ["properties"] = props };
            if (required.Count > 0)
            {
                schema["required"] = required;
            }

            return schema;
        }
    }

    public static class AgentApiTools
    {
        private static readonly AgentToolParam Project = new("project", "string", "Full project path from list_projects. Omit to use the active project.");

        public static readonly IReadOnlyList<AgentTool> All = new[]
        {
            new AgentTool("gitdeploy_status", "App version, API features and the active project.", AgentScopes.Read, "GET", "/v1/status"),
            new AgentTool("list_projects", "Projects GitDeploy knows about (the ones this key may use).", AgentScopes.Read, "GET", "/v1/projects"),
            new AgentTool("get_changes", "Uncommitted changed files of a project (what a deploy would send).", AgentScopes.Read, "GET", "/v1/changes", Project),
            new AgentTool("deploy", "Run the GitDeploy pipeline (commit + push + upload changed files). Returns a job; may wait for human approval.", AgentScopes.Deploy, "POST", "/v1/deploy", Project),
            new AgentTool("upload", "Upload specific files/folders of the project through its FTP/SFTP profile and path mappings. Returns a job; may wait for human approval.", AgentScopes.Upload, "POST", "/v1/upload",
                Project,
                new AgentToolParam("paths", "string[]", "Files or folders, relative to the project root.", true),
                new AgentToolParam("overwrite", "boolean", "Re-send files that already exist on the server (default true).")),
            new AgentTool("terminal_connect", "Open an SSH terminal session for this key. Returns a job; may wait for human approval.", AgentScopes.Terminal, "POST", "/v1/terminal/connect",
                Project,
                new AgentToolParam("profileId", "string", "SSH connection profile id (optional; defaults to the project's SSH profile).")),
            new AgentTool("terminal_run", "Run one single-line command in the open terminal session (dangerous commands are blocked).", AgentScopes.Terminal, "POST", "/v1/terminal/run",
                new AgentToolParam("command", "string", "Single-line shell command.", true),
                new AgentToolParam("timeoutSeconds", "integer", "Wait up to this many seconds (default 12, max 120).")),
            new AgentTool("terminal_close", "Close this key's terminal session.", AgentScopes.Terminal, "POST", "/v1/terminal/close"),
            new AgentTool("get_job", "State and result of a job started by deploy / upload / terminal_connect.", AgentScopes.Read, "GET", "/v1/job",
                new AgentToolParam("id", "string", "Job id.", true)),
            new AgentTool("list_jobs", "Recent jobs of this key.", AgentScopes.Read, "GET", "/v1/jobs"),
        };

        public static AgentTool? Find(string method, string path) =>
            All.FirstOrDefault(t => t.Method == method && string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));

        public static AgentTool? FindByName(string name) =>
            All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------------------------------
    // Jobs
    // ------------------------------------------------------------------------------------------

    public sealed class AgentJob
    {
        public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
        public string Kind { get; init; } = "";
        public string Project { get; init; } = "";
        public string KeyId { get; init; } = "";
        public string KeyName { get; init; } = "";
        public string State { get; set; } = "awaiting_approval";
        public string Summary { get; set; } = "";
        public JObject? Result { get; set; }
        public DateTime CreatedUtc { get; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["id"] = Id,
                ["kind"] = Kind,
                ["project"] = Project,
                ["state"] = State,
                ["summary"] = Summary,
                ["createdUtc"] = CreatedUtc,
                ["updatedUtc"] = UpdatedUtc
            };
            if (Result != null)
            {
                o["result"] = Result;
            }

            return o;
        }

        public void Set(string state, string summary, JObject? result = null)
        {
            State = state;
            Summary = summary;
            Result = result;
            UpdatedUtc = DateTime.UtcNow;
        }
    }

    public static class AgentApiJobs
    {
        private static readonly ConcurrentDictionary<string, AgentJob> Jobs = new();

        public static AgentJob Create(string kind, string project, AgentApiKey key)
        {
            var job = new AgentJob { Kind = kind, Project = project, KeyId = key.Id, KeyName = key.Name };
            Jobs[job.Id] = job;
            foreach (var old in Jobs.Values.OrderByDescending(j => j.CreatedUtc).Skip(100).ToList())
            {
                Jobs.TryRemove(old.Id, out _);
            }

            return job;
        }

        public static AgentJob? Get(string id, AgentApiKey key) =>
            Jobs.TryGetValue(id ?? "", out var j) && j.KeyId == key.Id ? j : null;

        public static IEnumerable<AgentJob> ForKey(AgentApiKey key) =>
            Jobs.Values.Where(j => j.KeyId == key.Id).OrderByDescending(j => j.CreatedUtc).Take(30);
    }

    // ------------------------------------------------------------------------------------------
    // Human approval + audit
    // ------------------------------------------------------------------------------------------

    public static class AgentApiGuard
    {
        private static readonly object AuditGate = new();

        private static readonly string AuditPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GitDeployPro",
            "agent_api_audit.log");

        public static async Task<bool> RequestApprovalAsync(AgentApiKey key, string action, string detail)
        {
            if (string.Equals(key.Approval, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var message = $"Agent \"{key.Name}\" wants to: {action}\n\n{detail}\n\nAllow?";
            new NotificationService().ShowToast("Agent API", $"{key.Name}: {action}");
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher == null)
            {
                return false;
            }

            return await app.Dispatcher.InvokeAsync(() => ModernMessageBox.Show(
                message,
                "Agent API request",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question,
                primaryText: "Allow",
                secondaryText: "Deny")).Task.ConfigureAwait(false);
        }

        public static void Audit(AgentApiKey? key, string tool, string project, string outcome)
        {
            try
            {
                lock (AuditGate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(AuditPath)!);
                    if (File.Exists(AuditPath) && new FileInfo(AuditPath).Length > 2_000_000)
                    {
                        File.Move(AuditPath, AuditPath + ".1", overwrite: true);
                    }

                    File.AppendAllText(
                        AuditPath,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{key?.Name ?? "-"}\t{tool}\t{project}\t{outcome}{Environment.NewLine}");
                }
            }
            catch
            {
                // audit must never break a request
            }
        }
    }

    // ------------------------------------------------------------------------------------------
    // Actions
    // ------------------------------------------------------------------------------------------

    public sealed class AgentApiException : Exception
    {
        public int Status { get; }

        public AgentApiException(int status, string message) : base(message)
        {
            Status = status;
        }
    }

    public static class AgentApiActions
    {
        private static readonly ConfigurationService Config = new();
        private static readonly ConcurrentDictionary<string, long> TerminalChats = new();

        public static async Task<JObject> ExecuteAsync(AgentTool tool, JObject args, AgentApiKey key)
        {
            if (!key.Has(tool.Scope))
            {
                throw new AgentApiException(403, $"This key has no '{tool.Scope}' scope.");
            }

            switch (tool.Name)
            {
                case "gitdeploy_status":
                    return Status(key);
                case "list_projects":
                    return new JObject { ["projects"] = new JArray(AllowedProjects(key).Select(p => ProjectInfo(p))) };
                case "get_changes":
                    return await GetChangesAsync(ResolveProject(args, key)).ConfigureAwait(false);
                case "deploy":
                    return StartJob("deploy", ResolveProject(args, key), key, "deploy the project (commit, push, upload changes)",
                        () => DeployAsync(args));
                case "upload":
                {
                    var project = ResolveProject(args, key);
                    var paths = (args["paths"] as JArray)?.Select(t => t.ToString()).Where(s => s.Length > 0).ToList() ?? new List<string>();
                    if (paths.Count == 0)
                    {
                        throw new AgentApiException(400, "'paths' is required (relative to the project root).");
                    }

                    var overwrite = args["overwrite"]?.Type != JTokenType.Boolean || args["overwrite"]!.Value<bool>();
                    return StartJob("upload", project, key, $"upload {paths.Count} path(s)",
                        () => UploadAsync(project, paths, overwrite), string.Join("\n", paths.Take(15)));
                }
                case "terminal_connect":
                {
                    var project = ResolveProject(args, key, required: false);
                    var profile = ResolveSshProfile(project, args["profileId"]?.ToString());
                    return StartJob("terminal_connect", project, key, $"open an SSH terminal on '{profile.Name}'",
                        () => ConnectTerminalAsync(key, profile), profile.Host);
                }
                case "terminal_run":
                    return await RunTerminalAsync(key, args).ConfigureAwait(false);
                case "terminal_close":
                    if (TerminalChats.TryRemove(key.Id, out var chat))
                    {
                        await TelegramSshSessionHub.CloseAsync(chat).ConfigureAwait(false);
                    }

                    return new JObject { ["closed"] = true };
                case "get_job":
                {
                    var job = AgentApiJobs.Get(args["id"]?.ToString() ?? "", key)
                              ?? throw new AgentApiException(404, "Job not found.");
                    return job.ToJson();
                }
                case "list_jobs":
                    return new JObject { ["jobs"] = new JArray(AgentApiJobs.ForKey(key).Select(j => j.ToJson())) };
                default:
                    throw new AgentApiException(404, "Unknown tool.");
            }
        }

        // ---- read --------------------------------------------------------------------------

        private static JObject Status(AgentApiKey key)
        {
            var cfg = Config.LoadGlobalConfig();
            return new JObject
            {
                ["app"] = "GitDeployPro",
                ["version"] = typeof(AgentApiActions).Assembly.GetName().Version?.ToString(3),
                ["activeProject"] = cfg.LastProjectPath,
                ["scopes"] = new JArray(key.Scopes),
                ["approval"] = key.Approval
            };
        }

        private static IEnumerable<string> KnownProjects()
        {
            var cfg = Config.LoadGlobalConfig();
            var list = new List<string>();
            if (!string.IsNullOrWhiteSpace(cfg.LastProjectPath))
            {
                list.Add(cfg.LastProjectPath);
            }

            list.AddRange((cfg.RecentProjects ?? new()).Select(r => r.Path));
            return list.Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                .Select(NormalizePath)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> AllowedProjects(AgentApiKey key)
        {
            var known = KnownProjects().ToList();
            if (key.Projects.Count == 0)
            {
                return known;
            }

            var allowed = key.Projects.Select(NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return known.Where(allowed.Contains);
        }

        private static JObject ProjectInfo(string path) => new()
        {
            ["path"] = path,
            ["name"] = Path.GetFileName(path),
            ["supportsDeploy"] = TelegramProjectProfile.SupportsDeploy(path),
            ["kind"] = TelegramProjectProfile.GetKindLabel(path)
        };

        private static string NormalizePath(string p) =>
            Path.GetFullPath(p.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        private static string ResolveProject(JObject args, AgentApiKey key, bool required = true)
        {
            var requested = args["project"]?.ToString();
            if (string.IsNullOrWhiteSpace(requested))
            {
                requested = Config.LoadGlobalConfig().LastProjectPath;
            }

            if (string.IsNullOrWhiteSpace(requested))
            {
                if (!required)
                {
                    return string.Empty;
                }

                throw new AgentApiException(400, "No project given and no active project.");
            }

            var normalized = NormalizePath(requested);
            var match = AllowedProjects(key).FirstOrDefault(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                throw new AgentApiException(403, "Project is unknown to GitDeploy or not allowed for this key (see list_projects).");
            }

            return match;
        }

        private static async Task<JObject> GetChangesAsync(string project)
        {
            var changes = await OnUiAsync(async () =>
            {
                // GitService reads one static working directory (and its constructor resets it to the active
                // project), so: construct first, switch, run, then restore what the UI was using.
                var git = new GitService();
                var previous = GitService.WorkingDirectoryPath;
                GitService.SetWorkingDirectory(project);
                try
                {
                    return await git.GetUncommittedChangesAsync(includeDiff: false).ConfigureAwait(true);
                }
                finally
                {
                    GitService.SetWorkingDirectory(previous);
                }
            }, TimeSpan.FromSeconds(40)).ConfigureAwait(false);

            return new JObject
            {
                ["project"] = project,
                ["count"] = changes.Count,
                ["files"] = new JArray(changes.Select(c => new JObject
                {
                    ["path"] = c.Name,
                    ["status"] = c.Type.ToString(),
                    ["oldPath"] = c.OldPath
                }))
            };
        }

        // ---- jobs --------------------------------------------------------------------------

        private static JObject StartJob(
            string kind,
            string project,
            AgentApiKey key,
            string action,
            Func<Task<(bool Ok, string Summary, JObject Result)>> work,
            string detail = "")
        {
            var job = AgentApiJobs.Create(kind, project, key);
            _ = Task.Run(async () =>
            {
                try
                {
                    var approved = await AgentApiGuard.RequestApprovalAsync(
                        key, action, (string.IsNullOrEmpty(project) ? "" : "Project: " + project + "\n") + detail).ConfigureAwait(false);
                    if (!approved)
                    {
                        job.Set("denied", "A human denied this request in GitDeploy.");
                        AgentApiGuard.Audit(key, kind, project, "denied");
                        return;
                    }

                    job.Set("running", "Running…");
                    var (ok, summary, result) = await work().ConfigureAwait(false);
                    job.Set(ok ? "done" : "failed", summary, result);
                    AgentApiGuard.Audit(key, kind, project, ok ? "done" : "failed: " + summary);
                }
                catch (Exception ex)
                {
                    job.Set("failed", ex.Message);
                    AgentApiGuard.Audit(key, kind, project, "error: " + ex.Message);
                }
            });

            return new JObject { ["jobId"] = job.Id, ["state"] = job.State, ["poll"] = "get_job / GET /v1/job?id=" + job.Id };
        }

        private static async Task<(bool, string, JObject)> DeployAsync(JObject args)
        {
            var project = ResolveProjectUnchecked(args);
            var r = await TelegramDeployCoordinator.RunAsync(project).ConfigureAwait(false);
            return (r.Ok, r.Summary, new JObject
            {
                ["changeCount"] = r.ChangeCount,
                ["uploadedCount"] = r.UploadedCount,
                ["commitDone"] = r.CommitDone,
                ["pushOk"] = r.PushOk
            });
        }

        // The project was validated before the job started; re-resolve it from the same args.
        private static string ResolveProjectUnchecked(JObject args)
        {
            var requested = args["project"]?.ToString();
            return NormalizePath(string.IsNullOrWhiteSpace(requested) ? Config.LoadGlobalConfig().LastProjectPath : requested);
        }

        private static async Task<(bool, string, JObject)> UploadAsync(string project, List<string> paths, bool overwrite)
        {
            var cfg = Config.LoadProjectConfig(project);
            var connections = Config.LoadConnections();
            var profileId = cfg.ConnectionProfileIds?.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id)) ?? cfg.ConnectionProfileId;
            var profile = connections.FirstOrDefault(c => string.Equals(c.Id, profileId, StringComparison.OrdinalIgnoreCase));
            if (profile == null || string.IsNullOrWhiteSpace(profile.Host))
            {
                return (false, "The project has no FTP/SFTP connection profile.", new JObject());
            }

            var mappings = RemotePathResolver.GetActiveMappings(profile);
            var profileBase = RemotePathResolver.NormalizeRemoteBase(profile.RemotePath);
            var jobs = new List<RemoteTransferJob>();
            var skipped = new JArray();

            foreach (var raw in paths)
            {
                var full = Path.GetFullPath(Path.Combine(project, raw.Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(project + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(full, project, StringComparison.OrdinalIgnoreCase))
                {
                    skipped.Add(raw + " (outside the project)");
                    continue;
                }

                IEnumerable<string> files = File.Exists(full)
                    ? new[] { full }
                    : Directory.Exists(full)
                        ? Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
                            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        : Array.Empty<string>();

                var any = false;
                foreach (var file in files)
                {
                    any = true;
                    if (jobs.Count >= 5000)
                    {
                        skipped.Add("limit of 5000 files reached");
                        break;
                    }

                    var relative = Path.GetRelativePath(project, file).Replace('\\', '/');
                    if (!RemotePathResolver.TryResolveDeployTarget(relative, mappings, profileBase, out var remote, out _, out _))
                    {
                        skipped.Add(relative + " (outside all path mappings)");
                        continue;
                    }

                    jobs.Add(new RemoteTransferJob
                    {
                        RemotePath = remote,
                        LocalPath = file,
                        SizeBytes = new FileInfo(file).Length,
                        Tag = relative
                    });
                }

                if (!any)
                {
                    skipped.Add(raw + " (not found)");
                }
            }

            if (jobs.Count == 0)
            {
                return (false, "Nothing to upload.", new JObject { ["skipped"] = skipped });
            }

            var result = await ParallelRemoteTransfer.UploadAsync(
                profile, jobs, Math.Min(3, jobs.Count), null, CancellationToken.None, skipIfExists: !overwrite).ConfigureAwait(false);

            var ok = result.IsComplete;
            return (ok,
                ok ? $"Uploaded {result.Completed} file(s) via '{profile.Name}'." : $"Upload finished with {result.Errors.Count} error(s).",
                new JObject
                {
                    ["profile"] = profile.Name,
                    ["completed"] = result.Completed,
                    ["skippedExisting"] = result.Skipped,
                    ["errors"] = new JArray(result.Errors.Take(20)),
                    ["skipped"] = skipped
                });
        }

        // ---- terminal ----------------------------------------------------------------------

        private static ConnectionProfile ResolveSshProfile(string project, string? profileId)
        {
            var connections = Config.LoadConnections().Where(c => c.UseSSH).ToList();
            if (!string.IsNullOrWhiteSpace(profileId))
            {
                return connections.FirstOrDefault(c => string.Equals(c.Id, profileId, StringComparison.OrdinalIgnoreCase))
                       ?? throw new AgentApiException(404, "SSH profile not found.");
            }

            if (!string.IsNullOrEmpty(project))
            {
                var cfg = Config.LoadProjectConfig(project);
                var ids = new List<string> { cfg.DefaultSshProfileId };
                ids.AddRange(cfg.ConnectionProfileIds ?? new());
                ids.Add(cfg.ConnectionProfileId);
                foreach (var id in ids.Where(i => !string.IsNullOrWhiteSpace(i)))
                {
                    var hit = connections.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
                    if (hit != null)
                    {
                        return hit;
                    }
                }
            }

            throw new AgentApiException(400, "No SSH profile: pass 'profileId' or set a default SSH profile for the project.");
        }

        private static long ChatIdFor(AgentApiKey key) =>
            -1_000_000_000L - (uint)StringComparer.Ordinal.GetHashCode(key.Id);

        private static async Task<(bool, string, JObject)> ConnectTerminalAsync(AgentApiKey key, ConnectionProfile profile)
        {
            var chat = ChatIdFor(key);
            var (ok, message) = await TelegramSshSessionHub.ConnectAsync(chat, profile).ConfigureAwait(false);
            if (ok)
            {
                TerminalChats[key.Id] = chat;
            }

            return (ok, ok ? $"Connected to '{profile.Name}'." : message, new JObject { ["profile"] = profile.Name });
        }

        private static async Task<JObject> RunTerminalAsync(AgentApiKey key, JObject args)
        {
            if (!TerminalChats.TryGetValue(key.Id, out var chat) || !TelegramSshSessionHub.IsOpen(chat))
            {
                throw new AgentApiException(409, "No open terminal session: call terminal_connect first and wait until the job is done.");
            }

            var guard = TelegramSshCommandGuard.Evaluate(args["command"]?.ToString());
            if (!guard.Ok)
            {
                AgentApiGuard.Audit(key, "terminal_run", "", "blocked: " + (args["command"]?.ToString() ?? ""));
                throw new AgentApiException(422, "Command blocked by GitDeploy's safety filter (" + guard.BlockKey + ").");
            }

            var timeout = Math.Clamp(args["timeoutSeconds"]?.Value<int>() ?? 12, 1, 120);
            var (output, cwd, stillRunning) = await TelegramSshSessionHub.RunAsync(chat, guard.Command, timeout).ConfigureAwait(false);
            AgentApiGuard.Audit(key, "terminal_run", "", guard.Command);
            if (output.Length > 20000)
            {
                output = output[..20000] + "\n…[truncated]";
            }

            return new JObject { ["command"] = guard.Command, ["output"] = output, ["cwd"] = cwd, ["stillRunning"] = stillRunning };
        }

        // ---- ui thread ---------------------------------------------------------------------

        private static async Task<T> OnUiAsync<T>(Func<Task<T>> work, TimeSpan timeout)
        {
            var app = System.Windows.Application.Current;
            Task<T> task = app?.Dispatcher == null || app.Dispatcher.CheckAccess()
                ? work()
                : app.Dispatcher.InvokeAsync(work).Task.Unwrap();
            var finished = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
            if (finished != task)
            {
                throw new AgentApiException(504, "The app did not answer in time.");
            }

            return await task.ConfigureAwait(false);
        }
    }
}
