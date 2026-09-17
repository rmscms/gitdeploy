using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Services.Localization;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// Manual + automatic Codex CLI updates (npm @latest). Auto runs at most once per 24h.
    /// </summary>
    public static class CodexCliUpdateService
    {
        public static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(24);

        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static readonly ConfigurationService Config = new();

        public static bool ShouldCheckAutomatically()
        {
            var cfg = Config.LoadGlobalConfig();
            if (!cfg.CodexAgentEnabled
                && string.IsNullOrWhiteSpace(cfg.CodexCliPath)
                && string.IsNullOrWhiteSpace(CodexAgentBridge.Instance.ResolveCodexExecutable(cfg.CodexCliPath)))
            {
                return false;
            }

            if (!cfg.LastCodexCliUpdateCheckUtc.HasValue)
            {
                return true;
            }

            var elapsed = AppTimeService.UtcNow - cfg.LastCodexCliUpdateCheckUtc.Value.ToUniversalTime();
            return elapsed >= AutoCheckInterval;
        }

        public static void MarkCheckCompleted()
        {
            Config.UpdateGlobalConfig(cfg => cfg.LastCodexCliUpdateCheckUtc = AppTimeService.UtcNow);
        }

        /// <summary>
        /// Background check: silent npm update when due. Never opens a UI window.
        /// </summary>
        public static async Task RunAutomaticCheckAsync(CancellationToken cancellationToken = default)
        {
            if (!ShouldCheckAutomatically())
            {
                return;
            }

            if (!await Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                // Stamp first so a hung npm does not retry every timer tick.
                MarkCheckCompleted();
                await UpdateSilentAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort background maintenance.
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>
        /// Manual update: silent npm when available; otherwise opens the visible update terminal.
        /// </summary>
        public static async Task<CodexCliUpdateResult> RunManualUpdateAsync(
            CancellationToken cancellationToken = default)
        {
            if (!await Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return new CodexCliUpdateResult
                {
                    Ok = false,
                    Message = Loc.T("codex.updateBusy")
                };
            }

            try
            {
                MarkCheckCompleted();
                var result = await UpdateSilentAsync(cancellationToken).ConfigureAwait(false);
                if (result.Ok || !result.NeedsVisibleTerminal)
                {
                    return result;
                }

                CodexInstallHelper.OpenUpdateTerminal();
                return new CodexCliUpdateResult
                {
                    Ok = true,
                    OpenedTerminal = true,
                    Message = Loc.T("codex.updateTerminalOpened")
                };
            }
            finally
            {
                Gate.Release();
            }
        }

        public static async Task<CodexCliUpdateResult> UpdateSilentAsync(
            CancellationToken cancellationToken = default)
        {
            CodexInstallHelper.RefreshProcessPathFromSystem();
            CodexAgentBridge.Instance.InvalidatePathCache();

            var beforePath = CodexAgentBridge.Instance.ResolveCodexExecutable(null);
            var beforeVersion = string.IsNullOrWhiteSpace(beforePath)
                ? null
                : CodexInstallHelper.TryGetVersion(beforePath);

            var npm = CodexInstallHelper.FindNpmExecutable();
            if (string.IsNullOrWhiteSpace(npm))
            {
                return new CodexCliUpdateResult
                {
                    Ok = false,
                    NeedsVisibleTerminal = true,
                    PreviousVersion = beforeVersion ?? string.Empty,
                    Message = Loc.T("codex.updateNeedNpm")
                };
            }

            var run = await RunProcessAsync(
                    npm!,
                    "install -g @openai/codex@latest --loglevel=error",
                    cancellationToken)
                .ConfigureAwait(false);

            CodexInstallHelper.RefreshProcessPathFromSystem();
            CodexAgentBridge.Instance.InvalidatePathCache();

            var afterPath = CodexAgentBridge.Instance.ResolveCodexExecutable(null);
            var afterVersion = string.IsNullOrWhiteSpace(afterPath)
                ? null
                : CodexInstallHelper.TryGetVersion(afterPath);

            if (run.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(run.Output)
                    ? ("exit " + run.ExitCode)
                    : TrimOneLine(run.Output);
                return new CodexCliUpdateResult
                {
                    Ok = false,
                    NeedsVisibleTerminal = true,
                    PreviousVersion = beforeVersion ?? string.Empty,
                    NewVersion = afterVersion ?? string.Empty,
                    Message = Loc.T("codex.updateFailed", detail)
                };
            }

            if (string.IsNullOrWhiteSpace(afterPath))
            {
                return new CodexCliUpdateResult
                {
                    Ok = false,
                    NeedsVisibleTerminal = true,
                    PreviousVersion = beforeVersion ?? string.Empty,
                    Message = Loc.T("codex.updateMissingAfter")
                };
            }

            var changed = !string.Equals(
                NormalizeVersion(beforeVersion),
                NormalizeVersion(afterVersion),
                StringComparison.OrdinalIgnoreCase);

            return new CodexCliUpdateResult
            {
                Ok = true,
                Updated = changed,
                PreviousVersion = beforeVersion ?? string.Empty,
                NewVersion = afterVersion ?? string.Empty,
                AgentPath = afterPath,
                Message = changed
                    ? Loc.T(
                        "codex.updateChanged",
                        string.IsNullOrWhiteSpace(beforeVersion) ? "?" : beforeVersion,
                        afterVersion ?? "?")
                    : Loc.T("codex.updateAlreadyLatest", afterVersion ?? "?")
            };
        }

        private static async Task<(int ExitCode, string Output)> RunProcessAsync(
            string fileName,
            string arguments,
            CancellationToken cancellationToken)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    stdout.AppendLine(e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    stderr.AppendLine(e.Data);
                }
            };
            process.Exited += (_, _) =>
            {
                try
                {
                    tcs.TrySetResult(process.ExitCode);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            };

            if (!process.Start())
            {
                return (-1, "failed to start npm");
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
                var exit = await tcs.Task.ConfigureAwait(false);
                var output = (stdout.ToString() + "\n" + stderr.ToString()).Trim();
                return (exit, output);
            }
        }

        private static string NormalizeVersion(string? version)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                return string.Empty;
            }

            var t = version.Trim();
            // "codex-cli 0.154.0" → "0.154.0"
            var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? t : parts[^1].Trim();
        }

        private static string TrimOneLine(string text)
        {
            text = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= 180 ? text : text[..177] + "…";
        }
    }

    public sealed class CodexCliUpdateResult
    {
        public bool Ok { get; init; }
        public bool Updated { get; init; }
        public bool OpenedTerminal { get; init; }
        public bool NeedsVisibleTerminal { get; init; }
        public string Message { get; init; } = "";
        public string PreviousVersion { get; init; } = "";
        public string NewVersion { get; init; } = "";
        public string AgentPath { get; init; } = "";
    }
}
