using System.Collections;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Globalization;
using Turso.Core;

namespace Turso;

public class TursoDataReader : DbDataReader, IConnectionOwnedReader
{
    private readonly TursoCommand _command;
    private readonly TursoConnection _connection;
    private readonly TursoNativeStatement? _nativeStatement;
    private readonly IManagedStatementAdapter? _managedStatement;
    private readonly CommandBehavior _behavior;
    private readonly Action _completionCallback;
    private bool _isClosed;
    private bool _hasCurrentRow;
    private bool _completionNotified;

    private enum ReaderValueKind
    {
        Empty,
        Null,
        Integer,
        Real,
        Text,
        Blob,
    }

    private readonly struct ReaderValue
    {
        private readonly TursoValue _nativeValue;
        private readonly ManagedResultValue _managedValue;
        private readonly bool _isManaged;

        private ReaderValue(TursoValue nativeValue)
        {
            _nativeValue = nativeValue;
        }

        private ReaderValue(ManagedResultValue managedValue)
        {
            _managedValue = managedValue;
            _isManaged = true;
        }

        public ReaderValueKind Kind => _isManaged
            ? _managedValue.Kind switch
            {
                ManagedResultValueKind.Null => ReaderValueKind.Null,
                ManagedResultValueKind.Integer => ReaderValueKind.Integer,
                ManagedResultValueKind.Real => ReaderValueKind.Real,
                ManagedResultValueKind.Text => ReaderValueKind.Text,
                ManagedResultValueKind.Blob => ReaderValueKind.Blob,
                _ => throw new InvalidOperationException($"Unknown managed result value kind {_managedValue.Kind}."),
            }
            : _nativeValue.ValueType switch
            {
                TursoValueType.Empty => ReaderValueKind.Empty,
                TursoValueType.Null => ReaderValueKind.Null,
                TursoValueType.Integer => ReaderValueKind.Integer,
                TursoValueType.Real => ReaderValueKind.Real,
                TursoValueType.Text => ReaderValueKind.Text,
                TursoValueType.Blob => ReaderValueKind.Blob,
                _ => throw new InvalidOperationException($"Unknown native result value type {_nativeValue.ValueType}."),
            };

        public long Integer => _isManaged ? _managedValue.AsInteger() : _nativeValue.IntValue;

        public double Real => _isManaged ? _managedValue.AsReal() : _nativeValue.RealValue;

        public string Text => _isManaged ? _managedValue.AsText() : _nativeValue.StringValue;

        public byte[] Blob => _isManaged ? _managedValue.AsBlob().ToArray() : _nativeValue.BlobValue;

        public static ReaderValue FromNative(TursoValue value) => new(value);

        public static ReaderValue FromManaged(ManagedResultValue value) => new(value);
    }

    public TursoDataReader(
        TursoCommand command,
        TursoNativeStatement? nativeStatement,
        IManagedStatementAdapter? managedStatement,
        CommandBehavior behavior)
        : this(command, nativeStatement, managedStatement, behavior, static () => { })
    {
    }

    internal TursoDataReader(
        TursoCommand command,
        TursoNativeStatement? nativeStatement,
        IManagedStatementAdapter? managedStatement,
        CommandBehavior behavior,
        Action completionCallback)
    {
        if ((nativeStatement is null) == (managedStatement is null))
            throw new ArgumentException("A reader requires exactly one statement implementation.");

        _command = command;
        _connection = command.Connection as TursoConnection
            ?? throw new InvalidOperationException("A data reader requires an associated TursoConnection.");
        _nativeStatement = nativeStatement;
        _managedStatement = managedStatement;
        _behavior = behavior;
        _completionCallback = completionCallback;
        ((ILocalReaderConnection)_connection).ReaderOpened(this);
    }

    public override bool GetBoolean(int ordinal)
    {
        return ReadValue(ordinal).Integer != 0;
    }

