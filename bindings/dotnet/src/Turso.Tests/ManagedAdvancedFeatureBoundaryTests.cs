using AwesomeAssertions;
using Turso.Core;

namespace Turso.Tests;

public sealed class ManagedAdvancedFeatureBoundaryTests
{
    [Test]
    public void ManagedEngineRetainsMemoryModeForMvccRequestAndRejectsVectorFunctions()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ReadValue(connection, "PRAGMA journal_mode = mvcc;").Should().Be(SqlValue.Text("memory"));

        var vector = () => ReadValue(connection, "SELECT vector32('[1.0, 2.0]');");
        vector.Should().Throw<EmbeddedSqlException>()
            .WithMessage("no such function: vector32");
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static SqlValue ReadValue(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }
}
