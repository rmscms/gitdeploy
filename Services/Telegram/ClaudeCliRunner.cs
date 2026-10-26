using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Services.Localization;
using Newtonsoft.Json.Linq;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// Runs <c>claude -p --output-format stream-json</c> for a single non-interactive turn.
    /// </summary>
    public sealed class ClaudeCliRunner
    {
        public async Task<AgentRunResult> RunAsync(
            string claudePath,
            string projectPath,
            string prompt,
            string? model,
            string? resumeSessionId,
            string permissionMode,
            TimeSpan timeout,
            Action<string>? onProgress,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(claudePath) || !File.Exists(claudePath))
            {
                return new AgentRunResult
                {
                    Output = "Claude Code CLI not found.",
                    ExitCode = -1,
                    Source = "claude"
                };
            }

            Directory.CreateDirectory(projectPath);

            var psi = new ProcessStartInfo
            {
                FileName = claudePath,
                WorkingDirectory = projectPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                // Persian / emoji prompts must reach the CLI as UTF-8 (Windows defaults to ANSI/OEM).
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add("--output-format");
            psi.ArgumentList.Add("stream-json");
            psi.ArgumentList.Add("--verbose");
            psi.ArgumentList.Add("--permission-mode");
            psi.ArgumentList.Add(NormalizePermissionMode(permissionMode));

            if (!string.IsNullOrWhiteSpace(model))
            {
                psi.ArgumentList.Add("--model");
                psi.ArgumentList.Add(model.Trim());
            }

            if (!string.IsNullOrWhiteSpace(resumeSessionId))
            {
                psi.ArgumentList.Add("--resume");
                psi.ArgumentList.Add(resumeSessionId.Trim());
            }

            foreach (var extra in CursorWorkspaceRoots.GetRoots(projectPath).Extras)
            {
                if (!string.IsNullOrWhiteSpace(extra) && Directory.Exists(extra))
                {
                    psi.ArgumentList.Add("--add-dir");
                    psi.ArgumentList.Add(extra);
                }
            }

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var stderr = new StringBuilder();
            var assistantText = new StringBuilder();
            var state = new ParseState { SessionId = resumeSessionId ?? string.Empty };
            var lastActivityUtc = DateTime.UtcNow;

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null)
                {
                    return;
                }

                lastActivityUtc = DateTime.UtcNow;
                ParseLine(e.Data, onProgress, state, assistantText);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null)
                {
                    return;
                }

                lastActivityUtc = DateTime.UtcNow;
                stderr.AppendLine(e.Data);
            };

            if (!process.Start())
            {
                return new AgentRunResult
                {
                    Output = "Failed to start Claude Code CLI.",
                    ExitCode = -1,
                    Source = "claude"
                };
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var timedOut = false;

            using var reg = timeoutCts.Token.Register(() =>
            {
                timedOut = !cancellationToken.IsCancellationRequested;
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

            var promptBytes = new UTF8Encoding(false).GetBytes(SanitizeForUtf8(prompt ?? string.Empty));
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(promptBytes, timeoutCts.Token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (Exception) when (timeoutCts.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
                // CLI exited before reading stdin; the exit code / stderr below explains why.
            }
            finally
            {
                try
                {
                    process.StandardInput.Close();
                }
                catch
                {
                }
            }

            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
            var startedUtc = DateTime.UtcNow;
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

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(quiet ? 30 : 90), heartbeatCts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        return;
                    }

                    if (process.HasExited)
                    {
                        return;
                    }

                    if (!quiet && (DateTime.UtcNow - lastActivityUtc).TotalSeconds < 75)
                    {
                        continue;
                    }

                    var minutes = Math.Max(1, (int)Math.Round((DateTime.UtcNow - startedUtc).TotalMinutes));
                    onProgress?.Invoke(quiet
                        ? Loc.T("cursor.progress.stillWorking")
                        : Loc.T("cursor.progress.stillWorkingMins", minutes));
                }
            }, heartbeatCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!timedOut)
                {
                    throw;
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

            if (timedOut)
            {
                return new AgentRunResult
                {
                    Output = Loc.T("claude.timeout", (int)timeout.TotalMinutes),
                    SessionId = state.SessionId,
                    ExitCode = -2,
                    Source = "claude"
                };
            }

            // Let the async stdout handler flush its last lines.
            process.WaitForExit();

            var output = state.FinalText;
            if (string.IsNullOrWhiteSpace(output))
            {
                output = assistantText.ToString().Trim();
            }

            var exitCode = process.ExitCode;
            if (state.IsError && exitCode == 0)
            {
                exitCode = 1;
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                var err = Truncate(stderr.ToString().Trim(), 600);
                output = string.IsNullOrWhiteSpace(err)
                    ? (exitCode == 0 ? string.Empty : "Claude Code failed with exit " + exitCode)
                    : err;
            }

            return new AgentRunResult
            {
                Output = output,
                SessionId = state.SessionId,
                ExitCode = exitCode,
                Source = "claude",
                Usage = state.Usage
            };
        }

        private sealed class ParseState
        {
            public string SessionId = "";
            public string FinalText = "";
            public bool IsError;
            public AgentTokenUsage? Usage;
            public DateTime LastThinkingUtc = DateTime.MinValue;
        }

        private static void ParseLine(
            string line,
            Action<string>? onProgress,
            ParseState state,
            StringBuilder assistantText)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{')
            {
                return;
            }

            JObject obj;
            try
            {
                obj = JObject.Parse(trimmed);
            }
            catch
            {
                return;
            }

            var sid = obj.Value<string>("session_id");
            if (!string.IsNullOrWhiteSpace(sid))
            {
                state.SessionId = sid!;
            }

            switch (obj.Value<string>("type"))
            {
                case "system":
                    if (string.Equals(obj.Value<string>("subtype"), "init", StringComparison.OrdinalIgnoreCase))
                    {
                        var model = obj.Value<string>("model");
                        if (!string.IsNullOrWhiteSpace(model))
                        {
                            onProgress?.Invoke("🧠 " + model);
                        }
                    }

                    break;

                case "assistant":
                    if (obj["message"]?["content"] is JArray blocks)
                    {
                        foreach (var block in blocks.OfType<JObject>())
                        {
                            switch (block.Value<string>("type"))
                            {
                                case "text":
                                    var text = block.Value<string>("text");
                                    if (!string.IsNullOrWhiteSpace(text))
                                    {
                                        assistantText.Clear();
                                        assistantText.Append(text!.Trim());
                                    }

                                    break;
                                case "tool_use":
                                    onProgress?.Invoke(DescribeToolUse(
                                        block.Value<string>("name") ?? "",
                                        block["input"] as JObject));
                                    break;
                                case "thinking":
                                    if ((DateTime.UtcNow - state.LastThinkingUtc).TotalSeconds >= 6)
                                    {
                                        state.LastThinkingUtc = DateTime.UtcNow;
                                        var thought = block.Value<string>("thinking");
                                        if (!string.IsNullOrWhiteSpace(thought))
                                        {
                                            onProgress?.Invoke("🧠 " + Truncate(Flatten(thought!), 160));
                                        }
                                    }

                                    break;
                            }
                        }
                    }

                    break;

                case "result":
                    state.FinalText = (obj.Value<string>("result") ?? string.Empty).Trim();
                    state.IsError = obj.Value<bool?>("is_error") == true;
                    state.Usage = ReadUsage(obj["usage"]);
                    break;
            }
        }

        private static AgentTokenUsage ReadUsage(JToken? usage)
        {
            if (usage == null || usage.Type != JTokenType.Object)
            {
                return AgentTokenUsage.Unavailable("claude");
            }

            long Read(string key) => long.TryParse(usage[key]?.ToString(), out var n) ? n : 0;

            var input = Read("input_tokens");
            var output = Read("output_tokens");
            var cacheRead = Read("cache_read_input_tokens");
            var cacheWrite = Read("cache_creation_input_tokens");
            if (input == 0 && output == 0 && cacheRead == 0 && cacheWrite == 0)
            {
                return AgentTokenUsage.Unavailable("claude");
            }

            return new AgentTokenUsage
            {
                Available = true,
                InputTokens = input,
                OutputTokens = output,
                CacheReadTokens = cacheRead,
                CacheWriteTokens = cacheWrite,
                Source = "claude"
            };
        }

        private static string DescribeToolUse(string name, JObject? input)
        {
            string Arg(params string[] keys)
            {
                foreach (var key in keys)
                {
                    var v = input?[key]?.ToString();
                    if (!string.IsNullOrWhiteSpace(v))
                    {
                        return Truncate(Flatten(v!), 110);
                    }
                }

                return string.Empty;
            }

            return name switch
            {
                "Read" => "📖 " + ShortPath(Arg("file_path")),
                "Edit" or "MultiEdit" or "Write" or "NotebookEdit" => "✍️ " + ShortPath(Arg("file_path", "notebook_path")),
                "Bash" => "⌨️ " + Arg("command"),
                "Grep" => "🔎 " + Arg("pattern"),
                "Glob" => "🔎 " + Arg("pattern"),
                "LS" => "📂 " + ShortPath(Arg("path")),
                "WebFetch" => "🌐 " + Arg("url"),
                "WebSearch" => "🌐 " + Arg("query"),
                "Task" or "Agent" => "🤖 " + Arg("description"),
                _ => "🔧 " + name
            };
        }

        private static string ShortPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } f ? f : path;
            }
            catch
            {
                return path;
            }
        }

        private static string NormalizePermissionMode(string? mode)
        {
            return string.Equals(mode?.Trim(), "acceptEdits", StringComparison.OrdinalIgnoreCase)
                ? "acceptEdits"
                : "bypassPermissions";
        }

        private static string Flatten(string s)
            => System.Text.RegularExpressions.Regex.Replace(s.Replace('\r', ' ').Replace('\n', ' '), "\\s{2,}", " ").Trim();

        private static string Truncate(string s, int max)
            => s.Length <= max ? s : s[..(max - 1)] + "…";

        /// <summary>Drops lone surrogates that would otherwise be written as invalid UTF-8.</summary>
        private static string SanitizeForUtf8(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    sb.Append(c).Append(s[++i]);
                }
                else if (!char.IsSurrogate(c))
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }
    }
}
