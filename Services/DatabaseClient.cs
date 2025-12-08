using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Models;
using MySqlConnector;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace GitDeployPro.Services
{
    public class DatabaseClient : IDisposable, IAsyncDisposable
    {
        private MySqlConnection? _mysqlConnection;
        private DatabaseType _currentType = DatabaseType.None;
        private string? _activeDatabase;
        private SshClient? _sshClient;
        private ForwardedPortLocal? _forwardedPort;
        private uint _localTunnelPort;

        public bool IsConnected => _mysqlConnection?.State == ConnectionState.Open;
        public uint TunnelPort => _localTunnelPort;
        public DatabaseType CurrentType => _currentType;
        public string? ActiveDatabase => _activeDatabase;

        public async Task ConnectAsync(DatabaseConnectionInfo info)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));

            await DisconnectAsync();

            if (info.DbType != DatabaseType.MySQL && info.DbType != DatabaseType.MariaDB)
            {
                throw new NotSupportedException("Only MySQL / MariaDB connections are supported in this version.");
            }

            try
            {
                if (info.UseSshTunnel)
                {
                    await SetupSshTunnelAsync(info);
                }

                var defaultHost = string.IsNullOrWhiteSpace(info.Host) ? "127.0.0.1" : info.Host;
                var defaultPort = (uint)(info.Port <= 0 ? 3306 : info.Port);
                var builder = new MySqlConnectionStringBuilder
                {
                    Server = info.UseSshTunnel ? "127.0.0.1" : defaultHost,
                    Port = info.UseSshTunnel && _localTunnelPort != 0 ? _localTunnelPort : defaultPort,
                    UserID = string.IsNullOrWhiteSpace(info.Username) ? "root" : info.Username,
                    Password = info.Password ?? string.Empty,
                    CharacterSet = "utf8mb4",
                    AllowUserVariables = true,
                    AllowZeroDateTime = true,
                    ConvertZeroDateTime = true,
                    ConnectionTimeout = 15,
                    DefaultCommandTimeout = 60
                };

                if (!string.IsNullOrWhiteSpace(info.DatabaseName))
                {
                    builder.Database = info.DatabaseName;
                }

                _mysqlConnection = new MySqlConnection(builder.ConnectionString);
                await _mysqlConnection.OpenAsync();

                _currentType = info.DbType;
                _activeDatabase = string.IsNullOrWhiteSpace(info.DatabaseName) ? null : info.DatabaseName;
            }
            catch
            {
                await DisconnectAsync();
                throw;
            }
        }

        public async Task DisconnectAsync()
        {
            if (_mysqlConnection != null)
            {
                try
                {
                    await _mysqlConnection.CloseAsync();
                    await _mysqlConnection.DisposeAsync();
                }
                catch
                {
                    _mysqlConnection.Dispose();
                }
                finally
                {
                    _mysqlConnection = null;
                    _currentType = DatabaseType.None;
                    _activeDatabase = null;
                }
            }

            TearDownTunnel();
        }

        public async Task<IReadOnlyList<string>> GetDatabasesAsync()
        {
            EnsureMySqlConnection();

            var databases = new List<string>();
            await using var cmd = _mysqlConnection!.CreateCommand();
            cmd.CommandText = "SHOW DATABASES";

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                databases.Add(name);
            }

            databases.Sort(StringComparer.OrdinalIgnoreCase);
            return databases;
        }

        public async Task<IReadOnlyList<string>> GetTablesAsync(string database)
        {
            EnsureMySqlConnection();
            await SetActiveDatabaseAsync(database);

            var tables = new List<string>();
            await using var cmd = _mysqlConnection!.CreateCommand();
            cmd.CommandText = "SHOW FULL TABLES";

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }

            return tables;
        }

        public async Task<DatabaseQueryResult> GetTablePreviewAsync(string database, string tableName, int limit = 200)
        {
            EnsureMySqlConnection();
            if (string.IsNullOrWhiteSpace(tableName)) throw new ArgumentException("Table name is required.", nameof(tableName));

            await SetActiveDatabaseAsync(database);

            var sanitizedTable = EscapeIdentifier(tableName);
            await using var cmd = _mysqlConnection!.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {sanitizedTable} LIMIT {(limit <= 0 ? 200 : limit)}";

            var schemaTable = new DataTable();
            await using var reader = await cmd.ExecuteReaderAsync();
            schemaTable.Load(reader);

            return new DatabaseQueryResult
            {
                HasResultSet = true,
                Table = schemaTable,
                RowsAffected = schemaTable.Rows.Count,
                Message = $"Showing first {schemaTable.Rows.Count} rows from {tableName}"
            };
        }

        public async Task<DatabaseQueryResult> ExecuteQueryAsync(string sql, string? database = null, int? commandTimeoutSeconds = null)
        {
            EnsureMySqlConnection();

            if (string.IsNullOrWhiteSpace(sql))
            {
                throw new ArgumentException("SQL query cannot be empty.", nameof(sql));
            }

            await SetActiveDatabaseAsync(database);

            var trimmed = sql.TrimStart();
            bool expectsResult = StartsWithAny(trimmed, "select", "show", "describe", "explain", "with");

            await using var cmd = _mysqlConnection!.CreateCommand();
            cmd.CommandText = sql;
            if (commandTimeoutSeconds.HasValue && commandTimeoutSeconds.Value > 0)
            {
                cmd.CommandTimeout = commandTimeoutSeconds.Value;
            }

            if (expectsResult)
            {
                var table = new DataTable();
                await using var reader = await cmd.ExecuteReaderAsync();
                table.Load(reader);

                return new DatabaseQueryResult
                {
                    HasResultSet = true,
                    Table = table,
                    RowsAffected = table.Rows.Count,
                    Message = $"Returned {table.Rows.Count} rows."
                };
            }
            else
            {
                int affected = await cmd.ExecuteNonQueryAsync();
                return new DatabaseQueryResult
                {
                    HasResultSet = false,
                    RowsAffected = affected,
                    Message = $"Query executed successfully. Rows affected: {affected}."
                };
            }
        }

        public async Task SetActiveDatabaseAsync(string? database)
        {
            if (_mysqlConnection == null || string.IsNullOrWhiteSpace(database))
            {
                return;
            }

            if (string.Equals(_activeDatabase, database, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Run(() => _mysqlConnection.ChangeDatabase(database));
            _activeDatabase = database;
        }

        internal void EnsureMySqlConnection()
        {
            if (_mysqlConnection == null || _mysqlConnection.State != ConnectionState.Open)
            {
                throw new InvalidOperationException("Database connection is not open.");
            }
        }

        internal MySqlConnection GetOpenConnection()
        {
            EnsureMySqlConnection();
            return _mysqlConnection!;
        }

        private static string GetSafeString(MySqlDataReader reader, string column)
        {
            var ordinal = reader.GetOrdinal(column);
            if (reader.IsDBNull(ordinal))
            {
                return string.Empty;
            }

            return reader.GetString(ordinal);
        }

        internal static string EscapeIdentifier(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                return identifier;
            }

            return $"`{identifier.Replace("`", "``")}`";
        }

        private static string SanitizeOptionValue(string? value, string fallback, string optionName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            var trimmed = value.Trim();
            if (!IsValidOptionValue(trimmed))
            {
                throw new ArgumentException($"Invalid {optionName} value.", optionName);
            }

            return trimmed;
        }

        private static bool IsValidOptionValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            foreach (var ch in value)
            {
                if (!(char.IsLetterOrDigit(ch) || ch == '_'))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool StartsWithAny(string value, params string[] tokens)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            foreach (var token in tokens)
            {
                if (value.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        public async Task ExecuteNonQueryAsync(string sql, string? database = null, int commandTimeoutSeconds = 60, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sql))
            {
                return;
            }

            EnsureMySqlConnection();
            await SetActiveDatabaseAsync(database ?? _activeDatabase);

            await using var cmd = _mysqlConnection!.CreateCommand();
            cmd.CommandText = sql;
            if (commandTimeoutSeconds > 0)
            {
                cmd.CommandTimeout = commandTimeoutSeconds;
            }

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task DropAndCreateDatabaseAsync(string databaseName, string? charset = null, string? collation = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                throw new ArgumentException("Database name is required.", nameof(databaseName));
            }

            EnsureMySqlConnection();
            await SetActiveDatabaseAsync("information_schema");

            var escaped = EscapeIdentifier(databaseName);
            await ExecuteNonQueryAsync($"DROP DATABASE IF EXISTS {escaped};", null, 0, cancellationToken);

            var sanitizedCharset = SanitizeOptionValue(charset, "utf8mb4", nameof(charset));
            var sanitizedCollation = SanitizeOptionValue(collation, "utf8mb4_unicode_ci", nameof(collation));
            await ExecuteNonQueryAsync($"CREATE DATABASE {escaped} CHARACTER SET {sanitizedCharset} COLLATE {sanitizedCollation};", null, 0, cancellationToken);

            await SetActiveDatabaseAsync(databaseName);
        }

        public async Task EnsureDatabaseExistsAsync(string databaseName, string? charset = null, string? collation = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                throw new ArgumentException("Database name is required.", nameof(databaseName));
            }

            EnsureMySqlConnection();
            var escaped = EscapeIdentifier(databaseName);
            var sanitizedCharset = SanitizeOptionValue(charset, "utf8mb4", nameof(charset));
            var sanitizedCollation = SanitizeOptionValue(collation, "utf8mb4_unicode_ci", nameof(collation));
            await ExecuteNonQueryAsync($"CREATE DATABASE IF NOT EXISTS {escaped} CHARACTER SET {sanitizedCharset} COLLATE {sanitizedCollation};", null, 0, cancellationToken);
        }

        public async Task CreateDatabaseAsync(string databaseName,
                                              string? charset = null,
                                              string? collation = null,
                                              bool switchToDatabase = false,
                                              CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                throw new ArgumentException("Database name is required.", nameof(databaseName));
            }

            EnsureMySqlConnection();
            await SetActiveDatabaseAsync("information_schema");

            var escaped = EscapeIdentifier(databaseName);
            var sanitizedCharset = SanitizeOptionValue(charset, "utf8mb4", nameof(charset));
            var sanitizedCollation = SanitizeOptionValue(collation, "utf8mb4_unicode_ci", nameof(collation));

            await ExecuteNonQueryAsync($"CREATE DATABASE {escaped} CHARACTER SET {sanitizedCharset} COLLATE {sanitizedCollation};", null, 0, cancellationToken);

            if (switchToDatabase)
            {
                await SetActiveDatabaseAsync(databaseName);
            }
        }

        public async Task<IReadOnlyList<DatabaseCharsetInfo>> GetCharacterSetsAsync(CancellationToken cancellationToken = default)
        {
            EnsureMySqlConnection();
            var list = new List<DatabaseCharsetInfo>();
            await using var cmd = _mysqlConnection!.CreateCommand();
            cmd.CommandText = @"
SELECT
    CHARACTER_SET_NAME AS Charset,
    DEFAULT_COLLATE_NAME AS DefaultCollation,
    DESCRIPTION AS Description
FROM information_schema.CHARACTER_SETS";

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString("Charset");
                var defaultCollation = reader.GetString("DefaultCollation");
                var description = reader.GetString("Description");
                list.Add(DatabaseCharsetInfo.Create(name, defaultCollation, description));
            }

            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        public async Task<IReadOnlyList<string>> GetCollationsAsync(string charset, CancellationToken cancellationToken = default)
        {
            var sanitizedCharset = SanitizeOptionValue(charset, "utf8mb4", nameof(charset));
            EnsureMySqlConnection();

            var collations = new List<string>();
            await using var cmd = _mysqlConnection!.CreateCommand();
            cmd.CommandText = @"
SELECT COLLATION_NAME AS Collation
FROM information_schema.COLLATIONS
WHERE CHARACTER_SET_NAME = @charset";
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = "@charset";
            parameter.Value = sanitizedCharset;
            cmd.Parameters.Add(parameter);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                collations.Add(reader.GetString("Collation"));
            }

            collations.Sort(StringComparer.OrdinalIgnoreCase);
            return collations;
        }

        public async Task<(bool Found, string Charset, string Collation)> GetDatabaseDefaultsAsync(string databaseName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                throw new ArgumentException("Database name is required.", nameof(databaseName));
            }

            EnsureMySqlConnection();
            await using var cmd = _mysqlConnection!.CreateCommand();
            cmd.CommandText = @"
