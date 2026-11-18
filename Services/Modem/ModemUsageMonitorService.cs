using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Services.Localization;
using GitDeployPro.Services.Telegram;

namespace GitDeployPro.Services.Modem
{
    public sealed class ModemSnapshot
    {
        public bool Enabled { get; init; }
        public bool HasData { get; init; }
        public string Error { get; init; } = "";
        public string DriverName { get; init; } = "";
        public DateTime SampledLocal { get; init; }
        public long UsedBytes { get; init; }
        public long DownloadBytes { get; init; }
        public long UploadBytes { get; init; }
        public long PlanBytes { get; init; }
        public long DownRate { get; init; }
        public long UpRate { get; init; }
        public long ConnectedSeconds { get; init; }

        /// <summary>0–100+, or -1 when no plan size is known.</summary>
        public double Percent { get; init; } = -1;

        public long RemainingBytes => PlanBytes > 0 ? Math.Max(0, PlanBytes - UsedBytes) : 0;

        /// <summary>Estimated days until the plan runs out at this cycle's average pace (-1 unknown).</summary>
        public double DaysLeft { get; init; } = -1;

        /// <summary>Percent reported by the operator's own SMS (-1 none).</summary>
        public double OperatorPercent { get; init; } = -1;
        public double OperatorRemainingGb { get; init; } = -1;
        public DateTime OperatorSmsDate { get; init; }
        public string SmsError { get; init; } = "";

        public IReadOnlyList<ModemDayUsage> Days { get; init; } = Array.Empty<ModemDayUsage>();
        public IReadOnlyList<ModemRatePoint> Rates { get; init; } = Array.Empty<ModemRatePoint>();
    }

    public sealed record ModemDayUsage(DateTime Date, long Bytes);

    public sealed record ModemRatePoint(long Down, long Up);

    /// <summary>
    /// Polls the configured modem driver, keeps short history, and raises threshold alerts
    /// (Telegram + toast) once per billing cycle.
    /// </summary>
    public sealed class ModemUsageMonitorService : IDisposable
    {
        public static ModemUsageMonitorService Instance { get; } = new();

        private const int MaxRatePoints = 60;
        private const int KeepDays = 45;

        private readonly ConfigurationService _config = new();
        private readonly object _gate = new();
        private readonly List<ModemRatePoint> _rates = new();
        private readonly string _historyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GitDeployPro",
            "modem-usage.json");

        private Dictionary<string, DayRecord> _history = new();
        private DateTime _lastHistorySaveUtc = DateTime.MinValue;
        private CancellationTokenSource? _cts;
        private ModemSnapshot _latest = new() { Enabled = false };
        private bool _disposed;

        public event EventHandler<ModemSnapshot>? SnapshotChanged;

        public ModemSnapshot Latest
        {
            get
            {
                lock (_gate)
                {
                    return _latest;
                }
            }
        }

        private sealed class DayRecord
        {
            public long Used { get; set; }
            public string Cycle { get; set; } = "";
        }

        public void StartFromConfig()
        {
            if (_disposed)
            {
                return;
            }

            StopLoop();
            _smsLoginFailed = false;
            var cfg = _config.LoadGlobalConfig();
            if (!cfg.ModemEnabled)
            {
                Publish(new ModemSnapshot { Enabled = false });
                return;
            }

            LoadHistory();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _ = Task.Run(() => RunAsync(token));
        }

