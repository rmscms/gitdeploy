using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Models;
using GitDeployPro.Services;
using Renci.SshNet;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// One interactive SSH shell per Telegram chat. Separate from the Deploy terminal.
    /// </summary>
    internal static class TelegramSshSessionHub
    {
        private const string DoneMarker = "__GDP_DONE__";
        private static readonly ConcurrentDictionary<long, Session> Sessions = new();
        private static readonly ConcurrentDictionary<long, byte> Armed = new();
        private static readonly Regex Csi = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
        private static readonly Regex Osc = new(@"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)?", RegexOptions.Compiled);
        private static readonly Regex ShellMark = new(@"\](?:3008|0);[^\\\r\n]*\\?", RegexOptions.Compiled);
        private static readonly Regex PromptLine = new(@"^\S+@\S+.*[#\$]\s*$", RegexOptions.Compiled);

        public static bool IsOpen(long chatId) => Sessions.ContainsKey(chatId);

        public static bool IsArmed(long chatId) => Armed.ContainsKey(chatId) || IsOpen(chatId);

        public static void Arm(long chatId) => Armed[chatId] = 1;

        public static void Disarm(long chatId) => Armed.TryRemove(chatId, out _);

        public static async Task<(bool Ok, string Message)> ConnectAsync(long chatId, ConnectionProfile profile)
        {
            await CloseAsync(chatId).ConfigureAwait(false);

            var session = new Session(profile);
            try
            {
                await session.ConnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                session.Dispose();
                return (false, ex.Message);
            }

            Sessions[chatId] = session;
            return (true, profile.Name);
        }

        public static async Task CloseAsync(long chatId)
        {
            if (Sessions.TryRemove(chatId, out var session))
            {
                session.Dispose();
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        public static async Task<(string Output, string Cwd, bool StillRunning)> RunAsync(long chatId, string command)
        {
            if (!Sessions.TryGetValue(chatId, out var session))
            {
                throw new InvalidOperationException("closed");
            }

            return await session.RunAsync(command).ConfigureAwait(false);
        }

        public static (string Output, string Cwd) CleanOutput(string? raw, string? command = null)
        {
            var text = Osc.Replace(raw ?? string.Empty, string.Empty);
            text = Csi.Replace(text, string.Empty);
            text = ShellMark.Replace(text, string.Empty);
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            text = text.Replace("\0", string.Empty);

            var cwd = string.Empty;
            var markerAt = text.LastIndexOf(DoneMarker, StringComparison.Ordinal);
            if (markerAt >= 0)
            {
                var after = text[(markerAt + DoneMarker.Length)..].Trim();
                var lineEnd = after.IndexOf('\n');
                if (lineEnd >= 0)
                {
                    after = after[..lineEnd];
                }

                after = after.Trim().Trim('\'', '"');
                if (after.StartsWith('/'))
                {
                    var space = after.IndexOf(' ');
                    cwd = space > 0 ? after[..space] : after;
                }

                text = text[..markerAt];
            }

            var cmd = (command ?? string.Empty).Trim();
            var skippedEcho = false;
            var lines = new StringBuilder();
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.Contains(DoneMarker, StringComparison.Ordinal)
                    || line.Contains("echo " + DoneMarker, StringComparison.Ordinal)
                    || line.StartsWith("]3008;", StringComparison.Ordinal)
                    || line.StartsWith("]0;", StringComparison.Ordinal)
                    || PromptLine.IsMatch(line))
                {
                    continue;
                }

                if (!skippedEcho && cmd.Length > 0 && string.Equals(line, cmd, StringComparison.Ordinal))
                {
                    skippedEcho = true;
                    continue;
                }

                if (lines.Length > 0)
                {
                    lines.Append('\n');
                }

                lines.Append(line);
            }

            return (lines.ToString().Trim(), cwd);
        }

        private sealed class Session : IDisposable
        {
            private readonly ConnectionProfile _profile;
            private readonly SemaphoreSlim _gate = new(1, 1);
            private SshClient? _client;
            private ShellStream? _shell;

            public Session(ConnectionProfile profile)
            {
                _profile = profile;
            }

            public async Task ConnectAsync()
            {
                var port = _profile.Port <= 0 || _profile.Port == 21 ? 22 : _profile.Port;
                var password = EncryptionService.Decrypt(_profile.Password ?? string.Empty);
                var info = new ConnectionInfo(
                    _profile.Host,
                    port,
                    _profile.Username,
                    new PasswordAuthenticationMethod(_profile.Username, password))
                {
                    Timeout = TimeSpan.FromSeconds(20)
                };

                var client = new SshClient(info);
                client.Connect();
                if (!client.IsConnected)
                {
                    throw new InvalidOperationException("SSH client reported disconnected after connect.");
                }

                var shell = client.CreateShellStream("xterm-256color", 120, 40, 800, 600, 4096);
                _client = client;
                _shell = shell;
                await DrainAsync(TimeSpan.FromMilliseconds(600)).ConfigureAwait(false);

                if (_profile.RunSshStartupCommand && !string.IsNullOrWhiteSpace(_profile.SshStartupCommand))
                {
                    var lines = _profile.SshStartupCommand.Replace("\r\n", "\n").Split('\n');
                    foreach (var raw in lines)
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith('#'))
                        {
                            continue;
                        }

                        shell.Write(line + "\n");
                        shell.Flush();
                    }

                    await DrainAsync(TimeSpan.FromMilliseconds(800)).ConfigureAwait(false);
                }
            }

            public async Task<(string Output, string Cwd, bool StillRunning)> RunAsync(string command)
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var shell = _shell;
                    var client = _client;
                    if (shell == null || client == null || !client.IsConnected)
                    {
                        throw new InvalidOperationException("closed");
                    }

                    shell.Write(command + "\n");
                    shell.Write("printf '%s\\n' \"" + DoneMarker + "${PWD}\"\n");
                    shell.Flush();

                    var buffer = new StringBuilder();
                    var started = Stopwatch.StartNew();
                    var lastData = Stopwatch.StartNew();
                    var sawMarker = false;
                    while (started.Elapsed < TimeSpan.FromSeconds(12))
                    {
                        if (shell.DataAvailable)
                        {
                            buffer.Append(shell.Read());
                            lastData.Restart();
                            if (buffer.ToString().Contains(DoneMarker, StringComparison.Ordinal))
                            {
                                sawMarker = true;
                                break;
                            }
                        }
                        else if (buffer.Length > 0 && lastData.ElapsedMilliseconds >= 800)
                        {
                            break;
                        }
                        else
                        {
                            await Task.Delay(40).ConfigureAwait(false);
                        }
                    }

                    var (cleaned, cwd) = CleanOutput(buffer.ToString(), command);
                    return (cleaned, cwd, !sawMarker && started.Elapsed >= TimeSpan.FromSeconds(12));
                }
                finally
                {
                    _gate.Release();
                }
            }

            private async Task DrainAsync(TimeSpan quiet)
            {
                var shell = _shell;
                if (shell == null)
                {
                    return;
                }

                var last = Stopwatch.StartNew();
                var cap = Stopwatch.StartNew();
                while (cap.Elapsed < TimeSpan.FromSeconds(3))
                {
                    if (shell.DataAvailable)
                    {
                        _ = shell.Read();
                        last.Restart();
                    }
                    else if (last.Elapsed >= quiet)
                    {
                        break;
                    }
                    else
                    {
                        await Task.Delay(40).ConfigureAwait(false);
                    }
                }
            }

            public void Dispose()
            {
                try
                {
                    _shell?.Close();
                    _shell?.Dispose();
                }
                catch
                {
                }

                try
                {
                    if (_client?.IsConnected == true)
                    {
                        _client.Disconnect();
                    }

                    _client?.Dispose();
                }
                catch
                {
                }

                _shell = null;
                _client = null;
                _gate.Dispose();
            }
        }
    }
}
