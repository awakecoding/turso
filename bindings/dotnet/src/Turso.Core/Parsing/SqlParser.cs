using System.Globalization;
using Turso.Core;

namespace Turso.Core.Parsing;

internal sealed class SqlParser
{
    private readonly SqlLexer _lexer;
    private readonly string _sql;
    private readonly Dictionary<string, int> _namedParameterIndices = new(StringComparer.Ordinal);
    private int _maximumParameterIndex;
    private bool _inTriggerBody;

    private SqlParser(string sql, SqlParameterMap parameterMap)
    {
        _lexer = new SqlLexer(sql);
        _sql = sql;
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
        if (ConsumeKeyword("VACUUM"))
            return ParseVacuum();
        if (IsQueryStart())
            return ParseQuery();
        if (ConsumeKeyword("BEGIN"))
        {
            ConsumeKeyword("DEFERRED");
            ConsumeKeyword("IMMEDIATE");
            ConsumeKeyword("EXCLUSIVE");
            ConsumeKeyword("TRANSACTION");
            return new BeginStatement();
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
            return new VacuumStatement(null);

        var schema = ExpectIdentifier();
        if (!schema.Equals("main", StringComparison.OrdinalIgnoreCase))
            throw Error($"Unsupported VACUUM database {schema}.");
        return new VacuumStatement(schema);
    }

    private ParsedStatement ParsePragma()
    {
        var name = ExpectIdentifier();
        if (Consume(TokenKind.Dot))
        {
            var schema = name;
            name = ExpectIdentifier();
            if (!schema.Equals("main", StringComparison.OrdinalIgnoreCase))
                throw Error($"Unsupported PRAGMA database {schema}.");
        }

        if (name.Equals("table_info", StringComparison.OrdinalIgnoreCase))
            return new PragmaTableInfoStatement(ParsePragmaObjectName());
        if (name.Equals("table_xinfo", StringComparison.OrdinalIgnoreCase))
            return new PragmaTableXInfoStatement(ParsePragmaObjectName());
        if (name.Equals("index_list", StringComparison.OrdinalIgnoreCase))
            return new PragmaIndexListStatement(ParsePragmaObjectName());
        if (name.Equals("index_info", StringComparison.OrdinalIgnoreCase))
            return new PragmaIndexInfoStatement(ParsePragmaObjectName());
        if (name.Equals("table_list", StringComparison.OrdinalIgnoreCase))
        {
            RequireReadOnlyPragma(name);
            return new PragmaTableListStatement();
        }
        if (name.Equals("database_list", StringComparison.OrdinalIgnoreCase))
        {
            RequireReadOnlyPragma(name);
            return new PragmaDatabaseListStatement();
        }
        if (name.Equals("encoding", StringComparison.OrdinalIgnoreCase))
        {
            RequireReadOnlyPragma(name);
            return new PragmaEncodingStatement();
        }
        if (name.Equals("query_only", StringComparison.OrdinalIgnoreCase))
            return new PragmaQueryOnlyStatement(ParseOptionalPragmaBoolean(name));
        if (name.Equals("foreign_keys", StringComparison.OrdinalIgnoreCase))
            return new PragmaForeignKeysStatement(ParseOptionalPragmaBoolean(name));
        if (name.Equals("recursive_triggers", StringComparison.OrdinalIgnoreCase))
            return new PragmaRecursiveTriggersStatement(ParseOptionalPragmaBoolean(name));
        if (name.Equals("schema_version", StringComparison.OrdinalIgnoreCase))
        {
            return new PragmaHeaderIntegerStatement(
                PragmaHeaderIntegerKind.SchemaVersion,
                ParseOptionalPragmaInteger(name));
        }
        if (name.Equals("user_version", StringComparison.OrdinalIgnoreCase))
        {
            return new PragmaHeaderIntegerStatement(
                PragmaHeaderIntegerKind.UserVersion,
                ParseOptionalPragmaInteger(name));
        }
        if (name.Equals("application_id", StringComparison.OrdinalIgnoreCase))
        {
            return new PragmaHeaderIntegerStatement(
                PragmaHeaderIntegerKind.ApplicationId,
                ParseOptionalPragmaInteger(name));
        }
        if (name.Equals("journal_mode", StringComparison.OrdinalIgnoreCase))
            return new PragmaJournalModeStatement(ParseOptionalPragmaMode(name));
        if (name.Equals("page_size", StringComparison.OrdinalIgnoreCase))
            return new PragmaPageSizeStatement(ParseOptionalPragmaInteger(name));

        throw Error($"Unsupported PRAGMA {name}.");
    }

    private string ParsePragmaObjectName()
    {
        Expect(TokenKind.LeftParen);
        var objectName = ExpectIdentifier();
        Expect(TokenKind.RightParen);
        return objectName;
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
        if (ConsumeKeyword("UNIQUE"))
        {
            ExpectKeyword("INDEX");
            return ParseCreateIndex(unique: true);
        }
        if (ConsumeKeyword("INDEX"))
            return ParseCreateIndex(unique: false);
        if (ConsumeKeyword("VIEW"))
            return ParseCreateView();
        if (CurrentIsKeyword("TEMP") || CurrentIsKeyword("TEMPORARY"))
            throw Error("Temporary triggers and views are not supported.");
        if (ConsumeKeyword("TRIGGER"))
            return ParseCreateTrigger();

        return ParseCreateTable();
    }

    private ParsedStatement ParseCreateTable()
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
        Expect(TokenKind.LeftParen);
        var columns = new List<EmbeddedColumn>();
        IReadOnlyList<TablePrimaryKeyColumn>? tablePrimaryKey = null;
        InsertConflictAlgorithm? tablePrimaryKeyConflictAlgorithm = null;
        string? tablePrimaryKeyConstraintName = null;
        var uniqueConstraints = new List<TableUniqueConstraint>();
        var checkConstraints = new List<CheckConstraint>();
        do
        {
            if (IsTableConstraintStart())
            {
                var parsed = ParseTableConstraint();
                switch (parsed)
                {
                    case PrimaryKeyTableConstraint primaryKey:
                        if (tablePrimaryKey is not null)
                            throw Error("table has more than one primary key");

                        tablePrimaryKey = primaryKey.Columns;
                        tablePrimaryKeyConflictAlgorithm = primaryKey.ConflictAlgorithm;
                        tablePrimaryKeyConstraintName = primaryKey.Name;
                        break;
                    case ForeignKeyTableConstraint foreignKey:
                        AttachTableForeignKey(columns, foreignKey.Definition);
                        break;
                    case UniqueTableConstraint unique:
                        uniqueConstraints.Add(new TableUniqueConstraint(
                            unique.Name,
                            unique.Columns,
                            unique.ConflictAlgorithm));
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

        // WITHOUT ROWID makes the PRIMARY KEY the physical key; the trailing clause is only
        // valid after the closing parenthesis, matching SQLite's grammar.
        var withoutRowid = false;
        if (ConsumeKeyword("WITHOUT"))
        {
            if (!ConsumeKeyword("ROWID"))
                throw Error("Expected ROWID after WITHOUT.");

            withoutRowid = true;
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
            tablePrimaryKeyConstraintName);
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
            var keyColumns = ParseTableConstraintColumns();
            return new PrimaryKeyTableConstraint(constraintName, keyColumns, ParseConflictClause());
        }

        if (ConsumeKeyword("UNIQUE"))
        {
            var keyColumns = ParseTableConstraintColumns();
            return new UniqueTableConstraint(constraintName, keyColumns, ParseConflictClause());
        }

        if (ConsumeKeyword("FOREIGN"))
        {
            ExpectKeyword("KEY");
            Expect(TokenKind.LeftParen);
            var childColumn = ExpectIdentifier();
            if (Consume(TokenKind.Comma))
                throw Error("Composite foreign key constraints are not supported.");
            Expect(TokenKind.RightParen);
            ExpectKeyword("REFERENCES");
            return new ForeignKeyTableConstraint(ParseForeignKeyReference(childColumn));
        }

        if (ConsumeKeyword("CHECK"))
        {
            var (expression, sql) = ParseParenthesizedSchemaExpression("CHECK");
            return new CheckTableConstraint(constraintName, expression, sql, ParseConflictClause());
        }

        throw Error("Expected PRIMARY KEY, UNIQUE, CHECK, or FOREIGN KEY after table constraint name.");
    }

    private IReadOnlyList<TablePrimaryKeyColumn> ParseTableConstraintColumns()
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

            columns.Add(new TablePrimaryKeyColumn(columnName, descending, collation));
        }
        while (Consume(TokenKind.Comma));
        Expect(TokenKind.RightParen);
        return columns;
    }