SELECT
    DEFAULT_CHARACTER_SET_NAME AS Charset,
    DEFAULT_COLLATION_NAME AS Collation
FROM information_schema.SCHEMATA
WHERE SCHEMA_NAME = @schema
LIMIT 1";
            var param = cmd.CreateParameter();
            param.ParameterName = "@schema";
            param.Value = databaseName;
            cmd.Parameters.Add(param);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return (true, reader.GetString("Charset"), reader.GetString("Collation"));
            }

            return (false, "utf8mb4", "utf8mb4_unicode_ci");
        }

        public async Task<bool> DatabaseExistsAsync(string databaseName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                return false;
            }

            EnsureMySqlConnection();
            await using var cmd = _mysqlConnection!.CreateCommand();
            cmd.CommandText = @"
SELECT 1
FROM information_schema.SCHEMATA
WHERE SCHEMA_NAME = @schema
LIMIT 1";
            var param = cmd.CreateParameter();
            param.ParameterName = "@schema";
            param.Value = databaseName;
            cmd.Parameters.Add(param);

            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result != null && result != DBNull.Value;
        }

        public async Task<IReadOnlyList<DatabaseProcessInfo>> GetProcessListAsync(CancellationToken cancellationToken = default)
        {
            EnsureMySqlConnection();

            try
            {
                return await ExecuteProcessListAsync("SHOW FULL PROCESSLIST", cancellationToken);
            }
            catch (MySqlException ex) when (IsProcessPrivilegeError(ex))
            {
                // Fallback for accounts without PROCESS privilege; SHOW PROCESSLIST still returns own sessions.
                return await ExecuteProcessListAsync("SHOW PROCESSLIST", cancellationToken);
            }
        }

        private async Task<IReadOnlyList<DatabaseProcessInfo>> ExecuteProcessListAsync(string sql, CancellationToken cancellationToken)
        {
            var processes = new List<DatabaseProcessInfo>();

            await using var cmd = _mysqlConnection!.CreateCommand();
            cmd.CommandText = sql;

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var process = new DatabaseProcessInfo
                {
                    Id = reader.GetInt64("Id"),
                    User = GetSafeString(reader, "User"),
                    Host = GetSafeString(reader, "Host"),
                    Database = GetSafeString(reader, "db"),
                    Command = GetSafeString(reader, "Command"),
                    TimeSeconds = reader.IsDBNull(reader.GetOrdinal("Time")) ? 0 : reader.GetInt32("Time"),
                    State = GetSafeString(reader, "State"),
                    Info = GetSafeString(reader, "Info")
                };

                processes.Add(process);
            }

            return processes;
        }

        private static bool IsProcessPrivilegeError(MySqlException ex)
        {
            // 1227 = ER_SPECIFIC_ACCESS_DENIED_ERROR, 1142 = ER_TABLEACCESS_DENIED_ERROR, 1044 = ER_DBACCESS_DENIED_ERROR
            return ex.Number == 1227 || ex.Number == 1142 || ex.Number == 1044;
        }

        public async Task ImportSqlAsync(string sqlFilePath,
                                         string targetDatabase,
                                         IProgress<ImportProgressUpdate>? progress,
                                         CancellationToken cancellationToken,
                                         bool fastMode,
                                         int commandTimeoutSeconds = 60,
                                         bool continueOnError = false,
                                         Action<string>? errorLogger = null)
        {
            if (string.IsNullOrWhiteSpace(sqlFilePath))
            {
                throw new ArgumentException("SQL file path required.", nameof(sqlFilePath));
            }

            if (!File.Exists(sqlFilePath))
            {
                throw new FileNotFoundException("SQL file not found.", sqlFilePath);
            }

            if (string.IsNullOrWhiteSpace(targetDatabase))
            {
                throw new ArgumentException("Target database required.", nameof(targetDatabase));
            }

            EnsureMySqlConnection();
            await SetActiveDatabaseAsync(targetDatabase);

            if (fastMode)
            {
                await ExecuteNonQueryAsync("SET unique_checks=0;", targetDatabase, commandTimeoutSeconds, cancellationToken);
                await ExecuteNonQueryAsync("SET autocommit=0;", targetDatabase, commandTimeoutSeconds, cancellationToken);
                await ExecuteNonQueryAsync("START TRANSACTION;", targetDatabase, commandTimeoutSeconds, cancellationToken);
            }

            try
            {
                await StreamImportAsync(sqlFilePath, progress, cancellationToken, commandTimeoutSeconds, continueOnError, errorLogger);

                if (fastMode)
                {
                    await ExecuteNonQueryAsync("COMMIT;", targetDatabase, commandTimeoutSeconds, cancellationToken);
                }
            }
            finally
            {
                if (fastMode)
                {
                    try
                    {
                        await ExecuteNonQueryAsync("SET autocommit=1;", targetDatabase, commandTimeoutSeconds);
                        await ExecuteNonQueryAsync("SET unique_checks=1;", targetDatabase, commandTimeoutSeconds);
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
        }

        private async Task StreamImportAsync(string sqlFilePath,
                                             IProgress<ImportProgressUpdate>? progress,
                                             CancellationToken cancellationToken,
                                             int commandTimeoutSeconds,
                                             bool continueOnError,
                                             Action<string>? errorLogger)
        {
            var delimiter = ";";
            var builder = new StringBuilder();
            long totalBytes = new FileInfo(sqlFilePath).Length;
            int statements = 0;

            await using var stream = File.OpenRead(sqlFilePath);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var trimmed = line.Trim();
                if (trimmed.StartsWith("DELIMITER ", StringComparison.OrdinalIgnoreCase))
                {
                    delimiter = trimmed.Substring(10).Trim();
                    builder.Clear();
                    continue;
                }

                builder.AppendLine(line);

                if (StatementReady(builder, delimiter))
                {
                    var statement = TrimDelimiter(builder, delimiter);
                    builder.Clear();

                    if (!string.IsNullOrWhiteSpace(statement))
                    {
                        await ExecuteStatementAsync(statement, commandTimeoutSeconds, cancellationToken, continueOnError, errorLogger);
                        statements++;
                        progress?.Report(new ImportProgressUpdate
                        {
                            BytesProcessed = stream.Position,
                            TotalBytes = totalBytes,
                            StatementsExecuted = statements,
                            Message = $"Executed {statements:N0} statements"
                        });
                    }
                }
                else
                {
                    progress?.Report(new ImportProgressUpdate
                    {
                        BytesProcessed = stream.Position,
                        TotalBytes = totalBytes,
                        StatementsExecuted = statements
                    });
                }
            }

            var leftover = builder.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(leftover))
            {
                await ExecuteStatementAsync(leftover, commandTimeoutSeconds, cancellationToken, continueOnError, errorLogger);
                statements++;
                progress?.Report(new ImportProgressUpdate
                {
                    BytesProcessed = totalBytes,
                    TotalBytes = totalBytes,
                    StatementsExecuted = statements,
                    Message = $"Executed {statements:N0} statements"
                });
            }
        }

        private async Task ExecuteStatementAsync(string sql,
                                                int commandTimeoutSeconds,
                                                CancellationToken cancellationToken,
                                                bool continueOnError,
                                                Action<string>? errorLogger)
        {
            await using var cmd = _mysqlConnection!.CreateCommand();
            cmd.CommandText = NormalizeImportStatement(sql);
            if (commandTimeoutSeconds > 0)
            {
                cmd.CommandTimeout = commandTimeoutSeconds;
            }

            try
            {
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                if (!continueOnError)
                {
                    throw;
                }

                var preview = sql.Length > 200 ? sql[..200] + "..." : sql;
                errorLogger?.Invoke($"Statement skipped: {ex.Message} (snippet: {preview})");
            }
        }

        private static string NormalizeImportStatement(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
            {
                return sql;
            }

            var trimmed = sql.Trim();
            if (!trimmed.StartsWith("SET", StringComparison.OrdinalIgnoreCase) ||
                !trimmed.Contains("sql_mode", StringComparison.OrdinalIgnoreCase))
            {
                return sql;
            }

            var lower = trimmed.ToLowerInvariant();
            var startIndex = lower.IndexOf("set", StringComparison.Ordinal);
            if (startIndex < 0) return sql;

            var equalsIndex = lower.IndexOf("=", StringComparison.Ordinal);
            if (equalsIndex < 0) return sql;

            var valuePart = trimmed[(equalsIndex + 1)..].Trim().TrimEnd(';');
            valuePart = valuePart.Trim('\'', '"');

            var filteredModes = valuePart
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(mode => !string.Equals(mode, "NO_AUTO_CREATE_USER", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (filteredModes.Length == 0)
            {
                return "-- filtered sql_mode due to incompatible values";
            }

            var rebuilt = string.Join(",", filteredModes);
            return $"SET sql_mode='{rebuilt}';";
        }

        private static bool StatementReady(StringBuilder builder, string delimiter)
        {
            if (builder.Length == 0) return false;

            var text = builder.ToString().TrimEnd();
            if (string.IsNullOrWhiteSpace(delimiter))
            {
                delimiter = ";";
            }

            return text.EndsWith(delimiter, StringComparison.Ordinal);
        }

        private static string TrimDelimiter(StringBuilder builder, string delimiter)
        {
            var text = builder.ToString().TrimEnd();
            if (string.IsNullOrWhiteSpace(delimiter))
            {
                delimiter = ";";
            }

            if (text.EndsWith(delimiter, StringComparison.Ordinal))
            {
                text = text[..^delimiter.Length];
            }

            return text.Trim();
        }

        public void Dispose()
        {
            _mysqlConnection?.Dispose();
            _mysqlConnection = null;
            TearDownTunnel();
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync();
        }

        private async Task SetupSshTunnelAsync(DatabaseConnectionInfo info)
        {
            if (string.IsNullOrWhiteSpace(info.SshHost))
            {
                throw new InvalidOperationException("SSH host is required to create a tunnel.");
            }

            if (string.IsNullOrWhiteSpace(info.SshUsername))
            {
                throw new InvalidOperationException("SSH username is required to create a tunnel.");
            }

            await Task.Run(() =>
            {
                var sshPort = info.SshPort <= 0 ? 22 : info.SshPort;
                var authMethods = new List<AuthenticationMethod>();

                if (!string.IsNullOrWhiteSpace(info.SshPrivateKeyPath) && File.Exists(info.SshPrivateKeyPath))
                {
                    var keyFile = new PrivateKeyFile(info.SshPrivateKeyPath);
                    authMethods.Add(new PrivateKeyAuthenticationMethod(info.SshUsername, keyFile));
                }

                if (!string.IsNullOrEmpty(info.SshPassword))
                {
                    authMethods.Add(new PasswordAuthenticationMethod(info.SshUsername, info.SshPassword));
                }

                if (authMethods.Count == 0)
                {
                    throw new InvalidOperationException("Provide an SSH password or private key to open the tunnel.");
                }

                var connectionInfo = new ConnectionInfo(info.SshHost, sshPort, info.SshUsername, authMethods.ToArray());
                _sshClient = new SshClient(connectionInfo);
                _sshClient.Connect();

                _localTunnelPort = GetAvailablePort();
                var remotePort = (uint)(info.Port <= 0 ? 3306 : info.Port);
                var remoteHost = string.IsNullOrWhiteSpace(info.Host) ? "127.0.0.1" : info.Host;

                _forwardedPort = new ForwardedPortLocal("127.0.0.1", _localTunnelPort, remoteHost, remotePort);
                _sshClient.AddForwardedPort(_forwardedPort);
                _forwardedPort.Start();
            });
        }

        private static uint GetAvailablePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return (uint)port;
        }

        private void TearDownTunnel()
        {
            _localTunnelPort = 0;

            if (_forwardedPort != null)
            {
                try
                {
                    if (_forwardedPort.IsStarted)
                    {
                        _forwardedPort.Stop();
                    }
                }
                catch
                {
                    // ignore cleanup errors
                }
                finally
                {
                    _forwardedPort.Dispose();
                    _forwardedPort = null;
                }
            }

            if (_sshClient != null)
            {
                try
                {
                    if (_sshClient.IsConnected)
                    {
                        _sshClient.Disconnect();
                    }
                }
                finally
                {
                    _sshClient.Dispose();
                    _sshClient = null;
                }
            }
        }
    }
}

