using System.Globalization;
using Ahtola.Core;

namespace Ahtola.Core.Parsing;

internal sealed class SqlParser
{
    private readonly SqlLexer _lexer;
    private readonly string _sql;
    private readonly Dictionary<string, int> _namedParameterIndices = new(StringComparer.Ordinal);
    private readonly SqlSourceSpans? _spans;
    private int _maximumParameterIndex;
    private bool _inTriggerBody;
    private (int Offset, int Length)? _temporaryKeywordSpan;
    private IReadOnlyList<SqlToken>? _pendingUpdateOfTokens;

    private SqlParser(string sql, SqlParameterMap parameterMap, SqlSourceSpans? spans = null)
    {
        _lexer = new SqlLexer(sql);
        _sql = sql;
        _spans = spans;
        for (var index = 1; index <= parameterMap.Count; index++)
        {
            var name = parameterMap.GetName(index);
            if (name is not null)
                _namedParameterIndices.TryAdd(name, index);
        }
    }

    public static ParsedStatement Parse(string sql, SqlParameterMap parameterMap)
    {
        var parser = new SqlParser(sql, parameterMap);
        var statement = parser.ParseStatement();
        parser.Consume(TokenKind.Semicolon);
        parser.Expect(TokenKind.End);
        return statement;
    }

    /// <summary>
    /// Parses a full statement while recording the source span of every identifier that can
    /// name a column, so <c>ALTER TABLE ... RENAME COLUMN</c> can edit the stored SQL text in
    /// place instead of re-rendering it from the parse tree.
    /// </summary>
    public static ParsedStatement ParseWithSpans(string sql, out SqlSourceSpans spans)
    {
        spans = new SqlSourceSpans();
        var parser = new SqlParser(sql, SqlParameterMap.Parse(sql), spans);
        var statement = parser.ParseStatement();
        parser.Consume(TokenKind.Semicolon);
        parser.Expect(TokenKind.End);
        return statement;
    }

    /// <summary>
    /// Parses a bare expression fragment (a CHECK body, a generated-column expression, an
    /// index key, or a partial-index predicate) with identifier spans recorded.
    /// </summary>
    public static Expression ParseExpressionWithSpans(string sql, out SqlSourceSpans spans)
    {
        spans = new SqlSourceSpans();
        var parser = new SqlParser(sql, SqlParameterMap.Parse(sql), spans);
        var expression = parser.ParseExpression();
        parser.Expect(TokenKind.End);
        return expression;
    }

    private ParsedStatement ParseStatement()
    {
        if (ConsumeKeyword("EXPLAIN"))
        {
            if (ConsumeKeyword("QUERY"))
            {
                ExpectKeyword("PLAN");
                return new ExplainQueryPlanStatement(ParseStatement());
            }

            return new ExplainStatement(ParseStatement());
        }

        if (ConsumeKeyword("CREATE"))
            return ParseCreate();
        if (ConsumeKeyword("DROP"))
            return ParseDrop();
        if (ConsumeKeyword("ALTER"))
            return ParseAlterTable();
        if (ConsumeKeyword("INSERT"))
            return ParseInsert();
        if (ConsumeKeyword("REPLACE"))
            return ParseInsert(InsertConflictAlgorithm.Replace);
        if (ConsumeKeyword("UPDATE"))
            return ParseUpdate();
        if (ConsumeKeyword("DELETE"))
            return ParseDelete();
        if (ConsumeKeyword("WITH"))
            return ParseWithStatement();
        if (ConsumeKeyword("PRAGMA"))
            return ParsePragma();
        if (ConsumeKeyword("ATTACH"))
            return ParseAttach();
        if (ConsumeKeyword("DETACH"))
            return ParseDetach();
        if (ConsumeKeyword("ANALYZE"))
            return new AnalyzeStatement(ParseOptionalMaintenanceTarget());
        if (ConsumeKeyword("REINDEX"))
            return new ReindexStatement(ParseOptionalMaintenanceTarget());
        if (ConsumeKeyword("VACUUM"))
            return ParseVacuum();
        if (IsQueryStart())
            return ParseQuery();
        if (ConsumeKeyword("BEGIN"))
        {
            // SQLite's grammar admits at most one mode keyword, and the mode decides
            // when the write lock is taken, so it cannot be discarded here.
            var mode = TransactionMode.Deferred;
            if (ConsumeKeyword("DEFERRED"))
                mode = TransactionMode.Deferred;
            else if (ConsumeKeyword("IMMEDIATE"))
                mode = TransactionMode.Immediate;
            else if (ConsumeKeyword("EXCLUSIVE"))
                mode = TransactionMode.Exclusive;

            ConsumeKeyword("TRANSACTION");
            return new BeginStatement(mode);
        }
        if (ConsumeKeyword("COMMIT") || ConsumeKeyword("END"))
        {
            ConsumeKeyword("TRANSACTION");
            return new CommitStatement();
        }
        if (ConsumeKeyword("ROLLBACK"))
        {
            ConsumeKeyword("TRANSACTION");
            if (ConsumeKeyword("TO"))
            {
                ConsumeKeyword("SAVEPOINT");
                return new RollbackToSavepointStatement(ExpectIdentifier());
            }

            return new RollbackStatement();
        }
        if (ConsumeKeyword("SAVEPOINT"))
            return new SavepointStatement(ExpectIdentifier());
        if (ConsumeKeyword("RELEASE"))
        {
            ConsumeKeyword("SAVEPOINT");
            return new ReleaseSavepointStatement(ExpectIdentifier());
        }

        throw Error("Expected a SQL statement.");
    }

    private ParsedStatement ParseAttach()
    {
        ConsumeKeyword("DATABASE");
        var path = ParseExpression();
        ExpectKeyword("AS");
        var alias = ExpectIdentifier();
        var key = ConsumeKeyword("KEY") ? ParseExpression() : null;

        return new AttachDatabaseStatement(path, alias, key);
    }

    private ParsedStatement ParseDetach()
    {
        ConsumeKeyword("DATABASE");
        return new DetachDatabaseStatement(ExpectIdentifier());
    }

    private ParsedStatement ParseVacuum()
    {
        if (_lexer.Current.Kind is TokenKind.Semicolon or TokenKind.End)
            return new VacuumStatement(null, null);

        if (ConsumeKeyword("INTO"))
            return new VacuumStatement(null, ParseExpression());

        var schema = ExpectIdentifier();
        return new VacuumStatement(
            schema,
            ConsumeKeyword("INTO") ? ParseExpression() : null);
    }

    private string? ParseOptionalMaintenanceTarget()
        => _lexer.Current.Kind is TokenKind.Semicolon or TokenKind.End
            ? null
            : ParsePragmaQualifiedName();

    private ParsedStatement ParsePragma()
    {
        var name = ExpectIdentifier();
        string? schema = null;
        if (Consume(TokenKind.Dot))
        {
            schema = name;
            name = ExpectIdentifier();
        }

        if (name.Equals("table_info", StringComparison.OrdinalIgnoreCase))
            return new PragmaTableInfoStatement(ParsePragmaObjectName(schema));
        if (name.Equals("table_xinfo", StringComparison.OrdinalIgnoreCase))
            return new PragmaTableXInfoStatement(ParsePragmaObjectName(schema));
        if (name.Equals("index_list", StringComparison.OrdinalIgnoreCase))
            return new PragmaIndexListStatement(ParsePragmaObjectName(schema));
        if (name.Equals("index_info", StringComparison.OrdinalIgnoreCase))
            return new PragmaIndexInfoStatement(ParsePragmaObjectName(schema));
        if (name.Equals("index_xinfo", StringComparison.OrdinalIgnoreCase))
            return new PragmaIndexXInfoStatement(ParsePragmaObjectName(schema));
        if (name.Equals("foreign_key_list", StringComparison.OrdinalIgnoreCase))
            return new PragmaForeignKeyListStatement(ParsePragmaObjectName(schema));
        if (name.Equals("foreign_key_check", StringComparison.OrdinalIgnoreCase))
            return new PragmaForeignKeyCheckStatement(
                ParseOptionalPragmaObjectName(name, schema),
                schema);
        if (name.Equals("table_list", StringComparison.OrdinalIgnoreCase))
        {
            RequireReadOnlyPragma(name);
            return new PragmaTableListStatement(schema);
        }
        if (name.Equals("database_list", StringComparison.OrdinalIgnoreCase))
        {
            RequireReadOnlyPragma(name);
            return new PragmaDatabaseListStatement(schema);
        }
        if (name.Equals("encoding", StringComparison.OrdinalIgnoreCase))
        {
            RequireReadOnlyPragma(name);
            return new PragmaEncodingStatement(schema);
        }
        if (name.Equals("query_only", StringComparison.OrdinalIgnoreCase))
            return new PragmaQueryOnlyStatement(ParseOptionalPragmaBoolean(name), schema);
        if (name.Equals("foreign_keys", StringComparison.OrdinalIgnoreCase))
            return new PragmaForeignKeysStatement(ParseOptionalPragmaBoolean(name), schema);
        if (name.Equals("defer_foreign_keys", StringComparison.OrdinalIgnoreCase))
            return new PragmaDeferForeignKeysStatement(ParseOptionalPragmaBoolean(name), schema);
        if (name.Equals("recursive_triggers", StringComparison.OrdinalIgnoreCase))
            return new PragmaRecursiveTriggersStatement(ParseOptionalPragmaBoolean(name), schema);
        if (name.Equals("schema_version", StringComparison.OrdinalIgnoreCase))
        {
            return new PragmaHeaderIntegerStatement(
                PragmaHeaderIntegerKind.SchemaVersion,
                ParseOptionalPragmaInteger(name),
                schema);
        }
        if (name.Equals("user_version", StringComparison.OrdinalIgnoreCase))
        {
            return new PragmaHeaderIntegerStatement(
                PragmaHeaderIntegerKind.UserVersion,
                ParseOptionalPragmaInteger(name),
                schema);
        }
        if (name.Equals("application_id", StringComparison.OrdinalIgnoreCase))
        {
            return new PragmaHeaderIntegerStatement(
                PragmaHeaderIntegerKind.ApplicationId,
                ParseOptionalPragmaInteger(name),
                schema);
        }
        if (name.Equals("journal_mode", StringComparison.OrdinalIgnoreCase))
            return new PragmaJournalModeStatement(ParseOptionalPragmaMode(name), schema);
        if (name.Equals("page_size", StringComparison.OrdinalIgnoreCase))
            return new PragmaPageSizeStatement(ParseOptionalPragmaInteger(name), schema);
        if (name.Equals("page_count", StringComparison.OrdinalIgnoreCase))
        {
            RequireReadOnlyPragma(name);
            return new PragmaPageCountStatement(schema);
        }
        if (name.Equals("freelist_count", StringComparison.OrdinalIgnoreCase))
        {
            RequireReadOnlyPragma(name);
            return new PragmaFreelistCountStatement(schema);
        }

        if (name.Equals("integrity_check", StringComparison.OrdinalIgnoreCase))
            return ParsePragmaIntegrityCheck(name, quick: false, schema);
        if (name.Equals("quick_check", StringComparison.OrdinalIgnoreCase))
            return ParsePragmaIntegrityCheck(name, quick: true, schema);

        throw Error($"Unsupported PRAGMA {name}.");
    }

    /// <remarks>
    /// SQLite accepts either an integer maximum error count or a single bare
    /// table name. An integer bounds the number of reported problems; a name
    /// restricts the check to that table in the pragma's schema. SQLite's pragma
    /// grammar takes one token, so a schema-qualified argument is a syntax error.
    /// </remarks>
    private ParsedStatement ParsePragmaIntegrityCheck(string name, bool quick, string? schema)
    {
        if (_lexer.Current.Kind is TokenKind.Semicolon or TokenKind.End)
            return new PragmaIntegrityCheckStatement(quick, null, null, schema);
        if (!Consume(TokenKind.LeftParen))
            throw Error($"PRAGMA {name} requires a parenthesized value.");

        int? maxErrors = null;
        string? tableName = null;
        var token = _lexer.Current;
        if (token.Kind == TokenKind.Integer)
        {
            maxErrors = ParsePragmaInteger(name);
        }
        else if (token.Kind is TokenKind.Identifier or TokenKind.String)
        {
            _lexer.Next();
            tableName = token.Text;
        }
        else
        {
            throw Error($"Invalid value for PRAGMA {name}.");
        }

        Expect(TokenKind.RightParen);
        return new PragmaIntegrityCheckStatement(quick, maxErrors, tableName, schema);
    }

    private string ParsePragmaObjectName(string? pragmaSchema)
    {
        Expect(TokenKind.LeftParen);
        var objectName = ParsePragmaQualifiedName();
        Expect(TokenKind.RightParen);
        if (ManagedSchemaName.TrySplit(objectName, out var objectSchema, out var localName))
        {
            if (pragmaSchema is not null
                && !pragmaSchema.Equals(objectSchema, StringComparison.OrdinalIgnoreCase))
            {
                throw Error("PRAGMA database qualifiers do not match.");
            }

            return objectName;
        }

        return pragmaSchema is null
            ? objectName
            : ManagedSchemaName.Create(pragmaSchema, localName);
    }

