using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Net;
using System.Net.Sockets;
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

        public async Task<DatabaseQueryResult> ExecuteQueryAsync(string sql, string? database = null)
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

        private void EnsureMySqlConnection()
        {
            if (_mysqlConnection == null || _mysqlConnection.State != ConnectionState.Open)
            {
                throw new InvalidOperationException("Database connection is not open.");
            }
        }

        private static string EscapeIdentifier(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                return identifier;
            }

            return $"`{identifier.Replace("`", "``")}`";
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

