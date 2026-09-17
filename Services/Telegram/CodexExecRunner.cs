using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Services;
using GitDeployPro.Services.Localization;
using Newtonsoft.Json.Linq;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// Runs <c>codex exec --json</c> for a single non-interactive turn.
    /// </summary>
    public sealed class CodexExecRunner
    {
            public async Task<AgentRunResult> RunAsync(
            string codexPath,
            string projectPath,
            string prompt,
            string? model,
            string? photoPath,
            string? resumeSessionId,
            string? apiKey,
            string? providerId,
            Action<string>? onProgress,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(codexPath) || !File.Exists(codexPath))
            {
                return new AgentRunResult
                {
                    Output = "Codex CLI not found.",
                    ExitCode = -1,
                    Source = "codex"
                };
            }

            Directory.CreateDirectory(projectPath);
            var lastMessageFile = Path.Combine(Path.GetTempPath(), "gdp-codex-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = codexPath,
                    WorkingDirectory = projectPath,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    // Without this, Windows defaults stdin to the ANSI/OEM code page and
                    // Persian / emoji prompts become invalid UTF-8 for Codex.
                    StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                psi.ArgumentList.Add("exec");

                var isResume = !string.IsNullOrWhiteSpace(resumeSessionId);
                if (isResume)
                {
                    // resume has a narrower option set — no --sandbox / -C / --add-dir
                    psi.ArgumentList.Add("resume");
                    psi.ArgumentList.Add("--json");
                    psi.ArgumentList.Add("--skip-git-repo-check");
                    // Same trust model as Cursor (--force --trust): Telegram agent must edit the project.
                    psi.ArgumentList.Add("--dangerously-bypass-approvals-and-sandbox");
                    // Emit reasoning summaries into --json so the app chat can show thinking like Cursor.
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add("model_reasoning_summary=\"auto\"");
                    psi.ArgumentList.Add("-o");
                    psi.ArgumentList.Add(lastMessageFile);

                    if (!string.IsNullOrWhiteSpace(model))
                    {
                        psi.ArgumentList.Add("-m");
                        psi.ArgumentList.Add(model.Trim());
                    }

                    if (!string.IsNullOrWhiteSpace(photoPath) && File.Exists(photoPath))
                    {
                        psi.ArgumentList.Add("-i");
                        psi.ArgumentList.Add(photoPath);
                    }

                    psi.ArgumentList.Add(resumeSessionId!.Trim());
                    psi.ArgumentList.Add("-");
                }
                else
                {
                    psi.ArgumentList.Add("--json");
                    psi.ArgumentList.Add("--skip-git-repo-check");
                    // Match Cursor bridge trust: edit/run inside the user's project without interactive approval.
                    // workspace-write alone often blocks tools on Windows and the model asks the user for files.
                    psi.ArgumentList.Add("--dangerously-bypass-approvals-and-sandbox");
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add("model_reasoning_summary=\"auto\"");
                    psi.ArgumentList.Add("-C");
                    psi.ArgumentList.Add(projectPath);
                    psi.ArgumentList.Add("-o");
                    psi.ArgumentList.Add(lastMessageFile);

                    if (!string.IsNullOrWhiteSpace(model))
                    {
                        psi.ArgumentList.Add("-m");
                        psi.ArgumentList.Add(model.Trim());
                    }

                    var workspace = CursorWorkspaceRoots.GetRoots(projectPath);
                    foreach (var extra in workspace.Extras)
                    {
                        if (!string.IsNullOrWhiteSpace(extra) && Directory.Exists(extra))
                        {
                            psi.ArgumentList.Add("--add-dir");
                            psi.ArgumentList.Add(extra);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(photoPath) && File.Exists(photoPath))
                    {
                        psi.ArgumentList.Add("-i");
                        psi.ArgumentList.Add(photoPath);
                    }

                    // Prompt via stdin sentinel so long prompts / special chars stay safe.
                    psi.ArgumentList.Add("-");
                }

                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    var provider = CodexProviderCatalog.Get(providerId);
                    psi.Environment[provider.EnvKey] = apiKey;
                    // Keep OpenRouter env populated when using that provider (compat).
                    if (provider.Id == CodexProviderCatalog.OpenRouter)
                    {
                        psi.Environment["OPENROUTER_API_KEY"] = apiKey;
                    }
                }

                using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                var sessionId = resumeSessionId ?? string.Empty;
                var assistantBits = new StringBuilder();
                var lastFatalError = new StringBuilder();
                var lastProgressUtc = DateTime.UtcNow;

                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null)
                    {
                        return;
                    }

                    lastProgressUtc = DateTime.UtcNow;
                    stdout.AppendLine(e.Data);
                    TryParseProgressLine(e.Data, onProgress, ref sessionId, assistantBits, lastFatalError);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null)
                    {
                        return;
                    }

                    lastProgressUtc = DateTime.UtcNow;
                    stderr.AppendLine(e.Data);
                    // Do not spam Telegram with Rust tracing / models_manager noise.
                    if (ShouldForwardStderrAsProgress(e.Data))
                    {
                        onProgress?.Invoke("⚙️ " + e.Data.Trim());
                    }
                };

                if (!process.Start())
                {
                    return new AgentRunResult
                    {
                        Output = "Failed to start Codex CLI.",
                        ExitCode = -1,
                        Source = "codex"
                    };
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var promptText = SanitizeForUtf8(prompt ?? string.Empty);
                var promptBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(promptText);
                await process.StandardInput.BaseStream.WriteAsync(promptBytes, cancellationToken).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();

                using var reg = cancellationToken.Register(() =>
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
                });

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
                            if ((DateTime.UtcNow - lastProgressUtc).TotalSeconds < 75)
                            {
                                continue;
                            }

                            if ((DateTime.UtcNow - lastHeartbeatEmitUtc).TotalSeconds < 120)
                            {
                                continue;
                            }
                        }

                        lastHeartbeatEmitUtc = DateTime.UtcNow;
                        var minutes = Math.Max(1, (int)Math.Round((DateTime.UtcNow - startedUtc).TotalMinutes));
                        onProgress?.Invoke(quiet
                            ? Loc.T("cursor.progress.stillWorking")
                            : Loc.T("cursor.progress.stillWorkingMins", minutes));
                    }
                }, heartbeatCts.Token);

                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

                heartbeatCts.Cancel();
                try
                {
                    await heartbeat.ConfigureAwait(false);
                }
                catch
                {
                }

                var lastMessage = string.Empty;
                if (File.Exists(lastMessageFile))
                {
                    try
                    {
                        lastMessage = (await File.ReadAllTextAsync(lastMessageFile, cancellationToken).ConfigureAwait(false)).Trim();
                    }
                    catch
                    {
                    }
                }

                if (string.IsNullOrWhiteSpace(lastMessage) && assistantBits.Length > 0)
                {
                    lastMessage = assistantBits.ToString().Trim();
                }

                if (string.IsNullOrWhiteSpace(lastMessage) || process.ExitCode != 0)
                {
                    var combined = string.Join(
                        "\n",
                        lastFatalError.ToString(),
                        stderr.ToString(),
                        stdout.ToString());
                    if (LooksLikeRateLimitText(combined))
                    {
                        lastMessage = Loc.T("codex.rateLimited");
                    }
                    else if (string.IsNullOrWhiteSpace(lastMessage))
                    {
                        var err = PreferUserFacingStderr(stderr.ToString());
                        if (string.IsNullOrWhiteSpace(err) && lastFatalError.Length > 0)
                        {
                            err = Truncate(lastFatalError.ToString().Trim(), 500);
                        }

                        lastMessage = string.IsNullOrWhiteSpace(err)
                            ? (process.ExitCode == 0 ? "(empty Codex reply)" : "Codex failed with exit " + process.ExitCode)
                            : err;
                    }
                }

                return new AgentRunResult
                {
                    Output = lastMessage,
                    SessionId = sessionId,
                    ExitCode = process.ExitCode,
                    Source = "codex"
                };
            }
            finally
            {
                try
                {
                    if (File.Exists(lastMessageFile))
                    {
                        File.Delete(lastMessageFile);
                    }
                }
                catch
                {
                }
            }
        }

        private static void TryParseProgressLine(
            string line,
            Action<string>? onProgress,
            ref string sessionId,
            StringBuilder assistantBits,
            StringBuilder lastFatalError)
        {
            if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
            {
                return;
            }

            try
            {
                var jo = JObject.Parse(line);
                var topType = jo.Value<string>("type") ?? string.Empty;

                var sid = jo.Value<string>("thread_id")
                          ?? jo.Value<string>("session_id")
                          ?? jo["session"]?["id"]?.ToString()
                          ?? jo["thread"]?["id"]?.ToString();
                if (!string.IsNullOrWhiteSpace(sid))
                {
                    sessionId = sid.Trim();
                }

                if (string.Equals(topType, "turn.started", StringComparison.OrdinalIgnoreCase))
                {
                    onProgress?.Invoke("⏳ Codex turn started…");
                    return;
                }

                if (string.Equals(topType, "error", StringComparison.OrdinalIgnoreCase))
                {
                    var err = jo.Value<string>("message") ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(err)
                        && !err.Contains("Reconnecting", StringComparison.OrdinalIgnoreCase))
                    {
                        lastFatalError.Clear();
                        lastFatalError.Append(err.Trim());
                        onProgress?.Invoke("⚠️ " + Truncate(err, 160));
                    }

                    return;
                }

                if (string.Equals(topType, "turn.failed", StringComparison.OrdinalIgnoreCase))
                {
                    var err = jo["error"]?["message"]?.ToString() ?? "turn failed";
                    lastFatalError.Clear();
                    lastFatalError.Append(err.Trim());
                    onProgress?.Invoke("⚠️ " + Truncate(err, 160));
                    return;
                }

                // Modern Codex JSONL: {"type":"item.completed","item":{"type":"reasoning","text":"..."}}
                if (topType.StartsWith("item.", StringComparison.OrdinalIgnoreCase))
                {
                    var item = jo["item"] as JObject;
                    if (item == null)
                    {
                        return;
                    }

                    var itemType = item.Value<string>("type") ?? string.Empty;
                    var isStarted = topType.Equals("item.started", StringComparison.OrdinalIgnoreCase);
                    var isCompleted = topType.Equals("item.completed", StringComparison.OrdinalIgnoreCase);

                    if (itemType.Equals("reasoning", StringComparison.OrdinalIgnoreCase) && isCompleted)
                    {
                        var think = item.Value<string>("text")?.Trim() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(think))
                        {
                            onProgress?.Invoke(Loc.T("cursor.progress.thinking", Truncate(think, 220)));
                        }
                        else
                        {
                            onProgress?.Invoke("🧠 thinking…");
                        }

                        return;
                    }

                    if (itemType.Equals("agent_message", StringComparison.OrdinalIgnoreCase) && isCompleted)
                    {
                        var text = item.Value<string>("text")?.Trim() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            if (assistantBits.Length > 0)
                            {
                                assistantBits.AppendLine();
                            }

                            assistantBits.Append(text);
                            // Preview in app chat like Cursor assistant crumbs (final reply still published separately).
                            onProgress?.Invoke(Loc.T("cursor.progress.assistant", Truncate(text, 160)));
                        }

                        return;
                    }

                    if (itemType.Equals("command_execution", StringComparison.OrdinalIgnoreCase))
                    {
                        var cmd = item.Value<string>("command")?.Trim() ?? "command";
                        if (isStarted)
                        {
                            onProgress?.Invoke(Loc.T("cursor.progress.shell", Truncate(cmd, 140)));
                        }
                        else if (isCompleted)
                        {
                            var code = item["exit_code"]?.ToString();
                            var status = item.Value<string>("status") ?? string.Empty;
                            var mark = string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ? "❌" : "✅";
                            onProgress?.Invoke($"{mark} {Truncate(cmd, 120)}"
                                               + (string.IsNullOrWhiteSpace(code) ? string.Empty : $" (exit {code})"));
                        }

                        return;
                    }

                    if (itemType.Equals("file_change", StringComparison.OrdinalIgnoreCase) && isCompleted)
                    {
                        var changes = item["changes"] as JArray;
                        if (changes != null && changes.Count > 0)
                        {
                            foreach (var ch in changes.Take(4))
                            {
                                var path = ch?["path"]?.ToString() ?? "?";
                                var kind = ch?["kind"]?.ToString() ?? "update";
                                var label = kind.Equals("add", StringComparison.OrdinalIgnoreCase)
                                    ? Loc.T("cursor.progress.write", Truncate(path, 120))
                                    : kind.Equals("delete", StringComparison.OrdinalIgnoreCase)
                                        ? "🗑️ " + Truncate(path, 120)
                                        : Loc.T("cursor.progress.edit", Truncate(path, 120));
                                onProgress?.Invoke(label);
                            }

                            if (changes.Count > 4)
                            {
                                onProgress?.Invoke($"✏️ +{changes.Count - 4} more file changes…");
                            }
                        }

                        return;
                    }

                    if (itemType.Equals("web_search", StringComparison.OrdinalIgnoreCase) && isCompleted)
                    {
                        var q = item.Value<string>("query") ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(q))
                        {
                            onProgress?.Invoke("🔎 " + Truncate(q, 140));
                        }

                        return;
                    }

                    if (itemType.Equals("mcp_tool_call", StringComparison.OrdinalIgnoreCase) && isStarted)
                    {
                        var tool = item.Value<string>("tool") ?? item.Value<string>("server") ?? "tool";
                        onProgress?.Invoke(Loc.T("cursor.progress.toolNamed", Truncate(tool, 120)));
                        return;
                    }

                    if (itemType.Equals("todo_list", StringComparison.OrdinalIgnoreCase))
                    {
                        var items = item["items"] as JArray;
                        if (items != null)
                        {
                            var done = items.Count(x => x?["completed"]?.Value<bool>() == true);
                            onProgress?.Invoke($"📋 plan {done}/{items.Count}");
                        }

                        return;
                    }

                    return;
                }

                // Legacy / alternate shapes
                var legacyType = topType
                                 ?? jo.Value<string>("msg")
                                 ?? string.Empty;
                if (legacyType.Contains("reasoning", StringComparison.OrdinalIgnoreCase)
                    || legacyType.Contains("thinking", StringComparison.OrdinalIgnoreCase))
                {
                    var think = jo.Value<string>("text")
                                ?? jo["item"]?["text"]?.ToString()
                                ?? string.Empty;
                    onProgress?.Invoke(string.IsNullOrWhiteSpace(think)
                        ? "🧠 thinking…"
                        : Loc.T("cursor.progress.thinking", Truncate(think, 220)));
                }
            }
            catch
            {
                // ignore non-json noise
            }
        }

        private static bool ShouldForwardStderrAsProgress(string line)
        {
            var t = (line ?? string.Empty).Trim();
            if (t.Length == 0 || t.Length >= 240)
            {
                return false;
            }

            // Rust tracing: "2026-09-17T05:03:50.105700Z ERROR codex_…"
            if (t.Length > 20 && t[4] == '-' && t[10] == '-' && t.Contains('T'))
            {
                return false;
            }

            if (t.Contains("codex_models_manager", StringComparison.OrdinalIgnoreCase)
                || t.Contains("failed to refresh available models", StringComparison.OrdinalIgnoreCase)
                || t.Contains("timeout waiting for child process", StringComparison.OrdinalIgnoreCase)
                || t.Contains("approval policy is Never", StringComparison.OrdinalIgnoreCase)
                || t.Contains("failed to parse function arguments", StringComparison.OrdinalIgnoreCase)
                || t.Contains("ERROR codex_", StringComparison.OrdinalIgnoreCase)
                || t.Contains("WARN  codex_", StringComparison.OrdinalIgnoreCase)
                || t.Contains("WARN codex_", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static string PreferUserFacingStderr(string stderr)
        {
            if (string.IsNullOrWhiteSpace(stderr))
            {
                return string.Empty;
            }

            var lines = stderr.Replace("\r\n", "\n").Split('\n');
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (line.Contains("not valid UTF-8", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Error loading config", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Missing Authentication", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("401", StringComparison.Ordinal)
                    || line.Contains("429", StringComparison.Ordinal)
                    || line.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("OPENROUTER", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("❌", StringComparison.Ordinal))
                {
                    if (line.Contains("429", StringComparison.Ordinal)
                        || line.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase))
                    {
                        return Loc.T("codex.rateLimited");
                    }

                    return line;
                }
            }

            // Ignore pure tracing dumps as the user-facing failure reason.
            if (lines.All(l =>
                    string.IsNullOrWhiteSpace(l)
                    || l.Contains("codex_models_manager", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("ERROR codex_", StringComparison.OrdinalIgnoreCase)
                    || (l.Trim().Length > 20 && l.Trim()[4] == '-' && l.Contains('T'))))
            {
                return string.Empty;
            }

            return stderr.Trim();
        }

        private static bool LooksLikeRateLimitText(string? text)
        {
            text ??= string.Empty;
            return text.Contains("429", StringComparison.Ordinal)
                   || text.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("exceeded retry limit", StringComparison.OrdinalIgnoreCase);
        }

        private static string SanitizeForUtf8(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            // Drop unpaired surrogates so UTF-8 encoding never emits invalid sequences.
            var sb = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        sb.Append(c);
                        sb.Append(text[++i]);
                    }
                    else
                    {
                        sb.Append('\uFFFD');
                    }
                }
                else if (char.IsLowSurrogate(c))
                {
                    sb.Append('\uFFFD');
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        private static string Truncate(string text, int max)
        {
            text = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= max ? text : text[..(max - 1)] + "…";
        }
    }
}
