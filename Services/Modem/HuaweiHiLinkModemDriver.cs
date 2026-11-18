using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace GitDeployPro.Services.Modem
{
    /// <summary>
    /// Huawei HiLink web-UI modems (E5xxx / B3xx …). Usage readings need no login;
    /// SMS (list / delete / send) uses the SCRAM-SHA256 login of the web UI.
    /// </summary>
    public sealed class HuaweiHiLinkModemDriver : IModemDriver
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

        private readonly SemaphoreSlim _gate = new(1, 1);
        private Session? _session;
        private string _sessionKey = "";
        private string _failedKey = "";
        private string _failedMessage = "";
        private DateTime _failedAtUtc = DateTime.MinValue;

        public string DriverId => ModemDriverFactory.HuaweiHiLink;
        public string DisplayName => "Huawei HiLink";
        public string DefaultBaseUrl => "http://192.168.8.1";

        // ---- usage (no login) ---------------------------------------------------------------

        public async Task<ModemUsage> GetUsageAsync(ModemConnection connection, CancellationToken cancellationToken)
        {
            var baseUrl = (connection.BaseUrl ?? "").Trim().TrimEnd('/');
            if (baseUrl.Length == 0)
            {
                return ModemUsage.Failed("Modem URL is empty.");
            }

            try
            {
                var monthTask = GetXmlAsync(baseUrl + "/api/monitoring/month_statistics", cancellationToken);
                var trafficTask = GetXmlAsync(baseUrl + "/api/monitoring/traffic-statistics", cancellationToken);
                var startTask = GetXmlAsync(baseUrl + "/api/monitoring/start_date", cancellationToken);
                await Task.WhenAll(monthTask, trafficTask, startTask).ConfigureAwait(false);

                var month = monthTask.Result;
                var traffic = trafficTask.Result;
                var start = startTask.Result;

                return new ModemUsage
                {
                    Success = true,
                    SampledLocal = DateTime.Now,
                    MonthDownloadBytes = Long(month, "CurrentMonthDownload"),
                    MonthUploadBytes = Long(month, "CurrentMonthUpload"),
                    SessionDownloadBytes = Long(traffic, "CurrentDownload"),
                    SessionUploadBytes = Long(traffic, "CurrentUpload"),
                    DownloadRateBytesPerSec = Long(traffic, "CurrentDownloadRate"),
                    UploadRateBytesPerSec = Long(traffic, "CurrentUploadRate"),
                    ConnectedSeconds = Long(traffic, "CurrentConnectTime"),
                    DeviceLimitBytes = ParseLimit(Text(start, "DataLimit")),
                    CycleKey = Text(month, "MonthLastClearTime")
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ModemUsage.Failed(ex.Message);
            }
        }

        // ---- SMS ----------------------------------------------------------------------------

        public async Task<IReadOnlyList<ModemSms>> GetSmsAsync(ModemConnection connection, int count, CancellationToken cancellationToken)
        {
            var page = await GetSmsPageAsync(connection, ModemSmsBox.Inbox, 1, count, cancellationToken).ConfigureAwait(false);
            return page.Items;
        }

        public Task<ModemSmsResult> GetSmsPageAsync(ModemConnection connection, ModemSmsBox box, int page, int pageSize, CancellationToken cancellationToken)
        {
            return WithSessionAsync(connection, async s =>
            {
                var boxType = box == ModemSmsBox.Sent ? 2 : 1;
                var body = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><request><PageIndex>" + Math.Max(1, page).ToString(CultureInfo.InvariantCulture)
                           + "</PageIndex><ReadCount>" + Math.Clamp(pageSize, 1, 50).ToString(CultureInfo.InvariantCulture)
                           + "</ReadCount><BoxType>" + boxType + "</BoxType><SortType>0</SortType><Ascending>0</Ascending><UnreadPreferred>0</UnreadPreferred></request>";
                var root = await s.PostAsync("/api/sms/sms-list", body, cancellationToken).ConfigureAwait(false);

                var items = new List<ModemSms>();
                foreach (var m in root.Element("Messages")?.Elements("Message") ?? Enumerable.Empty<XElement>())
                {
                    var date = DateTime.TryParse(Text(m, "Date"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.MinValue;
                    items.Add(new ModemSms(Text(m, "Index"), date, Text(m, "Phone"), Text(m, "Content"), Text(m, "Smstat") == "0"));
                }

                var total = items.Count;
                try
                {
                    var count = await s.GetAsync("/api/sms/sms-count", cancellationToken).ConfigureAwait(false);
                    total = (int)Long(count, box == ModemSmsBox.Sent ? "LocalOutbox" : "LocalInbox");
                }
                catch (ModemSessionExpiredException)
                {
                    throw;
                }
                catch
                {
                    // fall back to the page size
                }

                return new ModemSmsResult(items.OrderByDescending(x => x.Date).ToList(), Math.Max(total, items.Count));
            }, cancellationToken);
        }

        public Task DeleteSmsAsync(ModemConnection connection, IReadOnlyList<string> ids, CancellationToken cancellationToken)
        {
            if (ids.Count == 0)
            {
                return Task.CompletedTask;
            }

            return WithSessionAsync(connection, async s =>
            {
                var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><request>");
                foreach (var id in ids)
                {
                    sb.Append("<Index>").Append(System.Security.SecurityElement.Escape(id)).Append("</Index>");
                }

                sb.Append("</request>");
                await s.PostAsync("/api/sms/delete-sms", sb.ToString(), cancellationToken).ConfigureAwait(false);
                return true;
            }, cancellationToken);
        }

        public Task SendSmsAsync(ModemConnection connection, string phone, string text, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(phone) || string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException("Phone number and text are required.");
            }

            return WithSessionAsync(connection, async s =>
            {
                var body = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><request><Index>-1</Index><Phones><Phone>"
                           + System.Security.SecurityElement.Escape(phone.Trim()) + "</Phone></Phones><Sca></Sca><Content>"
                           + System.Security.SecurityElement.Escape(text) + "</Content><Length>"
                           + text.Length.ToString(CultureInfo.InvariantCulture) + "</Length><Reserved>1</Reserved><Date>"
                           + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "</Date></request>";
                await s.PostAsync("/api/sms/send-sms", body, cancellationToken).ConfigureAwait(false);
                return true;
            }, cancellationToken);
        }

        // ---- session cache ------------------------------------------------------------------

        private async Task<T> WithSessionAsync<T>(ModemConnection c, Func<Session, Task<T>> action, CancellationToken ct)
        {
            var baseUrl = (c.BaseUrl ?? "").Trim().TrimEnd('/');
            if (baseUrl.Length == 0 || string.IsNullOrEmpty(c.Password))
            {
                throw new InvalidOperationException("Modem password is required to use SMS.");
            }

            var user = string.IsNullOrWhiteSpace(c.Username) ? "admin" : c.Username.Trim();
            var key = baseUrl + "|" + user + "|" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(c.Password)));

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                for (var attempt = 0; ; attempt++)
                {
                    var session = await EnsureSessionAsync(baseUrl, user, c.Password, key, ct).ConfigureAwait(false);
                    try
                    {
                        return await action(session).ConfigureAwait(false);
                    }
                    catch (ModemSessionExpiredException) when (attempt == 0)
                    {
                        _session = null;
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<Session> EnsureSessionAsync(string baseUrl, string user, string password, string key, CancellationToken ct)
        {
            if (_session != null && _sessionKey == key)
            {
                return _session;
            }

            // A failed login must not be repeated blindly: wrong attempts lock the modem.
            if (_failedKey == key && (DateTime.UtcNow - _failedAtUtc).TotalMinutes < 10)
            {
                throw new InvalidOperationException(_failedMessage);
            }

            var handler = new HttpClientHandler { UseCookies = false };
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            var session = new Session(http, baseUrl);
            try
            {
                await session.LoginAsync(user, password, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("login", StringComparison.OrdinalIgnoreCase))
            {
                _failedKey = key;
                _failedMessage = ex.Message;
                _failedAtUtc = DateTime.UtcNow;
                http.Dispose();
                throw;
            }

            _failedKey = "";
            _session = session;
            _sessionKey = key;
            return session;
        }

        private sealed class Session
        {
            private readonly HttpClient _http;
            private readonly string _base;
            private string _cookie = "";
            private string _token = "";

            public Session(HttpClient http, string baseUrl)
            {
                _http = http;
                _base = baseUrl;
            }

            public async Task LoginAsync(string user, string password, CancellationToken ct)
            {
                var tok = await GetRawAsync("/api/webserver/SesTokInfo", ct).ConfigureAwait(false);
                _token = Elem(tok, "TokInfo");
                _cookie = "SessionID=" + Elem(tok, "SesInfo");

                var firstNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                var challengeBody = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><request><username>" + System.Security.SecurityElement.Escape(user)
                                    + "</username><firstnonce>" + firstNonce + "</firstnonce><mode>1</mode></request>";
                var (ch, _) = await SendAsync("/api/user/challenge_login", challengeBody, ct).ConfigureAwait(false);

                var salt = Convert.FromHexString(Elem(ch, "salt"));
                var iterations = int.Parse(Elem(ch, "iterations"), CultureInfo.InvariantCulture);
                var serverNonce = Elem(ch, "servernonce");

                var salted = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
                // Huawei's scram.js calls CryptoJS.HmacSHA256(message, key) with the arguments the
                // "wrong" way round, so the constant / auth message act as the HMAC key here.
                var clientKey = HMACSHA256.HashData(Encoding.UTF8.GetBytes("Client Key"), salted);
                var storedKey = SHA256.HashData(clientKey);
                var authMsg = Encoding.UTF8.GetBytes(firstNonce + "," + serverNonce + "," + serverNonce);
                var clientSig = HMACSHA256.HashData(authMsg, storedKey);
                var proof = new byte[clientKey.Length];
                for (var i = 0; i < proof.Length; i++)
                {
                    proof[i] = (byte)(clientKey[i] ^ clientSig[i]);
                }

                var authBody = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><request><clientproof>" + Convert.ToHexString(proof).ToLowerInvariant()
                               + "</clientproof><finalnonce>" + serverNonce + "</finalnonce></request>";
                var (_, authResp) = await SendAsync("/api/user/authentication_login", authBody, ct).ConfigureAwait(false);

                if (authResp.Headers.TryGetValues("Set-Cookie", out var cookies))
                {
                    var sid = cookies.Select(c => c.Split(';')[0]).FirstOrDefault(c => c.StartsWith("SessionID", StringComparison.OrdinalIgnoreCase));
                    if (sid != null)
                    {
                        _cookie = sid;
                    }
                }
            }

            public async Task<XElement> GetAsync(string path, CancellationToken ct) => await GetRawAsync(path, ct).ConfigureAwait(false);

            public async Task<XElement> PostAsync(string path, string body, CancellationToken ct)
            {
                var t = await GetRawAsync("/api/webserver/token", ct).ConfigureAwait(false);
                var token = Elem(t, "token");
                _token = token.Length >= 64 ? token[32..] : token;
                var (root, _) = await SendAsync(path, body, ct).ConfigureAwait(false);
                return root;
            }

            private async Task<XElement> GetRawAsync(string path, CancellationToken ct)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, _base + path);
                if (_cookie.Length > 0)
                {
                    req.Headers.TryAddWithoutValidation("Cookie", _cookie);
                }

                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return Parse(text);
            }

            private async Task<(XElement Root, HttpResponseMessage Resp)> SendAsync(string path, string body, CancellationToken ct)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, _base + path)
                {
                    Content = new StringContent(body, Encoding.UTF8, "text/xml")
                };
                req.Headers.TryAddWithoutValidation("Cookie", _cookie);
                req.Headers.TryAddWithoutValidation("__RequestVerificationToken", _token);
                var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var root = Parse(text);
                if (resp.Headers.TryGetValues("__RequestVerificationToken", out var toks))
                {
                    _token = toks.First().Split('#')[0];
                }

                return (root, resp);
            }

            private static XElement Parse(string text)
            {
                var root = XDocument.Parse(text).Root ?? throw new InvalidOperationException("Empty response.");
                if (string.Equals(root.Name.LocalName, "error", StringComparison.OrdinalIgnoreCase))
                {
                    var code = root.Element("code")?.Value ?? "?";
                    if (code is "125001" or "125002" or "125003" or "100003")
                    {
                        throw new ModemSessionExpiredException("Modem session expired (code " + code + ").");
                    }

                    throw new InvalidOperationException(code is "108001" or "108002" or "108006" or "108007"
                        ? "Modem login failed: wrong username or password (code " + code + ")."
                        : code is "108003" or "108004" or "108005" or "108008" or "108009"
                        ? "Modem login locked (too many attempts, code " + code + "). Wait a few minutes."
                        : "Modem error " + code + ".");
                }

                return root;
            }

            private static string Elem(XElement root, string name) =>
                root.Element(name)?.Value?.Trim() ?? throw new InvalidOperationException("Missing <" + name + "> in modem response.");
        }

        // ---- helpers ------------------------------------------------------------------------

        private static async Task<XElement> GetXmlAsync(string url, CancellationToken ct)
        {
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var root = XDocument.Parse(body).Root ?? throw new InvalidOperationException("Empty response.");
            if (string.Equals(root.Name.LocalName, "error", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Modem error " + (root.Element("code")?.Value ?? "?") + " (login may be required).");
            }

            return root;
        }

        private static string Text(XElement root, string name) => root.Element(name)?.Value?.Trim() ?? "";

        private static long Long(XElement root, string name) =>
            long.TryParse(Text(root, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

        /// <summary>Parses "100GB", "500MB", "1.5TB" (modem uses 1024 steps). "0GB"/empty = 0.</summary>
        internal static long ParseLimit(string raw)
        {
            raw = (raw ?? "").Trim().ToUpperInvariant();
            if (raw.Length == 0)
            {
                return 0;
            }

            double mult = 1;
            if (raw.EndsWith("TB")) { mult = 1024d * 1024 * 1024 * 1024; raw = raw[..^2]; }
            else if (raw.EndsWith("GB")) { mult = 1024d * 1024 * 1024; raw = raw[..^2]; }
            else if (raw.EndsWith("MB")) { mult = 1024d * 1024; raw = raw[..^2]; }
            else if (raw.EndsWith("KB")) { mult = 1024d; raw = raw[..^2]; }

            return double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 0
                ? (long)(n * mult)
                : 0;
        }
    }

    internal sealed class ModemSessionExpiredException : InvalidOperationException
    {
        public ModemSessionExpiredException(string message) : base(message)
        {
        }
    }
}
