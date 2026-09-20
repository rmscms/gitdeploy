using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Services;
using GitDeployPro.Services.Telegram;

namespace GitDeployPro.Services.Cursor
{
    /// <summary>
    /// Once-per-day Cursor maintenance while GitDeployPro is running:
    /// old chats + orphan blobs + optional VACUUM + Safe disk/cache/backup clear.
    /// First enable waits until the next scheduled clock time (does not run immediately).
    /// </summary>
    public sealed class CursorAutoCleanupRunner : IDisposable
    {
        private readonly ConfigurationService _config = new();
        private readonly System.Threading.Timer _timer;
        private readonly object _gate = new();
        private bool _disposed;
        private bool _running;

        public static CursorAutoCleanupRunner Instance { get; } = new();

        public event Action? StatusChanged;

        private CursorAutoCleanupRunner()
        {
            _timer = new System.Threading.Timer(
                _ => _ = CheckAsync(),
                null,
                Timeout.Infinite,
                Timeout.Infinite);
        }

        public void Start()
        {
            // Quiet first poll — never fire a cleanup just because the app started.
            _timer.Change(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5));
        }

        /// <summary>Reschedule the timer only; does not force an immediate cleanup.</summary>
        public void NudgeTimer()
        {
            _timer.Change(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
        }

        public async Task<(bool Ok, string Message)> RunNowAsync(bool forceQuitCursor = false)
        {
            return await RunInternalAsync(manual: true, forceQuitOverride: forceQuitCursor)
                .ConfigureAwait(false);
        }

        private async Task CheckAsync()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var cfg = _config.LoadGlobalConfig();
                if (!cfg.CursorAutoCleanEnabled)
                {
                    return;
                }

                if (!IsDue(cfg))
                {
                    return;
                }

                await RunInternalAsync(manual: false, forceQuitOverride: null).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[CursorAutoCleanupRunner] check error: " + ex);
            }
        }

        private async Task<(bool Ok, string Message)> RunInternalAsync(bool manual, bool? forceQuitOverride)
        {
            lock (_gate)
            {
                if (_running)
                {
                    return (false, "Cursor auto-clean already running.");
                }

                _running = true;
            }

            try
            {
                var cfg = _config.LoadGlobalConfig();
                var days = Math.Clamp(cfg.CursorAutoCleanOlderThanDays <= 0 ? 30 : cfg.CursorAutoCleanOlderThanDays, 1, 3650);
                var purge = cfg.CursorAutoCleanPurgeOrphans;
                var vacuum = cfg.CursorAutoCleanVacuum;
                var disk = cfg.CursorAutoCleanDiskCache;
                var forceQuit = forceQuitOverride ?? cfg.CursorAutoCleanForceQuit;

                var running = CursorDiskCleanupService.GetCursorProcessNames();
                if (running.Count > 0 && !forceQuit)
                {
                    var skip = "Skipped: Cursor is running (" + string.Join(", ", running) + "). Will retry later.";
                    if (manual)
                    {
                        return (false, skip);
                    }

                    PersistResult(skip, markRun: false);
                    return (false, skip);
                }

                var sw = Stopwatch.StartNew();
                var parts = new List<string>();

                var chat = await CursorChatCleanupService
                    .DeleteOlderAsync(
                        days,
                        workspaceIds: Array.Empty<string>(),
                        vacuum: vacuum,
                        forceQuitCursor: forceQuit,
                        purgeOrphanBlobs: purge)
                    .ConfigureAwait(false);

                parts.Add(chat.Ok
                    ? $"chats: {chat.DeletedChats} deleted, KV {chat.DeletedKvRows}, orphans {chat.DeletedOrphanBlobs}"
                    : "chats failed: " + chat.Message);

                if (disk)
                {
                    var clear = await CursorDiskCleanupService
                        .ClearAsync(CursorCleanupProfile.Safe, forceQuit)
                        .ConfigureAwait(false);
                    parts.Add(clear.Ok
                        ? $"disk: freed {CursorDiskCleanupService.FormatBytes(clear.FreedBytes)}"
                        : "disk failed: " + clear.Message);
                }

                var ok = chat.Ok;
                var msg = ok
                    ? $"OK in {sw.Elapsed.TotalSeconds:0}s · older>{days}d · " + string.Join(" · ", parts)
                    : $"Partial/failed in {sw.Elapsed.TotalSeconds:0}s · " + string.Join(" · ", parts);

                PersistResult(msg, markRun: true);
                TryNotifyTelegram(ok, msg, days, manual);
                return (ok, msg);
            }
            catch (Exception ex)
            {
                var fail = "Auto-clean error: " + ex.Message;
                try
                {
                    PersistResult(fail, markRun: manual);
                    TryNotifyTelegram(false, fail, 0, manual);
                }
                catch
                {
                }

                return (false, fail);
            }
            finally
            {
                lock (_gate)
                {
                    _running = false;
                }

                try
                {
                    StatusChanged?.Invoke();
                }
                catch
                {
                }
            }
        }

        private void PersistResult(string message, bool markRun)
        {
            _config.UpdateGlobalConfig(c =>
            {
                if (markRun)
                {
                    c.CursorAutoCleanLastRunUtc = DateTime.UtcNow;
                }

                c.CursorAutoCleanLastResult = Truncate(message, 500);
            });
        }

        private static void TryNotifyTelegram(bool ok, string message, int days, bool manual)
        {
            try
            {
                var cfg = new ConfigurationService().LoadGlobalConfig();
                if (!cfg.TelegramEnabled)
                {
                    return;
                }

                var token = EncryptionService.Decrypt(cfg.TelegramBotToken);
                if (string.IsNullOrWhiteSpace(token))
                {
                    return;
                }

                var chatIds = new HashSet<long>();
                foreach (var part in (cfg.TelegramAllowedUserIds ?? "")
                             .Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (long.TryParse(part.Trim(), out var id) && id != 0)
                    {
                        chatIds.Add(id);
                    }
                }

                var last = TelegramChatStore.Instance.GetLastChatId();
                if (last != 0)
                {
                    chatIds.Add(last);
                }

                if (chatIds.Count == 0)
                {
                    return;
                }

                var title = ok ? "✅ Cursor auto-clean done" : "⚠️ Cursor auto-clean issue";
                if (manual)
                {
                    title += " (manual)";
                }

                var body = title + "\n"
                    + (days > 0 ? $"Older than: {days}d\n" : "")
                    + message;

                foreach (var chatId in chatIds)
                {
                    TelegramOutboundQueue.Instance.EnqueueText(
                        token,
                        chatId,
                        cfg.TelegramActiveProjectPath ?? "",
                        body);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[CursorAutoCleanupRunner] telegram notify: " + ex.Message);
            }
        }

        /// <summary>
        /// Due only at/after the next scheduled clock time since last successful run.
        /// Never due immediately on first enable (waits until next 03:30 etc.).
        /// </summary>
        public static bool IsDue(ConfigurationService.GlobalConfig cfg)
        {
            if (!cfg.CursorAutoCleanEnabled)
            {
                return false;
            }

            return DateTime.Now >= GetNextRunLocal(cfg);
        }

        /// <summary>Next local run instant (for UI).</summary>
        public static DateTime GetNextRunLocal(ConfigurationService.GlobalConfig cfg)
        {
            var scheduled = ParseTime(cfg.CursorAutoCleanTimeLocal) ?? new TimeSpan(3, 30, 0);
            var now = DateTime.Now;

            if (cfg.CursorAutoCleanLastRunUtc is DateTime lastUtc)
            {
                var lastLocal = lastUtc.ToLocalTime();
                // First slot strictly after last run.
                var day = lastLocal.Date;
                var slot = day + scheduled;
                if (lastLocal >= slot)
                {
                    slot = day.AddDays(1) + scheduled;
                }

                return slot;
            }

            // Never ran: do NOT fire for "today after 03:30" when enabling at evening —
            // wait until the next upcoming clock time.
            var todaySlot = now.Date + scheduled;
            if (now < todaySlot)
            {
                return todaySlot;
            }

            return todaySlot.AddDays(1);
        }

        [Obsolete("Use IsDue")]
        public static bool ShouldRunToday(ConfigurationService.GlobalConfig cfg) => IsDue(cfg);

        public static TimeSpan? ParseTime(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (TimeSpan.TryParseExact(value.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var t)
                || TimeSpan.TryParseExact(value.Trim(), @"h\:mm", CultureInfo.InvariantCulture, out t)
                || TimeSpan.TryParse(value.Trim(), CultureInfo.InvariantCulture, out t))
            {
                if (t >= TimeSpan.Zero && t < TimeSpan.FromDays(1))
                {
                    return t;
                }
            }

            return null;
        }

        private static string Truncate(string value, int max)
            => string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Dispose();
        }
    }
}
