using System.Collections.Generic;

namespace Ahtola.Core;

public sealed partial class EmbeddedDatabase
{
    private SqlValue EvaluateStringAgg(
        FunctionExpression function,
        IReadOnlyList<SourceRow> rows,
        SqlValue[] parameters,
        QueryContext context)
    {
        // string_agg is group_concat with a mandatory separator.
        RequireAggregateArgumentCount("string_agg", function.Arguments, 2);
        return EvaluateGroupConcat(function, rows, parameters, context);
    }

    private SqlValue EvaluateJsonGroupArray(
        FunctionExpression function,
        IReadOnlyList<SourceRow> rows,
        SqlValue[] parameters,
        QueryContext context)
    {
        RequireAggregateArgumentCount("json_group_array", function.Arguments, 1);

        // Unlike group_concat, json_group_array keeps NULL rows as JSON nulls.
        var items = new List<SqlValue>(rows.Count);
        foreach (var row in rows)
        {
            context.CheckInterrupt();
            items.Add(Evaluate(function.Arguments[0], parameters, row, context));
        }

        return SqliteJson.JsonArray(items);
    }

    private SqlValue EvaluateJsonGroupObject(
        FunctionExpression function,
        IReadOnlyList<SourceRow> rows,
        SqlValue[] parameters,
        QueryContext context)
    {
        RequireAggregateArgumentCount("json_group_object", function.Arguments, 2);

        var members = new List<SqlValue>(checked(rows.Count * 2));
        foreach (var row in rows)
        {
            context.CheckInterrupt();
            var name = Evaluate(function.Arguments[0], parameters, row, context);
            var value = Evaluate(function.Arguments[1], parameters, row, context);

            // Labels are coerced to text so a non-text name still produces a well-formed object.
            members.Add(SqlValue.Text(name.Kind == SqlValueKind.Null ? string.Empty : ToSqlText(name)));
            members.Add(value);
        }

        return SqliteJson.JsonObject(members);
    }
}