        /// <summary>
        /// Starts counting from zero now (e.g. after buying a new package while the modem counter still holds old usage).
        /// Also re-arms the threshold alerts.
        /// </summary>
        public async Task<ModemSnapshot> ResetUsageAsync(CancellationToken cancellationToken = default)
        {
            var cfg = _config.LoadGlobalConfig();
            var usage = await ModemDriverFactory.Create(cfg.ModemDriver).GetUsageAsync(BuildConnection(), cancellationToken).ConfigureAwait(false);
            if (!usage.Success)
            {
                throw new InvalidOperationException(usage.Error);
            }

            var counter = usage.MonthDownloadBytes + (cfg.ModemCountUpload ? usage.MonthUploadBytes : 0);
            _config.UpdateGlobalConfig(c =>
            {
                c.ModemBaselineBytes = counter;
                c.ModemBaselineCycle = usage.CycleKey;
                c.ModemLastAlertCycle = usage.CycleKey;
                c.ModemLastAlertPercent = 0;
            });
            return await PollNowAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Immediate poll (used by the dashboard Refresh button and Settings "Test").</summary>
        public async Task<ModemSnapshot> PollNowAsync(CancellationToken cancellationToken = default)
        {
            var cfg = _config.LoadGlobalConfig();
            return await PollOnceAsync(cfg, cancellationToken, force: true).ConfigureAwait(false);
        }

        private async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var seconds = 60;
                try
                {
                    var cfg = _config.LoadGlobalConfig();
                    seconds = Math.Clamp(cfg.ModemPollSeconds <= 0 ? 60 : cfg.ModemPollSeconds, 15, 3600);
                    if (cfg.ModemEnabled)
                    {
                        await PollOnceAsync(cfg, ct, force: false).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ModemUsageMonitor] " + ex.Message);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private async Task<ModemSnapshot> PollOnceAsync(ConfigurationService.GlobalConfig cfg, CancellationToken ct, bool force)
        {
            var driver = ModemDriverFactory.Create(cfg.ModemDriver);
            var usage = await driver.GetUsageAsync(new ModemConnection
            {
                BaseUrl = cfg.ModemBaseUrl,
                Username = cfg.ModemUsername,
                Password = EncryptionService.Decrypt(cfg.ModemPassword ?? "")
            }, ct).ConfigureAwait(false);

            if (!usage.Success)
            {
                var prev = Latest;
                var failed = new ModemSnapshot
                {
                    Enabled = cfg.ModemEnabled,
                    HasData = prev.HasData,
                    Error = usage.Error,
                    DriverName = driver.DisplayName,
                    SampledLocal = prev.SampledLocal,
                    UsedBytes = prev.UsedBytes,
                    DownloadBytes = prev.DownloadBytes,
                    UploadBytes = prev.UploadBytes,
                    PlanBytes = prev.PlanBytes,
                    Percent = prev.Percent,
                    DaysLeft = prev.DaysLeft,
                    OperatorPercent = prev.OperatorPercent,
                    OperatorRemainingGb = prev.OperatorRemainingGb,
                    OperatorSmsDate = prev.OperatorSmsDate,
                    SmsError = prev.SmsError,
                    Days = prev.Days,
                    Rates = prev.Rates
                };
                Publish(failed);
                return failed;
            }

            var counter = usage.MonthDownloadBytes + (cfg.ModemCountUpload ? usage.MonthUploadBytes : 0);
            var baseline = string.Equals(cfg.ModemBaselineCycle, usage.CycleKey, StringComparison.Ordinal) ? cfg.ModemBaselineBytes : 0;
            var used = Math.Max(0, counter - baseline);
            var plan = cfg.ModemPlanGb > 0
                ? (long)(cfg.ModemPlanGb * 1024d * 1024d * 1024d)
                : usage.DeviceLimitBytes;
            var percent = plan > 0 ? used * 100.0 / plan : -1;

            List<ModemRatePoint> rates;
            lock (_gate)
            {
                _rates.Add(new ModemRatePoint(usage.DownloadRateBytesPerSec, usage.UploadRateBytesPerSec));
                while (_rates.Count > MaxRatePoints)
                {
                    _rates.RemoveAt(0);
                }

                rates = _rates.ToList();
            }

            await PollSmsAsync(cfg, driver, ct, force).ConfigureAwait(false);
            var days = UpdateHistory(usage, used);
            var snapshot = new ModemSnapshot
            {
                Enabled = true,
                HasData = true,
                DriverName = driver.DisplayName,
                SampledLocal = usage.SampledLocal,
                UsedBytes = used,
                DownloadBytes = usage.MonthDownloadBytes,
                UploadBytes = usage.MonthUploadBytes,
                PlanBytes = plan,
                DownRate = usage.DownloadRateBytesPerSec,
                UpRate = usage.UploadRateBytesPerSec,
                ConnectedSeconds = usage.ConnectedSeconds,
                Percent = percent,
                DaysLeft = EstimateDaysLeft(usage.CycleKey, used, plan),
                OperatorPercent = _operatorPercent,
                OperatorRemainingGb = _operatorRemainingGb,
                OperatorSmsDate = _operatorSmsDate,
                SmsError = _smsError,
                Days = days,
                Rates = rates
            };

            Publish(snapshot);
            try
            {
                CheckAlerts(cfg, usage.CycleKey, snapshot);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ModemUsageMonitor] alert: " + ex.Message);
            }

            return snapshot;
        }

        // ---- operator SMS ---------------------------------------------------------------

        private double _operatorPercent = -1;
        private double _operatorRemainingGb = -1;
        private DateTime _operatorSmsDate;
        private string _smsError = "";
        private DateTime _lastSmsPollUtc = DateTime.MinValue;

        /// <summary>Set after a failed modem login: stops automatic retries (they lock the modem) until settings are saved again.</summary>
        private volatile bool _smsLoginFailed;

        private async Task PollSmsAsync(ConfigurationService.GlobalConfig cfg, IModemDriver driver, CancellationToken ct, bool force)
        {
            if (!cfg.ModemReadSms || string.IsNullOrEmpty(cfg.ModemPassword))
            {
                _smsError = "";
                return;
            }

            if (!force && _smsLoginFailed)
            {
                return;
            }

            // Login per read is heavy for the modem: at most every 3 minutes unless forced.
            if (!force && (DateTime.UtcNow - _lastSmsPollUtc).TotalMinutes < 3)
            {
                return;
            }

            _lastSmsPollUtc = DateTime.UtcNow;
            try
            {
                var list = await driver.GetSmsAsync(new ModemConnection
                {
                    BaseUrl = cfg.ModemBaseUrl,
                    Username = cfg.ModemUsername,
                    Password = EncryptionService.Decrypt(cfg.ModemPassword)
                }, 30, ct).ConfigureAwait(false);
                _smsError = "";

                DateTime.TryParse(cfg.ModemSmsLastDate, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastDate);
                var firstRun = string.IsNullOrEmpty(cfg.ModemSmsLastDate);
                var newest = lastDate;

                // Newest usage SMS decides the displayed operator percent.
                var latestUsage = list.FirstOrDefault(m => OperatorSmsParser.TryParseUsagePercent(m.Text, out _));
                if (latestUsage != null && OperatorSmsParser.TryParseUsagePercent(latestUsage.Text, out var shown))
                {
                    _operatorPercent = shown;
                    _operatorSmsDate = latestUsage.Date;
                    _operatorRemainingGb = OperatorSmsParser.TryParseRemainingGb(latestUsage.Text, out var rem) ? rem : -1;
                }

                foreach (var m in list.OrderBy(x => x.Date))
                {
                    if (m.Date > newest)
                    {
                        newest = m.Date;
                    }

                    if (firstRun || m.Date <= lastDate)
                    {
                        continue;
                    }

                    if (OperatorSmsParser.TryParsePackage(m.Text, out var pkg))
                    {
                        if (TryApplyPackage(m, pkg, out var total))
                        {
                            NotifyPackageAdded(cfg, pkg, total);
                        }
                    }
                    else if (OperatorSmsParser.TryParseUsagePercent(m.Text, out var pct))
                    {
                        NotifyOperatorSms(cfg, pct, m);
                    }
                }

                if (newest > lastDate || firstRun)
                {
                    var iso = (newest == default ? DateTime.Now : newest).ToString("o", CultureInfo.InvariantCulture);
                    _config.UpdateGlobalConfig(c => c.ModemSmsLastDate = iso);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _smsError = ex.Message;
                if (ex.Message.Contains("login", StringComparison.OrdinalIgnoreCase))
                {
                    _smsLoginFailed = true;
                }
            }
        }

        public IModemDriver Driver => ModemDriverFactory.Create(_config.LoadGlobalConfig().ModemDriver);

        public ModemConnection BuildConnection()
        {
            var cfg = _config.LoadGlobalConfig();
            return new ModemConnection
            {
                BaseUrl = cfg.ModemBaseUrl,
                Username = cfg.ModemUsername,
                Password = EncryptionService.Decrypt(cfg.ModemPassword ?? "")
            };
        }

        /// <summary>Recent inbox for the Settings "Show SMS" button.</summary>
        public async Task<IReadOnlyList<ModemSms>> ReadInboxAsync(int count, CancellationToken ct = default)
        {
            var cfg = _config.LoadGlobalConfig();
            var driver = ModemDriverFactory.Create(cfg.ModemDriver);
            return await driver.GetSmsAsync(new ModemConnection
            {
                BaseUrl = cfg.ModemBaseUrl,
                Username = cfg.ModemUsername,
                Password = EncryptionService.Decrypt(cfg.ModemPassword ?? "")
            }, count, ct).ConfigureAwait(false);
        }

        /// <summary>Adds a purchased package to the plan size once (dedupes the duplicate activation SMS).</summary>
        public bool TryApplyPackage(ModemSms sms, PackageInfo pkg, out double newPlanGb)
        {
            var key = sms.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|"
                      + pkg.Gb.ToString("0.##", CultureInfo.InvariantCulture) + "|" + pkg.Days.ToString(CultureInfo.InvariantCulture);
            var total = 0.0;
            var added = false;
            _config.UpdateGlobalConfig(c =>
            {
                c.ModemAppliedPackages ??= new List<string>();
                if (c.ModemAppliedPackages.Contains(key))
                {
                    total = c.ModemPlanGb;
                    return;
                }

                c.ModemAppliedPackages.Add(key);
                while (c.ModemAppliedPackages.Count > 50)
                {
                    c.ModemAppliedPackages.RemoveAt(0);
                }

                c.ModemPlanGb += pkg.Gb;
                total = c.ModemPlanGb;
                added = true;
            });
            newPlanGb = total;
            return added;
        }

        public bool IsPackageApplied(ModemSms sms, PackageInfo pkg)
        {
            var key = sms.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|"
                      + pkg.Gb.ToString("0.##", CultureInfo.InvariantCulture) + "|" + pkg.Days.ToString(CultureInfo.InvariantCulture);
            return _config.LoadGlobalConfig().ModemAppliedPackages?.Contains(key) == true;
        }

        private void NotifyPackageAdded(ConfigurationService.GlobalConfig cfg, PackageInfo pkg, double total)
        {
            var title = Loc.T("modem.alert.packageTitle", pkg.Gb.ToString("0.##", CultureInfo.InvariantCulture));
            var body = Loc.T("modem.alert.packageBody", total.ToString("0.##", CultureInfo.InvariantCulture), pkg.Days, pkg.Expiry);
            if (cfg.ModemAlertToast)
            {
                new NotificationService().ShowToast(title, body);
            }

            if (cfg.ModemAlertTelegram)
            {
                TryNotifyTelegram(cfg, "📦 " + title + "\n" + body);
            }
        }

        private void NotifyOperatorSms(ConfigurationService.GlobalConfig cfg, double percent, ModemSms sms)
        {
            var pctText = percent.ToString("0.#", CultureInfo.InvariantCulture);
            var title = percent >= 100 ? Loc.T("modem.alert.titleFull") : Loc.T("modem.alert.smsTitle", pctText);
            var body = sms.Text.Trim();
            if (cfg.ModemAlertToast)
            {
                new NotificationService().ShowToast(title, body);
            }

            if (cfg.ModemAlertTelegram)
            {
                TryNotifyTelegram(cfg, "📶 " + title + "\n" + body);
            }
        }

        private void Publish(ModemSnapshot snapshot)
        {
            lock (_gate)
            {
                _latest = snapshot;
            }

            SnapshotChanged?.Invoke(this, snapshot);
        }

        // ---- forecast -------------------------------------------------------------------

        private static double EstimateDaysLeft(string cycleKey, long used, long plan)
        {
            if (plan <= 0 || used <= 0)
            {
                return -1;
            }

            if (used >= plan)
            {
                return 0;
            }

            var start = ParseCycleStart(cycleKey) ?? new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
            var elapsedDays = Math.Max(0.25, (DateTime.Now - start).TotalDays);
            var perDay = used / elapsedDays;
            return perDay <= 0 ? -1 : (plan - used) / perDay;
        }

        private static DateTime? ParseCycleStart(string key) =>
            DateTime.TryParseExact(key, new[] { "yyyy-M-d", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d
                : null;

        // ---- daily history --------------------------------------------------------------

        private void LoadHistory()
        {
            try
            {
                if (File.Exists(_historyPath))
                {
                    _history = JsonSerializer.Deserialize<Dictionary<string, DayRecord>>(File.ReadAllText(_historyPath))
                               ?? new Dictionary<string, DayRecord>();
                }
            }
            catch
            {
                _history = new Dictionary<string, DayRecord>();
            }
        }

        private List<ModemDayUsage> UpdateHistory(ModemUsage usage, long used)
        {
            lock (_gate)
            {
                var today = DateTime.Now.Date;
                _history[today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)] =
                    new DayRecord { Used = used, Cycle = usage.CycleKey };

                foreach (var key in _history.Keys.ToList())
                {
                    if (DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                        && (today - d).TotalDays > KeepDays)
                    {
                        _history.Remove(key);
                    }
                }

                if ((DateTime.UtcNow - _lastHistorySaveUtc).TotalMinutes >= 5)
                {
                    _lastHistorySaveUtc = DateTime.UtcNow;
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
                        File.WriteAllText(_historyPath, JsonSerializer.Serialize(_history));
                    }
                    catch
                    {
                        // history is cosmetic
                    }
                }

                var result = new List<ModemDayUsage>();
                for (var i = 13; i >= 0; i--)
                {
                    var day = today.AddDays(-i);
                    var key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    var prevKey = day.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    long bytes = 0;
                    if (_history.TryGetValue(key, out var cur))
                    {
                        // Needs the previous day's last reading as a baseline; a new cycle starts from zero.
                        if (_history.TryGetValue(prevKey, out var prev))
                        {
                            bytes = prev.Cycle == cur.Cycle ? Math.Max(0, cur.Used - prev.Used) : cur.Used;
                        }
                    }

                    result.Add(new ModemDayUsage(day, bytes));
                }

                return result;
            }
        }

        // ---- alerts ---------------------------------------------------------------------

        private void CheckAlerts(ConfigurationService.GlobalConfig cfg, string cycleKey, ModemSnapshot s)
        {
            if (s.Percent < 0)
            {
                return;
            }

            var thresholds = (cfg.ModemAlertThresholds ?? "")
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => int.TryParse(x.Trim().TrimEnd('%'), out var v) ? v : 0)
                .Where(v => v > 0 && v <= 200)
                .Distinct()
                .OrderBy(v => v)
                .ToList();
            if (thresholds.Count == 0)
            {
                return;
            }

            var cycle = cycleKey ?? "";
            var lastPercent = string.Equals(cfg.ModemLastAlertCycle, cycle, StringComparison.Ordinal)
                ? cfg.ModemLastAlertPercent
                : 0;

            var crossed = thresholds.Where(t => s.Percent >= t).DefaultIfEmpty(0).Max();
            if (crossed <= 0 || crossed <= lastPercent)
            {
                if (!string.Equals(cfg.ModemLastAlertCycle, cycle, StringComparison.Ordinal))
                {
                    _config.UpdateGlobalConfig(c =>
                    {
                        c.ModemLastAlertCycle = cycle;
                        c.ModemLastAlertPercent = 0;
                    });
                }

                return;
            }

            _config.UpdateGlobalConfig(c =>
            {
                c.ModemLastAlertCycle = cycle;
                c.ModemLastAlertPercent = crossed;
            });

            var title = crossed >= 100 ? Loc.T("modem.alert.titleFull") : Loc.T("modem.alert.title", crossed);
            var body = Loc.T(
                "modem.alert.body",
                s.Percent.ToString("0.#", CultureInfo.InvariantCulture),
                FormatBytes(s.UsedBytes),
                FormatBytes(s.PlanBytes),
                FormatBytes(s.RemainingBytes));

            if (cfg.ModemAlertToast)
            {
                new NotificationService().ShowToast(title, body);
            }

            if (cfg.ModemAlertTelegram)
            {
                TryNotifyTelegram(cfg, "📶 " + title + "\n" + body);
            }
        }

        private static void TryNotifyTelegram(ConfigurationService.GlobalConfig cfg, string text)
        {
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

            foreach (var chatId in chatIds)
            {
                TelegramOutboundQueue.Instance.EnqueueText(token, chatId, cfg.TelegramActiveProjectPath ?? "", text);
            }
        }

        public static string FormatBytes(long bytes)
        {
            double v = Math.Max(0, bytes);
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            var i = 0;
            while (v >= 1024 && i < units.Length - 1)
            {
                v /= 1024;
                i++;
            }

            return v.ToString(i >= 3 ? "0.##" : "0.#", CultureInfo.InvariantCulture) + " " + units[i];
        }

        private void StopLoop()
        {
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
            }
            catch
            {
            }

            _cts = null;
        }

        public void Dispose()
        {
            _disposed = true;
            StopLoop();
        }
    }
}