    private static void AttachTableForeignKey(List<EmbeddedColumn> columns, ForeignKeyDefinition foreignKey)
    {
        var index = columns.FindIndex(column =>
            string.Equals(column.Name, foreignKey.ChildColumn, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            throw new EmbeddedSqlException($"foreign key constraint references unknown column: {foreignKey.ChildColumn}");
        if (columns[index].ForeignKey is not null)
            throw new EmbeddedSqlException($"multiple foreign key constraints on column {foreignKey.ChildColumn} are not supported");

        columns[index] = columns[index] with { ForeignKey = foreignKey };
    }

    private ForeignKeyDefinition ParseForeignKeyReference(string childColumn)
    {
        var parentTable = ExpectIdentifier();
        if (Consume(TokenKind.Dot))
            throw Error("Schema-qualified foreign keys are not supported.");
        if (!Consume(TokenKind.LeftParen))
            throw Error("Foreign key references must name exactly one parent column.");

        var parentColumn = ExpectIdentifier();
        if (Consume(TokenKind.Comma))
            throw Error("Composite foreign key constraints are not supported.");
        Expect(TokenKind.RightParen);

        if (CurrentIsKeyword("ON")
            || CurrentIsKeyword("MATCH")
            || CurrentIsKeyword("DEFERRABLE")
            || CurrentIsKeyword("NOT"))
        {
            throw Error("Foreign key actions, MATCH, and deferral are not supported.");
        }

        return new ForeignKeyDefinition(childColumn, parentTable, parentColumn);
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

        if (CurrentIsKeyword("WHERE"))
            throw Error("Partial indexes are not supported.");

        return new CreateIndexStatement(name, tableName, columns, unique, ifNotExists);
    }

    private IndexedColumnDefinition ParseIndexedColumn()
    {
        if (_lexer.Current.Kind != TokenKind.Identifier)
            throw Error("Expression indexes are not supported.");

        var name = ExpectIdentifier();
        string? collation = null;
        if (ConsumeKeyword("COLLATE"))
            collation = ExpectIdentifier();

        var descending = false;
        if (!ConsumeKeyword("ASC") && ConsumeKeyword("DESC"))
            descending = true;

        if (_lexer.Current.Kind is not TokenKind.Comma and not TokenKind.RightParen)
            throw Error("Expression indexes are not supported.");

        return new IndexedColumnDefinition(name, collation, descending);
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
                return new AlterTableRenameColumnStatement(tableName, columnName, ExpectIdentifier());
            }

            ExpectKeyword("TO");
            return new AlterTableRenameStatement(tableName, ExpectIdentifier());
        }

        throw Error("Expected ADD or RENAME after ALTER TABLE.");
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

    private ParsedStatement ParseCreateView()
    {
        var ifNotExists = ParseIfNotExists();
        var name = ParseSchemaQualifiedName();
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
        return new CreateViewStatement(name, columns, query, NormalizeObjectSql(), ifNotExists);
    }

    private ParsedStatement ParseCreateTrigger()
    {
        var ifNotExists = ParseIfNotExists();
        var name = ParseSchemaQualifiedName();

        if (ConsumeKeyword("BEFORE"))
            throw Error("BEFORE triggers are not supported.");
        if (ConsumeKeyword("INSTEAD"))
            throw Error("INSTEAD OF triggers are not supported.");
        if (!ConsumeKeyword("AFTER"))
            throw Error("Only AFTER triggers are supported; specify the AFTER timing explicitly.");

        var triggerEvent = ParseTriggerEvent();
        ExpectKeyword("ON");
        var tableName = ParseSchemaQualifiedName();

        if (ConsumeKeyword("FOR"))
            throw Error("FOR EACH ROW triggers are not supported.");
        if (CurrentIsKeyword("WHEN"))
            throw Error("WHEN clauses in triggers are not supported.");

        ExpectKeyword("BEGIN");
        var body = new List<ParsedStatement>();
        _inTriggerBody = true;
        try
        {
            while (!ConsumeKeyword("END"))
            {
                if (_lexer.Current.Kind == TokenKind.End)
                    throw Error("Expected END to close the trigger body.");

                body.Add(ParseTriggerBodyStatement());
                Expect(TokenKind.Semicolon);
            }
        }
        finally
        {
            _inTriggerBody = false;
        }

        if (body.Count == 0)
            throw Error("A trigger body must contain at least one statement.");

        return new CreateTriggerStatement(name, triggerEvent, tableName, body, NormalizeObjectSql(), ifNotExists);
    }

    private TriggerEvent ParseTriggerEvent()
    {
        if (ConsumeKeyword("INSERT"))
            return TriggerEvent.Insert;
        if (ConsumeKeyword("DELETE"))
            return TriggerEvent.Delete;
        if (ConsumeKeyword("UPDATE"))
        {
            if (ConsumeKeyword("OF"))
                throw Error("UPDATE OF column triggers are not supported.");

            return TriggerEvent.Update;
        }

        throw Error("Expected INSERT, UPDATE, or DELETE as the trigger event.");
    }

    private ParsedStatement ParseTriggerBodyStatement()
    {
        if (ConsumeKeyword("INSERT"))
            return ParseInsert();
        if (ConsumeKeyword("UPDATE"))
            return ParseUpdate();
        if (ConsumeKeyword("DELETE"))
            return ParseDelete();

        throw Error("Only INSERT, UPDATE, and DELETE statements are allowed in a trigger body.");
    }

    private ParsedStatement ParseDropView()
    {
        var ifExists = ParseIfExists();
        return new DropViewStatement(ExpectIdentifier(), ifExists);
    }

    private ParsedStatement ParseDropTrigger()
    {
        var ifExists = ParseIfExists();
        return new DropTriggerStatement(ExpectIdentifier(), ifExists);
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
    private string NormalizeObjectSql()
    {
        var text = _sql.Trim();
        while (text.EndsWith(';'))
            text = text[..^1].TrimEnd();

        return text;
    }

    private ParsedStatement ParseInsert()
    {
        var conflictAlgorithm = ParseInsertConflictAlgorithm();
        ExpectKeyword("INTO");
        var tableName = ParseSchemaQualifiedName();
        string[]? columns = null;
        if (Consume(TokenKind.LeftParen))
        {
            columns = ParseIdentifierList();
            Expect(TokenKind.RightParen);
        }

        var rows = new List<Expression[]>();
        QueryStatement? source = null;
        if (ConsumeKeyword("VALUES"))
        {
            do
            {
                Expect(TokenKind.LeftParen);
                var values = new List<Expression> { ParseExpression() };
                while (Consume(TokenKind.Comma))
                    values.Add(ParseExpression());
                Expect(TokenKind.RightParen);
                rows.Add(values.ToArray());
            }
            while (Consume(TokenKind.Comma));
        }
        else if (ConsumeKeyword("DEFAULT"))
        {
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
        return new InsertStatement(tableName, columns, rows, source, ParseReturning(), upsert, conflictAlgorithm);
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
        if (!Consume(TokenKind.LeftParen))
        {
            throw Error(
                "Managed UPSERT requires a parenthesized PRIMARY KEY or UNIQUE conflict target.");
        }

        var target = new List<UpsertTargetColumn>();
        do
        {
            var name = ExpectIdentifier();
            string? collation = null;
            if (ConsumeKeyword("COLLATE"))
                collation = ExpectIdentifier();
            if (CurrentIsKeyword("ASC") || CurrentIsKeyword("DESC"))
                throw Error("UPSERT conflict targets with sort order are not supported.");

            target.Add(new UpsertTargetColumn(name, collation));
        }
        while (Consume(TokenKind.Comma));
        Expect(TokenKind.RightParen);

        if (ConsumeKeyword("WHERE"))
            throw Error("UPSERT conflict-target WHERE clauses are not supported.");

        ExpectKeyword("DO");
        if (ConsumeKeyword("NOTHING"))
            return new UpsertClause(target, new DoNothingUpsertAction());

        ExpectKeyword("UPDATE");
        ExpectKeyword("SET");
        var assignments = new List<ColumnAssignment>();
        do
        {
            var column = ExpectIdentifier();
            Expect(TokenKind.Equal);
            assignments.Add(new ColumnAssignment(column, ParseExpression()));
        }
        while (Consume(TokenKind.Comma));

        Expression? where = null;
        if (ConsumeKeyword("WHERE"))
            where = ParseExpression();

        return new UpsertClause(target, new DoUpdateUpsertAction(assignments, where));
    }

    private ParsedStatement ParseUpdate()
    {
        var tableName = ParseSchemaQualifiedName();
        ExpectKeyword("SET");
        var assignments = new List<ColumnAssignment>();
        do
        {
            var column = ExpectIdentifier();
            Expect(TokenKind.Equal);
            assignments.Add(new ColumnAssignment(column, ParseExpression()));
        }
        while (Consume(TokenKind.Comma));

        Expression? where = null;
        if (ConsumeKeyword("WHERE"))
            where = ParseExpression();

        return new UpdateStatement(tableName, assignments, where, ParseReturning());
    }

    private ParsedStatement ParseDelete()
    {
        ExpectKeyword("FROM");
        var tableName = ParseSchemaQualifiedName();
        Expression? where = null;
        if (ConsumeKeyword("WHERE"))
            where = ParseExpression();

        return new DeleteStatement(tableName, where, ParseReturning());
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

        var terms = new List<QueryStatement> { ParseQueryTerm() };
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
        if (ConsumeKeyword("UPDATE"))
            return new WithDmlStatement(commonTableExpressions, ParseUpdate());
        if (ConsumeKeyword("DELETE"))
            return new WithDmlStatement(commonTableExpressions, ParseDelete());
        if (IsQueryStart())
            return new WithSelectStatement(commonTableExpressions, ParseQuery());

        throw Error("Expected a SELECT, INSERT, UPDATE, or DELETE statement after the common table expression.");
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
            var name = ParseSchemaQualifiedName();
            IReadOnlyList<string>? columns = null;
            if (Consume(TokenKind.LeftParen))
            {
                columns = ParseIdentifierList();
                Expect(TokenKind.RightParen);
            }

            ExpectKeyword("AS");
            Expect(TokenKind.LeftParen);
            if (!IsQueryStart())
                throw Error("Managed common table expressions must contain a SELECT or VALUES query; writable CTEs are not supported.");
            var query = ParseQuery();
            Expect(TokenKind.RightParen);
            commonTableExpressions.Add(new CommonTableExpression(name, columns, query));
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

        return new SelectStatement(distinct, projections, source, where, groupBy, having, [], null, null);
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
            throw Error("Named windows are not supported; OVER must be followed by an inline window definition.");

        Expect(TokenKind.LeftParen);

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
        Expect(TokenKind.RightParen);
        return new WindowSpecification(partitionBy, orderBy, frame);
    }

    private WindowFrame? ParseWindowFrame()
    {
        if (CurrentIsKeyword("RANGE") || CurrentIsKeyword("GROUPS"))
            throw Error("Only ROWS window frames are supported.");
        if (!ConsumeKeyword("ROWS"))
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

        if (CurrentIsKeyword("EXCLUDE"))
            throw Error("EXCLUDE clauses in window frames are not supported.");

        ValidateFrameBounds(start, end);
        return new WindowFrame(start, end);
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
            if (!IsQueryStart())
                throw Error("Derived tables must contain a SELECT query.");

            var query = ParseQuery();
            Expect(TokenKind.RightParen);
            return new DerivedTableSource(query, ParseTableAlias());
        }

        var name = ParseSchemaQualifiedName();
        if (!string.Equals(name, "generate_series", StringComparison.OrdinalIgnoreCase))
            return new NamedTableSource(name, ParseTableAlias());

        Expect(TokenKind.LeftParen);
        var start = ParseExpression();
        Expect(TokenKind.Comma);
        var stop = ParseExpression();
        Expect(TokenKind.Comma);
        var step = ParseExpression();
        Expect(TokenKind.RightParen);
        return new GenerateSeriesSource(start, stop, step);
    }

    private string? ParseTableAlias()
    {
        if (ConsumeKeyword("AS"))
            return ExpectIdentifier();
        if (_lexer.Current.Kind == TokenKind.Identifier && !IsTableSourceClauseKeyword(_lexer.Current.Text))
            return ExpectIdentifier();

        return null;
    }

    private static bool IsTableSourceClauseKeyword(string keyword)
    {
        return keyword.Equals("CROSS", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("EXCEPT", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("FULL", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("GROUP", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("HAVING", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("INNER", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("JOIN", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("LIMIT", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("LEFT", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("NATURAL", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("ON", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("ORDER", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("OUTER", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("RIGHT", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("RETURNING", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("INTERSECT", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("UNION", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("USING", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("WHERE", StringComparison.OrdinalIgnoreCase);
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

    private Expression ParseComparison()
    {
        var expression = ParseAddSubtract();
        while (true)
        {
            if (ConsumeKeyword("IS"))
            {
                var isNot = ConsumeKeyword("NOT");
                expression = new BinaryExpression(
                    expression,
                    isNot ? BinaryOperator.IsNot : BinaryOperator.Is,
                    ParseAddSubtract());
                continue;
            }
            var negated = ConsumeKeyword("NOT");
            if (ConsumeKeyword("BETWEEN"))
            {
                var lower = ParseAddSubtract();
                ExpectKeyword("AND");
                expression = new BetweenExpression(expression, lower, ParseAddSubtract(), negated);
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
                var pattern = ParseAddSubtract();
                Expression? escape = null;
                if (ConsumeKeyword("ESCAPE"))
                    escape = ParseAddSubtract();

                expression = new LikeExpression(expression, pattern, escape, negated);
                continue;
            }
            if (ConsumeKeyword("GLOB"))
            {
                expression = new GlobExpression(expression, ParseAddSubtract(), negated);
                continue;
            }
            if (negated)
                throw Error("Expected BETWEEN, IN, LIKE, or GLOB after NOT.");
            if (!TryParseComparisonOperator(out var operation))
                return expression;

            expression = new BinaryExpression(expression, operation, ParseAddSubtract());
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
        var expression = ParsePrimary();
        while (ConsumeKeyword("COLLATE"))
            expression = new CollationExpression(expression, ExpectIdentifier());

        return expression;
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
            Expect(TokenKind.RightParen);
            return expression;
        }
        if (ConsumeKeyword("EXISTS"))
            return new ExistsExpression(ParseParenthesizedQuery(), Negated: false);
        if (Consume(TokenKind.Minus))
        {
            if (_lexer.Current is { Kind: TokenKind.Integer, Text: "9223372036854775808" })
            {
                _lexer.Next();
                return new LiteralExpression(SqlValue.Integer(long.MinValue));
            }

            var value = ParsePrimary();
            return new BinaryExpression(new LiteralExpression(SqlValue.Integer(0)), BinaryOperator.Subtract, value);
        }

        var token = _lexer.Current;
        switch (token.Kind)
        {
            case TokenKind.Integer:
                _lexer.Next();
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
                if (_inTriggerBody
                    && (string.Equals(token.Text, "NEW", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(token.Text, "OLD", StringComparison.OrdinalIgnoreCase)))
                {
                    throw Error("NEW and OLD row references are not supported; only statement-level trigger bodies are allowed.");
                }

                _lexer.Next();
                if (Consume(TokenKind.Dot))
                {
                    var columnName = ExpectIdentifier();
                    return new ColumnExpression(
                        token.Text + "." + columnName,
                        token.Text,
                        columnName);
                }
                if (string.Equals(token.Text, "NULL", StringComparison.OrdinalIgnoreCase))
                    return new LiteralExpression(SqlValue.Null);
                if (string.Equals(token.Text, "CURRENT_DATE", StringComparison.OrdinalIgnoreCase))
                    return new CurrentTimeExpression(CurrentTimeKind.Date);
                if (string.Equals(token.Text, "CURRENT_TIME", StringComparison.OrdinalIgnoreCase))
                    return new CurrentTimeExpression(CurrentTimeKind.Time);
                if (string.Equals(token.Text, "CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase))
                    return new CurrentTimeExpression(CurrentTimeKind.Timestamp);
                if (string.Equals(token.Text, "CASE", StringComparison.OrdinalIgnoreCase))
                    return ParseCaseExpression();
                if (string.Equals(token.Text, "CAST", StringComparison.OrdinalIgnoreCase) && Consume(TokenKind.LeftParen))
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
                        if (distinct)
                            throw Error("DISTINCT aggregates must have exactly one argument.");

                        var (emptyFilter, emptyWindow) = ParseFunctionSuffix();
                        return new FunctionExpression(functionName, [], false, false, emptyFilter, emptyWindow);
                    }

                    var arguments = new List<Expression> { ParseExpression() };
                    while (Consume(TokenKind.Comma))
                        arguments.Add(ParseExpression());
                    Expect(TokenKind.RightParen);
                    if (string.Equals(token.Text, "COUNT", StringComparison.OrdinalIgnoreCase) && arguments.Count != 1)
                        throw Error("wrong number of arguments to function COUNT()");
                    if (distinct && arguments.Count != 1)
                        throw Error("DISTINCT aggregates must have exactly one argument.");

                    var (filter, window) = ParseFunctionSuffix();
                    return new FunctionExpression(functionName, arguments, false, distinct, filter, window);
                }

                return new ColumnExpression(token.Text);
            default:
                throw Error("Expected an expression.");
        }
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

    private bool TryParseComparisonOperator(out BinaryOperator operation)
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
    {
        var identifiers = new List<string> { ExpectIdentifier() };
        while (Consume(TokenKind.Comma))
            identifiers.Add(ExpectIdentifier());

        return identifiers.ToArray();
    }

    private EmbeddedColumn ParseColumnDefinition()
    {
        var name = ExpectIdentifier();
        var declaredType = ParseDeclaredType();

        var primaryKey = false;
        var primaryKeyDescending = false;
        var notNull = false;
        var unique = false;
        SqlValue? defaultValue = null;
        Expression? defaultExpression = null;
        string? defaultSql = null;
        string? collation = null;
        Expression? generationExpression = null;
        var generatedStored = false;
        string? generationSql = null;
        ForeignKeyDefinition? foreignKey = null;
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
        string? foreignKeyConstraintName = null;
        string? nullConstraintName = null;
        var explicitNull = false;
        var generationAlways = false;
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
                // AUTOINCREMENT requires sqlite_sequence semantics (monotonic rowids that
                // never reuse a value). The managed engine does not implement that table,
                // so the keyword is rejected rather than silently downgraded to plain
                // rowid assignment, which would diverge from SQLite.
                throw Error("AUTOINCREMENT is not supported: the managed engine does not implement sqlite_sequence semantics");
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
                    : ParsePrimary();
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
                if (foreignKey is not null)
                    throw Error($"multiple foreign key constraints on column {name} are not supported");

                foreignKey = ParseForeignKeyReference(name);
                foreignKeyConstraintName = pendingConstraintName;
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
            foreignKey,
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
            foreignKeyConstraintName,
            nullConstraintName,
            explicitNull,
            generationAlways);
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

        if (expression is BinaryExpression
            {
                Left: LiteralExpression { Value.Kind: SqlValueKind.Integer } zero,
                Operator: BinaryOperator.Subtract,
                Right: LiteralExpression right,
            }
            && zero.Value.AsInteger() == 0)
        {
            value = right.Value.Kind switch
            {
                SqlValueKind.Integer => SqlValue.Integer(-right.Value.AsInteger()),
                SqlValueKind.Real => SqlValue.Real(-right.Value.AsReal()),
                _ => default,
            };
            return right.Value.Kind is SqlValueKind.Integer or SqlValueKind.Real;
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
            || !string.Equals(_lexer.Current.Text, keyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _lexer.Next();
        return true;
    }

    private bool CurrentIsKeyword(string keyword)
        => _lexer.Current.Kind == TokenKind.Identifier
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