    private string? ParseOptionalPragmaObjectName(string name, string? pragmaSchema)
    {
        if (_lexer.Current.Kind is TokenKind.Semicolon or TokenKind.End)
            return null;
        if (_lexer.Current.Kind != TokenKind.LeftParen)
            throw Error($"PRAGMA {name} requires a parenthesized table name.");
        return ParsePragmaObjectName(pragmaSchema);
    }

    private void RequireReadOnlyPragma(string name)
    {
        if (_lexer.Current.Kind is not (TokenKind.Semicolon or TokenKind.End))
            throw Error($"PRAGMA {name} does not accept a value.");
    }

    private bool? ParseOptionalPragmaBoolean(string name)
    {
        if (Consume(TokenKind.Equal))
            return ParsePragmaBoolean(name);

        if (Consume(TokenKind.LeftParen))
        {
            var value = ParsePragmaBoolean(name);
            Expect(TokenKind.RightParen);
            return value;
        }

        if (_lexer.Current.Kind is TokenKind.Semicolon or TokenKind.End)
            return null;

        throw Error($"PRAGMA {name} requires '=' or a parenthesized value.");
    }

    private bool ParsePragmaBoolean(string name)
    {
        var token = _lexer.Current;
        switch (token.Kind)
        {
            case TokenKind.Integer:
                _lexer.Next();
                if (!long.TryParse(token.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                    throw Error($"Invalid value for PRAGMA {name}.");

                return integer != 0;
            case TokenKind.Real:
                _lexer.Next();
                if (!double.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real)
                    || !double.IsFinite(real))
                {
                    throw Error($"Invalid value for PRAGMA {name}.");
                }

                return real != 0;
            case TokenKind.Identifier:
            case TokenKind.String:
                _lexer.Next();
                return token.Text.Equals("on", StringComparison.OrdinalIgnoreCase)
                    || token.Text.Equals("yes", StringComparison.OrdinalIgnoreCase)
                    || token.Text.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || token.Text.Equals("1", StringComparison.Ordinal);
            default:
                throw Error($"Invalid value for PRAGMA {name}.");
        }
    }

    private int? ParseOptionalPragmaInteger(string name)
    {
        if (Consume(TokenKind.Equal))
            return ParsePragmaInteger(name);

        if (Consume(TokenKind.LeftParen))
        {
            var value = ParsePragmaInteger(name);
            Expect(TokenKind.RightParen);
            return value;
        }

        if (_lexer.Current.Kind is TokenKind.Semicolon or TokenKind.End)
            return null;

        throw Error($"PRAGMA {name} requires '=' or a parenthesized value.");
    }

    private int ParsePragmaInteger(string name)
    {
        var sign = string.Empty;
        if (Consume(TokenKind.Minus))
            sign = "-";
        else if (Consume(TokenKind.Plus))
            sign = "+";

        var token = _lexer.Current;
        _lexer.Next();
        return token.Kind switch
        {
            TokenKind.Integer => ParsePragmaIntegerText(sign + token.Text),
            TokenKind.Real => ParsePragmaIntegerReal(sign + token.Text),
            TokenKind.Identifier or TokenKind.String when sign.Length == 0 => ParsePragmaIntegerText(token.Text),
            _ => throw Error($"Invalid value for PRAGMA {name}."),
        };
    }

    private int ParsePragmaIntegerText(string value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
            && integer is >= int.MinValue and <= int.MaxValue
            ? (int)integer
            : 0;
    }

    private int ParsePragmaIntegerReal(string value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var real)
            && double.IsFinite(real)
            && real is >= int.MinValue and <= int.MaxValue
            ? (int)real
            : 0;
    }

    private string? ParseOptionalPragmaMode(string name)
    {
        if (Consume(TokenKind.Equal))
            return ParsePragmaMode(name);

        if (Consume(TokenKind.LeftParen))
        {
            var mode = ParsePragmaMode(name);
            Expect(TokenKind.RightParen);
            return mode;
        }

        if (_lexer.Current.Kind is TokenKind.Semicolon or TokenKind.End)
            return null;

        throw Error($"PRAGMA {name} requires '=' or a parenthesized value.");
    }

    private string ParsePragmaMode(string name)
    {
        var token = _lexer.Current;
        if (token.Kind is not (TokenKind.Identifier or TokenKind.String))
            throw Error($"Invalid value for PRAGMA {name}.");

        _lexer.Next();
        return token.Text;
    }

    private ParsedStatement ParseCreate()
    {
        var temporaryKeyword = _lexer.Current;
        var temporary = ConsumeKeyword("TEMP") || ConsumeKeyword("TEMPORARY");
        if (temporary)
        {
            // sqlite_temp_schema stores the definition without the TEMP keyword, so remember
            // where it was and elide it when the object SQL is captured.
            _temporaryKeywordSpan = (temporaryKeyword.Offset, temporaryKeyword.Text.Length);
            if (ConsumeKeyword("VIEW"))
                return ParseCreateView(temporary: true);
            if (ConsumeKeyword("TRIGGER"))
                return ParseCreateTrigger(temporary: true);
            if (!CurrentIsKeyword("TABLE"))
                throw Error("Only temporary tables, views, and triggers are supported by the managed engine.");

            return ParseCreateTable(temporary: true);
        }
        if (ConsumeKeyword("UNIQUE"))
        {
            ExpectKeyword("INDEX");
            return ParseCreateIndex(unique: true);
        }
        if (ConsumeKeyword("INDEX"))
            return ParseCreateIndex(unique: false);
        if (ConsumeKeyword("VIEW"))
            return ParseCreateView(temporary: false);
        if (ConsumeKeyword("TRIGGER"))
            return ParseCreateTrigger(temporary: false);
        if (ConsumeKeyword("VIRTUAL"))
        {
            ExpectKeyword("TABLE");
            throw Error(
                "Managed virtual tables are not supported: no module registration, planner, or execution contract is available. "
                + "Managed CREATE VIRTUAL TABLE modules are not supported.");
        }

        return ParseCreateTable(temporary: false);
    }

    private ParsedStatement ParseCreateTable(bool temporary)
    {
        ExpectKeyword("TABLE");
        var ifNotExists = false;
        if (ConsumeKeyword("IF"))
        {
            ExpectKeyword("NOT");
            ExpectKeyword("EXISTS");
            ifNotExists = true;
        }

        var name = ParseSchemaQualifiedName();
        if (temporary
            && ManagedSchemaName.TrySplit(name, out var schema, out _)
            && !schema.Equals("temp", StringComparison.OrdinalIgnoreCase))
        {
            throw Error("temporary table name must be unqualified");
        }
        if (temporary && !ManagedSchemaName.TrySplit(name, out _, out _))
            name = ManagedSchemaName.Create("temp", name);
        if (ConsumeKeyword("AS"))
        {
            if (!IsQueryStart())
                throw Error("Expected a SELECT query after AS.");

            return new CreateTableAsSelectStatement(name, ParseQuery(), ifNotExists, temporary);
        }

        Expect(TokenKind.LeftParen);
        var columns = new List<EmbeddedColumn>();
        IReadOnlyList<TablePrimaryKeyColumn>? tablePrimaryKey = null;
        InsertConflictAlgorithm? tablePrimaryKeyConflictAlgorithm = null;
        string? tablePrimaryKeyConstraintName = null;
        int? tablePrimaryKeyDeclarationOrder = null;
        var uniqueConstraints = new List<TableUniqueConstraint>();
        var checkConstraints = new List<CheckConstraint>();
        var tableConstraintOrder = 0;
        var tableForeignKeys = new List<ForeignKeyDefinition>();
        do
        {
            if (IsTableConstraintStart())
            {
                var parsed = ParseTableConstraint();
                var declarationOrder = tableConstraintOrder++;
                switch (parsed)
                {
                    case PrimaryKeyTableConstraint primaryKey:
                        if (tablePrimaryKey is not null)
                            throw Error("table has more than one primary key");

                        tablePrimaryKey = primaryKey.Columns;
                        tablePrimaryKeyConflictAlgorithm = primaryKey.ConflictAlgorithm;
                        tablePrimaryKeyConstraintName = primaryKey.Name;
                        tablePrimaryKeyDeclarationOrder = declarationOrder;
                        break;
                    case ForeignKeyTableConstraint foreignKey:
                        tableForeignKeys.Add(foreignKey.Definition);
                        break;
                    case UniqueTableConstraint unique:
                        uniqueConstraints.Add(new TableUniqueConstraint(
                            unique.Name,
                            unique.Columns,
                            unique.ConflictAlgorithm,
                            declarationOrder));
                        break;
                    case CheckTableConstraint check:
                        checkConstraints.Add(new CheckConstraint(
                            check.Name,
                            check.Expression,
                            check.Sql,
                            check.ConflictAlgorithm));
                        break;
                }

                continue;
            }

            columns.Add(ParseColumnDefinition());
        }
        while (Consume(TokenKind.Comma));
        Expect(TokenKind.RightParen);

        var withoutRowid = false;
        var strict = false;
        var optionRequired = false;
        while (true)
        {
            if (ConsumeKeyword("WITHOUT"))
            {
                if (withoutRowid)
                    throw Error("WITHOUT ROWID may only be specified once.");
                if (!ConsumeKeyword("ROWID"))
                    throw Error("Expected ROWID after WITHOUT.");

                withoutRowid = true;
            }
            else if (ConsumeKeyword("STRICT"))
            {
                if (strict)
                    throw Error("STRICT may only be specified once.");

                strict = true;
            }
            else
            {
                if (optionRequired)
                    throw Error("Expected STRICT or WITHOUT ROWID after ','.");
                break;
            }

            optionRequired = Consume(TokenKind.Comma);
            if (!optionRequired)
                break;
        }

        return new CreateTableStatement(
            name,
            columns,
            ifNotExists,
            withoutRowid,
            tablePrimaryKey,
            uniqueConstraints,
            checkConstraints,
            tablePrimaryKeyConflictAlgorithm,
            tablePrimaryKeyConstraintName,
            tablePrimaryKeyDeclarationOrder,
            tableForeignKeys,
            strict);
    }

    private abstract record TableConstraint;

    private sealed record PrimaryKeyTableConstraint(
        string? Name,
        IReadOnlyList<TablePrimaryKeyColumn> Columns,
        InsertConflictAlgorithm? ConflictAlgorithm) : TableConstraint;

    private sealed record ForeignKeyTableConstraint(ForeignKeyDefinition Definition) : TableConstraint;

    private sealed record UniqueTableConstraint(
        string? Name,
        IReadOnlyList<TablePrimaryKeyColumn> Columns,
        InsertConflictAlgorithm? ConflictAlgorithm) : TableConstraint;

    private sealed record CheckTableConstraint(
        string? Name,
        Expression Expression,
        string Sql,
        InsertConflictAlgorithm? ConflictAlgorithm) : TableConstraint;

    private TableConstraint ParseTableConstraint()
    {
        string? constraintName = null;
        if (ConsumeKeyword("CONSTRAINT"))
            constraintName = ExpectIdentifier();

        if (ConsumeKeyword("PRIMARY"))
        {
            ExpectKeyword("KEY");
            var keyColumns = ParseTableConstraintColumns(allowAutoIncrement: true);
            return new PrimaryKeyTableConstraint(constraintName, keyColumns, ParseConflictClause());
        }

        if (ConsumeKeyword("UNIQUE"))
        {
            var keyColumns = ParseTableConstraintColumns(allowAutoIncrement: false);
            return new UniqueTableConstraint(constraintName, keyColumns, ParseConflictClause());
        }

        if (ConsumeKeyword("FOREIGN"))
        {
            ExpectKeyword("KEY");
            var childColumns = ParseForeignKeyColumns();
            ExpectKeyword("REFERENCES");
            return new ForeignKeyTableConstraint(ParseForeignKeyReference(childColumns, constraintName));
        }

        if (ConsumeKeyword("CHECK"))
        {
            var (expression, sql) = ParseParenthesizedSchemaExpression("CHECK");
            return new CheckTableConstraint(constraintName, expression, sql, ParseConflictClause());
        }

        throw Error("Expected PRIMARY KEY, UNIQUE, CHECK, or FOREIGN KEY after table constraint name.");
    }

    private IReadOnlyList<TablePrimaryKeyColumn> ParseTableConstraintColumns(bool allowAutoIncrement)
    {
        Expect(TokenKind.LeftParen);
        var columns = new List<TablePrimaryKeyColumn>();
        do
        {
            var columnName = ExpectIdentifier();
            string? collation = null;
            if (ConsumeKeyword("COLLATE"))
                collation = ExpectIdentifier();

            var descending = false;
            if (!ConsumeKeyword("ASC") && ConsumeKeyword("DESC"))
                descending = true;

            var autoIncrement = allowAutoIncrement && ConsumeKeyword("AUTOINCREMENT");
            columns.Add(new TablePrimaryKeyColumn(columnName, descending, collation, autoIncrement));
        }
        while (Consume(TokenKind.Comma));
        Expect(TokenKind.RightParen);
        return columns;
    }

