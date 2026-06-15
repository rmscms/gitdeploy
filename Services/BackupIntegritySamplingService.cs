using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Models;

namespace GitDeployPro.Services
{
    public sealed class BackupIntegritySamplingService
    {
        private const int DefaultTopTables = 10;
        private const int MaxCellValueLength = 300;

        public async Task<BackupIntegritySamplingSnapshot> CaptureAsync(
            DatabaseClient client,
            string databaseName,
            int topTables = DefaultTopTables,
            CancellationToken cancellationToken = default,
            bool includeLatestRows = true)
        {
            if (client == null)
            {
                throw new ArgumentNullException(nameof(client));
            }

            var normalizedDb = (databaseName ?? string.Empty).Trim();
            var snapshot = new BackupIntegritySamplingSnapshot
            {
                DatabaseName = normalizedDb,
                CapturedUtc = AppTimeService.UtcNow
            };

            if (string.IsNullOrWhiteSpace(normalizedDb))
            {
                snapshot.Message = "Sampling skipped: validation database name is empty.";
                return snapshot;
            }

            var maxTables = topTables <= 0 ? 0 : topTables;
            var connection = client.GetOpenConnection();
            var tableInfos = await GetTablesAsync(connection, normalizedDb, maxTables, cancellationToken);
            if (tableInfos.Count == 0)
            {
                snapshot.Message = "No base tables were found for integrity sampling.";
                return snapshot;
            }

            var rank = 0;
            foreach (var tableInfo in tableInfos)
            {
                rank++;
                var sample = new BackupIntegrityTableSample
                {
                    Rank = rank,
                    TableName = tableInfo.TableName,
                    ApproxRowCount = tableInfo.ApproxRowCount,
                    DataBytes = tableInfo.DataBytes,
                    IndexBytes = tableInfo.IndexBytes,
                    TotalBytes = tableInfo.TotalBytes
                };

                if (!includeLatestRows)
                {
                    sample.LastRowStatus = "Click this table to load latest row.";
                    snapshot.Tables.Add(sample);
                    continue;
                }

                try
                {
                    await LoadLatestRowAsync(client, normalizedDb, sample, cancellationToken);
                }
                catch (Exception ex)
                {
                    sample.LastRowStatus = $"Latest row sampling failed: {ex.Message}";
                }

                snapshot.Tables.Add(sample);
            }

            snapshot.Message = includeLatestRows
                ? $"{snapshot.Tables.Count} largest table sample(s) captured."
                : $"{snapshot.Tables.Count} table(s) loaded. Click a table to load its latest row.";
            return snapshot;
        }

        public async Task LoadLatestRowAsync(
            DatabaseClient client,
            string databaseName,
            BackupIntegrityTableSample sample,
            CancellationToken cancellationToken = default)
        {
            if (client == null)
            {
                throw new ArgumentNullException(nameof(client));
            }

            if (sample == null)
            {
                throw new ArgumentNullException(nameof(sample));
            }

            var normalizedDb = (databaseName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedDb))
            {
                sample.LastRowStatus = "Latest row sampling skipped: database name is empty.";
                return;
            }

            sample.LastRowValues.Clear();
            var connection = client.GetOpenConnection();
            var primaryKeys = await GetPrimaryKeyColumnsAsync(connection, normalizedDb, sample.TableName, cancellationToken);
            sample.PrimaryKeySummary = primaryKeys.Count == 0
                ? "No primary key"
                : string.Join(", ", primaryKeys);

            if (primaryKeys.Count == 0)
            {
                sample.LastRowStatus = "Latest row sampling skipped: table has no primary key.";
                return;
            }

            await FillLastRowAsync(connection, normalizedDb, sample, primaryKeys, cancellationToken);
        }

