using Turso.Core.Storage;

namespace Turso.Core;

public enum ManagedResultValueKind
{
    Null,
    Integer,
    Real,
    Text,
    Blob,
}

public readonly struct ManagedResultValue
{
    private readonly SqlValue _value;

    public ManagedResultValue(SqlValue value)
    {
        _value = value;
    }

    public ManagedResultValueKind Kind => _value.Kind switch
    {
        SqlValueKind.Null => ManagedResultValueKind.Null,
        SqlValueKind.Integer => ManagedResultValueKind.Integer,
        SqlValueKind.Real => ManagedResultValueKind.Real,
        SqlValueKind.Text => ManagedResultValueKind.Text,
        SqlValueKind.Blob => ManagedResultValueKind.Blob,
        _ => throw new InvalidOperationException($"Unknown SQL value kind {_value.Kind}."),
    };

    public long AsInteger() => _value.AsInteger();

    public double AsReal() => _value.AsReal();

    public string AsText() => _value.AsText();

    public ReadOnlyMemory<byte> AsBlob() => _value.AsBlob();
}

public readonly record struct ManagedResultColumn(string Name);

public readonly struct ManagedResultRow
{
    private readonly IManagedStatementAdapter _statement;

    internal ManagedResultRow(IManagedStatementAdapter statement)
    {
        _statement = statement;
    }

    public ManagedResultValue GetValue(int ordinal) => _statement.GetResultValue(ordinal);
}

public readonly struct ManagedResultMetadata
{
    private readonly IManagedStatementAdapter _statement;

    internal ManagedResultMetadata(IManagedStatementAdapter statement)
    {
        _statement = statement;
    }

    public int ColumnCount => _statement.GetResultColumnCount();

    public ManagedResultColumn GetColumn(int ordinal) => _statement.GetResultColumn(ordinal);
}

public readonly record struct ManagedParameter(int Index, string? Name);

public readonly struct ManagedParameterMetadata
{
    private readonly IManagedStatementAdapter _statement;

    internal ManagedParameterMetadata(IManagedStatementAdapter statement)
    {
        _statement = statement;
    }

    public int Count => _statement.ParameterCount;

    public ManagedParameter GetParameter(int index) => new(index, _statement.GetParameterName(index));

    public int GetParameterIndex(string name) => _statement.GetParameterIndex(name);
}

public enum ManagedSnapshotFailure
{
    DestinationNotEmpty,
    UnsupportedSchemaObject,
    RowidNotAccessible,
    ColumnCountMismatch,
}

public sealed class ManagedSnapshotException : Exception
{
    public ManagedSnapshotException(ManagedSnapshotFailure failure, string? objectName = null)
        : base($"Managed snapshot failed: {failure}.")
    {
        Failure = failure;
        ObjectName = objectName;
    }

    public ManagedSnapshotFailure Failure { get; }

    public string? ObjectName { get; }
}

public interface IManagedDatabaseAdapter : IDisposable
{
    IManagedConnectionAdapter Connect();

    IManagedConnectionAdapter Connection { get; }
}

public interface IManagedConnectionAdapter : IDisposable
{
    IManagedStatementAdapter Prepare(string sql);

    IManagedIncrementalBlobAdapter OpenBlob(
        string databaseName,
        string tableName,
        string columnName,
        long rowId,
        bool readOnly = false)
        => throw new NotSupportedException("Managed incremental blob I/O is not supported by this connection adapter.");

    void RegisterScalarFunction(string name, int arity, Func<IReadOnlyList<SqlValue>, SqlValue> function);

    int UnregisterScalarFunctions(string name);

    void RegisterAggregateFunction(
        string name,
        int arity,
        SqlValue seed,
        Func<SqlValue, IReadOnlyList<SqlValue>, SqlValue> step,
        Func<SqlValue, SqlValue> finalize);

    int UnregisterAggregateFunctions(string name);

    void RegisterCollation(string name, Func<string, string, int> compare);

    bool UnregisterCollation(string name);

    void CopySnapshotTo(IManagedConnectionAdapter destination)
        => throw new NotSupportedException("Managed snapshot copying is not supported by this connection adapter.");
}

public interface IManagedStatementAdapter : IDisposable
{
    int ParameterCount { get; }

    ManagedParameterMetadata ParameterMetadata => new(this);

    int RowsAffected { get; }

    void Bind(int index, SqlValue value);

    int GetParameterIndex(string name);

    StatementStepResult Step();

    bool HasRows();

    void Reset();

    void ClearBindings();

    SqlValue GetValue(int ordinal);

    string GetColumnName(int ordinal);

    int GetColumnCount();

