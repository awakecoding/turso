using System.Data;

namespace Turso.Data.Sqlite;

public partial class SqliteConnection
{
    private DataTable GetIndexesSchema(string collectionName, string?[]? restrictionValues)
    {
        EnsureOpen();
        ValidateRestrictions(collectionName, restrictionValues, 4);
        var table = CreateIndexesSchemaTable();
        var tableNameRestriction = GetRestriction(restrictionValues, 2);
        var indexNameRestriction = GetRestriction(restrictionValues, 3);

        foreach (var tableName in GetUserTableNames())
        {
            if (tableNameRestriction is not null
                && !string.Equals(tableName, tableNameRestriction, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var command = CreateCommand();
            command.CommandText = $"PRAGMA index_list({QuoteIdentifier(tableName)});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var indexName = reader.GetString(1);
                if (indexNameRestriction is not null
                    && !string.Equals(indexName, indexNameRestriction, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                table.Rows.Add(
                    "main",
                    DBNull.Value,
                    tableName,
                    indexName,
                    reader.GetInt64(2) != 0,
                    reader.GetString(3),
                    reader.GetInt64(4) != 0);
            }
        }

        return table;
    }

    private DataTable GetIndexColumnsSchema(string collectionName, string?[]? restrictionValues)
    {
        EnsureOpen();
        ValidateRestrictions(collectionName, restrictionValues, 5);
        var table = new DataTable("IndexColumns");
        table.Columns.Add("TABLE_CATALOG", typeof(string));
        table.Columns.Add("TABLE_SCHEMA", typeof(string));
        table.Columns.Add("TABLE_NAME", typeof(string));
        table.Columns.Add("INDEX_NAME", typeof(string));
        table.Columns.Add("ORDINAL_POSITION", typeof(int));
        table.Columns.Add("COLUMN_ORDINAL", typeof(int));
        table.Columns.Add("COLUMN_NAME", typeof(string));

        var tableNameRestriction = GetRestriction(restrictionValues, 2);
        var indexNameRestriction = GetRestriction(restrictionValues, 3);
        var columnNameRestriction = GetRestriction(restrictionValues, 4);

        foreach (var tableName in GetUserTableNames())
        {
            if (tableNameRestriction is not null
                && !string.Equals(tableName, tableNameRestriction, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var indexes = CreateCommand();
            indexes.CommandText = $"PRAGMA index_list({QuoteIdentifier(tableName)});";
            using var indexReader = indexes.ExecuteReader();
            while (indexReader.Read())
            {
                var indexName = indexReader.GetString(1);
                if (indexNameRestriction is not null
                    && !string.Equals(indexName, indexNameRestriction, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var columns = CreateCommand();
                columns.CommandText = $"PRAGMA index_info({QuoteIdentifier(indexName)});";
                using var columnReader = columns.ExecuteReader();
                while (columnReader.Read())
                {
                    var columnName = columnReader.GetString(2);
                    if (columnNameRestriction is not null
                        && !string.Equals(columnName, columnNameRestriction, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    table.Rows.Add(
                        "main",
                        DBNull.Value,
                        tableName,
                        indexName,
                        columnReader.GetInt32(0),
                        columnReader.GetInt32(1),
                        columnName);
                }
            }
        }

        return table;
    }

    private static DataTable CreateIndexesSchemaTable()
    {
        var table = new DataTable("Indexes");
        table.Columns.Add("TABLE_CATALOG", typeof(string));
        table.Columns.Add("TABLE_SCHEMA", typeof(string));
        table.Columns.Add("TABLE_NAME", typeof(string));
        table.Columns.Add("INDEX_NAME", typeof(string));
        table.Columns.Add("IS_UNIQUE", typeof(bool));
        table.Columns.Add("ORIGIN", typeof(string));
        table.Columns.Add("IS_PARTIAL", typeof(bool));
        return table;
    }
}