    public override byte GetByte(int ordinal)
    {
        return (byte)ReadValue(ordinal).Integer;
    }

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        EnsureOpen();
        return CopyValue(GetBlobValue(ordinal), dataOffset, buffer, bufferOffset, length);
    }

    public override char GetChar(int ordinal)
    {
        var value = ReadValue(ordinal);
        if (value.Kind == ReaderValueKind.Text && value.Text.Length == 1)
        {
            return value.Text[0];
        }

        return (char)ReadValue(ordinal).Integer;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        EnsureOpen();
        return CopyValue(GetTextValue(ordinal).ToCharArray(), dataOffset, buffer, bufferOffset, length);
    }

    public override Stream GetStream(int ordinal)
    {
        EnsureOpen();
        return new MemoryStream(GetBlobValue(ordinal).ToArray(), writable: false);
    }

    public override TextReader GetTextReader(int ordinal)
    {
        EnsureOpen();
        return new StringReader(GetTextValue(ordinal));
    }

    public override string GetDataTypeName(int ordinal)
    {
        if (!_hasCurrentRow)
        {
            ValidateOrdinal(ordinal);
            return string.Empty;
        }

        var value = ReadValue(ordinal);
        return GetTypeName(value.Kind);
    }

    public override DateTime GetDateTime(int ordinal)
    {
        var value = ReadValue(ordinal);
        switch (value.Kind)
        {
            case ReaderValueKind.Text:
                return DateTime.Parse(GetString(ordinal), CultureInfo.InvariantCulture);
            default:
                return DateTime.MinValue;
        }
    }

    public override decimal GetDecimal(int ordinal)
    {
        return (decimal)ReadValue(ordinal).Real;
    }

    public override double GetDouble(int ordinal)
    {
        return ReadValue(ordinal).Real;
    }

    public override Type GetFieldType(int ordinal)
    {
        if (!_hasCurrentRow)
        {
            ValidateOrdinal(ordinal);
            return typeof(object);
        }

        var value = ReadValue(ordinal);
        return value.Kind switch
        {
            ReaderValueKind.Integer => typeof(long),
            ReaderValueKind.Real => typeof(double),
            ReaderValueKind.Text => typeof(string),
            ReaderValueKind.Blob => typeof(byte[]),
            _ => typeof(object)
        };
    }

    public override float GetFloat(int ordinal)
    {
        return (float)ReadValue(ordinal).Real;
    }

    public override Guid GetGuid(int ordinal)
    {
        return Guid.Parse(ReadValue(ordinal).Text);
    }

    public override short GetInt16(int ordinal)
    {
        return (short)ReadValue(ordinal).Integer;
    }

    public override int GetInt32(int ordinal)
    {
        return (int)ReadValue(ordinal).Integer;
    }

    public override long GetInt64(int ordinal)
    {
        return ReadValue(ordinal).Integer;
    }

    public override string GetName(int ordinal)
    {
        return ReadName(ordinal);
    }

    public override int GetOrdinal(string name)
    {
        var fields = GetFieldCount();
        for (var i = 0; i < fields; i++)
        {
            var columnName = ReadName(i);
            if (columnName == name)
                return i;
        }

        throw new IndexOutOfRangeException($"column {name} not found");
    }

    public override string GetString(int ordinal)
    {
        return ReadValue(ordinal).Text;
    }

    public override object GetValue(int ordinal)
    {
        var value = ReadValue(ordinal);
        return value.Kind switch
        {
            ReaderValueKind.Null or ReaderValueKind.Empty => DBNull.Value,
            ReaderValueKind.Integer => value.Integer,
            ReaderValueKind.Real => value.Real,
            ReaderValueKind.Text => value.Text,
            ReaderValueKind.Blob => value.Blob,
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++)
        {
            values[i] = GetValue(i)!;
        }

        return count;
    }

    public override bool IsDBNull(int ordinal)
    {
        var valueType = ReadValue(ordinal).Kind;
        return valueType == ReaderValueKind.Null;
    }

    public override int FieldCount => GetFieldCount();

    public override object this[int ordinal] => GetValue(ordinal)!;

    public override object this[string name]
    {
        get
        {
            var ordinal = GetOrdinal(name);
            return GetValue(ordinal)!;
        }
    }

    public override int RecordsAffected => GetRowsAffected();
    public override bool HasRows => HasRowsCore();
    public override bool IsClosed => _isClosed
        || _connection.State != ConnectionState.Open
        || (_managedStatement is null && (_nativeStatement?.IsInvalid ?? true));

    public override bool NextResult()
        => _command.RunOperation(NextResultCore);

    private bool NextResultCore(CancellationToken cancellationToken)
    {
        EnsureOpen();
        while (Step(cancellationToken))
        {
        }

        _hasCurrentRow = false;
        NotifyCompletion();
        return false;
    }

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        return _command.RunOperationAsync(NextResultCore, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            CloseCore(closeConnection: true);

        base.Dispose(disposing);
    }

    void IConnectionOwnedReader.CloseFromConnection() => CloseCore(closeConnection: false);

    public override bool Read()
        => _command.RunOperation(ReadCore);

    private bool ReadCore(CancellationToken cancellationToken)
    {
        EnsureOpen();
        _hasCurrentRow = Step(cancellationToken);
        if (!_hasCurrentRow)
            NotifyCompletion();
        return _hasCurrentRow;
    }

    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        return _command.RunOperationAsync(ReadCore, cancellationToken);
    }

    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken)
    {
        return CompleteAsync(() => IsDBNull(ordinal), cancellationToken);
    }

    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken)
    {
        return CompleteAsync(() => GetFieldValue<T>(ordinal), cancellationToken);
    }

    public override int Depth => 0;

    public override IEnumerator GetEnumerator()
    {
        return new DbEnumerator(this, closeReader: false);
    }

    private static Task<T> CompleteAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<T>(cancellationToken);

        try
        {
            return Task.FromResult(operation());
        }
        catch (Exception exception)
        {
            return Task.FromException<T>(exception);
        }
    }

    private static long CopyValue<T>(T[] source, long dataOffset, T[]? buffer, int bufferOffset, int length)
    {
        if (dataOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(dataOffset), dataOffset, message: null);
        if (buffer is null)
            return source.LongLength;
        if (bufferOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(bufferOffset), bufferOffset, message: null);
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length), length, message: null);
        if (bufferOffset > buffer.Length - length)
            throw new ArgumentException("Offset and length must refer to a location within the buffer.");
        if (dataOffset >= source.LongLength)
            return 0;

        var count = (int)Math.Min(length, source.LongLength - dataOffset);
        Array.Copy(source, (int)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    private byte[] GetBlobValue(int ordinal)
    {
        var value = GetTypedValue(ordinal);
        return value.Kind == ReaderValueKind.Blob
            ? value.Blob
            : throw new InvalidCastException("The requested value is not a BLOB.");
    }

    private string GetTextValue(int ordinal)
    {
        var value = GetTypedValue(ordinal);
        return value.Kind == ReaderValueKind.Text
            ? value.Text
            : throw new InvalidCastException("The requested value is not TEXT.");
    }

    private ReaderValue GetTypedValue(int ordinal)
    {
        if (!_hasCurrentRow)
            throw new InvalidOperationException("No data exists for the row/column.");
        ValidateOrdinal(ordinal);

        var value = ReadValue(ordinal);
        if (value.Kind is ReaderValueKind.Null or ReaderValueKind.Empty)
            throw new InvalidOperationException("The data is Null. This method or property cannot be called on Null values.");

        return value;
    }

    private static string GetTypeName(ReaderValueKind valueType)
    {
        return valueType switch
        {
            ReaderValueKind.Empty => "",
            ReaderValueKind.Null => "NULL",
            ReaderValueKind.Integer => "INTEGER",
            ReaderValueKind.Real => "REAL",
            ReaderValueKind.Text => "TEXT",
            ReaderValueKind.Blob => "BLOB",
            _ => throw new InvalidEnumArgumentException(nameof(valueType))
        };
    }

    private ReaderValue ReadValue(int ordinal)
    {
        if (_managedStatement is null)
            return ReaderValue.FromNative(GetNativeStatement().GetValue(ordinal));

        return ExecuteManaged(statement => ReaderValue.FromManaged(statement.CurrentRow.GetValue(ordinal)));
    }

    private string ReadName(int ordinal)
        => _managedStatement is null
            ? GetNativeStatement().GetName(ordinal)
            : ExecuteManaged(statement => statement.ResultMetadata.GetColumn(ordinal).Name);

    private int GetFieldCount()
        => _managedStatement is null
            ? GetNativeStatement().FieldCount
            : ExecuteManaged(statement => statement.ResultMetadata.ColumnCount);

    private int GetRowsAffected()
        => _managedStatement is null
            ? GetNativeStatement().RowsAffected
            : ExecuteManaged(statement => statement.RowsAffected);

    private bool HasRowsCore()
        => _managedStatement is null
            ? GetNativeStatement().HasRows
            : ExecuteManaged(statement => statement.HasRows());

    private bool Step(CancellationToken cancellationToken)
        => _managedStatement is null
            ? ReadNative(cancellationToken)
            : ExecuteManaged(statement => statement.Step(cancellationToken) == StatementStepResult.Row);

    private T ExecuteManaged<T>(Func<IManagedStatementAdapter, T> operation)
    {
        try
        {
            return operation(_managedStatement!);
        }
        catch (EmbeddedSqlException exception)
        {
            throw TursoException.FromCore(exception);
        }
    }

    private bool ReadNative(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var statement = GetNativeStatement();
        using var registration = cancellationToken.UnsafeRegister(
            static state => ((TursoNativeStatement)state!).Interrupt(),
            statement);
        bool result;
        try
        {
            result = statement.Read();
        }
        catch (TursoException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private void ValidateOrdinal(int ordinal)
    {
        if (ordinal < 0 || ordinal >= FieldCount)
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, message: null);
    }

    private TursoNativeStatement GetNativeStatement()
        => _nativeStatement ?? throw new InvalidOperationException("The reader statement is unavailable.");

    private void EnsureOpen()
    {
        if (IsClosed)
            throw new InvalidOperationException("The data reader is closed.");
    }

    private void NotifyCompletion()
    {
        if (_completionNotified)
            return;

        _completionNotified = true;
        _completionCallback();
    }

    private void CloseCore(bool closeConnection)
    {
        if (_isClosed)
            return;

        _command.Cancel();
        try
        {
            _nativeStatement?.Dispose();
            _managedStatement?.Dispose();
        }
        finally
        {
            _hasCurrentRow = false;
            _isClosed = true;
            ((ILocalReaderConnection)_connection).ReaderClosed(this);
            if (closeConnection && (_behavior & CommandBehavior.CloseConnection) == CommandBehavior.CloseConnection)
                _connection.Close();
        }
    }

}
