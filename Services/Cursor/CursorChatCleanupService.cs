using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;

namespace GitDeployPro.Services.Cursor
{
    public sealed class CursorChatProjectRow
    {
        public string WorkspaceId { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string ProjectPath { get; init; } = "";
        public int ChatCount { get; init; }
        public int OlderThanDaysCount { get; init; }
        public DateTime? NewestUtc { get; init; }
        public DateTime? OldestUtc { get; init; }
        public bool IsSelected { get; set; } = true;
    }

    public sealed class CursorChatScanResult
    {
        public bool Ok { get; init; }
        public string Error { get; init; } = "";
        public long DatabaseBytes { get; init; }
        public long LiveBytes { get; init; }
        public long FreelistBytes { get; init; }
        public int AgentBlobCount { get; init; }
        public int TotalChats { get; init; }
        public int OlderThanDaysTotal { get; init; }
        public int DaysFilter { get; init; }
        public bool CursorRunning { get; init; }
        public IReadOnlyList<CursorChatProjectRow> Projects { get; init; } = Array.Empty<CursorChatProjectRow>();
    }

    public sealed class CursorChatDeleteProgress
    {
        public string Phase { get; init; } = "";
        public int Current { get; init; }
        public int Total { get; init; }
        public int DeletedKvRows { get; init; }
        public TimeSpan Elapsed { get; init; }
        public string Detail { get; init; } = "";
    }

    public sealed class CursorChatDeleteResult
    {
        public bool Ok { get; init; }
        public string Message { get; init; } = "";
        public int DeletedChats { get; init; }
        public int DeletedKvRows { get; init; }
        public int DeletedOrphanBlobs { get; init; }
        public int SkippedUnsafeIds { get; init; }
        public bool Vacuumed { get; init; }
        public long BytesBefore { get; init; }
        public long BytesAfter { get; init; }
        public bool AbortedBecauseRunning { get; init; }
        public bool AbortedBecauseSensitive { get; init; }
        public TimeSpan Elapsed { get; init; }
    }

    /// <summary>
    /// Lists Cursor chats by workspace and deletes chats older than N days from global state.vscdb.
    /// Only touches composerHeaders + allowlisted cursorDiskKV chat keys.
    /// Never writes/deletes ItemTable (auth, secrets, settings).
    /// Requires Cursor to be fully quit before delete/VACUUM.
    /// </summary>
    public static class CursorChatCleanupService
    {
        /// <summary>Only these cursorDiskKV key shapes may be deleted (composerId must be a real UUID).</summary>
        // Allowlist enforced in MaterializeKvRowidsToKill SQL (prefix + substr id extract).

        private static readonly string[] ProtectedItemKeyPrefixes =
        {
            "cursorAuth/",
            "secret://",
            "cloudAgentRepository.",
            "adminSettings.cachedAuthId",
            "glass.lastSignedInAuthId",
            "mcpOAuth.",
            "interactive.sessions"
        };

        /// <summary>System composer ids that must never be deleted (not real chats).</summary>
        private static readonly HashSet<string> ReservedComposerIds = new(StringComparer.OrdinalIgnoreCase)
        {
            "empty-state-draft"
        };

        private static readonly System.Text.RegularExpressions.Regex Hex64Regex =
            new(@"\b[a-fA-F0-9]{64}\b", System.Text.RegularExpressions.RegexOptions.Compiled);

        public static string GlobalDbPath
            => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Cursor", "User", "globalStorage", "state.vscdb");

        public static string WorkspaceStorageRoot
            => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Cursor", "User", "workspaceStorage");

