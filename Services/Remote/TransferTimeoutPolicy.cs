using System;
using System.Threading;

namespace GitDeployPro.Services.Remote
{
    /// <summary>
    /// Per-file transfer budget from size. Idle stall aborts a hung socket
    /// without killing a slow upload that is still sending bytes.
    /// </summary>
    internal static class TransferTimeoutPolicy
    {
        public const int ConnectMs = 20_000;
        public const int IdleStallMs = 120_000;
        public const long LargeFileBytes = 8L * 1024 * 1024;
        public const int MinTransferMs = 120_000;
        public const int MaxTransferMs = 6 * 60 * 60 * 1000;
        public const long WorstCaseBytesPerSecond = 32 * 1024;

        public static int IdleStallMsFor(long sizeBytes) =>
            sizeBytes >= LargeFileBytes ? 240_000 : IdleStallMs;

        public static TimeSpan ForFileBytes(long sizeBytes)
        {
            var size = Math.Max(0, sizeBytes);
            var transferMs = size * 1000d / WorstCaseBytesPerSecond;
            return TimeSpan.FromMilliseconds(Math.Clamp(MinTransferMs + transferMs, MinTransferMs, MaxTransferMs));
        }

        public static string Describe(long sizeBytes)
        {
            var span = ForFileBytes(sizeBytes);
            return span.TotalHours >= 1
                ? $"{span.TotalHours:0.#}h"
                : $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}m";
        }

        public static string StalledMessage(long sizeBytes)
        {
            return $"No progress for {IdleStallMsFor(sizeBytes) / 1000}s (budget {Describe(sizeBytes)}). Partial file removed — retry this file.";
        }
    }

    internal sealed class TransferStallWatchdog : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly System.Threading.Timer _timer;
        private long _lastBytes;
        private long _lastProgressTick;
        private int _canceled;

        public CancellationToken Token => _cts.Token;

        private readonly int _stallMs;

        public TransferStallWatchdog(CancellationToken parent, int? stallMs = null)
        {
            _stallMs = stallMs ?? TransferTimeoutPolicy.IdleStallMs;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
            _lastProgressTick = Environment.TickCount64;
            _timer = new System.Threading.Timer(Check, null, 5000, 5000);
        }

        public void Pulse(long bytes)
        {
            if (bytes < 0)
            {
                return;
            }

            if (bytes >= Interlocked.Read(ref _lastBytes))
            {
                Interlocked.Exchange(ref _lastBytes, bytes);
                Interlocked.Exchange(ref _lastProgressTick, Environment.TickCount64);
            }
        }

        private void Check(object? _)
        {
            if (Environment.TickCount64 - Interlocked.Read(ref _lastProgressTick) < _stallMs)
            {
                return;
            }

            if (Interlocked.Exchange(ref _canceled, 1) != 0)
            {
                return;
            }

            try
            {
                _cts.Cancel();
            }
            catch
            {
                // Ignore dispose races.
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
            _cts.Dispose();
        }
    }
}
