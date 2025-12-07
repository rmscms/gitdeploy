using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Models;
using MySqlConnector;
using SharpCompress.Common;
using SharpCompress.Writers;

namespace GitDeployPro.Services
{
    public class DatabaseBackupService
    {
        private const int InsertBatchSize = 500;

        public async Task<BackupExecutionResult> RunBackupAsync(
            ConnectionProfile profile,
            BackupSchedule schedule,
            IProgress<BackupProgressUpdate>? progress,
            CancellationToken cancellationToken,
            PauseTokenSource? pauseToken = null)
        {
            if (schedule == null) throw new ArgumentNullException(nameof(schedule));
            if (profile == null) throw new ArgumentNullException(nameof(profile));

            var connectionEntry = DatabaseConnectionEntry.FromProfile(profile);
            if (!string.IsNullOrWhiteSpace(schedule.DatabaseName))
            {
                connectionEntry.DatabaseName = schedule.DatabaseName;
            }

            if (string.IsNullOrWhiteSpace(connectionEntry.DatabaseName))
            {
                throw new InvalidOperationException("Select a database name before running the backup.");
            }

            progress?.Report(new BackupProgressUpdate
            {
                Message = $"Connecting to {connectionEntry.DatabaseName} …"
            });

            if (schedule.BackupMode == BackupMode.ExternalTool)
            {
                return await RunExternalBackupAsync(profile, schedule, progress, cancellationToken, pauseToken).ConfigureAwait(false);
            }

            await using var client = new DatabaseClient();
            await client.ConnectAsync(connectionEntry.ToConnectionInfo());
            await client.SetActiveDatabaseAsync(connectionEntry.DatabaseName).ConfigureAwait(false);

            var connection = client.GetOpenConnection();
            var sessionSettings = await LoadServerSettingsAsync(connection).ConfigureAwait(false);
            var dumpContext = BuildDumpContext(connection, profile, connectionEntry);
            var tables = await client.GetTablesAsync(connectionEntry.DatabaseName).ConfigureAwait(false);

            var totalTables = tables.Count;
            progress?.Report(new BackupProgressUpdate
            {
                Message = $"Preparing backup ({totalTables} table{(totalTables == 1 ? string.Empty : "s")}) …",
                TotalTables = totalTables,
                ProcessedTables = 0
            });

            var scheduleRoot = GetScheduleRoot(schedule);
            Directory.CreateDirectory(scheduleRoot);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var workingFolder = Path.Combine(scheduleRoot, timestamp);
            Directory.CreateDirectory(workingFolder);

            var sqlPath = Path.Combine(workingFolder, $"{connectionEntry.DatabaseName}.sql");

            long totalRows = 0;
            var fastMode = schedule.BackupMode == BackupMode.Fast;

            using (var writer = new StreamWriter(sqlPath, false, new UTF8Encoding(false)))
            {
                await WriteDumpHeaderAsync(writer, dumpContext, sessionSettings).ConfigureAwait(false);
                await writer.WriteLineAsync($"CREATE DATABASE IF NOT EXISTS {DatabaseClient.EscapeIdentifier(connectionEntry.DatabaseName)};");
                await writer.WriteLineAsync($"USE {DatabaseClient.EscapeIdentifier(connectionEntry.DatabaseName)};");
                await writer.WriteLineAsync();

                var processedTables = 0;
                foreach (var table in tables)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pauseToken != null)
                    {
                        await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                    }
                    var tableIndex = processedTables + 1;
                    var rowCount = fastMode
                        ? await TryGetApproximateRowCountAsync(connection, table).ConfigureAwait(false)
                        : await GetTableRowCountAsync(connection, table).ConfigureAwait(false);
                    progress?.Report(new BackupProgressUpdate
                    {
                        Message = $"Exporting {table} …",
                        TotalTables = totalTables,
                        ProcessedTables = processedTables,
                        Stage = "TableStart",
                        CurrentTable = table,
                        CurrentTableIndex = tableIndex,
                        CurrentTableTotalRows = rowCount,
                        CurrentTableProcessedRows = 0
                    });
                    await WriteTableSchemaAsync(connection, writer, table, sessionSettings.CharacterSetClient).ConfigureAwait(false);
                    var exportedRows = fastMode
                        ? await WriteTableDataFastAsync(connection, writer, table, rowCount, progress, processedTables, totalTables, pauseToken, cancellationToken).ConfigureAwait(false)
                        : await WriteTableDataAsync(connection, writer, table, rowCount, progress, processedTables, totalTables, pauseToken, cancellationToken).ConfigureAwait(false);
                    totalRows += exportedRows;
                    await writer.WriteLineAsync();
                    processedTables++;
                    progress?.Report(new BackupProgressUpdate
                    {
                        Message = $"Finished {table}",
                        TotalTables = totalTables,
                        ProcessedTables = processedTables,
                        Stage = "TableComplete",
                        CurrentTable = table,
                        CurrentTableIndex = tableIndex,
                        CurrentTableTotalRows = rowCount,
                        CurrentTableProcessedRows = rowCount
                    });
                }
                await WriteDumpFooterAsync(writer).ConfigureAwait(false);
            }

