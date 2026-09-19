using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// Account-level Cursor plan usage (same meters as IDE footer:
    /// Cursor Models % / Other Models %), via Dashboard GetCurrentPeriodUsage
    /// using the local IDE login token in %APPDATA%\Cursor\auth.json.
    /// </summary>
    public static class CursorAccountUsageService
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly object Gate = new();
        private static CursorAccountUsageSnapshot? _cache;
        private static DateTime _cacheUtc = DateTime.MinValue;
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(45);

        public static async Task<CursorAccountUsageSnapshot> GetAsync(CancellationToken cancellationToken = default)
        {
            lock (Gate)
            {
                if (_cache != null && DateTime.UtcNow - _cacheUtc < CacheTtl)
                {
                    return _cache;
                }
            }

            var snapshot = await FetchFreshAsync(cancellationToken).ConfigureAwait(false);
            lock (Gate)
            {
                _cache = snapshot;
                _cacheUtc = DateTime.UtcNow;
            }

            return snapshot;
        }

        /// <summary>Non-blocking: returns cache or a pending/unavailable stub.</summary>
        public static CursorAccountUsageSnapshot GetCachedOrStale()
        {
            lock (Gate)
            {
                return _cache ?? CursorAccountUsageSnapshot.Unavailable("not fetched yet");
            }
        }

        public static void InvalidateCache()
        {
            lock (Gate)
            {
                _cache = null;
                _cacheUtc = DateTime.MinValue;
            }
        }

        private static async Task<CursorAccountUsageSnapshot> FetchFreshAsync(CancellationToken cancellationToken)
        {
            var token = TryReadAccessToken();
            if (string.IsNullOrWhiteSpace(token))
            {
                return CursorAccountUsageSnapshot.Unavailable("no Cursor auth.json token");
            }

            try
            {
                using var req = new HttpRequestMessage(
                    HttpMethod.Post,
                    "https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
                req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linked.CancelAfter(TimeSpan.FromSeconds(12));
                using var res = await Http.SendAsync(req, linked.Token).ConfigureAwait(false);
                var body = await res.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    return CursorAccountUsageSnapshot.Unavailable(
                        $"HTTP {(int)res.StatusCode}");
                }

                var jo = JObject.Parse(body);
                var plan = jo["planUsage"] as JObject ?? jo;
                var autoPct = ReadDouble(plan, "autoPercentUsed");
                var apiPct = ReadDouble(plan, "apiPercentUsed");
                var totalPct = ReadDouble(plan, "totalPercentUsed");
                // Match IDE footer rounding (1% / 0%).
                var cursorModelsPct = (int)Math.Round(autoPct, MidpointRounding.AwayFromZero);
                var otherModelsPct = (int)Math.Round(apiPct, MidpointRounding.AwayFromZero);

                return new CursorAccountUsageSnapshot
                {
                    Ok = true,
                    CursorModelsPercent = cursorModelsPct,
                    OtherModelsPercent = otherModelsPct,
                    TotalPercent = (int)Math.Round(totalPct, MidpointRounding.AwayFromZero),
                    AutoPercentExact = autoPct,
                    ApiPercentExact = apiPct,
                    DisplayMessage = jo["displayMessage"]?.ToString()?.Trim() ?? string.Empty,
                    AutoDisplayMessage = jo["autoModelSelectedDisplayMessage"]?.ToString()?.Trim() ?? string.Empty,
                    NamedDisplayMessage = jo["namedModelSelectedDisplayMessage"]?.ToString()?.Trim() ?? string.Empty,
                    FetchedUtc = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                return CursorAccountUsageSnapshot.Unavailable(ex.Message);
            }
        }

        private static string? TryReadAccessToken()
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Cursor",
                    "auth.json");
                if (!File.Exists(path))
                {
                    return null;
                }

                var jo = JObject.Parse(File.ReadAllText(path));
                var token = jo["accessToken"]?.ToString()?.Trim();
                return string.IsNullOrWhiteSpace(token) ? null : token;
            }
            catch
            {
                return null;
            }
        }

        private static double ReadDouble(JObject obj, string key)
        {
            var t = obj[key];
            if (t == null || t.Type == JTokenType.Null)
            {
                return 0;
            }

            return double.TryParse(
                t.ToString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var n)
                ? n
                : 0;
        }
    }

    public sealed class CursorAccountUsageSnapshot
    {
        public bool Ok { get; init; }
        public string Error { get; init; } = "";
        /// <summary>IDE "Cursor Models: X% used" (auto bucket).</summary>
        public int CursorModelsPercent { get; init; }
        /// <summary>IDE "Other Models: Y% used" (named/API bucket).</summary>
        public int OtherModelsPercent { get; init; }
        public int TotalPercent { get; init; }
        public double AutoPercentExact { get; init; }
        public double ApiPercentExact { get; init; }
        public string DisplayMessage { get; init; } = "";
        public string AutoDisplayMessage { get; init; } = "";
        public string NamedDisplayMessage { get; init; } = "";
        public DateTime FetchedUtc { get; init; }

        public static CursorAccountUsageSnapshot Unavailable(string error)
            => new() { Ok = false, Error = error ?? string.Empty, FetchedUtc = DateTime.UtcNow };
    }
}
