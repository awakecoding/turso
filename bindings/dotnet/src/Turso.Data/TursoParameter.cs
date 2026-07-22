using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Turso.Core;

namespace Turso;

public class TursoParameter : DbParameter
{
    private int _size;
    private static readonly Dictionary<Type, ParameterValueKind> ParameterTypeMapping =
        new()
        {
            { typeof(bool), ParameterValueKind.Integer },
            { typeof(byte), ParameterValueKind.Integer },
            { typeof(byte[]), ParameterValueKind.Blob },
            { typeof(char), ParameterValueKind.Text },
            { typeof(DateTime), ParameterValueKind.Text },
            { typeof(DateTimeOffset), ParameterValueKind.Text },
            { typeof(DateOnly), ParameterValueKind.Text },
            { typeof(TimeOnly), ParameterValueKind.Text },
            { typeof(DBNull), ParameterValueKind.Null },
            { typeof(decimal), ParameterValueKind.Text },
            { typeof(double), ParameterValueKind.Real },
            { typeof(float), ParameterValueKind.Real },
            { typeof(Guid), ParameterValueKind.Text },
            { typeof(int), ParameterValueKind.Integer },
            { typeof(long), ParameterValueKind.Integer },
            { typeof(sbyte), ParameterValueKind.Integer },
            { typeof(short), ParameterValueKind.Integer },
            { typeof(string), ParameterValueKind.Text },
            { typeof(TimeSpan), ParameterValueKind.Text },
            { typeof(uint), ParameterValueKind.Integer },
            { typeof(ulong), ParameterValueKind.Integer },
            { typeof(ushort), ParameterValueKind.Integer }
        };

    public TursoParameter()
    {
    }

    public TursoParameter(object value)
    {
        Value = value;
    }

    public TursoParameter(string parameterName, object value)
    {
        ParameterName = parameterName;
        Value = value;
    }

    public TursoParameter(string parameterName, DbType dbType, object value)
    {
        ParameterName = parameterName;
        DbType = dbType;
        Value = value;
    }

    public override void ResetDbType()
    {
        DbType = DbType.String;
    }

    public override DbType DbType { get; set; } = DbType.String;

    public override ParameterDirection Direction
    {
        get => ParameterDirection.Input;
        set
        {
            if (value != ParameterDirection.Input)
            {
                throw new ArgumentException("Only input parameters are supported");
            }
        }
    }
    public override bool IsNullable { get; set; }
    [AllowNull]
    public override string ParameterName { get; set; } = "";

    [AllowNull]
    public override string SourceColumn { get; set; } = "";
    public override object? Value { get; set; }
    public override bool SourceColumnNullMapping { get; set; }

    public TursoValue ToValue()
    {
        if (Value is null)
            return new TursoValue { ValueType = TursoValueType.Null };

        var valueType = Value.GetType();
        if (!ParameterTypeMapping.TryGetValue(valueType, out var parameterValueKind))
        {
            throw new ArgumentException($"Parameter type {valueType} is not supported");
        }

        return GetTursoValue(Value, parameterValueKind);
    }

    internal SqlValue ToSqlValue()
    {
        if (Value is null)
            return SqlValue.Null;

        var valueType = Value.GetType();
        if (!ParameterTypeMapping.TryGetValue(valueType, out var parameterValueKind))
            throw new ArgumentException($"Parameter type {valueType} is not supported");

        return parameterValueKind switch
        {
            ParameterValueKind.Null => SqlValue.Null,
            ParameterValueKind.Integer => SqlValue.Integer(Convert.ToInt64(Value, CultureInfo.InvariantCulture)),
            ParameterValueKind.Real => SqlValue.Real(Convert.ToDouble(Value, CultureInfo.InvariantCulture)),
            ParameterValueKind.Text => SqlValue.Text(ToInvariantString(Value)),
            ParameterValueKind.Blob => SqlValue.Blob((byte[])Value),
            _ => throw new ArgumentOutOfRangeException(nameof(parameterValueKind), parameterValueKind, null)
        };
    }

    public override int Size
    {
        get => _size;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, -1);
            _size = value;
        }
    }

    private static TursoValue GetTursoValue(object value, ParameterValueKind parameterValueKind)
    {
        return parameterValueKind switch
        {
            ParameterValueKind.Null => new TursoValue { ValueType = TursoValueType.Null },
            ParameterValueKind.Integer => new TursoValue { ValueType = TursoValueType.Integer, IntValue = Convert.ToInt64(value) },
            ParameterValueKind.Real => new TursoValue { ValueType = TursoValueType.Real, RealValue = Convert.ToDouble(value, CultureInfo.InvariantCulture) },
            ParameterValueKind.Text => new TursoValue { ValueType = TursoValueType.Text, StringValue = ToInvariantString(value) },
            ParameterValueKind.Blob => new TursoValue { ValueType = TursoValueType.Blob, BlobValue = (byte[])value },
            _ => throw new ArgumentOutOfRangeException(nameof(parameterValueKind), parameterValueKind, null)
        };
    }

    private enum ParameterValueKind
    {
        Null,
        Integer,
        Real,
        Text,
        Blob,
    }

    private static string ToInvariantString(object value)
    {
        return value switch
        {
            DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
            DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly timeOnly => timeOnly.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
            TimeSpan timeSpan => timeSpan.ToString("c", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()!
        };
    }
}
