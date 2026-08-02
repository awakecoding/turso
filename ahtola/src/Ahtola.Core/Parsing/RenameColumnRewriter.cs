using System.Text;

namespace Ahtola.Core.Parsing;

/// <summary>
/// Describes the schema visible to a rename rewrite. The delegates take names exactly as they
/// were written in the stored SQL (possibly schema qualified) so the rewriter never has to
/// know how the catalog normalizes identifiers.
/// </summary>
/// <param name="ResolveColumns">
/// Returns the column names of the table or view written as <c>name</c>, or <see langword="null"/>
/// when the object is unknown. An unknown source makes the rewriter conservative rather than wrong.
/// </param>
/// <param name="IsRenameTarget">
/// Reports whether the table written as <c>name</c> is the table whose column is being renamed.
/// </param>
internal sealed record RenameColumnSchema(
    Func<string, IReadOnlyList<string>?> ResolveColumns,
    Func<string, bool> IsRenameTarget);

/// <summary>
/// Raised when a stored schema object references the renamed column in a way SQLite refuses to
/// rewrite. The rename is rejected rather than left to corrupt the object.
/// </summary>
internal sealed class RenameColumnRewriteException(string message) : Exception(message);

/// <summary>
/// Rewrites references to a renamed column inside the stored SQL text of dependent schema
/// objects, the way SQLite's <c>alter.c</c> does: the statement is reparsed, each column
/// reference is resolved against real scope rules, and only the resolved identifier tokens are
/// edited. Nothing else in the text moves, so string literals, comments, and unrelated
/// identifiers that merely contain the old name survive untouched.
/// </summary>
internal static class RenameColumnRewriter
{
    /// <summary>
    /// Rewrites a full <c>CREATE VIEW</c> or <c>CREATE TRIGGER</c> statement.
    /// Returns <see langword="null"/> when the statement does not reference the renamed column.
    /// </summary>
    public static string? RewriteSchemaObject(
        string sql,
        string oldName,
        string newName,
        bool quoteNewName,
        RenameColumnSchema schema)
    {
        ParsedStatement statement;
        SqlSourceSpans spans;
        try
        {
            statement = SqlParser.ParseWithSpans(sql, out spans);
        }
        catch (EmbeddedSqlException)
        {
            // The object could not be reparsed, so its references cannot be resolved safely.
            // Leave the text alone; schema validation decides whether the rename is legal.
            return null;
        }

        var walker = new Walker(spans, oldName, newName, quoteNewName, schema);
        switch (statement)
        {
            case CreateViewStatement view:
                walker.WalkQuery(view.Query, null);
                break;
            case CreateTriggerStatement trigger:
                walker.WalkTrigger(trigger);
                break;
            default:
                return null;
        }

        return walker.Apply(sql);
    }

    /// <summary>
    /// Rewrites a bare expression fragment (a CHECK body, a generated-column expression, an
    /// index key, or a partial-index predicate) that is evaluated against a single table's row.
    /// Returns <see langword="null"/> when the fragment does not reference the renamed column.
    /// </summary>
    public static string? RewriteTableExpression(
        string sql,
        string tableName,
        IReadOnlyList<string> columns,
        string oldName,
        string newName,
        bool quoteNewName)
    {
        Expression expression;
        SqlSourceSpans spans;
        try
        {
            expression = SqlParser.ParseExpressionWithSpans(sql, out spans);
        }
        catch (EmbeddedSqlException)
        {
            return null;
        }

        var schema = new RenameColumnSchema(
            name => string.Equals(name, tableName, StringComparison.OrdinalIgnoreCase) ? columns : null,
            name => string.Equals(name, tableName, StringComparison.OrdinalIgnoreCase));
        var walker = new Walker(spans, oldName, newName, quoteNewName, schema);
        var scope = new Scope(null);
        scope.Bindings.Add(new Binding(tableName, columns, IsTarget: true, QualifiedOnly: false));
        walker.WalkExpression(expression, scope);
        return walker.Apply(sql);
    }