    private IReadOnlyList<string> ParseForeignKeyColumns()
    {
        Expect(TokenKind.LeftParen);
        var columns = new List<string>();
        do
        {
            columns.Add(ExpectIdentifier());
        }
        while (Consume(TokenKind.Comma));
        Expect(TokenKind.RightParen);
        return columns;
    }

    private ForeignKeyDefinition ParseForeignKeyReference(
        IReadOnlyList<string> childColumns,
        string? constraintName = null)
    {
        var parentTable = ExpectIdentifier();
        if (Consume(TokenKind.Dot))
            throw Error("Schema-qualified foreign keys are not supported.");

        IReadOnlyList<string> parentColumns = [];
        if (_lexer.Current.Kind == TokenKind.LeftParen)
            parentColumns = ParseForeignKeyColumns();

        var onDelete = ForeignKeyAction.NoAction;
        var onUpdate = ForeignKeyAction.NoAction;
        string? match = null;
        var deferral = ForeignKeyDeferral.NotDeferrable;
        while (true)
        {
            if (ConsumeKeyword("ON"))
            {
                var isDelete = ConsumeKeyword("DELETE");
                if (!isDelete)
                    ExpectKeyword("UPDATE");

                var action = ParseForeignKeyAction();
                if (isDelete)
                    onDelete = action;
                else
                    onUpdate = action;
                continue;
            }

            if (ConsumeKeyword("MATCH"))
            {
                match = ExpectIdentifier();
                continue;
            }

            var notDeferrable = ConsumeKeyword("NOT");
            if (notDeferrable || ConsumeKeyword("DEFERRABLE"))
            {
                if (notDeferrable)
                    ExpectKeyword("DEFERRABLE");

                var initiallyDeferred = false;
                if (ConsumeKeyword("INITIALLY"))
                {
                    if (ConsumeKeyword("DEFERRED"))
                        initiallyDeferred = true;
                    else
                        ExpectKeyword("IMMEDIATE");
                }

                deferral = notDeferrable
                    ? ForeignKeyDeferral.NotDeferrable
                    : initiallyDeferred
                        ? ForeignKeyDeferral.InitiallyDeferred
                        : ForeignKeyDeferral.InitiallyImmediate;
                continue;
            }

            break;
        }

        return new ForeignKeyDefinition(
            childColumns,
            parentTable,
            parentColumns,
            onDelete,
            onUpdate,
            match,
            deferral,
            constraintName);
    }

    private ForeignKeyAction ParseForeignKeyAction()
    {
        if (ConsumeKeyword("CASCADE"))
            return ForeignKeyAction.Cascade;
        if (ConsumeKeyword("RESTRICT"))
            return ForeignKeyAction.Restrict;
        if (ConsumeKeyword("SET"))
        {
            if (ConsumeKeyword("NULL"))
                return ForeignKeyAction.SetNull;
            ExpectKeyword("DEFAULT");
            return ForeignKeyAction.SetDefault;
        }
        if (ConsumeKeyword("NO"))
        {
            ExpectKeyword("ACTION");
            return ForeignKeyAction.NoAction;
        }

        throw Error("Expected CASCADE, RESTRICT, SET NULL, SET DEFAULT, or NO ACTION.");
    }

    private ParsedStatement ParseCreateIndex(bool unique)
    {
        var ifNotExists = false;
        if (ConsumeKeyword("IF"))
        {
            ExpectKeyword("NOT");
            ExpectKeyword("EXISTS");
            ifNotExists = true;
        }

        var name = ParseSchemaQualifiedName();
        ExpectKeyword("ON");
        var tableName = ParseSchemaQualifiedName();
        Expect(TokenKind.LeftParen);
        var columns = new List<IndexedColumnDefinition>();
        do
        {
            columns.Add(ParseIndexedColumn());
        }
        while (Consume(TokenKind.Comma));
        Expect(TokenKind.RightParen);

        Expression? where = null;
        string? whereSql = null;
        if (ConsumeKeyword("WHERE"))
        {
            var startOffset = _lexer.Current.Offset;
            where = ParseExpression();
            whereSql = _sql[startOffset.._lexer.Current.Offset].Trim();
            if (whereSql.Length == 0)
                throw Error("Partial index WHERE clause requires an expression.");
        }

        return new CreateIndexStatement(name, tableName, columns, unique, ifNotExists, where, whereSql);
    }

    private IndexedColumnDefinition ParseIndexedColumn()
    {
        var startOffset = _lexer.Current.Offset;
        var expression = ParseExpression();
        var expressionSql = _sql[startOffset.._lexer.Current.Offset].Trim();
        if (expressionSql.Length == 0)
            throw Error("Index requires an expression.");

        var descending = false;
        if (!ConsumeKeyword("ASC") && ConsumeKeyword("DESC"))
            descending = true;

        if (_lexer.Current.Kind is not TokenKind.Comma and not TokenKind.RightParen)
            throw Error("Unexpected token in index expression.");

        string? collation = null;
        if (expression is CollationExpression collated)
        {
            collation = collated.Name;
            expression = collated.Expression;
        }

        if (expression is ColumnExpression { Qualifier: null } column)
            return new IndexedColumnDefinition(column.Name, collation, descending);

        return new IndexedColumnDefinition(
            Name: null,
            collation,
            descending,
            expression,
            expressionSql);
    }

    private ParsedStatement ParseAlterTable()
    {
        ExpectKeyword("TABLE");
        var tableName = ParseSchemaQualifiedName();
        if (ConsumeKeyword("ADD"))
        {
            ConsumeKeyword("COLUMN");
            return new AlterTableAddColumnStatement(tableName, ParseColumnDefinition());
        }
        if (ConsumeKeyword("RENAME"))
        {
            if (ConsumeKeyword("COLUMN"))
            {
                var columnName = ExpectIdentifier();
                ExpectKeyword("TO");
                var newNameToken = ExpectIdentifierToken();
                return new AlterTableRenameColumnStatement(
                    tableName,
                    columnName,
                    newNameToken.Text,
                    newNameToken.IsQuoted);
            }

            if (!CurrentIsKeyword("TO"))
            {
                // SQLite accepts the COLUMN keyword as optional, so `RENAME <old> TO <new>`
                // is the same statement as `RENAME COLUMN <old> TO <new>`.
                var columnName = ExpectIdentifier();
                ExpectKeyword("TO");
                var newNameToken = ExpectIdentifierToken();
                return new AlterTableRenameColumnStatement(
                    tableName,
                    columnName,
                    newNameToken.Text,
                    newNameToken.IsQuoted);
            }

            ExpectKeyword("TO");
            return new AlterTableRenameStatement(tableName, ExpectIdentifier());
        }
        if (ConsumeKeyword("DROP"))
        {
            ConsumeKeyword("COLUMN");
            return new AlterTableDropColumnStatement(tableName, ExpectIdentifier());
        }

        throw Error("Expected ADD, DROP, or RENAME after ALTER TABLE.");
    }

    private ParsedStatement ParseDrop()
    {
        if (ConsumeKeyword("INDEX"))
            return ParseDropIndex();
        if (ConsumeKeyword("VIEW"))
            return ParseDropView();
        if (ConsumeKeyword("TRIGGER"))
            return ParseDropTrigger();

        return ParseDropTable();
    }

    private ParsedStatement ParseDropTable()
    {
        ExpectKeyword("TABLE");
        var ifExists = false;
        if (ConsumeKeyword("IF"))
        {
            ExpectKeyword("EXISTS");
            ifExists = true;
        }

        return new DropTableStatement(ParseSchemaQualifiedName(), ifExists);
    }

    private ParsedStatement ParseDropIndex()
    {
        var ifExists = false;
        if (ConsumeKeyword("IF"))
        {
            ExpectKeyword("EXISTS");
            ifExists = true;
        }

        return new DropIndexStatement(ParseSchemaQualifiedName(), ifExists);
    }

    private ParsedStatement ParseCreateView(bool temporary)
    {
        var ifNotExists = ParseIfNotExists();
        var name = ParseSchemaQualifiedName();
        if (temporary
            && ManagedSchemaName.TrySplit(name, out var viewSchema, out _)
            && !viewSchema.Equals("temp", StringComparison.OrdinalIgnoreCase))
        {
            throw Error("temporary table name must be unqualified");
        }

        IReadOnlyList<string>? columns = null;
        if (Consume(TokenKind.LeftParen))
        {
            columns = ParseIdentifierList();
            Expect(TokenKind.RightParen);
        }

        ExpectKeyword("AS");
        if (!IsQueryStart())
            throw Error("Expected a SELECT query in the view definition.");

        var query = ParseQuery();
        return new CreateViewStatement(name, columns, query, NormalizeObjectSql(), ifNotExists, temporary);
    }

    private ParsedStatement ParseCreateTrigger(bool temporary)
    {
        var ifNotExists = ParseIfNotExists();
        var name = ParseSchemaQualifiedName();
        if (temporary && ManagedSchemaName.TrySplit(name, out _, out _))
            throw Error("temporary trigger may not have qualified name");

        var timing = TriggerTiming.Before;
        if (ConsumeKeyword("BEFORE"))
            timing = TriggerTiming.Before;
        else if (ConsumeKeyword("AFTER"))
            timing = TriggerTiming.After;
        else if (ConsumeKeyword("INSTEAD"))
        {
            ExpectKeyword("OF");
            timing = TriggerTiming.InsteadOf;
        }

        var (triggerEvent, updateOfColumns) = ParseTriggerEvent();
        ExpectKeyword("ON");
        var tableName = ParseSchemaQualifiedName();

        _inTriggerBody = true;
        try
        {
            if (ConsumeKeyword("FOR"))
            {
                ExpectKeyword("EACH");
                ExpectKeyword("ROW");
            }

            Expression? when = null;
            if (ConsumeKeyword("WHEN"))
                when = ParseExpression();

            ExpectKeyword("BEGIN");
            var body = new List<ParsedStatement>();
            while (!ConsumeKeyword("END"))
            {
                if (_lexer.Current.Kind == TokenKind.End)
                    throw Error("Expected END to close the trigger body.");

                body.Add(ParseTriggerBodyStatement());
                Expect(TokenKind.Semicolon);
            }

            if (body.Count == 0)
                throw Error("A trigger body must contain at least one statement.");

            var trigger = new CreateTriggerStatement(
                name,
                timing,
                triggerEvent,
                updateOfColumns,
                tableName,
                when,
                body,
                NormalizeObjectSql(),
                ifNotExists,
                temporary);
            if (_spans is not null && _pendingUpdateOfTokens is not null)
                _spans.RecordList(trigger, _pendingUpdateOfTokens);

            return trigger;
        }
        finally
        {
            _inTriggerBody = false;
            _pendingUpdateOfTokens = null;
        }
    }

    private (TriggerEvent Event, IReadOnlyList<string>? UpdateOfColumns) ParseTriggerEvent()
    {
        if (ConsumeKeyword("INSERT"))
            return (TriggerEvent.Insert, null);
        if (ConsumeKeyword("DELETE"))
            return (TriggerEvent.Delete, null);
        if (ConsumeKeyword("UPDATE"))
        {
            IReadOnlyList<string>? updateOfColumns = null;
            _pendingUpdateOfTokens = null;
            if (ConsumeKeyword("OF"))
            {
                updateOfColumns = ParseIdentifierList(out var tokens);
                _pendingUpdateOfTokens = tokens;
            }

            return (TriggerEvent.Update, updateOfColumns);
        }

        throw Error("Expected INSERT, UPDATE, or DELETE as the trigger event.");
    }

    private ParsedStatement ParseTriggerBodyStatement()
    {
        if (ConsumeKeyword("INSERT"))
            return ParseInsert();
        if (ConsumeKeyword("REPLACE"))
            return ParseInsert(InsertConflictAlgorithm.Replace);
        if (ConsumeKeyword("UPDATE"))
            return ParseUpdate();
        if (ConsumeKeyword("DELETE"))
            return ParseDelete();
        if (IsQueryStart())
            return ParseQuery();

        throw Error("Only INSERT, UPDATE, DELETE, and SELECT statements are allowed in a trigger body.");
    }

    private ParsedStatement ParseDropView()
    {
        var ifExists = ParseIfExists();
        return new DropViewStatement(ParseSchemaQualifiedName(), ifExists);
    }

    private ParsedStatement ParseDropTrigger()
    {
        var ifExists = ParseIfExists();
        return new DropTriggerStatement(ParseSchemaQualifiedName(), ifExists);
    }

    private bool ParseIfNotExists()
    {
        if (!ConsumeKeyword("IF"))
            return false;

        ExpectKeyword("NOT");
        ExpectKeyword("EXISTS");
        return true;
    }

