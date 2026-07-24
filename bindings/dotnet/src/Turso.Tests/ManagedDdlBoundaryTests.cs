using AwesomeAssertions;
using Turso.Core;

namespace Turso.Tests;

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

    [TestCase(
        "CREATE TABLE items(value INTEGER CHECK (value > 0));",
        "*CHECK constraints are not supported*")]
    [TestCase(
        "CREATE TABLE items(value INTEGER, CONSTRAINT items_value_unique UNIQUE(value));",
        "*Table-level UNIQUE constraints are not supported*")]
    [TestCase(
        "CREATE TABLE items(value INTEGER NOT NULL ON CONFLICT IGNORE);",
        "*Column constraint ON CONFLICT clauses are not supported*")]
    [TestCase(
        "CREATE TABLE items(value INTEGER UNIQUE ON CONFLICT REPLACE);",
        "*Column constraint ON CONFLICT clauses are not supported*")]
    [TestCase(
        "CREATE TABLE items(value INTEGER PRIMARY KEY ON CONFLICT ABORT);",
        "*Column constraint ON CONFLICT clauses are not supported*")]
    public void ManagedEngineRejectsUnrepresentableDdlBeforeSchemaMutation(string sql, string message)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        var create = () => Execute(connection, sql);

        create.Should().Throw<EmbeddedSqlException>().WithMessage(message);
        ReadCount(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';")
            .Should()
            .Be(0);
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