    /// <summary>
    /// Applies SQLite's replacement-token rule: the substituted identifier is quoted when the
    /// new name was written quoted in the ALTER statement or when the token being replaced was
    /// itself quoted.
    /// </summary>
    private static string FormatReplacement(string newName, bool quoteNewName, bool tokenWasQuoted)
    {
        if (!quoteNewName && !tokenWasQuoted)
            return newName;

        return "\"" + newName.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private sealed record Binding(
        string? Name,
        IReadOnlyList<string>? Columns,
        bool IsTarget,
        bool QualifiedOnly);

    private sealed class Scope(Scope? parent)
    {
        public Scope? Parent { get; } = parent;

        public List<Binding> Bindings { get; } = [];

        public List<string> OutputAliases { get; } = [];
    }

    private enum Resolution
    {
        /// <summary>The reference belongs to the renamed table and must be edited.</summary>
        Target,

        /// <summary>The reference belongs to some other source and must be left alone.</summary>
        Other,

        /// <summary>The reference cannot be resolved; the rename must be rejected.</summary>
        Unresolved,
    }

    private sealed class Walker(
        SqlSourceSpans spans,
        string oldName,
        string newName,
        bool quoteNewName,
        RenameColumnSchema schema)
    {
        private readonly List<SqlSourceSpan> _edits = [];

        public string? Apply(string sql)
        {
            if (_edits.Count == 0)
                return null;

            var ordered = _edits
                .Distinct()
                .OrderByDescending(static span => span.Start)
                .ToArray();
            var builder = new StringBuilder(sql);
            foreach (var span in ordered)
            {
                builder.Remove(span.Start, span.End - span.Start);
                builder.Insert(span.Start, FormatReplacement(newName, quoteNewName, span.IsQuoted));
            }

            return builder.ToString();
        }

        public void WalkTrigger(CreateTriggerStatement trigger)
        {
            var triggerTable = trigger.TableName;
            var triggerColumns = schema.ResolveColumns(triggerTable);
            var isTargetTrigger = schema.IsRenameTarget(triggerTable);

            if (isTargetTrigger && trigger.UpdateOfColumns is { Count: > 0 })
            {
                var updateOfSpans = spans.GetList(trigger);
                if (updateOfSpans is not null)
                {
                    for (var index = 0; index < trigger.UpdateOfColumns.Count && index < updateOfSpans.Count; index++)
                    {
                        if (Matches(trigger.UpdateOfColumns[index]))
                            _edits.Add(updateOfSpans[index]);
                    }
                }
                else if (trigger.UpdateOfColumns.Any(Matches))
                {
                    throw Reject($"no such column: {oldName}");
                }
            }

            // Inside a trigger program only the NEW and OLD pseudo-rows are in scope, and they
            // can only be reached through their qualifier. A reference qualified by the trigger's
            // own table name does not resolve, exactly as SQLite reports.
            var scope = new Scope(null);
            scope.Bindings.Add(new Binding("NEW", triggerColumns, isTargetTrigger, QualifiedOnly: true));
            scope.Bindings.Add(new Binding("OLD", triggerColumns, isTargetTrigger, QualifiedOnly: true));

            if (trigger.When is not null)
                WalkExpression(trigger.When, scope);

            foreach (var statement in trigger.Body)
                WalkStatement(statement, scope);
        }

        private void WalkStatement(ParsedStatement statement, Scope? outer)
        {
            switch (statement)
            {
                case QueryStatement query:
                    WalkQuery(query, outer);
                    break;
                case InsertStatement insert:
                    WalkInsert(insert, outer);
                    break;
                case UpdateStatement update:
                    WalkUpdate(update, outer);
                    break;
                case DeleteStatement delete:
                    WalkDelete(delete, outer);
                    break;
                case WithDmlStatement withDml:
                    {
                        var scope = new Scope(outer);
                        BindCommonTableExpressions(withDml.CommonTableExpressions, scope);
                        WalkStatement(withDml.Dml, scope);
                        break;
                    }
            }
        }

        private void WalkInsert(InsertStatement insert, Scope? outer)
        {
            if (insert.Columns is { Length: > 0 } && schema.IsRenameTarget(insert.TableName))
            {
                var columnSpans = spans.GetList(insert);
                if (columnSpans is not null)
                {
                    for (var index = 0; index < insert.Columns.Length && index < columnSpans.Count; index++)
                    {
                        if (Matches(insert.Columns[index]))
                            _edits.Add(columnSpans[index]);
                    }
                }
                else if (insert.Columns.Any(Matches))
                {
                    throw Reject($"no such column: {oldName}");
                }
            }

            foreach (var row in insert.Rows)
            {
                foreach (var value in row)
                    WalkExpression(value, outer);
            }

            if (insert.Source is not null)
                WalkQuery(insert.Source, outer);

            if (insert.Upsert is not null)
                WalkUpsert(insert.Upsert, insert.TableName, outer);

            WalkProjections(insert.Returning, TableScope(insert.TableName, outer));
        }

        private void WalkUpsert(UpsertClause upsert, string tableName, Scope? outer)
        {
            var scope = TableScope(tableName, outer);
            foreach (var target in upsert.Target)
            {
                if (target.Expression is not null)
                    WalkExpression(target.Expression, scope);
            }

            if (upsert.TargetWhere is not null)
                WalkExpression(upsert.TargetWhere, scope);

            if (upsert.Action is DoUpdateUpsertAction update)
            {
                WalkAssignments(update.Assignments, tableName);
                WalkAssignmentValues(update.Assignments, scope);
                if (update.Where is not null)
                    WalkExpression(update.Where, scope);
            }
        }

        private void WalkUpdate(UpdateStatement update, Scope? outer)
        {
            WalkAssignments(update.Assignments, update.TableName);
            var scope = TableScope(update.TableName, outer);
            WalkAssignmentValues(update.Assignments, scope);
            if (update.Where is not null)
                WalkExpression(update.Where, scope);

            WalkProjections(update.Returning, scope);
            WalkOrderBy(update.OrderBy, scope);
        }

        private void WalkDelete(DeleteStatement delete, Scope? outer)
        {
            var scope = TableScope(delete.TableName, outer);
            if (delete.Where is not null)
                WalkExpression(delete.Where, scope);

            WalkProjections(delete.Returning, scope);
            WalkOrderBy(delete.OrderBy, scope);
        }

        private void WalkAssignments(IReadOnlyList<ColumnAssignment> assignments, string tableName)
        {
            if (!schema.IsRenameTarget(tableName))
                return;

            foreach (var assignment in assignments)
            {
                if (!Matches(assignment.Column))
                    continue;

                var span = spans.GetName(assignment);
                if (span is null)
                    throw Reject($"no such column: {oldName}");

                _edits.Add(span.Value);
            }
        }

        // A row assignment shares one value expression across its column targets, so only the
        // first target walks it.
        private void WalkAssignmentValues(IReadOnlyList<ColumnAssignment> assignments, Scope scope)
        {
            foreach (var assignment in assignments)
            {
                if (assignment.ValueIndex == 0)
                    WalkExpression(assignment.Value, scope);
            }
        }

        private Scope TableScope(string tableName, Scope? outer)
        {
            var scope = new Scope(outer);
            scope.Bindings.Add(new Binding(
                UnqualifiedObjectName(tableName),
                schema.ResolveColumns(tableName),
                schema.IsRenameTarget(tableName),
                QualifiedOnly: false));
            return scope;
        }

        public void WalkQuery(QueryStatement query, Scope? outer)
        {
            switch (query)
            {
                case SelectStatement select:
                    WalkSelect(select, outer);
                    break;
                case ValuesClause values:
                    {
                        foreach (var row in values.Rows)
                        {
                            foreach (var value in row)
                                WalkExpression(value, outer);
                        }

                        break;
                    }
                case CompoundSelectStatement compound:
                    {
                        foreach (var term in compound.Terms)
                            WalkQuery(term, outer);

                        // A compound ORDER BY may name any term's result alias, so an alias from
                        // any branch keeps the ordering term an output reference.
                        var scope = new Scope(outer);
                        foreach (var term in compound.Terms)
                            CollectOutputAliases(term, scope);

                        WalkOrderBy(compound.OrderBy, scope);
                        WalkExpression(compound.Limit, scope);
                        WalkExpression(compound.Offset, scope);
                        break;
                    }
                case WithSelectStatement with:
                    {
                        var scope = new Scope(outer);
                        BindCommonTableExpressions(with.CommonTableExpressions, scope);
                        WalkQuery(with.Query, scope);
                        break;
                    }
            }
        }

        private void BindCommonTableExpressions(
            IReadOnlyList<CommonTableExpression> expressions,
            Scope scope)
        {
            foreach (var cte in expressions)
            {
                WalkQuery(cte.Query, scope);
                scope.Bindings.Add(new Binding(
                    cte.Name,
                    cte.Columns ?? TryComputeOutputColumns(cte.Query),
                    IsTarget: false,
                    QualifiedOnly: false));
            }
        }

        private void WalkSelect(SelectStatement select, Scope? outer)
        {
            var scope = new Scope(outer);
            if (select.Source is not null)
                BindSource(select.Source, scope);

            foreach (var projection in select.Projections)
            {
                WalkExpression(projection.Expression, scope);
                if (projection.Alias is not null)
                    scope.OutputAliases.Add(projection.Alias);
            }

            WalkExpression(select.Where, scope);
            foreach (var group in select.GroupBy)
                WalkExpression(group, scope);

            WalkExpression(select.Having, scope);
            foreach (var window in select.NamedWindows)
                WalkWindow(window.Specification, scope);

            WalkOrderBy(select.OrderBy, scope);
            WalkExpression(select.Limit, scope);
            WalkExpression(select.Offset, scope);
        }

        private void WalkProjections(IReadOnlyList<Projection>? projections, Scope? scope)
        {
            if (projections is null)
                return;

            foreach (var projection in projections)
                WalkExpression(projection.Expression, scope);
        }

        private void WalkOrderBy(IReadOnlyList<OrderByTerm>? terms, Scope? scope)
        {
            if (terms is null)
                return;

            foreach (var term in terms)
            {
                // A bare ORDER BY term that names an explicit result alias is an output
                // reference, not a column reference, so it keeps the alias spelling.
                if (term.Expression is ColumnExpression { Qualifier: null } column
                    && scope is not null
                    && scope.OutputAliases.Any(alias => string.Equals(alias, column.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                WalkExpression(term.Expression, scope);
            }
        }

        private void BindSource(TableSource source, Scope scope)
        {
            switch (source)
            {
                case NamedTableSource named:
                    scope.Bindings.Add(new Binding(
                        named.Alias ?? UnqualifiedObjectName(named.Name),
                        schema.ResolveColumns(named.Name),
                        named.Alias is null && schema.IsRenameTarget(named.Name),
                        QualifiedOnly: false));
                    break;
                case DerivedTableSource derived:
                    WalkQuery(derived.Query, scope.Parent);
                    scope.Bindings.Add(new Binding(
                        derived.Alias,
                        TryComputeOutputColumns(derived.Query),
                        IsTarget: false,
                        QualifiedOnly: false));
                    break;
                case TableValuedFunctionSource function:
                    foreach (var argument in function.Arguments)
                        WalkExpression(argument, scope.Parent);

                    scope.Bindings.Add(new Binding(
                        function.Alias ?? function.Name,
                        TableValuedFunctionRegistry.TryResolve(function.Name, out var module)
                            ? module.Schema.AllColumns
                            : [],
                        IsTarget: false,
                        QualifiedOnly: false));
                    break;
                case JoinTableSource join:
                    BindSource(join.Left, scope);
                    BindSource(join.Right, scope);
                    if (join.Condition is not null)
                        WalkExpression(join.Condition, scope);

                    // USING(...) names a column that must exist under the same spelling on both
                    // sides of the join, so a rename can never keep it valid.
                    if (join.UsingColumns is not null && join.UsingColumns.Any(Matches))
                        throw Reject($"cannot join using column {oldName} - column not present in both tables");

                    break;
            }
        }

        private void WalkWindow(WindowSpecification? window, Scope? scope)
        {
            if (window is null)
                return;

            foreach (var partition in window.PartitionBy)
                WalkExpression(partition, scope);

            foreach (var term in window.OrderBy)
                WalkExpression(term.Expression, scope);

            if (window.Frame is not null)
            {
                WalkExpression(window.Frame.Start.Offset, scope);
                WalkExpression(window.Frame.End.Offset, scope);
            }
        }

        public void WalkExpression(Expression? expression, Scope? scope)
        {
            switch (expression)
            {
                case null:
                case LiteralExpression:
                case ParameterExpression:
                case CurrentTimeExpression:
                case RaiseExpression:
                case StarExpression:
                case QualifiedStarExpression:
                    return;
                case ColumnExpression column:
                    ResolveColumnReference(column, scope);
                    return;
                case RowValueExpression row:
                    foreach (var value in row.Values)
                        WalkExpression(value, scope);
                    return;
                case FunctionExpression function:
                    foreach (var argument in function.Arguments)
                        WalkExpression(argument, scope);

                    WalkExpression(function.Filter, scope);
                    WalkWindow(function.Window, scope);
                    return;
                case ScalarSubqueryExpression scalar:
                    WalkQuery(scalar.Query, scope);
                    return;
                case ExistsExpression exists:
                    WalkQuery(exists.Query, scope);
                    return;
                case CollationExpression collation:
                    WalkExpression(collation.Expression, scope);
                    return;
                case CastExpression cast:
                    WalkExpression(cast.Expression, scope);
                    return;
                case CaseExpression caseExpression:
                    WalkExpression(caseExpression.Operand, scope);
                    foreach (var clause in caseExpression.Clauses)
                    {
                        WalkExpression(clause.When, scope);
                        WalkExpression(clause.Then, scope);
                    }

                    WalkExpression(caseExpression.Else, scope);
                    return;
                case LikeExpression like:
                    WalkExpression(like.Value, scope);
                    WalkExpression(like.Pattern, scope);
                    WalkExpression(like.Escape, scope);
                    return;
                case GlobExpression glob:
                    WalkExpression(glob.Value, scope);
                    WalkExpression(glob.Pattern, scope);
                    return;
                case InExpression inExpression:
                    WalkExpression(inExpression.Value, scope);
                    foreach (var value in inExpression.Values)
                        WalkExpression(value, scope);
                    return;
                case InSubqueryExpression inSubquery:
                    WalkExpression(inSubquery.Value, scope);
                    WalkQuery(inSubquery.Query, scope);
                    return;
                case BetweenExpression between:
                    WalkExpression(between.Value, scope);
                    WalkExpression(between.Lower, scope);
                    WalkExpression(between.Upper, scope);
                    return;
                case UnaryExpression unary:
                    WalkExpression(unary.Operand, scope);
                    return;
                case BinaryExpression binary:
                    WalkExpression(binary.Left, scope);
                    WalkExpression(binary.Right, scope);
                    return;
                default:
                    throw Reject(
                        $"unsupported expression '{expression.GetType().Name}' while renaming column {oldName}");
            }
        }

        private void ResolveColumnReference(ColumnExpression column, Scope? scope)
        {
            var name = column.UnqualifiedName ?? column.Name;
            if (!Matches(name))
                return;

            switch (Resolve(column.Qualifier, name, scope))
            {
                case Resolution.Target:
                    {
                        var span = spans.GetName(column);
                        if (span is null)
                            throw Reject($"no such column: {column.Name}");

                        _edits.Add(span.Value);
                        return;
                    }
                case Resolution.Other:
                    return;
                default:
                    throw Reject($"no such column: {column.Name}");
            }
        }

        private Resolution Resolve(string? qualifier, string name, Scope? scope)
        {
            for (var current = scope; current is not null; current = current.Parent)
            {
                if (qualifier is not null)
                {
                    foreach (var binding in current.Bindings)
                    {
                        if (binding.Name is null
                            || !string.Equals(binding.Name, qualifier, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        // An unknown column list means the source could not be resolved, so the
                        // safe answer is "not the renamed table" and validation gets the last word.
                        if (binding.Columns is null)
                            return Resolution.Other;

                        return binding.Columns.Any(column => string.Equals(column, name, StringComparison.OrdinalIgnoreCase))
                            ? binding.IsTarget ? Resolution.Target : Resolution.Other
                            : Resolution.Unresolved;
                    }

                    continue;
                }

                foreach (var binding in current.Bindings)
                {
                    if (binding.QualifiedOnly)
                        continue;
                    if (binding.Columns is null)
                        return Resolution.Other;
                    if (binding.Columns.Any(column => string.Equals(column, name, StringComparison.OrdinalIgnoreCase)))
                        return binding.IsTarget ? Resolution.Target : Resolution.Other;
                }

                if (current.OutputAliases.Any(alias => string.Equals(alias, name, StringComparison.OrdinalIgnoreCase)))
                    return Resolution.Other;
            }

            return Resolution.Unresolved;
        }

        private IReadOnlyList<string>? TryComputeOutputColumns(QueryStatement query)
        {
            switch (query)
            {
                case SelectStatement select:
                    {
                        var names = new List<string>();
                        foreach (var projection in select.Projections)
                        {
                            if (projection.Alias is not null)
                            {
                                names.Add(projection.Alias);
                                continue;
                            }

                            if (projection.Expression is ColumnExpression column)
                            {
                                names.Add(column.UnqualifiedName ?? column.Name);
                                continue;
                            }

                            return null;
                        }

                        return names;
                    }
                case CompoundSelectStatement compound when compound.Terms.Count > 0:
                    return TryComputeOutputColumns(compound.Terms[0]);
                case WithSelectStatement with:
                    return TryComputeOutputColumns(with.Query);
                default:
                    return null;
            }
        }

        private void CollectOutputAliases(QueryStatement? query, Scope scope)
        {
            if (query is not SelectStatement select)
                return;

            foreach (var projection in select.Projections)
            {
                if (projection.Alias is not null)
                    scope.OutputAliases.Add(projection.Alias);
            }
        }

        private bool Matches(string name) => string.Equals(name, oldName, StringComparison.OrdinalIgnoreCase);

        private static RenameColumnRewriteException Reject(string message) => new(message);
    }

    private static string UnqualifiedObjectName(string name)
        => ManagedSchemaName.TrySplit(name, out _, out var bare) ? bare : name;
}
