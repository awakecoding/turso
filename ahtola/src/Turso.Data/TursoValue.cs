namespace Turso;

public enum TursoValueType
{
    Empty,
    Null,
    Integer,
    Real,
    Text,
    Blob,
}

public struct TursoValue
{
    public TursoValueType ValueType;
    public long IntValue;
    public double RealValue;
    public string StringValue;
    public byte[] BlobValue;

    public static TursoValue Empty() => new() { ValueType = TursoValueType.Empty };
    public static TursoValue Null() => new() { ValueType = TursoValueType.Null };
    public static TursoValue Int(long value) => new() { ValueType = TursoValueType.Integer, IntValue = value };
    public static TursoValue Real(double value) => new() { ValueType = TursoValueType.Real, RealValue = value };
    public static TursoValue String(string value) => new() { ValueType = TursoValueType.Text, StringValue = value };
    public static TursoValue Blob(byte[] value) => new() { ValueType = TursoValueType.Blob, BlobValue = value };
}

/// <summary>
/// Supported encryption ciphers for local database encryption.
/// </summary>
public enum TursoEncryptionCipher
{
    /// <summary>AES-128-GCM cipher.</summary>
    Aes128Gcm,
    /// <summary>AES-256-GCM cipher.</summary>
    Aes256Gcm,
    /// <summary>AEGIS-256 cipher.</summary>
    Aegis256,
    /// <summary>AEGIS-256X2 cipher.</summary>
    Aegis256x2,
    /// <summary>AEGIS-128L cipher.</summary>
    Aegis128l,
    /// <summary>AEGIS-128X2 cipher.</summary>
    Aegis128x2,
    /// <summary>AEGIS-128X4 cipher.</summary>
    Aegis128x4,
}