            string finalPath = sqlPath;
            if (schedule.CompressOutput)
            {
                progress?.Report(new BackupProgressUpdate
                {
                    Message = "Compressing output …",
                    TotalTables = totalTables,
                    ProcessedTables = totalTables,
                    Stage = "Compressing"
                });

                if (pauseToken != null)
                {
                    await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                }

                if (schedule.CompressionFormat == BackupCompressionFormat.TarGz)
                {
                    finalPath = await CreateTarGzArchiveAsync(workingFolder, scheduleRoot, timestamp, pauseToken, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var zipPath = Path.Combine(scheduleRoot, $"{timestamp}.zip");
                    if (File.Exists(zipPath))
                    {
                        File.Delete(zipPath);
                    }
                    ZipFile.CreateFromDirectory(workingFolder, zipPath, CompressionLevel.Optimal, false);
                    Directory.Delete(workingFolder, true);
                    finalPath = zipPath;
                }
            }

            ApplyRetention(scheduleRoot, Math.Max(1, schedule.RetentionCount));

            await client.DisconnectAsync();
            var sha = ComputeSha256(finalPath);

            return new BackupExecutionResult
            {
                OutputPath = finalPath,
                BytesWritten = GetFileSize(finalPath),
                Sha256 = sha,
                TableCount = totalTables,
                RowCount = totalRows,
                IsCompressed = schedule.CompressOutput
            };
        }

        private async Task<BackupExecutionResult> RunExternalBackupAsync(
            ConnectionProfile profile,
            BackupSchedule schedule,
            IProgress<BackupProgressUpdate>? progress,
            CancellationToken cancellationToken,
            PauseTokenSource? pauseToken = null)
        {
            DatabaseClient? tunnelClient = null;
            try
            {
                if (profile.UseSSH)
                {
                    var entry = DatabaseConnectionEntry.FromProfile(profile);
                    tunnelClient = new DatabaseClient();
                    await tunnelClient.ConnectAsync(entry.ToConnectionInfo());
                }

                var scheduleRoot = GetScheduleRoot(schedule);
                Directory.CreateDirectory(scheduleRoot);
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var outputPath = Path.Combine(scheduleRoot, $"{schedule.DatabaseName}_{timestamp}.sql");

                var host = profile.UseSSH ? "127.0.0.1" : profile.Host;
                var port = profile.UseSSH && tunnelClient != null ? tunnelClient.TunnelPort : (uint)profile.Port;

                var args = $"--host={host} --port={port} " +
                           $"--user={profile.DbUsername} --password={profile.DbPassword} {schedule.DatabaseName}";

                var startInfo = new ProcessStartInfo("mysqldump", args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = startInfo };
                process.Start();

                await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await process.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    if (pauseToken != null)
                    {
                        await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                    }

                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                var error = await process.StandardError.ReadToEndAsync();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"mysqldump failed: {error}");
                }

