using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// Probes Cursor CLI models with a short ask-mode ping to find the quietest available model.
    /// Used after capacity/rate-limit failures before auto-retry or manual Resume buttons.
    /// </summary>
    internal sealed class CursorModelRouter
    {
        public static CursorModelRouter Instance { get; } = new();

        private static readonly string[] DefaultPreferred =
        [
            "composer-2.5",
            "auto",
            "composer-2",
            "grok-4.6"
        ];

        private readonly object _gate = new();
        private readonly Dictionary<string, DateTime> _busyUntil = new(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyList<CursorModelProbeResult> _lastProbes = Array.Empty<CursorModelProbeResult>();
        private DateTime _lastProbeUtc = DateTime.MinValue;

        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(25);
        private static readonly TimeSpan BusyTtl = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(45);
        private const int MaxParallelProbes = 3;
        private const string ProbePrompt = "Reply with exactly: pong";

        private CursorModelRouter()
        {
        }

        public sealed record CursorModelProbeResult(
            string Model,
            bool Ok,
            TimeSpan Latency,
            string Status,
            string Detail);

        public sealed record PickResult(string? BestModel, int? LatencyMs, IReadOnlyList<CursorModelProbeResult> Probes);

        public void MarkBusy(string? model)
        {
            var id = (model ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            lock (_gate)
            {
                _busyUntil[id] = DateTime.UtcNow + BusyTtl;
                _lastProbes = Array.Empty<CursorModelProbeResult>();
                _lastProbeUtc = DateTime.MinValue;
            }
        }

        public IReadOnlyList<string> GetLastOkModelIds()
        {
            lock (_gate)
            {
                return _lastProbes
                    .Where(p => p.Ok)
                    .OrderBy(p => p.Latency)
                    .Select(p => p.Model)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        public async Task<PickResult> PickQuietestAsync(
            string agentPath,
            string? failedModel,
            CancellationToken cancellationToken,
            bool useCache = false)
        {
            if (useCache && TryGetCachedPick(out var cached))
            {
                return cached;
            }

            var bridge = CursorAgentBridge.Instance;
            bridge.TryGetNodeEntry(agentPath, out var nodeExe, out var indexJs);

            var candidates = await ResolveCandidatesAsync(failedModel, cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                return new PickResult(null, null, Array.Empty<CursorModelProbeResult>());
            }

            var workspace = Path.Combine(Path.GetTempPath(), "gdp-cursor-probe");
            try
            {
                Directory.CreateDirectory(workspace);
            }
            catch
            {
                workspace = Path.GetTempPath();
            }

            var probes = await ProbeCandidatesAsync(
                    agentPath,
                    nodeExe,
                    indexJs,
                    workspace,
                    candidates,
                    cancellationToken)
                .ConfigureAwait(false);

            lock (_gate)
            {
                _lastProbes = probes;
                _lastProbeUtc = DateTime.UtcNow;
            }

            var winner = probes
                .Where(p => p.Ok)
                .OrderBy(p => p.Latency)
                .FirstOrDefault();

            if (winner == null)
            {
                return new PickResult(null, null, probes);
            }

            return new PickResult(
                winner.Model,
                (int)Math.Round(winner.Latency.TotalMilliseconds),
                probes);
        }

        private bool TryGetCachedPick(out PickResult result)
        {
            lock (_gate)
            {
                if (_lastProbes.Count == 0
                    || (DateTime.UtcNow - _lastProbeUtc) > CacheTtl)
                {
                    result = new PickResult(null, null, Array.Empty<CursorModelProbeResult>());
                    return false;
                }

                var winner = _lastProbes
                    .Where(p => p.Ok && !IsBusyLocked(p.Model))
                    .OrderBy(p => p.Latency)
                    .FirstOrDefault();

                if (winner == null)
                {
                    result = new PickResult(null, null, _lastProbes);
                    return false;
                }

                result = new PickResult(
                    winner.Model,
                    (int)Math.Round(winner.Latency.TotalMilliseconds),
                    _lastProbes);
                return true;
            }
        }

        private async Task<IReadOnlyList<string>> ResolveCandidatesAsync(
            string? failedModel,
            CancellationToken cancellationToken)
        {
            var failed = (failedModel ?? string.Empty).Trim();
            IReadOnlyList<CursorModelInfo> catalog;
            try
            {
                catalog = await CursorModelCatalog.Instance
                    .GetModelsAsync(forceRefresh: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                catalog = Array.Empty<CursorModelInfo>();
            }

            var catalogIds = new HashSet<string>(
                catalog.Select(m => m.Id),
                StringComparer.OrdinalIgnoreCase);

            var ordered = new List<string>();
            foreach (var preferred in DefaultPreferred)
            {
                if (string.Equals(preferred, "auto", StringComparison.OrdinalIgnoreCase)
                    || catalogIds.Contains(preferred))
                {
                    ordered.Add(preferred);
                }
            }

            if (ordered.Count == 0)
            {
                ordered.AddRange(catalog.Take(4).Select(m => m.Id));
            }

            if (!ordered.Contains("auto", StringComparer.OrdinalIgnoreCase))
            {
                ordered.Insert(Math.Min(1, ordered.Count), "auto");
            }

            var result = new List<string>();
            foreach (var id in ordered)
            {
                if (IsBusy(id))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(failed)
                    && string.Equals(id, failed, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!result.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(id);
                }
            }

            return result;
        }

        private async Task<IReadOnlyList<CursorModelProbeResult>> ProbeCandidatesAsync(
            string agentPath,
            string? nodeExe,
            string? indexJs,
            string workspace,
            IReadOnlyList<string> candidates,
            CancellationToken cancellationToken)
        {
            using var gate = new SemaphoreSlim(MaxParallelProbes);
            var tasks = candidates.Select(async model =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await ProbeOneAsync(
                            agentPath,
                            nodeExe,
                            indexJs,
                            workspace,
                            model,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            });

            return await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private async Task<CursorModelProbeResult> ProbeOneAsync(
            string agentPath,
            string? nodeExe,
            string? indexJs,
            string workspace,
            string model,
            CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var run = await RunProbeProcessAsync(
                        agentPath,
                        nodeExe,
                        indexJs,
                        workspace,
                        model,
                        cancellationToken)
                    .ConfigureAwait(false);
                sw.Stop();

                var combined = ((run.StdOut ?? string.Empty) + "\n" + (run.StdErr ?? string.Empty)).Trim();
                if (run.TimedOut)
                {
                    MarkBusy(model);
                    return new CursorModelProbeResult(model, false, sw.Elapsed, "timeout", "timeout");
                }

                if (CursorAcpErrorHelper.IsCapacityError(combined))
                {
                    MarkBusy(model);
                    var detail = TruncateDetail(combined);
                    return new CursorModelProbeResult(model, false, sw.Elapsed, "busy", detail);
                }

                if (run.ExitCode != 0)
                {
                    var detail = TruncateDetail(string.IsNullOrWhiteSpace(combined)
                        ? $"exit {run.ExitCode}"
                        : combined);
                    return new CursorModelProbeResult(model, false, sw.Elapsed, "error", detail);
                }

                return new CursorModelProbeResult(
                    model,
                    true,
                    sw.Elapsed,
                    "ok",
                    TruncateDetail(combined));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new CursorModelProbeResult(
                    model,
                    false,
                    sw.Elapsed,
                    "error",
                    TruncateDetail(ex.Message));
            }
        }

        private sealed record ProbeRunResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

        private static async Task<ProbeRunResult> RunProbeProcessAsync(
            string agentPath,
            string? nodeExe,
            string? indexJs,
            string workspace,
            string model,
            CancellationToken cancellationToken)
        {
            var startInfo = BuildProbeStartInfo(agentPath, nodeExe, indexJs, workspace, model);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new ProbeRunResult(-1, string.Empty, "Could not start cursor-agent.", false);
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
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
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var waitTask = process.WaitForExitAsync(cancellationToken);
            var completed = await Task.WhenAny(waitTask, Task.Delay(ProbeTimeout, cancellationToken))
                .ConfigureAwait(false);
            if (completed != waitTask)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return new ProbeRunResult(-1, stdout.ToString(), stderr.ToString(), true);
            }

            await waitTask.ConfigureAwait(false);
            try
            {
                process.WaitForExit(2000);
            }
            catch
            {
            }

            return new ProbeRunResult(process.ExitCode, stdout.ToString(), stderr.ToString(), false);
        }

        private static ProcessStartInfo BuildProbeStartInfo(
            string agentPath,
            string? nodeExe,
            string? indexJs,
            string workspace,
            string model)
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

            startInfo.Environment["NO_COLOR"] = "1";
            startInfo.Environment["FORCE_COLOR"] = "0";
            startInfo.Environment["TERM"] = "dumb";

            void AddProbeArgs(ProcessStartInfo psi)
            {
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add("--mode=ask");
                psi.ArgumentList.Add("--output-format");
                psi.ArgumentList.Add("text");
                psi.ArgumentList.Add("--trust");
                psi.ArgumentList.Add("--workspace");
                psi.ArgumentList.Add(workspace);
                psi.ArgumentList.Add("--model");
                psi.ArgumentList.Add(model);
                psi.ArgumentList.Add(ProbePrompt);
            }

            if (!string.IsNullOrWhiteSpace(nodeExe) && !string.IsNullOrWhiteSpace(indexJs))
            {
                startInfo.FileName = nodeExe;
                startInfo.ArgumentList.Add(indexJs);
                AddProbeArgs(startInfo);
                return startInfo;
            }

            if (agentPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || agentPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            {
                var inner = new StringBuilder();
                inner.Append('"').Append(agentPath).Append('"');
                inner.Append(" -p --mode=ask --output-format text --trust");
                inner.Append(" --workspace \"").Append(workspace).Append('"');
                inner.Append(" --model \"").Append(model).Append('"');
                inner.Append(" \"").Append(ProbePrompt).Append('"');

                startInfo.FileName = "cmd.exe";
                startInfo.ArgumentList.Add("/d");
                startInfo.ArgumentList.Add("/s");
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add(inner.ToString());
                return startInfo;
            }

            startInfo.FileName = agentPath;
            AddProbeArgs(startInfo);
            return startInfo;
        }

        private bool IsBusy(string model)
        {
            lock (_gate)
            {
                return IsBusyLocked(model);
            }
        }

        private bool IsBusyLocked(string model)
        {
            var id = (model ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            if (!_busyUntil.TryGetValue(id, out var until))
            {
                return false;
            }

            if (DateTime.UtcNow >= until)
            {
                _busyUntil.Remove(id);
                return false;
            }

            return true;
        }

        private static string TruncateDetail(string text)
        {
            var t = (text ?? string.Empty).Replace("\r\n", "\n").Trim();
            if (t.Length <= 120)
            {
                return t;
            }

            return t[..117] + "…";
        }
    }
}
