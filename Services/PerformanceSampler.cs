using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace GitDeployPro.Services
{
    public sealed class PerformanceSampler
    {
        private const long MaxLogSizeBytes = 5 * 1024 * 1024;
        private static readonly TimeSpan ConfigCacheTtl = TimeSpan.FromSeconds(30);

        private readonly object _sync = new();
        private readonly ConfigurationService _configService = new();
        private readonly string _logDirectory;
        private bool? _cachedEnabled;
        private DateTime _cacheExpiresUtc = DateTime.MinValue;

        public static PerformanceSampler Instance { get; } = new();

        private PerformanceSampler()
        {
            _logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GitDeployPro",
                "perf");
        }

        public PerfScope BeginScope(string area, string operation, string? detail = null)
        {
            return new PerfScope(this, area, operation, detail);
        }

        public void Mark(string area, string operation, string phase, string? detail = null, Exception? exception = null, long? elapsedMs = null)
        {
            if (!IsEnabled())
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(_logDirectory);
                string path = GetCurrentLogPath();

                RotateIfNeeded(path);

                var process = Process.GetCurrentProcess();
                var payload = new
                {
                    timestampUtc = AppTimeService.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    area,
                    operation,
                    phase,
                    detail,
                    elapsedMs,
                    processId = process.Id,
                    workingSetMb = Math.Round(process.WorkingSet64 / 1024d / 1024d, 2),
                    privateMb = Math.Round(process.PrivateMemorySize64 / 1024d / 1024d, 2),
                    gcHeapMb = Math.Round(GC.GetTotalMemory(false) / 1024d / 1024d, 2),
                    threadCount = process.Threads.Count,
                    gen0Collections = GC.CollectionCount(0),
                    gen1Collections = GC.CollectionCount(1),
                    gen2Collections = GC.CollectionCount(2),
                    exception = exception?.GetType().Name,
                    error = exception?.Message
                };

                var json = JsonSerializer.Serialize(payload);
                lock (_sync)
                {
                    File.AppendAllText(path, json + Environment.NewLine);
                }
            }
            catch
            {
                // Instrumentation must never affect app behavior.
            }
        }

        private bool IsEnabled()
        {
            var env = Environment.GetEnvironmentVariable("GDP_PERF_LOG");
            if (!string.IsNullOrWhiteSpace(env) && TryParseBool(env, out bool envEnabled))
            {
                return envEnabled;
            }

            var now = AppTimeService.UtcNow;
            if (_cachedEnabled.HasValue && now <= _cacheExpiresUtc)
            {
                return _cachedEnabled.Value;
            }

            try
            {
                _cachedEnabled = _configService.LoadGlobalConfig().EnablePerformanceSampling;
            }
            catch
            {
                _cachedEnabled = false;
            }

            _cacheExpiresUtc = now.Add(ConfigCacheTtl);
            return _cachedEnabled.Value;
        }

        private static bool TryParseBool(string value, out bool parsed)
        {
            var normalized = value.Trim();
            if (string.Equals(normalized, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "yes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "on", StringComparison.OrdinalIgnoreCase))
            {
                parsed = true;
                return true;
            }

            if (string.Equals(normalized, "0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "no", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "false", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "off", StringComparison.OrdinalIgnoreCase))
            {
                parsed = false;
                return true;
            }

            parsed = false;
            return false;
        }

        private string GetCurrentLogPath()
        {
            return Path.Combine(_logDirectory, $"perf-{AppTimeService.LocalNow:yyyyMMdd}.jsonl");
        }

        private void RotateIfNeeded(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < MaxLogSizeBytes)
                {
                    return;
                }

                string archive = Path.Combine(
                    _logDirectory,
                    $"perf-{AppTimeService.LocalNow:yyyyMMdd-HHmmss}.jsonl");
                File.Move(path, archive, overwrite: true);
            }
            catch
            {
                // Ignore rotation errors.
            }
        }

        public sealed class PerfScope : IDisposable
        {
            private readonly PerformanceSampler _sampler;
            private readonly string _area;
            private readonly string _operation;
            private readonly string? _detail;
            private readonly Stopwatch _stopwatch;
            private bool _completed;

            internal PerfScope(PerformanceSampler sampler, string area, string operation, string? detail)
            {
                _sampler = sampler;
                _area = area;
                _operation = operation;
                _detail = detail;
                _stopwatch = Stopwatch.StartNew();
                _sampler.Mark(_area, _operation, "start", _detail);
            }

            public void Fail(Exception exception)
            {
                Complete("error", exception);
            }

            private void Complete(string phase, Exception? exception = null)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
                _stopwatch.Stop();
                _sampler.Mark(_area, _operation, phase, _detail, exception, _stopwatch.ElapsedMilliseconds);
            }

            public void Dispose()
            {
                Complete("end");
            }
        }
    }
}
