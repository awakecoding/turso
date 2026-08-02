namespace Ahtola.Core;

/// <summary>
/// The function names the managed engine implements itself, independent of connection-registered
/// callbacks. Persisted file schema (views, triggers) may reference these: the stored CREATE SQL
/// re-resolves to the same engine implementations after reopen. The names must mirror the
/// evaluator dispatch in <see cref="EmbeddedDatabase"/> (the scalar-function switch,
/// IsBuiltInAggregate, the managed percentile aggregates, and ValidateWindowFunction); tests pin
/// the two against each other.
/// </summary>
internal static class SqliteBuiltinFunctions
{
    private static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        // Built-in aggregates (IsBuiltInAggregate) and managed percentile aggregates.
        "COUNT", "SUM", "TOTAL", "AVG", "MIN", "MAX", "GROUP_CONCAT", "STRING_AGG",
        "JSON_GROUP_ARRAY", "JSON_GROUP_OBJECT",
        "MEDIAN", "PERCENTILE", "PERCENTILE_CONT", "PERCENTILE_DISC",
        // Built-in window functions (ValidateWindowFunction).
        "ROW_NUMBER", "RANK", "DENSE_RANK", "PERCENT_RANK", "CUME_DIST", "NTILE",
        "LAG", "LEAD", "FIRST_VALUE", "LAST_VALUE", "NTH_VALUE",
        // Built-in scalars (the EvaluateScalarFunction dispatch).
        "ABS", "CEIL", "CEILING", "FLOOR", "TRUNC", "ROUND", "LN", "LOG", "LOG2", "LOG10",
        "EXP", "SQRT", "POW", "POWER", "MOD", "SIGN", "PI", "DEGREES", "RADIANS",
        "SIN", "COS", "TAN", "ASIN", "ACOS", "ATAN", "ATAN2", "SINH", "COSH", "TANH",
        "ASINH", "ACOSH", "ATANH",
        "SUBSTR", "SUBSTRING", "REPLACE", "TRIM", "BTRIM", "LTRIM", "RTRIM", "QUOTE",
        "CHAR", "UNICODE", "UNHEX", "ZEROBLOB", "RANDOMBLOB", "RANDOM", "CONCAT", "CONCAT_WS",
        "IIF", "LIKELY", "UNLIKELY", "LIKELIHOOD",
        "SQLITE_VERSION", "SQLITE_SOURCE_ID", "CHANGES", "TOTAL_CHANGES", "TIMEDIFF",
        "COALESCE", "DATE", "DATETIME", "GLOB", "HEX", "IFNULL", "INSTR",
        "JSON", "JSON_ARRAY", "JSON_ARRAY_LENGTH", "JSON_ERROR_POSITION", "JSON_EXTRACT",
        "JSON_INSERT", "JSON_OBJECT", "JSON_PATCH", "JSON_PRETTY", "JSON_QUOTE", "JSON_REMOVE",
        "JSON_REPLACE", "JSON_SET", "JSON_TYPE", "JSON_VALID", "JULIANDAY",
        "LAST_INSERT_ROWID", "LENGTH", "LIKE", "LOWER", "NULLIF", "FORMAT", "PRINTF",
        "STRFTIME", "TIME", "TYPEOF", "UNIXEPOCH", "UPPER",
        "UUID4_STR", "GEN_RANDOM_UUID", "UUID4", "UUID7_STR", "UUID7", "UUID7_TIMESTAMP_MS",
        "UUID_STR", "UUID_BLOB",
    };

    private static readonly HashSet<string> WindowOnlyNames = new(StringComparer.Ordinal)
    {
        "ROW_NUMBER", "RANK", "DENSE_RANK", "PERCENT_RANK", "CUME_DIST", "NTILE",
        "LAG", "LEAD", "FIRST_VALUE", "LAST_VALUE", "NTH_VALUE",
    };

    // Function names whose result can change between invocations even when the underlying
    // table data does not. Statement-scoped subquery memoization must never cache a query
    // that evaluates one of these. The date/time family is excluded wholesale because each
    // member accepts the 'now' time string. Maintained alongside Names.
    private static readonly HashSet<string> NonDeterministic = new(StringComparer.Ordinal)
    {
        "RANDOM",
        "RANDOMBLOB",
        "CHANGES",
        "TOTAL_CHANGES",
        "LAST_INSERT_ROWID",
        "DATE",
        "DATETIME",
        "TIME",
        "JULIANDAY",
        "STRFTIME",
        "UNIXEPOCH",
        "TIMEDIFF",
        "UUID4_STR",
        "GEN_RANDOM_UUID",
        "UUID4",
        "UUID7_STR",
        "UUID7",
        "UUID7_TIMESTAMP_MS",
        "UUID_STR",
        "UUID_BLOB",
    };

    public static bool Contains(string name)
        => Names.Contains(name.ToUpperInvariant());

    // A built-in is deterministic (memoizable) when it is a recognized built-in whose
    // result depends only on its arguments and the table data. Aggregates, percentiles,
    // and window functions qualify (they are stable over stable data); user-registered
    // functions and the non-deterministic set above do not.
    public static bool IsDeterministic(string name)
        => Names.Contains(name.ToUpperInvariant()) && !NonDeterministic.Contains(name.ToUpperInvariant());

    /// <summary>Window-only names error without an OVER clause; parity tests skip them.</summary>
    public static bool IsWindowOnly(string name)
        => WindowOnlyNames.Contains(name.ToUpperInvariant());

    /// <summary>Exposed for evaluator-dispatch parity tests.</summary>
    public static IReadOnlyCollection<string> All => Names;
}
