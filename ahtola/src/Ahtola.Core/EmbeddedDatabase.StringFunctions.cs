using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ahtola.Core;

public sealed partial class EmbeddedDatabase
{
    private static readonly char[] DefaultTrimCharacters = [' '];

    /// <summary>
    /// Applies SQLite's rule that a scalar function returns NULL as soon as any
    /// argument that participates in the result is NULL.
    /// </summary>
    private static bool HasNullArgument(IReadOnlyList<SqlValue> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].Kind == SqlValueKind.Null)
                return true;
        }

        return false;
    }

    private static void RequireArgumentCount(
        string functionName,
        IReadOnlyList<SqlValue> arguments,
        int minimum,
        int maximum)
    {
        if (arguments.Count < minimum || arguments.Count > maximum)
            throw new EmbeddedSqlException($"wrong number of arguments to function {functionName}()");
    }

    /// <summary>
    /// SQLite's substr() counts in bytes for blobs and in characters for text.
    /// Returning the operand as a blob preserves that distinction for callers.
    /// </summary>
    private static SqlValue EvaluateSubstring(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("substr", arguments, 2, 3);
        if (HasNullArgument(arguments))
            return SqlValue.Null;

        var isBlob = arguments[0].Kind == SqlValueKind.Blob;
        var length = isBlob ? arguments[0].AsBlob().Length : ToSqlText(arguments[0]).Length;
        var start = ToSqliteInteger(arguments[1]);
        var hasExplicitLength = arguments.Count == 3;
        var requested = hasExplicitLength ? ToSqliteInteger(arguments[2]) : length;

        // SQLite indexes from 1; a negative start counts back from the end, and a
        // negative length selects the characters *preceding* the start position.
        if (start < 0)
        {
            start = length + start + 1;
            if (start < 1 && !hasExplicitLength)
                start = 1;
        }
        else if (start == 0)
        {
            start = 1;
            requested = hasExplicitLength ? requested - 1 : requested;
        }

        if (requested < 0)
        {
            start += requested;
            requested = -requested;
        }

        var beginIndex = start - 1;
        var endIndex = beginIndex + requested;
        if (beginIndex < 0)
            beginIndex = 0;
        if (endIndex > length)
            endIndex = length;

        var take = endIndex - beginIndex;
        if (take <= 0 || beginIndex >= length)
            return isBlob ? SqlValue.Blob([]) : SqlValue.Text(string.Empty);

        if (isBlob)
            return SqlValue.Blob(arguments[0].AsBlob().Span.Slice((int)beginIndex, (int)take).ToArray());

        return SqlValue.Text(ToSqlText(arguments[0]).Substring((int)beginIndex, (int)take));
    }

    private static SqlValue EvaluateReplace(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("replace", arguments, 3);
        if (HasNullArgument(arguments))
            return SqlValue.Null;

        var source = ToSqlText(arguments[0]);
        var pattern = ToSqlText(arguments[1]);
        if (pattern.Length == 0)
            return SqlValue.Text(source);

        return SqlValue.Text(source.Replace(pattern, ToSqlText(arguments[2]), StringComparison.Ordinal));
    }

    private static SqlValue EvaluateTrim(IReadOnlyList<SqlValue> arguments, string functionName, bool trimStart, bool trimEnd)
    {
        RequireArgumentCount(functionName, arguments, 1, 2);
        if (HasNullArgument(arguments))
            return SqlValue.Null;

        var source = ToSqlText(arguments[0]);
        var characters = arguments.Count == 2
            ? ToSqlText(arguments[1]).ToCharArray()
            : DefaultTrimCharacters;

        if (characters.Length == 0)
            return SqlValue.Text(source);

        var result = trimStart && trimEnd
            ? source.Trim(characters)
            : trimStart
                ? source.TrimStart(characters)
                : source.TrimEnd(characters);

        return SqlValue.Text(result);
    }

    /// <summary>
    /// Renders a value as an SQL literal, matching SQLite's quote() output for
    /// each storage class.
    /// </summary>
    private static SqlValue EvaluateQuote(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("quote", arguments, 1);
        var value = arguments[0];
        return value.Kind switch
        {
            SqlValueKind.Null => SqlValue.Text("NULL"),
            SqlValueKind.Integer => SqlValue.Text(value.AsInteger().ToString(CultureInfo.InvariantCulture)),
            SqlValueKind.Real => SqlValue.Text(FormatQuotedReal(value.AsReal())),
            SqlValueKind.Blob => SqlValue.Text($"X'{Convert.ToHexString(value.AsBlob().Span)}'"),
            _ => SqlValue.Text($"'{value.AsText().Replace("'", "''", StringComparison.Ordinal)}'"),
        };
    }

    private static string FormatQuotedReal(double value)
    {
        if (double.IsNaN(value))
            return "NULL";
        if (double.IsPositiveInfinity(value))
            return "9.0e+999";
        if (double.IsNegativeInfinity(value))
            return "-9.0e+999";

        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static SqlValue EvaluateChar(IReadOnlyList<SqlValue> arguments)
    {
        var builder = new StringBuilder(arguments.Count);
        foreach (var argument in arguments)
        {
            // SQLite reads every argument with sqlite3_value_int64, which yields 0 for NULL and
            // for text that is not a number, so char(NULL) is a NUL character and not a skipped
            // argument.
            var codePoint = argument.Kind == SqlValueKind.Null ? 0 : ToSqliteInteger(argument);

            // SQLite substitutes U+FFFD for values outside the Unicode range and
            // for surrogate code points, which cannot stand alone.
            if (codePoint is < 0 or > 0x10FFFF || (codePoint >= 0xD800 && codePoint <= 0xDFFF))
            {
                builder.Append('\uFFFD');
                continue;
            }

            builder.Append(char.ConvertFromUtf32((int)codePoint));
        }

        return SqlValue.Text(builder.ToString());
    }

    private static SqlValue EvaluateUnicode(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("unicode", arguments, 1);
        if (arguments[0].Kind == SqlValueKind.Null)
            return SqlValue.Null;

        var text = ToSqlText(arguments[0]);
        if (text.Length == 0)
            return SqlValue.Null;

        return SqlValue.Integer(char.ConvertToUtf32(text, 0));
    }

    private static SqlValue EvaluateUnhex(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("unhex", arguments, 1, 2);
        if (HasNullArgument(arguments))
            return SqlValue.Null;

        var text = ToSqlText(arguments[0]);
        var ignored = arguments.Count == 2 ? ToSqlText(arguments[1]) : string.Empty;
        if (ignored.Length > 0)
            text = text.Trim(ignored.ToCharArray());

        if ((text.Length & 1) != 0)
            return SqlValue.Null;

        var bytes = new byte[text.Length / 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            var high = ParseHexDigit(text[index * 2]);
            var low = ParseHexDigit(text[(index * 2) + 1]);
            if (high < 0 || low < 0)
                return SqlValue.Null;

            bytes[index] = (byte)((high << 4) | low);
        }

        return SqlValue.Blob(bytes);
    }

    private static int ParseHexDigit(char character)
        => character switch
        {
            >= '0' and <= '9' => character - '0',
            >= 'a' and <= 'f' => character - 'a' + 10,
            >= 'A' and <= 'F' => character - 'A' + 10,
            _ => -1,
        };

    private static SqlValue EvaluateZeroBlob(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("zeroblob", arguments, 1);
        if (arguments[0].Kind == SqlValueKind.Null)
            return SqlValue.Null;

        var length = ToSqliteLength(arguments[0]);
        if (length < 0)
            length = 0;
        if (length > int.MaxValue)
            throw new EmbeddedSqlException("string or blob too big");

        return SqlValue.Blob(new byte[(int)length]);
    }

    private static SqlValue EvaluateRandomBlob(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("randomblob", arguments, 1);
        if (arguments[0].Kind == SqlValueKind.Null)
            return SqlValue.Null;

        var length = ToSqliteLength(arguments[0]);
        if (length < 1)
            length = 1;
        if (length > int.MaxValue)
            throw new EmbeddedSqlException("string or blob too big");

        var bytes = new byte[(int)length];
        RandomNumberGenerator.Fill(bytes);
        return SqlValue.Blob(bytes);
    }

    private static SqlValue EvaluateRandom(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("random", arguments, 0);
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return SqlValue.Integer(BitConverter.ToInt64(bytes));
    }

    private static SqlValue EvaluateConcat(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count == 0)
            throw new EmbeddedSqlException("wrong number of arguments to function concat()");

        var builder = new StringBuilder();
        foreach (var argument in arguments)
        {
            // concat() ignores NULL arguments rather than propagating them.
            if (argument.Kind == SqlValueKind.Null)
                continue;

            builder.Append(ToSqlText(argument));
        }

        return SqlValue.Text(builder.ToString());
    }

    private static SqlValue EvaluateConcatWithSeparator(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count == 0)
            throw new EmbeddedSqlException("wrong number of arguments to function concat_ws()");
        if (arguments[0].Kind == SqlValueKind.Null)
            return SqlValue.Null;

        var separator = ToSqlText(arguments[0]);
        var builder = new StringBuilder();
        var appended = false;
        for (var index = 1; index < arguments.Count; index++)
        {
            if (arguments[index].Kind == SqlValueKind.Null)
                continue;

            if (appended)
                builder.Append(separator);

            builder.Append(ToSqlText(arguments[index]));
            appended = true;
        }

        return SqlValue.Text(builder.ToString());
    }

    /// <summary>
    /// Coerces a value to an integer, saturating instead of wrapping so that
    /// length-style arguments cannot overflow into a negative size.
    /// </summary>
    private static long ToSqliteLength(SqlValue value)
    {
        var numeric = ApplyNumericAffinity(value);
        return numeric.Kind switch
        {
            SqlValueKind.Integer => numeric.AsInteger(),
            SqlValueKind.Real => ClampRealToInteger(numeric.AsReal()),
            _ => 0,
        };
    }

    private static long ClampRealToInteger(double value)
    {
        if (double.IsNaN(value))
            return 0;

        var truncated = Math.Truncate(value);
        if (truncated >= long.MaxValue)
            return long.MaxValue;
        if (truncated <= long.MinValue)
            return long.MinValue;

        return (long)truncated;
    }
}
