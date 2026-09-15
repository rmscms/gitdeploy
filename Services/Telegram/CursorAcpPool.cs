using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// Per-project warm ACP daemons. Idle children are disposed after <see cref="IdleTimeout"/>.
    /// </summary>
    internal sealed class CursorAcpPool : IDisposable
    {
        public static CursorAcpPool Instance { get; } = new();

        public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(45);

        private readonly ConcurrentDictionary<string, Lazy<CursorAcpDaemon>> _daemons =
            new(StringComparer.OrdinalIgnoreCase);

        private System.Threading.Timer? _reaper;
        private bool _disposed;

        private CursorAcpPool()
        {
            _reaper = new System.Threading.Timer(
                _ => ReapIdle(),
                null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(5));
        }

        public async Task<CursorAcpDaemon> GetOrCreateAsync(
            string projectPath,
            string agentPath,
            string? nodeExe,
            string? indexJs,
            string? model,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            var key = TelegramPaths.ToProjectKey(projectPath);
            var lazy = _daemons.GetOrAdd(
                key,
                _ => new Lazy<CursorAcpDaemon>(
                    () => new CursorAcpDaemon(projectPath, agentPath, nodeExe, indexJs, model),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            CursorAcpDaemon daemon;
            try
            {
                daemon = lazy.Value;
            }
            catch
            {
                _daemons.TryRemove(key, out _);
                throw;
            }

            // Path/model changed while a stale daemon was cached — replace.
            if (!string.Equals(daemon.AgentPath, agentPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(daemon.Model ?? string.Empty, model ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(daemon.ProjectPath, projectPath, StringComparison.OrdinalIgnoreCase))
            {
                DisposeProject(projectPath);
                return await GetOrCreateAsync(projectPath, agentPath, nodeExe, indexJs, model, cancellationToken)
                    .ConfigureAwait(false);
            }

            await daemon.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            return daemon;
        }

        public bool TryGetDaemon(string projectPath, out CursorAcpDaemon? daemon)
        {
            daemon = null;
            var key = TelegramPaths.ToProjectKey(projectPath);
            if (!_daemons.TryGetValue(key, out var lazy) || !lazy.IsValueCreated)
            {
                return false;
            }

            try
            {
                daemon = lazy.Value;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void DisposeProject(string projectPath)
        {
            var key = TelegramPaths.ToProjectKey(projectPath);
            if (_daemons.TryRemove(key, out var lazy) && lazy.IsValueCreated)
            {
                try
                {
                    lazy.Value.Dispose();
                }
                catch
                {
                }
            }
        }

        public void DisposeAll()
        {
            foreach (var key in _daemons.Keys)
            {
                if (_daemons.TryRemove(key, out var lazy) && lazy.IsValueCreated)
                {
                    try
                    {
                        lazy.Value.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _reaper?.Dispose();
            }
            catch
            {
            }

            _reaper = null;
            DisposeAll();
        }

        private void ReapIdle()
        {
            if (_disposed)
            {
                return;
            }

            var now = DateTime.UtcNow;
            foreach (var pair in _daemons)
            {
                if (!pair.Value.IsValueCreated)
                {
                    continue;
                }

                CursorAcpDaemon daemon;
                try
                {
                    daemon = pair.Value.Value;
                }
                catch
                {
                    _daemons.TryRemove(pair.Key, out _);
                    continue;
                }

                if (daemon.IsAlive && now - daemon.LastActivityUtc < IdleTimeout)
                {
                    continue;
                }

                if (_daemons.TryRemove(pair.Key, out var removed) && removed.IsValueCreated)
                {
                    try
                    {
                        removed.Value.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(CursorAcpPool));
            }
        }
    }

    internal sealed class CursorAcpAuthException : Exception
    {
        public CursorAcpAuthException(string message, Exception? inner = null)
            : base(message, inner)
        {
        }
    }
}
