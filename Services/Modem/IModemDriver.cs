using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GitDeployPro.Services.Modem
{
    /// <summary>
    /// One driver per modem family. Add a new class + register it in <see cref="ModemDriverFactory"/>.
    /// </summary>
    public interface IModemDriver
    {
        string DriverId { get; }
        string DisplayName { get; }

        /// <summary>Default admin URL shown in settings for this driver.</summary>
        string DefaultBaseUrl { get; }

        Task<ModemUsage> GetUsageAsync(ModemConnection connection, CancellationToken cancellationToken);

        /// <summary>Newest-first inbox messages. Drivers without SMS support return an empty list.</summary>
        Task<IReadOnlyList<ModemSms>> GetSmsAsync(ModemConnection connection, int count, CancellationToken cancellationToken);

        Task<ModemSmsResult> GetSmsPageAsync(ModemConnection connection, ModemSmsBox box, int page, int pageSize, CancellationToken cancellationToken);

        Task DeleteSmsAsync(ModemConnection connection, IReadOnlyList<string> ids, CancellationToken cancellationToken);

        Task SendSmsAsync(ModemConnection connection, string phone, string text, CancellationToken cancellationToken);
    }

    public enum ModemSmsBox
    {
        Inbox,
        Sent
    }

    public sealed record ModemSms(string Id, DateTime Date, string From, string Text, bool Unread = false);

    public sealed record ModemSmsResult(IReadOnlyList<ModemSms> Items, int Total);

    public sealed class ModemConnection
    {
        public string BaseUrl { get; init; } = "";
        public string Username { get; init; } = "";
        public string Password { get; init; } = "";
    }

    public sealed class ModemUsage
    {
        public bool Success { get; init; }
        public string Error { get; init; } = "";
        public DateTime SampledLocal { get; init; } = DateTime.Now;

        /// <summary>Billing-cycle usage as counted by the modem.</summary>
        public long MonthDownloadBytes { get; init; }
        public long MonthUploadBytes { get; init; }

        /// <summary>Current connection (since last connect).</summary>
        public long SessionDownloadBytes { get; init; }
        public long SessionUploadBytes { get; init; }
        public long DownloadRateBytesPerSec { get; init; }
        public long UploadRateBytesPerSec { get; init; }
        public long ConnectedSeconds { get; init; }

        /// <summary>Plan size configured inside the modem (0 = not set).</summary>
        public long DeviceLimitBytes { get; init; }

        /// <summary>Stable key for the current billing cycle (changes when the modem resets counters).</summary>
        public string CycleKey { get; init; } = "";

        public static ModemUsage Failed(string error) => new() { Success = false, Error = error };
    }
}
