using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GitDeployPro.Models;
using GitDeployPro.Services.Localization;
using Newtonsoft.Json;

namespace GitDeployPro.Services.Telegram
{
    public sealed class TelegramChatStore
    {
        public const int MaxMessagesPerThread = 2000;
        private readonly object _gate = new();

        public static TelegramChatStore Instance { get; } = new();

        public TelegramRuntimeState LoadState()
        {
            lock (_gate)
            {
                try
                {
                    if (File.Exists(TelegramPaths.StateFile))
                    {
                        var json = File.ReadAllText(TelegramPaths.StateFile);
                        return JsonConvert.DeserializeObject<TelegramRuntimeState>(json) ?? new TelegramRuntimeState();
                    }
                }
                catch
                {
                    // Keep a usable empty state if the file is corrupt.
                }

                return new TelegramRuntimeState();
            }
        }

        public void SaveState(TelegramRuntimeState state)
        {
            lock (_gate)
            {
                try
                {
                    File.WriteAllText(
                        TelegramPaths.StateFile,
                        JsonConvert.SerializeObject(state ?? new TelegramRuntimeState(), Formatting.Indented));
                }
                catch
                {
                }
            }
        }

        public string GetActiveProjectPath()
        {
            var state = LoadState();
            if (!string.IsNullOrWhiteSpace(state.ActiveProjectPath))
            {
                return state.ActiveProjectPath;
            }

            var config = new ConfigurationService().LoadGlobalConfig();
            if (!string.IsNullOrWhiteSpace(config.TelegramActiveProjectPath))
            {
                return config.TelegramActiveProjectPath;
            }

            return string.IsNullOrWhiteSpace(config.LastProjectPath)
                ? TelegramPaths.UnassignedPath
                : config.LastProjectPath;
        }

        public void SetActiveProjectPath(string projectPath)
        {
            var state = LoadState();
            state.ActiveProjectPath = string.IsNullOrWhiteSpace(projectPath)
                ? TelegramPaths.UnassignedPath
                : projectPath.Trim();
            SaveState(state);

            new ConfigurationService().UpdateGlobalConfig(cfg =>
            {
                cfg.TelegramActiveProjectPath = state.ActiveProjectPath;
            });
        }

        public long GetLastChatId() => LoadState().LastChatId;

        public void SetLastChatId(long chatId)
        {
            if (chatId == 0)
            {
                return;
            }

            var state = LoadState();
            state.LastChatId = chatId;
            SaveState(state);
        }

        public long GetLastUpdateId() => LoadState().LastUpdateId;

        public void SetLastUpdateId(long updateId)
        {
            var state = LoadState();
            state.LastUpdateId = updateId;
            SaveState(state);
        }

        public TelegramThread LoadThread(string projectPath)
        {
            lock (_gate)
            {
                var path = TelegramPaths.ThreadFile(projectPath);
                try
                {
                    if (File.Exists(path))
                    {
                        var json = File.ReadAllText(path);
                        var thread = JsonConvert.DeserializeObject<TelegramThread>(json);
                        if (thread != null)
                        {
                            thread.Messages ??= new List<TelegramChatMessage>();
                            thread.TrackedBotMessageIds ??= new List<long>();
                            thread.ProjectPath = string.IsNullOrWhiteSpace(thread.ProjectPath)
                                ? projectPath
                                : thread.ProjectPath;
                            thread.DisplayName = string.IsNullOrWhiteSpace(thread.DisplayName)
                                ? TelegramPaths.DisplayName(thread.ProjectPath)
                                : thread.DisplayName;
                            PruneChromeLocked(path, thread);
                            return thread;
                        }
                    }
                }
                catch
                {
                }

                return new TelegramThread
                {
                    ProjectPath = TelegramPaths.IsUnassigned(projectPath) ? TelegramPaths.UnassignedPath : projectPath,
                    DisplayName = TelegramPaths.DisplayName(projectPath),
                    Messages = new List<TelegramChatMessage>()
                };
            }
        }

        public void SaveThread(TelegramThread thread)
        {
            if (thread == null)
            {
                return;
            }

            lock (_gate)
            {
                try
                {
                    thread.Messages ??= new List<TelegramChatMessage>();
                    if (thread.Messages.Count > MaxMessagesPerThread)
                    {
                        thread.Messages = thread.Messages
                            .Skip(thread.Messages.Count - MaxMessagesPerThread)
                            .ToList();
                    }

                    File.WriteAllText(
                        TelegramPaths.ThreadFile(thread.ProjectPath),
                        JsonConvert.SerializeObject(thread, Formatting.Indented));
                }
                catch
                {
                }
            }
        }

