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

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// Runs Claude Code CLI (<c>claude -p</c>) turns with a per-project queue, mirroring replies to Telegram.
    /// </summary>
    public sealed class ClaudeAgentBridge : IAgentDriver
    {
        public static ClaudeAgentBridge Instance { get; } = new();

        public AgentEngineKind Kind => AgentEngineKind.Claude;

        public string DisplayName => "Claude";

        private readonly ConcurrentDictionary<string, byte> _workers = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ConcurrentQueue<PendingTurn>> _queues =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _turnCts =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly TelegramChatStore _store = TelegramChatStore.Instance;
        private readonly ConfigurationService _config = new();
        private readonly ClaudeCliRunner _runner = new();
        private readonly object _pathCacheGate = new();
        private string? _cachedConfiguredPath;
        private string? _cachedClaudePath;
        private DateTime _pathCacheUtc = DateTime.MinValue;
        private readonly object _telegramProgressGate = new();
        private DateTime _lastTelegramProgressUtc = DateTime.MinValue;

        private ClaudeAgentBridge()
        {
        }

        public void EnqueueUserTurn(string projectPath, string text, string? photoPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || TelegramPaths.IsUnassigned(projectPath))
            {
                return;
            }

            var config = _config.LoadGlobalConfig();
            if (!config.ClaudeAgentEnabled)
            {
                PostStatus(projectPath, Loc.T("claude.disabled"));
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
                var notice = Loc.T("cursor.queued", queue.Count);
                PostStatus(projectPath, notice);
                NotifyTelegramWorking(projectPath, notice);
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
                                PostStatus(turn.ProjectPath, Loc.T("claude.failed", ex.Message));
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

        public void PrewarmForProject(string projectPath)
        {
            // One-shot CLI: nothing to keep warm. Just refresh the executable lookup.
            if (string.IsNullOrWhiteSpace(projectPath) || TelegramPaths.IsUnassigned(projectPath))
            {
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    ResolveClaudeExecutable(_config.LoadGlobalConfig().ClaudeCliPath);
                }
                catch
                {
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

        public bool StopTurn(string projectPath)
        {
            var key = TelegramPaths.ToProjectKey(projectPath);
            if (_queues.TryGetValue(key, out var queue))
            {
                while (queue.TryDequeue(out _))
                {
                }
            }

            return CancelCurrentTurn(projectPath);
        }

        public void RestartProjectAgent(string projectPath, string? reason = null)
        {
            StopTurn(projectPath);
            _store.ClearClaudeSession(projectPath);
            PostStatus(
                projectPath,
                string.IsNullOrWhiteSpace(reason)
                    ? Loc.T("claude.restarted")
                    : Loc.T("claude.restartedReason", reason));
            PrewarmForProject(projectPath);
        }

        public void SetModelAndRestart(string projectPath, string? modelId)
        {
            var model = (modelId ?? string.Empty).Trim();
            if (string.Equals(model, "auto", StringComparison.OrdinalIgnoreCase)
                || string.Equals(model, "default", StringComparison.OrdinalIgnoreCase))
            {
                model = string.Empty;
            }

            _config.UpdateGlobalConfig(cfg => cfg.ClaudeAgentModel = model);
            RestartProjectAgent(
                projectPath,
                Loc.T("cursor.modelSet", string.IsNullOrWhiteSpace(model) ? "default" : model));
        }

        public CursorAgentStatusInfo GetAgentStatus(string projectPath)
        {
            var config = _config.LoadGlobalConfig();
            var key = TelegramPaths.ToProjectKey(projectPath);
            var queueDepth = _queues.TryGetValue(key, out var q) ? q.Count : 0;
            var session = _store.GetClaudeSessionId(projectPath);

            return new CursorAgentStatusInfo
            {
                Enabled = config.ClaudeAgentEnabled,
                DaemonAlive = !string.IsNullOrWhiteSpace(ResolveClaudeExecutable(config.ClaudeCliPath)),
                Model = string.IsNullOrWhiteSpace(config.ClaudeAgentModel) ? "default" : config.ClaudeAgentModel.Trim(),
                QueueDepth = queueDepth,
                TurnBusy = _workers.ContainsKey(key),
                LastActivityUtc = null,
                SessionHint = string.IsNullOrWhiteSpace(session) ? "none" : TruncateId(session)
            };
        }

        public void InvalidateAfterSettingsSave(string? projectPath = null)
        {
            InvalidatePathCache();
            if (!string.IsNullOrWhiteSpace(projectPath))
            {
                PrewarmForProject(projectPath);
            }
        }

        public void Shutdown()
        {
            foreach (var key in _turnCts.Keys.ToList())
            {
                if (_turnCts.TryGetValue(key, out var cts))
                {
                    try
                    {
                        cts.Cancel();
                    }
                    catch
                    {
                    }
                }
            }
        }

        public void InvalidatePathCache()
        {
            lock (_pathCacheGate)
            {
                _cachedConfiguredPath = null;
                _cachedClaudePath = null;
                _pathCacheUtc = DateTime.MinValue;
            }
        }

        public string? ResolveClaudeExecutable(string? configuredPath = null)
        {
            lock (_pathCacheGate)
            {
                var cfg = (configuredPath ?? string.Empty).Trim().Trim('"');
                if (_pathCacheUtc > DateTime.UtcNow.AddMinutes(-5)
                    && string.Equals(_cachedConfiguredPath, cfg, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(_cachedClaudePath)
                    && File.Exists(_cachedClaudePath))
                {
                    return _cachedClaudePath;
                }

                string? found = null;
                if (!string.IsNullOrWhiteSpace(cfg) && File.Exists(cfg))
                {
                    found = cfg;
                }
                else
                {
                    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    found = FindOnPath("claude.exe")
                            ?? FirstExisting(
                                Path.Combine(home, ".local", "bin", "claude.exe"),
                                Path.Combine(home, ".claude", "local", "claude.exe"))
                            ?? FindOnPath("claude.cmd")
                            ?? FirstExisting(Path.Combine(roaming, "npm", "claude.cmd"));
                }

                _cachedConfiguredPath = cfg;
                _cachedClaudePath = found;
                _pathCacheUtc = DateTime.UtcNow;
                return found;
            }
        }

        private async Task RunTurnAsync(
            string projectPath,
            string text,
            string? photoPath,
            CancellationToken cancellationToken)
        {
            PostStatus(projectPath, Loc.T("claude.started"));
            NotifyTelegramWorking(projectPath, Loc.T("claude.started"));
            var config = _config.LoadGlobalConfig();
            var exe = ResolveClaudeExecutable(config.ClaudeCliPath);
            if (string.IsNullOrWhiteSpace(exe))
            {
                PostStatus(projectPath, Loc.T("claude.agentMissing"));
                return;
            }

            var resume = _store.GetClaudeSessionId(projectPath);
            var model = (config.ClaudeAgentModel ?? string.Empty).Trim();
            var timeout = config.GetClaudeAgentTurnTimeout();
            void OnProgress(string line) => PostProgress(projectPath, line);

            var run = await _runner.RunAsync(
                    exe,
                    projectPath,
                    BuildPrompt(projectPath, text, photoPath, !string.IsNullOrWhiteSpace(resume)),
                    model,
                    string.IsNullOrWhiteSpace(resume) ? null : resume,
                    config.ClaudePermissionMode,
                    timeout,
                    OnProgress,
                    cancellationToken)
                .ConfigureAwait(false);

            // Stale session id (deleted ~/.claude jsonl, other machine) -> drop it and retry fresh once.
            if (run.ExitCode != 0
                && !string.IsNullOrWhiteSpace(resume)
                && LooksLikeResumeFailure(run.Output))
            {
                _store.ClearClaudeSession(projectPath);
                PostStatus(projectPath, Loc.T("claude.resumeRetry"));
                run = await _runner.RunAsync(
                        exe,
                        projectPath,
                        BuildPrompt(projectPath, text, photoPath, resumeSession: false),
                        model,
                        resumeSessionId: null,
                        config.ClaudePermissionMode,
                        timeout,
                        OnProgress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(run.SessionId) && run.ExitCode == 0)
            {
                _store.SetClaudeSessionId(projectPath, run.SessionId);
            }

            string reply;
            if (run.ExitCode != 0 && LooksLikeAuthFailure(run.Output))
            {
                reply = "❌ " + Loc.T("claude.notLoggedIn");
            }
            else if (string.IsNullOrWhiteSpace(run.Output))
            {
                reply = Loc.T("claude.emptyReply");
            }
            else
            {
                reply = TelegramAgentReplyFormatter.Format(run.Output.Trim());
                if (run.ExitCode != 0 && !reply.StartsWith("❌", StringComparison.Ordinal))
                {
                    reply = "❌ " + reply;
                }
            }

            if (run.Usage is { Available: true } usage)
            {
                _store.RecordCursorUsage(projectPath, usage);
            }

            await PublishAgentReplyAsync(projectPath, reply, cancellationToken).ConfigureAwait(false);
        }

        private static bool LooksLikeResumeFailure(string? output)
        {
            var text = output ?? string.Empty;
            return text.Contains("No conversation found", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("session not found", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("No such session", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("Invalid session", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("not a valid session", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeAuthFailure(string? output)
        {
            var text = output ?? string.Empty;
            return text.Contains("Invalid API key", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("Please run /login", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("authentication_error", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("OAuth token has expired", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildPrompt(string projectPath, string text, string? photoPath, bool resumeSession)
        {
            var projectName = TelegramPaths.DisplayName(projectPath);
            var requestText = (text ?? string.Empty).Trim();
            var hasPhoto = !string.IsNullOrWhiteSpace(photoPath) && File.Exists(photoPath);
            var workspaceBlock = CursorWorkspaceRoots.BuildPromptContext(projectPath, resumeSession);

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(workspaceBlock))
            {
                sb.AppendLine(workspaceBlock);
                sb.AppendLine();
            }

            if (resumeSession)
            {
                sb.AppendLine("Continue the same coding session. Do the new user request now. No greeting.");
                sb.AppendLine(string.IsNullOrWhiteSpace(requestText)
                    ? "(Photo with no caption — inspect the image and continue the bug fix.)"
                    : requestText);
                if (hasPhoto)
                {
                    sb.AppendLine($"Image file: {photoPath}");
                }

                return sb.ToString();
            }

            sb.AppendLine("CURRENT USER REQUEST — DO THIS NOW:");
            sb.AppendLine(string.IsNullOrWhiteSpace(requestText)
                ? "(User sent a photo with no caption. Inspect the image and fix/report the issue shown.)"
                : requestText);

            if (hasPhoto)
            {
                sb.AppendLine();
                sb.AppendLine($"Attached screenshot/image file (open and inspect it): {photoPath}");
            }

            sb.AppendLine();
            sb.AppendLine("Rules:");
            sb.AppendLine("- You are the coding agent for this GitDeploy project chat (Claude Code).");
            sb.AppendLine("- Do NOT introduce yourself. Do NOT ask what to do. Do NOT say you are ready.");
            sb.AppendLine("- Investigate/fix quickly inside the listed workspace roots, then reply briefly with the result.");
            sb.AppendLine("- Telegram reply format (required when the answer has two parts):");
            sb.AppendLine("  1) Short understanding / what you checked (optional).");
            sb.AppendLine("  2) A separator line exactly: ────────");
            sb.AppendLine("  3) Work result only (what changed / what to test).");
            sb.AppendLine("  Do not mash mid-work narration into the result. Prefer result-only if short.");
            sb.AppendLine("- Prefer reading/editing files under the Primary root; do not ask the user for paths that are already listed above.");
            sb.AppendLine("- Reply in the same language the user used (Persian or English).");
            sb.AppendLine("- This reply is delivered on Telegram. Prefer clear spacing, emoji where helpful, and Telegram HTML for emphasis:");
            sb.AppendLine("  use <b>bold</b>, <i>italic</i>, <code>inline</code>, <pre>blocks</pre>. Avoid Markdown **stars**.");
            sb.AppendLine($"- Project: {projectName} @ {projectPath}");
            sb.AppendLine("- Always obey PROJECT RULES and stay inside the listed workspace roots.");

            return sb.ToString();
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
                    CursorAgentBridge.BuildStopInlineMarkup());
            }
            catch
            {
            }
        }

        private CancellationTokenSource BeginTurnCts(string key)
        {
            var cts = new CancellationTokenSource();
            _turnCts[key] = cts;
            return cts;
        }

        private Task PublishAgentReplyAsync(
            string projectPath,
            string reply,
            CancellationToken cancellationToken)
        {
            var message = new TelegramChatMessage
            {
                Direction = TelegramMessageDirection.Outgoing,
                Status = TelegramMessageStatus.Sent,
                Text = reply,
                Utc = DateTime.UtcNow,
                SenderName = "Claude"
            };

            _store.Append(projectPath, message);
            TelegramPoller.Instance.RaiseMessage(projectPath, message);

            var config = _config.LoadGlobalConfig();
            var token = EncryptionService.Decrypt(config.TelegramBotToken);
            var chatId = _store.GetLastChatId();
            if (string.IsNullOrWhiteSpace(token) || chatId == 0)
            {
                return Task.CompletedTask;
            }

            try
            {
                const int maxLen = 3500;
                var chunks = ChunkText(reply, maxLen).ToList();
                var markup = TelegramDeployCoordinator.BuildReplyKeyboard(projectPath);
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
            }

            return Task.CompletedTask;
        }

        private void PostProgress(string projectPath, string text)
        {
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

            var isHeartbeat = clean.StartsWith("⏳", StringComparison.Ordinal);

            var quietTelegram = false;
            try
            {
                quietTelegram = _config.LoadGlobalConfig().TelegramQuietProgress;
            }
            catch
            {
            }

            // Heartbeats: Telegram only. Detail lines always stay in the in-app chat.
            if (!isHeartbeat)
            {
                PostStatus(projectPath, clean);
            }

            if (quietTelegram && !isHeartbeat)
            {
                return;
            }

            var minGapSeconds = quietTelegram && isHeartbeat ? 28 : isHeartbeat ? 90 : 2.5;
            lock (_telegramProgressGate)
            {
                if ((DateTime.UtcNow - _lastTelegramProgressUtc).TotalSeconds < minGapSeconds)
                {
                    return;
                }

                _lastTelegramProgressUtc = DateTime.UtcNow;
            }

            try
            {
                var config = _config.LoadGlobalConfig();
                var token = EncryptionService.Decrypt(config.TelegramBotToken);
                var chatId = _store.GetLastChatId();
                if (!string.IsNullOrWhiteSpace(token) && chatId != 0)
                {
                    TelegramOutboundQueue.Instance.EnqueueText(
                        token,
                        chatId,
                        projectPath,
                        TelegramMarkup.Html(clean),
                        isHeartbeat ? CursorAgentBridge.BuildStopInlineMarkup() : null,
                        "HTML");
                }
            }
            catch
            {
            }
        }

        private void PostStatus(string projectPath, string text)
        {
            try
            {
                var msg = new TelegramChatMessage
                {
                    Direction = TelegramMessageDirection.System,
                    Status = TelegramMessageStatus.Sent,
                    Text = text,
                    Utc = DateTime.UtcNow,
                    SenderName = "Claude"
                };
                _store.Append(projectPath, msg);
                TelegramPoller.Instance.RaiseMessage(projectPath, msg);
            }
            catch
            {
            }
        }

        private static IEnumerable<string> ChunkText(string text, int maxLen)
        {
            text ??= string.Empty;
            if (text.Length <= maxLen)
            {
                yield return text;
                yield break;
            }

            for (var i = 0; i < text.Length; i += maxLen)
            {
                yield return text.Substring(i, Math.Min(maxLen, text.Length - i));
            }
        }

        private static string TruncateId(string id)
        {
            id = (id ?? string.Empty).Trim();
            return id.Length <= 10 ? id : id[..8] + "…";
        }

        /// <summary>
        /// Settings "Test": finds the binary and runs <c>claude --version</c>.
        /// Login state is only known after the first real turn (or running <c>claude</c> once in a terminal).
        /// </summary>
        public async Task<ClaudeConnectionTestResult> TestConnectionAsync(
            string? configuredPath = null,
            CancellationToken cancellationToken = default)
        {
            InvalidatePathCache();
            var exe = ResolveClaudeExecutable(configuredPath ?? _config.LoadGlobalConfig().ClaudeCliPath);
            if (string.IsNullOrWhiteSpace(exe))
            {
                return new ClaudeConnectionTestResult
                {
                    Ok = false,
                    Message = Loc.T("claude.agentMissing")
                };
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                psi.ArgumentList.Add("--version");

                using var process = Process.Start(psi);
                if (process == null)
                {
                    return new ClaudeConnectionTestResult { Ok = false, AgentPath = exe, Message = Loc.T("claude.testFail", "start") };
                }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                var version = stdout.Trim();

                return process.ExitCode == 0
                    ? new ClaudeConnectionTestResult
                    {
                        Ok = true,
                        AgentPath = exe,
                        Version = version,
                        Message = Loc.T("claude.testOk", exe, string.IsNullOrWhiteSpace(version) ? "?" : version)
                    }
                    : new ClaudeConnectionTestResult
                    {
                        Ok = false,
                        AgentPath = exe,
                        Version = version,
                        Message = Loc.T("claude.testFail", "exit " + process.ExitCode)
                    };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new ClaudeConnectionTestResult { Ok = false, AgentPath = exe, Message = Loc.T("claude.testFail", "timeout") };
            }
            catch (Exception ex)
            {
                return new ClaudeConnectionTestResult { Ok = false, AgentPath = exe, Message = Loc.T("claude.testFail", ex.Message) };
            }
        }

        private static string? FirstExisting(params string[] candidates)
            => candidates.FirstOrDefault(File.Exists);

        private static string? FindOnPath(string fileName)
        {
            try
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        var candidate = Path.Combine(dir.Trim().Trim('"'), fileName);
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            return null;
        }
    }

    public sealed class ClaudeConnectionTestResult
    {
        public bool Ok { get; init; }
        public string Message { get; init; } = "";
        public string AgentPath { get; init; } = "";
        public string Version { get; init; } = "";
    }
}
