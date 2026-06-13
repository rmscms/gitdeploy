using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Models;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace GitDeployPro.Services
{
    public sealed class BackupRestoreValidationService
    {
        private const string ValidationDbPrefix = "gdp_validate_";
        private static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase)
        {
            "mysql",
            "information_schema",
            "performance_schema",
            "sys"
        };

        public static ConnectionProfile CreateDefaultLocalValidationProfile()
        {
            return new ConnectionProfile
            {
                Id = BackupRestoreValidationDefaults.LocalhostDefaultProfileId,
                Name = "Localhost (Default Validation)",
                Host = "127.0.0.1",
                Port = 3306,
                Username = "root",
                UseSSH = false,
                DbType = DatabaseType.MySQL,
                DbHost = "127.0.0.1",
                DbPort = 3306,
                DbUsername = "root"
            };
        }

        public static bool TryBuildLocalConnectionInfo(ConnectionProfile profile, out DatabaseConnectionInfo connectionInfo, out string reason)
        {
            connectionInfo = new DatabaseConnectionInfo();
            reason = string.Empty;

            if (profile == null)
            {
                reason = "Local validation profile is missing.";
                return false;
            }

            DatabaseConnectionEntry entry;
            if (string.Equals(profile.Id, BackupRestoreValidationDefaults.LocalhostDefaultProfileId, StringComparison.Ordinal))
            {
                entry = DatabaseConnectionEntry.CreateLocalDefault();
            }
            else
            {
                entry = DatabaseConnectionEntry.FromProfile(profile);
            }

            if (entry.UseSshTunnel)
            {
                reason = "Local validation profile must not use SSH tunnel.";
                return false;
            }

            if (!IsLoopbackHost(entry.Host))
            {
                reason = $"Local validation only allows localhost targets (current: {entry.Host}).";
                return false;
            }

            entry.IsLocal = true;
            entry.DatabaseName = "information_schema";
            connectionInfo = entry.ToConnectionInfo();
            return true;
        }

        public static bool TryBuildLocalConnectionInfo(BackupSchedule schedule, out DatabaseConnectionInfo connectionInfo, out string reason)
        {
            connectionInfo = new DatabaseConnectionInfo();
            reason = string.Empty;

            if (schedule == null)
            {
                reason = "Validation schedule is missing.";
                return false;
            }

            var host = string.IsNullOrWhiteSpace(schedule.LocalValidationHost)
                ? "127.0.0.1"
                : schedule.LocalValidationHost.Trim();
            if (!IsLoopbackHost(host))
            {
                reason = $"Local validation only allows localhost targets (current: {host}).";
                return false;
            }

            var username = string.IsNullOrWhiteSpace(schedule.LocalValidationUsername)
                ? "root"
                : schedule.LocalValidationUsername.Trim();
            if (string.IsNullOrWhiteSpace(username))
            {
                reason = "Local validation username is required.";
                return false;
            }

            var database = string.IsNullOrWhiteSpace(schedule.LocalValidationDatabaseName)
                ? "information_schema"
                : schedule.LocalValidationDatabaseName.Trim();

            connectionInfo = new DatabaseConnectionInfo
            {
                Name = "Local validation",
                DbType = DatabaseType.MySQL,
                Host = host,
                Port = schedule.LocalValidationPort <= 0 ? 3306 : schedule.LocalValidationPort,
                Username = username,
                Password = schedule.LocalValidationPassword ?? string.Empty,
                DatabaseName = "information_schema",
                IsLocal = true,
                UseSshTunnel = false,
                SourceId = schedule.Id ?? Guid.NewGuid().ToString()
            };
            return true;
        }

        public async Task<LocalValidationProbeResult> ProbeEnvironmentAsync(ConnectionProfile profile, CancellationToken cancellationToken)
        {
            if (!TryBuildLocalConnectionInfo(profile, out var info, out var reason))
            {
                return LocalValidationProbeResult.Fail(reason);
            }

            try
            {
                await using var client = new DatabaseClient();
                await client.ConnectAsync(info);
                var probeDb = BuildValidationDatabaseName("probe");
                await client.DropAndCreateDatabaseAsync(probeDb, cancellationToken: cancellationToken);
                await client.ExecuteNonQueryAsync($"DROP DATABASE IF EXISTS {DatabaseClient.EscapeIdentifier(probeDb)};", null, 0, cancellationToken);
                return LocalValidationProbeResult.Success("Localhost access and create/drop permissions are ready.");
            }
            catch (Exception ex)
            {
                return LocalValidationProbeResult.Fail(ex.Message);
            }
        }

        public async Task<LocalValidationProbeResult> ProbeEnvironmentAsync(BackupSchedule schedule, CancellationToken cancellationToken, bool ensureConfiguredDatabase = true)
        {
            if (!TryBuildLocalConnectionInfo(schedule, out var info, out var reason))
            {
                return LocalValidationProbeResult.Fail(reason);
            }

            try
            {
                await using var client = new DatabaseClient();
                await client.ConnectAsync(info);
                (string Charset, string Collation) resolved = ("", "");
                if (ensureConfiguredDatabase)
                {
                    resolved = await EnsureConfiguredLocalDatabaseAsync(client, schedule, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    resolved = await ResolveCharsetAndCollationAsync(client, schedule, cancellationToken).ConfigureAwait(false);
                }

                var probeDb = BuildValidationDatabaseName("probe");
                await client.DropAndCreateDatabaseAsync(probeDb, cancellationToken: cancellationToken);
                await client.ExecuteNonQueryAsync($"DROP DATABASE IF EXISTS {DatabaseClient.EscapeIdentifier(probeDb)};", null, 0, cancellationToken);
                return LocalValidationProbeResult.Success(
                    ensureConfiguredDatabase
                        ? $"Localhost access ready. Dedicated DB ensured with charset/collation: {resolved.Charset}/{resolved.Collation}."
                        : $"Localhost access ready. Charset/collation resolved as: {resolved.Charset}/{resolved.Collation}.",
                    resolved.Charset,
                    resolved.Collation);
            }
            catch (Exception ex)
            {
                return LocalValidationProbeResult.Fail(ex.Message);
            }
        }

        public async Task<LocalValidationInspectResult> InspectConfiguredDatabaseAsync(BackupSchedule schedule, CancellationToken cancellationToken)
        {
            if (!TryBuildLocalConnectionInfo(schedule, out var info, out var reason))
            {
                return LocalValidationInspectResult.Fail(reason);
            }

            try
            {
                await using var client = new DatabaseClient();
                await client.ConnectAsync(info);
                var configuredDb = schedule.LocalValidationDatabaseName?.Trim() ?? string.Empty;
                var (charset, collation) = await ResolveCharsetAndCollationAsync(client, schedule, cancellationToken).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(configuredDb))
                {
                    return LocalValidationInspectResult.Success(
                        databaseExists: false,
                        configuredDatabase: string.Empty,
                        effectiveCharset: charset,
                        effectiveCollation: collation,
                        message: "Validation database name is empty.");
                }

                if (SystemDatabases.Contains(configuredDb))
                {
                    return LocalValidationInspectResult.Fail("Configured local validation database must not be a system database.");
                }

                var exists = await client.DatabaseExistsAsync(configuredDb, cancellationToken).ConfigureAwait(false);
                return LocalValidationInspectResult.Success(
                    databaseExists: exists,
                    configuredDatabase: configuredDb,
                    effectiveCharset: charset,
                    effectiveCollation: collation,
                    message: exists
                        ? $"Configured database '{configuredDb}' exists."
                        : $"Configured database '{configuredDb}' does not exist.");
            }
            catch (Exception ex)
            {
                return LocalValidationInspectResult.Fail(ex.Message);
            }
        }

        public async Task<LocalValidationProbeResult> EnsureConfiguredDatabaseAsync(BackupSchedule schedule, CancellationToken cancellationToken)
        {
            if (!TryBuildLocalConnectionInfo(schedule, out var info, out var reason))
            {
                return LocalValidationProbeResult.Fail(reason);
            }

            try
            {
                await using var client = new DatabaseClient();
                await client.ConnectAsync(info);
                var resolved = await EnsureConfiguredLocalDatabaseAsync(client, schedule, cancellationToken).ConfigureAwait(false);
                return LocalValidationProbeResult.Success(
                    $"Configured local database ensured with charset/collation: {resolved.Charset}/{resolved.Collation}.",
                    resolved.Charset,
                    resolved.Collation);
            }
            catch (Exception ex)
            {
                return LocalValidationProbeResult.Fail(ex.Message);
            }
        }

        public async Task<BackupRestoreValidationResult> ValidateAsync(
            BackupSchedule schedule,
            ConnectionProfile localProfile,
            string localArtifactPath,
            IProgress<BackupProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            if (schedule == null)
            {
                throw new ArgumentNullException(nameof(schedule));
            }

            if (localProfile == null)
            {
                throw new ArgumentNullException(nameof(localProfile));
            }

            if (string.IsNullOrWhiteSpace(localArtifactPath))
            {
                return BackupRestoreValidationResult.Warning("Validation skipped: local artifact path is empty.");
            }

            if (!File.Exists(localArtifactPath))
            {
                return BackupRestoreValidationResult.Warning($"Validation skipped: local artifact not found ({localArtifactPath}).");
            }

            if (!TryBuildLocalConnectionInfo(localProfile, out var info, out var reason))
            {
                return BackupRestoreValidationResult.Warning($"Validation skipped: {reason}");
            }

            progress?.Report(new BackupProgressUpdate
            {
                Stage = "ValidationPrepare",
                Message = "Preparing localhost restore-validation environment …",
                TotalTables = 0,
                ProcessedTables = 0
            });

            var tempFiles = new List<string>();
            string validationDatabaseName = BuildValidationDatabaseName(schedule.Id);
            bool validationDbCreated = false;
            bool cleanupDropFailed = false;
            string cleanupDropError = string.Empty;
            string validationStage = "prepare";
            var validationResult = BackupRestoreValidationResult.Warning("Validation warning: validation was not executed.")
                .WithDatabase(validationDatabaseName);
            try
            {
                validationStage = "connect";
                await using var client = new DatabaseClient();
                await client.ConnectAsync(info);

                validationStage = "database-create";
                await client.DropAndCreateDatabaseAsync(validationDatabaseName, cancellationToken: cancellationToken);
                validationDbCreated = true;

                validationStage = "artifact-extract";
                var preparedSqlPath = await PrepareSqlFileAsync(localArtifactPath, tempFiles, cancellationToken).ConfigureAwait(false);
                progress?.Report(new BackupProgressUpdate
                {
                    Stage = "ValidationImport",
                    Message = $"Importing backup into temporary database `{validationDatabaseName}` …",
                    TotalTables = 0,
                    ProcessedTables = 0
                });

                var importProgress = new Progress<ImportProgressUpdate>(update =>
                {
                    var message = string.IsNullOrWhiteSpace(update.Message)
                        ? $"Validation import progress: {FormatBytes(update.BytesProcessed)}/{FormatBytes(update.TotalBytes)}"
                        : $"Validation import: {update.Message}";
                    progress?.Report(new BackupProgressUpdate
                    {
                        Stage = "ValidationImport",
                        Message = message,
                        TotalTables = 0,
                        ProcessedTables = 0
                    });
                });

                await client.ImportSqlAsync(
                    preparedSqlPath,
                    validationDatabaseName,
                    importProgress,
                    cancellationToken,
                    fastMode: true,
                    commandTimeoutSeconds: 120,
                    continueOnError: false);

                validationStage = "post-import-check";
                progress?.Report(new BackupProgressUpdate
                {
                    Stage = "ValidationCheck",
                    Message = "Running validation sanity checks …",
                    TotalTables = 0,
                    ProcessedTables = 0
                });

                var dbExists = await client.DatabaseExistsAsync(validationDatabaseName, cancellationToken);
                if (!dbExists)
                {
                    validationResult = BackupRestoreValidationResult.Warning(
                            BuildValidationWarningMessage(validationStage, "Temporary validation database was not found after import."))
                        .WithDatabase(validationDatabaseName);
                }
                else
                {
                    var tables = await client.GetTablesAsync(validationDatabaseName);
                    if (tables.Count <= 0)
                    {
                        validationResult = BackupRestoreValidationResult.Warning(
                                BuildValidationWarningMessage(validationStage, "Import completed but no tables were found in validation database."))
                            .WithDatabase(validationDatabaseName);
                    }
                    else
                    {
                        progress?.Report(new BackupProgressUpdate
                        {
                            Stage = "ValidationDone",
                            Message = $"Local restore validation passed ({tables.Count} table(s) imported).",
                            TotalTables = 0,
                            ProcessedTables = 0
                        });
                        validationResult = BackupRestoreValidationResult.Success($"Local restore validation passed ({tables.Count} table(s) imported).")
                            .WithDatabase(validationDatabaseName);
                    }
                }
            }
            catch (Exception ex)
            {
                var warningMessage = BuildValidationWarningMessage(validationStage, ex.Message);
                progress?.Report(new BackupProgressUpdate
                {
                    Stage = "ValidationWarning",
                    Message = $"Local restore validation warning: {warningMessage}",
                    TotalTables = 0,
                    ProcessedTables = 0
                });
                validationResult = BackupRestoreValidationResult.Warning(warningMessage)
                    .WithDatabase(validationDatabaseName);
            }
            finally
            {
                if (validationDbCreated)
                {
                    try
                    {
                        await using var cleanupClient = new DatabaseClient();
                        await cleanupClient.ConnectAsync(info);
                        if (CanDropValidationDatabase(validationDatabaseName))
                        {
                            await cleanupClient.ExecuteNonQueryAsync(
                                $"DROP DATABASE IF EXISTS {DatabaseClient.EscapeIdentifier(validationDatabaseName)};",
                                null,
                                0,
                                cancellationToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        cleanupDropFailed = true;
                        cleanupDropError = ex.Message;
                    }
                }

                foreach (var tempFile in tempFiles)
                {
                    try
                    {
                        if (File.Exists(tempFile))
                        {
                            File.Delete(tempFile);
                        }
                    }
                    catch
                    {
                        // Ignore temp cleanup errors.
                    }
                }

                progress?.Report(new BackupProgressUpdate
                {
                    Stage = "ValidationCleanup",
                    Message = cleanupDropFailed
                        ? $"Validation cleanup warning: unable to drop `{validationDatabaseName}` ({cleanupDropError})."
                        : "Validation cleanup completed.",
                    TotalTables = 0,
                    ProcessedTables = 0
                });
            }

            if (cleanupDropFailed)
            {
                validationResult = BackupRestoreValidationResult.Warning(
                        BuildValidationWarningMessage(
                            "cleanup",
                            $"Import flow ended but cleanup failed for `{validationDatabaseName}` ({cleanupDropError})."))
                    .WithDatabase(validationDatabaseName);
            }

            return validationResult;
        }

        public async Task<BackupRestoreValidationResult> ValidateAsync(
            BackupSchedule schedule,
            string localArtifactPath,
            IProgress<BackupProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            if (schedule == null)
            {
                throw new ArgumentNullException(nameof(schedule));
            }

            if (string.IsNullOrWhiteSpace(localArtifactPath))
            {
                return BackupRestoreValidationResult.Warning("Validation skipped: local artifact path is empty.");
            }

            if (!File.Exists(localArtifactPath))
            {
                return BackupRestoreValidationResult.Warning($"Validation skipped: local artifact not found ({localArtifactPath}).");
            }

            if (!TryBuildLocalConnectionInfo(schedule, out var info, out var reason))
            {
                return BackupRestoreValidationResult.Warning($"Validation skipped: {reason}");
            }

            progress?.Report(new BackupProgressUpdate
            {
                Stage = "ValidationPrepare",
                Message = "Preparing localhost restore-validation environment …",
                TotalTables = 0,
                ProcessedTables = 0
            });

            var tempFiles = new List<string>();
            string validationDatabaseName = BuildValidationDatabaseName(schedule.Id);
            bool validationDbCreated = false;
            bool cleanupDropFailed = false;
            string cleanupDropError = string.Empty;
            string validationStage = "prepare";
            var validationResult = BackupRestoreValidationResult.Warning("Validation warning: validation was not executed.")
                .WithDatabase(validationDatabaseName);
            try
            {
                validationStage = "connect";
                await using var client = new DatabaseClient();
                await client.ConnectAsync(info);
                validationStage = "configured-db-ensure";
                await EnsureConfiguredLocalDatabaseAsync(client, schedule, cancellationToken).ConfigureAwait(false);

                validationStage = "database-create";
                await client.DropAndCreateDatabaseAsync(validationDatabaseName, cancellationToken: cancellationToken);
                validationDbCreated = true;

                validationStage = "artifact-extract";
                var preparedSqlPath = await PrepareSqlFileAsync(localArtifactPath, tempFiles, cancellationToken).ConfigureAwait(false);
                progress?.Report(new BackupProgressUpdate
                {
                    Stage = "ValidationImport",
                    Message = $"Importing backup into temporary database `{validationDatabaseName}` …",
                    TotalTables = 0,
                    ProcessedTables = 0
                });

                var importProgress = new Progress<ImportProgressUpdate>(update =>
                {
                    var message = string.IsNullOrWhiteSpace(update.Message)
                        ? $"Validation import progress: {FormatBytes(update.BytesProcessed)}/{FormatBytes(update.TotalBytes)}"
                        : $"Validation import: {update.Message}";
                    progress?.Report(new BackupProgressUpdate
                    {
                        Stage = "ValidationImport",
                        Message = message,
                        TotalTables = 0,
                        ProcessedTables = 0
                    });
                });

                await client.ImportSqlAsync(
                    preparedSqlPath,
                    validationDatabaseName,
                    importProgress,
                    cancellationToken,
                    fastMode: true,
                    commandTimeoutSeconds: 120,
                    continueOnError: false);

                validationStage = "post-import-check";
                progress?.Report(new BackupProgressUpdate
                {
                    Stage = "ValidationCheck",
                    Message = "Running validation sanity checks …",
                    TotalTables = 0,
                    ProcessedTables = 0
                });

                var dbExists = await client.DatabaseExistsAsync(validationDatabaseName, cancellationToken);
                if (!dbExists)
                {
                    validationResult = BackupRestoreValidationResult.Warning(
                            BuildValidationWarningMessage(validationStage, "Temporary validation database was not found after import."))
                        .WithDatabase(validationDatabaseName);
                }
                else
                {
                    var tables = await client.GetTablesAsync(validationDatabaseName);
                    if (tables.Count <= 0)
                    {
                        validationResult = BackupRestoreValidationResult.Warning(
                                BuildValidationWarningMessage(validationStage, "Import completed but no tables were found in validation database."))
                            .WithDatabase(validationDatabaseName);
                    }
                    else
                    {
                        progress?.Report(new BackupProgressUpdate
                        {
                            Stage = "ValidationDone",
                            Message = $"Local restore validation passed ({tables.Count} table(s) imported).",
                            TotalTables = 0,
                            ProcessedTables = 0
                        });
                        validationResult = BackupRestoreValidationResult.Success($"Local restore validation passed ({tables.Count} table(s) imported).")
                            .WithDatabase(validationDatabaseName);
                    }
                }
            }
            catch (Exception ex)
            {
                var warningMessage = BuildValidationWarningMessage(validationStage, ex.Message);
                progress?.Report(new BackupProgressUpdate
                {
                    Stage = "ValidationWarning",
                    Message = $"Local restore validation warning: {warningMessage}",
                    TotalTables = 0,
                    ProcessedTables = 0
                });
                validationResult = BackupRestoreValidationResult.Warning(warningMessage)
                    .WithDatabase(validationDatabaseName);
            }
            finally
            {
                if (validationDbCreated)
                {
                    try
                    {
                        await using var cleanupClient = new DatabaseClient();
                        await cleanupClient.ConnectAsync(info);
                        if (CanDropValidationDatabase(validationDatabaseName))
                        {
                            await cleanupClient.ExecuteNonQueryAsync(
                                $"DROP DATABASE IF EXISTS {DatabaseClient.EscapeIdentifier(validationDatabaseName)};",
                                null,
                                0,
                                cancellationToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        cleanupDropFailed = true;
                        cleanupDropError = ex.Message;
                    }
                }

                foreach (var tempFile in tempFiles)
                {
                    try
                    {
                        if (File.Exists(tempFile))
                        {
                            File.Delete(tempFile);
                        }
                    }
                    catch
                    {
                        // Ignore temp cleanup errors.
                    }
                }

                progress?.Report(new BackupProgressUpdate
                {
                    Stage = "ValidationCleanup",
                    Message = cleanupDropFailed
                        ? $"Validation cleanup warning: unable to drop `{validationDatabaseName}` ({cleanupDropError})."
                        : "Validation cleanup completed.",
                    TotalTables = 0,
                    ProcessedTables = 0
                });
            }

            if (cleanupDropFailed)
            {
                validationResult = BackupRestoreValidationResult.Warning(
                        BuildValidationWarningMessage(
                            "cleanup",
                            $"Import flow ended but cleanup failed for `{validationDatabaseName}` ({cleanupDropError})."))
                    .WithDatabase(validationDatabaseName);
            }

            return validationResult;
        }

        private static async Task<(string Charset, string Collation)> EnsureConfiguredLocalDatabaseAsync(DatabaseClient client, BackupSchedule schedule, CancellationToken cancellationToken)
        {
            var configuredDb = schedule.LocalValidationDatabaseName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(configuredDb))
            {
                return ("utf8mb4", "utf8mb4_unicode_ci");
            }

            if (SystemDatabases.Contains(configuredDb))
            {
                throw new InvalidOperationException("Configured local validation database must not be a system database.");
            }

            var (charset, collation) = await ResolveCharsetAndCollationAsync(client, schedule, cancellationToken).ConfigureAwait(false);
            await client.EnsureDatabaseExistsAsync(configuredDb, charset, collation, cancellationToken);
            return (charset, collation);
        }

        private static async Task<(string Charset, string Collation)> ResolveCharsetAndCollationAsync(
            DatabaseClient client,
            BackupSchedule schedule,
            CancellationToken cancellationToken)
        {
            var requestedCharset = schedule.LocalValidationCharset?.Trim() ?? string.Empty;
            var requestedCollation = schedule.LocalValidationCollation?.Trim() ?? string.Empty;
            var (serverCharset, serverCollation) = await TryReadServerCharsetSettingsAsync(client, cancellationToken).ConfigureAwait(false);

            var charset = IsAutoToken(requestedCharset) ? serverCharset : requestedCharset;
            if (string.IsNullOrWhiteSpace(charset))
            {
                charset = "utf8mb4";
            }

            if (!charset.StartsWith("utf8mb4", StringComparison.OrdinalIgnoreCase) &&
                IsAutoToken(requestedCharset))
            {
                charset = "utf8mb4";
            }

            var collation = requestedCollation;
            if (IsAutoToken(collation))
            {
                if (charset.StartsWith("utf8mb4", StringComparison.OrdinalIgnoreCase))
                {
                    collation = serverCollation.StartsWith("utf8mb4_", StringComparison.OrdinalIgnoreCase)
                        ? serverCollation
                        : "utf8mb4_unicode_ci";
                }
                else
                {
                    collation = serverCollation.StartsWith(charset + "_", StringComparison.OrdinalIgnoreCase)
                        ? serverCollation
                        : $"{charset}_general_ci";
                }
            }

            if (charset.StartsWith("utf8mb4", StringComparison.OrdinalIgnoreCase) &&
                !collation.StartsWith("utf8mb4_", StringComparison.OrdinalIgnoreCase))
            {
                collation = serverCollation.StartsWith("utf8mb4_", StringComparison.OrdinalIgnoreCase)
                    ? serverCollation
                    : "utf8mb4_unicode_ci";
            }

            return (charset, collation);
        }

        private static async Task<(string Charset, string Collation)> TryReadServerCharsetSettingsAsync(DatabaseClient client, CancellationToken cancellationToken)
        {
            try
            {
                var result = await client.ExecuteQueryAsync(
                    "SELECT @@character_set_server AS Charset, @@collation_server AS Collation",
                    null,
                    30).ConfigureAwait(false);
                if (result.HasResultSet &&
                    result.Table != null &&
                    result.Table.Rows.Count > 0)
                {
                    var row = result.Table.Rows[0];
                    var charset = row["Charset"]?.ToString()?.Trim() ?? string.Empty;
                    var collation = row["Collation"]?.ToString()?.Trim() ?? string.Empty;
                    return (
                        string.IsNullOrWhiteSpace(charset) ? "utf8mb4" : charset,
                        string.IsNullOrWhiteSpace(collation) ? "utf8mb4_unicode_ci" : collation);
                }
            }
            catch
            {
                // Fallback to utf8mb4 defaults.
            }

            return ("utf8mb4", "utf8mb4_unicode_ci");
        }

        private static bool IsAutoToken(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            return string.Equals(value.Trim(), "auto", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value.Trim(), "server", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<string> PrepareSqlFileAsync(string artifactPath, List<string> tempFiles, CancellationToken cancellationToken)
        {
            var workingPath = artifactPath;
            var lower = workingPath.ToLowerInvariant();

            if (lower.EndsWith(".protected", StringComparison.Ordinal))
            {
                var decryptedPath = Path.Combine(Path.GetTempPath(), $"gdp-validate-{Guid.NewGuid():N}{Path.GetExtension(Path.GetFileNameWithoutExtension(workingPath))}");
                await DecryptProtectedFileAsync(workingPath, decryptedPath, cancellationToken).ConfigureAwait(false);
                tempFiles.Add(decryptedPath);
                workingPath = decryptedPath;
                lower = workingPath.ToLowerInvariant();
            }

            if (lower.EndsWith(".sql", StringComparison.Ordinal))
            {
                return workingPath;
            }

            if (lower.EndsWith(".tar.gz", StringComparison.Ordinal) || lower.EndsWith(".tgz", StringComparison.Ordinal))
            {
                var tempTar = Path.Combine(Path.GetTempPath(), $"gdp-validate-{Guid.NewGuid():N}.tar");
                await DecompressGzipAsync(workingPath, tempTar, cancellationToken).ConfigureAwait(false);
                tempFiles.Add(tempTar);
                var extractedSql = await ExtractSqlFromArchiveAsync(tempTar, cancellationToken).ConfigureAwait(false);
                tempFiles.Add(extractedSql);
                return extractedSql;
            }

            if (lower.EndsWith(".gz", StringComparison.Ordinal))
            {
                var sqlPath = Path.Combine(Path.GetTempPath(), $"gdp-validate-{Guid.NewGuid():N}.sql");
                await DecompressGzipAsync(workingPath, sqlPath, cancellationToken).ConfigureAwait(false);
                tempFiles.Add(sqlPath);
                return sqlPath;
            }

            if (lower.EndsWith(".zip", StringComparison.Ordinal) || lower.EndsWith(".tar", StringComparison.Ordinal))
            {
                var extractedSql = await ExtractSqlFromArchiveAsync(workingPath, cancellationToken).ConfigureAwait(false);
                tempFiles.Add(extractedSql);
                return extractedSql;
            }

            throw new InvalidOperationException("Validation only supports .sql, .sql.gz, .zip, .tar, .tar.gz, .tgz (optionally .protected).");
        }

        private static async Task DecryptProtectedFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
        {
            var encryptedBytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            var plainBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(destinationPath, plainBytes, cancellationToken).ConfigureAwait(false);
        }

        private static async Task DecompressGzipAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
        {
            await using var source = File.OpenRead(sourcePath);
            await using var gzip = new GZipStream(source, CompressionMode.Decompress);
            await using var destination = File.Create(destinationPath);
            await gzip.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<string> ExtractSqlFromArchiveAsync(string archivePath, CancellationToken cancellationToken)
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"gdp-validate-{Guid.NewGuid():N}.sql");
            await Task.Run(() =>
            {
                using var archive = ArchiveFactory.Open(archivePath);
                var entry = archive.Entries.FirstOrDefault(e =>
                    !e.IsDirectory &&
                    e.Key.EndsWith(".sql", StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    throw new InvalidOperationException("Archive does not contain a .sql file.");
                }

                entry.WriteToFile(tempPath, new ExtractionOptions
                {
                    ExtractFullPath = false,
                    Overwrite = true
                });
            }, cancellationToken).ConfigureAwait(false);
            return tempPath;
        }

        private static bool IsLoopbackHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            var normalized = host.Trim();
            return string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "::1", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildValidationWarningMessage(string stage, string reason)
        {
            var safeStage = string.IsNullOrWhiteSpace(stage) ? "unknown" : stage.Trim();
            var safeReason = string.IsNullOrWhiteSpace(reason)
                ? "No additional details were provided by the validator."
                : reason.Replace(Environment.NewLine, " ").Trim();

            return $"Validation warning (stage: {safeStage}): {safeReason}";
        }

        private static string BuildValidationDatabaseName(string seed)
        {
            var safeSeed = string.IsNullOrWhiteSpace(seed)
                ? "run"
                : new string(seed.Where(ch => char.IsLetterOrDigit(ch)).ToArray());
            if (string.IsNullOrWhiteSpace(safeSeed))
            {
                safeSeed = "run";
            }

            safeSeed = safeSeed.Length > 8 ? safeSeed[..8] : safeSeed;
            var suffix = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}"[..21];
            return $"{ValidationDbPrefix}{safeSeed}_{suffix}";
        }

        private static bool CanDropValidationDatabase(string databaseName)
        {
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                return false;
            }

            if (SystemDatabases.Contains(databaseName))
            {
                return false;
            }

            return databaseName.StartsWith(ValidationDbPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            var order = Math.Min(units.Length - 1, (int)Math.Floor(Math.Log(bytes, 1024)));
            var adjusted = bytes / Math.Pow(1024, order);
            return $"{adjusted:0.##} {units[order]}";
        }
    }

    public sealed class LocalValidationProbeResult
    {
        public bool IsSuccess { get; private init; }
        public string Message { get; private init; } = string.Empty;
        public string EffectiveCharset { get; private init; } = string.Empty;
        public string EffectiveCollation { get; private init; } = string.Empty;

        public static LocalValidationProbeResult Success(string message) => new()
        {
            IsSuccess = true,
            Message = message
        };

        public static LocalValidationProbeResult Success(string message, string charset, string collation) => new()
        {
            IsSuccess = true,
            Message = message,
            EffectiveCharset = charset ?? string.Empty,
            EffectiveCollation = collation ?? string.Empty
        };

        public static LocalValidationProbeResult Fail(string message) => new()
        {
            IsSuccess = false,
            Message = message
        };
    }

    public sealed class LocalValidationInspectResult
    {
        public bool IsSuccess { get; private init; }
        public bool DatabaseExists { get; private init; }
        public string ConfiguredDatabase { get; private init; } = string.Empty;
        public string EffectiveCharset { get; private init; } = string.Empty;
        public string EffectiveCollation { get; private init; } = string.Empty;
        public string Message { get; private init; } = string.Empty;

        public static LocalValidationInspectResult Success(
            bool databaseExists,
            string configuredDatabase,
            string effectiveCharset,
            string effectiveCollation,
            string message) => new()
            {
                IsSuccess = true,
                DatabaseExists = databaseExists,
                ConfiguredDatabase = configuredDatabase ?? string.Empty,
                EffectiveCharset = effectiveCharset ?? string.Empty,
                EffectiveCollation = effectiveCollation ?? string.Empty,
                Message = message
            };

        public static LocalValidationInspectResult Fail(string message) => new()
        {
            IsSuccess = false,
            Message = message
        };
    }

    public sealed class BackupRestoreValidationResult
    {
        public bool IsAttempted { get; private init; }
        public bool Passed { get; private init; }
        public bool IsWarning => IsAttempted && !Passed;
        public string Message { get; private init; } = string.Empty;
        public string ValidationDatabaseName { get; private set; } = string.Empty;

        public static BackupRestoreValidationResult Success(string message) => new()
        {
            IsAttempted = true,
            Passed = true,
            Message = message
        };

        public static BackupRestoreValidationResult Warning(string message) => new()
        {
            IsAttempted = true,
            Passed = false,
            Message = message
        };

        public static BackupRestoreValidationResult Skipped(string message) => new()
        {
            IsAttempted = false,
            Passed = false,
            Message = message
        };

        public BackupRestoreValidationResult WithDatabase(string dbName)
        {
            ValidationDatabaseName = dbName ?? string.Empty;
            return this;
        }
    }
}