        public TelegramChatMessage Append(string projectPath, TelegramChatMessage message)
        {
            message ??= new TelegramChatMessage();
            if (TelegramChatChrome.IsNoise(message))
            {
                return message;
            }

            if (string.IsNullOrWhiteSpace(message.Id))
            {
                message.Id = Guid.NewGuid().ToString("N");
            }

            if (message.Utc == default)
            {
                message.Utc = DateTime.UtcNow;
            }

            var thread = LoadThread(projectPath);
            thread.Messages.Add(message);
            SaveThread(thread);
            return message;
        }

        public void MarkRead(string projectPath)
        {
            var thread = LoadThread(projectPath);
            thread.LastReadUtc = DateTime.UtcNow;
            SaveThread(thread);
        }

        public string GetCursorSessionId(string projectPath)
        {
            return GetCliSessionId(projectPath);
        }

        public string GetCliSessionId(string projectPath)
        {
            var thread = LoadThread(projectPath);
            if (!string.IsNullOrWhiteSpace(thread.CursorCliSessionId))
            {
                return thread.CursorCliSessionId.Trim();
            }

            // Legacy field was used for -p --resume before ACP split.
            return thread.CursorSessionId ?? string.Empty;
        }

        public string GetAcpSessionId(string projectPath)
        {
            return LoadThread(projectPath).CursorAcpSessionId ?? string.Empty;
        }

        public void SetCursorSessionId(string projectPath, string? sessionId)
        {
            SetCliSessionId(projectPath, sessionId);
        }

        public void SetCliSessionId(string projectPath, string? sessionId)
        {
            var thread = LoadThread(projectPath);
            var value = (sessionId ?? string.Empty).Trim();
            thread.CursorCliSessionId = value;
            thread.CursorSessionId = value; // keep legacy field in sync for older readers
            SaveThread(thread);
        }

        public void SetAcpSessionId(string projectPath, string? sessionId)
        {
            var thread = LoadThread(projectPath);
            thread.CursorAcpSessionId = (sessionId ?? string.Empty).Trim();
            SaveThread(thread);
        }

        public void ClearAllCursorSessions(string projectPath)
        {
            var thread = LoadThread(projectPath);
            thread.CursorSessionId = string.Empty;
            thread.CursorCliSessionId = string.Empty;
            thread.CursorAcpSessionId = string.Empty;
            SaveThread(thread);
        }

        public string GetCodexSessionId(string projectPath)
        {
            return LoadThread(projectPath).CodexSessionId ?? string.Empty;
        }

        public void SetCodexSessionId(string projectPath, string? sessionId)
        {
            var thread = LoadThread(projectPath);
            thread.CodexSessionId = (sessionId ?? string.Empty).Trim();
            SaveThread(thread);
        }

        public void ClearCodexSession(string projectPath)
        {
            SetCodexSessionId(projectPath, string.Empty);
        }

        public string GetCursorAgentMode(string projectPath)
        {
            var mode = (LoadThread(projectPath).CursorAgentMode ?? string.Empty).Trim().ToLowerInvariant();
            return mode == "plan" ? "plan" : "agent";
        }

        public bool IsCursorPlanMode(string projectPath)
            => GetCursorAgentMode(projectPath) == "plan";

        public void SetCursorAgentMode(string projectPath, string? mode)
        {
            var thread = LoadThread(projectPath);
            var next = string.Equals((mode ?? string.Empty).Trim(), "plan", StringComparison.OrdinalIgnoreCase)
                ? "plan"
                : "agent";
            thread.CursorAgentMode = next;
            SaveThread(thread);
        }

        public void RecordCursorUsage(string projectPath, AgentTokenUsage usage)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || usage == null || !usage.Available)
            {
                return;
            }

