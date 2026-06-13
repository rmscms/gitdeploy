using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GitDeployPro.Services.Terminal
{
    public sealed class RedirectedProcessTerminalSession : ITerminalSession
    {
        private readonly string _shell;
        private readonly string _workingDirectory;

        private Process? _process;
        private CancellationTokenSource? _cts;
        private Task? _outputTask;
        private Task? _errorTask;
        private bool _disposed;

        public event Action<string>? OutputReceived;
        public event Action? SessionClosed;

        public bool IsConnected => _process != null && !_process.HasExited;

        public RedirectedProcessTerminalSession(string shell, string workingDirectory)
        {
            _shell = string.IsNullOrWhiteSpace(shell) ? "cmd.exe" : shell;
            _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? "C:\\" : workingDirectory;
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _shell,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = _workingDirectory,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false)
                }
            };

            if (!_process.Start())
            {
                throw new InvalidOperationException($"Unable to start fallback shell '{_shell}'.");
            }

            _process.StandardInput.AutoFlush = true;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _outputTask = Task.Run(() => ReadLoopAsync(_process.StandardOutput, _cts.Token), _cts.Token);
            _errorTask = Task.Run(() => ReadLoopAsync(_process.StandardError, _cts.Token), _cts.Token);

            return Task.CompletedTask;
        }

        public async Task WriteAsync(string data, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (!IsConnected || string.IsNullOrEmpty(data))
            {
                return;
            }

            await _process!.StandardInput.WriteAsync(data);
            await _process.StandardInput.FlushAsync();
        }

        public Task SendInterruptAsync(CancellationToken cancellationToken = default)
        {
            return WriteAsync("\u0003", cancellationToken);
        }

        public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                return;
            }

            _cts?.Cancel();

            if (_process != null)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill(true);
                    }
                }
                catch
                {
                    // Ignore shutdown race.
                }
            }

            if (_outputTask != null)
            {
                try { await _outputTask; } catch { }
            }

            if (_errorTask != null)
            {
                try { await _errorTask; } catch { }
            }

            _cts?.Dispose();
            _cts = null;
            _outputTask = null;
            _errorTask = null;

            _process?.Dispose();
            _process = null;

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

        private async Task ReadLoopAsync(System.IO.StreamReader reader, CancellationToken cancellationToken)
        {
            var buffer = new char[2048];
            try
            {
                while (!cancellationToken.IsCancellationRequested && IsConnected)
                {
                    var read = await reader.ReadAsync(buffer, 0, buffer.Length);
                    if (read > 0)
                    {
                        OutputReceived?.Invoke(new string(buffer, 0, read));
                    }
                    else
                    {
                        await Task.Delay(50, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
            catch
            {
                // Terminal UI handles disconnect status.
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RedirectedProcessTerminalSession));
            }
        }
    }
}
