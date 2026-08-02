using System.Data.Common;

namespace Turso;

public sealed class TursoFactory : DbProviderFactory
{
    public static readonly TursoFactory Instance = new();

    private TursoFactory()
    {
    }

    public override bool CanCreateBatch => true;

    public override bool CanCreateDataAdapter => true;

    public override bool CanCreateCommandBuilder => true;

    public override DbBatch CreateBatch() => new TursoBatch();

    public override DbBatchCommand CreateBatchCommand() => new TursoBatchCommand();

    public override DbCommand CreateCommand() => new TursoCommand();

    public override DbCommandBuilder CreateCommandBuilder() => new TursoCommandBuilder();

    public override DbConnection CreateConnection() => new TursoConnection();

    public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new TursoConnectionStringBuilder();

    public override DbDataAdapter CreateDataAdapter() => new TursoDataAdapter();

    public override DbParameter CreateParameter() => new TursoParameter();
}