            var thread = LoadThread(projectPath);
            thread.LastUsageInputTokens = usage.InputTokens;
            thread.LastUsageOutputTokens = usage.OutputTokens;
            thread.LastUsageCacheReadTokens = usage.CacheReadTokens;
            thread.LastUsageCacheWriteTokens = usage.CacheWriteTokens;
            thread.LastUsageSource = usage.Source ?? string.Empty;
            thread.LastUsageUtc = DateTime.UtcNow;
            thread.SessionUsageInputTokens += usage.InputTokens;
            thread.SessionUsageOutputTokens += usage.OutputTokens;
            thread.SessionUsageCacheReadTokens += usage.CacheReadTokens;
            thread.SessionUsageCacheWriteTokens += usage.CacheWriteTokens;
            SaveThread(thread);
        }

        public void ResetSessionUsage(string projectPath)
        {
            var thread = LoadThread(projectPath);
            thread.SessionUsageInputTokens = 0;
            thread.SessionUsageOutputTokens = 0;
            thread.SessionUsageCacheReadTokens = 0;
            thread.SessionUsageCacheWriteTokens = 0;
            thread.LastUsageInputTokens = 0;
            thread.LastUsageOutputTokens = 0;
            thread.LastUsageCacheReadTokens = 0;
            thread.LastUsageCacheWriteTokens = 0;
            thread.LastUsageSource = string.Empty;
            thread.LastUsageUtc = null;
            SaveThread(thread);
        }

        public IReadOnlyList<TelegramThreadSummary> ListThreads(IEnumerable<string>? recentProjectPaths)
        {
            var byPath = new Dictionary<string, TelegramThreadSummary>(StringComparer.OrdinalIgnoreCase);

            void Upsert(string projectPath, bool fromFile)
            {
                if (string.IsNullOrWhiteSpace(projectPath))
                {
                    return;
                }

                var key = TelegramPaths.IsUnassigned(projectPath)
                    ? TelegramPaths.UnassignedPath
                    : NormalizePath(projectPath);
                if (byPath.ContainsKey(key) && !fromFile)
                {
                    return;
                }

                var thread = LoadThread(key);
                var last = thread.Messages.LastOrDefault();
                var unread = thread.Messages.Count(m =>
                    m.Direction == TelegramMessageDirection.Incoming
                    && (!thread.LastReadUtc.HasValue || m.Utc > thread.LastReadUtc.Value));

                byPath[key] = new TelegramThreadSummary
                {
                    ProjectPath = key,
                    DisplayName = thread.DisplayName,
                    LastPreview = BuildPreview(last),
                    LastMessageUtc = last?.Utc,
                    UnreadCount = unread,
                    HasThreadFile = fromFile || File.Exists(TelegramPaths.ThreadFile(key))
                };
            }

            foreach (var recent in recentProjectPaths ?? Array.Empty<string>())
            {
                Upsert(recent, fromFile: false);
            }

            try
            {
                foreach (var file in Directory.GetFiles(TelegramPaths.ThreadsFolder, "*.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var thread = JsonConvert.DeserializeObject<TelegramThread>(json);
                        if (thread != null && !string.IsNullOrWhiteSpace(thread.ProjectPath))
                        {
                            Upsert(thread.ProjectPath, fromFile: true);
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

            return byPath.Values
                .OrderByDescending(t => t.LastMessageUtc ?? DateTime.MinValue)
                .ThenBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string BuildPreview(TelegramChatMessage? message)
        {
            if (message == null)
            {
                return Loc.T("telegram.noMessages");
            }

            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                var text = message.Text.Replace("\r", " ").Replace("\n", " ").Trim();
                return text.Length <= 80 ? text : text[..77] + "...";
            }

            if (!string.IsNullOrWhiteSpace(message.PhotoPath)
                || (!string.IsNullOrWhiteSpace(message.TelegramFileId)
                    && string.IsNullOrWhiteSpace(message.AttachmentPath)))
            {
                return Loc.T("telegram.photo");
            }

            if (!string.IsNullOrWhiteSpace(message.AttachmentPath)
                || !string.IsNullOrWhiteSpace(message.AttachmentName))
            {
                var name = string.IsNullOrWhiteSpace(message.AttachmentName)
                    ? Path.GetFileName(message.AttachmentPath)
                    : message.AttachmentName;
                return Loc.T("telegram.document", name);
            }

            return Loc.T("telegram.noMessages");
        }

        public void TrackBotMessageId(string projectPath, long messageId)
        {
            if (messageId <= 0 || string.IsNullOrWhiteSpace(projectPath))
            {
                return;
            }

            lock (_gate)
            {
                var thread = LoadThreadUnlocked(projectPath);
                thread.TrackedBotMessageIds ??= new List<long>();
                if (!thread.TrackedBotMessageIds.Contains(messageId))
                {
                    thread.TrackedBotMessageIds.Add(messageId);
                }

                const int maxTracked = 800;
                if (thread.TrackedBotMessageIds.Count > maxTracked)
                {
                    thread.TrackedBotMessageIds = thread.TrackedBotMessageIds
                        .Skip(thread.TrackedBotMessageIds.Count - maxTracked)
                        .ToList();
                }

                SaveThreadUnlocked(thread);
            }
        }

        public IReadOnlyList<long> CollectDeletableTelegramMessageIds(string projectPath)
        {
            var thread = LoadThread(projectPath);
            var ids = new HashSet<long>();
            foreach (var id in thread.TrackedBotMessageIds ?? new List<long>())
            {
                if (id > 0)
                {
                    ids.Add(id);
                }
            }

            foreach (var message in thread.Messages ?? new List<TelegramChatMessage>())
            {
                if (message.TelegramMessageId > 0)
                {
                    ids.Add(message.TelegramMessageId);
                }
            }

            return ids.OrderBy(x => x).ToList();
        }

        /// <summary>Clears local project chat history (keeps Cursor session).</summary>
        public int ClearLocalChat(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                return 0;
            }

            lock (_gate)
            {
                var thread = LoadThreadUnlocked(projectPath);
                var count = thread.Messages?.Count ?? 0;
                var keepCli = !string.IsNullOrWhiteSpace(thread.CursorCliSessionId)
                    ? thread.CursorCliSessionId
                    : (thread.CursorSessionId ?? string.Empty);
                var keepAcp = thread.CursorAcpSessionId ?? string.Empty;
                thread.Messages = new List<TelegramChatMessage>();
                thread.TrackedBotMessageIds = new List<long>();
                thread.CursorSessionId = keepCli;
                thread.CursorCliSessionId = keepCli;
                thread.CursorAcpSessionId = keepAcp; // never drop warm Cursor resume across chat clear
                thread.LastReadUtc = DateTime.UtcNow;
                // Usage session totals reset with local clear (per plan).
                thread.SessionUsageInputTokens = 0;
                thread.SessionUsageOutputTokens = 0;
                thread.SessionUsageCacheReadTokens = 0;
                thread.SessionUsageCacheWriteTokens = 0;
                thread.LastUsageInputTokens = 0;
                thread.LastUsageOutputTokens = 0;
                thread.LastUsageCacheReadTokens = 0;
                thread.LastUsageCacheWriteTokens = 0;
                thread.LastUsageSource = string.Empty;
                thread.LastUsageUtc = null;
                SaveThreadUnlocked(thread);
                TryClearMediaFolder(projectPath);
                return count;
            }
        }

        private TelegramThread LoadThreadUnlocked(string projectPath)
        {
            var path = TelegramPaths.ThreadFile(projectPath);
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var thread = JsonConvert.DeserializeObject<TelegramThread>(json);
                    if (thread != null)
                    {
                        thread.Messages ??= new List<TelegramChatMessage>();
                        thread.TrackedBotMessageIds ??= new List<long>();
                        thread.ProjectPath = string.IsNullOrWhiteSpace(thread.ProjectPath)
                            ? projectPath
                            : thread.ProjectPath;
                        thread.DisplayName = string.IsNullOrWhiteSpace(thread.DisplayName)
                            ? TelegramPaths.DisplayName(thread.ProjectPath)
                            : thread.DisplayName;
                        return thread;
                    }
                }
            }
            catch
            {
            }

            return new TelegramThread
            {
                ProjectPath = TelegramPaths.IsUnassigned(projectPath) ? TelegramPaths.UnassignedPath : projectPath,
                DisplayName = TelegramPaths.DisplayName(projectPath),
                Messages = new List<TelegramChatMessage>(),
                TrackedBotMessageIds = new List<long>()
            };
        }

        private void SaveThreadUnlocked(TelegramThread thread)
        {
            try
            {
                thread.Messages ??= new List<TelegramChatMessage>();
                thread.TrackedBotMessageIds ??= new List<long>();
                if (thread.Messages.Count > MaxMessagesPerThread)
                {
                    thread.Messages = thread.Messages
                        .Skip(thread.Messages.Count - MaxMessagesPerThread)
                        .ToList();
                }

                File.WriteAllText(
                    TelegramPaths.ThreadFile(thread.ProjectPath),
                    JsonConvert.SerializeObject(thread, Formatting.Indented));
            }
            catch
            {
            }
        }

        private static void TryClearMediaFolder(string projectPath)
        {
            try
            {
                var folder = TelegramPaths.MediaFolder(projectPath);
                if (!Directory.Exists(folder))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    try
                    {
                        File.Delete(file);
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

        private static void PruneChromeLocked(string threadFile, TelegramThread thread)
        {
            var before = thread.Messages.Count;
            thread.Messages = thread.Messages.FindAll(m => !TelegramChatChrome.IsNoise(m));
            if (thread.Messages.Count == before)
            {
                return;
            }

            try
            {
                File.WriteAllText(
                    threadFile,
                    JsonConvert.SerializeObject(thread, Formatting.Indented));
            }
            catch
            {
            }
        }

        private static string NormalizePath(string projectPath)
        {
            try
            {
                return Path.GetFullPath(projectPath.Trim());
            }
            catch
            {
                return projectPath.Trim();
            }
        }
    }
}