        public static CursorChatScanResult Scan(int olderThanDays)
        {
            olderThanDays = Math.Clamp(olderThanDays, 1, 3650);
            var dbPath = GlobalDbPath;
            if (!File.Exists(dbPath))
            {
                return new CursorChatScanResult
                {
                    Ok = false,
                    Error = "Cursor state.vscdb not found.",
                    DaysFilter = olderThanDays,
                    CursorRunning = CursorDiskCleanupService.GetCursorProcessNames().Count > 0
                };
            }

            try
            {
                var cutoffMs = DateTimeOffset.UtcNow.AddDays(-olderThanDays).ToUnixTimeMilliseconds();
                var map = LoadWorkspaceMap();
                var rows = new List<CursorChatProjectRow>();
                var total = 0;
                var olderTotal = 0;

                using var con = OpenReadOnly(dbPath);
                var space = ReadDbSpace(con);
                var blobCount = CountAgentBlobs(con);

                using var cmd = con.CreateCommand();
                cmd.CommandText =
                    """
                    SELECT workspaceId,
                           COUNT(*) AS chatCount,
                           SUM(CASE WHEN COALESCE(lastUpdatedAt, createdAt, 0) > 0
                                         AND COALESCE(lastUpdatedAt, createdAt, 0) < $cut
                                    THEN 1 ELSE 0 END) AS olderCount,
                           MIN(NULLIF(COALESCE(lastUpdatedAt, createdAt), 0)),
                           MAX(NULLIF(COALESCE(lastUpdatedAt, createdAt), 0))
                    FROM composerHeaders
                    WHERE IFNULL(isSubagent, 0) = 0
                      AND composerId NOT IN ('empty-state-draft')
                    GROUP BY workspaceId
                    ORDER BY olderCount DESC, chatCount DESC
                    """;
                cmd.Parameters.AddWithValue("$cut", cutoffMs);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var wid = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    var count = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                    var older = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2));
                    DateTime? oldest = reader.IsDBNull(3) ? null : FromUnixMs(Convert.ToInt64(reader.GetValue(3)));
                    DateTime? newest = reader.IsDBNull(4) ? null : FromUnixMs(Convert.ToInt64(reader.GetValue(4)));

                    total += count;
                    olderTotal += older;

                    map.TryGetValue(wid ?? string.Empty, out var info);
                    rows.Add(new CursorChatProjectRow
                    {
                        WorkspaceId = wid ?? string.Empty,
                        DisplayName = info.DisplayName,
                        ProjectPath = info.Path,
                        ChatCount = count,
                        OlderThanDaysCount = older,
                        OldestUtc = oldest,
                        NewestUtc = newest,
                        IsSelected = older > 0
                    });
                }

