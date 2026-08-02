using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

public sealed class ManagedDdlBoundaryTests
{
    [Test]
    public void ManagedEngineAcceptsExplicitNullColumnConstraints()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE items(untyped NULL, typed TEXT NULL);");
        Execute(connection, "INSERT INTO items VALUES (NULL, NULL);");

        ReadCount(connection, "SELECT COUNT(*) FROM items WHERE untyped IS NULL AND typed IS NULL;")
            .Should()
            .Be(1);
    }

    [TestCase("CREATE TABLE items(value INTEGER CHECK (value > 0));")]
    [TestCase("CREATE TABLE items(value INTEGER, CONSTRAINT items_value_unique UNIQUE(value));")]
    [TestCase("CREATE TABLE items(value INTEGER NOT NULL ON CONFLICT IGNORE);")]
    [TestCase("CREATE TABLE items(value INTEGER UNIQUE ON CONFLICT REPLACE);")]
    [TestCase("CREATE TABLE items(value INTEGER PRIMARY KEY ON CONFLICT ABORT);")]
    public void ManagedEngineAcceptsConstraintDdl(string sql)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, sql);
        ReadCount(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';")
            .Should()
            .Be(1);
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static long ReadCount(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }
}
