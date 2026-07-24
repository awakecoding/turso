using System.Runtime.InteropServices;
using Turso.Raw.Public;
using Turso.Raw.Public.Handles;
using RawCipher = Turso.Raw.Public.Value.TursoEncryptionCipher;
using RawException = Turso.Raw.Public.TursoException;
using RawValue = Turso.Raw.Public.Value.TursoValue;
using RawValueType = Turso.Raw.Public.Value.TursoValueType;
using Turso.Data.Sqlite;

namespace Turso.Data.Native;

public static class NativeProviderRegistration
{
    public static void Register()
    {
        TursoNativeProvider.Register(new RawNativeProviderFactory());
        SqliteNativeProvider.Register(new RawSqliteNativeProviderFactory());
    }
}

internal sealed class RawNativeProviderFactory : TursoNativeProviderFactory
{
    public override TursoNativeDatabase OpenDatabase(
        string path,
        TursoEncryptionCipher? cipher,
        string? encryptionKey)
    {
        ArgumentNullException.ThrowIfNull(path);

        var database = Execute(() => cipher switch
        {
            null => TursoBindings.OpenDatabase(path),
            _ when string.IsNullOrWhiteSpace(encryptionKey) => throw new InvalidOperationException(
                "Encryption Key is required when Encryption Cipher is specified."),
            _ => TursoBindings.OpenDatabaseWithEncryption(path, ToRawCipher(cipher.Value), encryptionKey),
        });

        return new RawNativeDatabase(database);
    }

    private static RawCipher ToRawCipher(TursoEncryptionCipher cipher)
    {
        return cipher switch
        {
            TursoEncryptionCipher.Aes128Gcm => RawCipher.Aes128Gcm,
            TursoEncryptionCipher.Aes256Gcm => RawCipher.Aes256Gcm,
            TursoEncryptionCipher.Aegis256 => RawCipher.Aegis256,
            TursoEncryptionCipher.Aegis256x2 => RawCipher.Aegis256x2,
            TursoEncryptionCipher.Aegis128l => RawCipher.Aegis128l,
            TursoEncryptionCipher.Aegis128x2 => RawCipher.Aegis128x2,
            TursoEncryptionCipher.Aegis128x4 => RawCipher.Aegis128x4,
            _ => throw new ArgumentOutOfRangeException(nameof(cipher), cipher, null),
        };
    }

    private static T Execute<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (RawException exception)
        {
            throw new TursoException(exception.Message);
        }
    }
}

internal sealed class RawNativeDatabase : TursoNativeDatabase
{
    private readonly object _gate = new();
    private readonly TursoDatabaseHandle _database;
    private readonly List<GCHandle> _nativeContexts = [];
    private readonly HashSet<RawNativeStatement> _statements = [];
    private int _operationThreadId;
    private bool _disposed;

    internal TursoDatabaseHandle Handle => _database;

    public RawNativeDatabase(TursoDatabaseHandle database)
    {
        _database = database;
    }

    public override bool IsInvalid => _disposed || _database.IsInvalid;

    public override TursoNativeStatement PrepareStatement(string sql)
    {
        try
        {
            return ExecuteExclusive(() =>
            {
                var statement = new RawNativeStatement(TursoBindings.PrepareStatement(_database, sql), this);
                _statements.Add(statement);
                return statement;
            });
        }
        catch (RawException exception)
        {
            throw new TursoException(exception.Message);
        }
    }

    public override void SetBusyTimeout(TimeSpan timeout)
        => ExecuteExclusive(() => TursoBindings.SetBusyTimeout(_database, timeout));

    internal void Interrupt()
    {
        try
        {
            TursoBindings.Interrupt(_database);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public override void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            ThrowIfReentrant();
            _operationThreadId = Environment.CurrentManagedThreadId;
            try
            {
                foreach (var statement in _statements)
                    statement.DisposeFromDatabase();
                _statements.Clear();
                _database.Dispose();

                foreach (var context in _nativeContexts)
                {
                    if (context.Target is INativeContext nativeContext)
                        nativeContext.Release();
                    if (context.IsAllocated)
                        context.Free();
                }

                _nativeContexts.Clear();
                _disposed = true;
            }
            finally
            {
                _operationThreadId = 0;
            }
        }
    }

