using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using GitDeployPro.Models;
using MySqlConnector;

namespace GitDeployPro.Services
{
    public class DatabaseClient : IDisposable, IAsyncDisposable
    {
        private MySqlConnection? _mysqlConnection;
        private DatabaseType _currentType = DatabaseType.None;
        private string? _activeDatabase;

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

            var builder = new MySqlConnectionStringBuilder
            {
                Server = string.IsNullOrWhiteSpace(info.Host) ? "127.0.0.1" : info.Host,
                Port = (uint)(info.Port <= 0 ? 3306 : info.Port),
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
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync();
        }
    }
}

