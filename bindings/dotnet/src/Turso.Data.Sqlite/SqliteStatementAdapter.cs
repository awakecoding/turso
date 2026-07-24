using Turso.Core;
using Turso;

namespace Turso.Data.Sqlite;

internal sealed class SqliteStatementAdapter : IDisposable
{
    private readonly TursoNativeStatement? _nativeStatement;
    private readonly IManagedStatementAdapter? _managedStatement;
    private bool _disposed;

    private SqliteStatementAdapter(TursoNativeStatement nativeStatement)
    {
        _nativeStatement = nativeStatement;
    }

    private SqliteStatementAdapter(IManagedStatementAdapter managedStatement)
    {
        _managedStatement = managedStatement;
    }

    public static SqliteStatementAdapter FromNative(TursoNativeStatement statement)
        => new(statement ?? throw new ArgumentNullException(nameof(statement)));

    public static SqliteStatementAdapter FromManaged(IManagedStatementAdapter statement)
        => new(statement ?? throw new ArgumentNullException(nameof(statement)));

    public bool IsInvalid => _disposed || (_nativeStatement?.IsInvalid ?? false);

    public bool UsesManagedResults => _managedStatement is not null;

    public int NativeParameterCount => GetNativeStatement().ParameterCount;

    public int RowsAffected
        => _managedStatement?.RowsAffected ?? GetNativeStatement().RowsAffected;

    public int ColumnCount
        => _managedStatement?.ResultMetadata.ColumnCount ?? GetNativeStatement().FieldCount;

    public void BindNative(int index, Turso.TursoValue value)
        => GetNativeStatement().BindParameter(index, value);

    public string? GetNativeParameterName(int index)
        => GetNativeStatement().GetParameterName(index);

    public bool Read()
        => Read(CancellationToken.None);

    public bool Read(CancellationToken cancellationToken)
        => _managedStatement is null
            ? ReadNative(cancellationToken)
            : _managedStatement.Step(cancellationToken) == StatementStepResult.Row;

    public bool HasRows()
        => _managedStatement?.HasRows() ?? GetNativeStatement().HasRows;

    public string GetName(int ordinal)
        => _managedStatement?.ResultMetadata.GetColumn(ordinal).Name ?? GetNativeStatement().GetName(ordinal);

    public ManagedResultMetadata ManagedResultMetadata
        => _managedStatement?.ResultMetadata
           ?? throw new InvalidOperationException("The managed statement is unavailable.");

    public ManagedResultRow ManagedCurrentRow
        => _managedStatement?.CurrentRow
           ?? throw new InvalidOperationException("The managed statement is unavailable.");

    public TursoValue GetNativeValue(int ordinal)
    {
        return GetNativeStatement().GetValue(ordinal);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_managedStatement is null)
            _nativeStatement?.Dispose();
        else
            _managedStatement.Dispose();
    }

    private TursoNativeStatement GetNativeStatement()
        => _nativeStatement ?? throw new InvalidOperationException("The native statement is unavailable.");

    private bool ReadNative(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = GetNativeStatement().Read();
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
