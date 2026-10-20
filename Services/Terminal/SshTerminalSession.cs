using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace GitDeployPro.Services.Terminal
{
    public sealed class SshTerminalSession : ITerminalSession
    {
        /// <summary>
        /// NAT/firewall on non-standard SSH ports often drop idle TCP in ~2–4 minutes.
        /// Keep this well under that window.
        /// </summary>
        private static readonly TimeSpan KeepAlivePeriod = TimeSpan.FromSeconds(10);

        private readonly string _host;
        private readonly string _user;
        private readonly string _password;
        private readonly int _port;
        private int _columns;
        private int _rows;

        private SshClient? _sshClient;
        private ShellStream? _shellStream;
        private CancellationTokenSource? _readLoopCts;
        private Task? _readLoopTask;
        private int _sessionClosedRaised;
        private int _stopping;
        private bool _disposed;

        public event Action<string>? OutputReceived;
        public event Action? SessionClosed;

        public bool IsConnected => _sshClient?.IsConnected == true && _shellStream != null;

        public SshTerminalSession(string host, string user, string password, int port, int columns, int rows)
        {
            _host = host;
            _user = user;
            _password = password;
            _port = port == 21 ? 22 : port;
            _columns = Math.Max(20, columns);
            _rows = Math.Max(5, rows);
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            await Task.Run(() =>
            {
                var connectionInfo = new ConnectionInfo(
                    _host,
                    _port,
                    _user,
                    new PasswordAuthenticationMethod(_user, _password))
                {
                    Timeout = TimeSpan.FromSeconds(20)
                };

                _sshClient = new SshClient(connectionInfo);
                _sshClient.ConnectionInfo.Timeout = TimeSpan.FromSeconds(20);
                // Interactive shells often sit idle (composer, editors). Without SSH keepalives,
                // NAT/firewall/sshd idle timers commonly drop the TCP after a few minutes.
                _sshClient.KeepAliveInterval = KeepAlivePeriod;
                _sshClient.ErrorOccurred += OnClientError;
                _sshClient.Connect();
                if (!_sshClient.IsConnected)
                {
                    throw new InvalidOperationException("SSH client reported disconnected after connect.");
                }

                TryEnableTcpKeepAlive(_sshClient);

                var terminalModes = new Dictionary<TerminalModes, uint>
                {
                    { TerminalModes.ECHO, 1 },
                    { TerminalModes.ISIG, 1 },
                    { TerminalModes.ICANON, 1 },
                    { TerminalModes.OPOST, 1 },
                    { TerminalModes.ONLCR, 1 },
                    { TerminalModes.ICRNL, 1 },
                    { TerminalModes.IXON, 0 },
                    { TerminalModes.IXOFF, 0 }
                };

                _shellStream = _sshClient.CreateShellStream(
                    "xterm-256color",
                    (uint)_columns,
                    (uint)_rows,
                    (uint)Math.Max(640, _columns * 8),
                    (uint)Math.Max(320, _rows * 16),
                    4096,
                    terminalModes);

                if (_shellStream == null)
                {
                    throw new InvalidOperationException("Failed to open SSH shell stream.");
                }

                _shellStream.ErrorOccurred += OnShellError;
            }, cancellationToken);

            _readLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _readLoopTask = Task.Run(() => ReadLoopAsync(_readLoopCts.Token), _readLoopCts.Token);
        }

        public Task WriteAsync(string data, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (!IsConnected || string.IsNullOrEmpty(data))
            {
                return Task.CompletedTask;
            }

            _shellStream!.Write(data);
            _shellStream.Flush();
            return Task.CompletedTask;
        }

        public Task SendInterruptAsync(CancellationToken cancellationToken = default)
        {
            return WriteAsync("\u0003", cancellationToken);
        }

        public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (!IsConnected)
            {
                return Task.CompletedTask;
            }

            _columns = Math.Max(20, columns);
            _rows = Math.Max(5, rows);
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                return;
            }

            Interlocked.Exchange(ref _stopping, 1);

            _readLoopCts?.Cancel();

            if (_readLoopTask != null)
            {
                try
                {
                    await _readLoopTask.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
                }
                catch
                {
                    // Ignore background read loop cancellation errors.
                }
            }

            try
            {
                if (_shellStream != null)
                {
                    _shellStream.ErrorOccurred -= OnShellError;
                    _shellStream.Close();
                }
            }
            catch
            {
                // Ignore dispose errors.
            }

            try
            {
                if (_sshClient != null)
                {
                    _sshClient.ErrorOccurred -= OnClientError;
                    if (_sshClient.IsConnected)
                    {
                        _sshClient.Disconnect();
                    }
                }
            }
            catch
            {
                // Ignore dispose errors.
            }

            _shellStream?.Dispose();
            _shellStream = null;
            _sshClient?.Dispose();
            _sshClient = null;
            _readLoopCts?.Dispose();
            _readLoopCts = null;
            _readLoopTask = null;

            RaiseSessionClosed();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            await StopAsync();
            _disposed = true;
        }

        private void OnClientError(object? sender, ExceptionEventArgs e)
        {
            RaiseSessionClosed();
        }

        private void OnShellError(object? sender, ExceptionEventArgs e)
        {
            RaiseSessionClosed();
        }

        private void RaiseSessionClosed()
        {
            if (Interlocked.Exchange(ref _sessionClosedRaised, 1) != 0)
            {
                return;
            }

            try
            {
                SessionClosed?.Invoke();
            }
            catch
            {
                // UI handlers must not break teardown.
            }
        }

        private async Task ReadLoopAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            try
            {
                while (!cancellationToken.IsCancellationRequested && Volatile.Read(ref _stopping) == 0)
                {
                    var client = _sshClient;
                    var stream = _shellStream;
                    if (client == null || stream == null || !client.IsConnected)
                    {
                        break;
                    }

                    try
                    {
                        if (!stream.DataAvailable)
                        {
                            await Task.Delay(40, cancellationToken);
                            continue;
                        }

                        var read = stream.Read(buffer, 0, buffer.Length);
                        if (read <= 0)
                        {
                            // Channel EOF — shell closed on the server (TMOUT/logout) or TCP died.
                            break;
                        }

                        var text = Encoding.UTF8.GetString(buffer, 0, read);
                        if (!string.IsNullOrEmpty(text))
                        {
                            OutputReceived?.Invoke(text);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (SshConnectionException)
                    {
                        break;
                    }
                    catch (SocketException)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when session is closed.
            }
            catch
            {
                // Terminal handles connection-close state itself.
            }
            finally
            {
                if (Volatile.Read(ref _stopping) == 0)
                {
                    RaiseSessionClosed();
                }
            }
        }

        /// <summary>
        /// SSH protocol keepalives help, but some middleboxes only refresh on TCP keepalive.
        /// Best-effort: find the underlying socket via reflection and enable SO_KEEPALIVE.
        /// </summary>
        private static void TryEnableTcpKeepAlive(SshClient client)
        {
            try
            {
                var sessionProp = typeof(BaseClient).GetProperty(
                    "Session",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var session = sessionProp?.GetValue(client);
                if (session == null)
                {
                    return;
                }

                Socket? socket = null;
                foreach (var name in new[] { "Socket", "_socket", "Connection" })
                {
                    var member = session.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?? (MemberInfo?)session.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (member == null)
                    {
                        continue;
                    }

                    var value = member is PropertyInfo pi ? pi.GetValue(session) : ((FieldInfo)member).GetValue(session);
                    if (value is Socket s)
                    {
                        socket = s;
                        break;
                    }
                }

                if (socket == null || !socket.Connected)
                {
                    return;
                }

                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                // Windows: start probes after 30s idle, every 10s.
                if (OperatingSystem.IsWindows())
                {
                    const uint on = 1;
                    const uint timeMs = 30_000;
                    const uint intervalMs = 10_000;
                    var inValue = new byte[12];
                    BitConverter.GetBytes(on).CopyTo(inValue, 0);
                    BitConverter.GetBytes(timeMs).CopyTo(inValue, 4);
                    BitConverter.GetBytes(intervalMs).CopyTo(inValue, 8);
                    socket.IOControl(IOControlCode.KeepAliveValues, inValue, null);
                }
            }
            catch
            {
                // Optional hardening — SSH keepalives still run.
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SshTerminalSession));
            }
        }
    }
}
