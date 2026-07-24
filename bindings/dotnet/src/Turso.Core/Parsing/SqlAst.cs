global using Turso.Core.Parsing;

namespace Turso.Core.Parsing;

internal abstract record ParsedStatement;

internal abstract record QueryStatement : ParsedStatement;

internal sealed record CreateTableStatement(
    string Name,
    IReadOnlyList<EmbeddedColumn> Columns,
    bool IfNotExists,
    bool WithoutRowid = false,
    IReadOnlyList<TablePrimaryKeyColumn>? PrimaryKeyColumns = null,
    IReadOnlyList<TableUniqueConstraint>? UniqueConstraints = null,
    IReadOnlyList<CheckConstraint>? CheckConstraints = null,
    InsertConflictAlgorithm? PrimaryKeyConflictAlgorithm = null,
    string? PrimaryKeyConstraintName = null,
    int? PrimaryKeyDeclarationOrder = null,
    IReadOnlyList<ForeignKeyDefinition>? TableForeignKeys = null) : ParsedStatement;

internal sealed record DropTableStatement(string Name, bool IfExists) : ParsedStatement;

internal sealed record CreateIndexStatement(
    string Name,
    string TableName,
    IReadOnlyList<IndexedColumnDefinition> Columns,
    bool Unique,
    bool IfNotExists) : ParsedStatement;

internal sealed record DropIndexStatement(string Name, bool IfExists) : ParsedStatement;

internal sealed record IndexedColumnDefinition(string Name, string? Collation, bool Descending);

internal sealed record CreateViewStatement(
    string Name,
    IReadOnlyList<string>? Columns,
    QueryStatement Query,
    string Sql,
    bool IfNotExists) : ParsedStatement;

internal sealed record DropViewStatement(string Name, bool IfExists) : ParsedStatement;

internal enum TriggerEvent
{
    Insert,
    Update,
    Delete,
}

internal sealed record CreateTriggerStatement(
    string Name,
    TriggerEvent Event,
    string TableName,
    IReadOnlyList<ParsedStatement> Body,
    string Sql,
    bool IfNotExists) : ParsedStatement;

internal sealed record DropTriggerStatement(string Name, bool IfExists) : ParsedStatement;

internal sealed record ViewDefinition(
    string Name,
    IReadOnlyList<string>? Columns,
    QueryStatement Query,
    string Sql);

internal sealed record TriggerDefinition(
    string Name,
    TriggerEvent Event,
    string TableName,
    IReadOnlyList<ParsedStatement> Body,
    string Sql);

// A parser-only separator retains whether a dot was SQL syntax rather than part of a
// quoted identifier. Catalog object names remain ordinary strings after connection routing.
internal static class ManagedSchemaName
{
    private const char Separator = '\u001f';

    public static string Create(string schema, string name) => schema + Separator + name;

    public static bool TrySplit(string value, out string schema, out string name)
    {
        var separator = value.IndexOf(Separator);
        if (separator < 0)
        {
            schema = string.Empty;
            name = value;
            return false;
        }

        schema = value[..separator];
        name = value[(separator + 1)..];
        return true;
    }

    public static string Display(string value)
        => TrySplit(value, out var schema, out var name) ? schema + "." + name : value;
}

internal sealed record AlterTableAddColumnStatement(string TableName, EmbeddedColumn Column) : ParsedStatement;

internal sealed record AlterTableRenameStatement(string TableName, string NewName) : ParsedStatement;

internal sealed record AlterTableRenameColumnStatement(string TableName, string ColumnName, string NewName) : ParsedStatement;

internal sealed record InsertStatement(
    string TableName,
    string[]? Columns,
    IReadOnlyList<Expression[]> Rows,
    QueryStatement? Source = null,
    IReadOnlyList<Projection>? Returning = null,
    UpsertClause? Upsert = null,
    InsertConflictAlgorithm? ConflictAlgorithm = null) : ParsedStatement;

internal enum InsertConflictAlgorithm
{
    Rollback,
    Abort,
    Fail,
    Ignore,
    Replace,
}

internal sealed record UpsertTargetColumn(string Name, string? Collation);

internal abstract record UpsertAction;

internal sealed record DoNothingUpsertAction : UpsertAction;

internal sealed record DoUpdateUpsertAction(
    IReadOnlyList<ColumnAssignment> Assignments,
    Expression? Where) : UpsertAction;

internal sealed record UpsertClause(
    IReadOnlyList<UpsertTargetColumn> Target,
    UpsertAction Action);

