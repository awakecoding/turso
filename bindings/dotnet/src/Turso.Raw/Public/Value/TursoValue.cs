namespace Turso.Raw.Public.Value;

public struct TursoValue
{
    public TursoValueType ValueType;
    public long IntValue;
    public double RealValue;
    public string StringValue;
    public byte[] BlobValue;

    public static TursoValue Empty() => new() { ValueType = TursoValueType.Empty };
    public static TursoValue Null() => new() { ValueType = TursoValueType.Null };
    public static TursoValue Int(Int64 value) => new() { ValueType = TursoValueType.Integer, IntValue = value };
    public static TursoValue Real(Double value) => new() { ValueType = TursoValueType.Real, RealValue = value };
    public static TursoValue String(string value) => new() { ValueType = TursoValueType.Text, StringValue = value };
    public static TursoValue Blob(byte[] value) => new() { ValueType = TursoValueType.Blob, BlobValue = value };

    public static implicit operator TursoValue(global::Turso.TursoValue value)
    {
        return value.ValueType switch
        {
            global::Turso.TursoValueType.Empty => Empty(),
            global::Turso.TursoValueType.Null => Null(),
            global::Turso.TursoValueType.Integer => Int(value.IntValue),
            global::Turso.TursoValueType.Real => Real(value.RealValue),
            global::Turso.TursoValueType.Text => String(value.StringValue),
            global::Turso.TursoValueType.Blob => Blob(value.BlobValue),
            _ => throw new ArgumentOutOfRangeException(nameof(value), value.ValueType, null),
        };
    }

    public static implicit operator global::Turso.TursoValue(TursoValue value)
    {
        return value.ValueType switch
        {
            TursoValueType.Empty => global::Turso.TursoValue.Empty(),
            TursoValueType.Null => global::Turso.TursoValue.Null(),
            TursoValueType.Integer => global::Turso.TursoValue.Int(value.IntValue),
            TursoValueType.Real => global::Turso.TursoValue.Real(value.RealValue),
            TursoValueType.Text => global::Turso.TursoValue.String(value.StringValue),
            TursoValueType.Blob => global::Turso.TursoValue.Blob(value.BlobValue),
            _ => throw new ArgumentOutOfRangeException(nameof(value), value.ValueType, null),
        };
    }
}