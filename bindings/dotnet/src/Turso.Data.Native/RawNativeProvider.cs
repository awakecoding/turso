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

internal sealed class RawNativeDatabase(TursoDatabaseHandle database) : TursoNativeDatabase
{
    private readonly TursoDatabaseHandle _database = database;
    private readonly List<GCHandle> _nativeContexts = [];

    internal TursoDatabaseHandle Handle => _database;

    public override bool IsInvalid => _database.IsInvalid;

    public override TursoNativeStatement PrepareStatement(string sql)
    {
        try
        {
            return new RawNativeStatement(TursoBindings.PrepareStatement(_database, sql));
        }
        catch (RawException exception)
        {
            throw new TursoException(exception.Message);
        }
    }

    public override void Dispose()
    {
        try
        {
            _database.Dispose();
        }
        finally
        {
            foreach (var context in _nativeContexts)
            {
                if (context.Target is INativeContext nativeContext)
                    nativeContext.Release();
                if (context.IsAllocated)
                    context.Free();
            }

            _nativeContexts.Clear();
        }
    }

    internal void AddNativeContext(GCHandle context) => _nativeContexts.Add(context);
}

internal sealed class RawNativeStatement(TursoStatementHandle statement) : TursoNativeStatement
{
    private readonly TursoStatementHandle _statement = statement;

    public override bool IsInvalid => _statement.IsInvalid;

    public override int ParameterCount => Execute(() => TursoBindings.GetParameterCount(_statement));

    public override void BindParameter(int index, TursoValue value)
    {
        Execute(() => TursoBindings.BindParameter(_statement, index, ToRawValue(value)));
    }

    public override int BindNamedParameter(string name, TursoValue value)
    {
        return Execute(() => TursoBindings.BindNamedParameter(_statement, name, ToRawValue(value)));
    }

    public override string? GetParameterName(int index)
    {
        return Execute(() => TursoBindings.GetParameterName(_statement, index));
    }

    public override bool Read()
    {
        return Execute(() => TursoBindings.Read(_statement));
    }

    public override TursoValue GetValue(int ordinal)
    {
        return ToTursoValue(Execute(() => TursoBindings.GetValue(_statement, ordinal)));
    }

    public override string GetName(int ordinal)
    {
        return Execute(() => TursoBindings.GetName(_statement, ordinal));
    }

    public override int FieldCount => Execute(() => TursoBindings.GetFieldCount(_statement));

    public override int RowsAffected => Execute(() => TursoBindings.RowsAffected(_statement));

    public override bool HasRows => Execute(() => TursoBindings.HasRows(_statement));

    public override void Dispose()
    {
        _statement.Dispose();
    }

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

    private static void Execute(Action operation)
    {
        try
        {
            operation();
        }
        catch (RawException exception)
        {
            throw new TursoException(exception.Message);
        }
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
