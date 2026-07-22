namespace Turso.Core;

public interface IManagedDatabaseAdapter : IDisposable
{
    IManagedConnectionAdapter Connect();

    IManagedConnectionAdapter Connection { get; }
}

public interface IManagedConnectionAdapter : IDisposable
{
    IManagedStatementAdapter Prepare(string sql);

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
}

public interface IManagedStatementAdapter : IDisposable
{
    int ParameterCount { get; }

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

    string? GetParameterName(int index);
}

public sealed class ManagedDatabaseAdapter : IManagedDatabaseAdapter
{
    private readonly object _gate = new();
    private EmbeddedDatabase? _ownedDatabase;
    private ManagedConnectionAdapter? _connection;
    private bool _disposed;

    private ManagedDatabaseAdapter(EmbeddedDatabase ownedDatabase)
    {
        _ownedDatabase = ownedDatabase;
    }

    private ManagedDatabaseAdapter(ManagedConnectionAdapter connection, EmbeddedDatabase? ownedDatabase)
    {
        _connection = connection;
        _ownedDatabase = ownedDatabase;
    }

    public static ManagedDatabaseAdapter Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return string.Equals(path, ":memory:", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(path)
            ? new ManagedDatabaseAdapter(new EmbeddedDatabase())
            : new ManagedDatabaseAdapter(EmbeddedDatabase.OpenFile(path));
    }

    public static ManagedDatabaseAdapter FromConnection(
        EmbeddedConnection connection,
        EmbeddedDatabase? owner = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new ManagedDatabaseAdapter(ManagedConnectionAdapter.Wrap(connection), owner);
    }

    public IManagedConnectionAdapter Connect()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_connection is not null)
                return _connection;

            var database = _ownedDatabase
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
            ownedDatabase = _ownedDatabase;
            _ownedDatabase = null;
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

    public string GetColumnName(int ordinal)
    {
        return GetStatement().GetColumnName(ordinal);
    }

    public int GetColumnCount()
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
