using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Models;
using GitDeployPro.Services.Localization;
using Newtonsoft.Json.Linq;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// Runs Cursor CLI for a project chat turn (warm ACP daemon preferred, one-shot -p fallback)
    /// and mirrors the reply into the thread + Telegram.
    /// </summary>
    public sealed class CursorAgentBridge
    {
        public static CursorAgentBridge Instance { get; } = new();

        private readonly ConcurrentDictionary<string, byte> _workers = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ConcurrentQueue<PendingTurn>> _queues =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _turnCts =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly TelegramChatStore _store = TelegramChatStore.Instance;
        private readonly ConfigurationService _config = new();
        private readonly object _telegramProgressGate = new();
        private readonly object _pathCacheGate = new();
        private DateTime _lastTelegramProgressUtc = DateTime.MinValue;
        private DateTime _lastTelegramThinkingUtc = DateTime.MinValue;
        private string? _cachedConfiguredPath;
        private string? _cachedAgentPath;
        private string? _cachedNodeExe;
        private string? _cachedIndexJs;
        private DateTime _pathCacheUtc = DateTime.MinValue;

        private CursorAgentBridge()
        {
        }

        public void EnqueueUserTurn(string projectPath, string text, string? photoPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || TelegramPaths.IsUnassigned(projectPath))
            {
                return;
            }

            var config = _config.LoadGlobalConfig();
            if (!config.CursorAgentEnabled)
            {
                return;
            }

            var promptText = (text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(promptText) && string.IsNullOrWhiteSpace(photoPath))
            {
                return;
            }

            var key = TelegramPaths.ToProjectKey(projectPath);
            var queue = _queues.GetOrAdd(key, _ => new ConcurrentQueue<PendingTurn>());
            queue.Enqueue(new PendingTurn(projectPath, promptText, photoPath));

            if (!_workers.TryAdd(key, 0))
            {
                PostStatus(projectPath, Loc.T("cursor.queued", queue.Count));
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        while (queue.TryDequeue(out var turn))
                        {
                            using var turnCts = BeginTurnCts(key);
                            try
                            {
                                await RunTurnAsync(turn.ProjectPath, turn.Text, turn.PhotoPath, turnCts.Token)
                                    .ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                PostStatus(turn.ProjectPath, Loc.T("cursor.cancelled"));
                            }
                            catch (Exception ex)
                            {
                                PostStatus(turn.ProjectPath, Loc.T("cursor.failed", ex.Message));
                            }
                        }

                        _workers.TryRemove(key, out _);
                        if (queue.IsEmpty)
                        {
                            break;
                        }

                        if (!_workers.TryAdd(key, 0))
                        {
                            break;
                        }
                    }
                }
                catch
                {
                    _workers.TryRemove(key, out _);
                }
            });
        }

        /// <summary>
        /// Start (or refresh) the warm ACP daemon when a project is opened so the first chat turn is fast.
        /// </summary>
        public void PrewarmForProject(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || TelegramPaths.IsUnassigned(projectPath))
            {
                return;
            }

            var config = _config.LoadGlobalConfig();
            if (!config.CursorAgentEnabled)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var agentPath = ResolveAgentExecutable(config.CursorAgentPath);
                    if (string.IsNullOrWhiteSpace(agentPath))
                    {
                        return;
                    }

                    ResolveNodeEntryCached(agentPath, out var nodeExe, out var indexJs);
                    await CursorAcpPool.Instance
                        .GetOrCreateAsync(
                            projectPath,
                            agentPath,
                            nodeExe,
                            indexJs,
                            config.CursorAgentModel,
                            CancellationToken.None,
                            _store.GetCursorAgentMode(projectPath))
                        .ConfigureAwait(false);
                    PostStatus(projectPath, Loc.T("cursor.progress.warmReady"));
                }
                catch
                {
                    // Best-effort — first real turn will cold-start or fall back to -p.
                }
            });
        }

        public bool CancelCurrentTurn(string projectPath)
        {
            var key = TelegramPaths.ToProjectKey(projectPath);
            if (_turnCts.TryGetValue(key, out var cts))
            {
                try
                {
                    cts.Cancel();
                    return true;
                }
                catch
                {
                }
            }

            return false;
        }

        /// <summary>
        /// Cancels the in-flight turn and drops any queued turns for this project.
        /// </summary>
        public bool StopTurn(string projectPath)
        {
            var key = TelegramPaths.ToProjectKey(projectPath);
            var clearedQueued = false;
            if (_queues.TryGetValue(key, out var queue))
            {
                while (queue.TryDequeue(out _))
                {
                    clearedQueued = true;
                }
            }

            var cancelled = CancelCurrentTurn(projectPath);
            if (!cancelled && clearedQueued)
            {
                PostStatus(projectPath, Loc.T("cursor.cancelled"));
            }

            return cancelled || clearedQueued;
        }

        public static JObject? BuildStopInlineMarkup()
            => TelegramMarkup.Inline(new[]
            {
                (Loc.T("telegram.btnStopAgent"), "agent:stop")
            });

        public void RestartProjectAgent(string projectPath, string? reason = null)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || TelegramPaths.IsUnassigned(projectPath))
            {
                return;
            }

            CancelCurrentTurn(projectPath);
            _store.ClearAllCursorSessions(projectPath);
            _store.SetCursorAgentMode(projectPath, "agent");
            CursorAcpPool.Instance.DisposeProject(projectPath);
            PostStatus(
                projectPath,
                string.IsNullOrWhiteSpace(reason)
                    ? Loc.T("cursor.restarted")
                    : Loc.T("cursor.restartedReason", reason));
            PrewarmForProject(projectPath);
        }

        /// <summary>Sticky Cursor mode for this project (agent|plan). Restarts warm ACP session.</summary>
        public void SetAgentModeAndRestart(string projectPath, string mode, string? reason = null)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || TelegramPaths.IsUnassigned(projectPath))
            {
                return;
            }

            var next = string.Equals((mode ?? string.Empty).Trim(), "plan", StringComparison.OrdinalIgnoreCase)
                ? "plan"
                : "agent";
            CancelCurrentTurn(projectPath);
            _store.ClearAllCursorSessions(projectPath);
            _store.SetCursorAgentMode(projectPath, next);
            CursorAcpPool.Instance.DisposeProject(projectPath);
            PostStatus(
                projectPath,
                reason
                ?? (next == "plan" ? Loc.T("cursor.modePlan") : Loc.T("cursor.modeAgent")));
            PrewarmForProject(projectPath);
        }

        public void SetModelAndRestart(string projectPath, string? modelId)
        {
            var model = (modelId ?? string.Empty).Trim();
            _config.UpdateGlobalConfig(cfg => cfg.CursorAgentModel = model);
            RestartProjectAgent(
                projectPath,
                string.IsNullOrWhiteSpace(model)
                    ? Loc.T("cursor.modelDefault")
                    : Loc.T("cursor.modelSet", model));
            PostStatus(
                projectPath,
                string.IsNullOrWhiteSpace(model)
                    ? Loc.T("cursor.modelDefault")
                    : Loc.T("cursor.modelSet", model));
        }

        public CursorAgentStatusInfo GetAgentStatus(string projectPath)
        {
            var config = _config.LoadGlobalConfig();
            var key = TelegramPaths.ToProjectKey(projectPath);
            var queueDepth = _queues.TryGetValue(key, out var q) ? q.Count : 0;
            var busy = _workers.ContainsKey(key);
            DateTime? lastActivity = null;
            var alive = false;
            if (CursorAcpPool.Instance.TryGetDaemon(projectPath, out var daemon) && daemon != null)
            {
                alive = daemon.IsAlive;
                lastActivity = daemon.LastActivityUtc;
            }

            var model = string.IsNullOrWhiteSpace(config.CursorAgentModel)
                ? "(default)"
                : config.CursorAgentModel.Trim();
            var acp = _store.GetAcpSessionId(projectPath);
            var cli = _store.GetCliSessionId(projectPath);
            var sessionHint = !string.IsNullOrWhiteSpace(acp)
                ? "ACP " + TruncateId(acp)
                : (!string.IsNullOrWhiteSpace(cli) ? "CLI " + TruncateId(cli) : "none");

            var thread = _store.LoadThread(projectPath);
            AgentTokenUsage? lastUsage = null;
            if (!string.IsNullOrWhiteSpace(thread.LastUsageSource)
                || thread.LastUsageInputTokens > 0
                || thread.LastUsageOutputTokens > 0)
            {
                lastUsage = new AgentTokenUsage
                {
                    Available = true,
                    InputTokens = thread.LastUsageInputTokens,
                    OutputTokens = thread.LastUsageOutputTokens,
                    CacheReadTokens = thread.LastUsageCacheReadTokens,
                    CacheWriteTokens = thread.LastUsageCacheWriteTokens,
                    Source = thread.LastUsageSource
                };
            }

            var sessionUsage = new AgentTokenUsage
            {
                Available = thread.SessionUsageInputTokens > 0
                            || thread.SessionUsageOutputTokens > 0
                            || thread.SessionUsageCacheReadTokens > 0
                            || thread.SessionUsageCacheWriteTokens > 0,
                InputTokens = thread.SessionUsageInputTokens,
                OutputTokens = thread.SessionUsageOutputTokens,
                CacheReadTokens = thread.SessionUsageCacheReadTokens,
                CacheWriteTokens = thread.SessionUsageCacheWriteTokens,
                Source = "session"
            };

            return new CursorAgentStatusInfo
            {
                Enabled = config.CursorAgentEnabled,
                DaemonAlive = alive,
                Model = model,
                QueueDepth = queueDepth,
                TurnBusy = busy,
                LastActivityUtc = lastActivity,
                SessionHint = sessionHint,
                AgentMode = _store.GetCursorAgentMode(projectPath),
                LastUsage = lastUsage,
                SessionUsage = sessionUsage.Available ? sessionUsage : null
            };
        }

        public void InvalidateAfterSettingsSave(string? projectPath = null)
        {
            if (!string.IsNullOrWhiteSpace(projectPath) && !TelegramPaths.IsUnassigned(projectPath))
            {
                RestartProjectAgent(projectPath, Loc.T("cursor.settingsReload"));
                return;
            }

            var path = _store.GetActiveProjectPath();
            if (!string.IsNullOrWhiteSpace(path) && !TelegramPaths.IsUnassigned(path))
            {
                RestartProjectAgent(path, Loc.T("cursor.settingsReload"));
            }
        }

        private CancellationTokenSource BeginTurnCts(string key)
        {
            var cts = new CancellationTokenSource();
            if (_turnCts.TryRemove(key, out var previous))
            {
                try
                {
                    previous.Cancel();
                }
                catch
                {
                }

                try
                {
                    previous.Dispose();
                }
                catch
                {
                }
            }

            _turnCts[key] = cts;
            return cts;
        }

        private static string TruncateId(string id)
        {
            var value = (id ?? string.Empty).Trim();
            return value.Length <= 8 ? value : value[..8] + "…";
        }

        public void Shutdown()
        {
            foreach (var pair in _turnCts)
            {
                try
                {
                    pair.Value.Cancel();
                }
                catch
                {
                }
            }

            try
            {
                CursorAcpPool.Instance.DisposeAll();
            }
            catch
            {
            }

            try
            {
                TelegramOutboundQueue.Instance.Dispose();
            }
            catch
            {
            }
        }

        public string? ResolveAgentExecutable(string? configuredPath = null)
        {
            var configured = configuredPath?.Trim() ?? string.Empty;
            lock (_pathCacheGate)
            {
                if (!string.IsNullOrWhiteSpace(_cachedAgentPath)
                    && string.Equals(_cachedConfiguredPath, configured, StringComparison.Ordinal)
                    && (DateTime.UtcNow - _pathCacheUtc).TotalMinutes < 10
                    && File.Exists(_cachedAgentPath))
                {
                    return _cachedAgentPath;
                }
            }

            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(configured))
            {
                candidates.Add(configured);
            }

            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "cursor-agent",
                "agent.cmd"));
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "cursor-agent",
                "cursor-agent.cmd"));
            candidates.Add("agent");
            candidates.Add("cursor-agent");
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "cursor-agent",
                "agent.exe"));
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "bin",
                "agent.exe"));
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cursor",
                "bin",
                "agent.exe"));

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                var looksLikePath = candidate.Contains('\\') || candidate.Contains('/')
                    || candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    || candidate.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                    || candidate.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
                if (looksLikePath)
                {
                    if (File.Exists(candidate))
                    {
                        CacheAgentPath(configured, candidate);
                        return candidate;
                    }

                    continue;
                }

                var fromPath = FindOnPath(candidate);
                if (!string.IsNullOrWhiteSpace(fromPath))
                {
                    CacheAgentPath(configured, fromPath);
                    return fromPath;
                }
            }

            return null;
        }

        private void CacheAgentPath(string configured, string agentPath)
        {
            lock (_pathCacheGate)
            {
                _cachedConfiguredPath = configured;
                _cachedAgentPath = agentPath;
                _pathCacheUtc = DateTime.UtcNow;
                _cachedNodeExe = null;
                _cachedIndexJs = null;
            }
        }

        public async Task<CursorAgentTestResult> TestConnectionAsync(CancellationToken cancellationToken)
        {
            var config = _config.LoadGlobalConfig();
            var agentPath = ResolveAgentExecutable(config.CursorAgentPath);
            if (string.IsNullOrWhiteSpace(agentPath))
            {
                return new CursorAgentTestResult
                {
                    Ok = false,
                    Message = Loc.T("cursor.agentMissing")
                };
            }

            var auth = await CheckLoginStatusAsync(agentPath, cancellationToken).ConfigureAwait(false);
            if (!auth.LoggedIn)
            {
                return new CursorAgentTestResult
                {
                    Ok = false,
                    NeedsLogin = true,
                    AgentPath = agentPath,
                    Message = Loc.T("cursor.help.testNeedLogin")
                };
            }

            var workspace = _store.GetActiveProjectPath();
            if (string.IsNullOrWhiteSpace(workspace)
                || TelegramPaths.IsUnassigned(workspace)
                || !Directory.Exists(workspace))
            {
                workspace = config.LastProjectPath;
            }

            if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
            {
                workspace = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            try
            {
                var run = await RunAgentProcessAsync(
                        agentPath,
                        workspace,
                        "Reply with exactly: GitDeploy CLI OK",
                        config.CursorAgentModel,
                        resumeSessionId: null,
                        mode: "agent",
                        onProgress: null,
                        onSessionId: null,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(run.Output))
                {
                    return new CursorAgentTestResult
                    {
                        Ok = false,
                        AgentPath = agentPath,
                        Workspace = workspace,
                        Account = auth.Account,
                        Message = Loc.T("cursor.emptyReply")
                    };
                }

                return new CursorAgentTestResult
                {
                    Ok = true,
                    AgentPath = agentPath,
                    Workspace = workspace,
                    Account = auth.Account,
                    OutputSnippet = TrimReply(run.Output),
                    Message = Loc.T("cursor.help.testOk", agentPath)
                };
            }
            catch (Exception ex)
            {
                return new CursorAgentTestResult
                {
                    Ok = false,
                    AgentPath = agentPath,
                    Workspace = workspace,
                    Account = auth.Account,
                    Message = Loc.T("cursor.help.testFail", ex.Message)
                };
            }
        }

        /// <summary>
        /// Runs <c>agent status</c> / <c>whoami</c> to see if the CLI is authenticated.
        /// </summary>
        public async Task<CursorAgentAuthStatus> CheckLoginStatusAsync(
            string? agentPath = null,
            CancellationToken cancellationToken = default)
        {
            agentPath ??= ResolveAgentExecutable(_config.LoadGlobalConfig().CursorAgentPath);
            if (string.IsNullOrWhiteSpace(agentPath))
            {
                return new CursorAgentAuthStatus
                {
                    LoggedIn = false,
                    Message = Loc.T("cursor.agentMissing")
                };
            }

            try
            {
                var output = await RunAgentCommandTextAsync(
                        agentPath,
                        new[] { "status" },
                        cancellationToken)
                    .ConfigureAwait(false);
                var text = (output ?? string.Empty).Trim();
                if (LooksLoggedIn(text))
                {
                    return new CursorAgentAuthStatus
                    {
                        LoggedIn = true,
                        Account = ExtractAccount(text),
                        AgentPath = agentPath,
                        Message = text
                    };
                }

                return new CursorAgentAuthStatus
                {
                    LoggedIn = false,
                    AgentPath = agentPath,
                    Message = string.IsNullOrWhiteSpace(text)
                        ? Loc.T("cursor.help.testNeedLogin")
                        : text
                };
            }
            catch (Exception ex)
            {
                return new CursorAgentAuthStatus
                {
                    LoggedIn = false,
                    AgentPath = agentPath,
                    Message = ex.Message
                };
            }
        }

        /// <summary>
        /// Opens a visible PowerShell that runs <c>agent login</c> (browser sign-in).
        /// </summary>
        public void OpenLoginTerminal(string? agentPath = null)
        {
            agentPath ??= ResolveAgentExecutable(_config.LoadGlobalConfig().CursorAgentPath);
            if (string.IsNullOrWhiteSpace(agentPath) || !File.Exists(agentPath))
            {
                throw new InvalidOperationException(Loc.T("cursor.help.loginMissing"));
            }

            var agentLiteral = agentPath.Replace("'", "''");
            var inner = string.Join("; ", new[]
            {
                "Write-Host '=== GitDeploy: Cursor CLI login ===' -ForegroundColor Cyan",
                "Write-Host 'A browser window should open. Sign in with your Cursor account.' -ForegroundColor Yellow",
                "Write-Host ''",
                $"& '{agentLiteral}' login",
                "Write-Host ''",
                "Write-Host '=== Login finished (or cancelled) ===' -ForegroundColor Green",
                "Write-Host 'Close this window, then click Test CLI in GitDeploy.' -ForegroundColor Yellow",
                "Write-Host ''",
                "pause"
            });

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoExit -NoProfile -ExecutionPolicy Bypass -Command \"" + inner.Replace("\"", "\\\"") + "\"",
                UseShellExecute = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            });
        }

        private static bool LooksLoggedIn(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (text.Contains("not logged", StringComparison.OrdinalIgnoreCase)
                || text.Contains("unauthenticated", StringComparison.OrdinalIgnoreCase)
                || text.Contains("please login", StringComparison.OrdinalIgnoreCase)
                || text.Contains("please log in", StringComparison.OrdinalIgnoreCase)
                || text.Contains("run 'agent login'", StringComparison.OrdinalIgnoreCase)
                || text.Contains("run \"agent login\"", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return text.Contains("logged in", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("@", StringComparison.Ordinal);
        }

        private static string ExtractAccount(string text)
        {
            // Typical: "✓ Logged in as user@example.com"
            var asIdx = text.IndexOf(" as ", StringComparison.OrdinalIgnoreCase);
            if (asIdx >= 0)
            {
                var rest = text[(asIdx + 4)..].Trim();
                var end = rest.IndexOfAny(['\r', '\n', ' ']);
                return end > 0 ? rest[..end].Trim() : rest;
            }

            return string.Empty;
        }

        private async Task<string> RunAgentCommandTextAsync(
            string agentPath,
            string[] args,
            CancellationToken cancellationToken)
        {
            var start = new ProcessStartInfo
            {
                FileName = agentPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = start };
            if (!process.Start())
            {
                throw new InvalidOperationException("Could not start Cursor agent.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var combined = ((stdout ?? string.Empty) + Environment.NewLine + (stderr ?? string.Empty)).Trim();
            return combined;
        }

        private async Task RunTurnAsync(
            string projectPath,
            string text,
            string? photoPath,
            CancellationToken cancellationToken)
        {
            EnsureProjectSynced(projectPath);

            var config = _config.LoadGlobalConfig();
            var agentPath = ResolveAgentExecutable(config.CursorAgentPath);
            if (string.IsNullOrWhiteSpace(agentPath))
            {
                PostStatus(projectPath, Loc.T("cursor.agentMissing"));
                return;
            }

            PostStatus(projectPath, Loc.T("cursor.started"));
            NotifyTelegramWorking(projectPath, Loc.T("cursor.started"));
            if (!string.IsNullOrWhiteSpace(photoPath) && File.Exists(photoPath))
            {
                PostStatus(projectPath, Loc.T("cursor.progress.photo"));
            }

            ResolveNodeEntryCached(agentPath, out var nodeExe, out var indexJs);

            // Prefer warm ACP daemon (process stays alive across turns).
            AgentRunResult? run = null;
            var usedAcp = false;
            try
            {
                var daemon = await CursorAcpPool.Instance
                    .GetOrCreateAsync(
                        projectPath,
                        agentPath,
                        nodeExe,
                        indexJs,
                        config.CursorAgentModel,
                        cancellationToken,
                        _store.GetCursorAgentMode(projectPath))
                    .ConfigureAwait(false);

                var hadConversation = daemon.HasConversation;
                PostStatus(
                    projectPath,
                    hadConversation
                        ? Loc.T("cursor.progress.warmTurn")
                        : Loc.T("cursor.progress.warming"));

                var prompt = BuildPrompt(projectPath, text, photoPath, resumeSession: hadConversation);
                run = await daemon
                    .PromptAsync(prompt, line => PostProgress(projectPath, line), cancellationToken)
                    .ConfigureAwait(false);
                usedAcp = true;

                if (!string.IsNullOrWhiteSpace(run.SessionId))
                {
                    _store.SetAcpSessionId(projectPath, run.SessionId);
                }
            }
            catch (CursorAcpAuthException ex)
            {
                PostStatus(projectPath, Loc.T("cursor.authRequired", ex.Message));
            }
            catch (TimeoutException ex)
            {
                PostStatus(projectPath, Loc.T("cursor.progress.timedOut", ex.Message));
                CursorAcpPool.Instance.DisposeProject(projectPath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                PostStatus(projectPath, Loc.T("cursor.progress.acpFallback", ex.Message));
            }

            // Fallback: classic one-shot `agent -p` (+ optional --resume) — never resume ACP ids.
            if (!usedAcp || run == null)
            {
                var existingSession = _store.GetCliSessionId(projectPath);
                var resume = !string.IsNullOrWhiteSpace(existingSession);
                if (resume)
                {
                    PostStatus(projectPath, Loc.T("cursor.progress.resume"));
                }
                else
                {
                    PostStatus(projectPath, Loc.T("cursor.progress.coldStart"));
                }

                var prompt = BuildPrompt(projectPath, text, photoPath, resume);
                run = await RunAgentProcessAsync(
                        agentPath,
                        projectPath,
                        prompt,
                        config.CursorAgentModel,
                        resumeSessionId: resume ? existingSession : null,
                        mode: _store.GetCursorAgentMode(projectPath),
                        onProgress: line => PostProgress(projectPath, line),
                        onSessionId: sid =>
                        {
                            if (!string.IsNullOrWhiteSpace(sid))
                            {
                                _store.SetCliSessionId(projectPath, sid);
                            }
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                if (resume && run.ExitCode != 0 && string.IsNullOrWhiteSpace(run.Output))
                {
                    PostStatus(projectPath, Loc.T("cursor.progress.resumeFailed"));
                    _store.SetCliSessionId(projectPath, null);
                    prompt = BuildPrompt(projectPath, text, photoPath, resumeSession: false);
                    run = await RunAgentProcessAsync(
                            agentPath,
                            projectPath,
                            prompt,
                            config.CursorAgentModel,
                            resumeSessionId: null,
                            mode: _store.GetCursorAgentMode(projectPath),
                            onProgress: line => PostProgress(projectPath, line),
                            onSessionId: sid =>
                            {
                                if (!string.IsNullOrWhiteSpace(sid))
                                {
                                    _store.SetCliSessionId(projectPath, sid);
                                }
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (!string.IsNullOrWhiteSpace(run.SessionId))
            {
                if (string.Equals(run.Source, "acp", StringComparison.OrdinalIgnoreCase) || usedAcp)
                {
                    _store.SetAcpSessionId(projectPath, run.SessionId);
                }
                else
                {
                    _store.SetCliSessionId(projectPath, run.SessionId);
                }
            }

            var rawOutput = string.IsNullOrWhiteSpace(run.Output)
                ? Loc.T("cursor.emptyReply")
                : TrimReply(run.Output);

            var docs = TelegramDocumentDispatch.CollectPaths(projectPath, text, rawOutput).ToList();
            if (!string.IsNullOrWhiteSpace(run.PlanFilePath) && File.Exists(run.PlanFilePath))
            {
                docs.Insert(0, run.PlanFilePath);
            }

            var reply = TelegramDocumentDispatch.StripMarkers(rawOutput);

            if (run.Usage is { Available: true } usage)
            {
                _store.RecordCursorUsage(projectPath, usage);
                reply = reply.TrimEnd() + "\n\n" + FormatUsageFooter(usage);
            }
            else if (string.Equals(run.Source, "acp", StringComparison.OrdinalIgnoreCase))
            {
                // ACP often omits usage — make that explicit once after the reply.
                reply = reply.TrimEnd() + "\n\n" + Loc.T("cursor.usageNaAcp");
            }

            // Plan mode must always leave a dated .md under .cursor/plans/ for Get MD / Build.
            string? planForButtons = null;
            if (!string.IsNullOrWhiteSpace(run.PlanFilePath) && File.Exists(run.PlanFilePath))
            {
                planForButtons = run.PlanFilePath;
            }
            else if (_store.IsCursorPlanMode(projectPath))
            {
                planForButtons = CursorPlanFileWriter.WriteFromReply(projectPath, reply);
                if (!string.IsNullOrWhiteSpace(planForButtons))
                {
                    docs.Insert(0, planForButtons);
                }
            }

            if (string.IsNullOrWhiteSpace(planForButtons))
            {
                planForButtons = docs.FirstOrDefault(p =>
                    CursorPlanCatalog.IsUnderPlansDir(projectPath, p));
            }

            var planPendingId = await PublishAgentReplyAsync(projectPath, reply, cancellationToken, planForButtons)
                .ConfigureAwait(false);

            // Auto-send only the plan file from this turn (not every scraped doc).
            if (!string.IsNullOrWhiteSpace(planForButtons) && File.Exists(planForButtons))
            {
                var buildId = planPendingId
                              ?? TelegramPlanMdBroker.Instance.Register(projectPath, planForButtons);
                await PublishPlanDocumentAsync(projectPath, planForButtons, cancellationToken, buildId)
                    .ConfigureAwait(false);
            }
            else
            {
                foreach (var doc in docs.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (File.Exists(doc) && !CursorPlanCatalog.IsUnderPlansDir(projectPath, doc))
                    {
                        await PublishPlanDocumentAsync(projectPath, doc, cancellationToken, null)
                            .ConfigureAwait(false);
                    }
                }
            }
        }

        private string BuildPrompt(string projectPath, string text, string? photoPath, bool resumeSession)
        {
            var projectName = TelegramPaths.DisplayName(projectPath);
            var requestText = (text ?? string.Empty).Trim();
            var hasPhoto = !string.IsNullOrWhiteSpace(photoPath) && File.Exists(photoPath);
            var workspaceBlock = CursorWorkspaceRoots.BuildPromptContext(projectPath, resumeSession);

            var sb = new StringBuilder();

            // Warm resume: keep request short, but always restate workspace + rules.
            if (resumeSession)
            {
                if (!string.IsNullOrWhiteSpace(workspaceBlock))
                {
                    sb.AppendLine(workspaceBlock);
                    sb.AppendLine();
                }

                sb.AppendLine("Continue the same coding session. Do the new user request now. No greeting.");
                if (!string.IsNullOrWhiteSpace(requestText))
                {
                    sb.AppendLine(requestText);
                }
                else
                {
                    sb.AppendLine("(Photo with no caption — inspect the image and continue the bug fix.)");
                }

                if (hasPhoto)
                {
                    sb.AppendLine($"Image file: {photoPath}");
                }

                return sb.ToString();
            }

            if (!string.IsNullOrWhiteSpace(workspaceBlock))
            {
                sb.AppendLine(workspaceBlock);
                sb.AppendLine();
            }

            sb.AppendLine("CURRENT USER REQUEST — DO THIS NOW:");
            if (!string.IsNullOrWhiteSpace(requestText))
            {
                sb.AppendLine(requestText);
            }
            else
            {
                sb.AppendLine("(User sent a photo with no caption. Inspect the image and fix/report the issue shown.)");
            }

            if (hasPhoto)
            {
                sb.AppendLine();
                sb.AppendLine($"Attached screenshot/image file (open and inspect it): {photoPath}");
            }

            sb.AppendLine();
            sb.AppendLine("Rules:");
            sb.AppendLine("- You are the coding agent for this GitDeploy project chat.");
            sb.AppendLine("- Do NOT introduce yourself. Do NOT ask what to do. Do NOT say you are ready.");
            sb.AppendLine("- Investigate/fix quickly, then reply briefly with the result.");
            sb.AppendLine("- Telegram reply format (required when the answer has two parts):");
            sb.AppendLine("  1) Short understanding / what you checked (optional).");
            sb.AppendLine("  2) A separator line exactly: ────────");
            sb.AppendLine("  3) Work result only (what changed / what to test).");
            sb.AppendLine("  Do not mash mid-work narration into the result. Prefer result-only if short.");
            sb.AppendLine("- Reply in the same language the user used (Persian or English).");
            sb.AppendLine("- This reply is delivered on Telegram. Prefer clear spacing, emoji where helpful, and Telegram HTML for emphasis:");
            sb.AppendLine("  use <b>bold</b>, <i>italic</i>, <code>inline</code>, <pre>blocks</pre>. Avoid Markdown **stars**.");
            sb.AppendLine("- When the user asks to send/attach an .md/.txt file on Telegram: put ONE line exactly");
            sb.AppendLine("  [[TG_DOC:C:\\\\full\\\\path\\\\file.md]] (real existing path). The bridge strips the marker and sendDocuments it.");
            sb.AppendLine("  Do NOT only paste the path or file body — without [[TG_DOC:...]] no attachment is sent.");
            sb.AppendLine($"- Project: {projectName} @ {projectPath}");
            sb.AppendLine("- Always obey PROJECT RULES and stay inside the listed workspace roots.");

            if (_store.IsCursorPlanMode(projectPath))
            {
                sb.AppendLine("- PLAN MODE (required): finish by calling create_plan so GitDeploy saves the plan under .cursor/plans/ as a dated .md.");
                sb.AppendLine("- Do NOT write plan files under docs/ or elsewhere — only create_plan (GitDeploy stores them in .cursor/plans/, gitignored).");
                sb.AppendLine("- The saved plan must be complete enough to Build later.");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Keep Telegram active path + LastProjectPath aligned with the thread running this turn.
        /// </summary>
        private void EnsureProjectSynced(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || TelegramPaths.IsUnassigned(projectPath))
            {
                return;
            }

            string full;
            try
            {
                full = Path.GetFullPath(projectPath.Trim());
            }
            catch
            {
                return;
            }

            if (!Directory.Exists(full))
            {
                return;
            }

            var active = _store.GetActiveProjectPath();
            if (!string.Equals(active, full, StringComparison.OrdinalIgnoreCase))
            {
                _store.SetActiveProjectPath(full);
            }

            var last = _config.LoadGlobalConfig().LastProjectPath;
            if (!string.Equals(last, full, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _config.AddRecentProject(full);
                }
                catch
                {
                }
            }
        }

        private async Task<AgentRunResult> RunAgentProcessAsync(
            string agentPath,
            string workspace,
            string prompt,
            string? model,
            string? resumeSessionId,
            string? mode,
            Action<string>? onProgress,
            Action<string>? onSessionId,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                WorkingDirectory = workspace,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            if (TryResolveNodeEntry(agentPath, out var nodeExe, out var indexJs))
            {
                startInfo.FileName = nodeExe;
                startInfo.ArgumentList.Add(indexJs);
            }
            else
            {
                startInfo.FileName = agentPath;
            }

            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add("--force");
            startInfo.ArgumentList.Add("--trust");
            startInfo.ArgumentList.Add("--workspace");
            startInfo.ArgumentList.Add(workspace);

            if (string.Equals((mode ?? string.Empty).Trim(), "plan", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.ArgumentList.Add("--mode=plan");
            }

            var extras = CursorWorkspaceRoots.GetRoots(workspace).Extras;
            foreach (var extra in extras)
            {
                startInfo.ArgumentList.Add("--add-dir");
                startInfo.ArgumentList.Add(extra);
            }

            // Docs: https://cursor.com/docs/cli/reference/output-format
            startInfo.ArgumentList.Add("--output-format");
            startInfo.ArgumentList.Add("stream-json");
            startInfo.ArgumentList.Add("--stream-partial-output");

            if (!string.IsNullOrWhiteSpace(resumeSessionId))
            {
                startInfo.ArgumentList.Add("--resume");
                startInfo.ArgumentList.Add(resumeSessionId.Trim());
            }

            if (!string.IsNullOrWhiteSpace(model))
            {
                startInfo.ArgumentList.Add("--model");
                startInfo.ArgumentList.Add(model.Trim());
            }

            // Pass prompt directly (ArgumentList keeps Persian intact). Avoids an extra "read temp file" tool hop.
            try
            {
                var lastPromptPath = Path.Combine(Path.GetTempPath(), "GitDeployPro-cursor-last-prompt.txt");
                await File.WriteAllTextAsync(lastPromptPath, prompt, new UTF8Encoding(false), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
            }

            startInfo.ArgumentList.Add(prompt);

            var logOut = Path.Combine(Path.GetTempPath(), "GitDeployPro-cursor-last-stdout.txt");
            var logErr = Path.Combine(Path.GetTempPath(), "GitDeployPro-cursor-last-stderr.txt");

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var stdoutLog = new StringBuilder();
            var stderr = new StringBuilder();
            var finalResult = new StringBuilder();
            var assistantSegments = new StringBuilder();
            var sessionId = resumeSessionId ?? string.Empty;
            AgentTokenUsage? capturedUsage = null;
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var lastEventUtc = DateTime.UtcNow;
            var progressGate = new object();
            var lastProgressUtc = DateTime.MinValue;
            var thinkingBuf = new StringBuilder();
            var lastThinkingFlushUtc = DateTime.MinValue;

            void EmitProgress(string line, bool force = false)
            {
                if (string.IsNullOrWhiteSpace(line) || onProgress == null)
                {
                    return;
                }

                lock (progressGate)
                {
                    if (!force && (DateTime.UtcNow - lastProgressUtc).TotalSeconds < 2.5)
                    {
                        return;
                    }

                    lastProgressUtc = DateTime.UtcNow;
                }

                try
                {
                    onProgress(line);
                }
                catch
                {
                }
            }

            void FlushThinking(bool force)
            {
                string chunk;
                lock (progressGate)
                {
                    if (thinkingBuf.Length == 0)
                    {
                        return;
                    }

                    if (!force && (DateTime.UtcNow - lastThinkingFlushUtc).TotalSeconds < 3)
                    {
                        return;
                    }

                    chunk = thinkingBuf.ToString().Trim();
                    thinkingBuf.Clear();
                    lastThinkingFlushUtc = DateTime.UtcNow;
                }

                if (string.IsNullOrWhiteSpace(chunk))
                {
                    return;
                }

                if (chunk.Length > 160)
                {
                    chunk = chunk[..157] + "…";
                }

                EmitProgress(Loc.T("cursor.progress.thinking", chunk), force: true);
            }

            void HandleStdoutLine(string line)
            {
                stdoutLog.AppendLine(line);
                lastEventUtc = DateTime.UtcNow;
                if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
                {
                    return;
                }

                try
                {
                    var jo = JObject.Parse(line);
                    var sid = jo["session_id"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(sid))
                    {
                        if (!string.Equals(sessionId, sid, StringComparison.Ordinal))
                        {
                            sessionId = sid;
                            try
                            {
                                onSessionId?.Invoke(sid);
                            }
                            catch
                            {
                            }
                        }
                    }

                    var type = jo["type"]?.ToString() ?? string.Empty;
                    switch (type)
                    {
                        case "system" when string.Equals(jo["subtype"]?.ToString(), "init", StringComparison.OrdinalIgnoreCase):
                        {
                            var modelName = jo["model"]?.ToString();
                            EmitProgress(string.IsNullOrWhiteSpace(modelName)
                                ? Loc.T("cursor.progress.init")
                                : Loc.T("cursor.progress.initModel", modelName), force: true);
                            break;
                        }
                        case "thinking":
                        {
                            var subtype = jo["subtype"]?.ToString() ?? string.Empty;
                            if (string.Equals(subtype, "completed", StringComparison.OrdinalIgnoreCase))
                            {
                                FlushThinking(force: true);
                                break;
                            }

                            var think = jo["text"]?.ToString() ?? string.Empty;
                            if (string.IsNullOrEmpty(think))
                            {
                                break;
                            }

                            lock (progressGate)
                            {
                                thinkingBuf.Append(think);
                            }

                            FlushThinking(force: false);
                            break;
                        }
                        case "tool_call" when string.Equals(jo["subtype"]?.ToString(), "started", StringComparison.OrdinalIgnoreCase):
                        {
                            FlushThinking(force: true);
                            // Mid-turn assistant narration must not mash into the final Telegram reply.
                            string pendingAssist;
                            lock (progressGate)
                            {
                                pendingAssist = assistantSegments.ToString().Trim();
                                if (pendingAssist.Length >= 12)
                                {
                                    assistantSegments.Clear();
                                }
                                else
                                {
                                    pendingAssist = string.Empty;
                                }
                            }

                            if (!string.IsNullOrWhiteSpace(pendingAssist))
                            {
                                var chunk = pendingAssist.Length > 280
                                    ? pendingAssist[..277] + "…"
                                    : pendingAssist;
                                EmitProgress("💬 " + chunk, force: true);
                            }

                            var label = DescribeToolCall(jo["tool_call"] as JObject);
                            if (!string.IsNullOrWhiteSpace(label))
                            {
                                EmitProgress(label, force: true);
                            }

                            break;
                        }
                        case "assistant":
                        {
                            // With --stream-partial-output only keep real deltas:
                            // timestamp_ms present + model_call_id absent. Never AppendLine
                            // or Persian tokens become one-character-per-line in Telegram.
                            if (jo["model_call_id"] != null || jo["timestamp_ms"] == null)
                            {
                                break;
                            }

                            var text = ExtractAssistantText(jo);
                            if (string.IsNullOrEmpty(text))
                            {
                                break;
                            }

                            assistantSegments.Append(text);
                            break;
                        }
                        case "result":
                        {
                            FlushThinking(force: true);
                            var resultText = jo["result"]?.ToString();
                            if (!string.IsNullOrWhiteSpace(resultText))
                            {
                                finalResult.Clear();
                                finalResult.Append(resultText.Trim());
                            }

                            var usage = AgentTokenUsage.FromJToken(jo["usage"], "cli");
                            if (usage.Available)
                            {
                                capturedUsage = usage;
                            }

                            break;
                        }
                    }
                }
                catch
                {
                }
            }

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    HandleStdoutLine(e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    stderr.AppendLine(e.Data);
                }
            };
            process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);

            if (!process.Start())
            {
                throw new InvalidOperationException(Loc.T("cursor.startFailed"));
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await using (cancellationToken.Register(() =>
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
                       }

                       tcs.TrySetCanceled(cancellationToken);
                   }))
            {
                using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var startedUtc = DateTime.UtcNow;
                var lastHeartbeatEmitUtc = DateTime.MinValue;
                var heartbeat = Task.Run(async () =>
                {
                    while (!heartbeatCts.IsCancellationRequested)
                    {
                        var quiet = false;
                        try
                        {
                            quiet = new ConfigurationService().LoadGlobalConfig().TelegramQuietProgress;
                        }
                        catch
                        {
                        }

                        var delaySec = quiet ? 30 : 90;
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(delaySec), heartbeatCts.Token).ConfigureAwait(false);
                        }
                        catch
                        {
                            return;
                        }

                        if (process.HasExited)
                        {
                            return;
                        }

                        if (!quiet)
                        {
                            var silentFor = (DateTime.UtcNow - lastEventUtc).TotalSeconds;
                            if (silentFor < 75)
                            {
                                continue;
                            }

                            // At most one heartbeat every 2 minutes (detailed mode).
                            if ((DateTime.UtcNow - lastHeartbeatEmitUtc).TotalSeconds < 120)
                            {
                                continue;
                            }

                            lastHeartbeatEmitUtc = DateTime.UtcNow;
                            var minutes = Math.Max(1, (int)Math.Round((DateTime.UtcNow - startedUtc).TotalMinutes));
                            EmitProgress(Loc.T("cursor.progress.stillWorkingMins", minutes));
                            continue;
                        }

                        // Quiet mode: ping every ~30s even while tools are busy.
                        if ((DateTime.UtcNow - lastHeartbeatEmitUtc).TotalSeconds < 28)
                        {
                            continue;
                        }

                        lastHeartbeatEmitUtc = DateTime.UtcNow;
                        EmitProgress(Loc.T("cursor.progress.stillWorking"));
                    }
                }, heartbeatCts.Token);

                var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromMinutes(8), cancellationToken))
                    .ConfigureAwait(false);
                heartbeatCts.Cancel();
                try
                {
                    await heartbeat.ConfigureAwait(false);
                }
                catch
                {
                }

                if (completed != tcs.Task)
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
                    }

                    throw new TimeoutException(Loc.T("cursor.help.testTimeout"));
                }

                var exit = await tcs.Task.ConfigureAwait(false);
                FlushThinking(force: true);

                try
                {
                    await File.WriteAllTextAsync(logOut, stdoutLog.ToString(), Encoding.UTF8, CancellationToken.None)
                        .ConfigureAwait(false);
                    await File.WriteAllTextAsync(logErr, stderr.ToString(), Encoding.UTF8, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                }

                var textOut = finalResult.ToString().Trim();
                if (string.IsNullOrWhiteSpace(textOut))
                {
                    textOut = assistantSegments.ToString().Trim();
                }

                if (string.IsNullOrWhiteSpace(textOut))
                {
                    textOut = stderr.ToString().Trim();
                }

                if (exit != 0 && string.IsNullOrWhiteSpace(textOut))
                {
                    throw new InvalidOperationException(Loc.T("cursor.exitCode", exit));
                }

                if (exit != 0 && !string.IsNullOrWhiteSpace(stderr.ToString()) && string.IsNullOrWhiteSpace(finalResult.ToString()))
                {
                    textOut = string.IsNullOrWhiteSpace(textOut)
                        ? stderr.ToString().Trim()
                        : textOut + Environment.NewLine + stderr.ToString().Trim();
                }

                return new AgentRunResult
                {
                    Output = textOut,
                    SessionId = sessionId,
                    ExitCode = exit,
                    Source = "cli",
                    Usage = capturedUsage
                };
            }
        }

        private static string ExtractAssistantText(JObject jo)
        {
            var content = jo["message"]?["content"] as JArray;
            if (content == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            foreach (var part in content.OfType<JObject>())
            {
                var t = part["text"]?.ToString();
                if (!string.IsNullOrWhiteSpace(t))
                {
                    if (sb.Length > 0)
                    {
                        sb.Append(' ');
                    }

                    sb.Append(t.Trim());
                }
            }

            return sb.ToString();
        }

        private static string DescribeToolCall(JObject? toolCall)
        {
            if (toolCall == null)
            {
                return Loc.T("cursor.progress.toolGeneric");
            }

            if (toolCall["readToolCall"] is JObject read)
            {
                var path = read["args"]?["path"]?.ToString() ?? "?";
                return Loc.T("cursor.progress.read", Path.GetFileName(path));
            }

            if (toolCall["writeToolCall"] is JObject write)
            {
                var path = write["args"]?["path"]?.ToString() ?? "?";
                return Loc.T("cursor.progress.write", Path.GetFileName(path));
            }

            if (toolCall["editToolCall"] is JObject edit)
            {
                var path = edit["args"]?["path"]?.ToString()
                           ?? edit["args"]?["file_path"]?.ToString()
                           ?? "?";
                return Loc.T("cursor.progress.edit", Path.GetFileName(path));
            }

            if (toolCall["grepToolCall"] is JObject grep)
            {
                var pattern = grep["args"]?["pattern"]?.ToString() ?? "?";
                if (pattern.Length > 40)
                {
                    pattern = pattern[..37] + "…";
                }

                return Loc.T("cursor.progress.grep", pattern);
            }

            if (toolCall["shellToolCall"] is JObject shell)
            {
                var cmd = shell["args"]?["command"]?.ToString()
                          ?? shell["args"]?["cmd"]?.ToString()
                          ?? "?";
                if (cmd.Length > 60)
                {
                    cmd = cmd[..57] + "…";
                }

                return Loc.T("cursor.progress.shell", cmd);
            }

            if (toolCall["globToolCall"] is JObject glob)
            {
                var pattern = glob["args"]?["globPattern"]?.ToString()
                              ?? glob["args"]?["pattern"]?.ToString()
                              ?? "?";
                return Loc.T("cursor.progress.glob", pattern);
            }

            if (toolCall["lsToolCall"] is JObject ls)
            {
                var path = ls["args"]?["path"]?.ToString() ?? ".";
                return Loc.T("cursor.progress.ls", path);
            }

            if (toolCall["function"] is JObject fn)
            {
                var name = fn["name"]?.ToString() ?? "tool";
                return Loc.T("cursor.progress.toolNamed", name);
            }

            var first = toolCall.Properties().FirstOrDefault()?.Name;
            return string.IsNullOrWhiteSpace(first)
                ? Loc.T("cursor.progress.toolGeneric")
                : Loc.T("cursor.progress.toolNamed", first);
        }

        private bool ResolveNodeEntryCached(string agentPath, out string? nodeExe, out string? indexJs)
        {
            lock (_pathCacheGate)
            {
                if (!string.IsNullOrWhiteSpace(_cachedNodeExe)
                    && !string.IsNullOrWhiteSpace(_cachedIndexJs)
                    && string.Equals(_cachedAgentPath, agentPath, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(_cachedNodeExe)
                    && File.Exists(_cachedIndexJs))
                {
                    nodeExe = _cachedNodeExe;
                    indexJs = _cachedIndexJs;
                    return true;
                }
            }

            if (!TryResolveNodeEntry(agentPath, out var resolvedNode, out var resolvedIndex))
            {
                nodeExe = null;
                indexJs = null;
                return false;
            }

            lock (_pathCacheGate)
            {
                _cachedAgentPath = agentPath;
                _cachedNodeExe = resolvedNode;
                _cachedIndexJs = resolvedIndex;
                _pathCacheUtc = DateTime.UtcNow;
            }

            nodeExe = resolvedNode;
            indexJs = resolvedIndex;
            return true;
        }

        private static bool TryResolveNodeEntry(string agentPath, out string nodeExe, out string indexJs)
        {
            nodeExe = string.Empty;
            indexJs = string.Empty;
            try
            {
                var root = Path.GetDirectoryName(agentPath);
                if (string.IsNullOrWhiteSpace(root))
                {
                    return false;
                }

                // agent.cmd lives in %LocalAppData%\cursor-agent\
                var versionsRoot = Path.Combine(root, "versions");
                if (!Directory.Exists(versionsRoot))
                {
                    // Maybe configured path pointed at a version folder already.
                    versionsRoot = root;
                }

                var versionDir = Directory.GetDirectories(versionsRoot)
                    .Select(d => new DirectoryInfo(d))
                    .Where(d => File.Exists(Path.Combine(d.FullName, "node.exe"))
                                && File.Exists(Path.Combine(d.FullName, "index.js")))
                    .OrderByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                if (versionDir == null)
                {
                    var directNode = Path.Combine(root, "node.exe");
                    var directIndex = Path.Combine(root, "index.js");
                    if (File.Exists(directNode) && File.Exists(directIndex))
                    {
                        nodeExe = directNode;
                        indexJs = directIndex;
                        return true;
                    }

                    return false;
                }

                nodeExe = Path.Combine(versionDir.FullName, "node.exe");
                indexJs = Path.Combine(versionDir.FullName, "index.js");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<string?> PublishAgentReplyAsync(
            string projectPath,
            string reply,
            CancellationToken cancellationToken,
            string? preferredPlanFilePath = null)
        {
            var message = new TelegramChatMessage
            {
                Direction = TelegramMessageDirection.Outgoing,
                Status = TelegramMessageStatus.Sent,
                Text = reply,
                Utc = DateTime.UtcNow,
                SenderName = "Cursor"
            };

            _store.Append(projectPath, message);
            TelegramPoller.Instance.RaiseMessage(projectPath, message);

            var config = _config.LoadGlobalConfig();
            var token = EncryptionService.Decrypt(config.TelegramBotToken);
            var chatId = _store.GetLastChatId();
            if (string.IsNullOrWhiteSpace(token) || chatId == 0)
            {
                return null;
            }

            string? planPendingId = null;
            // Local chat is already updated — Telegram delivery is fully async/queued.
            try
            {
                const int maxLen = 3500;
                var chunks = ChunkText(reply, maxLen).ToList();
                JToken? markup = TelegramDeployCoordinator.BuildReplyKeyboard(projectPath);

                // Only show Get MD / Build when THIS turn produced a real plan file.
                if (_store.IsCursorPlanMode(projectPath)
                    && !string.IsNullOrWhiteSpace(preferredPlanFilePath)
                    && File.Exists(preferredPlanFilePath)
                    && CursorPlanCatalog.IsUnderPlansDir(projectPath, preferredPlanFilePath))
                {
                    planPendingId = TelegramPlanMdBroker.Instance.Register(
                        projectPath,
                        preferredPlanFilePath);
                    if (!string.IsNullOrWhiteSpace(planPendingId))
                    {
                        markup = TelegramPlanMdBroker.BuildCardKeyboard(planPendingId);
                    }
                }

                for (var i = 0; i < chunks.Count; i++)
                {
                    var isLast = i == chunks.Count - 1;
                    var html = TelegramTextFormat.ToTelegramHtml(chunks[i]);
                    TelegramOutboundQueue.Instance.EnqueueText(
                        token,
                        chatId,
                        projectPath,
                        html,
                        isLast ? markup : null,
                        parseMode: "HTML");
                }
            }
            catch
            {
                // Local chat already has the reply.
            }

            await Task.CompletedTask.ConfigureAwait(false);
            return planPendingId;
        }

        private async Task PublishPlanDocumentAsync(
            string projectPath,
            string planFilePath,
            CancellationToken cancellationToken,
            string? buildPendingId = null)
        {
            var config = _config.LoadGlobalConfig();
            var token = EncryptionService.Decrypt(config.TelegramBotToken);
            var chatId = _store.GetLastChatId();
            if (string.IsNullOrWhiteSpace(token) || chatId == 0 || !File.Exists(planFilePath))
            {
                return;
            }

            // Document may arrive without a prior plan-mode card — still offer Build for that file.
            var pendingId = buildPendingId;
            if (string.IsNullOrWhiteSpace(pendingId)
                && CursorPlanCatalog.IsUnderPlansDir(projectPath, planFilePath))
            {
                pendingId = TelegramPlanMdBroker.Instance.Register(projectPath, planFilePath);
            }

            JToken? markup = null;
            if (!string.IsNullOrWhiteSpace(pendingId)
                && CursorPlanCatalog.IsUnderPlansDir(projectPath, planFilePath))
            {
                markup = TelegramPlanMdBroker.BuildDocumentKeyboard(pendingId);
            }

            var caption = Loc.T("cursor.planDocumentCaption", Path.GetFileName(planFilePath));
            TelegramOutboundQueue.Instance.EnqueueDocument(
                token,
                chatId,
                projectPath,
                planFilePath,
                caption,
                markup);

            var note = new TelegramChatMessage
            {
                Direction = TelegramMessageDirection.System,
                Status = TelegramMessageStatus.Sent,
                Text = Loc.T("cursor.planSentTelegram", Path.GetFileName(planFilePath)),
                Utc = DateTime.UtcNow,
                SenderName = "Cursor",
                AttachmentPath = planFilePath,
                AttachmentName = Path.GetFileName(planFilePath)
            };
            _store.Append(projectPath, note);
            TelegramPoller.Instance.RaiseMessage(projectPath, note);
            await Task.CompletedTask.ConfigureAwait(false);
        }

        private static bool LooksLikePlanFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var normalized = path.Replace('/', '\\');
            return normalized.Contains("\\.cursor\\plans\\", StringComparison.OrdinalIgnoreCase)
                   && normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatUsageFooter(AgentTokenUsage usage)
        {
            var cache = usage.CacheReadTokens + usage.CacheWriteTokens;
            if (cache > 0)
            {
                return Loc.T(
                    "cursor.usageFooterCache",
                    usage.InputTokens,
                    usage.OutputTokens,
                    usage.CacheReadTokens,
                    usage.CacheWriteTokens);
            }

            return Loc.T("cursor.usageFooter", usage.InputTokens, usage.OutputTokens);
        }

        private void PostProgress(string projectPath, string text)
        {
            // Keep progress as a single readable line (no vertical wrapping artifacts).
            var clean = (text ?? string.Empty)
                .Replace("\r\n", " ")
                .Replace('\n', ' ')
                .Replace('\r', ' ')
                .Trim();
            clean = System.Text.RegularExpressions.Regex.Replace(clean, "\\s{2,}", " ");
            if (string.IsNullOrWhiteSpace(clean))
            {
                return;
            }

            var isHeartbeat = clean.StartsWith("⏳", StringComparison.Ordinal)
                              && (clean.Contains("Still working", StringComparison.OrdinalIgnoreCase)
                                  || clean.Contains("هنوز", StringComparison.Ordinal)
                                  || clean.Contains("در حال کار", StringComparison.Ordinal)
                                  || clean.Contains("working", StringComparison.OrdinalIgnoreCase));

            var quietTelegram = false;
            try
            {
                quietTelegram = _config.LoadGlobalConfig().TelegramQuietProgress;
            }
            catch
            {
            }

            // Heartbeats: Telegram only (don't flood the in-app thread).
            // Quiet mode: keep tool/thinking in the local app chat, but not on Telegram.
            if (!isHeartbeat)
            {
                PostStatus(projectPath, clean);
            }

            var isThinking = clean.StartsWith("🧠", StringComparison.Ordinal);

            if (quietTelegram)
            {
                if (!isHeartbeat)
                {
                    return;
                }
            }
            else
            {
                // Telegram: tools + thinking + init/photo; heartbeats throttled separately.
                var toTelegram = clean.StartsWith("📖", StringComparison.Ordinal)
                                 || clean.StartsWith("✍️", StringComparison.Ordinal)
                                 || clean.StartsWith("✏️", StringComparison.Ordinal)
                                 || clean.StartsWith("⌨️", StringComparison.Ordinal)
                                 || clean.StartsWith("🔎", StringComparison.Ordinal)
                                 || clean.StartsWith("📂", StringComparison.Ordinal)
                                 || clean.StartsWith("🔧", StringComparison.Ordinal)
                                 || clean.StartsWith("♻️", StringComparison.Ordinal)
                                 || clean.StartsWith("📷", StringComparison.Ordinal)
                                 || isThinking
                                 || isHeartbeat
                                 || (clean.StartsWith("⏳", StringComparison.Ordinal) && !isHeartbeat);
                if (!toTelegram)
                {
                    return;
                }
            }

            // Thinking and tools use separate gates so tool spam does not starve thoughts.
            var minGapSeconds = quietTelegram && isHeartbeat
                ? 28
                : isHeartbeat ? 90 : isThinking ? 6 : 5;
            bool sendTelegram;
            lock (_telegramProgressGate)
            {
                var last = isThinking ? _lastTelegramThinkingUtc : _lastTelegramProgressUtc;
                sendTelegram = (DateTime.UtcNow - last).TotalSeconds >= minGapSeconds;
                if (sendTelegram)
                {
                    if (isThinking)
                    {
                        _lastTelegramThinkingUtc = DateTime.UtcNow;
                    }
                    else
                    {
                        _lastTelegramProgressUtc = DateTime.UtcNow;
                    }
                }
            }

            if (!sendTelegram)
            {
                return;
            }

            try
            {
                var config = _config.LoadGlobalConfig();
                var token = EncryptionService.Decrypt(config.TelegramBotToken);
                var chatId = _store.GetLastChatId();
                if (string.IsNullOrWhiteSpace(token) || chatId == 0)
                {
                    return;
                }

                TelegramOutboundQueue.Instance.EnqueueText(
                    token,
                    chatId,
                    projectPath,
                    clean,
                    BuildStopInlineMarkup());
            }
            catch
            {
            }
        }

        private void NotifyTelegramWorking(string projectPath, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            try
            {
                var config = _config.LoadGlobalConfig();
                var token = EncryptionService.Decrypt(config.TelegramBotToken);
                var chatId = _store.GetLastChatId();
                if (string.IsNullOrWhiteSpace(token) || chatId == 0)
                {
                    return;
                }

                TelegramOutboundQueue.Instance.EnqueueText(
                    token,
                    chatId,
                    projectPath,
                    text,
                    BuildStopInlineMarkup());
            }
            catch
            {
            }
        }

        private void PostStatus(string projectPath, string text)
        {
            var message = new TelegramChatMessage
            {
                Direction = TelegramMessageDirection.System,
                Status = TelegramMessageStatus.Sent,
                Text = text,
                Utc = DateTime.UtcNow,
                SenderName = "Cursor"
            };

            var thread = _store.LoadThread(projectPath);
            if (string.IsNullOrWhiteSpace(message.Id))
            {
                message.Id = Guid.NewGuid().ToString("N");
            }

            thread.Messages.Add(message);
            _store.SaveThread(thread);
            TelegramPoller.Instance.RaiseMessage(projectPath, message);
        }

        private static IEnumerable<string> ChunkText(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text))
            {
                yield break;
            }

            for (var i = 0; i < text.Length; i += maxLen)
            {
                yield return text.Substring(i, Math.Min(maxLen, text.Length - i));
            }
        }

        private static string TrimReply(string output)
        {
            var text = (output ?? string.Empty).Trim();
            // Repair vertically-broken stream tokens: "عکس\nو\nکد" → "عکس و کد"
            if (text.Contains('\n'))
            {
                var lines = text.Replace("\r\n", "\n").Replace('\r', '\n')
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var shortLines = 0;
                foreach (var line in lines)
                {
                    if (line.Length <= 12)
                    {
                        shortLines++;
                    }
                }

                if (lines.Length >= 3 && shortLines >= lines.Length * 0.6)
                {
                    text = string.Join(" ", lines);
                }
                else
                {
                    text = string.Join("\n", lines);
                }
            }

            text = System.Text.RegularExpressions.Regex.Replace(text, "[ \\t]{2,}", " ").Trim();
            text = TelegramAgentReplyFormatter.Format(text);
            if (text.Length > 12000)
            {
                text = text[..11900] + "…";
            }

            return text;
        }

        private static string? FindOnPath(string fileName)
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var full = Path.Combine(dir.Trim().Trim('"'), fileName);
                    if (File.Exists(full))
                    {
                        return full;
                    }

                    var withCmd = full + ".cmd";
                    if (File.Exists(withCmd))
                    {
                        return withCmd;
                    }

                    var withExe = full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? full
                        : full + ".exe";
                    if (File.Exists(withExe))
                    {
                        return withExe;
                    }

                    var withPs1 = full + ".ps1";
                    if (File.Exists(withPs1))
                    {
                        return withPs1;
                    }
                }
                catch
                {
                }
            }

            return null;
        }
    }

    public sealed class CursorAgentTestResult
    {
        public bool Ok { get; init; }
        public bool NeedsLogin { get; init; }
        public string Message { get; init; } = "";
        public string AgentPath { get; init; } = "";
        public string Workspace { get; init; } = "";
        public string Account { get; init; } = "";
        public string OutputSnippet { get; init; } = "";
    }

    public sealed class CursorAgentAuthStatus
    {
        public bool LoggedIn { get; init; }
        public string Account { get; init; } = "";
        public string AgentPath { get; init; } = "";
        public string Message { get; init; } = "";
    }

    public sealed class AgentRunResult
    {
        public string Output { get; init; } = "";
        public string SessionId { get; init; } = "";
        public int ExitCode { get; init; }
        public string Source { get; init; } = "";
        public AgentTokenUsage? Usage { get; init; }
        /// <summary>Plan markdown file written during this turn (ACP create_plan), if any.</summary>
        public string? PlanFilePath { get; init; }
    }

    public sealed class AgentTokenUsage
    {
        public bool Available { get; init; }
        public long InputTokens { get; init; }
        public long OutputTokens { get; init; }
        public long CacheReadTokens { get; init; }
        public long CacheWriteTokens { get; init; }
        public string Source { get; init; } = "";

        public static AgentTokenUsage Unavailable(string source = "")
            => new() { Available = false, Source = source ?? string.Empty };

        public static AgentTokenUsage FromJToken(JToken? usage, string source)
        {
            if (usage == null || usage.Type == JTokenType.Null)
            {
                return Unavailable(source);
            }

            long Read(params string[] keys)
            {
                foreach (var key in keys)
                {
                    var v = usage[key];
                    if (v != null && v.Type != JTokenType.Null && long.TryParse(v.ToString(), out var n))
                    {
                        return n;
                    }
                }

                return 0;
            }

            var input = Read("inputTokens", "input_tokens", "promptTokens", "prompt_tokens");
            var output = Read("outputTokens", "output_tokens", "completionTokens", "completion_tokens");
            var cacheRead = Read("cacheReadTokens", "cache_read_tokens", "cacheRead", "cache_read");
            var cacheWrite = Read("cacheWriteTokens", "cache_write_tokens", "cacheWrite", "cache_write");
            if (input == 0 && output == 0 && cacheRead == 0 && cacheWrite == 0)
            {
                // Some payloads nest under "totalTokens" only — still mark unavailable if empty.
                var total = Read("totalTokens", "total_tokens");
                if (total == 0)
                {
                    return Unavailable(source);
                }

                input = total;
            }

            return new AgentTokenUsage
            {
                Available = true,
                InputTokens = input,
                OutputTokens = output,
                CacheReadTokens = cacheRead,
                CacheWriteTokens = cacheWrite,
                Source = source ?? string.Empty
            };
        }
    }

    internal readonly record struct PendingTurn(string ProjectPath, string Text, string? PhotoPath);

    public sealed class CursorAgentStatusInfo
    {
        public bool Enabled { get; init; }
        public bool DaemonAlive { get; init; }
        public string Model { get; init; } = "";
        public int QueueDepth { get; init; }
        public bool TurnBusy { get; init; }
        public DateTime? LastActivityUtc { get; init; }
        public string SessionHint { get; init; } = "";
        public string AgentMode { get; init; } = "agent";
        public AgentTokenUsage? LastUsage { get; init; }
        public AgentTokenUsage? SessionUsage { get; init; }
    }
}
