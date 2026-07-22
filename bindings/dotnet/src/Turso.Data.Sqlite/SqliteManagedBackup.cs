namespace Turso.Data.Sqlite;

internal static class SqliteManagedBackup
{
    private static readonly string[] RowidNames = ["rowid", "_rowid_", "oid"];

    internal static void Copy(SqliteConnection source, SqliteConnection destination, string destinationName, string sourceName)
    {
        if (!source.DatabaseHandle.IsManaged || !destination.DatabaseHandle.IsManaged)
            throw new InvalidOperationException("Managed backup requires managed source and destination connections.");
        ArgumentNullException.ThrowIfNull(destinationName);
        ArgumentNullException.ThrowIfNull(sourceName);
        if (!string.Equals(sourceName, "main", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(destinationName, "main", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(Properties.Resources.ManagedBackupAttachedDatabasesNotSupported);
        }
        if (ReferenceEquals(source, destination))
            throw new ArgumentException(Properties.Resources.ManagedBackupSameConnectionNotSupported, nameof(destination));
        if (source.Transaction is not null || destination.Transaction is not null
            || source.HasOpenReader || destination.HasOpenReader)
        {
            throw new SqliteException(Properties.Resources.SqliteNativeError(5, "database is locked"), 5);
        }

        EnsureEmpty(destination);

        BeginSnapshot(source);
        try
        {
            var schema = ReadSchema(source);
            using var destinationTransaction = destination.BeginTransaction();
            foreach (var entry in schema.Where(entry => entry.Type == "table"))
                Execute(destination, entry.Sql);

            foreach (var table in schema.Where(entry => entry.Type == "table"))
                CopyRows(source, destination, table);

            foreach (var entry in schema.Where(entry => entry.Type is "index" or "view" or "trigger"))
                Execute(destination, entry.Sql);

            destinationTransaction.Commit();
        }
        finally
        {
            RollbackSnapshot(source);
        }
    }

    private static void EnsureEmpty(SqliteConnection destination)
    {
        using var command = destination.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE name NOT LIKE 'sqlite_%';";
        using var reader = command.ExecuteReader();
        if (reader.Read())
            throw new InvalidOperationException(Properties.Resources.ManagedBackupDestinationMustBeEmpty);
    }

    private static List<SchemaEntry> ReadSchema(SqliteConnection source)
    {
        using var command = source.CreateCommand();
        command.CommandText = "SELECT type, name, sql FROM sqlite_master WHERE sql IS NOT NULL;";
        using var reader = command.ExecuteReader();
        var schema = new List<SchemaEntry>();
        while (reader.Read())
        {
            var type = reader.GetString(0);
            if (type is not ("table" or "index" or "view" or "trigger"))
                throw new NotSupportedException(Properties.Resources.ManagedBackupSchemaObjectNotSupported(type));

            var name = reader.GetString(1);
            var sql = reader.GetString(2);
            schema.Add(new SchemaEntry(type, name, sql, HasWithoutRowidClause(sql)));
        }

        return schema;
    }

    private static void CopyRows(SqliteConnection source, SqliteConnection destination, SchemaEntry table)
    {
        var tableName = table.Name;
        var columnNames = ReadColumnNames(source, tableName);
        var selectColumnNames = columnNames.ToArray();
        if (!table.IsWithoutRowid)
        {
            var rowidName = GetRowidName(columnNames);
            if (rowidName is null)
                throw new NotSupportedException(Properties.Resources.ManagedBackupRowidNotAccessible(tableName));

            selectColumnNames = new[] { rowidName }.Concat(selectColumnNames).ToArray();
        }

        using var select = source.CreateCommand();
        select.CommandText = "SELECT " + string.Join(", ", selectColumnNames.Select(QuoteIdentifier))
                          + " FROM " + QuoteIdentifier(tableName) + ";";
        using var reader = select.ExecuteReader();

        var insertColumnNames = selectColumnNames;
        var parameterNames = Enumerable.Range(0, insertColumnNames.Length).Select(index => "$p" + index).ToArray();
        while (reader.Read())
        {
            if (reader.FieldCount != insertColumnNames.Length)
                throw new InvalidOperationException(Properties.Resources.ManagedBackupColumnCountMismatch(tableName));

            using var insert = destination.CreateCommand();
            insert.CommandText = "INSERT INTO " + QuoteIdentifier(tableName)
                                 + " (" + string.Join(", ", insertColumnNames.Select(QuoteIdentifier)) + ") VALUES ("
                                 + string.Join(", ", parameterNames) + ");";
            for (var index = 0; index < parameterNames.Length; index++)
                insert.Parameters.AddWithValue(parameterNames[index], reader.GetValue(index));

            insert.ExecuteNonQuery();
        }
    }

    private static List<string> ReadColumnNames(SqliteConnection source, string tableName)
    {
        using var command = source.CreateCommand();
        command.CommandText = "PRAGMA table_info(" + QuoteIdentifier(tableName) + ");";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(1));

        return names;
    }

    private static string? GetRowidName(IReadOnlyList<string> columnNames)
    {
        foreach (var rowidName in RowidNames)
        {
            if (!columnNames.Contains(rowidName, StringComparer.OrdinalIgnoreCase))
                return rowidName;
        }

        return null;
    }

    private static bool HasWithoutRowidClause(string sql)
    {
        string? previousWord = null;
        for (var index = 0; index < sql.Length;)
        {
            switch (sql[index])
            {
                case '\'':
                case '"':
                    index = SkipQuoted(sql, index, sql[index]);
                    continue;
                case '[':
                    index = SkipBracketedIdentifier(sql, index);
                    continue;
                case '-' when index + 1 < sql.Length && sql[index + 1] == '-':
                    index = SkipLineComment(sql, index + 2);
                    continue;
                case '/' when index + 1 < sql.Length && sql[index + 1] == '*':
                    index = SkipBlockComment(sql, index + 2);
                    continue;
            }

            if (!char.IsLetter(sql[index]))
            {
                index++;
                continue;
            }

            var wordStart = index++;
            while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] == '_'))
                index++;

            var word = sql[wordStart..index];
            if (string.Equals(previousWord, "WITHOUT", StringComparison.OrdinalIgnoreCase)
                && string.Equals(word, "ROWID", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            previousWord = word;
        }

        return false;
    }

    private static int SkipQuoted(string sql, int index, char quote)
    {
        index++;
        while (index < sql.Length)
        {
            if (sql[index++] != quote)
                continue;
            if (index >= sql.Length || sql[index] != quote)
                break;
            index++;
        }

        return index;
    }

    private static int SkipBracketedIdentifier(string sql, int index)
    {
        index++;
        while (index < sql.Length && sql[index++] != ']')
        {
        }

        return index;
    }

    private static int SkipLineComment(string sql, int index)
    {
        while (index < sql.Length && sql[index] is not '\r' and not '\n')
            index++;

        return index;
    }

    private static int SkipBlockComment(string sql, int index)
    {
        while (index + 1 < sql.Length && (sql[index] != '*' || sql[index + 1] != '/'))
            index++;

        return Math.Min(index + 2, sql.Length);
    }

    private static void BeginSnapshot(SqliteConnection source) => Execute(source, "BEGIN;");

    private static void RollbackSnapshot(SqliteConnection source) => Execute(source, "ROLLBACK;");

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private sealed record SchemaEntry(string Type, string Name, string Sql, bool IsWithoutRowid);
}