    ManagedResultValue GetResultValue(int ordinal) => new(GetValue(ordinal));

    ManagedResultColumn GetResultColumn(int ordinal) => new(GetColumnName(ordinal));

    int GetResultColumnCount() => GetColumnCount();

    ManagedResultRow CurrentRow => new(this);

    ManagedResultMetadata ResultMetadata => new(this);

    string? GetParameterName(int index);
}

public sealed class ManagedDatabaseAdapter : IManagedDatabaseAdapter
{
    private readonly object _gate = new();
    private EmbeddedDatabase? _databaseOwner;
    private ManagedConnectionAdapter? _connection;
    private bool _disposed;

    private ManagedDatabaseAdapter(EmbeddedDatabase databaseOwner)
    {
        _databaseOwner = databaseOwner;
    }

    private ManagedDatabaseAdapter(ManagedConnectionAdapter connection)
    {
        _connection = connection;
    }

    private ManagedDatabaseAdapter(
        ManagedConnectionAdapter connection,
        EmbeddedDatabase databaseOwner)
    {
        _connection = connection;
        _databaseOwner = databaseOwner;
    }

    public static ManagedDatabaseAdapter Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return string.Equals(path, ":memory:", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(path)
            ? new ManagedDatabaseAdapter(new EmbeddedDatabase())
            : new ManagedDatabaseAdapter(EmbeddedDatabase.OpenFile(path));
    }

    public static ManagedDatabaseAdapter OpenFile(string path, IFileSystem fileSystem, bool readOnly = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(fileSystem);
        return new ManagedDatabaseAdapter(EmbeddedDatabase.OpenFile(path, fileSystem, readOnly));
    }

    public static ManagedDatabaseAdapter FromConnection(EmbeddedConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new ManagedDatabaseAdapter(ManagedConnectionAdapter.Wrap(connection));
    }