    internal T ExecuteExclusive<T>(Func<T> operation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfReentrant();
            _operationThreadId = Environment.CurrentManagedThreadId;
            try
            {
                return operation();
            }
            finally
            {
                _operationThreadId = 0;
            }
        }
    }

    internal void ExecuteExclusive(Action operation)
        => ExecuteExclusive(() =>
        {
            operation();
            return true;
        });

    internal void AddNativeContext(GCHandle context)
    {
        if (_operationThreadId != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Native contexts must be registered within an exclusive operation.");
        _nativeContexts.Add(context);
    }

    internal void DisposeStatement(RawNativeStatement statement, TursoStatementHandle handle)
    {
        lock (_gate)
        {
            if (_statements.Remove(statement))
                handle.Dispose();
        }
    }

    private void ThrowIfReentrant()
    {
        if (_operationThreadId == Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException(
                "The native Turso connection does not support reentrant operations from callbacks.");
        }
    }
}

internal sealed class RawNativeStatement : TursoNativeStatement
{
    private readonly TursoStatementHandle _statement;
    private readonly RawNativeDatabase _database;

    public RawNativeStatement(TursoStatementHandle statement, RawNativeDatabase database)
    {
        _statement = statement;
        _database = database;
    }

    public override bool IsInvalid => _statement.IsInvalid || _database.IsInvalid;

    public override int ParameterCount
        => ExecuteExclusive(() => TursoBindings.GetParameterCount(_statement));

    public override void BindParameter(int index, TursoValue value)
    {
        ExecuteExclusive(() => TursoBindings.BindParameter(_statement, index, ToRawValue(value)));
    }

    public override int BindNamedParameter(string name, TursoValue value)
    {
        return ExecuteExclusive(() => TursoBindings.BindNamedParameter(_statement, name, ToRawValue(value)));
    }

    public override string? GetParameterName(int index)
    {
        return ExecuteExclusive(() => TursoBindings.GetParameterName(_statement, index));
    }

    public override bool Read()
    {
        return ExecuteExclusive(() => TursoBindings.Read(_statement));
    }

    public override void Interrupt() => _database.Interrupt();

    public override TursoValue GetValue(int ordinal)
    {
        return ToTursoValue(ExecuteExclusive(() => TursoBindings.GetValue(_statement, ordinal)));
    }

    public override string GetName(int ordinal)
    {
        return ExecuteExclusive(() => TursoBindings.GetName(_statement, ordinal));
    }

    public override int FieldCount
        => ExecuteExclusive(() => TursoBindings.GetFieldCount(_statement));

    public override int RowsAffected
        => ExecuteExclusive(() => TursoBindings.RowsAffected(_statement));

    public override bool HasRows
        => ExecuteExclusive(() => TursoBindings.HasRows(_statement));

    public override void Dispose() => _database.DisposeStatement(this, _statement);

    internal void DisposeFromDatabase() => _statement.Dispose();

    private static RawValue ToRawValue(TursoValue value)
    {
        return value.ValueType switch
        {
            TursoValueType.Empty => RawValue.Empty(),
            TursoValueType.Null => RawValue.Null(),
            TursoValueType.Integer => RawValue.Int(value.IntValue),
            TursoValueType.Real => RawValue.Real(value.RealValue),
            TursoValueType.Text => RawValue.String(value.StringValue),
            TursoValueType.Blob => RawValue.Blob(value.BlobValue),
            _ => throw new ArgumentOutOfRangeException(nameof(value), value.ValueType, null),
        };
    }

    private static TursoValue ToTursoValue(RawValue value)
    {
        return value.ValueType switch
        {
            RawValueType.Empty => TursoValue.Empty(),
            RawValueType.Null => TursoValue.Null(),
            RawValueType.Integer => TursoValue.Int(value.IntValue),
            RawValueType.Real => TursoValue.Real(value.RealValue),
            RawValueType.Text => TursoValue.String(value.StringValue),
            RawValueType.Blob => TursoValue.Blob(value.BlobValue),
            _ => throw new ArgumentOutOfRangeException(nameof(value), value.ValueType, null),
        };
    }

    private void ExecuteExclusive(Action operation)
    {
        try
        {
            _database.ExecuteExclusive(operation);
        }
        catch (RawException exception)
        {
            throw new TursoException(exception.Message);
        }
    }

    private T ExecuteExclusive<T>(Func<T> operation)
    {
        try
        {
            return _database.ExecuteExclusive(operation);
        }
        catch (RawException exception)
        {
            throw new TursoException(exception.Message);
        }
    }
}
