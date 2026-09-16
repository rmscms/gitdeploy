using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GitDeployPro.Models;
using GitDeployPro.Services;
using GitDeployPro.Services.Localization;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// Per-project multi-root workspace for Cursor CLI (primary + optional --add-dir extras).
    /// </summary>
    public static class CursorWorkspaceRoots
    {
        public const int MaxExtraRoots = 8;
        public const int MaxRulesChars = 12_000;

        private static readonly object AnnounceGate = new();
        private static string _lastAnnouncedPath = "";
        private static DateTime _lastAnnouncedUtc = DateTime.MinValue;

        public readonly record struct WorkspaceInfo(
            string Primary,
            IReadOnlyList<string> Extras,
            bool HasMultiRoot);

        public static WorkspaceInfo GetRoots(string? projectPath)
        {
            var primary = NormalizeExistingDir(projectPath);
            if (string.IsNullOrWhiteSpace(primary))
            {
                return new WorkspaceInfo(string.Empty, Array.Empty<string>(), false);
            }

            var extras = LoadExtras(primary);
            return new WorkspaceInfo(primary, extras, extras.Count > 0);
        }

        public static IReadOnlyList<string> NormalizeExtras(string primaryPath, IEnumerable<string?>? candidates)
        {
            var primary = NormalizeExistingDir(primaryPath);
            var result = new List<string>();
            if (candidates == null)
            {
                return result;
            }

            foreach (var raw in candidates)
            {
                if (result.Count >= MaxExtraRoots)
                {
                    break;
                }

                var path = NormalizeExistingDir(raw);
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(primary)
                    && string.Equals(path, primary, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (result.Any(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                result.Add(path);
            }

            return result;
        }

        public static void SaveExtras(string projectPath, IEnumerable<string?> extras)
        {
            var primary = NormalizeExistingDir(projectPath);
            if (string.IsNullOrWhiteSpace(primary))
            {
                return;
            }

            var configService = new ConfigurationService();
            var config = configService.LoadProjectConfig(primary);
            config.LocalProjectPath = primary;
            config.CursorExtraRoots = NormalizeExtras(primary, extras).ToList();
            configService.SaveProjectConfig(config);
        }

        public static string FormatAnnouncement(WorkspaceInfo info)
        {
            if (string.IsNullOrWhiteSpace(info.Primary))
            {
                return Loc.T("telegram.workspace.none");
            }

            var name = TelegramPaths.DisplayName(info.Primary);
            var sb = new StringBuilder();
            sb.AppendLine(Loc.T("telegram.workspace.title", name));
            sb.AppendLine(Loc.T("telegram.workspace.root", info.Primary));
            if (info.HasMultiRoot)
            {
                sb.AppendLine(Loc.T("telegram.workspace.extra", string.Join(" , ", info.Extras)));
                sb.Append(Loc.T("telegram.workspace.multi", info.Extras.Count));
            }
            else
            {
                sb.Append(Loc.T("telegram.workspace.single"));
            }

            return sb.ToString().TrimEnd();
        }

        public static string FormatBadge(WorkspaceInfo info)
        {
            if (!info.HasMultiRoot)
            {
                return string.Empty;
            }

            return Loc.T("telegram.workspace.badge", info.Extras.Count);
        }

        /// <summary>
        /// Builds the Workspace + Rules block injected into every Cursor prompt.
        /// </summary>
        public static string BuildPromptContext(string? projectPath, bool resumeSession)
        {
            var info = GetRoots(projectPath);
            if (string.IsNullOrWhiteSpace(info.Primary))
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            if (resumeSession)
            {
                sb.AppendLine("Workspace/Rules still apply for this session.");
                sb.AppendLine($"Primary: {info.Primary}");
                if (info.HasMultiRoot)
                {
                    sb.AppendLine("Extra roots (also in scope): " + string.Join(" | ", info.Extras));
                }
            }
            else
            {
                sb.AppendLine("WORKSPACE:");
                sb.AppendLine($"- Primary root (cwd / --workspace): {info.Primary}");
                if (info.HasMultiRoot)
                {
                    foreach (var extra in info.Extras)
                    {
                        sb.AppendLine($"- Extra root (--add-dir): {extra}");
                    }
                }
                else
                {
                    sb.AppendLine("- Extra roots: none (single-root)");
                }
            }

            var rules = CollectRulesText(info.Primary, info.Extras);
            if (!string.IsNullOrWhiteSpace(rules))
            {
                sb.AppendLine();
                sb.AppendLine("PROJECT RULES (always follow these; they come from .cursor/rules):");
                sb.AppendLine(rules);
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine("PROJECT RULES: none found under .cursor/rules for this workspace.");
            }

            return sb.ToString().TrimEnd();
        }

        public static string CollectRulesText(string primary, IReadOnlyList<string> extras)
        {
            var roots = new List<string>();
            if (!string.IsNullOrWhiteSpace(primary))
            {
                roots.Add(primary);
            }

            if (extras != null)
            {
                roots.AddRange(extras.Where(x => !string.IsNullOrWhiteSpace(x)));
            }

            var sb = new StringBuilder();
            var remaining = MaxRulesChars;

            foreach (var root in roots)
            {
                if (remaining <= 0)
                {
                    break;
                }

                var rulesDir = Path.Combine(root, ".cursor", "rules");
                if (!Directory.Exists(rulesDir))
                {
                    continue;
                }

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(rulesDir, "*.*", SearchOption.AllDirectories)
                        .Where(f =>
                            f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".mdc", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
                }
                catch
                {
                    continue;
                }

                foreach (var file in files)
                {
                    if (remaining <= 0)
                    {
                        sb.AppendLine("…(rules truncated)");
                        return sb.ToString().TrimEnd();
                    }

                    string body;
                    try
                    {
                        body = File.ReadAllText(file);
                    }
                    catch
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(body))
                    {
                        continue;
                    }

                    var header = $"--- {file} ---";
                    sb.AppendLine(header);
                    remaining -= header.Length + 1;

                    if (body.Length > remaining)
                    {
                        sb.AppendLine(body.Substring(0, Math.Max(0, remaining)));
                        sb.AppendLine("…(truncated)");
                        remaining = 0;
                        break;
                    }

                    sb.AppendLine(body.TrimEnd());
                    remaining -= body.Length + 1;
                    sb.AppendLine();
                }
            }

            return sb.ToString().TrimEnd();
        }

        public static void AnnounceToChatAndTelegram(
            string projectPath,
            bool sendTelegram = true,
            bool force = false)
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
                full = projectPath.Trim();
            }

            lock (AnnounceGate)
            {
                if (!force
                    && string.Equals(_lastAnnouncedPath, full, StringComparison.OrdinalIgnoreCase)
                    && (DateTime.UtcNow - _lastAnnouncedUtc).TotalSeconds < 8)
                {
                    return;
                }

                _lastAnnouncedPath = full;
                _lastAnnouncedUtc = DateTime.UtcNow;
            }

            var info = GetRoots(full);
            var text = FormatAnnouncement(info);
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var store = TelegramChatStore.Instance;
            var message = new TelegramChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                Direction = TelegramMessageDirection.System,
                Status = TelegramMessageStatus.Sent,
                Text = text,
                Utc = DateTime.UtcNow,
                SenderName = "Workspace"
            };
            store.Append(full, message);
            TelegramPoller.Instance.RaiseMessage(full, message);

            if (!sendTelegram)
            {
                return;
            }

            try
            {
                var config = new ConfigurationService().LoadGlobalConfig();
                var token = EncryptionService.Decrypt(config.TelegramBotToken);
                var chatId = store.GetLastChatId();
                if (!string.IsNullOrWhiteSpace(token) && chatId != 0)
                {
                    TelegramOutboundQueue.Instance.EnqueueText(token, chatId, full, text);
                }
            }
            catch
            {
            }
        }

        private static IReadOnlyList<string> LoadExtras(string primary)
        {
            try
            {
                var config = new ConfigurationService().LoadProjectConfig(primary);
                return NormalizeExtras(primary, config.CursorExtraRoots);
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static string NormalizeExistingDir(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || TelegramPaths.IsUnassigned(path))
            {
                return string.Empty;
            }

            try
            {
                var full = Path.GetFullPath(path.Trim());
                return Directory.Exists(full) ? full : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