    private bool ParseIfExists()
    {
        if (!ConsumeKeyword("IF"))
            return false;

        ExpectKeyword("EXISTS");
        return true;
    }

    // Views and triggers have no AST-to-SQL printer, so sqlite_master exposes the original
    // statement text with trailing terminators trimmed to match SQLite's stored schema.
    // SQLite also drops the TEMP keyword from the definition it records in sqlite_temp_schema.
    private string NormalizeObjectSql()
    {
        var text = _sql;
        if (_temporaryKeywordSpan is { } keyword)
        {
            var end = keyword.Offset + keyword.Length;
            while (end < text.Length && char.IsWhiteSpace(text[end]))
                end++;
            text = text.Remove(keyword.Offset, end - keyword.Offset);
        }

        text = text.Trim();
        while (text.EndsWith(';'))
            text = text[..^1].TrimEnd();

        return text;
    }

    private ParsedStatement ParseInsert(InsertConflictAlgorithm? impliedConflictAlgorithm = null)
    {
        var conflictAlgorithm = impliedConflictAlgorithm ?? ParseInsertConflictAlgorithm();
        ExpectKeyword("INTO");
        var tableName = ParseSchemaQualifiedName();
        RejectQualifiedTriggerDmlTarget(tableName);
        string[]? columns = null;
        IReadOnlyList<SqlToken>? columnTokens = null;
        if (Consume(TokenKind.LeftParen))
        {
            columns = ParseIdentifierList(out columnTokens);
            Expect(TokenKind.RightParen);
        }

        var rows = new List<Expression[]>();
        QueryStatement? source = null;
        if (ConsumeKeyword("VALUES"))
        {
            var values = ParseValuesClause();
            if (CurrentIsKeyword("UNION")
                || CurrentIsKeyword("INTERSECT")
                || CurrentIsKeyword("EXCEPT"))
            {
                source = ParseQuery(values);
            }
            else
            {
                rows.AddRange(values.Rows.Select(static row => row.ToArray()));
            }
        }
        else if (ConsumeKeyword("DEFAULT"))
        {
            if (_inTriggerBody)
                throw Error("DEFAULT VALUES is not available inside a trigger body.");
            if (columns is not null)
                throw Error("DEFAULT VALUES cannot be used with a column list.");

            ExpectKeyword("VALUES");
            columns = [];
            rows.Add([]);
        }
        else if (IsQueryStart())
        {
            source = ParseQuery();
        }
        else
        {
            throw Error("Expected VALUES, DEFAULT VALUES, or a SELECT query after the INSERT target.");
        }

        var upsert = ParseUpsert();
        var insert = new InsertStatement(
            tableName,
            columns,
            rows,
            source,
            ParseReturning(),
            upsert,
            conflictAlgorithm);
        if (_spans is not null && columnTokens is not null)
            _spans.RecordList(insert, columnTokens);

        return insert;
    }

    private InsertConflictAlgorithm? ParseInsertConflictAlgorithm()
    {
        if (!ConsumeKeyword("OR"))
            return null;

        if (ConsumeKeyword("ROLLBACK"))
            return InsertConflictAlgorithm.Rollback;
        if (ConsumeKeyword("ABORT"))
            return InsertConflictAlgorithm.Abort;
        if (ConsumeKeyword("FAIL"))
            return InsertConflictAlgorithm.Fail;
        if (ConsumeKeyword("IGNORE"))
            return InsertConflictAlgorithm.Ignore;
        if (ConsumeKeyword("REPLACE"))
            return InsertConflictAlgorithm.Replace;

        throw Error("Expected ROLLBACK, ABORT, FAIL, IGNORE, or REPLACE after INSERT OR.");
    }

    private UpsertClause? ParseUpsert()
    {
        if (!ConsumeKeyword("ON"))
            return null;

        ExpectKeyword("CONFLICT");
        var target = new List<UpsertTargetColumn>();
        if (Consume(TokenKind.LeftParen))
        {
            do
            {
                var term = ParseIndexedColumn();
                target.Add(new UpsertTargetColumn(
                    term.Name,
                    term.Collation,
                    term.Descending,
                    term.Expression,
                    term.ExpressionSql));
            }
            while (Consume(TokenKind.Comma));
            Expect(TokenKind.RightParen);
        }

        Expression? targetWhere = null;
        string? targetWhereSql = null;
        if (ConsumeKeyword("WHERE"))
        {
            var startOffset = _lexer.Current.Offset;
            targetWhere = ParseExpression();
            targetWhereSql = _sql[startOffset.._lexer.Current.Offset].Trim();
            if (targetWhereSql.Length == 0)
                throw Error("UPSERT conflict-target WHERE clause requires an expression.");
        }

        ExpectKeyword("DO");
        if (ConsumeKeyword("NOTHING"))
            return new UpsertClause(target, new DoNothingUpsertAction(), targetWhere, targetWhereSql);

        ExpectKeyword("UPDATE");
        ExpectKeyword("SET");
        var assignments = ParseAssignments();

        Expression? where = null;
        if (ConsumeKeyword("WHERE"))
            where = ParseExpression();

        return new UpsertClause(
            target,
            new DoUpdateUpsertAction(assignments, where),
            targetWhere,
            targetWhereSql);
    }

    private ParsedStatement ParseUpdate()
    {
        var conflictAlgorithm = ParseInsertConflictAlgorithm();
        var tableName = ParseSchemaQualifiedName();
        RejectQualifiedTriggerDmlTarget(tableName);
        var alias = ParseDmlTargetAlias();
        RejectUnsupportedDmlTargetSuffix("UPDATE");
        ExpectKeyword("SET");
        var assignments = ParseAssignments();

        TableSource? from = null;
        if (ConsumeKeyword("FROM"))
        {
            from = ParseTableSource();
        }

        Expression? where = null;
        if (ConsumeKeyword("WHERE"))
            where = ParseExpression();

        var returning = ParseReturning();
        var (orderBy, limit, offset) = ParseLimitedDmlTail("UPDATE");
        if (from is not null && limit is not null)
            throw Error("LIMIT is not supported on UPDATE ... FROM.");

        return new UpdateStatement(
            tableName,
            assignments,
            where,
            returning,
            orderBy,
            limit,
            offset,
            alias,
            from,
            conflictAlgorithm);
    }

    private IReadOnlyList<ColumnAssignment> ParseAssignments()
    {
        var assignments = new List<ColumnAssignment>();
        do
        {
            SqlToken[] columnTokens;
            var isRowAssignment = Consume(TokenKind.LeftParen);
            if (isRowAssignment)
            {
                ParseIdentifierList(out var tokens);
                columnTokens = tokens.ToArray();
                Expect(TokenKind.RightParen);
            }
            else
            {
                columnTokens = [ExpectIdentifierToken()];
            }

            Expect(TokenKind.Equal);
            var value = ParseExpression();
            for (var index = 0; index < columnTokens.Length; index++)
            {
                var assignment = new ColumnAssignment(
                    columnTokens[index].Text,
                    value,
                    index,
                    columnTokens.Length,
                    isRowAssignment);
                _spans?.RecordName(assignment, columnTokens[index]);
                assignments.Add(assignment);
            }
        }
        while (Consume(TokenKind.Comma));

        return assignments;
    }

    private ParsedStatement ParseDelete()
    {
        ExpectKeyword("FROM");
        var tableName = ParseSchemaQualifiedName();
        RejectQualifiedTriggerDmlTarget(tableName);
        var alias = ParseDmlTargetAlias();
        RejectUnsupportedDmlTargetSuffix("DELETE");
        Expression? where = null;
        if (ConsumeKeyword("WHERE"))
            where = ParseExpression();

        var returning = ParseReturning();
        var (orderBy, limit, offset) = ParseLimitedDmlTail("DELETE");
        return new DeleteStatement(tableName, where, returning, orderBy, limit, offset, alias);
    }

    // SQLite's qualified-table-name allows an alias on UPDATE and DELETE targets. Only the
    // explicit AS form is accepted so a bare identifier cannot silently swallow SET or WHERE.
    private string? ParseDmlTargetAlias()
        => ConsumeKeyword("AS") ? ExpectIdentifier() : null;

    private void RejectUnsupportedDmlTargetSuffix(string statementKind)
    {
        if (CurrentIsKeyword("INDEXED") || CurrentIsKeyword("NOT"))
            throw Error($"Managed {statementKind} does not support INDEXED BY or NOT INDEXED.");
    }

    private void RejectQualifiedTriggerDmlTarget(string tableName)
    {
        if (_inTriggerBody && ManagedSchemaName.TrySplit(tableName, out _, out _))
        {
            throw Error(
                "Qualified table names are not allowed on INSERT, UPDATE, and DELETE statements within triggers.");
        }
    }

    private (IReadOnlyList<OrderByTerm> OrderBy, Expression? Limit, Expression? Offset)
        ParseLimitedDmlTail(string statementKind)
    {
        if (_inTriggerBody && (CurrentIsKeyword("ORDER") || CurrentIsKeyword("LIMIT")))
        {
            throw Error(
                $"ORDER BY and LIMIT are not available on {statementKind} statements inside trigger bodies.");
        }

        var (orderBy, limit, offset) = ParseOrderByAndLimit();
        if (orderBy.Count > 0 && limit is null)
            throw Error($"ORDER BY without LIMIT on {statementKind} is not supported.");

        return (orderBy, limit, offset);
    }

    // Parses an optional RETURNING clause shared by INSERT/UPDATE/DELETE. RETURNING is
    // rejected inside trigger bodies to match SQLite, which forbids it there.
    private IReadOnlyList<Projection>? ParseReturning()
    {
        if (!ConsumeKeyword("RETURNING"))
            return null;

        if (_inTriggerBody)
            throw Error("RETURNING is not available inside a trigger body.");

        var projections = new List<Projection> { ParseProjection() };
        while (Consume(TokenKind.Comma))
            projections.Add(ParseProjection());

        return projections;
    }

    private QueryStatement ParseQuery()
    {
        if (ConsumeKeyword("WITH"))
            return ParseWithSelect();

        return ParseQuery(ParseQueryTerm());
    }

    private QueryStatement ParseQuery(QueryStatement firstTerm)
    {
        var terms = new List<QueryStatement> { firstTerm };
        var operators = new List<CompoundOperator>();
        while (true)
        {
            if (ConsumeKeyword("UNION"))
            {
                operators.Add(ConsumeKeyword("ALL") ? CompoundOperator.UnionAll : CompoundOperator.Union);
            }
            else if (ConsumeKeyword("INTERSECT"))
            {
                operators.Add(CompoundOperator.Intersect);
            }
            else if (ConsumeKeyword("EXCEPT"))
            {
                operators.Add(CompoundOperator.Except);
            }
            else
            {
                break;
            }

            terms.Add(ParseQueryTerm());
        }

        // SQLite forbids ORDER BY/LIMIT immediately following a trailing VALUES term;
        // only parse them when the final compound term is a SELECT so that the shared
        // "syntax error near ORDER/LIMIT" rejection is preserved for VALUES.
        var (orderBy, limit, offset) = terms[^1] is ValuesClause
            ? ([], null, null)
            : ParseOrderByAndLimit();

        if (terms.Count == 1)
        {
            return terms[0] switch
            {
                SelectStatement select => select with { OrderBy = orderBy, Limit = limit, Offset = offset },
                _ => terms[0],
            };
        }

        return new CompoundSelectStatement(terms, operators, orderBy, limit, offset);
    }

    // Parses a single compound-select term: either VALUES(...) or a SELECT core.
    private QueryStatement ParseQueryTerm()
    {
        if (ConsumeKeyword("VALUES"))
            return ParseValuesClause();

        ExpectKeyword("SELECT");
        return ParseSelectCore();
    }

    // Parses the row list of a VALUES clause (the VALUES keyword has already been consumed).
    private ValuesClause ParseValuesClause()
    {
        var rows = new List<IReadOnlyList<Expression>>();
        do
        {
            Expect(TokenKind.LeftParen);
            var values = new List<Expression> { ParseExpression() };
            while (Consume(TokenKind.Comma))
                values.Add(ParseExpression());
            Expect(TokenKind.RightParen);
            rows.Add(values);
        }
        while (Consume(TokenKind.Comma));

        return new ValuesClause(rows);
    }

    private WithSelectStatement ParseWithSelect()
    {
        var commonTableExpressions = ParseCommonTableExpressions();
        if (!IsQueryStart())
            throw Error("Expected a SELECT query after the common table expression.");
        return new WithSelectStatement(commonTableExpressions, ParseQuery());
    }

