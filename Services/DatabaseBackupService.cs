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
using Renci.SshNet;
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
                Message = $"Connecting to database '{connectionEntry.DatabaseName}' …",
                Stage = "Connecting"
            });

            if (schedule.BackupMode == BackupMode.ExternalTool)
            {
                return await RunExternalBackupAsync(profile, schedule, progress, cancellationToken, pauseToken).ConfigureAwait(false);
            }

            if (schedule.BackupMode == BackupMode.RemoteSshMysqldump)
            {
                return await RunRemoteSshBackupAsync(profile, schedule, progress, cancellationToken, pauseToken).ConfigureAwait(false);
            }

            if (schedule.BackupMode == BackupMode.RemoteSshFileBuild)
            {
                return await RunRemoteSshFileBuildAsync(profile, schedule, progress, cancellationToken, pauseToken).ConfigureAwait(false);
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
                Message = $"Analyzing database structure ({totalTables} table{(totalTables == 1 ? string.Empty : "s")}) …",
                TotalTables = totalTables,
                ProcessedTables = 0,
                Stage = "Preparing"
            });

            var (scheduleRoot, artifactBaseName, workingFolder, sqlPath) = PrepareExecutionPaths(schedule, connectionEntry.DatabaseName);

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
                        Message = $"Backing up table {table} ({tableIndex}/{totalTables}) …",
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
                        Message = $"Finished table {table}.",
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

            var result = await FinalizeBackupArtifactsAsync(
                    schedule,
                    scheduleRoot,
                    workingFolder,
                    sqlPath,
                    artifactBaseName,
                    totalTables,
                    totalRows,
                    progress,
                    pauseToken,
                    cancellationToken)
                .ConfigureAwait(false);

            await client.DisconnectAsync();
            return result;
        }

        private async Task<BackupExecutionResult> RunExternalBackupAsync(
            ConnectionProfile profile,
            BackupSchedule schedule,
            IProgress<BackupProgressUpdate>? progress,
            CancellationToken cancellationToken,
            PauseTokenSource? pauseToken = null)
        {
            DatabaseClient? tunnelClient = null;
            Process? process = null;
            try
            {
                if (string.IsNullOrWhiteSpace(schedule.DatabaseName))
                {
                    throw new InvalidOperationException("Select a database name before running external backup.");
                }

                if (profile.UseSSH)
                {
                    var entry = DatabaseConnectionEntry.FromProfile(profile);
                    tunnelClient = new DatabaseClient();
                    await tunnelClient.ConnectAsync(entry.ToConnectionInfo());
                }

                var (scheduleRoot, artifactBaseName, workingFolder, sqlPath) = PrepareExecutionPaths(schedule, schedule.DatabaseName);

                var host = profile.UseSSH ? "127.0.0.1" : profile.Host;
                var port = profile.UseSSH && tunnelClient != null ? tunnelClient.TunnelPort : (uint)profile.Port;
                var args = BuildMysqldumpArgs(host, port, profile.DbUsername, schedule.DatabaseName);

                var startInfo = new ProcessStartInfo("mysqldump", args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var dbPassword = ResolveDecryptedSecret(profile.DbPassword);
                if (!string.IsNullOrEmpty(dbPassword))
                {
                    startInfo.Environment["MYSQL_PWD"] = dbPassword;
                }

                process = new Process { StartInfo = startInfo };
                process.Start();
                progress?.Report(new BackupProgressUpdate
                {
                    Message = "mysqldump started. Receiving backup stream …",
                    Stage = "ExternalDumpStart",
                    TotalTables = 0,
                    ProcessedTables = 0
                });
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromHours(2));
                var effectiveToken = timeoutCts.Token;

                await using var fileStream = new FileStream(sqlPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long streamedBytes = 0;
                long lastReportedBytes = 0;
                var reportClock = Stopwatch.StartNew();
                while (true)
                {
                    var read = await process.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), effectiveToken).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    if (pauseToken != null)
                    {
                        await pauseToken.WaitWhilePausedAsync(effectiveToken).ConfigureAwait(false);
                    }

                    await fileStream.WriteAsync(buffer.AsMemory(0, read), effectiveToken).ConfigureAwait(false);
                    streamedBytes += read;
                    if (streamedBytes - lastReportedBytes >= 2L * 1024 * 1024 || reportClock.ElapsedMilliseconds >= 1500)
                    {
                        progress?.Report(new BackupProgressUpdate
                        {
                            Message = $"Receiving backup stream: {FormatByteSize(streamedBytes)} downloaded …",
                            Stage = "ExternalDumpStreaming",
                            TotalTables = 0,
                            ProcessedTables = 0
                        });
                        lastReportedBytes = streamedBytes;
                        reportClock.Restart();
                    }
                }

                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(effectiveToken).ConfigureAwait(false);
                progress?.Report(new BackupProgressUpdate
                {
                    Message = $"Dump stream completed ({FormatByteSize(streamedBytes)}). Finalizing backup artifact …",
                    Stage = "Finalizing",
                    TotalTables = 0,
                    ProcessedTables = 0
                });

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"mysqldump failed: {error.Trim()}");
                }

                return await FinalizeBackupArtifactsAsync(
                        schedule,
                        scheduleRoot,
                        workingFolder,
                        sqlPath,
                        artifactBaseName,
                        0,
                        0,
                        progress,
                        pauseToken,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (process != null)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch
                    {
                        // Ignore cleanup failures.
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
                tunnelClient?.Dispose();
            }
        }

        private async Task<BackupExecutionResult> RunRemoteSshBackupAsync(
            ConnectionProfile profile,
            BackupSchedule schedule,
            IProgress<BackupProgressUpdate>? progress,
            CancellationToken cancellationToken,
            PauseTokenSource? pauseToken = null)
        {
            if (!profile.UseSSH)
            {
                throw new InvalidOperationException("Remote SSH backup mode requires an SSH-enabled connection profile.");
            }

            if (string.IsNullOrWhiteSpace(schedule.DatabaseName))
            {
                throw new InvalidOperationException("Select a database name before running SSH backup.");
            }

            var (scheduleRoot, artifactBaseName, workingFolder, sqlPath) = PrepareExecutionPaths(schedule, schedule.DatabaseName);
            progress?.Report(new BackupProgressUpdate
            {
                Message = "Connecting to SSH server and starting remote mysqldump …",
                Stage = "RemoteDumpStart",
                ProcessedTables = 0,
                TotalTables = 0
            });

            await Task.Run(async () =>
            {
                using var sshClient = BuildSshClient(profile);
                sshClient.Connect();

                using var command = sshClient.CreateCommand(BuildRemoteMysqldumpCommand(profile, schedule.DatabaseName));
                command.CommandTimeout = TimeSpan.FromHours(2);
                var asyncResult = command.BeginExecute();

                await using var outputStream = new FileStream(sqlPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long streamedBytes = 0;
                long lastReportedBytes = 0;
                var reportClock = Stopwatch.StartNew();
                var stderrBuilder = new StringBuilder();

                var stderrTask = Task.Run(async () =>
                {
                    var errorBuffer = new byte[4096];
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var errorRead = await command.ExtendedOutputStream
                            .ReadAsync(errorBuffer.AsMemory(0, errorBuffer.Length), cancellationToken)
                            .ConfigureAwait(false);
                        if (errorRead > 0)
                        {
                            stderrBuilder.Append(Encoding.UTF8.GetString(errorBuffer, 0, errorRead));
                            continue;
                        }

                        if (asyncResult.IsCompleted)
                        {
                            break;
                        }

                        await Task.Delay(40, cancellationToken).ConfigureAwait(false);
                    }
                }, cancellationToken);

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pauseToken != null)
                    {
                        await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                    }

                    var read = await command.OutputStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                    if (read > 0)
                    {
                        await outputStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        streamedBytes += read;
                        if (streamedBytes - lastReportedBytes >= 2L * 1024 * 1024 || reportClock.ElapsedMilliseconds >= 1500)
                        {
                            progress?.Report(new BackupProgressUpdate
                            {
                                Message = $"Streaming dump from server: {FormatByteSize(streamedBytes)} received …",
                                Stage = "RemoteDumpStreaming",
                                TotalTables = 0,
                                ProcessedTables = 0
                            });
                            lastReportedBytes = streamedBytes;
                            reportClock.Restart();
                        }
                        continue;
                    }

                    if (asyncResult.IsCompleted)
                    {
                        break;
                    }

                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }

                command.EndExecute(asyncResult);
                await stderrTask.ConfigureAwait(false);
                if (command.ExitStatus != 0)
                {
                    var stderr = stderrBuilder.ToString().Trim();
                    if (string.IsNullOrWhiteSpace(stderr) && !string.IsNullOrWhiteSpace(command.Error))
                    {
                        stderr = command.Error.Trim();
                    }

                    var message = string.IsNullOrWhiteSpace(stderr)
                        ? $"Exit code {command.ExitStatus} after streaming {FormatByteSize(streamedBytes)}. Remote process returned failure without stderr details."
                        : $"Exit code {command.ExitStatus} after streaming {FormatByteSize(streamedBytes)}. {stderr}";
                    throw new InvalidOperationException($"Remote mysqldump failed: {message}");
                }

                progress?.Report(new BackupProgressUpdate
                {
                    Message = $"Remote dump completed ({FormatByteSize(streamedBytes)}). Finalizing backup artifact …",
                    Stage = "Finalizing",
                    TotalTables = 0,
                    ProcessedTables = 0
                });
            }, cancellationToken).ConfigureAwait(false);

            return await FinalizeBackupArtifactsAsync(
                    schedule,
                    scheduleRoot,
                    workingFolder,
                    sqlPath,
                    artifactBaseName,
                    0,
                    0,
                    progress,
                    pauseToken,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<BackupExecutionResult> RunRemoteSshFileBuildAsync(
            ConnectionProfile profile,
            BackupSchedule schedule,
            IProgress<BackupProgressUpdate>? progress,
            CancellationToken cancellationToken,
            PauseTokenSource? pauseToken = null)
        {
            if (!profile.UseSSH)
            {
                throw new InvalidOperationException("Remote file build mode requires an SSH-enabled connection profile.");
            }

            if (string.IsNullOrWhiteSpace(schedule.DatabaseName))
            {
                throw new InvalidOperationException("Select a database name before running SSH remote file build.");
            }

            var remoteDirectory = string.IsNullOrWhiteSpace(schedule.RemoteOutputDirectory)
                ? "/tmp/gitdeploypro-backups"
                : schedule.RemoteOutputDirectory.Trim();
            var artifactBaseName = BackupArtifactNaming.CreateArtifactBaseName(schedule.DatabaseName);
            var preferredRemoteArtifactPath = CombineRemotePath(remoteDirectory, $"{artifactBaseName}.sql.gz");
            var remoteArtifactPath = await EnsureUniqueRemoteArtifactPathAsync(profile, preferredRemoteArtifactPath, cancellationToken).ConfigureAwait(false);
            var remoteTmpSqlPath = remoteArtifactPath + ".part.sql";
            var remoteTmpGzipPath = remoteArtifactPath + ".part.gz";

            progress?.Report(new BackupProgressUpdate
            {
                Message = $"Connecting to SSH server and preparing remote build path '{remoteDirectory}' …",
                Stage = "RemoteFilePrepare",
                ProcessedTables = 0,
                TotalTables = 0
            });

            RemoteBuildSnapshot finalSnapshot = RemoteBuildSnapshot.Empty;
            string remoteSha256 = string.Empty;

            await Task.Run(async () =>
            {
                using var sshClient = BuildSshClient(profile);
                sshClient.Connect();

                var buildCommandText = BuildRemoteFileBuildCommand(profile, schedule.DatabaseName, remoteDirectory, remoteArtifactPath, remoteTmpSqlPath, remoteTmpGzipPath);
                using var command = sshClient.CreateCommand(buildCommandText);
                command.CommandTimeout = TimeSpan.FromHours(4);
                var asyncResult = command.BeginExecute();
                var stderrBuilder = new StringBuilder();

                var stderrTask = Task.Run(async () =>
                {
                    var errorBuffer = new byte[4096];
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var errorRead = await command.ExtendedOutputStream
                            .ReadAsync(errorBuffer.AsMemory(0, errorBuffer.Length), cancellationToken)
                            .ConfigureAwait(false);
                        if (errorRead > 0)
                        {
                            stderrBuilder.Append(Encoding.UTF8.GetString(errorBuffer, 0, errorRead));
                            continue;
                        }

                        if (asyncResult.IsCompleted)
                        {
                            break;
                        }

                        await Task.Delay(40, cancellationToken).ConfigureAwait(false);
                    }
                }, cancellationToken);

                var reportClock = Stopwatch.StartNew();
                var lastReportedSize = -1L;
                var lastReportedPhase = string.Empty;
                while (!asyncResult.IsCompleted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pauseToken != null)
                    {
                        await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                    }

                    var snapshot = TryGetRemoteBuildSnapshot(sshClient, remoteTmpSqlPath, remoteTmpGzipPath, remoteArtifactPath);
                    var (stage, labelSize) = DetermineRemoteBuildPhase(snapshot);

                    if (labelSize != lastReportedSize || !string.Equals(stage, lastReportedPhase, StringComparison.Ordinal) || reportClock.ElapsedMilliseconds >= 1500)
                    {
                        progress?.Report(new BackupProgressUpdate
                        {
                            Message = $"{GetRemoteBuildPhaseMessage(stage)}: {FormatByteSize(Math.Max(0, labelSize))} …",
                            Stage = stage,
                            ProcessedTables = 0,
                            TotalTables = 0
                        });
                        lastReportedSize = labelSize;
                        lastReportedPhase = stage;
                        reportClock.Restart();
                    }

                    await Task.Delay(1200, cancellationToken).ConfigureAwait(false);
                }

                command.EndExecute(asyncResult);
                await stderrTask.ConfigureAwait(false);

                finalSnapshot = TryGetRemoteBuildSnapshot(sshClient, remoteTmpSqlPath, remoteTmpGzipPath, remoteArtifactPath);
                if (command.ExitStatus != 0)
                {
                    var stderr = stderrBuilder.ToString().Trim();
                    if (string.IsNullOrWhiteSpace(stderr) && !string.IsNullOrWhiteSpace(command.Error))
                    {
                        stderr = command.Error.Trim();
                    }

                    var stderrText = string.IsNullOrWhiteSpace(stderr) ? "No stderr details were reported by remote shell." : stderr;
                    throw new InvalidOperationException(
                        $"Remote file build failed (exit {command.ExitStatus}). Final observed size: {FormatByteSize(finalSnapshot.FinalArtifactBytes)}. {stderrText}");
                }

                if (finalSnapshot.FinalArtifactBytes <= 0)
                {
                    throw new InvalidOperationException("Remote build finished but final artifact is empty or missing.");
                }

                remoteSha256 = TryGetRemoteFileSha256(sshClient, remoteArtifactPath);
                progress?.Report(new BackupProgressUpdate
                {
                    Message = $"Remote artifact created: {FormatByteSize(finalSnapshot.FinalArtifactBytes)} at {remoteArtifactPath}",
                    Stage = "RemoteFileBuilt",
                    ProcessedTables = 0,
                    TotalTables = 0
                });
            }, cancellationToken).ConfigureAwait(false);

            var result = new BackupExecutionResult
            {
                OutputPath = remoteArtifactPath,
                BytesWritten = finalSnapshot.FinalArtifactBytes,
                Sha256 = remoteSha256,
                TableCount = 0,
                RowCount = 0,
                IsCompressed = true,
                IsRemoteArtifact = true,
                HasLocalArtifact = false,
                RemoteArtifactPath = remoteArtifactPath,
                RemoteArtifactBytes = finalSnapshot.FinalArtifactBytes,
                RemoteArtifactSha256 = remoteSha256
            };

            if (schedule.RemoteDownloadPolicy == RemoteArtifactDownloadPolicy.AutoDownload)
            {
                var scheduleRoot = GetScheduleRoot(schedule);
                Directory.CreateDirectory(scheduleRoot);
                var localFileName = Path.GetFileName(remoteArtifactPath);
                var localTargetPath = Path.Combine(scheduleRoot, localFileName);
                localTargetPath = EnsureUniqueFilePath(localTargetPath);

                progress?.Report(new BackupProgressUpdate
                {
                    Message = "Remote artifact ready. Starting SFTP download …",
                    Stage = "RemoteFileDownloadStart",
                    ProcessedTables = 0,
                    TotalTables = 0
                });

                await DownloadRemoteArtifactAsync(profile, remoteArtifactPath, finalSnapshot.FinalArtifactBytes, localTargetPath, progress, cancellationToken, pauseToken)
                    .ConfigureAwait(false);

                var localSha = ComputeSha256(localTargetPath);
                if (!string.IsNullOrWhiteSpace(remoteSha256) &&
                    !string.Equals(localSha, remoteSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Downloaded artifact hash does not match remote hash. File integrity check failed.");
                }

                progress?.Report(new BackupProgressUpdate
                {
                    Message = "Downloaded artifact verified successfully.",
                    Stage = "RemoteFileDownloadVerified",
                    ProcessedTables = 0,
                    TotalTables = 0
                });

                var finalLocalPath = localTargetPath;
                if (schedule.EncryptAtRest)
                {
                    progress?.Report(new BackupProgressUpdate
                    {
                        Message = "Encrypting downloaded backup file at rest …",
                        Stage = "Encrypting",
                        ProcessedTables = 0,
                        TotalTables = 0
                    });
                    finalLocalPath = ProtectFileAtRest(localTargetPath);
                }

                ApplyRetention(scheduleRoot, Math.Max(1, schedule.RetentionCount));
                result.HasLocalArtifact = true;
                result.OutputPath = finalLocalPath;
                result.BytesWritten = GetFileSize(finalLocalPath);
                result.Sha256 = ComputeSha256(finalLocalPath);

                if (schedule.DeleteRemoteArtifactAfterDownload)
                {
                    progress?.Report(new BackupProgressUpdate
                    {
                        Message = "Remote cleanup enabled. Verifying safe delete rules …",
                        Stage = "RemoteFileCleanupStart",
                        ProcessedTables = 0,
                        TotalTables = 0
                    });

                    var cleanupResult = await TryDeleteRemoteArtifactSafelyAsync(
                            profile,
                            remoteArtifactPath,
                            remoteDirectory,
                            progress,
                            cancellationToken,
                            pauseToken)
                        .ConfigureAwait(false);

                    result.RemoteArtifactDeleted = cleanupResult.Deleted;
                    result.RemoteCleanupMessage = cleanupResult.Message;
                }
            }

            return result;
        }

        private static (string scheduleRoot, string artifactBaseName, string workingFolder, string sqlPath) PrepareExecutionPaths(BackupSchedule schedule, string databaseName)
        {
            var scheduleRoot = GetScheduleRoot(schedule);
            Directory.CreateDirectory(scheduleRoot);
            var artifactBaseName = BackupArtifactNaming.CreateArtifactBaseName(databaseName);
            var workingFolder = EnsureUniqueDirectory(Path.Combine(scheduleRoot, $"{artifactBaseName}_work"));
            Directory.CreateDirectory(workingFolder);
            var sqlPath = Path.Combine(workingFolder, $"{artifactBaseName}.sql");
            return (scheduleRoot, artifactBaseName, workingFolder, sqlPath);
        }

        private async Task<BackupExecutionResult> FinalizeBackupArtifactsAsync(
            BackupSchedule schedule,
            string scheduleRoot,
            string workingFolder,
            string sqlPath,
            string artifactBaseName,
            int tableCount,
            long rowCount,
            IProgress<BackupProgressUpdate>? progress,
            PauseTokenSource? pauseToken,
            CancellationToken cancellationToken)
        {
            string finalPath;
            if (schedule.CompressOutput)
            {
                progress?.Report(new BackupProgressUpdate
                {
                    Message = "Compressing backup output …",
                    TotalTables = tableCount,
                    ProcessedTables = tableCount,
                    Stage = "Compressing"
                });

                if (pauseToken != null)
                {
                    await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                }

                if (schedule.CompressionFormat == BackupCompressionFormat.TarGz)
                {
                    finalPath = await CreateTarGzArchiveAsync(workingFolder, scheduleRoot, artifactBaseName, pauseToken, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var zipPath = EnsureUniqueFilePath(Path.Combine(scheduleRoot, $"{artifactBaseName}.zip"));
                    if (File.Exists(zipPath))
                    {
                        File.Delete(zipPath);
                    }

                    ZipFile.CreateFromDirectory(workingFolder, zipPath, CompressionLevel.Optimal, false);
                    Directory.Delete(workingFolder, true);
                    finalPath = zipPath;
                }
            }
            else
            {
                var preferredSqlPath = EnsureUniqueFilePath(Path.Combine(scheduleRoot, $"{artifactBaseName}.sql"));
                if (string.Equals(sqlPath, preferredSqlPath, StringComparison.OrdinalIgnoreCase))
                {
                    finalPath = sqlPath;
                }
                else
                {
                    File.Move(sqlPath, preferredSqlPath);
                    finalPath = preferredSqlPath;
                }

                if (Directory.Exists(workingFolder))
                {
                    Directory.Delete(workingFolder, true);
                }
            }

            if (schedule.EncryptAtRest)
            {
                progress?.Report(new BackupProgressUpdate
                {
                    Message = "Encrypting backup file at rest …",
                    TotalTables = tableCount,
                    ProcessedTables = tableCount,
                    Stage = "Encrypting"
                });
                finalPath = ProtectFileAtRest(finalPath);
            }

            ApplyRetention(scheduleRoot, Math.Max(1, schedule.RetentionCount));
            progress?.Report(new BackupProgressUpdate
            {
                Message = "Backup artifact is ready.",
                TotalTables = tableCount,
                ProcessedTables = tableCount,
                Stage = "Completed"
            });
            return BuildResult(finalPath, tableCount, rowCount, schedule.CompressOutput);
        }

        private static BackupExecutionResult BuildResult(string outputPath, int tableCount, long rowCount, bool compressed)
        {
            return new BackupExecutionResult
            {
                OutputPath = outputPath,
                BytesWritten = GetFileSize(outputPath),
                Sha256 = ComputeSha256(outputPath),
                TableCount = tableCount,
                RowCount = rowCount,
                IsCompressed = compressed,
                HasLocalArtifact = true
            };
        }

        private static string BuildMysqldumpArgs(string host, uint port, string user, string databaseName)
        {
            var resolvedHost = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host;
            var resolvedUser = string.IsNullOrWhiteSpace(user) ? "root" : user;
            var safeDb = EscapeShellDoubleQuoted(databaseName);
            var safeHost = EscapeShellDoubleQuoted(resolvedHost);
            var safeUser = EscapeShellDoubleQuoted(resolvedUser);

            return $"--single-transaction --quick --routines --triggers --host=\"{safeHost}\" --port={port} --user=\"{safeUser}\" \"{safeDb}\"";
        }

        private static SshClient BuildSshClient(ConnectionProfile profile)
        {
            return new SshClient(BuildSshConnectionInfo(profile));
        }

        private static SftpClient BuildSftpClient(ConnectionProfile profile)
        {
            return new SftpClient(BuildSshConnectionInfo(profile));
        }

        private static ConnectionInfo BuildSshConnectionInfo(ConnectionProfile profile)
        {
            if (string.IsNullOrWhiteSpace(profile.Host))
            {
                throw new InvalidOperationException("SSH host is required for remote backup mode.");
            }

            if (string.IsNullOrWhiteSpace(profile.Username))
            {
                throw new InvalidOperationException("SSH username is required for remote backup mode.");
            }

            var authMethods = new List<AuthenticationMethod>();
            if (!string.IsNullOrWhiteSpace(profile.PrivateKeyPath) && File.Exists(profile.PrivateKeyPath))
            {
                var keyFile = new PrivateKeyFile(profile.PrivateKeyPath);
                authMethods.Add(new PrivateKeyAuthenticationMethod(profile.Username, keyFile));
            }

            var sshPassword = ResolveDecryptedSecret(profile.Password);
            if (!string.IsNullOrWhiteSpace(sshPassword))
            {
                authMethods.Add(new PasswordAuthenticationMethod(profile.Username, sshPassword));
            }

            if (authMethods.Count == 0)
            {
                throw new InvalidOperationException("Provide SSH password or private key for remote backup mode.");
            }

            var sshPort = profile.Port > 0 ? profile.Port : 22;
            return new ConnectionInfo(profile.Host, sshPort, profile.Username, authMethods.ToArray());
        }

        private static string BuildRemoteMysqldumpCommand(ConnectionProfile profile, string databaseName)
        {
            var dbHost = string.IsNullOrWhiteSpace(profile.DbHost) ? "127.0.0.1" : profile.DbHost;
            var dbPort = profile.DbPort > 0 ? profile.DbPort : 3306;
            var dbUser = string.IsNullOrWhiteSpace(profile.DbUsername) ? "root" : profile.DbUsername;

            var dbPassword = ResolveDecryptedSecret(profile.DbPassword);
            var passwordPrefix = string.IsNullOrWhiteSpace(dbPassword)
                ? string.Empty
                : $"MYSQL_PWD={EscapeForShellLiteral(dbPassword)} ";

            return $"{passwordPrefix}mysqldump --single-transaction --quick --routines --triggers --host={EscapeForShellLiteral(dbHost)} --port={dbPort} --user={EscapeForShellLiteral(dbUser)} --databases {EscapeForShellLiteral(databaseName)}";
        }

        private static string BuildRemoteFileBuildCommand(
            ConnectionProfile profile,
            string databaseName,
            string remoteDirectory,
            string remoteArtifactPath,
            string remoteTmpSqlPath,
            string remoteTmpGzipPath)
        {
            var dbHost = string.IsNullOrWhiteSpace(profile.DbHost) ? "127.0.0.1" : profile.DbHost;
            var dbPort = profile.DbPort > 0 ? profile.DbPort : 3306;
            var dbUser = string.IsNullOrWhiteSpace(profile.DbUsername) ? "root" : profile.DbUsername;

            var dbPassword = ResolveDecryptedSecret(profile.DbPassword);
            var passwordPrefix = string.IsNullOrWhiteSpace(dbPassword)
                ? string.Empty
                : $"MYSQL_PWD={EscapeForShellLiteral(dbPassword)} ";

            var escapedDirectory = EscapeForShellLiteral(remoteDirectory);
            var escapedFinal = EscapeForShellLiteral(remoteArtifactPath);
            var escapedTmpSql = EscapeForShellLiteral(remoteTmpSqlPath);
            var escapedTmpGzip = EscapeForShellLiteral(remoteTmpGzipPath);
            var escapedDbHost = EscapeForShellLiteral(dbHost);
            var escapedDbUser = EscapeForShellLiteral(dbUser);
            var escapedDbName = EscapeForShellLiteral(databaseName);

            return $"mkdir -p {escapedDirectory} && " +
                   $"rm -f {escapedFinal} {escapedTmpSql} {escapedTmpGzip} && " +
                   $"{passwordPrefix}mysqldump --single-transaction --quick --routines --triggers --host={escapedDbHost} --port={dbPort} --user={escapedDbUser} --databases {escapedDbName} --result-file={escapedTmpSql} && " +
                   $"gzip -1 -c {escapedTmpSql} > {escapedTmpGzip} && " +
                   $"rm -f {escapedTmpSql} && " +
                   $"mv {escapedTmpGzip} {escapedFinal}";
        }

        private static RemoteBuildSnapshot TryGetRemoteBuildSnapshot(SshClient sshClient, string tmpSqlPath, string tmpGzipPath, string finalPath)
        {
            try
            {
                var cmdText = BuildRemoteSnapshotCommand(tmpSqlPath, tmpGzipPath, finalPath);
                using var cmd = sshClient.CreateCommand(cmdText);
                cmd.CommandTimeout = TimeSpan.FromSeconds(15);
                var output = (cmd.Execute() ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(output))
                {
                    return RemoteBuildSnapshot.Empty;
                }

                var parts = output.Split(':');
                if (parts.Length != 3)
                {
                    return RemoteBuildSnapshot.Empty;
                }

                _ = long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sqlBytes);
                _ = long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gzipBytes);
                _ = long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var finalBytes);
                return new RemoteBuildSnapshot(sqlBytes, gzipBytes, finalBytes);
            }
            catch
            {
                return RemoteBuildSnapshot.Empty;
            }
        }

        private static string BuildRemoteSnapshotCommand(string tmpSqlPath, string tmpGzipPath, string finalPath)
        {
            var escapedTmpSql = EscapeForShellLiteral(tmpSqlPath);
            var escapedTmpGzip = EscapeForShellLiteral(tmpGzipPath);
            var escapedFinal = EscapeForShellLiteral(finalPath);
            return "sql=0; gz=0; fin=0; " +
                   $"if [ -f {escapedTmpSql} ]; then sql=$(wc -c < {escapedTmpSql}); fi; " +
                   $"if [ -f {escapedTmpGzip} ]; then gz=$(wc -c < {escapedTmpGzip}); fi; " +
                   $"if [ -f {escapedFinal} ]; then fin=$(wc -c < {escapedFinal}); fi; " +
                   "echo \"$sql:$gz:$fin\"";
        }

        private static (string stage, long labelSize) DetermineRemoteBuildPhase(RemoteBuildSnapshot snapshot)
        {
            if (snapshot.FinalArtifactBytes > 0)
            {
                return ("RemoteFileFinalizing", snapshot.FinalArtifactBytes);
            }

            if (snapshot.TemporaryGzipBytes > 0)
            {
                return ("RemoteFileCompressing", snapshot.TemporaryGzipBytes);
            }

            return ("RemoteFileDumping", snapshot.TemporarySqlBytes);
        }

        private static string GetRemoteBuildPhaseMessage(string stage)
        {
            return stage switch
            {
                "RemoteFileFinalizing" => "Finalizing remote artifact",
                "RemoteFileCompressing" => "Compressing dump on server",
                _ => "Generating SQL dump on server"
            };
        }

        private static string TryGetRemoteFileSha256(SshClient sshClient, string remotePath)
        {
            try
            {
                var escaped = EscapeForShellLiteral(remotePath);
                var cmdText = "if command -v sha256sum >/dev/null 2>&1; then " +
                              $"sha256sum {escaped} | awk '{{print $1}}'; " +
                              "elif command -v shasum >/dev/null 2>&1; then " +
                              $"shasum -a 256 {escaped} | awk '{{print $1}}'; " +
                              "else echo ''; fi";
                using var cmd = sshClient.CreateCommand(cmdText);
                cmd.CommandTimeout = TimeSpan.FromSeconds(30);
                return (cmd.Execute() ?? string.Empty).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string CombineRemotePath(string directory, string fileName)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return "/" + fileName.TrimStart('/');
            }

            var normalized = directory.Replace('\\', '/').TrimEnd('/');
            return $"{normalized}/{fileName.TrimStart('/')}";
        }

        private async Task<string> EnsureUniqueRemoteArtifactPathAsync(
            ConnectionProfile profile,
            string preferredRemotePath,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                using var sftpClient = BuildSftpClient(profile);
                sftpClient.Connect();

                if (!sftpClient.Exists(preferredRemotePath))
                {
                    return preferredRemotePath;
                }

                var normalized = preferredRemotePath.Replace('\\', '/');
                var slashIndex = normalized.LastIndexOf('/');
                var directory = slashIndex >= 0 ? normalized[..slashIndex] : string.Empty;
                var fileName = slashIndex >= 0 ? normalized[(slashIndex + 1)..] : normalized;

                var extension = fileName.EndsWith(".sql.gz", StringComparison.OrdinalIgnoreCase)
                    ? ".sql.gz"
                    : Path.GetExtension(fileName);
                var baseName = string.IsNullOrWhiteSpace(extension)
                    ? fileName
                    : fileName[..^extension.Length];

                for (var i = 1; i <= 999; i++)
                {
                    var candidateName = $"{baseName}_{i:00}{extension}";
                    var candidatePath = string.IsNullOrWhiteSpace(directory)
                        ? candidateName
                        : $"{directory}/{candidateName}";
                    if (!sftpClient.Exists(candidatePath))
                    {
                        return candidatePath;
                    }
                }

                var fallbackName = $"{baseName}_{Guid.NewGuid():N[..6]}{extension}";
                return string.IsNullOrWhiteSpace(directory)
                    ? fallbackName
                    : $"{directory}/{fallbackName}";
            }, cancellationToken).ConfigureAwait(false);
        }

        private async Task DownloadRemoteArtifactAsync(
            ConnectionProfile profile,
            string remoteArtifactPath,
            long expectedBytes,
            string localTargetPath,
            IProgress<BackupProgressUpdate>? progress,
            CancellationToken cancellationToken,
            PauseTokenSource? pauseToken)
        {
            await Task.Run(async () =>
            {
                using var sftpClient = BuildSftpClient(profile);
                sftpClient.Connect();

                var remoteLength = expectedBytes;
                try
                {
                    remoteLength = Math.Max(remoteLength, sftpClient.GetAttributes(remoteArtifactPath).Size);
                }
                catch
                {
                    // Keep expected size from build snapshot if stat fails.
                }

                using var remoteStream = sftpClient.OpenRead(remoteArtifactPath);
                await using var localStream = new FileStream(localTargetPath, FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[81920];
                long downloadedBytes = 0;
                long lastReportedBytes = 0;
                var reportClock = Stopwatch.StartNew();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pauseToken != null)
                    {
                        await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                    }

                    var read = await remoteStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    await localStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    downloadedBytes += read;

                    if (downloadedBytes - lastReportedBytes >= 2L * 1024 * 1024 || reportClock.ElapsedMilliseconds >= 1200)
                    {
                        var totalLabel = remoteLength > 0 ? $"/{FormatByteSize(remoteLength)}" : string.Empty;
                        progress?.Report(new BackupProgressUpdate
                        {
                            Message = $"Downloading remote artifact: {FormatByteSize(downloadedBytes)}{totalLabel} …",
                            Stage = "RemoteFileDownloading",
                            ProcessedTables = 0,
                            TotalTables = 0
                        });
                        lastReportedBytes = downloadedBytes;
                        reportClock.Restart();
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        private static string EnsureUniqueFilePath(string preferredPath)
        {
            if (!File.Exists(preferredPath))
            {
                return preferredPath;
            }

            var directory = Path.GetDirectoryName(preferredPath) ?? string.Empty;
            var fileName = Path.GetFileNameWithoutExtension(preferredPath);
            var extension = Path.GetExtension(preferredPath);
            var index = 1;
            while (true)
            {
                var candidate = Path.Combine(directory, $"{fileName}_{index}{extension}");
                if (!File.Exists(candidate))
                {
                    return candidate;
                }

                index++;
            }
        }

        private static string EnsureUniqueDirectory(string preferredDirectoryPath)
        {
            if (!Directory.Exists(preferredDirectoryPath))
            {
                return preferredDirectoryPath;
            }

            var parent = Path.GetDirectoryName(preferredDirectoryPath) ?? string.Empty;
            var name = Path.GetFileName(preferredDirectoryPath);
            var index = 1;
            while (true)
            {
                var candidate = Path.Combine(parent, $"{name}_{index:00}");
                if (!Directory.Exists(candidate))
                {
                    return candidate;
                }

                index++;
            }
        }

        private async Task<RemoteCleanupResult> TryDeleteRemoteArtifactSafelyAsync(
            ConnectionProfile profile,
            string remoteArtifactPath,
            string remoteOutputDirectory,
            IProgress<BackupProgressUpdate>? progress,
            CancellationToken cancellationToken,
            PauseTokenSource? pauseToken)
        {
            return await Task.Run(async () =>
            {
                if (!CanSafelyDeleteRemoteArtifact(remoteArtifactPath, remoteOutputDirectory, out var reason))
                {
                    progress?.Report(new BackupProgressUpdate
                    {
                        Message = $"Remote cleanup skipped (safe-guard): {reason}",
                        Stage = "RemoteFileCleanupSkipped",
                        ProcessedTables = 0,
                        TotalTables = 0
                    });
                    return RemoteCleanupResult.Skipped(reason);
                }

                using var sftpClient = BuildSftpClient(profile);
                sftpClient.Connect();

                cancellationToken.ThrowIfCancellationRequested();
                if (pauseToken != null)
                {
                    await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                }

                if (!sftpClient.Exists(remoteArtifactPath))
                {
                    progress?.Report(new BackupProgressUpdate
                    {
                        Message = "Remote cleanup skipped: artifact file already missing on server.",
                        Stage = "RemoteFileCleanupSkipped",
                        ProcessedTables = 0,
                        TotalTables = 0
                    });
                    return RemoteCleanupResult.Skipped("Artifact file already missing on server.");
                }

                var attributes = sftpClient.GetAttributes(remoteArtifactPath);
                if (!attributes.IsRegularFile)
                {
                    var notFileMessage = "Target path is not a regular file. Cleanup blocked by safe-guard.";
                    progress?.Report(new BackupProgressUpdate
                    {
                        Message = $"Remote cleanup skipped: {notFileMessage}",
                        Stage = "RemoteFileCleanupSkipped",
                        ProcessedTables = 0,
                        TotalTables = 0
                    });
                    return RemoteCleanupResult.Skipped(notFileMessage);
                }

                sftpClient.DeleteFile(remoteArtifactPath);
                progress?.Report(new BackupProgressUpdate
                {
                    Message = $"Remote artifact deleted safely: {remoteArtifactPath}",
                    Stage = "RemoteFileCleanupDone",
                    ProcessedTables = 0,
                    TotalTables = 0
                });
                return RemoteCleanupResult.Success($"Deleted remote artifact: {remoteArtifactPath}");
            }, cancellationToken).ConfigureAwait(false);
        }

        private static bool CanSafelyDeleteRemoteArtifact(string remoteArtifactPath, string remoteOutputDirectory, out string reason)
        {
            reason = string.Empty;

            var normalizedArtifact = NormalizeRemotePath(remoteArtifactPath);
            var normalizedOutput = NormalizeRemotePath(remoteOutputDirectory).TrimEnd('/');

            if (string.IsNullOrWhiteSpace(normalizedArtifact) || string.IsNullOrWhiteSpace(normalizedOutput))
            {
                reason = "Remote artifact path or remote output directory is empty.";
                return false;
            }

            if (!normalizedArtifact.StartsWith("/", StringComparison.Ordinal) ||
                !normalizedOutput.StartsWith("/", StringComparison.Ordinal))
            {
                reason = "Only absolute Linux paths are allowed for cleanup.";
                return false;
            }

            if (normalizedArtifact.EndsWith("/", StringComparison.Ordinal))
            {
                reason = "Target path resolves to a directory-like value.";
                return false;
            }

            if (string.Equals(normalizedArtifact, normalizedOutput, StringComparison.Ordinal))
            {
                reason = "Target path equals output directory.";
                return false;
            }

            if (!normalizedArtifact.StartsWith(normalizedOutput + "/", StringComparison.Ordinal))
            {
                reason = "Artifact is outside configured remote output directory.";
                return false;
            }

            var outputSegments = normalizedOutput.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (outputSegments.Length < 2)
            {
                reason = "Remote output directory is too broad. Use a dedicated subdirectory.";
                return false;
            }

            var fileName = Path.GetFileName(normalizedArtifact);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                reason = "Artifact file name is invalid.";
                return false;
            }

            if (!fileName.EndsWith(".sql.gz", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Only .sql.gz artifact files are allowed for cleanup.";
                return false;
            }

            if (normalizedArtifact.Contains('*') || normalizedArtifact.Contains('?'))
            {
                reason = "Wildcard path is not allowed.";
                return false;
            }

            return true;
        }

        private static string NormalizeRemotePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var normalized = path.Replace('\\', '/').Trim();
            while (normalized.Contains("//", StringComparison.Ordinal))
            {
                normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
            }
            return normalized;
        }

        private static string EscapeForShellLiteral(string value)
        {
            var safe = value ?? string.Empty;
            return $"'{safe.Replace("'", "'\"'\"'")}'";
        }

        private static string EscapeShellDoubleQuoted(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string FormatByteSize(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            var order = Math.Min(units.Length - 1, (int)Math.Floor(Math.Log(bytes, 1024)));
            var adjusted = bytes / Math.Pow(1024, order);
            return $"{adjusted:0.##} {units[order]}";
        }

        private static string ResolveDecryptedSecret(string encryptedOrPlain)
        {
            if (string.IsNullOrWhiteSpace(encryptedOrPlain))
            {
                return string.Empty;
            }

            var decrypted = EncryptionService.Decrypt(encryptedOrPlain);
            return string.IsNullOrWhiteSpace(decrypted) ? encryptedOrPlain : decrypted;
        }

        private static string ProtectFileAtRest(string sourcePath)
        {
            var plainBytes = File.ReadAllBytes(sourcePath);
            var protectedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            var protectedPath = sourcePath + ".protected";
            File.WriteAllBytes(protectedPath, protectedBytes);
            File.Delete(sourcePath);
            return protectedPath;
        }

        private static async Task<string> CreateTarGzArchiveAsync(
            string workingFolder,
            string scheduleRoot,
            string artifactBaseName,
            PauseTokenSource? pauseToken,
            CancellationToken token)
        {
            var tarPath = EnsureUniqueFilePath(Path.Combine(scheduleRoot, $"{artifactBaseName}.tar"));
            var tarGzPath = EnsureUniqueFilePath(Path.Combine(scheduleRoot, $"{artifactBaseName}.tar.gz"));

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
            WriteTableDataInternalAsync(connection, writer, tableName, tableRowCount, progress, processedTables, totalTables, pauseToken, token, "Reading rows");

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
            WriteTableDataInternalAsync(connection, writer, tableName, tableRowCount, progress, processedTables, totalTables, pauseToken, token, "Fast row scan");

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
                        Message = $"{progressLabel} from {tableName}: {rowCounter:N0}/{tableRowCount:N0}",
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
                    Message = $"{progressLabel} from {tableName}: {rowCounter:N0}/{tableRowCount:N0}",
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

        private readonly struct RemoteBuildSnapshot
        {
            public static RemoteBuildSnapshot Empty => new(0, 0, 0);

            public RemoteBuildSnapshot(long temporarySqlBytes, long temporaryGzipBytes, long finalArtifactBytes)
            {
                TemporarySqlBytes = temporarySqlBytes;
                TemporaryGzipBytes = temporaryGzipBytes;
                FinalArtifactBytes = finalArtifactBytes;
            }

            public long TemporarySqlBytes { get; }
            public long TemporaryGzipBytes { get; }
            public long FinalArtifactBytes { get; }
        }

        private readonly struct RemoteCleanupResult
        {
            public static RemoteCleanupResult Success(string message) => new(true, message);
            public static RemoteCleanupResult Skipped(string message) => new(false, message);

            public RemoteCleanupResult(bool deleted, string message)
            {
                Deleted = deleted;
                Message = message;
            }

            public bool Deleted { get; }
            public string Message { get; }
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
        public bool IsRemoteArtifact { get; set; }
        public bool HasLocalArtifact { get; set; } = true;
        public string RemoteArtifactPath { get; set; } = string.Empty;
        public long RemoteArtifactBytes { get; set; }
        public string RemoteArtifactSha256 { get; set; } = string.Empty;
        public bool RemoteArtifactDeleted { get; set; }
        public string RemoteCleanupMessage { get; set; } = string.Empty;
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