                return new CursorChatScanResult
                {
                    Ok = true,
                    DatabaseBytes = space.FileBytes > 0 ? space.FileBytes : new FileInfo(dbPath).Length,
                    LiveBytes = space.LiveBytes,
                    FreelistBytes = space.FreelistBytes,
                    AgentBlobCount = blobCount,
                    TotalChats = total,
                    OlderThanDaysTotal = olderTotal,
                    DaysFilter = olderThanDays,
                    CursorRunning = CursorDiskCleanupService.GetCursorProcessNames().Count > 0,
                    Projects = rows
                };
            }
            catch (Exception ex)
            {
                return new CursorChatScanResult
                {
                    Ok = false,
                    Error = ex.Message,
                    DaysFilter = olderThanDays,
                    CursorRunning = CursorDiskCleanupService.GetCursorProcessNames().Count > 0
                };
            }
        }

        public static Task<CursorChatDeleteResult> DeleteOlderAsync(
            int olderThanDays,
            IEnumerable<string>? workspaceIds,
            bool vacuum,
            bool forceQuitCursor,
            bool purgeOrphanBlobs = true,
            IProgress<CursorChatDeleteProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.Run(
                () => DeleteOlder(
                    olderThanDays,
                    workspaceIds,
                    vacuum,
                    forceQuitCursor,
                    purgeOrphanBlobs,
                    progress,
                    cancellationToken),
                cancellationToken);

        public static CursorChatDeleteResult DeleteOlder(
            int olderThanDays,
            IEnumerable<string>? workspaceIds,
            bool vacuum,
            bool forceQuitCursor,
            bool purgeOrphanBlobs = true,
            IProgress<CursorChatDeleteProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            void Report(string phase, int current, int total, int kvDeleted = 0, string detail = "")
            {
                progress?.Report(new CursorChatDeleteProgress
                {
                    Phase = phase,
                    Current = current,
                    Total = total,
                    DeletedKvRows = kvDeleted,
                    Elapsed = sw.Elapsed,
                    Detail = detail
                });
            }

            olderThanDays = Math.Clamp(olderThanDays, 1, 3650);
            var running = CursorDiskCleanupService.GetCursorProcessNames();
            if (running.Count > 0)
            {
                if (!forceQuitCursor)
                {
                    return new CursorChatDeleteResult
                    {
                        Ok = false,
                        AbortedBecauseRunning = true,
                        Message = "Cursor is running: " + string.Join(", ", running),
                        Elapsed = sw.Elapsed
                    };
                }

                Report("quit", 0, 0, detail: "Closing Cursor…");
                TryKillCursor();
                Thread.Sleep(1500);
                running = CursorDiskCleanupService.GetCursorProcessNames();
                if (running.Count > 0)
                {
                    return new CursorChatDeleteResult
                    {
                        Ok = false,
                        AbortedBecauseRunning = true,
                        Message = "Could not quit Cursor: " + string.Join(", ", running),
                        Elapsed = sw.Elapsed
                    };
                }
            }

            try
            {
                GitDeployPro.Services.Telegram.CursorAcpPool.Instance.DisposeAll();
            }
            catch
            {
            }

            var dbPath = GlobalDbPath;
            if (!File.Exists(dbPath))
            {
                return new CursorChatDeleteResult
                {
                    Ok = false,
                    Message = "state.vscdb not found.",
                    Elapsed = sw.Elapsed
                };
            }

            var before = new FileInfo(dbPath).Length;
            var cutoffMs = DateTimeOffset.UtcNow.AddDays(-olderThanDays).ToUnixTimeMilliseconds();
            var selected = workspaceIds?
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            try
            {
                Report("backup", 0, 0, detail: "Backing up state.vscdb…");
                var bak = dbPath + $".gdp-bak-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
                File.Copy(dbPath, bak, overwrite: false);

                using var con = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadWrite
                }.ToString());
                con.Open();

                // Snapshot protected ItemTable rows — we never modify ItemTable, but verify after.
                var authBefore = SnapshotProtectedItemKeys(con);
                if (authBefore.Count == 0)
                {
                    Report("auth", 0, 0, detail: "No cursorAuth keys found (already logged out?). Continuing…");
                }

                Report("collect", 0, 0, detail: "Collecting old chat ids…");
                var ids = CollectComposerIds(con, cutoffMs, selected, includeSubagentsInSelectedWorkspaces: true);
                cancellationToken.ThrowIfCancellationRequested();

                // Skip reserved/invalid ids — do NOT abort the whole job (empty-state-draft etc).
                var skipped = ids.Count(id => !IsSafeComposerId(id) || IsReservedComposerId(id));
                ids = ids
                    .Where(id => IsSafeComposerId(id) && !IsReservedComposerId(id))
                    .ToList();

                if (ids.Count == 0 && !purgeOrphanBlobs)
                {
                    return new CursorChatDeleteResult
                    {
                        Ok = true,
                        Message = skipped > 0
                            ? $"No deletable chats (skipped {skipped} system/invalid id(s))."
                            : "No chats matched the age filter.",
                        SkippedUnsafeIds = skipped,
                        BytesBefore = before,
                        BytesAfter = before,
                        Elapsed = sw.Elapsed
                    };
                }

                var kvDeleted = 0;
                var orphanDeleted = 0;

                if (ids.Count > 0)
                {
                    Report("delete", 0, ids.Count, detail: $"Deleting {ids.Count} chats…");
                    Report("prepare", 0, ids.Count, detail: "Storing chat ids in temp…");
                    EnsureTempIdTable(con, ids);

                    Report("scan", 0, ids.Count, detail: "Collecting KV rowids (1 pass, no join)…");
                    var keyed = MaterializeKvRowidsToKill(con);
                    Report("scan", keyed, Math.Max(1, keyed), keyed, $"Queued {keyed:N0} KV rows");

                    using (var tx = con.BeginTransaction())
                    {
                        Report("kv", 0, Math.Max(1, keyed), detail: $"Deleting {keyed:N0} KV rows by rowid…");
                        kvDeleted = DeleteKvByTempRowids(con, tx);
                        Report("kv", kvDeleted, Math.Max(1, Math.Max(keyed, 1)), kvDeleted,
                            $"Removed {kvDeleted:N0} KV rows");

                        Report("headers", 0, ids.Count, kvDeleted, "Deleting composerHeaders…");
                        var headersDeleted = DeleteHeadersByTempIds(con, tx);
                        DropTempCleanupTables(con, tx);
                        tx.Commit();
                        Report("headers", ids.Count, ids.Count, kvDeleted, $"Removed {headersDeleted} header rows");
                    }
                }

                if (purgeOrphanBlobs)
                {
                    Report("orphans", 0, 1, kvDeleted, "Purging orphan agentKv blobs / composer.content…");
                    orphanDeleted = PurgeOrphanContentAddressed(con, sw, progress, cancellationToken);
                    Report("orphans", 1, 1, kvDeleted + orphanDeleted,
                        $"Removed {orphanDeleted:N0} orphan content rows");
                }

                // Verify ItemTable protected keys untouched.
                var authAfter = SnapshotProtectedItemKeys(con);
                if (!ProtectedSnapshotsEqual(authBefore, authAfter))
                {
                    return new CursorChatDeleteResult
                    {
                        Ok = false,
                        AbortedBecauseSensitive = true,
                        Message =
                            "Aborted after delete: protected ItemTable keys changed unexpectedly. " +
                            "Restore from the .gdp-bak backup next to state.vscdb.",
                        DeletedChats = ids.Count,
                        DeletedKvRows = kvDeleted,
                        DeletedOrphanBlobs = orphanDeleted,
                        SkippedUnsafeIds = skipped,
                        BytesBefore = before,
                        BytesAfter = new FileInfo(dbPath).Length,
                        Elapsed = sw.Elapsed
                    };
                }

                var vacuumed = false;
                if (vacuum)
                {
                    Report("vacuum", 0, 1, kvDeleted + orphanDeleted, "VACUUM (can take several minutes)…");
                    using var vac = con.CreateCommand();
                    vac.CommandText = "VACUUM";
                    vac.CommandTimeout = 0;
                    vac.ExecuteNonQuery();
                    vacuumed = true;

                    var authAfterVac = SnapshotProtectedItemKeys(con);
                    if (!ProtectedSnapshotsEqual(authBefore, authAfterVac))
                    {
                        return new CursorChatDeleteResult
                        {
                            Ok = false,
                            AbortedBecauseSensitive = true,
                            Message = "VACUUM finished but protected ItemTable keys changed. Restore .gdp-bak backup.",
                            DeletedChats = ids.Count,
                            DeletedKvRows = kvDeleted,
                            DeletedOrphanBlobs = orphanDeleted,
                            SkippedUnsafeIds = skipped,
                            Vacuumed = true,
                            BytesBefore = before,
                            BytesAfter = new FileInfo(dbPath).Length,
                            Elapsed = sw.Elapsed
                        };
                    }

                    Report("vacuum", 1, 1, kvDeleted + orphanDeleted, "VACUUM done");
                }

                con.Close();
                SqliteConnection.ClearAllPools();

                var after = new FileInfo(dbPath).Length;
                var freed = Math.Max(0, before - after);
                var elapsed = FormatElapsed(sw.Elapsed);
                var skipNote = skipped > 0 ? $" Skipped {skipped} system id(s)." : "";
                var orphanNote = purgeOrphanBlobs ? $" Orphans {orphanDeleted:N0}." : "";
                var msg = vacuumed
                    ? $"Deleted {ids.Count} chats ({kvDeleted:N0} KV){orphanNote} in {elapsed}.{skipNote} Auth OK. VACUUM freed {CursorDiskCleanupService.FormatBytes(freed)}."
                    : $"Deleted {ids.Count} chats ({kvDeleted:N0} KV){orphanNote} in {elapsed}.{skipNote} Auth OK. File still {CursorDiskCleanupService.FormatBytes(after)} — enable VACUUM to shrink (freelist stays until then).";

                Report("done", ids.Count, Math.Max(1, ids.Count), kvDeleted + orphanDeleted, msg);
                return new CursorChatDeleteResult
                {
                    Ok = true,
                    Message = msg,
                    DeletedChats = ids.Count,
                    DeletedKvRows = kvDeleted,
                    DeletedOrphanBlobs = orphanDeleted,
                    SkippedUnsafeIds = skipped,
                    Vacuumed = vacuumed,
                    BytesBefore = before,
                    BytesAfter = after,
                    Elapsed = sw.Elapsed
                };
            }
            catch (OperationCanceledException)
            {
                return new CursorChatDeleteResult
                {
                    Ok = false,
                    Message = "Cancelled.",
                    BytesBefore = before,
                    BytesAfter = File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0,
                    Elapsed = sw.Elapsed
                };
            }
            catch (Exception ex)
            {
                return new CursorChatDeleteResult
                {
                    Ok = false,
                    Message = ex.Message,
                    BytesBefore = before,
                    BytesAfter = File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0,
                    Elapsed = sw.Elapsed
                };
            }
        }

        // Placeholder keeps the old method body from being duplicated — removed below.
        // --- helpers ---

        private static List<string> CollectComposerIds(
            SqliteConnection con,
            long cutoffMs,
            List<string> selectedWorkspaces,
            bool includeSubagentsInSelectedWorkspaces)
        {
            var ids = new List<string>();
            using (var pick = con.CreateCommand())
            {
                if (selectedWorkspaces.Count == 0)
                {
                    pick.CommandText =
                        """
                        SELECT composerId FROM composerHeaders
                        WHERE IFNULL(isSubagent, 0) = 0
                          AND COALESCE(lastUpdatedAt, createdAt, 0) > 0
                          AND COALESCE(lastUpdatedAt, createdAt, 0) < $cut
                        """;
                }
                else
                {
                    var inList = string.Join(",", selectedWorkspaces.Select((_, i) => "$w" + i));
                    pick.CommandText =
                        $"""
                        SELECT composerId FROM composerHeaders
                        WHERE IFNULL(isSubagent, 0) = 0
                          AND COALESCE(lastUpdatedAt, createdAt, 0) > 0
                          AND COALESCE(lastUpdatedAt, createdAt, 0) < $cut
                          AND workspaceId IN ({inList})
                        """;
                    for (var i = 0; i < selectedWorkspaces.Count; i++)
                    {
                        pick.Parameters.AddWithValue("$w" + i, selectedWorkspaces[i]);
                    }
                }

                pick.Parameters.AddWithValue("$cut", cutoffMs);
                using var r = pick.ExecuteReader();
                while (r.Read())
                {
                    ids.Add(r.GetString(0));
                }
            }

            // Subagents only inside selected workspaces (never global wipe).
            if (includeSubagentsInSelectedWorkspaces)
            {
                using var sub = con.CreateCommand();
                if (selectedWorkspaces.Count == 0)
                {
                    sub.CommandText =
                        """
                        SELECT composerId FROM composerHeaders
                        WHERE IFNULL(isSubagent, 0) = 1
                          AND COALESCE(lastUpdatedAt, createdAt, 0) > 0
                          AND COALESCE(lastUpdatedAt, createdAt, 0) < $cut
                        """;
                }
                else
                {
                    var inList = string.Join(",", selectedWorkspaces.Select((_, i) => "$sw" + i));
                    sub.CommandText =
                        $"""
                        SELECT composerId FROM composerHeaders
                        WHERE IFNULL(isSubagent, 0) = 1
                          AND COALESCE(lastUpdatedAt, createdAt, 0) > 0
                          AND COALESCE(lastUpdatedAt, createdAt, 0) < $cut
                          AND workspaceId IN ({inList})
                        """;
                    for (var i = 0; i < selectedWorkspaces.Count; i++)
                    {
                        sub.Parameters.AddWithValue("$sw" + i, selectedWorkspaces[i]);
                    }
                }

                sub.Parameters.AddWithValue("$cut", cutoffMs);
                using var r = sub.ExecuteReader();
                while (r.Read())
                {
                    ids.Add(r.GetString(0));
                }
            }

            return ids
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Where(id => !IsReservedComposerId(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void EnsureTempIdTable(SqliteConnection con, List<string> ids)
        {
            using (var pragma = con.CreateCommand())
            {
                pragma.CommandText = "PRAGMA temp_store = MEMORY";
                pragma.ExecuteNonQuery();
            }

            Exec(con, "DROP TABLE IF EXISTS _gdp_kill");
            Exec(con, "DROP TABLE IF EXISTS _gdp_ids");
            Exec(con, "CREATE TEMP TABLE _gdp_ids (id TEXT NOT NULL PRIMARY KEY)");
            Exec(con, "CREATE TEMP TABLE _gdp_kill (rid INTEGER NOT NULL PRIMARY KEY)");

            const int batch = 400;
            for (var i = 0; i < ids.Count; i += batch)
            {
                var slice = ids.Skip(i).Take(batch).ToList();
                using var cmd = con.CreateCommand();
                var values = new List<string>(slice.Count);
                for (var j = 0; j < slice.Count; j++)
                {
                    var p = "$id" + j;
                    values.Add($"({p})");
                    cmd.Parameters.AddWithValue(p, slice[j]);
                }

                cmd.CommandText = "INSERT OR IGNORE INTO _gdp_ids(id) VALUES " + string.Join(",", values);
                cmd.ExecuteNonQuery();
            }
        }

        private static void Exec(SqliteConnection con, string sql, SqliteTransaction? tx = null)
        {
            using var cmd = con.CreateCommand();
            if (tx != null)
            {
                cmd.Transaction = tx;
            }

            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// One sequential scan of allowlisted chat keys; extract composerId with substr;
        /// membership via PK lookup on _gdp_ids (no JOIN, no LIKE-per-id).
        /// Stores matching rowids in _gdp_kill for a cheap DELETE BY rowid.
        /// </summary>
        private static int MaterializeKvRowidsToKill(SqliteConnection con)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandTimeout = 0;
            // Prefix lengths:
            // composerData:               13 → substr(key, 14)
            // agentKv:checkpoint:         20 → substr(key, 21)
            // bubbleId:                    9 → substr(key, 10, …)
            // checkpointId:               13 → substr(key, 14, …)
            // codeBlockDiff:              14 → substr(key, 15, …)
            // messageRequestContext:      22 → substr(key, 23, …)
            // agentKv:bubbleCheckpoint:   25 → substr(key, 26, …)
            cmd.CommandText =
                """
                INSERT OR IGNORE INTO _gdp_kill(rid)
                SELECT k.rowid
                FROM cursorDiskKV k
                WHERE (
                      k.key LIKE 'composerData:%'
                   OR k.key LIKE 'agentKv:checkpoint:%'
                   OR k.key LIKE 'bubbleId:%'
                   OR k.key LIKE 'checkpointId:%'
                   OR k.key LIKE 'codeBlockDiff:%'
                   OR k.key LIKE 'messageRequestContext:%'
                   OR k.key LIKE 'agentKv:bubbleCheckpoint:%'
                )
                AND (
                  CASE
                    WHEN k.key LIKE 'composerData:%'
                      THEN substr(k.key, 14)
                    WHEN k.key LIKE 'agentKv:checkpoint:%'
                      THEN substr(k.key, 21)
                    WHEN k.key LIKE 'agentKv:bubbleCheckpoint:%'
                      AND instr(substr(k.key, 26), ':') > 0
                      THEN substr(k.key, 26, instr(substr(k.key, 26), ':') - 1)
                    WHEN k.key LIKE 'bubbleId:%'
                      AND instr(substr(k.key, 10), ':') > 0
                      THEN substr(k.key, 10, instr(substr(k.key, 10), ':') - 1)
                    WHEN k.key LIKE 'checkpointId:%'
                      AND instr(substr(k.key, 14), ':') > 0
                      THEN substr(k.key, 14, instr(substr(k.key, 14), ':') - 1)
                    WHEN k.key LIKE 'codeBlockDiff:%'
                      AND instr(substr(k.key, 15), ':') > 0
                      THEN substr(k.key, 15, instr(substr(k.key, 15), ':') - 1)
                    WHEN k.key LIKE 'messageRequestContext:%'
                      AND instr(substr(k.key, 23), ':') > 0
                      THEN substr(k.key, 23, instr(substr(k.key, 23), ':') - 1)
                    ELSE NULL
                  END
                ) IN (SELECT id FROM _gdp_ids)
                """;
            return cmd.ExecuteNonQuery();
        }

        private static int DeleteKvByTempRowids(SqliteConnection con, SqliteTransaction tx)
        {
            using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandTimeout = 0;
            // rowid IN (temp PK) — no key join, no second full-table key probe storm
            cmd.CommandText =
                """
                DELETE FROM cursorDiskKV
                WHERE rowid IN (SELECT rid FROM _gdp_kill)
                """;
            return cmd.ExecuteNonQuery();
        }

        private static int DeleteHeadersByTempIds(SqliteConnection con, SqliteTransaction tx)
        {
            using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                DELETE FROM composerHeaders
                WHERE composerId IN (SELECT id FROM _gdp_ids)
                """;
            return cmd.ExecuteNonQuery();
        }

        private static void DropTempCleanupTables(SqliteConnection con, SqliteTransaction? tx)
        {
            Exec(con, "DROP TABLE IF EXISTS _gdp_kill", tx);
            Exec(con, "DROP TABLE IF EXISTS _gdp_ids", tx);
        }

        private static bool IsReservedComposerId(string id)
            => !string.IsNullOrEmpty(id) && ReservedComposerIds.Contains(id);

        private static bool IsSafeComposerId(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || IsReservedComposerId(id))
            {
                return false;
            }

            // Cursor composerIds are UUIDs (~36). Reject short/wildcard-ish ids.
            if (id.Length < 20 || id.Length > 80)
            {
                return false;
            }

            if (id.IndexOfAny(new[] { '%', '_', '*', '?', '"' }) >= 0)
            {
                return false;
            }

            foreach (var ch in id)
            {
                if (!(char.IsLetterOrDigit(ch) || ch is '-' or '_'))
                {
                    return false;
                }
            }

            return true;
        }

        private static (long FileBytes, long LiveBytes, long FreelistBytes) ReadDbSpace(SqliteConnection con)
        {
            long pageCount = 0, freelist = 0, pageSize = 4096;
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "PRAGMA page_count";
                pageCount = Convert.ToInt64(cmd.ExecuteScalar() ?? 0);
            }

            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "PRAGMA freelist_count";
                freelist = Convert.ToInt64(cmd.ExecuteScalar() ?? 0);
            }

            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "PRAGMA page_size";
                pageSize = Convert.ToInt64(cmd.ExecuteScalar() ?? 4096);
            }

            var file = pageCount * pageSize;
            var free = freelist * pageSize;
            return (file, Math.Max(0, file - free), free);
        }

        private static int CountAgentBlobs(SqliteConnection con)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM cursorDiskKV WHERE key LIKE 'agentKv:blob:%'";
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }

        /// <summary>
        /// Deletes content-addressed rows (agentKv:blob / composer.content) whose hash is not
        /// referenced by any remaining non-blob KV value. This is the bulk of a bloated state.vscdb.
        /// </summary>
        private static int PurgeOrphanContentAddressed(
            SqliteConnection con,
            Stopwatch sw,
            IProgress<CursorChatDeleteProgress>? progress,
            CancellationToken cancellationToken)
        {
            Exec(con, "PRAGMA temp_store = MEMORY");
            Exec(con, "DROP TABLE IF EXISTS _gdp_live_hash");
            Exec(con, "DROP TABLE IF EXISTS _gdp_kill");
            Exec(con, "CREATE TEMP TABLE _gdp_live_hash (hash TEXT NOT NULL PRIMARY KEY)");
            Exec(con, "CREATE TEMP TABLE _gdp_kill (rid INTEGER NOT NULL PRIMARY KEY)");

            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandTimeout = 0;
                cmd.CommandText =
                    """
                    SELECT value FROM cursorDiskKV
                    WHERE key NOT LIKE 'agentKv:blob:%'
                      AND key NOT LIKE 'composer.content.%'
                    """;
                var scanned = 0;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scanned++;
                    if (r.IsDBNull(0))
                    {
                        continue;
                    }

                    var raw = r.GetValue(0);
                    string text;
                    if (raw is byte[] bytes)
                    {
                        text = Encoding.UTF8.GetString(bytes);
                    }
                    else
                    {
                        text = Convert.ToString(raw) ?? "";
                    }

                    foreach (System.Text.RegularExpressions.Match m in Hex64Regex.Matches(text))
                    {
                        live.Add(m.Value.ToLowerInvariant());
                    }

                    if (scanned % 25000 == 0)
                    {
                        progress?.Report(new CursorChatDeleteProgress
                        {
                            Phase = "orphans",
                            Current = scanned,
                            Total = Math.Max(scanned + 1, 1),
                            Elapsed = sw.Elapsed,
                            Detail = $"Scanning refs… {scanned:N0} rows · live hashes {live.Count:N0}"
                        });
                    }
                }
            }

            // Persist live hashes for SQL anti-join.
            const int batch = 400;
            var liveList = live.ToList();
            for (var i = 0; i < liveList.Count; i += batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slice = liveList.Skip(i).Take(batch).ToList();
                using var ins = con.CreateCommand();
                var values = new List<string>(slice.Count);
                for (var j = 0; j < slice.Count; j++)
                {
                    var p = "$h" + j;
                    values.Add($"({p})");
                    ins.Parameters.AddWithValue(p, slice[j]);
                }

                ins.CommandText = "INSERT OR IGNORE INTO _gdp_live_hash(hash) VALUES " + string.Join(",", values);
                ins.ExecuteNonQuery();
            }

            progress?.Report(new CursorChatDeleteProgress
            {
                Phase = "orphans",
                Current = 0,
                Total = 1,
                Elapsed = sw.Elapsed,
                Detail = $"Queuing orphan blobs (live refs {live.Count:N0})…"
            });

            using (var q = con.CreateCommand())
            {
                q.CommandTimeout = 0;
                // agentKv:blob: = 13 chars → substr from 14
                // composer.content. = 17 chars → substr from 18
                q.CommandText =
                    """
                    INSERT OR IGNORE INTO _gdp_kill(rid)
                    SELECT rowid FROM cursorDiskKV
                    WHERE key LIKE 'agentKv:blob:%'
                      AND lower(substr(key, 14)) NOT IN (SELECT hash FROM _gdp_live_hash)
                    """;
                q.ExecuteNonQuery();
            }

            using (var q = con.CreateCommand())
            {
                q.CommandTimeout = 0;
                q.CommandText =
                    """
                    INSERT OR IGNORE INTO _gdp_kill(rid)
                    SELECT rowid FROM cursorDiskKV
                    WHERE key LIKE 'composer.content.%'
                      AND lower(substr(key, 18)) NOT IN (SELECT hash FROM _gdp_live_hash)
                    """;
                q.ExecuteNonQuery();
            }

            int queued;
            using (var c = con.CreateCommand())
            {
                c.CommandText = "SELECT COUNT(*) FROM _gdp_kill";
                queued = Convert.ToInt32(c.ExecuteScalar() ?? 0);
            }

            progress?.Report(new CursorChatDeleteProgress
            {
                Phase = "orphans",
                Current = 0,
                Total = Math.Max(1, queued),
                DeletedKvRows = 0,
                Elapsed = sw.Elapsed,
                Detail = $"Deleting {queued:N0} orphan rows by rowid…"
            });

            int deleted;
            using (var tx = con.BeginTransaction())
            {
                using (var del = con.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandTimeout = 0;
                    del.CommandText = "DELETE FROM cursorDiskKV WHERE rowid IN (SELECT rid FROM _gdp_kill)";
                    deleted = del.ExecuteNonQuery();
                }

                Exec(con, "DROP TABLE IF EXISTS _gdp_kill", tx);
                Exec(con, "DROP TABLE IF EXISTS _gdp_live_hash", tx);
                tx.Commit();
            }

            return deleted;
        }

        private static Dictionary<string, string> SnapshotProtectedItemKeys(SqliteConnection con)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            using var cmd = con.CreateCommand();
            // Only ItemTable — never cursorDiskKV for auth fingerprint.
            var sb = new StringBuilder();
            sb.Append("SELECT key, length(value), substr(hex(value), 1, 64) FROM ItemTable WHERE ");
            for (var i = 0; i < ProtectedItemKeyPrefixes.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(" OR ");
                }

                sb.Append($"key LIKE $p{i} ESCAPE '\\'");
            }

            cmd.CommandText = sb.ToString();
            for (var i = 0; i < ProtectedItemKeyPrefixes.Length; i++)
            {
                var p = ProtectedItemKeyPrefixes[i];
                var like = EscapeLike(p) + "%";
                cmd.Parameters.AddWithValue("$p" + i, like);
            }

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var key = r.GetString(0);
                var len = r.IsDBNull(1) ? 0 : Convert.ToInt64(r.GetValue(1));
                var hex = r.IsDBNull(2) ? "" : r.GetString(2);
                map[key] = len + ":" + hex;
            }

            return map;
        }

        private static bool ProtectedSnapshotsEqual(
            Dictionary<string, string> before,
            Dictionary<string, string> after)
        {
            if (before.Count != after.Count)
            {
                return false;
            }

            foreach (var kv in before)
            {
                if (!after.TryGetValue(kv.Key, out var v) || !string.Equals(v, kv.Value, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static string FormatElapsed(TimeSpan t)
        {
            if (t.TotalMinutes >= 1)
            {
                return $"{(int)t.TotalMinutes}m {t.Seconds:D2}s";
            }

            return $"{t.TotalSeconds:0.0}s";
        }

        private static string EscapeLike(string value)
            => (value ?? string.Empty).Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

        private static SqliteConnection OpenReadOnly(string dbPath)
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared
            }.ToString();
            var con = new SqliteConnection(cs);
            con.Open();
            return con;
        }

        private static Dictionary<string, (string DisplayName, string Path)> LoadWorkspaceMap()
        {
            var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            var root = WorkspaceStorageRoot;
            if (!Directory.Exists(root))
            {
                return map;
            }

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var hash = Path.GetFileName(dir);
                var jsonPath = Path.Combine(dir, "workspace.json");
                if (!File.Exists(jsonPath))
                {
                    map[hash] = ("(empty window)", string.Empty);
                    continue;
                }

                try
                {
                    var jo = JObject.Parse(File.ReadAllText(jsonPath));
                    var folderUri = jo["folder"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(folderUri))
                    {
                        var path = UriToPath(folderUri);
                        map[hash] = (Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name
                            ? name
                            : path, path);
                        continue;
                    }

                    var wsUri = jo["workspace"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(wsUri))
                    {
                        var wsFile = UriToPath(wsUri);
                        var names = TryReadMultiRootNames(wsFile);
                        var label = names.Count > 0
                            ? string.Join(" + ", names.Take(3)) + (names.Count > 3 ? "…" : string.Empty)
                            : Path.GetFileName(wsFile);
                        map[hash] = ("[multi] " + label, wsFile);
                        continue;
                    }

                    map[hash] = ("(unknown)", string.Empty);
                }
                catch
                {
                    map[hash] = ("(unreadable)", string.Empty);
                }
            }

            return map;
        }

        private static List<string> TryReadMultiRootNames(string workspaceFile)
        {
            var names = new List<string>();
            try
            {
                if (!File.Exists(workspaceFile))
                {
                    return names;
                }

                var jo = JObject.Parse(File.ReadAllText(workspaceFile));
                if (jo["folders"] is not JArray arr)
                {
                    return names;
                }

                foreach (var f in arr)
                {
                    var p = f?["path"]?.ToString();
                    if (string.IsNullOrWhiteSpace(p))
                    {
                        continue;
                    }

                    names.Add(Path.GetFileName(p.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : p);
                }
            }
            catch
            {
            }

            return names;
        }

        private static string UriToPath(string uri)
        {
            try
            {
                if (uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    var u = new Uri(uri);
                    return Uri.UnescapeDataString(u.LocalPath).Replace('/', '\\');
                }
            }
            catch
            {
            }

            return uri;
        }

        private static DateTime FromUnixMs(long ms)
            => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

        private static void TryKillCursor()
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
    }
}
