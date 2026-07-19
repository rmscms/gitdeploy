using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace GitDeployPro.Services.Terminal
{
    public sealed class SshTerminalSession : ITerminalSession
    {
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
                    new PasswordAuthenticationMethod(_user, _password));

                _sshClient = new SshClient(connectionInfo);
                _sshClient.Connect();

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
                _shellStream?.Close();
            }
            catch
            {
                // Ignore dispose errors.
            }

            try
            {
                if (_sshClient?.IsConnected == true)
                {
                    _sshClient.Disconnect();
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

            SessionClosed?.Invoke();
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

        private async Task ReadLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && IsConnected)
                {
                    if (_shellStream!.DataAvailable)
                    {
                        var text = _shellStream.Read();
                        if (!string.IsNullOrEmpty(text))
                        {
                            OutputReceived?.Invoke(text);
                        }
                    }
                    else
                    {
                        await Task.Delay(30, cancellationToken);
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
                SessionClosed?.Invoke();
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