internal sealed record UpdateStatement(
    string TableName,
    IReadOnlyList<ColumnAssignment> Assignments,
    Expression? Where,
    IReadOnlyList<Projection>? Returning = null,
    IReadOnlyList<OrderByTerm>? OrderBy = null,
    Expression? Limit = null,
    Expression? Offset = null) : ParsedStatement
{
    public IReadOnlyList<OrderByTerm> EffectiveOrderBy => OrderBy ?? [];
}

internal sealed record DeleteStatement(
    string TableName,
    Expression? Where,
    IReadOnlyList<Projection>? Returning = null,
    IReadOnlyList<OrderByTerm>? OrderBy = null,
    Expression? Limit = null,
    Expression? Offset = null) : ParsedStatement
{
    public IReadOnlyList<OrderByTerm> EffectiveOrderBy => OrderBy ?? [];
}

internal sealed record PragmaTableInfoStatement(string TableName) : ParsedStatement;

internal sealed record PragmaTableXInfoStatement(string TableName) : ParsedStatement;

internal sealed record PragmaIndexListStatement(string TableName) : ParsedStatement;

internal sealed record PragmaIndexInfoStatement(string IndexName) : ParsedStatement;

internal sealed record PragmaForeignKeyListStatement(string TableName) : ParsedStatement;

internal sealed record PragmaForeignKeyCheckStatement(string? TableName) : ParsedStatement;

internal sealed record PragmaTableListStatement : ParsedStatement;

internal sealed record PragmaDatabaseListStatement : ParsedStatement;

internal sealed record PragmaEncodingStatement : ParsedStatement;

internal sealed record PragmaQueryOnlyStatement(bool? Enabled) : ParsedStatement;

internal sealed record PragmaForeignKeysStatement(bool? Enabled) : ParsedStatement;

internal sealed record PragmaDeferForeignKeysStatement(bool? Enabled) : ParsedStatement;

internal sealed record PragmaRecursiveTriggersStatement(bool? Enabled) : ParsedStatement;

internal enum PragmaHeaderIntegerKind
{
    SchemaVersion,
    UserVersion,
    ApplicationId,
}

internal sealed record PragmaHeaderIntegerStatement(
    PragmaHeaderIntegerKind Kind,
    int? Value) : ParsedStatement;

internal sealed record PragmaJournalModeStatement(string? Mode) : ParsedStatement;

internal sealed record PragmaPageSizeStatement(int? Value) : ParsedStatement;

internal sealed record VacuumStatement(string? Schema) : ParsedStatement;

internal sealed record AttachDatabaseStatement(
    Expression Path,
    string Alias,
    Expression? Key) : ParsedStatement;

internal sealed record DetachDatabaseStatement(string Alias) : ParsedStatement;

internal sealed record ExplainStatement(ParsedStatement Inner) : ParsedStatement;

internal sealed record ExplainQueryPlanStatement(ParsedStatement Inner) : ParsedStatement;

internal sealed record SelectStatement(
    bool Distinct,
    IReadOnlyList<Projection> Projections,
    TableSource? Source,
    Expression? Where,
    IReadOnlyList<Expression> GroupBy,
    Expression? Having,
    IReadOnlyList<OrderByTerm> OrderBy,
    Expression? Limit,
    Expression? Offset) : QueryStatement;

// A VALUES(...) row-set expression. It is a first-class query term so it can appear
// at the top level, inside FROM/JOIN as a derived table, as a scalar/IN/EXISTS
// subquery, as a compound-select term, and as the body of a common table expression.
// SQLite names its columns "column1".."columnN".
internal sealed record ValuesClause(
    IReadOnlyList<IReadOnlyList<Expression>> Rows) : QueryStatement;

internal sealed record CompoundSelectStatement(
    IReadOnlyList<QueryStatement> Terms,
    IReadOnlyList<CompoundOperator> Operators,
    IReadOnlyList<OrderByTerm> OrderBy,
    Expression? Limit,
    Expression? Offset) : QueryStatement;

internal sealed record WithSelectStatement(
    IReadOnlyList<CommonTableExpression> CommonTableExpressions,
    QueryStatement Query) : QueryStatement;

internal sealed record WithDmlStatement(
    IReadOnlyList<CommonTableExpression> CommonTableExpressions,
    ParsedStatement Dml) : ParsedStatement;

internal sealed record CommonTableExpression(
    string Name,
    IReadOnlyList<string>? Columns,
    QueryStatement Query);

internal sealed record BeginStatement : ParsedStatement;