                var sha = ComputeSha256(outputPath);
                return new BackupExecutionResult
                {
                    OutputPath = outputPath,
                    BytesWritten = GetFileSize(outputPath),
                    Sha256 = sha,
                    TableCount = 0,
                    RowCount = 0,
                    IsCompressed = false
                };
            }
            finally
            {
                tunnelClient?.Dispose();
            }
        }

        private static async Task<string> CreateTarGzArchiveAsync(
            string workingFolder,
            string scheduleRoot,
            string timestamp,
            PauseTokenSource? pauseToken,
            CancellationToken token)
        {
            var tarPath = Path.Combine(scheduleRoot, $"{timestamp}.tar");
            var tarGzPath = Path.Combine(scheduleRoot, $"{timestamp}.tar.gz");

            if (File.Exists(tarPath))
            {
                File.Delete(tarPath);
            }
            if (File.Exists(tarGzPath))
            {
                File.Delete(tarGzPath);
            }

            var writerOptions = new WriterOptions(CompressionType.None)
            {
                ArchiveEncoding = new ArchiveEncoding
                {
                    Default = Encoding.UTF8
                }
            };

            using (var tarStream = File.Create(tarPath))
            using (var writer = WriterFactory.Open(tarStream, ArchiveType.Tar, writerOptions))
            {
                foreach (var file in Directory.EnumerateFiles(workingFolder, "*", SearchOption.AllDirectories))
                {
                    token.ThrowIfCancellationRequested();
                    if (pauseToken != null)
                    {
                        await pauseToken.WaitWhilePausedAsync(token).ConfigureAwait(false);
                    }

                    var relativePath = Path.GetRelativePath(workingFolder, file).Replace('\\', '/');
                    writer.Write(relativePath, file);
                }
            }

            using (var tarStream = File.OpenRead(tarPath))
            using (var gzStream = File.Create(tarGzPath))
            using (var gzip = new GZipStream(gzStream, CompressionLevel.Optimal))
            {
                await tarStream.CopyToAsync(gzip, 81920, token).ConfigureAwait(false);
            }

            File.Delete(tarPath);
            Directory.Delete(workingFolder, true);
            return tarGzPath;
        }

        private static long GetFileSize(string path)
        {
            if (File.Exists(path))
            {
                return new FileInfo(path).Length;
            }
            if (Directory.Exists(path))
            {
                return Directory.GetFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            }
            return 0;
        }

        internal static string GetScheduleRoot(BackupSchedule schedule)
        {
            var basePath = !string.IsNullOrWhiteSpace(schedule.OutputDirectory)
                ? schedule.OutputDirectory
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GitDeploy Backups");

            var safeName = SanitizeFileName(string.IsNullOrWhiteSpace(schedule.Name) ? "BackupPlan" : schedule.Name);
            return Path.Combine(basePath, $"{safeName}_{schedule.Id.Substring(0, Math.Min(8, schedule.Id.Length))}");
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '_');
            }
            return name.Trim();
        }

        private static void ApplyRetention(string rootPath, int retentionCount)
        {
            if (!Directory.Exists(rootPath)) return;
            var entries = new DirectoryInfo(rootPath)
                .EnumerateFileSystemInfos()
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();

            foreach (var extra in entries.Skip(retentionCount))
            {
                try
                {
                    if (extra is DirectoryInfo dir)
                    {
                        dir.Delete(true);
                    }
                    else
                    {
                        extra.Delete();
                    }
                }
                catch
                {
                    // Ignore retention cleanup errors
                }
            }
        }

        private static async Task WriteTableSchemaAsync(MySqlConnection connection, TextWriter writer, string tableName, string? dumpCharset)
        {
            var escapedName = DatabaseClient.EscapeIdentifier(tableName);
            await writer.WriteLineAsync("--").ConfigureAwait(false);
            await writer.WriteLineAsync($"-- Table structure for table {escapedName}").ConfigureAwait(false);
            await writer.WriteLineAsync("--").ConfigureAwait(false);
            await writer.WriteLineAsync($"DROP TABLE IF EXISTS {escapedName};").ConfigureAwait(false);
            await writer.WriteLineAsync("/*!40101 SET @saved_cs_client     = @@character_set_client */;").ConfigureAwait(false);
            var charset = string.IsNullOrWhiteSpace(dumpCharset) ? "utf8mb4" : dumpCharset;
            await writer.WriteLineAsync($"/*!40101 SET character_set_client = {charset} */;").ConfigureAwait(false);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SHOW CREATE TABLE {escapedName}";
            await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow);
            if (await reader.ReadAsync())
            {
                var createStatement = reader.GetString(1);
                await writer.WriteLineAsync(createStatement + ";").ConfigureAwait(false);
            }
            await writer.WriteLineAsync("/*!40101 SET character_set_client = @saved_cs_client */;").ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);
        }

        private static async Task<long> GetTableRowCountAsync(MySqlConnection connection, string tableName)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {DatabaseClient.EscapeIdentifier(tableName)}";
            var scalar = await cmd.ExecuteScalarAsync();
            return scalar == null || scalar == DBNull.Value ? 0 : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        }

        private static async Task<long> TryGetApproximateRowCountAsync(MySqlConnection connection, string tableName)
        {
            try
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = $@"
                    SELECT TABLE_ROWS
                    FROM INFORMATION_SCHEMA.TABLES
                    WHERE TABLE_SCHEMA = DATABASE()
                      AND TABLE_NAME = {DatabaseClient.EscapeIdentifier(tableName)}";
                var scalar = await cmd.ExecuteScalarAsync();
                if (scalar != null && scalar != DBNull.Value)
                {
                    var approx = Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
                    if (approx > 0)
                    {
                        return approx;
                    }
                }
            }
            catch
            {
            }

            return 0;
        }

        private static Task<long> WriteTableDataAsync(
            MySqlConnection connection,
            TextWriter writer,
            string tableName,
            long tableRowCount,
            IProgress<BackupProgressUpdate>? progress,
            int processedTables,
            int totalTables,
            PauseTokenSource? pauseToken,
            CancellationToken token) =>
            WriteTableDataInternalAsync(connection, writer, tableName, tableRowCount, progress, processedTables, totalTables, pauseToken, token, "Writing");

        private static Task<long> WriteTableDataFastAsync(
            MySqlConnection connection,
            TextWriter writer,
            string tableName,
            long tableRowCount,
            IProgress<BackupProgressUpdate>? progress,
            int processedTables,
            int totalTables,
            PauseTokenSource? pauseToken,
            CancellationToken token) =>
            WriteTableDataInternalAsync(connection, writer, tableName, tableRowCount, progress, processedTables, totalTables, pauseToken, token, "(Fast)");

        private static async Task<long> WriteTableDataInternalAsync(
            MySqlConnection connection,
            TextWriter writer,
            string tableName,
            long tableRowCount,
            IProgress<BackupProgressUpdate>? progress,
            int processedTables,
            int totalTables,
            PauseTokenSource? pauseToken,
            CancellationToken token,
            string progressLabel)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {DatabaseClient.EscapeIdentifier(tableName)}";
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!reader.HasRows)
            {
                await writer.WriteLineAsync("--").ConfigureAwait(false);
                await writer.WriteLineAsync($"-- Dumping data for table {DatabaseClient.EscapeIdentifier(tableName)} (empty)").ConfigureAwait(false);
                await writer.WriteLineAsync("--").ConfigureAwait(false);
                return 0;
            }

            var columnNames = Enumerable.Range(0, reader.FieldCount)
                .Select(i => DatabaseClient.EscapeIdentifier(reader.GetName(i)))
                .ToArray();
            var columnList = string.Join(", ", columnNames);
            var escapedTable = DatabaseClient.EscapeIdentifier(tableName);

            await writer.WriteLineAsync("--").ConfigureAwait(false);
            await writer.WriteLineAsync($"-- Dumping data for table {escapedTable}").ConfigureAwait(false);
            await writer.WriteLineAsync("--").ConfigureAwait(false);
            await writer.WriteLineAsync($"LOCK TABLES {escapedTable} WRITE;").ConfigureAwait(false);
            await writer.WriteLineAsync($"/*!40000 ALTER TABLE {escapedTable} DISABLE KEYS */;").ConfigureAwait(false);

            var batch = new List<string>(InsertBatchSize);
            long rowCounter = 0;
            var reportInterval = Math.Max(1, Math.Max(1, tableRowCount) / 50);

            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (pauseToken != null)
                {
                    await pauseToken.WaitWhilePausedAsync(token).ConfigureAwait(false);
                }

                var values = new string[reader.FieldCount];
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    values[i] = FormatSqlValue(reader.GetValue(i));
                }

                batch.Add("(" + string.Join(", ", values) + ")");
                rowCounter++;

                if (batch.Count >= InsertBatchSize)
                {
                    await WriteInsertBatchAsync(writer, escapedTable, columnList, batch).ConfigureAwait(false);
                    batch.Clear();
                }

                if (tableRowCount == 0 || rowCounter % reportInterval == 0)
                {
                    progress?.Report(new BackupProgressUpdate
                    {
                        Message = $"{progressLabel} {tableName}: {rowCounter:N0}/{tableRowCount:N0}",
                        TotalTables = totalTables,
                        ProcessedTables = processedTables,
                        Stage = "TableProgress",
                        CurrentTable = tableName,
                        CurrentTableIndex = processedTables + 1,
                        CurrentTableTotalRows = tableRowCount,
                        CurrentTableProcessedRows = rowCounter
                    });
                }
            }

            if (batch.Count > 0)
            {
                await WriteInsertBatchAsync(writer, escapedTable, columnList, batch).ConfigureAwait(false);
                batch.Clear();
            }

            await writer.WriteLineAsync($"/*!40000 ALTER TABLE {escapedTable} ENABLE KEYS */;").ConfigureAwait(false);
            await writer.WriteLineAsync("UNLOCK TABLES;").ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);

            if (tableRowCount > 0 && rowCounter < tableRowCount)
            {
                progress?.Report(new BackupProgressUpdate
                {
                    Message = $"{progressLabel} {tableName}: {rowCounter:N0}/{tableRowCount:N0}",
                    TotalTables = totalTables,
                    ProcessedTables = processedTables,
                    Stage = "TableProgress",
                    CurrentTable = tableName,
                    CurrentTableIndex = processedTables + 1,
                    CurrentTableTotalRows = tableRowCount,
                    CurrentTableProcessedRows = rowCounter
                });
            }

            return rowCounter;
        }

        private static async Task WriteInsertBatchAsync(TextWriter writer, string tableName, string columnList, List<string> batch)
        {
            if (batch.Count == 0) return;
            var sb = new StringBuilder();
            sb.Append($"INSERT INTO {tableName} ({columnList}) VALUES ");
            for (int i = 0; i < batch.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(batch[i]);
            }
            sb.Append(";");
            await writer.WriteLineAsync(sb.ToString()).ConfigureAwait(false);
        }

        private static async Task<ServerSessionSettings> LoadServerSettingsAsync(MySqlConnection connection)
        {
            return new ServerSessionSettings
            {
                SqlMode = await GetServerVariableAsync(connection, "@@sql_mode").ConfigureAwait(false),
                TimeZone = await GetServerVariableAsync(connection, "@@time_zone").ConfigureAwait(false),
                CharacterSetClient = await GetServerVariableAsync(connection, "@@character_set_client").ConfigureAwait(false),
                CharacterSetResults = await GetServerVariableAsync(connection, "@@character_set_results").ConfigureAwait(false),
                CollationConnection = await GetServerVariableAsync(connection, "@@collation_connection").ConfigureAwait(false),
                SqlNotes = await GetServerVariableAsync(connection, "@@sql_notes").ConfigureAwait(false),
                UniqueChecks = await GetServerVariableAsync(connection, "@@unique_checks").ConfigureAwait(false),
                ForeignKeyChecks = await GetServerVariableAsync(connection, "@@foreign_key_checks").ConfigureAwait(false)
            };
        }

        private static async Task<string?> GetServerVariableAsync(MySqlConnection connection, string expression)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT {expression}";
            var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return result?.ToString();
        }

        private static DumpContext BuildDumpContext(MySqlConnection connection, ConnectionProfile profile, DatabaseConnectionEntry entry)
        {
            return new DumpContext
            {
                Host = string.IsNullOrWhiteSpace(profile.Host) ? "localhost" : profile.Host,
                DatabaseName = entry.DatabaseName ?? connection.Database ?? "database",
                ServerVersion = connection.ServerVersion,
                UserName = profile.DbUsername ?? entry.Username ?? "root",
                GeneratedAt = DateTime.Now
            };
        }

        private static async Task WriteDumpHeaderAsync(TextWriter writer, DumpContext context, ServerSessionSettings settings)
        {
            var charset = string.IsNullOrWhiteSpace(settings.CharacterSetClient) ? "utf8mb4" : settings.CharacterSetClient;
            await writer.WriteLineAsync($"-- MySQL dump 10.13  Distrib {context.ServerVersion}, for Windows (.NET)").ConfigureAwait(false);
            await writer.WriteLineAsync($"-- Host: {context.Host}    Database: {context.DatabaseName}").ConfigureAwait(false);
            await writer.WriteLineAsync($"-- ------------------------------------------------------").ConfigureAwait(false);
            await writer.WriteLineAsync($"-- Server version\t{context.ServerVersion}").ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync("/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;").ConfigureAwait(false);
            await writer.WriteLineAsync("/*!40101 SET @OLD_CHARACTER_SET_RESULTS=@@CHARACTER_SET_RESULTS */;").ConfigureAwait(false);
            await writer.WriteLineAsync("/*!40101 SET @OLD_COLLATION_CONNECTION=@@COLLATION_CONNECTION */;").ConfigureAwait(false);
            await writer.WriteLineAsync($"/*!40101 SET NAMES {charset} */;").ConfigureAwait(false);
            await writer.WriteLineAsync("/*!40103 SET @OLD_TIME_ZONE=@@TIME_ZONE */;").ConfigureAwait(false);
            await writer.WriteLineAsync("/*!40103 SET TIME_ZONE='+00:00' */;").ConfigureAwait(false);
            await writer.WriteLineAsync("/*!40014 SET @OLD_UNIQUE_CHECKS=@@UNIQUE_CHECKS, UNIQUE_CHECKS=0 */;").ConfigureAwait(false);
            await writer.WriteLineAsync("/*!40014 SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0 */;").ConfigureAwait(false);
            await writer.WriteLineAsync("/*!40101 SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;").ConfigureAwait(false);
            await writer.WriteLineAsync("/*!40111 SET @OLD_SQL_NOTES=@@SQL_NOTES, SQL_NOTES=0 */;").ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);
        }

        private static async Task WriteDumpFooterAsync(TextWriter writer)
        {
            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync("/*!40111 SET SQL_NOTES=@OLD_SQL_NOTES */;").ConfigureAwait(false);
            await writer.WriteLineAsync("/*!40101 SET SQL_MODE=@OLD_SQL_MODE */;").ConfigureAwait(false);
            await writer.WriteLineAsync("/*!40014 SET FOREIGN_KEY_CHECKS=@OLD_FOREIGN_KEY_CHECKS */;").ConfigureAwait(false);
            await writer.WriteLineAsync("/*!40014 SET UNIQUE_CHECKS=@OLD_UNIQUE_CHECKS */;").ConfigureAwait(false);
            await writer.WriteLineAsync("/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;").ConfigureAwait(false);
            await writer.WriteLineAsync("/*!40101 SET CHARACTER_SET_RESULTS=@OLD_CHARACTER_SET_RESULTS */;").ConfigureAwait(false);
            await writer.WriteLineAsync("/*!40101 SET COLLATION_CONNECTION=@OLD_COLLATION_CONNECTION */;").ConfigureAwait(false);
            await writer.WriteLineAsync("/*!40103 SET TIME_ZONE=@OLD_TIME_ZONE */;").ConfigureAwait(false);
        }

        private static string FormatSqlValue(object? value)
        {
            if (value == null || value is DBNull)
            {
                return "NULL";
            }

            switch (value)
            {
                case string s:
                    if (TryParseDateTimeString(s, out var parsed))
                    {
                        return $"'{parsed:yyyy-MM-dd HH:mm:ss}'";
                    }
                    return $"'{EscapeSqlLiteral(s)}'";
                case bool b:
                    return b ? "1" : "0";
                case byte[] bytes:
                    return "0x" + BitConverter.ToString(bytes).Replace("-", string.Empty, StringComparison.Ordinal);
                case DateTime dt:
                    if (dt == DateTime.MinValue)
                    {
                        return "'0000-00-00 00:00:00'";
                    }
                    return $"'{dt:yyyy-MM-dd HH:mm:ss}'";
                case MySqlDateTime mysqlDt:
                    if (!mysqlDt.IsValidDateTime)
                    {
                        return "'0000-00-00 00:00:00'";
                    }
                    var mysqlDate = mysqlDt.GetDateTime();
                    return $"'{mysqlDate:yyyy-MM-dd HH:mm:ss}'";
                case Guid guid:
                    return $"'{guid}'";
                case TimeSpan ts:
                    return $"'{ts:hh\\:mm\\:ss}'";
                default:
                    if (value is IFormattable formattable)
                    {
                        return formattable.ToString(null, CultureInfo.InvariantCulture);
                    }
                    return $"'{EscapeSqlLiteral(value.ToString() ?? string.Empty)}'";
            }
        }

        private static bool TryParseDateTimeString(string value, out DateTime parsed)
        {
            var styles = DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal;
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, styles, out parsed))
            {
                return true;
            }

            var usCulture = CultureInfo.GetCultureInfo("en-US");
            return DateTime.TryParse(value, usCulture, styles, out parsed);
        }

        private static string EscapeSqlLiteral(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(value.Length + 16);
            foreach (var ch in value)
            {
                switch (ch)
                {
                    case '\0':
                        sb.Append("\\0");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    case '\u001A':
                        sb.Append("\\Z");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\'':
                        sb.Append("\\'");
                        break;
                    case '\"':
                        sb.Append("\\\"");
                        break;
                    default:
                        sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }

        private static string ComputeSha256(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var sha = SHA256.Create();
                var hash = sha.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", string.Empty, StringComparison.Ordinal);
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    public class BackupExecutionResult
    {
        public string OutputPath { get; set; } = string.Empty;
        public long BytesWritten { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public int TableCount { get; set; }
        public long RowCount { get; set; }
        public bool IsCompressed { get; set; }
    }

    internal sealed class ServerSessionSettings
    {
        public string? SqlMode { get; set; }
        public string? TimeZone { get; set; }
        public string? CharacterSetClient { get; set; }
        public string? CharacterSetResults { get; set; }
        public string? CollationConnection { get; set; }
        public string? SqlNotes { get; set; }
        public string? UniqueChecks { get; set; }
        public string? ForeignKeyChecks { get; set; }
    }

    internal sealed class DumpContext
    {
        public string Host { get; init; } = "localhost";
        public string DatabaseName { get; init; } = string.Empty;
        public string ServerVersion { get; init; } = string.Empty;
        public string UserName { get; init; } = "root";
        public DateTime GeneratedAt { get; init; } = DateTime.Now;
    }
}

