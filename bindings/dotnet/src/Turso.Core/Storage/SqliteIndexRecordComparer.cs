using System.Text;

namespace Turso.Core.Storage;

/// <summary>
/// Compares SQLite index records using ascending BINARY collation.
/// </summary>
/// <remarks>
/// This is intentionally limited to SQLite's default ascending BINARY ordering.
/// DESC terms, NOCASE, RTRIM, and application-defined collations need their own
/// comparator before their index pages can be built or validated.
/// </remarks>
public sealed class SqliteIndexRecordComparer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16LittleEndian = new(false, false, true);
    private static readonly UnicodeEncoding StrictUtf16BigEndian = new(true, false, true);

    /// <summary>Creates a comparer for records encoded using <paramref name="textEncoding"/>.</summary>
    public SqliteIndexRecordComparer(SqliteTextEncoding textEncoding = SqliteTextEncoding.Utf8)
    {
        TextEncoding = textEncoding is SqliteTextEncoding.Unset
            ? SqliteTextEncoding.Utf8
            : textEncoding;
        _ = GetTextEncoding(TextEncoding);
    }

    /// <summary>The database text encoding used to interpret text record fields.</summary>
    public SqliteTextEncoding TextEncoding { get; }

    /// <summary>Compares two complete SQLite record payloads.</summary>
    public int Compare(ReadOnlySpan<byte> leftRecord, ReadOnlySpan<byte> rightRecord)
        => Compare(SqliteRecordCodec.Decode(leftRecord, TextEncoding), SqliteRecordCodec.Decode(rightRecord, TextEncoding));

    /// <summary>Compares two decoded SQLite records.</summary>
    public int Compare(IReadOnlyList<SqlValue> left, IReadOnlyList<SqlValue> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var count = Math.Min(left.Count, right.Count);
        for (var index = 0; index < count; index++)
        {
            var result = CompareValue(left[index], right[index]);
            if (result != 0)
                return result;
        }

        return left.Count.CompareTo(right.Count);
    }

    /// <summary>Validates that <paramref name="record"/> is a supported index key record.</summary>
    public void Validate(ReadOnlySpan<byte> record)
    {
        var values = SqliteRecordCodec.Decode(record, TextEncoding);
        foreach (var value in values)
        {
            if (value.Kind == SqlValueKind.Real && double.IsNaN(value.AsReal()))
                throw new InvalidDataException("SQLite index records containing NaN are not supported.");
        }
    }

    private int CompareValue(SqlValue left, SqlValue right)
    {
        var leftClass = GetStorageClass(left.Kind);
        var rightClass = GetStorageClass(right.Kind);
        if (leftClass != rightClass)
            return leftClass.CompareTo(rightClass);

        return leftClass switch
        {
            StorageClass.Null => 0,
            StorageClass.Numeric => CompareNumeric(left, right),
            StorageClass.Text => CompareBinary(
                GetTextEncoding(TextEncoding).GetBytes(left.AsText()),
                GetTextEncoding(TextEncoding).GetBytes(right.AsText())),
            StorageClass.Blob => CompareBinary(left.AsBlob().Span, right.AsBlob().Span),
            _ => throw new InvalidOperationException("SQLite index record has an unknown storage class."),
        };
    }

    private static StorageClass GetStorageClass(SqlValueKind kind)
    {
        return kind switch
        {
            SqlValueKind.Null => StorageClass.Null,
            SqlValueKind.Integer or SqlValueKind.Real => StorageClass.Numeric,
            SqlValueKind.Text => StorageClass.Text,
            SqlValueKind.Blob => StorageClass.Blob,
            _ => throw new InvalidOperationException($"Unknown SQL value kind {kind}."),
        };
    }

    private static int CompareNumeric(SqlValue left, SqlValue right)
    {
        if (left.Kind == SqlValueKind.Integer && right.Kind == SqlValueKind.Integer)
            return left.AsInteger().CompareTo(right.AsInteger());

        if (left.Kind == SqlValueKind.Real && right.Kind == SqlValueKind.Real)
        {
            var leftReal = left.AsReal();
            var rightReal = right.AsReal();
            ThrowIfNaN(leftReal);
            ThrowIfNaN(rightReal);
            return leftReal.CompareTo(rightReal);
        }

        var integer = left.Kind == SqlValueKind.Integer ? left.AsInteger() : right.AsInteger();
        var real = left.Kind == SqlValueKind.Real ? left.AsReal() : right.AsReal();
        var result = CompareIntegerToReal(integer, real);
        return left.Kind == SqlValueKind.Integer ? result : -result;
    }

    private static int CompareIntegerToReal(long integer, double real)
    {
        ThrowIfNaN(real);

        // These boundaries are exactly representable doubles. The positive
        // boundary is one past Int64.MaxValue.
        const double MinimumInt64 = -9_223_372_036_854_775_808d;
        const double OnePastMaximumInt64 = 9_223_372_036_854_775_808d;
        if (real < MinimumInt64)
            return 1;
        if (real >= OnePastMaximumInt64)
            return -1;

        var truncated = (long)real;
        var comparison = integer.CompareTo(truncated);
        if (comparison != 0 || real == truncated)
            return comparison;

        return real > 0 ? -1 : 1;
    }

    private static void ThrowIfNaN(double value)
    {
        if (double.IsNaN(value))
            throw new InvalidDataException("SQLite index records containing NaN are not supported.");
    }

    private static int CompareBinary(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var count = Math.Min(left.Length, right.Length);
        for (var index = 0; index < count; index++)
        {
            var comparison = left[index].CompareTo(right[index]);
            if (comparison != 0)
                return comparison;
        }

        return left.Length.CompareTo(right.Length);
    }

    private static Encoding GetTextEncoding(SqliteTextEncoding textEncoding)
    {
        return textEncoding switch
        {
            SqliteTextEncoding.Utf8 => StrictUtf8,
            SqliteTextEncoding.Utf16LittleEndian => StrictUtf16LittleEndian,
            SqliteTextEncoding.Utf16BigEndian => StrictUtf16BigEndian,
            _ => throw new ArgumentOutOfRangeException(
                nameof(textEncoding),
                textEncoding,
                "SQLite index records require a concrete supported text encoding."),
        };
    }

    private enum StorageClass
    {
        Null,
        Numeric,
        Text,
        Blob,
    }
}
