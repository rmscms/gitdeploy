using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GitDeployPro.Services.Cursor
{
    public enum CursorCleanupProfile
    {
        Safe,
        Aggressive
    }

    public sealed class CursorDiskScanResult
    {
        public string RoamingRoot { get; init; } = "";
        public long StateDbBytes { get; init; }
        public long StateBackupBytes { get; init; }
        public long AgentWorkerBytes { get; init; }
        public long CacheBytes { get; init; }
        public long LogsCrashBytes { get; init; }
        public long SnapshotsBytes { get; init; }
        public long HistoryBytes { get; init; }
        public long SearchDbBytes { get; init; }
        public long SafeReclaimableBytes { get; init; }
        public long AggressiveReclaimableBytes { get; init; }
        public long TotalCursorBytes { get; init; }
        public bool CursorRunning { get; init; }
        public IReadOnlyList<string> RunningProcessNames { get; init; } = Array.Empty<string>();
    }

    public sealed class CursorDiskClearResult
    {
        public bool Ok { get; init; }
        public string Message { get; init; } = "";
        public long FreedBytes { get; init; }
        public int DeletedItems { get; init; }
        public bool AbortedBecauseRunning { get; init; }
    }

    /// <summary>
    /// Safe/aggressive cleanup of Cursor AppData caches.
    /// Never deletes live state.vscdb (that breaks chat loading).
    /// </summary>
    public static class CursorDiskCleanupService
    {
        public static string RoamingRoot
            => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Cursor");

        public static string GlobalStorage
            => Path.Combine(RoamingRoot, "User", "globalStorage");

        public static CursorDiskScanResult Scan()
        {
            var root = RoamingRoot;
            var gs = GlobalStorage;
            var stateDb = Path.Combine(gs, "state.vscdb");
            var stateBak = Path.Combine(gs, "state.vscdb.backup");
            var agentWorker = Path.Combine(gs, "anysphere.cursor-agent-worker");
            var searchDb = Path.Combine(gs, "conversation-search.db");

            var cacheDirs = new[]
            {
                Path.Combine(root, "Cache"),
                Path.Combine(root, "Code Cache"),
                Path.Combine(root, "GPUCache"),
                Path.Combine(root, "CachedData"),
                Path.Combine(root, "DawnWebGPUCache"),
                Path.Combine(root, "DawnGraphiteCache"),
                Path.Combine(root, "CachedExtensionVSIXs"),
                Path.Combine(root, "CachedProfilesData")
            };

            var logsCrash = new[]
            {
                Path.Combine(root, "logs"),
                Path.Combine(root, "Crashpad")
            };

            var snapshots = Path.Combine(root, "snapshots");
            var history = Path.Combine(root, "User", "History");

            var stateBytes = FileSize(stateDb) + FileSize(stateDb + "-wal") + FileSize(stateDb + "-shm");
            var bakBytes = FileSize(stateBak);
            var agentBytes = DirSize(agentWorker);
            var cacheBytes = cacheDirs.Sum(DirSize);
            var logsBytes = logsCrash.Sum(DirSize);
            var snapBytes = DirSize(snapshots);
            var histBytes = DirSize(history);
            var searchBytes = FileSize(searchDb)
                              + FileSize(searchDb + "-wal")
                              + FileSize(searchDb + "-shm");

            // Safe: backup + old agent versions (approx all agent folder for scan estimate)
            // + caches + logs/crash + search index
            var safe = bakBytes + EstimateOldAgentVersions(agentWorker) + cacheBytes + logsBytes + searchBytes;
            var aggressive = safe + snapBytes + histBytes;

            var running = GetCursorProcessNames();

            return new CursorDiskScanResult
            {
                RoamingRoot = root,
                StateDbBytes = stateBytes,
                StateBackupBytes = bakBytes,
                AgentWorkerBytes = agentBytes,
                CacheBytes = cacheBytes,
                LogsCrashBytes = logsBytes,
                SnapshotsBytes = snapBytes,
                HistoryBytes = histBytes,
                SearchDbBytes = searchBytes,
                SafeReclaimableBytes = safe,
                AggressiveReclaimableBytes = aggressive,
                TotalCursorBytes = DirSize(root),
                CursorRunning = running.Count > 0,
                RunningProcessNames = running
            };
        }

        public static Task<CursorDiskClearResult> ClearAsync(
            CursorCleanupProfile profile,
            bool forceQuitCursor,
            CancellationToken cancellationToken = default)
            => Task.Run(() => Clear(profile, forceQuitCursor), cancellationToken);

        public static CursorDiskClearResult Clear(CursorCleanupProfile profile, bool forceQuitCursor)
        {
            var running = GetCursorProcessNames();
            if (running.Count > 0)
            {
                if (!forceQuitCursor)
                {
                    return new CursorDiskClearResult
                    {
                        Ok = false,
                        AbortedBecauseRunning = true,
                        Message = "Cursor is running: " + string.Join(", ", running)
                    };
                }

                TryKillCursorProcesses();
                Thread.Sleep(1500);
                running = GetCursorProcessNames();
                if (running.Count > 0)
                {
                    return new CursorDiskClearResult
                    {
                        Ok = false,
                        AbortedBecauseRunning = true,
                        Message = "Could not quit Cursor: " + string.Join(", ", running)
                    };
                }
            }

            // Pause GDP Cursor ACP so agent-cli locks release.
            try
            {
                GitDeployPro.Services.Telegram.CursorAcpPool.Instance.DisposeAll();
            }
            catch
            {
            }

            long freed = 0;
            var deleted = 0;
            var errors = new List<string>();

            var root = RoamingRoot;
            var gs = GlobalStorage;

            // --- Safe allowlist ---
            freed += TryDeleteFile(Path.Combine(gs, "state.vscdb.backup"), ref deleted, errors);
            freed += TryDeleteFile(Path.Combine(gs, "conversation-search.db"), ref deleted, errors);
            freed += TryDeleteFile(Path.Combine(gs, "conversation-search.db-wal"), ref deleted, errors);
            freed += TryDeleteFile(Path.Combine(gs, "conversation-search.db-shm"), ref deleted, errors);
            freed += TryDeleteGdpChatBackups(gs, ref deleted, errors);

            freed += TryPruneOldAgentVersions(
                Path.Combine(gs, "anysphere.cursor-agent-worker"),
                ref deleted,
                errors);

            foreach (var dir in new[]
                     {
                         Path.Combine(root, "Cache"),
                         Path.Combine(root, "Code Cache"),
                         Path.Combine(root, "GPUCache"),
                         Path.Combine(root, "DawnWebGPUCache"),
                         Path.Combine(root, "DawnGraphiteCache"),
                         Path.Combine(root, "CachedExtensionVSIXs"),
                         Path.Combine(root, "CachedProfilesData")
                     })
            {
                freed += TryClearDirectoryContents(dir, ref deleted, errors);
            }

            freed += TryPruneOldCachedData(Path.Combine(root, "CachedData"), ref deleted, errors);
            freed += TryClearDirectoryContents(Path.Combine(root, "logs"), ref deleted, errors);
            freed += TryClearDirectoryContents(Path.Combine(root, "Crashpad", "reports"), ref deleted, errors);
            freed += TryClearDirectoryContents(Path.Combine(root, "Crashpad", "attachments"), ref deleted, errors);

            if (profile == CursorCleanupProfile.Aggressive)
            {
                freed += TryClearDirectoryContents(Path.Combine(root, "snapshots"), ref deleted, errors);
                freed += TryClearDirectoryContents(Path.Combine(root, "User", "History"), ref deleted, errors);
            }

            // NEVER touch live state.vscdb / settings / auth.

            var ok = errors.Count == 0 || freed > 0;
            var msg = freed > 0
                ? $"Freed {FormatBytes(freed)} ({deleted} items)."
                : "Nothing deleted.";
            if (errors.Count > 0)
            {
                msg += " Issues: " + string.Join("; ", errors.Take(3));
            }

            var stateLeft = FileSize(Path.Combine(gs, "state.vscdb"));
            if (stateLeft > 1L * 1024 * 1024 * 1024)
            {
                msg += $" Live state.vscdb still {FormatBytes(stateLeft)} — in Cursor run: GC Agent KV Blobs.";
            }

            return new CursorDiskClearResult
            {
                Ok = ok,
                FreedBytes = freed,
                DeletedItems = deleted,
                Message = msg
            };
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes + " B";
            }

            double mb = bytes / (1024.0 * 1024.0);
            if (mb < 1024)
            {
                return mb.ToString("0.0") + " MB";
            }

            return (mb / 1024.0).ToString("0.00") + " GB";
        }

        public static IReadOnlyList<string> GetCursorProcessNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in new[] { "Cursor", "cursor", "cursor-agent" })
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        try
                        {
                            names.Add(p.ProcessName);
                        }
                        finally
                        {
                            p.Dispose();
                        }
                    }
                }
                catch
                {
                }
            }

            return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void TryKillCursorProcesses()
        {
            foreach (var name in new[] { "Cursor", "cursor", "cursor-agent" })
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        try
                        {
                            p.Kill(entireProcessTree: true);
                            p.WaitForExit(5000);
                        }
                        catch
                        {
                        }
                        finally
                        {
                            p.Dispose();
                        }
                    }
                }
                catch
                {
                }
            }
        }

        private static long FileSize(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static long DirSize(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return 0;
                }

                return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Sum(f =>
                    {
                        try
                        {
                            return new FileInfo(f).Length;
                        }
                        catch
                        {
                            return 0L;
                        }
                    });
            }
            catch
            {
                return 0;
            }
        }

        private static long EstimateOldAgentVersions(string agentWorkerRoot)
        {
            try
            {
                var versions = FindAgentVersionsDir(agentWorkerRoot);
                if (versions == null || !Directory.Exists(versions))
                {
                    return 0;
                }

                var entries = Directory.GetFileSystemEntries(versions)
                    .Select(p => new FileInfo(p))
                    .Where(fi => fi.Exists || Directory.Exists(fi.FullName))
                    .OrderByDescending(fi => fi.LastWriteTimeUtc)
                    .ToList();

                if (entries.Count <= 1)
                {
                    return 0;
                }

                return entries.Skip(1).Sum(fi =>
                    Directory.Exists(fi.FullName) ? DirSize(fi.FullName) : FileSize(fi.FullName));
            }
            catch
            {
                return 0;
            }
        }

        private static string? FindAgentVersionsDir(string agentWorkerRoot)
        {
            if (!Directory.Exists(agentWorkerRoot))
            {
                return null;
            }

            // Typical: ...\.local\share\cursor-agent\versions
            try
            {
                var hit = Directory.EnumerateDirectories(agentWorkerRoot, "versions", SearchOption.AllDirectories)
                    .FirstOrDefault();
                return hit;
            }
            catch
            {
                return null;
            }
        }

        private static long TryPruneOldAgentVersions(string agentWorkerRoot, ref int deleted, List<string> errors)
        {
            long freed = 0;
            try
            {
                var versions = FindAgentVersionsDir(agentWorkerRoot);
                if (versions == null || !Directory.Exists(versions))
                {
                    return 0;
                }

                var entries = Directory.GetFileSystemEntries(versions)
                    .OrderByDescending(p =>
                    {
                        try
                        {
                            return File.GetLastWriteTimeUtc(p);
                        }
                        catch
                        {
                            return DateTime.MinValue;
                        }
                    })
                    .ToList();

                foreach (var old in entries.Skip(1))
                {
                    freed += TryDeletePath(old, ref deleted, errors);
                }
            }
            catch (Exception ex)
            {
                errors.Add(Truncate(ex.Message, 80));
            }

            return freed;
        }

        private static long TryPruneOldCachedData(string cachedDataRoot, ref int deleted, List<string> errors)
        {
            long freed = 0;
            try
            {
                if (!Directory.Exists(cachedDataRoot))
                {
                    return 0;
                }

                var dirs = Directory.GetDirectories(cachedDataRoot)
                    .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
                    .ToList();

                foreach (var old in dirs.Skip(1))
                {
                    freed += TryDeletePath(old, ref deleted, errors);
                }
            }
            catch (Exception ex)
            {
                errors.Add(Truncate(ex.Message, 80));
            }

            return freed;
        }

        private static long TryClearDirectoryContents(string dir, ref int deleted, List<string> errors)
        {
            long freed = 0;
            try
            {
                if (!Directory.Exists(dir))
                {
                    return 0;
                }

                foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                {
                    freed += TryDeletePath(entry, ref deleted, errors);
                }
            }
            catch (Exception ex)
            {
                errors.Add(Truncate(ex.Message, 80));
            }

            return freed;
        }

        private static long TryDeleteGdpChatBackups(string globalStorage, ref int deleted, List<string> errors)
        {
            long freed = 0;
            try
            {
                if (!Directory.Exists(globalStorage))
                {
                    return 0;
                }

                foreach (var path in Directory.EnumerateFiles(globalStorage, "state.vscdb.gdp-bak-*"))
                {
                    freed += TryDeleteFile(path, ref deleted, errors);
                }
            }
            catch (Exception ex)
            {
                errors.Add(Truncate(ex.Message, 80));
            }

            return freed;
        }

        private static long TryDeleteFile(string path, ref int deleted, List<string> errors)
            => TryDeletePath(path, ref deleted, errors);

        private static long TryDeletePath(string path, ref int deleted, List<string> errors)
        {
            try
            {
                if (File.Exists(path))
                {
                    var size = new FileInfo(path).Length;
                    File.Delete(path);
                    deleted++;
                    return size;
                }

                if (Directory.Exists(path))
                {
                    var size = DirSize(path);
                    Directory.Delete(path, recursive: true);
                    deleted++;
                    return size;
                }
            }
            catch (Exception ex)
            {
                errors.Add(Truncate(Path.GetFileName(path) + ": " + ex.Message, 80));
            }

            return 0;
        }

        private static string Truncate(string value, int max)
            => string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
    }
}
