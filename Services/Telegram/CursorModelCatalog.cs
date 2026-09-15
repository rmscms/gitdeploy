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
    /// Live model catalog from Cursor CLI (<c>agent models</c> / <c>--list-models</c>).
    /// </summary>
    internal sealed class CursorModelCatalog
    {
        public static CursorModelCatalog Instance { get; } = new();

        private readonly object _gate = new();
        private IReadOnlyList<CursorModelInfo> _cache = Array.Empty<CursorModelInfo>();
        private DateTime _cacheUtc = DateTime.MinValue;
        private string? _lastError;

        public string? LastError
        {
            get
            {
                lock (_gate)
                {
                    return _lastError;
                }
            }
        }

        public async Task<IReadOnlyList<CursorModelInfo>> GetModelsAsync(
            bool forceRefresh,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (!forceRefresh
                    && _cache.Count > 0
                    && (DateTime.UtcNow - _cacheUtc).TotalMinutes < 15)
                {
                    return _cache;
                }
            }

            var models = await FetchFromCliAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (models.Count > 0)
                {
                    _cache = models;
                    _cacheUtc = DateTime.UtcNow;
                    _lastError = null;
                }
                else if (_cache.Count == 0)
                {
                    _lastError ??= "No models returned from Cursor CLI.";
                }

                return _cache.Count > 0 ? _cache : models;
            }
        }

        public async Task<IReadOnlyList<CursorModelInfo>> SearchAsync(
            string query,
            CancellationToken cancellationToken)
        {
            var all = await GetModelsAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
            var q = (query ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(q))
            {
                return all;
            }

            return all
                .Where(m =>
                    m.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || m.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || m.Family.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public static string DetectFamily(string modelId)
        {
            var id = (modelId ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(id) || id is "auto" or "default")
            {
                return "auto";
            }

            if (id.Contains("grok", StringComparison.Ordinal))
            {
                return "grok";
            }

            if (id.Contains("composer", StringComparison.Ordinal))
            {
                return "composer";
            }

            if (id.Contains("claude", StringComparison.Ordinal) || id.Contains("sonnet", StringComparison.Ordinal)
                || id.Contains("opus", StringComparison.Ordinal) || id.Contains("fable", StringComparison.Ordinal))
            {
                return "claude";
            }

            if (id.Contains("codex", StringComparison.Ordinal))
            {
                return "codex";
            }

            if (id.Contains("gemini", StringComparison.Ordinal))
            {
                return "gemini";
            }

            if (id.StartsWith("gpt-", StringComparison.Ordinal) || id.Contains("luna", StringComparison.Ordinal)
                || id.Contains("sol-", StringComparison.Ordinal))
            {
                return "gpt";
            }

            return "other";
        }

        private async Task<IReadOnlyList<CursorModelInfo>> FetchFromCliAsync(CancellationToken cancellationToken)
        {
            var bridge = CursorAgentBridge.Instance;
            var config = new ConfigurationService().LoadGlobalConfig();
            var agentPath = bridge.ResolveAgentExecutable(config.CursorAgentPath);
            if (string.IsNullOrWhiteSpace(agentPath))
            {
                lock (_gate)
                {
                    _lastError = "Cursor agent executable not found.";
                }

                return Array.Empty<CursorModelInfo>();
            }

            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            if (TryResolveNode(agentPath, out var nodeExe, out var indexJs))
            {
                startInfo.FileName = nodeExe;
                startInfo.ArgumentList.Add(indexJs);
                startInfo.ArgumentList.Add("models");
            }
            else
            {
                startInfo.FileName = agentPath;
                startInfo.ArgumentList.Add("models");
            }

            try
            {
                using var process = new Process { StartInfo = startInfo };
                if (!process.Start())
                {
                    lock (_gate)
                    {
                        _lastError = "Could not start agent models.";
                    }

                    return Array.Empty<CursorModelInfo>();
                }

                var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
                var exitTask = process.WaitForExitAsync(cancellationToken);
                var completed = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(45), cancellationToken))
                    .ConfigureAwait(false);
                if (completed != exitTask)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                    }

                    lock (_gate)
                    {
                        _lastError = "Listing models timed out.";
                    }

                    return Array.Empty<CursorModelInfo>();
                }

                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);
                var parsed = ParseModelsOutput(stdout);
                if (parsed.Count == 0)
                {
                    // Fallback flag used by some CLI builds.
                    parsed = await FetchWithListModelsFlagAsync(agentPath, cancellationToken).ConfigureAwait(false);
                }

                if (parsed.Count == 0)
                {
                    lock (_gate)
                    {
                        _lastError = string.IsNullOrWhiteSpace(stderr)
                            ? "Could not parse model list."
                            : stderr.Trim();
                    }
                }

                return parsed;
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _lastError = ex.Message;
                }

                return Array.Empty<CursorModelInfo>();
            }
        }

        private static async Task<IReadOnlyList<CursorModelInfo>> FetchWithListModelsFlagAsync(
            string agentPath,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            if (TryResolveNode(agentPath, out var nodeExe, out var indexJs))
            {
                startInfo.FileName = nodeExe;
                startInfo.ArgumentList.Add(indexJs);
                startInfo.ArgumentList.Add("--list-models");
            }
            else
            {
                startInfo.FileName = agentPath;
                startInfo.ArgumentList.Add("--list-models");
            }

            try
            {
                using var process = new Process { StartInfo = startInfo };
                if (!process.Start())
                {
                    return Array.Empty<CursorModelInfo>();
                }

                var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return ParseModelsOutput(stdout);
            }
            catch
            {
                return Array.Empty<CursorModelInfo>();
            }
        }

        internal static IReadOnlyList<CursorModelInfo> ParseModelsOutput(string output)
        {
            var list = new List<CursorModelInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in (output ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (string.IsNullOrWhiteSpace(line)
                    || line.StartsWith("Available models", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("Tip:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // "id - Display Name" or "id — Display"
                string id;
                string display;
                var sep = line.IndexOf(" - ", StringComparison.Ordinal);
                if (sep < 0)
                {
                    sep = line.IndexOf(" — ", StringComparison.Ordinal);
                }

                if (sep > 0)
                {
                    id = line[..sep].Trim();
                    display = line[(sep + 3)..].Trim();
                }
                else if (line.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
                {
                    id = line;
                    display = line;
                }
                else
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                {
                    continue;
                }

                // Strip markers like "(current, default)"
                display = display
                    .Replace("(current, default)", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("(current)", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("(default)", "", StringComparison.OrdinalIgnoreCase)
                    .Trim();

                list.Add(new CursorModelInfo(id, string.IsNullOrWhiteSpace(display) ? id : display, DetectFamily(id)));
            }

            return list;
        }

        private static bool TryResolveNode(string agentPath, out string nodeExe, out string indexJs)
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

                var versionsRoot = Path.Combine(root, "versions");
                if (!Directory.Exists(versionsRoot))
                {
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
    }

    internal readonly record struct CursorModelInfo(string Id, string DisplayName, string Family);
}