    private ParsedStatement ParseWithStatement()
    {
        var commonTableExpressions = ParseCommonTableExpressions();
        if (ConsumeKeyword("INSERT"))
            return new WithDmlStatement(commonTableExpressions, ParseInsert());
        if (ConsumeKeyword("REPLACE"))
            return new WithDmlStatement(
                commonTableExpressions,
                ParseInsert(InsertConflictAlgorithm.Replace));
        if (ConsumeKeyword("UPDATE"))
            return new WithDmlStatement(commonTableExpressions, ParseUpdate());
        if (ConsumeKeyword("DELETE"))
            return new WithDmlStatement(commonTableExpressions, ParseDelete());
        if (IsQueryStart())
            return new WithSelectStatement(commonTableExpressions, ParseQuery());

        throw Error(
            "Expected a SELECT, INSERT, REPLACE, UPDATE, or DELETE statement after the common table expression.");
    }

    private IReadOnlyList<CommonTableExpression> ParseCommonTableExpressions()
    {
        // The RECURSIVE keyword is accepted for compatibility. Recursion is detected
        // structurally (a CTE whose body references its own name), matching SQLite,
        // which treats the keyword as optional.
        ConsumeKeyword("RECURSIVE");
        var commonTableExpressions = new List<CommonTableExpression>();
        do
        {
            var name = ExpectIdentifier();
            IReadOnlyList<string>? columns = null;
            if (Consume(TokenKind.LeftParen))
            {
                columns = ParseIdentifierList();
                Expect(TokenKind.RightParen);
            }

            ExpectKeyword("AS");
            var materializationHint = CteMaterializationHint.Unspecified;
            if (ConsumeKeyword("MATERIALIZED"))
            {
                materializationHint = CteMaterializationHint.Materialized;
            }
            else if (ConsumeKeyword("NOT"))
            {
                ExpectKeyword("MATERIALIZED");
                materializationHint = CteMaterializationHint.NotMaterialized;
            }

            Expect(TokenKind.LeftParen);
            if (!IsQueryStart())
                throw Error("Managed common table expressions must contain a SELECT or VALUES query; writable CTEs are not supported.");
            var query = ParseQuery();
            Expect(TokenKind.RightParen);
            commonTableExpressions.Add(new CommonTableExpression(name, columns, query, materializationHint));
        }
        while (Consume(TokenKind.Comma));

        return commonTableExpressions;
    }

    private SelectStatement ParseSelectCore()
    {
        var distinct = ConsumeKeyword("DISTINCT");
        if (!distinct)
            ConsumeKeyword("ALL");

        var projections = new List<Projection> { ParseProjection() };
        while (Consume(TokenKind.Comma))
            projections.Add(ParseProjection());

        TableSource? source = null;
        if (ConsumeKeyword("FROM"))
            source = ParseTableSource();

        Expression? where = null;
        if (ConsumeKeyword("WHERE"))
            where = ParseExpression();

        var groupBy = new List<Expression>();
        if (ConsumeKeyword("GROUP"))
        {
            ExpectKeyword("BY");
            do
            {
                groupBy.Add(ParseExpression());
            }
            while (Consume(TokenKind.Comma));
        }

        Expression? having = null;
        if (ConsumeKeyword("HAVING"))
            having = ParseExpression();

        var namedWindows = ParseNamedWindows();
        return new SelectStatement(distinct, projections, source, where, groupBy, having, namedWindows, [], null, null);
    }

    private IReadOnlyList<NamedWindowDefinition> ParseNamedWindows()
    {
        if (!ConsumeKeyword("WINDOW"))
            return [];

        var windows = new List<NamedWindowDefinition>();
        do
        {
            var name = ExpectIdentifier();
            ExpectKeyword("AS");
            Expect(TokenKind.LeftParen);
            var specification = ParseWindowSpecification();
            Expect(TokenKind.RightParen);
            windows.Add(new NamedWindowDefinition(name, specification));
        }
        while (Consume(TokenKind.Comma));

        return windows;
    }

    private (IReadOnlyList<OrderByTerm> OrderBy, Expression? Limit, Expression? Offset) ParseOrderByAndLimit()
    {
        var orderBy = new List<OrderByTerm>();
        if (ConsumeKeyword("ORDER"))
        {
            ExpectKeyword("BY");
            do
            {
                orderBy.Add(ParseOrderByTerm());
            }
            while (Consume(TokenKind.Comma));
        }

        Expression? limit = null;
        Expression? offset = null;
        if (ConsumeKeyword("LIMIT"))
        {
            limit = ParseExpression();
            if (Consume(TokenKind.Comma))
            {
                offset = limit;
                limit = ParseExpression();
            }
            else if (ConsumeKeyword("OFFSET"))
            {
                offset = ParseExpression();
            }
        }

        return (orderBy, limit, offset);
    }

    private OrderByTerm ParseOrderByTerm()
    {
        var expressionOffset = _lexer.Current.Offset;
        var expression = ParseExpression();
        var ordinal = TryParseOrderByOrdinal(
            _sql.AsSpan(expressionOffset, _lexer.Current.Offset - expressionOffset));
        var descending = ConsumeKeyword("DESC");
        if (!descending)
            ConsumeKeyword("ASC");

        var nullPlacement = NullPlacement.Default;
        if (ConsumeKeyword("NULLS"))
        {
            if (ConsumeKeyword("FIRST"))
                nullPlacement = NullPlacement.First;
            else if (ConsumeKeyword("LAST"))
                nullPlacement = NullPlacement.Last;
            else
                throw Error("Expected FIRST or LAST after NULLS.");
        }

        return new OrderByTerm(expression, descending, nullPlacement, ordinal);
    }

    private OrderByTerm ParseAggregateOrderByTerm()
    {
        // ORDER BY terms inside an aggregate are plain expressions evaluated per grouped row:
        // an integer literal is a constant there, never a projection ordinal like in a
        // select-level ORDER BY.
        return ParseOrderByTerm() with { Ordinal = null };
    }