    public static ManagedDatabaseAdapter FromConnection(
        EmbeddedConnection connection,
        EmbeddedDatabase? owner)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return owner is null
            ? FromConnection(connection)
            : new ManagedDatabaseAdapter(ManagedConnectionAdapter.Wrap(connection), owner);
    }

    public IManagedConnectionAdapter Connect()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_connection is not null)
                return _connection;

            var database = _databaseOwner
                ?? throw new InvalidOperationException("The managed database cannot create another connection.");
            return _connection = new ManagedConnectionAdapter(database.Connect());
        }
    }

    public IManagedConnectionAdapter Connection
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _connection
                    ?? throw new InvalidOperationException("The managed database has not been connected.");
            }
        }
    }

    public void Dispose()
    {
        ManagedConnectionAdapter? connection;
        EmbeddedDatabase? ownedDatabase;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            connection = _connection;
            _connection = null;
            ownedDatabase = _databaseOwner;
            _databaseOwner = null;
        }

        try
        {
            connection?.Dispose();
        }
        finally
        {
            ownedDatabase?.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

public sealed class ManagedConnectionAdapter : IManagedConnectionAdapter
{
    private readonly object _gate = new();
    private EmbeddedConnection? _connection;

    internal ManagedConnectionAdapter(EmbeddedConnection connection)
    {
        _connection = connection;
    }

    public static ManagedConnectionAdapter Wrap(EmbeddedConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new ManagedConnectionAdapter(connection);
    }

    public IManagedStatementAdapter Prepare(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return ManagedStatementAdapter.FromPreparedStatement(this, sql, GetConnection().Prepare(sql));
    }

    public IManagedIncrementalBlobAdapter OpenBlob(
        string databaseName,
        string tableName,
        string columnName,
        long rowId,
        bool readOnly = false)
    {
        return ManagedIncrementalBlobAdapter.Open(this, databaseName, tableName, columnName, rowId, readOnly);
    }

    public void RegisterScalarFunction(string name, int arity, Func<IReadOnlyList<SqlValue>, SqlValue> function)
    {
        GetConnection().RegisterScalarFunction(name, arity, function);
    }

    public int UnregisterScalarFunctions(string name)
    {
        return GetConnection().UnregisterScalarFunctions(name);
    }

    public void RegisterAggregateFunction(
        string name,
        int arity,
        SqlValue seed,
        Func<SqlValue, IReadOnlyList<SqlValue>, SqlValue> step,
        Func<SqlValue, SqlValue> finalize)
    {
        GetConnection().RegisterAggregateFunction(name, arity, seed, step, finalize);
    }

    public int UnregisterAggregateFunctions(string name)
    {
        return GetConnection().UnregisterAggregateFunctions(name);
    }

    public void RegisterCollation(string name, Func<string, string, int> compare)
    {
        GetConnection().RegisterCollation(name, compare);
    }

    public bool UnregisterCollation(string name)
    {
        return GetConnection().UnregisterCollation(name);
    }

    public void CopySnapshotTo(IManagedConnectionAdapter destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (ReferenceEquals(this, destination))
            throw new ArgumentException("Managed snapshots require distinct source and destination adapters.", nameof(destination));

        ManagedSnapshot.Copy(this, destination);
    }

    public void Dispose()
    {
        EmbeddedConnection? connection;
        lock (_gate)
        {
            connection = _connection;
            _connection = null;
        }

        connection?.Dispose();
    }

    internal EmbeddedStatement PrepareEmbeddedStatement(string sql)
    {
        return GetConnection().Prepare(sql);
    }

    private EmbeddedConnection GetConnection()
    {
        lock (_gate)
        {
            return _connection ?? throw new ObjectDisposedException(nameof(ManagedConnectionAdapter));
        }
    }
}

public sealed class ManagedStatementAdapter : IManagedStatementAdapter
{
    private readonly object _gate = new();
    private readonly ManagedConnectionAdapter _connection;
    private readonly string _sql;
    private EmbeddedStatement? _statement;
    private bool _hasCurrentRow;
    private bool _clearBindingsPending;

    private ManagedStatementAdapter(
        ManagedConnectionAdapter connection,
        string sql,
        EmbeddedStatement statement)
    {
        _connection = connection;
        _sql = sql;
        _statement = statement;
    }

    public static ManagedStatementAdapter FromPreparedStatement(
        ManagedConnectionAdapter connection,
        string sql,
        EmbeddedStatement statement)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(statement);
        return new ManagedStatementAdapter(connection, sql, statement);
    }

    public int ParameterCount => GetStatement().ParameterCount;

    public ManagedParameterMetadata ParameterMetadata => new(this);

    public int RowsAffected => GetStatement().RowsAffected;

    public void Bind(int index, SqlValue value)
    {
        GetStatement().Bind(index, value);
    }

    public int GetParameterIndex(string name)
    {
        return GetStatement().GetParameterIndex(name);
    }

    public StatementStepResult Step()
    {
        try
        {
            var result = GetStatement().Step();
            lock (_gate)
                _hasCurrentRow = result == StatementStepResult.Row;
            return result;
        }
        catch
        {
            lock (_gate)
                _hasCurrentRow = false;
            throw;
        }
    }

    public bool HasRows()
    {
        return GetStatement().HasRows();
    }

    public void Reset()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_clearBindingsPending)
            {
                ReplaceStatementWithoutBindings();
                return;
            }
        }

        GetStatement().Reset();
        lock (_gate)
            _hasCurrentRow = false;
    }

    public void ClearBindings()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_hasCurrentRow)
            {
                // A stepped row remains readable until Reset replaces the statement without its bindings.
                _clearBindingsPending = true;
                return;
            }

            ReplaceStatementWithoutBindings();
            _clearBindingsPending = true;
        }
    }

    public SqlValue GetValue(int ordinal)
    {
        return GetStatement().GetValue(ordinal);
    }

    public ManagedResultValue GetResultValue(int ordinal)
    {
        return new(GetStatement().GetValue(ordinal));
    }

    public string GetColumnName(int ordinal)
    {
        return GetStatement().GetColumnName(ordinal);
    }

    public ManagedResultColumn GetResultColumn(int ordinal)
    {
        return new(GetStatement().GetColumnName(ordinal));
    }

    public int GetColumnCount()
    {
        return GetStatement().GetColumnCount();
    }

    public int GetResultColumnCount()
    {
        return GetStatement().GetColumnCount();
    }

    public string? GetParameterName(int index)
    {
        return GetStatement().GetParameterName(index);
    }

    public void Dispose()
    {
        EmbeddedStatement? statement;
        lock (_gate)
        {
            statement = _statement;
            _statement = null;
            _hasCurrentRow = false;
            _clearBindingsPending = false;
        }

        statement?.Dispose();
    }

    private void ReplaceStatementWithoutBindings()
    {
        var replacement = _connection.PrepareEmbeddedStatement(_sql);
        EmbeddedStatement? previous = null;
        try
        {
            ThrowIfDisposed();
            previous = _statement;
            _statement = replacement;
            _hasCurrentRow = false;
            _clearBindingsPending = false;
        }
        catch
        {
            replacement.Dispose();
            throw;
        }

        previous!.Dispose();
    }

    private EmbeddedStatement GetStatement()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _statement!;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_statement is null, this);
    }
}

internal static class ManagedSnapshot
{
    private static readonly string[] RowidNames = ["rowid", "_rowid_", "oid"];

