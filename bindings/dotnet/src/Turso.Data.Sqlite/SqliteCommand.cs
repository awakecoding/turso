using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Turso;
using Turso.Core;

namespace Turso.Data.Sqlite;

public class SqliteCommand : DbCommand
{
    private SqliteConnection? _connection;
    private SqliteTransaction? _transaction;
    private SqliteStatementAdapter? _statement;
    private string _commandText = string.Empty;
    private int _commandTimeout = 30;
    private bool _hasOpenReader;

    public SqliteCommand()
    {
    }

    public SqliteCommand(string? commandText)
    {
        CommandText = commandText;
    }

    public SqliteCommand(SqliteConnection? connection)
    {
        Connection = connection;
    }

    public SqliteCommand(string? commandText, SqliteConnection? connection)
        : this(commandText)
    {
        Connection = connection;
    }

    public SqliteCommand(string? commandText, SqliteConnection? connection, SqliteTransaction? transaction)
        : this(commandText, connection)
    {
        Transaction = transaction;
    }

    public SqliteCommand(string? commandText, SqliteConnection? connection, DbTransaction? transaction)
        : this(commandText, connection)
    {
        Transaction = transaction as SqliteTransaction
                      ?? (transaction is null ? null : throw new ArgumentException("Transaction must be a SqliteTransaction.", nameof(transaction)));
    }

    [AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set
        {
            ThrowIfReaderOpen(nameof(CommandText));
            _commandText = value ?? string.Empty;
        }
    }