internal sealed record CommitStatement : ParsedStatement;

internal sealed record RollbackStatement : ParsedStatement;

internal sealed record SavepointStatement(string Name) : ParsedStatement;

internal sealed record ReleaseSavepointStatement(string Name) : ParsedStatement;

internal sealed record RollbackToSavepointStatement(string Name) : ParsedStatement;

internal abstract record TableSource;

internal sealed record NamedTableSource(string Name, string? Alias = null) : TableSource;

internal sealed record GenerateSeriesSource(Expression Start, Expression Stop, Expression Step) : TableSource;

internal sealed record DerivedTableSource(QueryStatement Query, string? Alias) : TableSource;

internal sealed record JoinTableSource(
    TableSource Left,
    TableSource Right,
    Expression? Condition,
    JoinKind Kind,
    IReadOnlyList<string>? UsingColumns = null,
    bool Natural = false) : TableSource;

internal enum JoinKind
{
    Inner,
    Left,
    Right,
    Full,
}

internal enum CompoundOperator
{
    Union,
    UnionAll,
    Intersect,
    Except,
}

internal sealed record Projection(Expression Expression, string? Alias);

internal enum NullPlacement
{
    Default,
    First,
    Last,
}

internal sealed record OrderByTerm(
    Expression Expression,
    bool Descending,
    NullPlacement NullPlacement = NullPlacement.Default,
    long? Ordinal = null);

// Aggregate window functions (func(...) OVER (...)). Only the ROWS frame type is
// materialized; RANGE/GROUPS/EXCLUDE and dedicated ranking functions are rejected
// at parse time so the engine never silently produces divergent results.
internal sealed record WindowSpecification(
    IReadOnlyList<Expression> PartitionBy,
    IReadOnlyList<OrderByTerm> OrderBy,
    WindowFrame? Frame);

internal enum FrameBoundKind
{
    UnboundedPreceding,
    Preceding,
    CurrentRow,
    Following,
    UnboundedFollowing,
}

internal sealed record FrameBound(FrameBoundKind Kind, Expression? Offset);

internal sealed record WindowFrame(FrameBound Start, FrameBound End);

internal sealed record ColumnAssignment(
    string Column,
    Expression Value,
    int ValueIndex = 0,
    int ValueCount = 1,
    bool IsRowAssignment = false);

internal sealed record EmbeddedColumn(
    string Name,
    string? DeclaredType,
    bool PrimaryKey,
    bool NotNull,
    bool Unique,
    SqlValue? DefaultValue,
    bool PrimaryKeyDescending = false,
    Expression? GenerationExpression = null,
    bool GeneratedStored = false,
    string? GenerationSql = null,
    string? Collation = null,
    ForeignKeyDefinition? ForeignKey = null,
    IReadOnlyList<CheckConstraint>? Checks = null,
    Expression? DefaultExpression = null,
    string? DefaultSql = null,
    InsertConflictAlgorithm? PrimaryKeyConflictAlgorithm = null,
    InsertConflictAlgorithm? NotNullConflictAlgorithm = null,
    InsertConflictAlgorithm? UniqueConflictAlgorithm = null,
    string? PrimaryKeyConstraintName = null,
    string? NotNullConstraintName = null,
    string? UniqueConstraintName = null,
    string? DefaultConstraintName = null,
    string? CollationConstraintName = null,
    string? GenerationConstraintName = null,
    string? NullConstraintName = null,
    bool ExplicitNull = false,
    bool GenerationAlways = false,
    IReadOnlyList<ForeignKeyDefinition>? AdditionalForeignKeys = null)
{
    // A column is generated when it carries a computed AS (...) expression. Generated
    // columns are materialized at write time; VIRTUAL and STORED differ only in whether
    // the value may be persisted (STORED) or must be recomputed (VIRTUAL).
    public bool IsGenerated => GenerationExpression is not null;

    public IReadOnlyList<CheckConstraint> CheckConstraints { get; } =
        Array.AsReadOnly((Checks ?? []).ToArray());

    public IReadOnlyList<ForeignKeyDefinition> ForeignKeyConstraints { get; } =
        Array.AsReadOnly(
            (ForeignKey is null
                ? AdditionalForeignKeys ?? []
                : new[] { ForeignKey }.Concat(AdditionalForeignKeys ?? []))
            .ToArray());

    public bool HasDefault => DefaultValue.HasValue || DefaultExpression is not null;
}