    private static long? TryParseOrderByOrdinal(ReadOnlySpan<char> expression)
    {
        var collation = expression.IndexOf("COLLATE", StringComparison.OrdinalIgnoreCase);
        if (collation >= 0)
            expression = expression[..collation];

        expression = expression.Trim();
        while (TryStripOuterParentheses(ref expression))
            expression = expression.Trim();

        var sign = '\0';
        if (!expression.IsEmpty && expression[0] is '+' or '-')
        {
            sign = expression[0];
            expression = expression[1..].Trim();
            while (TryStripOuterParentheses(ref expression))
                expression = expression.Trim();
        }

        if (expression.IsEmpty || expression.IndexOfAnyExceptInRange('0', '9') >= 0)
            return null;

        var text = sign == '\0'
            ? expression.ToString()
            : sign + expression.ToString();
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal)
            ? ordinal
            : null;
    }

    private static bool TryStripOuterParentheses(ref ReadOnlySpan<char> expression)
    {
        if (expression.Length < 2 || expression[0] != '(' || expression[^1] != ')')
            return false;

        var depth = 0;
        for (var index = 0; index < expression.Length; index++)
        {
            depth += expression[index] switch
            {
                '(' => 1,
                ')' => -1,
                _ => 0,
            };
            if (depth == 0 && index != expression.Length - 1)
                return false;
            if (depth < 0)
                return false;
        }

        if (depth != 0)
            return false;

        expression = expression[1..^1];
        return true;
    }

    private Projection ParseProjection()
    {
        if (Consume(TokenKind.Asterisk))
            return new Projection(new StarExpression(), null);

        if (_lexer.Current.Kind == TokenKind.Identifier)
        {
            var snapshot = _lexer.Snapshot();
            var qualifier = _lexer.Current.Text;
            _lexer.Next();
            if (Consume(TokenKind.Dot) && _lexer.Current.Kind == TokenKind.Asterisk)
            {
                _lexer.Next();
                return new Projection(new QualifiedStarExpression(qualifier), null);
            }

            _lexer.Restore(snapshot);
        }

        var expression = ParseExpression();
        string? alias = null;
        if (ConsumeKeyword("AS"))
            alias = ExpectIdentifier();

        return new Projection(expression, alias);
    }

    private Expression? ParseFilter()
    {
        if (_lexer.Current.Kind != TokenKind.Identifier
            || !string.Equals(_lexer.Current.Text, "FILTER", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var snapshot = _lexer.Snapshot();
        _lexer.Next();
        if (_lexer.Current.Kind != TokenKind.LeftParen)
        {
            _lexer.Restore(snapshot);
            return null;
        }

        Expect(TokenKind.LeftParen);
        ExpectKeyword("WHERE");
        var condition = ParseExpression();
        Expect(TokenKind.RightParen);
        return condition;
    }

    // Parses the trailing FILTER (WHERE ...) and OVER (...) clauses that may follow an
    // aggregate call, in the order SQLite accepts them.
    private (Expression? Filter, WindowSpecification? Window) ParseFunctionSuffix()
    {
        var filter = ParseFilter();
        var window = ParseOver();
        return (filter, window);
    }

    private WindowSpecification? ParseOver()
    {
        if (!ConsumeKeyword("OVER"))
            return null;

        if (_lexer.Current.Kind != TokenKind.LeftParen)
        {
            return new WindowSpecification(
                ExpectIdentifier(),
                [],
                [],
                null,
                IsNamedReference: true);
        }

        Expect(TokenKind.LeftParen);
        var specification = ParseWindowSpecification();
        Expect(TokenKind.RightParen);
        return specification;
    }

    private WindowSpecification ParseWindowSpecification()
    {
        string? baseWindowName = null;
        if (_lexer.Current.Kind == TokenKind.Identifier
            && !CurrentIsKeyword("PARTITION")
            && !CurrentIsKeyword("ORDER")
            && !CurrentIsKeyword("ROWS")
            && !CurrentIsKeyword("RANGE")
            && !CurrentIsKeyword("GROUPS")
            && !CurrentIsKeyword("EXCLUDE"))
        {
            baseWindowName = ExpectIdentifier();
        }

        var partitionBy = new List<Expression>();
        if (ConsumeKeyword("PARTITION"))
        {
            ExpectKeyword("BY");
            do
            {
                partitionBy.Add(ParseExpression());
            }
            while (Consume(TokenKind.Comma));
        }

        var orderBy = new List<OrderByTerm>();
        if (ConsumeKeyword("ORDER"))
        {
            ExpectKeyword("BY");
            do
            {
                orderBy.Add(ParseOrderByTerm());
            }
            while (Consume(TokenKind.Comma));
        }

        var frame = ParseWindowFrame();
        return new WindowSpecification(baseWindowName, partitionBy, orderBy, frame);
    }

    private WindowFrame? ParseWindowFrame()
    {
        WindowFrameMode mode;
        if (ConsumeKeyword("ROWS"))
            mode = WindowFrameMode.Rows;
        else if (ConsumeKeyword("RANGE"))
            mode = WindowFrameMode.Range;
        else if (ConsumeKeyword("GROUPS"))
            mode = WindowFrameMode.Groups;
        else
            return null;

        FrameBound start;
        FrameBound end;
        if (ConsumeKeyword("BETWEEN"))
        {
            start = ParseFrameBound();
            ExpectKeyword("AND");
            end = ParseFrameBound();
        }
        else
        {
            start = ParseFrameBound();
            end = new FrameBound(FrameBoundKind.CurrentRow, null);
        }

        ValidateFrameBounds(start, end);
        return new WindowFrame(mode, start, end, ParseFrameExclusion());
    }

    private FrameExclusion ParseFrameExclusion()
    {
        if (!ConsumeKeyword("EXCLUDE"))
            return FrameExclusion.NoOthers;
        if (ConsumeKeyword("NO"))
        {
            ExpectKeyword("OTHERS");
            return FrameExclusion.NoOthers;
        }
        if (ConsumeKeyword("CURRENT"))
        {
            ExpectKeyword("ROW");
            return FrameExclusion.CurrentRow;
        }
        if (ConsumeKeyword("GROUP"))
            return FrameExclusion.Group;
        if (ConsumeKeyword("TIES"))
            return FrameExclusion.Ties;

        throw Error("Expected NO OTHERS, CURRENT ROW, GROUP, or TIES after EXCLUDE.");
    }

    private FrameBound ParseFrameBound()
    {
        if (ConsumeKeyword("UNBOUNDED"))
        {
            if (ConsumeKeyword("PRECEDING"))
                return new FrameBound(FrameBoundKind.UnboundedPreceding, null);

            ExpectKeyword("FOLLOWING");
            return new FrameBound(FrameBoundKind.UnboundedFollowing, null);
        }

        if (ConsumeKeyword("CURRENT"))
        {
            ExpectKeyword("ROW");
            return new FrameBound(FrameBoundKind.CurrentRow, null);
        }

        var offset = ParseExpression();
        if (ConsumeKeyword("PRECEDING"))
            return new FrameBound(FrameBoundKind.Preceding, offset);

        ExpectKeyword("FOLLOWING");
        return new FrameBound(FrameBoundKind.Following, offset);
    }

    private void ValidateFrameBounds(FrameBound start, FrameBound end)
    {
        if (start.Kind == FrameBoundKind.UnboundedFollowing)
            throw Error("A window frame cannot start with UNBOUNDED FOLLOWING.");
        if (end.Kind == FrameBoundKind.UnboundedPreceding)
            throw Error("A window frame cannot end with UNBOUNDED PRECEDING.");
        if ((start.Kind == FrameBoundKind.Following && end.Kind is FrameBoundKind.CurrentRow or FrameBoundKind.Preceding)
            || (start.Kind == FrameBoundKind.CurrentRow && end.Kind == FrameBoundKind.Preceding))
        {
            throw Error("Invalid window frame boundary ordering.");
        }
    }

    private TableSource ParseTableSource()
    {
        var source = ParseSimpleTableSource();
        while (true)
        {
            if (Consume(TokenKind.Comma))
            {
                source = new JoinTableSource(source, ParseSimpleTableSource(), null, JoinKind.Inner);
                continue;
            }

            if (ConsumeKeyword("CROSS"))
            {
                ExpectKeyword("JOIN");
                source = new JoinTableSource(source, ParseSimpleTableSource(), null, JoinKind.Inner);
                continue;
            }

            var natural = ConsumeKeyword("NATURAL");

            JoinKind kind;
            if (ConsumeKeyword("LEFT"))
            {
                ConsumeKeyword("OUTER");
                kind = JoinKind.Left;
            }
            else if (ConsumeKeyword("RIGHT"))
            {
                ConsumeKeyword("OUTER");
                kind = JoinKind.Right;
            }
            else if (ConsumeKeyword("FULL"))
            {
                ConsumeKeyword("OUTER");
                kind = JoinKind.Full;
            }
            else
            {
                ConsumeKeyword("INNER");
                kind = JoinKind.Inner;
            }

            if (!ConsumeKeyword("JOIN"))
            {
                if (natural || kind != JoinKind.Inner)
                    throw Error("Expected JOIN.");

                return source;
            }

            var right = ParseSimpleTableSource();
            Expression? condition = null;
            IReadOnlyList<string>? usingColumns = null;
            if (ConsumeKeyword("ON"))
            {
                condition = ParseExpression();
            }
            else if (ConsumeKeyword("USING"))
            {
                Expect(TokenKind.LeftParen);
                usingColumns = ParseIdentifierList();
                Expect(TokenKind.RightParen);
            }

            if (natural && (condition is not null || usingColumns is not null))
                throw Error("NATURAL joins may not have an ON or USING clause.");

            source = new JoinTableSource(source, right, condition, kind, usingColumns, natural);
        }
    }

    private TableSource ParseSimpleTableSource()
    {
        if (Consume(TokenKind.LeftParen))
        {
            if (IsQueryStart())
            {
                var query = ParseQuery();
                Expect(TokenKind.RightParen);
                return new DerivedTableSource(query, ParseTableAlias());
            }

            // Parenthesized join clause, e.g. a JOIN (b JOIN c ON ...) ON ...
            var inner = ParseTableSource();
            Expect(TokenKind.RightParen);
            return inner;
        }

        var name = ParseSchemaQualifiedName();
        if (_lexer.Current.Kind != TokenKind.LeftParen)
        {
            var alias = ParseTableAlias();
            return new NamedTableSource(name, alias, ParseTableIndexDirective());
        }

        var qualified = ManagedSchemaName.TrySplit(name, out var schema, out var functionName);
        if (!TableValuedFunctionRegistry.TryResolve(functionName, out var module))
            throw Error(TableValuedFunctionRegistry.UnsupportedMessage(ManagedSchemaName.Display(name)));

        Expect(TokenKind.LeftParen);
        var arguments = new List<Expression>();
        if (!Consume(TokenKind.RightParen))
        {
            do
            {
                arguments.Add(ParseExpression());
            }
            while (Consume(TokenKind.Comma));
            Expect(TokenKind.RightParen);
        }

        if (arguments.Count > module.MaximumArgumentCount)
        {
            throw Error(
                $"too many arguments on {functionName}() - max {module.MaximumArgumentCount}");
        }
        if (arguments.Count < module.MinimumArgumentCount)
        {
            throw Error(
                $"too few arguments on {functionName}() - min {module.MinimumArgumentCount}");
        }

        return new TableValuedFunctionSource(
            functionName,
            arguments,
            ParseTableAlias(),
            qualified ? schema : null);
    }

    private string? ParseTableAlias()
    {
        if (ConsumeKeyword("AS"))
            return ExpectIdentifier();
        if (_lexer.Current.Kind == TokenKind.Identifier
            && (_lexer.Current.IsQuoted || !IsTableSourceClauseKeyword(_lexer.Current.Text)))
            return ExpectIdentifier();

        return null;
    }

    private TableIndexDirective? ParseTableIndexDirective()
    {
        if (ConsumeKeyword("INDEXED"))
        {
            ExpectKeyword("BY");
            return new IndexedByDirective(ExpectIdentifier());
        }
        if (ConsumeKeyword("NOT"))
        {
            ExpectKeyword("INDEXED");
            return new NotIndexedDirective();
        }

        return null;
    }

    private static bool IsTableSourceClauseKeyword(string keyword)
    {
        return keyword.Equals("CROSS", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("EXCEPT", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("FULL", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("GROUP", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("HAVING", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("INDEXED", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("INNER", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("JOIN", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("LIMIT", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("LEFT", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("NATURAL", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("NOT", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("ON", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("ORDER", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("OUTER", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("RIGHT", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("RETURNING", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("INTERSECT", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("UNION", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("USING", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("WHERE", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("WINDOW", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsQueryStart()
    {
        return _lexer.Current.Kind == TokenKind.Identifier
            && (string.Equals(_lexer.Current.Text, "SELECT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(_lexer.Current.Text, "VALUES", StringComparison.OrdinalIgnoreCase)
                || string.Equals(_lexer.Current.Text, "WITH", StringComparison.OrdinalIgnoreCase));
    }

    private Expression ParseExpression() => ParseOr();

    private Expression ParseOr()
    {
        var expression = ParseAnd();
        while (ConsumeKeyword("OR"))
            expression = new BinaryExpression(expression, BinaryOperator.Or, ParseAnd());

        return expression;
    }

    private Expression ParseAnd()
    {
        var expression = ParseNot();
        while (ConsumeKeyword("AND"))
            expression = new BinaryExpression(expression, BinaryOperator.And, ParseNot());

        return expression;
    }

    private Expression ParseNot()
    {
        if (ConsumeKeyword("NOT"))
        {
            if (ConsumeKeyword("EXISTS"))
                return new ExistsExpression(ParseParenthesizedQuery(), Negated: true);

            return new UnaryExpression(UnaryOperator.Not, ParseNot());
        }

        return ParseComparison();
    }

    /// <summary>
    /// Parses the right operand of IS / IS NOT. A leading NOT negates a whole comparison in
    /// SQLite, so <c>1 IS NOT NOT 2 = 3</c> negates <c>2 = 3</c>, while an operand without NOT
    /// stays at the relational level so chained <c>IS</c> keeps its left associativity.
    /// </summary>
    private Expression ParseIsRightOperand()
        => CurrentIsKeyword("NOT") ? ParseNot() : ParseRelational();

    private Expression ParseComparison()
    {
        var expression = ParseRelational();
        while (true)
        {
            if (ConsumeKeyword("IS"))
            {
                var isNot = ConsumeKeyword("NOT");
                var distinct = ConsumeKeyword("DISTINCT");
                if (distinct)
                    ExpectKeyword("FROM");
                expression = new BinaryExpression(
                    expression,
                    distinct
                        ? isNot ? BinaryOperator.Is : BinaryOperator.IsNot
                        : isNot ? BinaryOperator.IsNot : BinaryOperator.Is,
                    ParseIsRightOperand());
                continue;
            }
            var negated = ConsumeKeyword("NOT");
            if (ConsumeKeyword("BETWEEN"))
            {
                var lower = ParseRelational();
                ExpectKeyword("AND");
                expression = new BetweenExpression(expression, lower, ParseRelational(), negated);
                continue;
            }
            if (ConsumeKeyword("IN"))
            {
                Expect(TokenKind.LeftParen);
                if (IsQueryStart())
                {
                    var query = ParseQuery();
                    Expect(TokenKind.RightParen);
                    expression = new InSubqueryExpression(expression, query, negated);
                    continue;
                }

                var values = new List<Expression>();
                if (!Consume(TokenKind.RightParen))
                {
                    values.Add(ParseExpression());
                    while (Consume(TokenKind.Comma))
                        values.Add(ParseExpression());
                    Expect(TokenKind.RightParen);
                }

                expression = new InExpression(expression, values, negated);
                continue;
            }
            if (ConsumeKeyword("LIKE"))
            {
                var pattern = ParseRelational();
                Expression? escape = null;
                if (ConsumeKeyword("ESCAPE"))
                    escape = ParseRelational();

                expression = new LikeExpression(expression, pattern, escape, negated);
                continue;
            }
            if (ConsumeKeyword("GLOB"))
            {
                expression = new GlobExpression(expression, ParseRelational(), negated);
                continue;
            }
            if (CurrentIsKeyword("REGEXP") || CurrentIsKeyword("MATCH"))
            {
                var functionName = _lexer.Current.Text.ToUpperInvariant();
                _lexer.Next();
                Expression function = new FunctionExpression(
                    functionName,
                    [ParseRelational(), expression],
                    CountStar: false);
                expression = negated
                    ? new UnaryExpression(UnaryOperator.Not, function)
                    : function;
                continue;
            }
            if (negated)
                throw Error("Expected BETWEEN, IN, LIKE, GLOB, REGEXP, or MATCH after NOT.");
            if (!TryParseEqualityOperator(out var operation))
                return expression;

            expression = new BinaryExpression(expression, operation, ParseRelational());
        }
    }

    private Expression ParseRelational()
    {
        var expression = ParseBitwise();
        while (TryParseRelationalOperator(out var operation))
            expression = new BinaryExpression(expression, operation, ParseBitwise());

        return expression;
    }

    private Expression ParseBitwise()
    {
        var expression = ParseAddSubtract();
        while (true)
        {
            if (Consume(TokenKind.BitwiseAnd))
                expression = new BinaryExpression(expression, BinaryOperator.BitwiseAnd, ParseAddSubtract());
            else if (Consume(TokenKind.BitwiseOr))
                expression = new BinaryExpression(expression, BinaryOperator.BitwiseOr, ParseAddSubtract());
            else if (Consume(TokenKind.ShiftLeft))
                expression = new BinaryExpression(expression, BinaryOperator.ShiftLeft, ParseAddSubtract());
            else if (Consume(TokenKind.ShiftRight))
                expression = new BinaryExpression(expression, BinaryOperator.ShiftRight, ParseAddSubtract());
            else
                return expression;
        }
    }

    private Expression ParseAddSubtract()
    {
        var expression = ParseMultiplyDivide();
        while (true)
        {
            if (Consume(TokenKind.Plus))
                expression = new BinaryExpression(expression, BinaryOperator.Add, ParseMultiplyDivide());
            else if (Consume(TokenKind.Minus))
                expression = new BinaryExpression(expression, BinaryOperator.Subtract, ParseMultiplyDivide());
            else
                return expression;
        }
    }

    private Expression ParseMultiplyDivide()
    {
        var expression = ParseConcatenate();
        while (true)
        {
            if (Consume(TokenKind.Asterisk))
                expression = new BinaryExpression(expression, BinaryOperator.Multiply, ParseConcatenate());
            else if (Consume(TokenKind.Slash))
                expression = new BinaryExpression(expression, BinaryOperator.Divide, ParseConcatenate());
            else if (Consume(TokenKind.Percent))
                expression = new BinaryExpression(expression, BinaryOperator.Modulo, ParseConcatenate());
            else
                return expression;
        }
    }

    private Expression ParseConcatenate()
    {
        var expression = ParseCollation();
        while (true)
        {
            if (Consume(TokenKind.Concatenate))
                expression = new BinaryExpression(expression, BinaryOperator.Concatenate, ParseCollation());
            else if (Consume(TokenKind.JsonArrow))
                expression = new BinaryExpression(expression, BinaryOperator.JsonArrow, ParseCollation());
            else if (Consume(TokenKind.JsonArrowText))
                expression = new BinaryExpression(expression, BinaryOperator.JsonArrowText, ParseCollation());
            else
                return expression;
        }
    }

    private Expression ParseCollation()
    {
        var expression = ParseUnary();
        while (ConsumeKeyword("COLLATE"))
            expression = new CollationExpression(expression, ExpectIdentifier());

        return expression;
    }

    private Expression ParseUnary()
    {
        if (Consume(TokenKind.Plus))
            return new UnaryExpression(UnaryOperator.Plus, ParseUnary());
        if (Consume(TokenKind.Minus))
        {
            if (_lexer.Current is { Kind: TokenKind.Integer, Text: "9223372036854775808" })
            {
                _lexer.Next();
                return new LiteralExpression(SqlValue.Integer(long.MinValue));
            }

            return new UnaryExpression(UnaryOperator.Negate, ParseUnary());
        }
        if (Consume(TokenKind.BitwiseNot))
            return new UnaryExpression(UnaryOperator.BitwiseNot, ParseUnary());

        return ParsePrimary();
    }

    private Expression ParseSignedPrimary()
    {
        if (Consume(TokenKind.Plus))
            return new UnaryExpression(UnaryOperator.Plus, ParseSignedPrimary());
        if (Consume(TokenKind.Minus))
        {
            if (_lexer.Current is { Kind: TokenKind.Integer, Text: "9223372036854775808" })
            {
                _lexer.Next();
                return new LiteralExpression(SqlValue.Integer(long.MinValue));
            }

            return new UnaryExpression(UnaryOperator.Negate, ParseSignedPrimary());
        }

        return ParsePrimary();
    }

    private Expression ParsePrimary()
    {
        if (Consume(TokenKind.LeftParen))
        {
            if (IsQueryStart())
            {
                var query = ParseQuery();
                Expect(TokenKind.RightParen);
                return new ScalarSubqueryExpression(query);
            }

            var expression = ParseExpression();
            if (Consume(TokenKind.Comma))
            {
                var values = new List<Expression> { expression, ParseExpression() };
                while (Consume(TokenKind.Comma))
                    values.Add(ParseExpression());
                Expect(TokenKind.RightParen);
                return new RowValueExpression(values);
            }

            Expect(TokenKind.RightParen);
            return expression;
        }
        if (ConsumeKeyword("EXISTS"))
            return new ExistsExpression(ParseParenthesizedQuery(), Negated: false);

        var token = _lexer.Current;
        switch (token.Kind)
        {
            case TokenKind.Integer:
                _lexer.Next();
                if (token.Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    && ulong.TryParse(
                        token.Text.AsSpan(2),
                        NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture,
                        out var hexadecimal))
                {
                    return new LiteralExpression(SqlValue.Integer(unchecked((long)hexadecimal)));
                }
                if (long.TryParse(token.Text, CultureInfo.InvariantCulture, out var integer))
                    return new LiteralExpression(SqlValue.Integer(integer));

                if (double.TryParse(token.Text, CultureInfo.InvariantCulture, out var real))
                    return new LiteralExpression(SqlValue.Real(real));

                throw Error($"Invalid numeric literal {token.Text}.");
            case TokenKind.Real:
                _lexer.Next();
                return new LiteralExpression(SqlValue.Real(double.Parse(token.Text, CultureInfo.InvariantCulture)));
            case TokenKind.String:
                _lexer.Next();
                return new LiteralExpression(SqlValue.Text(token.Text));
            case TokenKind.Blob:
                _lexer.Next();
                return new LiteralExpression(SqlValue.Blob(Convert.FromHexString(token.Text)));
            case TokenKind.Parameter:
                if (_inTriggerBody)
                    throw Error("Bind parameters are not supported in trigger bodies.");

                _lexer.Next();
                return new ParameterExpression(ResolveParameterIndex(token.Text));
            case TokenKind.Identifier:
                _lexer.Next();
                if (_inTriggerBody
                    && !token.IsQuoted
                    && string.Equals(token.Text, "RAISE", StringComparison.OrdinalIgnoreCase)
                    && Consume(TokenKind.LeftParen))
                {
                    return ParseRaiseExpression();
                }
                if (Consume(TokenKind.Dot))
                {
                    var columnToken = ExpectIdentifierToken();
                    var qualified = new ColumnExpression(
                        token.Text + "." + columnToken.Text,
                        token.Text,
                        columnToken.Text);
                    if (_spans is not null)
                    {
                        _spans.RecordQualifier(qualified, token);
                        _spans.RecordName(qualified, columnToken);
                    }

                    return qualified;
                }
                if (!token.IsQuoted
                    && string.Equals(token.Text, "NULL", StringComparison.OrdinalIgnoreCase))
                    return new LiteralExpression(SqlValue.Null);
                if (!token.IsQuoted
                    && string.Equals(token.Text, "CURRENT_DATE", StringComparison.OrdinalIgnoreCase))
                    return new CurrentTimeExpression(CurrentTimeKind.Date);
                if (!token.IsQuoted
                    && string.Equals(token.Text, "CURRENT_TIME", StringComparison.OrdinalIgnoreCase))
                    return new CurrentTimeExpression(CurrentTimeKind.Time);
                if (!token.IsQuoted
                    && string.Equals(token.Text, "CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase))
                    return new CurrentTimeExpression(CurrentTimeKind.Timestamp);
                if (!token.IsQuoted
                    && string.Equals(token.Text, "CASE", StringComparison.OrdinalIgnoreCase))
                    return ParseCaseExpression();
                if (!token.IsQuoted
                    && string.Equals(token.Text, "CAST", StringComparison.OrdinalIgnoreCase)
                    && Consume(TokenKind.LeftParen))
                {
                    var expression = ParseExpression();
                    ExpectKeyword("AS");
                    var typeName = ExpectIdentifier();
                    if (_lexer.Current.Kind == TokenKind.LeftParen)
                        SkipParenthesized();
                    Expect(TokenKind.RightParen);
                    return new CastExpression(expression, typeName);
                }
                if (Consume(TokenKind.LeftParen))
                {
                    if (!token.IsQuoted
                        && string.Equals(token.Text, "RAISE", StringComparison.OrdinalIgnoreCase))
                        return ParseRaiseExpression();

                    var functionName = token.Text.ToUpperInvariant();
                    if (string.Equals(token.Text, "COUNT", StringComparison.OrdinalIgnoreCase) && Consume(TokenKind.Asterisk))
                    {
                        Expect(TokenKind.RightParen);
                        var (countFilter, countWindow) = ParseFunctionSuffix();
                        return new FunctionExpression("COUNT", [], true, false, countFilter, countWindow);
                    }

                    var distinct = ConsumeKeyword("DISTINCT");
                    if (!distinct)
                        ConsumeKeyword("ALL");

                    if (Consume(TokenKind.RightParen))
                    {
                        var (emptyFilter, emptyWindow) = ParseFunctionSuffix();
                        return new FunctionExpression(functionName, [], false, false, emptyFilter, emptyWindow);
                    }

                    var arguments = new List<Expression> { ParseExpression() };
                    while (Consume(TokenKind.Comma))
                        arguments.Add(ParseExpression());
                    IReadOnlyList<OrderByTerm>? aggregateOrderBy = null;
                    if (ConsumeKeyword("ORDER"))
                    {
                        ExpectKeyword("BY");
                        var orderByTerms = new List<OrderByTerm> { ParseAggregateOrderByTerm() };
                        while (Consume(TokenKind.Comma))
                            orderByTerms.Add(ParseAggregateOrderByTerm());

                        aggregateOrderBy = orderByTerms;
                    }
                    Expect(TokenKind.RightParen);
                    if (string.Equals(token.Text, "COUNT", StringComparison.OrdinalIgnoreCase) && arguments.Count != 1)
                        throw Error("wrong number of arguments to function COUNT()");

                    var (filter, window) = ParseFunctionSuffix();
                    return new FunctionExpression(functionName, arguments, false, distinct, filter, window, aggregateOrderBy);
                }

                var bareColumn = new ColumnExpression(token.Text, BooleanKeyword: GetBooleanKeyword(token));
                _spans?.RecordName(bareColumn, token);
                return bareColumn;
            default:
                throw Error("Expected an expression.");
        }
    }

    /// <summary>
    /// Reports whether <paramref name="token"/> is a bare <c>TRUE</c>/<c>FALSE</c> keyword. Those are
    /// not reserved words in SQLite, so the name still has to be resolved against the columns in
    /// scope first; the value is only the fallback used when no such column exists.
    /// </summary>
    private static bool? GetBooleanKeyword(SqlToken token)
    {
        if (token.IsQuoted)
            return null;
        if (string.Equals(token.Text, "TRUE", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(token.Text, "FALSE", StringComparison.OrdinalIgnoreCase))
            return false;

        return null;
    }

    private Expression ParseRaiseExpression()
    {
        if (!_inTriggerBody)
            throw Error("RAISE() may only be used within a trigger program.");
        if (ConsumeKeyword("IGNORE"))
        {
            Expect(TokenKind.RightParen);
            return new RaiseExpression(RaiseAction.Ignore, null);
        }

        var action = ConsumeKeyword("ROLLBACK")
            ? RaiseAction.Rollback
            : ConsumeKeyword("ABORT")
                ? RaiseAction.Abort
                : ConsumeKeyword("FAIL")
                    ? RaiseAction.Fail
                    : throw Error("Expected ROLLBACK, ABORT, FAIL, or IGNORE in RAISE().");
        Expect(TokenKind.Comma);
        if (_lexer.Current.Kind != TokenKind.String)
            throw Error("RAISE() error messages must be string literals.");

        var message = _lexer.Current.Text;
        _lexer.Next();
        Expect(TokenKind.RightParen);
        return new RaiseExpression(action, message);
    }

    private QueryStatement ParseParenthesizedQuery()
    {
        Expect(TokenKind.LeftParen);
        if (!IsQueryStart())
            throw Error("Expected a SELECT query.");

        var query = ParseQuery();
        Expect(TokenKind.RightParen);
        return query;
    }

    private Expression ParseCaseExpression()
    {
        Expression? operand = null;
        if (!ConsumeKeyword("WHEN"))
        {
            operand = ParseExpression();
            ExpectKeyword("WHEN");
        }

        var clauses = new List<CaseClause>();
        do
        {
            var when = ParseExpression();
            ExpectKeyword("THEN");
            clauses.Add(new CaseClause(when, ParseExpression()));
        }
        while (ConsumeKeyword("WHEN"));

        Expression? elseExpression = null;
        if (ConsumeKeyword("ELSE"))
            elseExpression = ParseExpression();
        ExpectKeyword("END");
        return new CaseExpression(operand, clauses, elseExpression);
    }

    private int ResolveParameterIndex(string token)
    {
        if (token == "?")
            return ++_maximumParameterIndex;

        if (token[0] == '?')
        {
            var numberedIndex = int.Parse(token.AsSpan(1), CultureInfo.InvariantCulture);
            _maximumParameterIndex = Math.Max(_maximumParameterIndex, numberedIndex);
            return numberedIndex;
        }

        if (_namedParameterIndices.TryGetValue(token, out var index))
        {
            _maximumParameterIndex = Math.Max(_maximumParameterIndex, index);
            return index;
        }

        throw Error($"Parameter {token} was not found.");
    }

    private bool TryParseEqualityOperator(out BinaryOperator operation)
    {
        if (Consume(TokenKind.Equal))
        {
            operation = BinaryOperator.Equal;
            return true;
        }
        if (Consume(TokenKind.NotEqual))
        {
            operation = BinaryOperator.NotEqual;
            return true;
        }

        operation = default;
        return false;
    }

    private bool TryParseRelationalOperator(out BinaryOperator operation)
    {
        if (Consume(TokenKind.LessThan))
        {
            operation = BinaryOperator.LessThan;
            return true;
        }
        if (Consume(TokenKind.LessThanOrEqual))
        {
            operation = BinaryOperator.LessThanOrEqual;
            return true;
        }
        if (Consume(TokenKind.GreaterThan))
        {
            operation = BinaryOperator.GreaterThan;
            return true;
        }
        if (Consume(TokenKind.GreaterThanOrEqual))
        {
            operation = BinaryOperator.GreaterThanOrEqual;
            return true;
        }

        operation = default;
        return false;
    }

    private string[] ParseIdentifierList()
        => ParseIdentifierList(out _);

    private string[] ParseIdentifierList(out IReadOnlyList<SqlToken> tokens)
    {
        var collected = new List<SqlToken> { ExpectIdentifierToken() };
        while (Consume(TokenKind.Comma))
            collected.Add(ExpectIdentifierToken());

        tokens = collected;
        return collected.Select(static token => token.Text).ToArray();
    }

    private EmbeddedColumn ParseColumnDefinition()
    {
        var name = ExpectIdentifier();
        var declaredType = ParseDeclaredType();

        var primaryKey = false;
        var primaryKeyDescending = false;
        var autoIncrement = false;
        var notNull = false;
        var unique = false;
        SqlValue? defaultValue = null;
        Expression? defaultExpression = null;
        string? defaultSql = null;
        string? collation = null;
        Expression? generationExpression = null;
        var generatedStored = false;
        string? generationSql = null;
        var foreignKeys = new List<ForeignKeyDefinition>();
        var checks = new List<CheckConstraint>();
        InsertConflictAlgorithm? primaryKeyConflictAlgorithm = null;
        InsertConflictAlgorithm? notNullConflictAlgorithm = null;
        InsertConflictAlgorithm? uniqueConflictAlgorithm = null;
        string? primaryKeyConstraintName = null;
        string? notNullConstraintName = null;
        string? uniqueConstraintName = null;
        string? defaultConstraintName = null;
        string? collationConstraintName = null;
        string? generationConstraintName = null;
        string? nullConstraintName = null;
        var explicitNull = false;
        var generationAlways = false;
        var keyConstraintOrder = 0;
        int? primaryKeyDeclarationOrder = null;
        int? uniqueDeclarationOrder = null;
        string? pendingConstraintName = null;
        while (_lexer.Current.Kind == TokenKind.Identifier)
        {
            if (ConsumeKeyword("CONSTRAINT"))
            {
                pendingConstraintName = ExpectIdentifier();
                continue;
            }
            if (ConsumeKeyword("PRIMARY"))
            {
                ExpectKeyword("KEY");
                primaryKey = true;
                primaryKeyDeclarationOrder ??= keyConstraintOrder++;
                primaryKeyConstraintName = pendingConstraintName;
                pendingConstraintName = null;

                // A trailing ASC keeps the rowid-alias behavior; DESC disqualifies the
                // column from aliasing the rowid, matching SQLite.
                if (!ConsumeKeyword("ASC") && ConsumeKeyword("DESC"))
                    primaryKeyDescending = true;

                primaryKeyConflictAlgorithm = ParseConflictClause();
                continue;
            }
            if (ConsumeKeyword("AUTOINCREMENT"))
            {
                if (!primaryKey || autoIncrement)
                    throw Error("AUTOINCREMENT is only allowed on an INTEGER PRIMARY KEY");

                autoIncrement = true;
                continue;
            }
            if (ConsumeKeyword("NOT"))
            {
                ExpectKeyword("NULL");
                notNull = true;
                notNullConstraintName = pendingConstraintName;
                pendingConstraintName = null;
                notNullConflictAlgorithm = ParseConflictClause();
                continue;
            }
            if (ConsumeKeyword("NULL"))
            {
                explicitNull = true;
                nullConstraintName = pendingConstraintName;
                pendingConstraintName = null;
                continue;
            }
            if (ConsumeKeyword("UNIQUE"))
            {
                unique = true;
                uniqueDeclarationOrder ??= keyConstraintOrder++;
                uniqueConstraintName = pendingConstraintName;
                pendingConstraintName = null;
                uniqueConflictAlgorithm = ParseConflictClause();
                continue;
            }
            if (ConsumeKeyword("COLLATE"))
            {
                collation = ExpectIdentifier();
                collationConstraintName = pendingConstraintName;
                pendingConstraintName = null;
                continue;
            }
            if (ConsumeKeyword("DEFAULT"))
            {
                var startOffset = _lexer.Current.Offset;
                var expression = _lexer.Current.Kind == TokenKind.LeftParen
                    ? ParseExpression()
                    : ParseSignedPrimary();
                var endOffset = _lexer.Current.Offset;
                defaultSql = _sql[startOffset..endOffset].Trim();
                if (TryGetLiteralDefault(expression, out var literalValue))
                    defaultValue = literalValue;
                else
                    defaultExpression = expression;
                defaultConstraintName = pendingConstraintName;
                pendingConstraintName = null;
                continue;
            }
            // GENERATED ALWAYS AS (expr) and the bare AS (expr) shorthand both declare a
            // computed column. The raw expression text is captured verbatim so the column
            // round-trips through schema regeneration.
            if (ConsumeKeyword("GENERATED"))
            {
                ExpectKeyword("ALWAYS");
                ExpectKeyword("AS");
                (generationExpression, generationSql, generatedStored) = ParseGenerationClause();
                generationAlways = true;
                generationConstraintName = pendingConstraintName;
                pendingConstraintName = null;
                continue;
            }
            if (ConsumeKeyword("AS"))
            {
                (generationExpression, generationSql, generatedStored) = ParseGenerationClause();
                generationConstraintName = pendingConstraintName;
                pendingConstraintName = null;
                continue;
            }
            if (ConsumeKeyword("REFERENCES"))
            {
                foreignKeys.Add(ParseForeignKeyReference([name], pendingConstraintName));
                pendingConstraintName = null;
                continue;
            }
            if (ConsumeKeyword("FOREIGN"))
                throw Error("FOREIGN KEY constraints must be table-level.");
            if (ConsumeKeyword("CHECK"))
            {
                var (expression, sql) = ParseParenthesizedSchemaExpression("CHECK");
                checks.Add(new CheckConstraint(pendingConstraintName, expression, sql));
                pendingConstraintName = null;
                continue;
            }

            throw Error($"Unsupported column constraint '{_lexer.Current.Text}'.");
        }

        if (pendingConstraintName is not null)
            throw Error($"Expected a constraint after CONSTRAINT {pendingConstraintName}.");

        return new EmbeddedColumn(
            name,
            declaredType,
            primaryKey,
            notNull,
            unique,
            defaultValue,
            primaryKeyDescending,
            generationExpression,
            generatedStored,
            generationSql,
            collation,
            foreignKeys.FirstOrDefault(),
            checks,
            defaultExpression,
            defaultSql,
            primaryKeyConflictAlgorithm,
            notNullConflictAlgorithm,
            uniqueConflictAlgorithm,
            primaryKeyConstraintName,
            notNullConstraintName,
            uniqueConstraintName,
            defaultConstraintName,
            collationConstraintName,
            generationConstraintName,
            nullConstraintName,
            explicitNull,
            generationAlways,
            autoIncrement,
            foreignKeys.Skip(1).ToArray(),
            primaryKeyDeclarationOrder,
            uniqueDeclarationOrder);
    }

    private string? ParseDeclaredType()
    {
        if (_lexer.Current.Kind is TokenKind.Comma or TokenKind.RightParen)
            return null;
        if (_lexer.Current.Kind == TokenKind.Identifier && IsColumnConstraintKeyword(_lexer.Current.Text))
            return null;

        var startOffset = _lexer.Current.Offset;
        var depth = 0;
        while (_lexer.Current.Kind != TokenKind.End)
        {
            if (depth == 0)
            {
                if (_lexer.Current.Kind is TokenKind.Comma or TokenKind.RightParen)
                    break;
                if (_lexer.Current.Kind == TokenKind.Identifier && IsColumnConstraintKeyword(_lexer.Current.Text))
                    break;
            }

            if (_lexer.Current.Kind == TokenKind.LeftParen)
                depth++;
            else if (_lexer.Current.Kind == TokenKind.RightParen)
                depth--;
            _lexer.Next();
        }

        return _sql[startOffset.._lexer.Current.Offset].Trim();
    }

    private (Expression Expression, string Sql) ParseParenthesizedSchemaExpression(string constraint)
    {
        Expect(TokenKind.LeftParen);
        var startOffset = _lexer.Current.Offset;
        var expression = ParseExpression();
        var endOffset = _lexer.Current.Offset;
        Expect(TokenKind.RightParen);
        var sql = _sql[startOffset..endOffset].Trim();
        if (sql.Length == 0)
            throw Error($"{constraint} constraint requires an expression.");
        return (expression, sql);
    }

    private InsertConflictAlgorithm? ParseConflictClause()
    {
        if (!ConsumeKeyword("ON"))
            return null;

        ExpectKeyword("CONFLICT");
        if (ConsumeKeyword("ROLLBACK"))
            return InsertConflictAlgorithm.Rollback;
        if (ConsumeKeyword("ABORT"))
            return InsertConflictAlgorithm.Abort;
        if (ConsumeKeyword("FAIL"))
            return InsertConflictAlgorithm.Fail;
        if (ConsumeKeyword("IGNORE"))
            return InsertConflictAlgorithm.Ignore;
        if (ConsumeKeyword("REPLACE"))
            return InsertConflictAlgorithm.Replace;

        throw Error("Expected ROLLBACK, ABORT, FAIL, IGNORE, or REPLACE after ON CONFLICT.");
    }

    // Parses the "(expr) [STORED|VIRTUAL]" body shared by GENERATED ALWAYS AS and the bare
    // AS shorthand. The raw expression source between the parentheses is captured so the
    // generated column can be regenerated verbatim; VIRTUAL is the SQLite default.
    private (Expression Expression, string Sql, bool Stored) ParseGenerationClause()
    {
        Expect(TokenKind.LeftParen);
        var startOffset = _lexer.Current.Offset;
        var expression = ParseExpression();
        var endOffset = _lexer.Current.Offset;
        Expect(TokenKind.RightParen);
        var rawSql = _sql[startOffset..endOffset].Trim();

        var stored = false;
        if (ConsumeKeyword("STORED"))
            stored = true;
        else
            ConsumeKeyword("VIRTUAL");

        return (expression, rawSql, stored);
    }

    private bool IsTableConstraintStart()
    {
        return _lexer.Current.Kind == TokenKind.Identifier
            && IsColumnConstraintKeyword(_lexer.Current.Text);
    }

    private static bool IsColumnConstraintKeyword(string keyword)
    {
        return keyword.Equals("AS", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("CHECK", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("COLLATE", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("CONSTRAINT", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("FOREIGN", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("GENERATED", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("NOT", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("NULL", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("REFERENCES", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("UNIQUE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetLiteralDefault(Expression expression, out SqlValue value)
    {
        if (expression is LiteralExpression literal)
        {
            value = literal.Value;
            return true;
        }

        // DEFAULT expressions are resolved with no columns in scope, so a bare TRUE/FALSE
        // keyword is always the integer literal 1/0 and stays a constant default.
        if (expression is ColumnExpression { BooleanKeyword: { } keyword })
        {
            value = SqlValue.Integer(keyword ? 1 : 0);
            return true;
        }

        if (expression is UnaryExpression
            {
                Operator: UnaryOperator.Negate,
                Operand: LiteralExpression right,
            })
        {
            value = right.Value.Kind switch
            {
                SqlValueKind.Integer => SqlValue.Integer(-right.Value.AsInteger()),
                SqlValueKind.Real => SqlValue.Real(-right.Value.AsReal()),
                _ => default,
            };
            return right.Value.Kind is SqlValueKind.Integer or SqlValueKind.Real;
        }
        if (expression is UnaryExpression
            {
                Operator: UnaryOperator.Plus,
                Operand: LiteralExpression positive,
            })
        {
            value = positive.Value;
            return true;
        }

        value = default;
        return false;
    }

    private void SkipParenthesized()
    {
        Expect(TokenKind.LeftParen);
        var depth = 1;
        while (depth > 0 && _lexer.Current.Kind != TokenKind.End)
        {
            if (_lexer.Current.Kind == TokenKind.LeftParen)
                depth++;
            else if (_lexer.Current.Kind == TokenKind.RightParen)
                depth--;

            _lexer.Next();
        }

        if (depth != 0)
            throw Error("Unterminated parenthesized column type.");
    }

    private bool ConsumeKeyword(string keyword)
    {
        if (_lexer.Current.Kind != TokenKind.Identifier
            || _lexer.Current.IsQuoted
            || !string.Equals(_lexer.Current.Text, keyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _lexer.Next();
        return true;
    }

    private bool CurrentIsKeyword(string keyword)
        => _lexer.Current.Kind == TokenKind.Identifier
            && !_lexer.Current.IsQuoted
            && string.Equals(_lexer.Current.Text, keyword, StringComparison.OrdinalIgnoreCase);

    private void ExpectKeyword(string keyword)
    {
        if (!ConsumeKeyword(keyword))
            throw Error($"Expected keyword {keyword}.");
    }

    private string ExpectIdentifier()
    {
        if (_lexer.Current.Kind != TokenKind.Identifier)
            throw Error("Expected an identifier.");

        var value = _lexer.Current.Text;
        _lexer.Next();
        return value;
    }

    private SqlToken ExpectIdentifierToken()
    {
        if (_lexer.Current.Kind != TokenKind.Identifier)
            throw Error("Expected an identifier.");

        var token = _lexer.Current;
        _lexer.Next();
        return token;
    }

    private string ParseSchemaQualifiedName()
    {
        var schemaOrName = ExpectIdentifier();
        if (!Consume(TokenKind.Dot))
            return schemaOrName;

        var name = ExpectIdentifier();
        if (_lexer.Current.Kind == TokenKind.Dot)
            throw Error("Only one schema qualifier is supported for database objects.");

        return ManagedSchemaName.Create(schemaOrName, name);
    }

    private string ParsePragmaQualifiedName()
    {
        var schemaOrName = ExpectIdentifierOrString();
        if (!Consume(TokenKind.Dot))
            return schemaOrName;

        var name = ExpectIdentifierOrString();
        if (_lexer.Current.Kind == TokenKind.Dot)
            throw Error("Only one schema qualifier is supported for database objects.");

        return ManagedSchemaName.Create(schemaOrName, name);
    }

    private string ExpectIdentifierOrString()
    {
        if (_lexer.Current.Kind is not (TokenKind.Identifier or TokenKind.String))
            throw Error("Expected an identifier.");

        var value = _lexer.Current.Text;
        _lexer.Next();
        return value;
    }

    private bool Consume(TokenKind kind)
    {
        if (_lexer.Current.Kind != kind)
            return false;

        _lexer.Next();
        return true;
    }

    private void Expect(TokenKind kind)
    {
        if (!Consume(kind))
            throw Error($"Expected {kind}.");
    }

    private EmbeddedSqlException Error(string message)
        => new($"{message} At SQL offset {_lexer.Current.Offset}.");
}
