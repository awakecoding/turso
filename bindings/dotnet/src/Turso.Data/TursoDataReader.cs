using System.Collections;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Globalization;
using Turso.Raw.Public;
using Turso.Raw.Public.Handles;
using Turso.Raw.Public.Value;

namespace Turso;

public class TursoDataReader : DbDataReader
{
    private readonly TursoCommand _command;
    private readonly TursoStatementHandle _statement;
    private readonly CommandBehavior _behavior;
    private bool _isClosed;
    private bool _hasCurrentRow;

    public TursoDataReader(TursoCommand command, TursoStatementHandle statement, CommandBehavior behavior)
    {
        _command = command;
        _statement = statement;
        _behavior = behavior;
    }

    public override bool GetBoolean(int ordinal)
    {
        return TursoBindings.GetValue(_statement, ordinal).IntValue != 0;
    }

    public override byte GetByte(int ordinal)
    {
        return (byte)TursoBindings.GetValue(_statement, ordinal).IntValue;
    }

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        EnsureOpen();
        return CopyValue(GetBlobValue(ordinal), dataOffset, buffer, bufferOffset, length);
    }

    public override char GetChar(int ordinal)
    {
        var value = TursoBindings.GetValue(_statement, ordinal);
        if (value.ValueType == TursoValueType.Text && value.StringValue.Length == 1)
        {
            return value.StringValue[0];
        }

        return (char)TursoBindings.GetValue(_statement, ordinal).IntValue;
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
        var value = TursoBindings.GetValue(_statement, ordinal);
        return GetTypeName(value.ValueType);
    }

    public override DateTime GetDateTime(int ordinal)
    {
        var value = TursoBindings.GetValue(_statement, ordinal);
        switch (value.ValueType)
        {
            case TursoValueType.Text:
                return DateTime.Parse(GetString(ordinal), CultureInfo.InvariantCulture);
            default:
                return DateTime.MinValue;
        }
    }

    public override decimal GetDecimal(int ordinal)
    {
        return (decimal)TursoBindings.GetValue(_statement, ordinal).RealValue;
    }

    public override double GetDouble(int ordinal)
    {
        return TursoBindings.GetValue(_statement, ordinal).RealValue;
    }

    public override Type GetFieldType(int ordinal)
    {
        var value = TursoBindings.GetValue(_statement, ordinal);
        return value.ValueType switch
        {
            TursoValueType.Integer => typeof(long),
            TursoValueType.Real => typeof(double),
            TursoValueType.Text => typeof(string),
            TursoValueType.Blob => typeof(byte[]),
            _ => typeof(object)
        };
    }

    public override float GetFloat(int ordinal)
    {
        return (float)TursoBindings.GetValue(_statement, ordinal).RealValue;
    }

    public override Guid GetGuid(int ordinal)
    {
        return Guid.Parse(TursoBindings.GetValue(_statement, ordinal).StringValue);
    }

    public override short GetInt16(int ordinal)
    {
        return (short)TursoBindings.GetValue(_statement, ordinal).IntValue;
    }

    public override int GetInt32(int ordinal)
    {
        return (int)TursoBindings.GetValue(_statement, ordinal).IntValue;
    }

    public override long GetInt64(int ordinal)
    {
        return TursoBindings.GetValue(_statement, ordinal).IntValue;
    }

    public override string GetName(int ordinal)
    {
        return TursoBindings.GetName(_statement, ordinal);
    }

    public override int GetOrdinal(string name)
    {
        var fields = TursoBindings.GetFieldCount(_statement);
        for (var i = 0; i < fields; i++)
        {
            var columnName = TursoBindings.GetName(_statement, i);
            if (columnName == name)
                return i;
        }

        throw new IndexOutOfRangeException($"column {name} not found");
    }

    public override string GetString(int ordinal)
    {
        return TursoBindings.GetValue(_statement, ordinal).StringValue;
    }

    public override object GetValue(int ordinal)
    {
        var value = TursoBindings.GetValue(_statement, ordinal);
        return value.ValueType switch
        {
            TursoValueType.Null or TursoValueType.Empty => DBNull.Value,
            TursoValueType.Integer => value.IntValue,
            TursoValueType.Real => value.RealValue,
            TursoValueType.Text => value.StringValue,
            TursoValueType.Blob => value.BlobValue,
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
        var valueType = TursoBindings.GetValue(_statement, ordinal).ValueType;
        return valueType == TursoValueType.Null;
    }

    public override int FieldCount => TursoBindings.GetFieldCount(_statement);

    public override object this[int ordinal] => GetValue(ordinal)!;

    public override object this[string name]
    {
        get
        {
            var ordinal = GetOrdinal(name);
            return GetValue(ordinal)!;
        }
    }

    public override int RecordsAffected => TursoBindings.RowsAffected(_statement);
    public override bool HasRows => TursoBindings.HasRows(_statement);
    public override bool IsClosed => _isClosed || _statement.IsInvalid;

    public override bool NextResult()
    {
        EnsureOpen();
        while (TursoBindings.Read(_statement))
        {
        }

        _hasCurrentRow = false;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_isClosed)
        {
            _statement.Dispose();
            if ((_behavior & CommandBehavior.CloseConnection) == CommandBehavior.CloseConnection)
                _command.Connection?.Close();
        }

        _isClosed = true;
        base.Dispose(disposing);
    }

    public override bool Read()
    {
        EnsureOpen();
        _hasCurrentRow = TursoBindings.Read(_statement);
        return _hasCurrentRow;
    }

    public override int Depth => 0;

    public override IEnumerator GetEnumerator()
    {
        return new DbEnumerator(this, closeReader: false);
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
        return value.ValueType == TursoValueType.Blob
            ? value.BlobValue
            : throw new InvalidCastException("The requested value is not a BLOB.");
    }

    private string GetTextValue(int ordinal)
    {
        var value = GetTypedValue(ordinal);
        return value.ValueType == TursoValueType.Text
            ? value.StringValue
            : throw new InvalidCastException("The requested value is not TEXT.");
    }

    private TursoValue GetTypedValue(int ordinal)
    {
        if (!_hasCurrentRow)
            throw new InvalidOperationException("No data exists for the row/column.");
        if (ordinal < 0 || ordinal >= FieldCount)
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, message: null);

        var value = TursoBindings.GetValue(_statement, ordinal);
        if (value.ValueType is TursoValueType.Null or TursoValueType.Empty)
            throw new InvalidOperationException("The data is Null. This method or property cannot be called on Null values.");

        return value;
    }

    private static string GetTypeName(TursoValueType valueType)
    {
        return valueType switch
        {
            TursoValueType.Empty => "",
            TursoValueType.Null => "NULL",
            TursoValueType.Integer => "INTEGER",
            TursoValueType.Real => "REAL",
            TursoValueType.Text => "TEXT",
            TursoValueType.Blob => "BLOB",
            _ => throw new InvalidEnumArgumentException(nameof(valueType))
        };
    }

    private void EnsureOpen()
    {
        if (IsClosed)
            throw new InvalidOperationException("The data reader is closed.");
    }
}
