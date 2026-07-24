namespace Turso;

internal enum TransactionCompletionKind
{
    None,
    Commit,
    Rollback,
}

internal static class TransactionSqlParser
{
    internal static TransactionCompletionKind GetCompletionKind(string sql)
    {
        var span = sql.AsSpan();
        var index = 0;
        if (!TryReadKeyword(span, ref index, out var command))
            return TransactionCompletionKind.None;
        if (command.Equals("COMMIT", StringComparison.OrdinalIgnoreCase)
            || command.Equals("END", StringComparison.OrdinalIgnoreCase))
        {
            return TransactionCompletionKind.Commit;
        }
        if (!command.Equals("ROLLBACK", StringComparison.OrdinalIgnoreCase))
            return TransactionCompletionKind.None;

        if (!TryReadKeyword(span, ref index, out var next))
            return TransactionCompletionKind.Rollback;
        if (next.Equals("TRANSACTION", StringComparison.OrdinalIgnoreCase)
            && !TryReadKeyword(span, ref index, out next))
        {
            return TransactionCompletionKind.Rollback;
        }

        return next.Equals("TO", StringComparison.OrdinalIgnoreCase)
            ? TransactionCompletionKind.None
            : TransactionCompletionKind.Rollback;
    }

    private static bool TryReadKeyword(
        ReadOnlySpan<char> sql,
        ref int index,
        out ReadOnlySpan<char> keyword)
    {
        SkipTrivia(sql, ref index);
        var start = index;
        while (index < sql.Length && IsIdentifierPart(sql[index]))
            index++;

        keyword = sql[start..index];
        return index != start;
    }

    private static void SkipTrivia(ReadOnlySpan<char> sql, ref int index)
    {
        while (true)
        {
            while (index < sql.Length && char.IsWhiteSpace(sql[index]))
                index++;

            if (index + 1 < sql.Length && sql[index] == '-' && sql[index + 1] == '-')
            {
                index += 2;
                while (index < sql.Length && sql[index] is not '\r' and not '\n')
                    index++;
                continue;
            }

            if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < sql.Length && (sql[index] != '*' || sql[index + 1] != '/'))
                    index++;
                index = Math.Min(index + 2, sql.Length);
                continue;
            }

            return;
        }
    }

    private static bool IsIdentifierPart(char value)
        => char.IsLetterOrDigit(value) || value is '_' or '$';
}