    public override int CommandTimeout
    {
        get => _commandTimeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _commandTimeout = value;
        }
    }

    public override CommandType CommandType
    {
        get => CommandType.Text;
        set
        {
            if (value != CommandType.Text)
                throw new ArgumentException(Properties.Resources.InvalidCommandType(value));
        }
    }

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    public new SqliteConnection? Connection
    {
        get => _connection;
        set
        {
            ThrowIfReaderOpen(nameof(Connection));
            _connection = value;
            if (value is not null)
            {
                _commandTimeout = value.DefaultTimeout;
                _transaction ??= value.Transaction;
            }
        }
    }

    public new SqliteParameterCollection Parameters { get; } = new();

    public new SqliteTransaction? Transaction
    {
        get => _transaction;
        set
        {
            ThrowIfReaderOpen(nameof(Transaction));
            _transaction = value;
        }
    }

    protected override DbConnection? DbConnection
    {
        get => Connection;
        set => Connection = value as SqliteConnection
                            ?? (value is null ? null : throw new ArgumentException("Connection must be a SqliteConnection.", nameof(value)));
    }

    protected override DbParameterCollection DbParameterCollection => Parameters;

    protected override DbTransaction? DbTransaction
    {
        get => Transaction;
        set => Transaction = value as SqliteTransaction
                            ?? (value is null ? null : throw new ArgumentException("Transaction must be a SqliteTransaction.", nameof(value)));
    }

    public override void Cancel()
    {
    }

    public override int ExecuteNonQuery()
    {
        using var reader = Execute("ExecuteNonQuery");
        while (reader.Read())
        {
        }

        reader.Close();
        MarkTransactionCompletedExternally();

        return reader.RecordsAffected;
    }

    public override object? ExecuteScalar()
    {
        using var reader = Execute("ExecuteScalar");
        var result = reader.Read() ? reader.GetValue(0) : null;
        reader.Close();
        MarkTransactionCompletedExternally();
        return result;
    }

    public override void Prepare()
    {
        EnsureExecutable("Prepare");
        var statements = SplitStatements(CommandText);
        if (statements.Count != 1)
        {
            _statement?.Dispose();
            _statement = null;
            return;
        }

        SqliteStatementAdapter? preparedStatement = null;
        try
        {
            preparedStatement = PrepareSingleStatement(statements[0]);
            _statement?.Dispose();
            _statement = preparedStatement;
            preparedStatement = null;
        }
        catch (Exception ex) when (ex is TursoException or EmbeddedSqlException)
        {
            throw ToSqliteException(ex);
        }
        finally
        {
            preparedStatement?.Dispose();
        }
    }

    protected override DbParameter CreateDbParameter() => new SqliteParameter();

    public new SqliteDataReader ExecuteReader() => Execute("ExecuteReader");

    public new SqliteDataReader ExecuteReader(CommandBehavior behavior) => Execute("ExecuteReader", behavior);

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => Execute("ExecuteReader", behavior);

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        => CompleteAsync(ExecuteNonQuery, cancellationToken);

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        => CompleteAsync(ExecuteScalar, cancellationToken);

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        => CompleteAsync<DbDataReader>(() => Execute("ExecuteReader", behavior), cancellationToken);

    private static Task<T> CompleteAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<T>(cancellationToken);

        try
        {
            return Task.FromResult(operation());
        }
        catch (Exception exception)
        {
            return Task.FromException<T>(exception);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _statement?.Dispose();

        base.Dispose(disposing);
    }

    private SqliteDataReader Execute(string method, CommandBehavior behavior = CommandBehavior.Default)
    {
        EnsureExecutable(method);
        if (IsEmptyCommand(CommandText))
        {
            _hasOpenReader = true;
            return new SqliteDataReader(this, -1, behavior, CloseReader);
        }

        if (Connection?.HasOpenReader == true && IsWriteCommand(CommandText))
        {
            Thread.Sleep(TimeSpan.FromSeconds(CommandTimeout));
            throw new SqliteException(Properties.Resources.SqliteNativeError(5, "database is locked"), 5);
        }
        if (Connection?.IsReadOnly == true && IsWriteCommand(CommandText))
            throw new SqliteException(Properties.Resources.SqliteNativeError(8, "attempt to write a readonly database"), 8);

        var recordsAffected = 0;
        var statements = SplitStatements(CommandText);
        try
        {
            for (var i = 0; i < statements.Count; i++)
            {
                if (TryHandleFacadeStatement(statements[i], out var sql))
                    continue;

                var statement = PrepareSingleStatement(sql);
                if (statement.ColumnCount > 0)
                {
                    _hasOpenReader = true;
                    return new SqliteDataReader(this, statement, statements[i], statements.Skip(i + 1).ToList(), recordsAffected, behavior, CloseReader);
                }

                while (statement.Read())
                {
                }

                if (CountsRowsAffected(statements[i]))
                    recordsAffected += statement.RowsAffected;
                statement.Dispose();
            }
        }
        catch (Exception ex) when (ex is TursoException or EmbeddedSqlException)
        {
            throw ToSqliteException(ex);
        }
        _hasOpenReader = true;
        return new SqliteDataReader(this, recordsAffected, behavior, CloseReader);
    }

    private void ThrowIfReaderOpen(string property)
    {
        if (_hasOpenReader)
            throw new InvalidOperationException(Properties.Resources.SetRequiresNoOpenReader(property));
    }

    private void EnsureExecutable(string method)
    {
        if (_hasOpenReader)
            throw new InvalidOperationException(Properties.Resources.DataReaderOpen);
        if (Connection is null || Connection.State != ConnectionState.Open)
            throw new InvalidOperationException(Properties.Resources.CallRequiresOpenConnection(method));
        if (Transaction is { IsCompleted: true } or { WasRolledBackExternally: true })
            throw new InvalidOperationException(Properties.Resources.TransactionCompleted);
        if (Transaction is not null && !ReferenceEquals(Transaction.Connection, Connection))
            throw new InvalidOperationException(Properties.Resources.TransactionConnectionMismatch);

        var connectionTransaction = Connection.Transaction;
        if (connectionTransaction is null || ReferenceEquals(Transaction, connectionTransaction))
            return;
        if (connectionTransaction.IsCompleted)
            throw new InvalidOperationException(Properties.Resources.TransactionCompleted);
        if (!IsTransactionControlCommand(CommandText))
            throw new InvalidOperationException(Properties.Resources.TransactionRequired);
    }

    private void CloseReader()
    {
        _hasOpenReader = false;
    }

    private void MarkTransactionCompletedExternally()
    {
        if (IsTransactionControlCommand(CommandText))
            Connection?.Transaction?.MarkCompletedExternally(IsRollbackCommand(CommandText));
    }

    internal SqliteStatementAdapter PrepareSingleStatement(string sql)
    {
        var connection = Connection!;
        if (connection.IsManagedReadOnly)
            ManagedReadOnlySqlGuard.ThrowIfQueryOnlyIsDisabled(sql);
        sql = RewriteFacadeStatement(sql, connection);
        if (connection.IsManagedConnection)
        {
            IManagedStatementAdapter? managedStatement = null;
            try
            {
                managedStatement = connection.ManagedConnection.Prepare(sql);
                BindManagedParameters(managedStatement);

                var statement = SqliteStatementAdapter.FromManaged(managedStatement);
                managedStatement = null;
                return statement;
            }
            catch (EmbeddedSqlException ex)
            {
                throw ToSqliteException(ex, sql);
            }
            finally
            {
                managedStatement?.Dispose();
            }
        }

        SqliteStatementAdapter? nativeStatement = null;
        try
        {
            nativeStatement = SqliteStatementAdapter.FromNative(connection.NativeDatabase.PrepareStatement(sql));
            BindNativeParameters(nativeStatement);
            var statement = nativeStatement;
            nativeStatement = null;
            return statement;
        }
        catch (TursoException ex)
        {
            throw ToSqliteException(ex, sql);
        }
        finally
        {
            nativeStatement?.Dispose();
        }
    }

    private void BindNativeParameters(SqliteStatementAdapter statement)
    {
        var parameterCount = statement.NativeParameterCount;
        var boundParameters = new bool[parameterCount + 1];

        for (var i = 0; i < Parameters.Count; i++)
        {
            var parameter = Parameters[i];
            if (string.IsNullOrEmpty(parameter.ParameterName))
                throw new InvalidOperationException(Properties.Resources.RequiresSet(nameof(parameter.ParameterName)));
            if (!parameter.HasValue)
                throw new InvalidOperationException(Properties.Resources.RequiresSet(nameof(parameter.Value)));

            var parameterIndex = FindNativeParameterIndex(statement, parameter.ParameterName, parameterCount);
            if (parameterIndex == 0)
                continue;

            statement.BindNative(parameterIndex, parameter.ToNativeValue());
            boundParameters[parameterIndex] = true;
        }

        for (var i = 1; i <= parameterCount; i++)
        {
            if (!boundParameters[i])
            {
                var parameterName = statement.GetNativeParameterName(i);
                throw new InvalidOperationException(
                    parameterName is null
                        ? Properties.Resources.MissingParameters(i)
                        : Properties.Resources.MissingParameters(parameterName));
            }
        }
    }

    private void BindManagedParameters(IManagedStatementAdapter statement)
    {
        var parameterMetadata = statement.ParameterMetadata;
        var parameterCount = parameterMetadata.Count;
        var boundParameters = new bool[parameterCount + 1];
        var statementParameterNames = new string?[parameterCount + 1];
        var highestNumberedParameterIndex = 0;
        for (var i = 1; i <= parameterCount; i++)
        {
            var parameterName = parameterMetadata.GetParameter(i).Name;
            statementParameterNames[i] = parameterName;
            if (IsNumberedParameterName(parameterName, i))
                highestNumberedParameterIndex = i;
        }

        for (var i = 1; i < highestNumberedParameterIndex; i++)
        {
            if (statementParameterNames[i] is null)
            {
                throw new NotSupportedException(
                    "Numbered parameters with gaps or preceding unnamed parameters are not supported by Local Provider=Managed.");
            }
        }

        List<SqliteParameter>? positionalParameters = null;

        for (var i = 0; i < Parameters.Count; i++)
        {
            var parameter = Parameters[i];
            if (!parameter.HasValue)
                throw new InvalidOperationException(Properties.Resources.RequiresSet(nameof(parameter.Value)));

            if (string.IsNullOrEmpty(parameter.ParameterName))
            {
                (positionalParameters ??= []).Add(parameter);
                continue;
            }

            var parameterIndex = IsNumberedParameterName(parameter.ParameterName)
                ? parameterMetadata.GetParameterIndex(parameter.ParameterName)
                : FindManagedParameterIndex(parameterMetadata, parameter.ParameterName);
            if (parameterIndex == 0)
                continue;

            statement.Bind(parameterIndex, parameter.ToSqlValue());
            boundParameters[parameterIndex] = true;
        }

        var positionalParameterIndex = 0;
        for (var statementParameterIndex = 1; statementParameterIndex <= parameterCount; statementParameterIndex++)
        {
            if (statementParameterNames[statementParameterIndex] is not null)
                continue;
            if (positionalParameters is null || positionalParameterIndex == positionalParameters.Count)
                continue;

            var parameter = positionalParameters[positionalParameterIndex++];
            statement.Bind(statementParameterIndex, parameter.ToSqlValue());
            boundParameters[statementParameterIndex] = true;
        }

        if (positionalParameters is not null && positionalParameterIndex != positionalParameters.Count)
        {
            throw new InvalidOperationException(
                Properties.Resources.ParameterNotFound($"at position {positionalParameterIndex + 1}"));
        }

        for (var i = 1; i <= parameterCount; i++)
        {
            if (!boundParameters[i])
            {
                var parameterName = statementParameterNames[i];
                throw new InvalidOperationException(
                    parameterName is null
                        ? Properties.Resources.MissingParameters(i)
                        : Properties.Resources.MissingParameters(parameterName));
            }
        }
    }

    private static bool IsEmptyCommand(string commandText)
    {
        foreach (var line in commandText.Split('\n'))
        {
            var trimmedLine = line.Trim();
            if (trimmedLine.Length != 0 && !trimmedLine.StartsWith("--", StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static bool IsTransactionControlCommand(string commandText)
    {
        var trimmed = commandText.TrimStart();
        return IsRollbackCommand(trimmed) || IsCommitCommand(trimmed);
    }

    private static bool IsRollbackCommand(string commandText)
    {
        var tail = GetCommandTail(commandText, "ROLLBACK");
        return tail is not null
               && !tail.StartsWith("TO", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCommitCommand(string commandText)
        => GetCommandTail(commandText, "COMMIT") is not null;

    private static string? GetCommandTail(string commandText, string command)
    {
        var trimmed = commandText.TrimStart();
        if (!trimmed.StartsWith(command, StringComparison.OrdinalIgnoreCase))
            return null;
        if (trimmed.Length > command.Length && char.IsLetterOrDigit(trimmed[command.Length]))
            return null;

        return trimmed[command.Length..].TrimStart();
    }

    private static bool IsWriteCommand(string commandText)
        => SplitStatements(commandText).Any(IsWriteStatement);

    private static bool IsWriteStatement(string statement)
    {
        var trimmed = statement.TrimStart();
        return trimmed.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("DROP", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("ALTER", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("VACUUM", StringComparison.OrdinalIgnoreCase)
               || IsWithDmlStatement(trimmed);
    }

    internal bool TryHandleFacadeStatement(string sql, out string rewrittenSql)
    {
        var connection = Connection!;
        var normalized = NormalizeSql(sql);
        if (TryParseReadUncommittedSetter(normalized, out var enabled))
        {
            connection.ReadUncommitted = enabled;
            rewrittenSql = EmptyResultSql;
            return true;
        }

        rewrittenSql = RewriteUnsupportedPragmas(normalized, sql, connection);
        return false;
    }

    private const string EmptyResultSql = "SELECT 1 WHERE 0";

    private static string RewriteFacadeStatement(string sql, SqliteConnection connection)
        => RewriteUnsupportedPragmas(NormalizeSql(sql), sql, connection);

    private static string RewriteUnsupportedPragmas(string normalized, string sql, SqliteConnection connection)
    {
        if (normalized.Equals("PRAGMA recursive_triggers", StringComparison.OrdinalIgnoreCase))
            return "SELECT " + (connection.RecursiveTriggers ? "1" : "0");
        if (TryParseReadUncommittedSetter(normalized, out _))
            return EmptyResultSql;
        if (normalized.Equals("PRAGMA read_uncommitted", StringComparison.OrdinalIgnoreCase))
            return "SELECT " + (connection.ReadUncommitted ? "1" : "0");
        if (normalized.Equals("PRAGMA compile_options", StringComparison.OrdinalIgnoreCase))
            return "SELECT CAST(NULL AS TEXT) AS compile_options WHERE 0";
        if (normalized.IndexOf("pragma_compile_options", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return normalized.IndexOf("count", StringComparison.OrdinalIgnoreCase) >= 0
                ? "SELECT 0"
                : "SELECT CAST(NULL AS TEXT) AS compile_options WHERE 0";
        }

        return sql;
    }

    private static string NormalizeSql(string sql)
        => sql.Trim().TrimEnd(';').Trim();

    private static bool TryParseReadUncommittedSetter(string normalized, out bool enabled)
    {
        enabled = false;
        const string prefix = "PRAGMA read_uncommitted";
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        if (normalized.Length == prefix.Length)
            return false;

        var value = normalized[prefix.Length..].TrimStart();
        if (value.StartsWith("=", StringComparison.Ordinal))
            value = value[1..].Trim();
        else if (value.StartsWith("(", StringComparison.Ordinal) && value.EndsWith(")", StringComparison.Ordinal))
            value = value[1..^1].Trim();
        else
            return false;

        enabled = ParsePragmaEnabled(value);
        return true;
    }

    private static bool ParsePragmaEnabled(string value)
    {
        value = value.Trim('\'', '"');
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number != 0
            : value.Equals("ON", StringComparison.OrdinalIgnoreCase)
              || value.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
              || value.Equals("YES", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool CountsRowsAffected(string commandText)
    {
        var firstStatement = SplitStatements(commandText).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstStatement))
            return false;

        var trimmed = firstStatement.TrimStart();
        return trimmed.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase)
               || IsWithDmlStatement(trimmed);
    }

    private static bool IsWithDmlStatement(string trimmedStatement)
        => trimmedStatement.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)
           && Regex.IsMatch(trimmedStatement, @"\)\s*(INSERT|UPDATE|DELETE|REPLACE)\b", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static List<string> SplitStatements(string commandText)
    {
        var statements = new List<string>();
        var start = 0;
        var firstTokenInStatement = true;
        var triggerHeader = TriggerHeader.None;
        var triggerBlockDepth = 0;
        var triggerBodyAtStatementStart = false;
        var offset = 0;

        while (TryReadScriptToken(commandText, ref offset, out var token))
        {
            if (token.Kind == ScriptTokenKind.Semicolon)
            {
                if (triggerBlockDepth > 0)
                {
                    triggerBodyAtStatementStart = true;
                }
                else
                {
                    AddStatement(commandText, start, token.Offset, statements);
                    start = token.Offset + token.Length;
                    firstTokenInStatement = true;
                    triggerHeader = TriggerHeader.None;
                }

                continue;
            }

            if (triggerBlockDepth > 0)
            {
                if (triggerBodyAtStatementStart)
                {
                    // Only complete trigger-body statements can close a trigger, so CASE ... END
                    // expressions and words inside strings never affect the outer boundary.
                    if (IsKeyword(commandText, token, "BEGIN"))
                        triggerBlockDepth++;
                    else if (IsKeyword(commandText, token, "END"))
                        triggerBlockDepth--;

                    triggerBodyAtStatementStart = false;
                }

                continue;
            }

            if (firstTokenInStatement)
            {
                firstTokenInStatement = false;
                triggerHeader = IsKeyword(commandText, token, "CREATE")
                    ? TriggerHeader.ExpectTrigger
                    : TriggerHeader.NotTrigger;
            }
            else
            {
                triggerHeader = AdvanceTriggerHeader(
                    commandText,
                    triggerHeader,
                    token,
                    ref triggerBlockDepth,
                    ref triggerBodyAtStatementStart);
            }
        }

        AddStatement(commandText, start, commandText.Length, statements);
        return statements;
    }

    private static TriggerHeader AdvanceTriggerHeader(
        string sql,
        TriggerHeader header,
        ScriptToken token,
        ref int triggerBlockDepth,
        ref bool triggerBodyAtStatementStart)
    {
        return header switch
        {
            TriggerHeader.ExpectTrigger => IsKeyword(sql, token, "TRIGGER")
                ? TriggerHeader.ExpectNameOrIf
                : TriggerHeader.NotTrigger,
            TriggerHeader.ExpectNameOrIf => IsKeyword(sql, token, "IF")
                ? TriggerHeader.ExpectNot
                : IsIdentifier(token)
                    ? TriggerHeader.ExpectAfter
                    : TriggerHeader.NotTrigger,
            TriggerHeader.ExpectNot => IsKeyword(sql, token, "NOT")
                ? TriggerHeader.ExpectExists
                : TriggerHeader.NotTrigger,
            TriggerHeader.ExpectExists => IsKeyword(sql, token, "EXISTS")
                ? TriggerHeader.ExpectName
                : TriggerHeader.NotTrigger,
            TriggerHeader.ExpectName => IsIdentifier(token)
                ? TriggerHeader.ExpectAfter
                : TriggerHeader.NotTrigger,
            TriggerHeader.ExpectAfter => IsKeyword(sql, token, "AFTER")
                ? TriggerHeader.ExpectEvent
                : TriggerHeader.NotTrigger,
            TriggerHeader.ExpectEvent => IsKeyword(sql, token, "INSERT")
                || IsKeyword(sql, token, "UPDATE")
                || IsKeyword(sql, token, "DELETE")
                    ? TriggerHeader.ExpectOn
                    : TriggerHeader.NotTrigger,
            TriggerHeader.ExpectOn => IsKeyword(sql, token, "ON")
                ? TriggerHeader.ExpectTable
                : TriggerHeader.NotTrigger,
            TriggerHeader.ExpectTable => IsIdentifier(token)
                ? TriggerHeader.ExpectBegin
                : TriggerHeader.NotTrigger,
            TriggerHeader.ExpectBegin => EnterTriggerBody(sql, token, ref triggerBlockDepth, ref triggerBodyAtStatementStart),
            _ => header,
        };
    }

    private static TriggerHeader EnterTriggerBody(
        string sql,
        ScriptToken token,
        ref int triggerBlockDepth,
        ref bool triggerBodyAtStatementStart)
    {
        if (!IsKeyword(sql, token, "BEGIN"))
            return TriggerHeader.NotTrigger;

        triggerBlockDepth = 1;
        triggerBodyAtStatementStart = true;
        return TriggerHeader.None;
    }

    private static bool IsKeyword(string sql, ScriptToken token, string keyword)
        => token.Kind == ScriptTokenKind.Identifier
           && token.Length == keyword.Length
           && sql.AsSpan(token.Offset, token.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase);

    private static bool IsIdentifier(ScriptToken token)
        => token.Kind is ScriptTokenKind.Identifier or ScriptTokenKind.QuotedIdentifier;

    private static bool TryReadScriptToken(string sql, ref int offset, out ScriptToken token)
    {
        SkipWhitespaceAndComments(sql, ref offset, out var unterminatedComment);
        if (unterminatedComment)
        {
            token = new ScriptToken(ScriptTokenKind.Malformed, offset, 0);
            return true;
        }

        if (offset == sql.Length)
        {
            token = default;
            return false;
        }

        var start = offset;
        var current = sql[offset++];
        switch (current)
        {
            case ';':
                token = new ScriptToken(ScriptTokenKind.Semicolon, start, 1);
                return true;
            case '\'':
                token = new ScriptToken(
                    ReadDelimitedToken(sql, ref offset, '\'')
                        ? ScriptTokenKind.Other
                        : ScriptTokenKind.Malformed,
                    start,
                    offset - start);
                return true;
            case '"':
            case '[':
            case '`':
                var closingCharacter = current == '[' ? ']' : current;
                token = new ScriptToken(
                    ReadDelimitedToken(sql, ref offset, closingCharacter)
                        ? ScriptTokenKind.QuotedIdentifier
                        : ScriptTokenKind.Malformed,
                    start,
                    offset - start);
                return true;
            default:
                if (IsIdentifierStart(current))
                {
                    while (offset < sql.Length && IsIdentifierContinuation(sql[offset]))
                        offset++;

                    token = new ScriptToken(ScriptTokenKind.Identifier, start, offset - start);
                    return true;
                }

                token = new ScriptToken(ScriptTokenKind.Other, start, 1);
                return true;
        }
    }

    private static void SkipWhitespaceAndComments(string sql, ref int offset, out bool unterminatedComment)
    {
        unterminatedComment = false;
        while (offset < sql.Length)
        {
            if (char.IsWhiteSpace(sql[offset]))
            {
                offset++;
                continue;
            }

            if (offset + 1 < sql.Length && sql[offset] == '-' && sql[offset + 1] == '-')
            {
                offset += 2;
                while (offset < sql.Length && sql[offset] is not '\r' and not '\n')
                    offset++;
                continue;
            }

            if (offset + 1 < sql.Length && sql[offset] == '/' && sql[offset + 1] == '*')
            {
                offset += 2;
                while (offset + 1 < sql.Length && (sql[offset] != '*' || sql[offset + 1] != '/'))
                    offset++;

                if (offset + 1 >= sql.Length)
                {
                    offset = sql.Length;
                    unterminatedComment = true;
                    return;
                }

                offset += 2;
                continue;
            }

            return;
        }
    }

    private static bool ReadDelimitedToken(string sql, ref int offset, char closingCharacter)
    {
        while (offset < sql.Length)
        {
            if (sql[offset++] != closingCharacter)
                continue;

            if (offset < sql.Length && sql[offset] == closingCharacter)
            {
                offset++;
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool IsIdentifierStart(char value)
        => value is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or '_';

    private static bool IsIdentifierContinuation(char value)
        => IsIdentifierStart(value)
            || value is >= '0' and <= '9'
            or '$';

    private static void AddStatement(string sql, int start, int end, List<string> statements)
    {
        var statement = sql[start..end].Trim();
        var offset = 0;
        if (statement.Length != 0 && TryReadScriptToken(statement, ref offset, out _))
            statements.Add(statement);
    }

    private enum TriggerHeader
    {
        None,
        NotTrigger,
        ExpectTrigger,
        ExpectNameOrIf,
        ExpectNot,
        ExpectExists,
        ExpectName,
        ExpectAfter,
        ExpectEvent,
        ExpectOn,
        ExpectTable,
        ExpectBegin,
    }

    private enum ScriptTokenKind
    {
        Identifier,
        QuotedIdentifier,
        Semicolon,
        Other,
        Malformed,
    }

    private readonly record struct ScriptToken(ScriptTokenKind Kind, int Offset, int Length);

    internal static SqliteException ToSqliteException(Exception ex, string? sql = null)
    {
        var message = ex.Message;
        foreach (var prefix in new[] { "Unable to prepare statement: Parse error: ", "Parse error: " })
        {
            if (message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                message = message[prefix.Length..];
                break;
            }
        }
        if (message.StartsWith("Extension error: ", StringComparison.OrdinalIgnoreCase))
            message = message["Extension error: ".Length..];
        if (message.StartsWith("Error: cannot use aggregate, window functions or reference other tables in WHERE clause of CREATE INDEX", StringComparison.Ordinal))
            message = "non-deterministic functions prohibited in partial index WHERE clauses";
        const string sqliteErrorPrefix = "__turso_sqlite_error__:";
        if (message.StartsWith(sqliteErrorPrefix, StringComparison.Ordinal))
        {
            var codeEnd = message.IndexOf(':', sqliteErrorPrefix.Length);
            if (codeEnd > sqliteErrorPrefix.Length
                && int.TryParse(message[sqliteErrorPrefix.Length..codeEnd], NumberStyles.Integer, CultureInfo.InvariantCulture, out var errorCode))
            {
                var sqliteMessage = message[(codeEnd + 1)..];
                return new SqliteException(Properties.Resources.SqliteNativeError(errorCode, sqliteMessage), errorCode);
            }
        }

        if (sql is not null)
            message = PreserveNoSuchTableCase(message, sql);

        return new SqliteException(Properties.Resources.SqliteNativeError(1, message), 1);
    }

    private static string PreserveNoSuchTableCase(string message, string sql)
    {
        const string noSuchTable = "no such table: ";
        if (!message.StartsWith(noSuchTable, StringComparison.OrdinalIgnoreCase))
            return message;

        var tableName = message[noSuchTable.Length..];
        var sqlSpan = sql.AsSpan();
        for (var i = 0; i <= sqlSpan.Length - tableName.Length; i++)
        {
            if (MemoryExtensions.Equals(sqlSpan.Slice(i, tableName.Length), tableName, StringComparison.OrdinalIgnoreCase))
                return noSuchTable + sql.Substring(i, tableName.Length);
        }

        return message;
    }

    private static int FindNativeParameterIndex(SqliteStatementAdapter statement, string parameterName, int parameterCount)
    {
        var index = FindExactNativeParameterIndex(statement, parameterName, parameterCount);
        if (index != 0 || IsPrefixed(parameterName))
            return index;

        foreach (var prefix in new[] { '@', '$', ':' })
        {
            var prefixedIndex = FindExactNativeParameterIndex(statement, prefix + parameterName, parameterCount);
            if (prefixedIndex == 0)
                continue;

            if (index != 0)
                throw new InvalidOperationException(Properties.Resources.AmbiguousParameterName(parameterName));

            index = prefixedIndex;
        }

        return index;
    }

    private static int FindExactNativeParameterIndex(SqliteStatementAdapter statement, string parameterName, int parameterCount)
    {
        for (var i = 1; i <= parameterCount; i++)
        {
            if (string.Equals(statement.GetNativeParameterName(i), parameterName, StringComparison.Ordinal))
                return i;
        }

        return 0;
    }

    private static int FindManagedParameterIndex(ManagedParameterMetadata parameterMetadata, string parameterName)
    {
        var index = FindExactManagedParameterIndex(parameterMetadata, parameterName);
        if (index != 0 || IsPrefixed(parameterName))
            return index;

        foreach (var prefix in new[] { '@', '$', ':' })
        {
            var prefixedIndex = FindExactManagedParameterIndex(parameterMetadata, prefix + parameterName);
            if (prefixedIndex == 0)
                continue;

            if (index != 0)
                throw new InvalidOperationException(Properties.Resources.AmbiguousParameterName(parameterName));

            index = prefixedIndex;
        }

        return index;
    }

    private static int FindExactManagedParameterIndex(ManagedParameterMetadata parameterMetadata, string parameterName)
    {
        for (var i = 1; i <= parameterMetadata.Count; i++)
        {
            if (string.Equals(parameterMetadata.GetParameter(i).Name, parameterName, StringComparison.Ordinal))
                return i;
        }

        return 0;
    }

    private static bool IsPrefixed(string parameterName)
        => parameterName.Length > 0 && parameterName[0] is '@' or '$' or ':';

    private static bool IsNumberedParameterName(string? parameterName, int? expectedIndex = null)
        => parameterName is { Length: > 1 }
           && parameterName[0] == '?'
           && int.TryParse(parameterName.AsSpan(1), out var index)
           && index > 0
           && (expectedIndex is null || index == expectedIndex);
}
