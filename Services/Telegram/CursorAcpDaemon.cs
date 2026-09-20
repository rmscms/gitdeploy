using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// Long-lived Cursor ACP child (`agent acp`) talking JSON-RPC over stdio.
    /// Keeps initialize/auth/session warm so turns skip cold process bootstrap.
    /// </summary>
    internal sealed class CursorAcpDaemon : IDisposable
    {
        private readonly object _gate = new();
        private readonly SemaphoreSlim _rpcLock = new(1, 1);
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JToken>> _pending = new();
        private readonly StringBuilder _stdoutCarry = new();

        private Process? _process;
        private long _nextId = 1;
        private bool _disposed;
        private string? _sessionId;
        private DateTime _lastActivityUtc = DateTime.UtcNow;
        private int _completedPrompts;
        private string? _lastPlanFilePath;
        private AgentTokenUsage? _lastUsage;

        public CursorAcpDaemon(
            string projectPath,
            string agentPath,
            string? nodeExe,
            string? indexJs,
            string? model,
            string? mode = null)
        {
            ProjectPath = projectPath;
            AgentPath = agentPath;
            NodeExe = nodeExe;
            IndexJs = indexJs;
            Model = model;
            Mode = NormalizeMode(mode);
        }

        public string ProjectPath { get; }
        public string AgentPath { get; }
        public string? NodeExe { get; }
        public string? IndexJs { get; }
        public string? Model { get; }
        /// <summary>ACP session mode: agent | plan.</summary>
        public string Mode { get; }
        public string? SessionId => _sessionId;
        public bool HasConversation => Volatile.Read(ref _completedPrompts) > 0;
        public DateTime LastActivityUtc => _lastActivityUtc;
        public bool IsAlive
        {
            get
            {
                lock (_gate)
                {
                    return !_disposed && _process is { HasExited: false } && !string.IsNullOrWhiteSpace(_sessionId);
                }
            }
        }

        public async Task EnsureReadyAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            Touch();

            lock (_gate)
            {
                if (_process is { HasExited: false } && !string.IsNullOrWhiteSpace(_sessionId))
                {
                    return;
                }
            }

            await _rpcLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_gate)
                {
                    if (_process is { HasExited: false } && !string.IsNullOrWhiteSpace(_sessionId))
                    {
                        return;
                    }
                }

                await StartProcessUnlockedAsync(cancellationToken).ConfigureAwait(false);
                await InitializeSessionUnlockedAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _rpcLock.Release();
            }
        }

        public async Task<AgentRunResult> PromptAsync(
            string promptText,
            Action<string>? onProgress,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            Touch();

            await _rpcLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!IsAlive)
                {
                    onProgress?.Invoke("♻️ " + GitDeployPro.Services.Localization.Loc.T("cursor.autoRestarted"));
                    await StartProcessUnlockedAsync(cancellationToken).ConfigureAwait(false);
                    await InitializeSessionUnlockedAsync(cancellationToken).ConfigureAwait(false);
                }

                var sessionId = _sessionId
                    ?? throw new InvalidOperationException("ACP session was not created.");

                var assistant = new StringBuilder();
                var thoughtBuf = new StringBuilder();
                var lastThoughtFlush = DateTime.MinValue;
                var lastEventUtc = DateTime.UtcNow;
                var progressHandler = (Action<JObject>)(update =>
                {
                    lastEventUtc = DateTime.UtcNow;
                    HandleSessionUpdate(update, assistant, thoughtBuf, ref lastThoughtFlush, onProgress);
                });

                var promptParams = new JObject
                {
                    ["sessionId"] = sessionId,
                    ["prompt"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "text",
                            ["text"] = promptText
                        }
                    }
                };

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

                        if (!quiet)
                        {
                            if ((DateTime.UtcNow - lastEventUtc).TotalSeconds < 75)
                            {
                                continue;
                            }

                            if ((DateTime.UtcNow - lastHeartbeatEmitUtc).TotalSeconds < 120)
                            {
                                continue;
                            }

                            lastHeartbeatEmitUtc = DateTime.UtcNow;
                            var minutes = Math.Max(1, (int)Math.Round((DateTime.UtcNow - startedUtc).TotalMinutes));
                            onProgress?.Invoke(GitDeployPro.Services.Localization.Loc.T(
                                "cursor.progress.stillWorkingMins",
                                minutes));
                            continue;
                        }

                        if ((DateTime.UtcNow - lastHeartbeatEmitUtc).TotalSeconds < 28)
                        {
                            continue;
                        }

                        lastHeartbeatEmitUtc = DateTime.UtcNow;
                        onProgress?.Invoke(GitDeployPro.Services.Localization.Loc.T("cursor.progress.stillWorking"));
                    }
                }, heartbeatCts.Token);

                JToken result;
                try
                {
                    using var reg = RegisterUpdateHandler(progressHandler);
                    try
                    {
                        result = await SendRequestUnlockedAsync(
                                "session/prompt",
                                promptParams,
                                cancellationToken,
                                timeout: TimeSpan.FromMinutes(8))
                            .ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        // Do not blindly re-run a long prompt after timeout.
                        KillProcessUnlocked();
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        KillProcessUnlocked();
                        throw;
                    }
                    catch (CursorAcpAuthException)
                    {
                        KillProcessUnlocked();
                        throw;
                    }
                    catch
                    {
                        // Dead pipe / crashed child — recreate once (not on timeout).
                        KillProcessUnlocked();
                        await StartProcessUnlockedAsync(cancellationToken).ConfigureAwait(false);
                        await InitializeSessionUnlockedAsync(cancellationToken).ConfigureAwait(false);
                        sessionId = _sessionId
                            ?? throw new InvalidOperationException("ACP session was not created after restart.");
                        promptParams["sessionId"] = sessionId;
                        result = await SendRequestUnlockedAsync(
                                "session/prompt",
                                promptParams,
                                cancellationToken,
                                timeout: TimeSpan.FromMinutes(8))
                            .ConfigureAwait(false);
                    }
                }
                finally
                {
                    heartbeatCts.Cancel();
                    try
                    {
                        await heartbeat.ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }

                Touch();
                Interlocked.Increment(ref _completedPrompts);

                if (thoughtBuf.Length >= 8 && onProgress != null)
                {
                    var leftover = thoughtBuf.ToString().Trim();
                    thoughtBuf.Clear();
                    if (leftover.Length > 280)
                    {
                        leftover = leftover[..277] + "…";
                    }

                    onProgress("🧠 " + leftover);
                }

                var stop = result?["stopReason"]?.ToString() ?? string.Empty;
                var text = assistant.ToString().Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    text = result?["response"]?.ToString()?.Trim()
                           ?? result?["text"]?.ToString()?.Trim()
                           ?? string.Empty;
                }

                var usage = AgentTokenUsage.FromJToken(result?["usage"], "acp");
                if (!usage.Available && _lastUsage != null)
                {
                    usage = _lastUsage;
                }

                var planPath = _lastPlanFilePath;
                _lastPlanFilePath = null;
                _lastUsage = null;

                return new AgentRunResult
                {
                    Output = text,
                    SessionId = sessionId,
                    ExitCode = string.Equals(stop, "cancelled", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                    Source = "acp",
                    Usage = usage,
                    PlanFilePath = planPath
                };
            }
            finally
            {
                _rpcLock.Release();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            foreach (var pending in _pending.Values)
            {
                pending.TrySetCanceled();
            }

            _pending.Clear();
            KillProcessUnlocked();
            _rpcLock.Dispose();
        }

        private async Task StartProcessUnlockedAsync(CancellationToken cancellationToken)
        {
            KillProcessUnlocked();

            var startInfo = new ProcessStartInfo
            {
                WorkingDirectory = ProjectPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            if (!string.IsNullOrWhiteSpace(NodeExe) && !string.IsNullOrWhiteSpace(IndexJs)
                && File.Exists(NodeExe) && File.Exists(IndexJs))
            {
                startInfo.FileName = NodeExe;
                startInfo.ArgumentList.Add(IndexJs);
                startInfo.ArgumentList.Add("acp");
            }
            else
            {
                startInfo.FileName = AgentPath;
                startInfo.ArgumentList.Add("acp");
            }

            // Headless Telegram turns should not wait on interactive permission prompts.
            startInfo.Environment["CURSOR_AGENT_FORCE"] = "1";

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start())
            {
                throw new InvalidOperationException("Could not start Cursor ACP process.");
            }

            lock (_gate)
            {
                _process = process;
                _sessionId = null;
                _completedPrompts = 0;
                _stdoutCarry.Clear();
            }

            _ = Task.Run(() => ReadStdoutLoop(process), CancellationToken.None);
            _ = Task.Run(() => DrainStderr(process), CancellationToken.None);

            // Give the child a moment to open stdio before initialize.
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            if (process.HasExited)
            {
                throw new InvalidOperationException($"Cursor ACP exited immediately ({process.ExitCode}).");
            }
        }

        private async Task InitializeSessionUnlockedAsync(CancellationToken cancellationToken)
        {
            await SendRequestUnlockedAsync(
                    "initialize",
                    new JObject
                    {
                        ["protocolVersion"] = 1,
                        ["clientCapabilities"] = new JObject
                        {
                            ["fs"] = new JObject
                            {
                                ["readTextFile"] = false,
                                ["writeTextFile"] = false
                            },
                            ["terminal"] = false
                        },
                        ["clientInfo"] = new JObject
                        {
                            ["name"] = "GitDeployPro",
                            ["version"] = "1.0"
                        }
                    },
                    cancellationToken,
                    timeout: TimeSpan.FromSeconds(60))
                .ConfigureAwait(false);

            try
            {
                await SendRequestUnlockedAsync(
                        "authenticate",
                        new JObject { ["methodId"] = "cursor_login" },
                        cancellationToken,
                        timeout: TimeSpan.FromSeconds(60))
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not TimeoutException)
            {
                throw new CursorAcpAuthException(
                    "Cursor CLI is not logged in. Run agent.cmd → /login, then Restart agent.",
                    ex);
            }

            var sessionParams = new JObject
            {
                ["cwd"] = ProjectPath,
                ["mcpServers"] = new JArray(),
                ["mode"] = Mode
            };
            if (!string.IsNullOrWhiteSpace(Model))
            {
                sessionParams["model"] = Model.Trim();
            }

            var session = await SendRequestUnlockedAsync(
                    "session/new",
                    sessionParams,
                    cancellationToken,
                    timeout: TimeSpan.FromSeconds(90))
                .ConfigureAwait(false);

            var sid = session?["sessionId"]?.ToString();
            if (string.IsNullOrWhiteSpace(sid))
            {
                throw new InvalidOperationException("ACP session/new returned no sessionId.");
            }

            lock (_gate)
            {
                _sessionId = sid;
            }

            Touch();
        }

        private async Task<JToken> SendRequestUnlockedAsync(
            string method,
            JObject parameters,
            CancellationToken cancellationToken,
            TimeSpan timeout)
        {
            var process = _process
                ?? throw new InvalidOperationException("ACP process is not running.");
            if (process.HasExited)
            {
                throw new InvalidOperationException("ACP process has exited.");
            }

            var id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(id, tcs))
            {
                throw new InvalidOperationException("Failed to register ACP request.");
            }

            var envelope = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters
            };

            var line = envelope.ToString(Newtonsoft.Json.Formatting.None) + "\n";
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            await using (linked.Token.Register(() => tcs.TrySetCanceled(linked.Token)))
            {
                try
                {
                    // Include stdin write under the same timeout — a blocked pipe must not hang forever.
                    await process.StandardInput.WriteAsync(line.AsMemory(), linked.Token).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(linked.Token).ConfigureAwait(false);
                    return await tcs.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _pending.TryRemove(id, out _);
                    throw new TimeoutException($"ACP {method} timed out after {timeout.TotalSeconds:0}s.");
                }
                finally
                {
                    _pending.TryRemove(id, out _);
                }
            }
        }

        private void ReadStdoutLoop(Process process)
        {
            try
            {
                var buffer = new char[4096];
                while (!process.HasExited)
                {
                    var read = process.StandardOutput.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    _stdoutCarry.Append(buffer, 0, read);
                    DrainStdoutLines();
                }
            }
            catch
            {
            }

            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(new InvalidOperationException("ACP process closed."));
            }

            _pending.Clear();
        }

        private void DrainStdoutLines()
        {
            while (true)
            {
                string? line;
                lock (_gate)
                {
                    var text = _stdoutCarry.ToString();
                    var idx = text.IndexOf('\n');
                    if (idx < 0)
                    {
                        return;
                    }

                    line = text[..idx].TrimEnd('\r');
                    _stdoutCarry.Clear();
                    if (idx + 1 < text.Length)
                    {
                        _stdoutCarry.Append(text[(idx + 1)..]);
                    }
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                HandleStdoutLine(line);
            }
        }

        private void HandleStdoutLine(string line)
        {
            JObject msg;
            try
            {
                msg = JObject.Parse(line);
            }
            catch
            {
                return;
            }

            // Response to our request
            if (msg["id"] != null && (msg["result"] != null || msg["error"] != null))
            {
                var idToken = msg["id"];
                long id = idToken?.Type == JTokenType.Integer
                    ? idToken.Value<long>()
                    : long.TryParse(idToken?.ToString(), out var parsed) ? parsed : -1;
                if (id >= 0 && _pending.TryRemove(id, out var tcs))
                {
                    if (msg["error"] != null)
                    {
                        tcs.TrySetException(new InvalidOperationException(msg["error"]?.ToString() ?? "ACP error"));
                    }
                    else
                    {
                        tcs.TrySetResult(msg["result"] ?? JValue.CreateNull());
                    }
                }

                return;
            }

            var method = msg["method"]?.ToString() ?? string.Empty;
            if (string.Equals(method, "session/update", StringComparison.OrdinalIgnoreCase))
            {
                var update = msg["params"]?["update"] as JObject;
                if (update != null)
                {
                    var handlers = _updateHandlers;
                    foreach (var handler in handlers)
                    {
                        try
                        {
                            handler(update);
                        }
                        catch
                        {
                        }
                    }
                }

                return;
            }

            if (string.Equals(method, "session/request_permission", StringComparison.OrdinalIgnoreCase))
            {
                ReplyNotification(msg["id"], new JObject
                {
                    ["outcome"] = new JObject
                    {
                        ["outcome"] = "selected",
                        ["optionId"] = "allow-always"
                    }
                });
                return;
            }

            if (string.Equals(method, "cursor/ask_question", StringComparison.OrdinalIgnoreCase))
            {
                _ = HandleAskQuestionAsync(msg);
                return;
            }

            if (string.Equals(method, "cursor/create_plan", StringComparison.OrdinalIgnoreCase))
            {
                HandleCreatePlan(msg);
                return;
            }
        }

        private void HandleCreatePlan(JObject msg)
        {
            var p = msg["params"] as JObject ?? new JObject();
            var planMd = p["plan"]?.ToString()
                         ?? p["content"]?.ToString()
                         ?? p["markdown"]?.ToString()
                         ?? string.Empty;
            var name = (p["name"]?.ToString()
                        ?? p["title"]?.ToString()
                        ?? "plan").Trim();
            var overview = (p["overview"]?.ToString() ?? string.Empty).Trim();

            string? savedPath = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(planMd))
                {
                    savedPath = CursorPlanFileWriter.Write(ProjectPath, name, overview, planMd);
                    CursorPlanFileWriter.EnsurePlansDirectory(ProjectPath);
                    _lastPlanFilePath = savedPath;
                    onProgressSafe("📋 " + GitDeployPro.Services.Localization.Loc.T(
                        "cursor.planSaved",
                        Path.GetFileName(savedPath)));
                }
            }
            catch (Exception ex)
            {
                onProgressSafe("⚠️ " + GitDeployPro.Services.Localization.Loc.T("cursor.planSaveFailed", ex.Message));
            }

            var outcome = new JObject { ["outcome"] = "accepted" };
            if (!string.IsNullOrWhiteSpace(savedPath))
            {
                try
                {
                    outcome["planUri"] = new Uri(savedPath).AbsoluteUri;
                }
                catch
                {
                    outcome["planUri"] = savedPath;
                }
            }

            ReplyNotification(msg["id"], new JObject { ["outcome"] = outcome });
        }

        private async Task HandleAskQuestionAsync(JObject msg)
        {
            var id = msg["id"];
            var p = msg["params"] as JObject ?? new JObject();
            try
            {
                var answered = await CursorAskQuestionBroker.Instance
                    .AskAsync(ProjectPath, p, TimeSpan.FromMinutes(8))
                    .ConfigureAwait(false);
                if (answered != null)
                {
                    ReplyNotification(id, answered);
                }
                else
                {
                    ReplyNotification(id, new JObject
                    {
                        ["outcome"] = new JObject
                        {
                            ["outcome"] = "skipped",
                            ["reason"] = "timeout waiting for Telegram answer"
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                ReplyNotification(id, new JObject
                {
                    ["outcome"] = new JObject
                    {
                        ["outcome"] = "skipped",
                        ["reason"] = ex.Message
                    }
                });
            }
        }

        private void onProgressSafe(string text)
        {
            try
            {
                foreach (var h in _updateHandlers)
                {
                    try
                    {
                        h(new JObject
                        {
                            ["sessionUpdate"] = "agent_message_chunk",
                            ["content"] = new JObject { ["type"] = "text", ["text"] = text }
                        });
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static string NormalizeMode(string? mode)
        {
            return string.Equals((mode ?? string.Empty).Trim(), "plan", StringComparison.OrdinalIgnoreCase)
                ? "plan"
                : "agent";
        }

        private void ReplyNotification(JToken? id, JObject result)
        {
            var process = _process;
            if (process == null || process.HasExited || id == null)
            {
                return;
            }

            try
            {
                var envelope = new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["result"] = result
                };
                var line = envelope.ToString(Newtonsoft.Json.Formatting.None) + "\n";
                process.StandardInput.Write(line);
                process.StandardInput.Flush();
            }
            catch
            {
            }
        }

        private volatile List<Action<JObject>> _updateHandlers = new();

        private IDisposable RegisterUpdateHandler(Action<JObject> handler)
        {
            while (true)
            {
                var current = _updateHandlers;
                var next = new List<Action<JObject>>(current) { handler };
                if (ReferenceEquals(Interlocked.CompareExchange(ref _updateHandlers, next, current), current))
                {
                    break;
                }
            }

            return new AnonymousDisposable(() =>
            {
                while (true)
                {
                    var current = _updateHandlers;
                    var next = new List<Action<JObject>>(current);
                    next.Remove(handler);
                    if (ReferenceEquals(Interlocked.CompareExchange(ref _updateHandlers, next, current), current))
                    {
                        break;
                    }
                }
            });
        }

        private void HandleSessionUpdate(
            JObject update,
            StringBuilder assistant,
            StringBuilder thoughtBuf,
            ref DateTime lastThoughtFlush,
            Action<string>? onProgress)
        {
            var kind = update["sessionUpdate"]?.ToString() ?? string.Empty;
            if (string.Equals(kind, "usage_update", StringComparison.OrdinalIgnoreCase))
            {
                var usage = AgentTokenUsage.FromJToken(update["usage"] ?? update, "acp");
                if (usage.Available)
                {
                    _lastUsage = usage;
                }

                return;
            }

            if (string.Equals(kind, "agent_message_chunk", StringComparison.OrdinalIgnoreCase))
            {
                var text = update["content"]?["text"]?.ToString()
                           ?? update["content"]?.ToString();
                if (!string.IsNullOrEmpty(text))
                {
                    assistant.Append(text);
                }

                return;
            }

            if (string.Equals(kind, "agent_thought_chunk", StringComparison.OrdinalIgnoreCase))
            {
                var text = ExtractUpdateText(update);
                if (string.IsNullOrEmpty(text) || onProgress == null)
                {
                    return;
                }

                thoughtBuf.Append(text);
                // Flush often enough that Telegram sees thinking, not only tools.
                if ((DateTime.UtcNow - lastThoughtFlush).TotalSeconds < 2 && thoughtBuf.Length < 40)
                {
                    return;
                }

                if (thoughtBuf.Length < 12)
                {
                    return;
                }

                var chunk = thoughtBuf.ToString().Trim();
                thoughtBuf.Clear();
                lastThoughtFlush = DateTime.UtcNow;
                if (chunk.Length > 280)
                {
                    chunk = chunk[..277] + "…";
                }

                onProgress("🧠 " + chunk);
                return;
            }

            if (string.Equals(kind, "tool_call", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "tool_call_update", StringComparison.OrdinalIgnoreCase))
            {
                if (onProgress == null)
                {
                    return;
                }

                var status = update["status"]?.ToString() ?? string.Empty;
                var isTerminal = string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase);
                if (isTerminal)
                {
                    return;
                }

                // Flush pending thoughts before a tool so Telegram sees them in order.
                if (thoughtBuf.Length >= 12)
                {
                    var pendingThought = thoughtBuf.ToString().Trim();
                    thoughtBuf.Clear();
                    lastThoughtFlush = DateTime.UtcNow;
                    if (pendingThought.Length > 280)
                    {
                        pendingThought = pendingThought[..277] + "…";
                    }

                    onProgress("🧠 " + pendingThought);
                }

                // Mid-turn assistant narration ("در حال بررسی…") must not stick to the final reply.
                // Promote it to live progress, then keep only post-tool text as the answer.
                if (assistant.Length >= 12)
                {
                    var pendingMsg = assistant.ToString().Trim();
                    assistant.Clear();
                    if (pendingMsg.Length > 280)
                    {
                        pendingMsg = pendingMsg[..277] + "…";
                    }

                    onProgress("💬 " + pendingMsg);
                }

                var title = update["title"]?.ToString()
                            ?? update["kind"]?.ToString()
                            ?? update["toolName"]?.ToString()
                            ?? "tool";
                onProgress("🔧 " + title);
            }
        }

        private static string ExtractUpdateText(JObject update)
        {
            var content = update["content"];
            if (content is JObject contentObj)
            {
                var text = contentObj["text"]?.ToString();
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }

            if (content != null && content.Type == JTokenType.String)
            {
                return content.ToString();
            }

            return update["text"]?.ToString() ?? string.Empty;
        }

        private static void DrainStderr(Process process)
        {
            try
            {
                while (!process.StandardError.EndOfStream)
                {
                    _ = process.StandardError.ReadLine();
                }
            }
            catch
            {
            }
        }

        private void KillProcessUnlocked()
        {
            Process? process;
            lock (_gate)
            {
                process = _process;
                _process = null;
                _sessionId = null;
            }

            if (process == null)
            {
                return;
            }

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

            try
            {
                process.Dispose();
            }
            catch
            {
            }
        }

        private void Touch() => _lastActivityUtc = DateTime.UtcNow;

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(CursorAcpDaemon));
            }
        }

        private sealed class AnonymousDisposable : IDisposable
        {
            private readonly Action _dispose;

            public AnonymousDisposable(Action dispose) => _dispose = dispose;

            public void Dispose() => _dispose();
        }
    }
}