    public static void Copy(IManagedConnectionAdapter source, IManagedConnectionAdapter destination)
    {
        EnsureEmpty(destination);
        var sourceTransactionStarted = false;
        try
        {
            Execute(source, "BEGIN;");
            sourceTransactionStarted = true;
            var schema = ReadSchema(source);
            var destinationTransactionStarted = false;
            try
            {
                Execute(destination, "BEGIN;");
                destinationTransactionStarted = true;
                foreach (var entry in schema.Where(entry => entry.Type == "table"))
                    Execute(destination, entry.Sql);

                foreach (var table in schema.Where(entry => entry.Type == "table"))
                    CopyRows(source, destination, table);

                foreach (var entry in schema.Where(entry => entry.Type is "index" or "view" or "trigger"))
                    Execute(destination, entry.Sql);

                Execute(destination, "COMMIT;");
                destinationTransactionStarted = false;
            }
            catch (EmbeddedPostCommitMaintenanceException)
            {
                throw;
            }
            catch
            {
                if (destinationTransactionStarted)
                    Execute(destination, "ROLLBACK;");
                throw;
            }
        }
        finally
        {
            if (sourceTransactionStarted)
                Execute(source, "ROLLBACK;");
        }
    }

    private static void EnsureEmpty(IManagedConnectionAdapter destination)
    {
        using var statement = destination.Prepare(
            "SELECT name FROM sqlite_master WHERE name NOT LIKE 'sqlite_%';");
        if (statement.Step() == StatementStepResult.Row)
            throw new ManagedSnapshotException(ManagedSnapshotFailure.DestinationNotEmpty);
    }

    private static List<SchemaEntry> ReadSchema(IManagedConnectionAdapter source)
    {
        using var statement = source.Prepare("SELECT type, name, sql FROM sqlite_master WHERE sql IS NOT NULL;");
        var schema = new List<SchemaEntry>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var type = statement.GetValue(0).AsText();
            if (type is not ("table" or "index" or "view" or "trigger"))
            {
                throw new ManagedSnapshotException(
                    ManagedSnapshotFailure.UnsupportedSchemaObject,
                    type);
            }

            var name = statement.GetValue(1).AsText();
            var sql = statement.GetValue(2).AsText();
            schema.Add(new SchemaEntry(type, name, sql, HasWithoutRowidClause(sql)));
        }

        return schema;
    }

    private static void CopyRows(
        IManagedConnectionAdapter source,
        IManagedConnectionAdapter destination,
        SchemaEntry table)
    {
        var columnNames = ReadColumnNames(source, table.Name);
        var selectColumnNames = columnNames.ToArray();
        if (!table.IsWithoutRowid)
        {
            var rowidName = GetRowidName(columnNames);
            if (rowidName is null)
            {
                throw new ManagedSnapshotException(
                    ManagedSnapshotFailure.RowidNotAccessible,
                    table.Name);
            }

            selectColumnNames = [rowidName, .. selectColumnNames];
        }

        using var select = source.Prepare(
            "SELECT " + string.Join(", ", selectColumnNames.Select(QuoteIdentifier))
            + " FROM " + QuoteIdentifier(table.Name) + ";");

        var parameterNames = Enumerable.Range(0, selectColumnNames.Length)
            .Select(index => "$p" + index)
            .ToArray();
        var insertSql = "INSERT INTO " + QuoteIdentifier(table.Name)
                        + " (" + string.Join(", ", selectColumnNames.Select(QuoteIdentifier)) + ") VALUES ("
                        + string.Join(", ", parameterNames) + ");";
        while (select.Step() == StatementStepResult.Row)
        {
            if (select.GetColumnCount() != selectColumnNames.Length)
            {
                throw new ManagedSnapshotException(
                    ManagedSnapshotFailure.ColumnCountMismatch,
                    table.Name);
            }

            using var insert = destination.Prepare(insertSql);
            for (var index = 0; index < parameterNames.Length; index++)
                insert.Bind(index + 1, select.GetValue(index));
            Execute(insert);
        }
    }

    private static List<string> ReadColumnNames(IManagedConnectionAdapter source, string tableName)
    {
        using var statement = source.Prepare("PRAGMA table_info(" + QuoteIdentifier(tableName) + ");");
        var names = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
            names.Add(statement.GetValue(1).AsText());
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

    private static void Execute(IManagedConnectionAdapter connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        Execute(statement);
    }

    private static void Execute(IManagedStatementAdapter statement)
    {
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private sealed record SchemaEntry(string Type, string Name, string Sql, bool IsWithoutRowid);
}
