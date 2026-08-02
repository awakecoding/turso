using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Sqlite.Query.Internal;

namespace Turso.EntityFrameworkCore.Sqlite.Query.Internal;

public sealed class TursoSqliteParameterBasedSqlProcessor(
    RelationalParameterBasedSqlProcessorDependencies dependencies,
    RelationalParameterBasedSqlProcessorParameters parameters)
    : SqliteParameterBasedSqlProcessor(dependencies, parameters)
{
    protected override Expression ProcessSqlNullability(
        Expression queryExpression,
        IReadOnlyDictionary<string, object?> parametersValues,
        out bool canCache)
        => new TursoSqliteSqlNullabilityProcessor(Dependencies, Parameters)
            .Process(queryExpression, parametersValues, out canCache);
}
