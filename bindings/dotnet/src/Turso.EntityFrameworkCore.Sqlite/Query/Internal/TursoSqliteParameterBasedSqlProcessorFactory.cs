using Microsoft.EntityFrameworkCore.Query;

namespace Turso.EntityFrameworkCore.Sqlite.Query.Internal;

public sealed class TursoSqliteParameterBasedSqlProcessorFactory(
    RelationalParameterBasedSqlProcessorDependencies dependencies) : IRelationalParameterBasedSqlProcessorFactory
{
    public RelationalParameterBasedSqlProcessor Create(RelationalParameterBasedSqlProcessorParameters parameters)
        => new TursoSqliteParameterBasedSqlProcessor(dependencies, parameters);
}
