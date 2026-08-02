using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

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

    [Test]
    public void ManagedFileEngineLeavesItsDurableJournalModeUnchangedForAnMvccRequest()
    {
        const string path = "unsupported-mvcc.db";
        var fileSystem = new InMemoryFileSystem();

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            ReadValue(connection, "PRAGMA journal_mode=WAL;").Should().Be(SqlValue.Text("wal"));
            ReadValue(connection, "PRAGMA journal_mode=mvcc;").Should().Be(SqlValue.Text("wal"));
            ReadValue(connection, "PRAGMA journal_mode;").Should().Be(SqlValue.Text("wal"));
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadValue(reopenedConnection, "PRAGMA journal_mode;").Should().Be(SqlValue.Text("wal"));
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