        private static async Task<List<TableInfo>> GetTablesAsync(
            MySqlConnector.MySqlConnection connection,
            string databaseName,
            int take,
            CancellationToken cancellationToken)
        {
            var items = new List<TableInfo>();
            await using var cmd = connection.CreateCommand();
            var query = @"
SELECT
    TABLE_NAME,
    COALESCE(TABLE_ROWS, 0) AS ApproxRows,
    COALESCE(DATA_LENGTH, 0) AS DataBytes,
    COALESCE(INDEX_LENGTH, 0) AS IndexBytes
FROM information_schema.TABLES
WHERE TABLE_SCHEMA = @schema
  AND TABLE_TYPE = 'BASE TABLE'
ORDER BY (COALESCE(DATA_LENGTH, 0) + COALESCE(INDEX_LENGTH, 0)) DESC, TABLE_NAME ASC";
            if (take > 0)
            {
                query += "\nLIMIT @take";
            }

            cmd.CommandText = query;

            var schema = cmd.CreateParameter();
            schema.ParameterName = "@schema";
            schema.Value = databaseName;
            cmd.Parameters.Add(schema);

            if (take > 0)
            {
                var takeParam = cmd.CreateParameter();
                takeParam.ParameterName = "@take";
                takeParam.Value = take;
                cmd.Parameters.Add(takeParam);
            }

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var tableName = reader.GetString("TABLE_NAME");
                var approxRows = Convert.ToInt64(reader["ApproxRows"], CultureInfo.InvariantCulture);
                var dataBytes = Convert.ToInt64(reader["DataBytes"], CultureInfo.InvariantCulture);
                var indexBytes = Convert.ToInt64(reader["IndexBytes"], CultureInfo.InvariantCulture);
                items.Add(new TableInfo(
                    tableName,
                    approxRows,
                    dataBytes,
                    indexBytes,
                    dataBytes + indexBytes));
            }

            return items;
        }

        private static async Task<List<string>> GetPrimaryKeyColumnsAsync(
            MySqlConnector.MySqlConnection connection,
            string databaseName,
            string tableName,
            CancellationToken cancellationToken)
        {
            var columns = new List<string>();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT COLUMN_NAME
FROM information_schema.KEY_COLUMN_USAGE
WHERE TABLE_SCHEMA = @schema
  AND TABLE_NAME = @table
  AND CONSTRAINT_NAME = 'PRIMARY'
ORDER BY ORDINAL_POSITION;";

            var schema = cmd.CreateParameter();
            schema.ParameterName = "@schema";
            schema.Value = databaseName;
            cmd.Parameters.Add(schema);

            var table = cmd.CreateParameter();
            table.ParameterName = "@table";
            table.Value = tableName;
            cmd.Parameters.Add(table);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString("COLUMN_NAME"));
            }

            return columns;
        }

        private static async Task FillLastRowAsync(
            MySqlConnector.MySqlConnection connection,
            string databaseName,
            BackupIntegrityTableSample sample,
            IReadOnlyList<string> primaryKeys,
            CancellationToken cancellationToken)
        {
            var orderBy = string.Join(", ", primaryKeys.Select(pk => $"{DatabaseClient.EscapeIdentifier(pk)} DESC"));
            var escapedDatabase = DatabaseClient.EscapeIdentifier(databaseName);
            var escapedTable = DatabaseClient.EscapeIdentifier(sample.TableName);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {escapedDatabase}.{escapedTable} ORDER BY {orderBy} LIMIT 1;";
            cmd.CommandTimeout = 30;

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                sample.LastRowStatus = "Latest row not found: table is empty.";
                return;
            }

            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                sample.LastRowValues.Add(new BackupIntegrityCellValue
                {
                    ColumnName = name,
                    DisplayValue = FormatCellValue(value)
                });
            }

            sample.LastRowStatus = "Latest row loaded using PRIMARY KEY order.";
        }

        private static string FormatCellValue(object? value)
        {
            if (value == null || value == DBNull.Value)
            {
                return "NULL";
            }

            return value switch
            {
                byte[] buffer => $"<binary {buffer.Length} bytes>",
                DateTime dateTime => AppTimeService.FormatLocal(dateTime, "yyyy-MM-dd HH:mm:ss"),
                DateTimeOffset offset => offset.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
                _ => TrimLongValue(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
            };
        }

        private static string TrimLongValue(string value)
        {
            if (value.Length <= MaxCellValueLength)
            {
                return value;
            }

            return $"{value[..MaxCellValueLength]}…";
        }

        private sealed record TableInfo(
            string TableName,
            long ApproxRowCount,
            long DataBytes,
            long IndexBytes,
            long TotalBytes);
    }
}