// A column participating in a table-level PRIMARY KEY(...) clause, preserving the
// declared collation and ASC/DESC direction so its physical-key descriptor does not
// lose SQLite's comparison semantics.
internal sealed record TablePrimaryKeyColumn(string Name, bool Descending, string? Collation = null);

internal sealed record TableUniqueConstraint(
    string? Name,
    IReadOnlyList<TablePrimaryKeyColumn> Columns,
    InsertConflictAlgorithm? ConflictAlgorithm = null,
    int DeclarationOrder = int.MaxValue);

internal sealed record CheckConstraint(
    string? Name,
    Expression Expression,
    string Sql,
    InsertConflictAlgorithm? ConflictAlgorithm = null);

internal enum ForeignKeyAction
{
    NoAction,
    Restrict,
    SetNull,
    SetDefault,
    Cascade,
}

internal enum ForeignKeyDeferral
{
    NotDeferrable,
    InitiallyImmediate,
    InitiallyDeferred,
}

internal sealed record ForeignKeyDefinition(
    IReadOnlyList<string> ChildColumns,
    string ParentTable,
    IReadOnlyList<string> ParentColumns,
    ForeignKeyAction OnDelete = ForeignKeyAction.NoAction,
    ForeignKeyAction OnUpdate = ForeignKeyAction.NoAction,
    string? Match = null,
    ForeignKeyDeferral Deferral = ForeignKeyDeferral.NotDeferrable,
    string? ConstraintName = null);

internal sealed record EmbeddedIndexColumn(string Name, int ColumnIndex, string? Collation, bool Descending);

internal enum EmbeddedIndexOrigin
{
    Explicit,
    UniqueConstraint,
    PrimaryKey,
}

internal sealed record EmbeddedIndex(
    string Name,
    bool Unique,
    IReadOnlyList<EmbeddedIndexColumn> Columns,
    EmbeddedIndexOrigin Origin = EmbeddedIndexOrigin.Explicit,
    InsertConflictAlgorithm? ConflictAlgorithm = null);

internal abstract record Expression;

internal sealed record LiteralExpression(SqlValue Value) : Expression;

internal enum CurrentTimeKind
{
    Date,
    Time,
    Timestamp,
}

internal sealed record CurrentTimeExpression(CurrentTimeKind Kind) : Expression;

internal sealed record ParameterExpression(int Index) : Expression;

internal sealed record RowValueExpression(IReadOnlyList<Expression> Values) : Expression;

internal sealed record ColumnExpression(
    string Name,
    string? Qualifier = null,
    string? UnqualifiedName = null) : Expression;

internal sealed record FunctionExpression(
    string Name,
    IReadOnlyList<Expression> Arguments,
    bool CountStar,
    bool Distinct = false,
    Expression? Filter = null,
    WindowSpecification? Window = null) : Expression;

internal sealed record ScalarSubqueryExpression(QueryStatement Query) : Expression;

internal sealed record ExistsExpression(QueryStatement Query, bool Negated) : Expression;

internal sealed record CollationExpression(Expression Expression, string Name) : Expression;

internal sealed record CastExpression(Expression Expression, string TypeName) : Expression;

internal sealed record CaseExpression(Expression? Operand, IReadOnlyList<CaseClause> Clauses, Expression? Else) : Expression;

internal sealed record CaseClause(Expression When, Expression Then);

internal sealed record LikeExpression(Expression Value, Expression Pattern, Expression? Escape, bool Negated) : Expression;

internal sealed record InExpression(Expression Value, IReadOnlyList<Expression> Values, bool Negated) : Expression;

internal sealed record InSubqueryExpression(Expression Value, QueryStatement Query, bool Negated) : Expression;

internal sealed record BetweenExpression(Expression Value, Expression Lower, Expression Upper, bool Negated) : Expression;

internal sealed record UnaryExpression(UnaryOperator Operator, Expression Operand) : Expression;

internal sealed record StarExpression : Expression;

internal sealed record QualifiedStarExpression(string Qualifier) : Expression;

internal sealed record GlobExpression(Expression Value, Expression Pattern, bool Negated) : Expression;

internal sealed record BinaryExpression(Expression Left, BinaryOperator Operator, Expression Right) : Expression;

internal enum BinaryOperator
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    BitwiseAnd,
    BitwiseOr,
    ShiftLeft,
    ShiftRight,
    Concatenate,
    JsonArrow,
    JsonArrowText,
    And,
    Or,
    Is,
    IsNot,
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
}

internal enum UnaryOperator
{
    Not,
    Plus,
    Negate,
    BitwiseNot,
}
