using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Models;
using GitDeployPro.Services.Localization;
using Newtonsoft.Json.Linq;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// Runs Codex CLI (OpenRouter) turns with a per-project queue, mirroring replies to Telegram.
    /// </summary>
    public sealed class CodexAgentBridge
    {
        public static CodexAgentBridge Instance { get; } = new();

        private readonly ConcurrentDictionary<string, byte> _workers = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ConcurrentQueue<PendingTurn>> _queues =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _turnCts =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly TelegramChatStore _store = TelegramChatStore.Instance;
        private readonly ConfigurationService _config = new();
        private readonly CodexExecRunner _runner = new();
        private readonly object _pathCacheGate = new();
        private string? _cachedConfiguredPath;
        private string? _cachedCodexPath;
        private DateTime _pathCacheUtc = DateTime.MinValue;
        private readonly object _telegramProgressGate = new();
        private DateTime _lastTelegramProgressUtc = DateTime.MinValue;

        private CodexAgentBridge()
        {
        }

        public void EnqueueUserTurn(string projectPath, string text, string? photoPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || TelegramPaths.IsUnassigned(projectPath))
            {
                return;
            }

            var config = _config.LoadGlobalConfig();
            if (!config.CodexAgentEnabled)
            {
                PostStatus(projectPath, Loc.T("codex.disabled"));
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
                                PostStatus(turn.ProjectPath, Loc.T("codex.failed", ex.Message));
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
            if (string.IsNullOrWhiteSpace(projectPath) || TelegramPaths.IsUnassigned(projectPath))
            {
                return;
            }

            var config = _config.LoadGlobalConfig();
            if (!config.CodexAgentEnabled)
            {
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    var exe = ResolveCodexExecutable(config.CodexCliPath);
                    if (string.IsNullOrWhiteSpace(exe))
                    {
                        return;
                    }

                    var providerId = CodexProviderCatalog.Normalize(config.CodexProvider);
                    var provider = CodexProviderCatalog.Get(providerId);
                    var key = CodexConfigWriter.DecryptApiKey();
                    if (!provider.RequiresApiKey || !string.IsNullOrWhiteSpace(key))
                    {
                        CodexConfigWriter.EnsureProviderConfig(providerId, config.CodexAgentModel);
                    }
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
            _store.ClearCodexSession(projectPath);
            PostStatus(
                projectPath,
                string.IsNullOrWhiteSpace(reason)
                    ? Loc.T("codex.restarted")
                    : Loc.T("codex.restartedReason", reason));
            PrewarmForProject(projectPath);
        }

        public void SetModelAndRestart(string projectPath, string? modelId)
        {
            var config = _config.LoadGlobalConfig();
            var providerId = CodexProviderCatalog.Normalize(config.CodexProvider);
            var model = (modelId ?? string.Empty).Trim();
            if (string.Equals(model, "auto", StringComparison.OrdinalIgnoreCase)
                || string.Equals(model, "default", StringComparison.OrdinalIgnoreCase))
            {
                model = CodexModelCatalog.DefaultModelFor(providerId);
            }

            _config.UpdateGlobalConfig(cfg => cfg.CodexAgentModel = model);
            try
            {
                CodexConfigWriter.EnsureProviderConfig(providerId, model);
            }
            catch
            {
            }

            RestartProjectAgent(projectPath, Loc.T("cursor.modelSet", model));
        }

        public CursorAgentStatusInfo GetAgentStatus(string projectPath)
        {
            var config = _config.LoadGlobalConfig();
            var key = TelegramPaths.ToProjectKey(projectPath);
            var queueDepth = _queues.TryGetValue(key, out var q) ? q.Count : 0;
            var busy = _workers.ContainsKey(key);
            var providerId = CodexProviderCatalog.Normalize(config.CodexProvider);
            var model = string.IsNullOrWhiteSpace(config.CodexAgentModel)
                ? CodexModelCatalog.DefaultModelFor(providerId)
                : config.CodexAgentModel.Trim();
            var session = _store.GetCodexSessionId(projectPath);
            var sessionHint = string.IsNullOrWhiteSpace(session)
                ? "none"
                : TruncateId(session);

            return new CursorAgentStatusInfo
            {
                Enabled = config.CodexAgentEnabled,
                DaemonAlive = !string.IsNullOrWhiteSpace(ResolveCodexExecutable(config.CodexCliPath)),
                Model = model,
                QueueDepth = queueDepth,
                TurnBusy = busy,
                LastActivityUtc = null,
                SessionHint = sessionHint
            };
        }

        public void InvalidateAfterSettingsSave(string? projectPath = null)
        {
            lock (_pathCacheGate)
            {
                _cachedConfiguredPath = null;
                _cachedCodexPath = null;
                _pathCacheUtc = DateTime.MinValue;
            }

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
                _cachedCodexPath = null;
                _pathCacheUtc = DateTime.MinValue;
            }
        }

        public string? ResolveCodexExecutable(string? configuredPath = null)
        {
            lock (_pathCacheGate)
            {
                var cfg = (configuredPath ?? string.Empty).Trim();
                if (_pathCacheUtc > DateTime.UtcNow.AddMinutes(-5)
                    && string.Equals(_cachedConfiguredPath, cfg, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(_cachedCodexPath)
                    && File.Exists(_cachedCodexPath))
                {
                    return _cachedCodexPath;
                }

                string? found = null;
                if (!string.IsNullOrWhiteSpace(cfg) && File.Exists(cfg))
                {
                    found = cfg;
                }
                else
                {
                    found = FindOnPath("codex.exe") ?? FindOnPath("codex");
                    if (found == null)
                    {
                        var local = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "npm",
                            "codex.cmd");
                        if (File.Exists(local))
                        {
                            found = local;
                        }
                    }
                }

                _cachedConfiguredPath = cfg;
                _cachedCodexPath = found;
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
            PostStatus(projectPath, Loc.T("codex.started"));
            NotifyTelegramWorking(projectPath, Loc.T("codex.started"));
            var config = _config.LoadGlobalConfig();
            var exe = ResolveCodexExecutable(config.CodexCliPath);
            if (string.IsNullOrWhiteSpace(exe))
            {
                PostStatus(projectPath, Loc.T("codex.agentMissing"));
                return;
            }

            var providerId = CodexProviderCatalog.Normalize(config.CodexProvider);
            var provider = CodexProviderCatalog.Get(providerId);
            var storedKey = CodexConfigWriter.DecryptApiKey();
            var apiKey = CodexConfigWriter.ResolveRuntimeApiKey(providerId, storedKey);
            if (provider.RequiresApiKey && string.IsNullOrWhiteSpace(storedKey))
            {
                PostStatus(projectPath, Loc.T("codex.missingKey"));
                return;
            }

            try
            {
                CodexConfigWriter.EnsureProviderConfig(providerId, config.CodexAgentModel);
            }
            catch (Exception ex)
            {
                PostStatus(projectPath, Loc.T("codex.tomlFailed", ex.Message));
            }

            var resume = _store.GetCodexSessionId(projectPath);
            var prompt = BuildPrompt(projectPath, text, photoPath, !string.IsNullOrWhiteSpace(resume));
            var model = string.IsNullOrWhiteSpace(config.CodexAgentModel)
                ? CodexModelCatalog.DefaultModelFor(providerId)
                : config.CodexAgentModel.Trim();

            void OnProgress(string line) => PostProgress(projectPath, line);

            var run = await _runner.RunAsync(
                    exe,
                    projectPath,
                    prompt,
                    model,
                    photoPath,
                    string.IsNullOrWhiteSpace(resume) ? null : resume,
                    apiKey,
                    providerId,
                    OnProgress,
                    cancellationToken)
                .ConfigureAwait(false);

            // Stale / incompatible resume CLI args → drop session and retry fresh once.
            if (run.ExitCode != 0
                && !string.IsNullOrWhiteSpace(resume)
                && LooksLikeResumeCliFailure(run.Output))
            {
                _store.ClearCodexSession(projectPath);
                PostStatus(projectPath, Loc.T("codex.resumeRetry"));
                var freshPrompt = BuildPrompt(projectPath, text, photoPath, resumeSession: false);
                run = await _runner.RunAsync(
                        exe,
                        projectPath,
                        freshPrompt,
                        model,
                        photoPath,
                        resumeSessionId: null,
                        apiKey,
                        providerId,
                        OnProgress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(run.SessionId))
            {
                _store.SetCodexSessionId(projectPath, run.SessionId);
            }

            var reply = string.IsNullOrWhiteSpace(run.Output)
                ? Loc.T("codex.emptyReply")
                : run.Output.Trim();
            if (run.ExitCode != 0 && !reply.StartsWith("❌", StringComparison.Ordinal))
            {
                if (LooksLikeRateLimit(reply))
                {
                    reply = "❌ " + Loc.T("codex.rateLimited");
                }
                else
                {
                    reply = "❌ " + reply;
                }
            }

            await PublishAgentReplyAsync(projectPath, reply, cancellationToken).ConfigureAwait(false);
        }

        private static bool LooksLikeRateLimit(string? output)
        {
            var text = output ?? string.Empty;
            return text.Contains("429", StringComparison.Ordinal)
                   || text.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("exceeded retry limit", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeResumeCliFailure(string? output)
        {
            var text = output ?? string.Empty;
            return text.Contains("unexpected argument", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("exec resume", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("session not found", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("No such session", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("unknown session", StringComparison.OrdinalIgnoreCase);
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
            sb.AppendLine("- You are the coding agent for this GitDeploy project chat (Codex CLI).");
            sb.AppendLine("- Do NOT introduce yourself. Do NOT ask what to do. Do NOT say you are ready.");
            sb.AppendLine("- Investigate/fix quickly inside the listed workspace roots, then reply briefly with the result.");
            sb.AppendLine("- Prefer reading/editing files under the Primary root with normal tools; do not ask the user for paths that are already listed above.");
            sb.AppendLine("- You have full read/write access to the workspace for this turn (approvals/sandbox bypassed by GitDeploy). Edit files directly — do not claim sandbox blocked you.");
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

        private async Task PublishAgentReplyAsync(
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
                SenderName = "Codex"
            };

            _store.Append(projectPath, message);
            TelegramPoller.Instance.RaiseMessage(projectPath, message);

            var config = _config.LoadGlobalConfig();
            var token = EncryptionService.Decrypt(config.TelegramBotToken);
            var chatId = _store.GetLastChatId();
            if (string.IsNullOrWhiteSpace(token) || chatId == 0)
            {
                return;
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

            await Task.CompletedTask.ConfigureAwait(false);
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
                    SenderName = "Codex"
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
        /// Verifies Codex binary + active provider (OpenRouter key / local Ollama / OpenAI / LM Studio).
        /// </summary>
        public async Task<CodexConnectionTestResult> TestConnectionAsync(
            string? plainApiKeyOverride = null,
            CancellationToken cancellationToken = default)
        {
            CodexInstallHelper.RefreshProcessPathFromSystem();
            var config = _config.LoadGlobalConfig();
            var providerId = CodexProviderCatalog.Normalize(config.CodexProvider);
            var provider = CodexProviderCatalog.Get(providerId);
            var exe = ResolveCodexExecutable(config.CodexCliPath);
            if (string.IsNullOrWhiteSpace(exe))
            {
                return new CodexConnectionTestResult
                {
                    Ok = false,
                    Message = Loc.T("codex.agentMissing")
                };
            }

            var version = CodexInstallHelper.TryGetVersion(exe);
            var apiKey = (plainApiKeyOverride ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                apiKey = CodexConfigWriter.DecryptApiKey() ?? string.Empty;
            }

            if (provider.RequiresApiKey && string.IsNullOrWhiteSpace(apiKey))
            {
                return new CodexConnectionTestResult
                {
                    Ok = false,
                    NeedsKey = true,
                    AgentPath = exe,
                    Version = version ?? string.Empty,
                    Message = Loc.T("codex.help.testNeedKey")
                };
            }

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
                HttpResponseMessage response;
                if (provider.Id == CodexProviderCatalog.OpenRouter)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/key");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://gitdeploy.pro");
                    request.Headers.TryAddWithoutValidation("X-Title", "GitDeployPro");
                    response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                }
                else if (provider.Id == CodexProviderCatalog.Ollama)
                {
                    response = await client.GetAsync("http://127.0.0.1:11434/api/tags", cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (provider.Id == CodexProviderCatalog.LmStudio)
                {
                    response = await client.GetAsync("http://127.0.0.1:1234/v1/models", cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                }

                using (response)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        var detail = TruncateBody(body);
                        return new CodexConnectionTestResult
                        {
                            Ok = false,
                            NeedsKey = provider.RequiresApiKey && (int)response.StatusCode == 401,
                            AgentPath = exe,
                            Version = version ?? string.Empty,
                            Message = Loc.T(
                                "codex.help.testKeyFail",
                                ((int)response.StatusCode).ToString(),
                                string.IsNullOrWhiteSpace(detail) ? response.ReasonPhrase ?? "error" : detail)
                        };
                    }

                    try
                    {
                        CodexConfigWriter.EnsureProviderConfig(providerId, config.CodexAgentModel);
                    }
                    catch
                    {
                    }

                    var label = provider.Id == CodexProviderCatalog.OpenRouter
                        ? TryReadOpenRouterKeyLabel(body)
                        : provider.Label;
                    return new CodexConnectionTestResult
                    {
                        Ok = true,
                        AgentPath = exe,
                        Version = version ?? string.Empty,
                        KeyLabel = label,
                        Message = string.IsNullOrWhiteSpace(label)
                            ? Loc.T("codex.help.testConnOk", exe, version ?? "?")
                            : Loc.T("codex.help.testConnOkLabel", exe, version ?? "?", label)
                    };
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new CodexConnectionTestResult
                {
                    Ok = false,
                    AgentPath = exe,
                    Version = version ?? string.Empty,
                    Message = Loc.T("codex.help.testFail", ex.Message)
                };
            }
        }

        private static string TryReadOpenRouterKeyLabel(string body)
        {
            try
            {
                var jo = JObject.Parse(body);
                var data = jo["data"] as JObject ?? jo;
                var label = data?["label"]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(label))
                {
                    return label!;
                }

                var limit = data?["limit_remaining"]?.ToString();
                if (!string.IsNullOrWhiteSpace(limit))
                {
                    return "limit_remaining=" + limit;
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string TruncateBody(string body)
        {
            body = (body ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (body.Length <= 160)
            {
                return body;
            }

            return body[..157] + "…";
        }

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

    public sealed class CodexConnectionTestResult
    {
        public bool Ok { get; init; }
        public bool NeedsKey { get; init; }
        public string Message { get; init; } = "";
        public string AgentPath { get; init; } = "";
        public string Version { get; init; } = "";
        public string KeyLabel { get; init; } = "";
    }
}
