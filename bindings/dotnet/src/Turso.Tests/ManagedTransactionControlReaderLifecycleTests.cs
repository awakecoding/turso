using AwesomeAssertions;
using Turso.Data.Sqlite;

namespace Turso.Tests;

public sealed class ManagedTransactionControlReaderLifecycleTests
{
    [Test]
    public void ManagedExecuteReaderDetachesTransactionAfterSqlCommit()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");

        using var transaction = connection.BeginTransaction();
        connection.ExecuteNonQuery("INSERT INTO data VALUES (1);");

        using (var command = new SqliteCommand("COMMIT;", connection, transaction))
        using (var reader = command.ExecuteReader())
            reader.FieldCount.Should().Be(0);

        connection.Transaction.Should().BeNull();
        transaction.Connection.Should().BeNull();
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM data;").Should().Be(1);

        using var nextTransaction = connection.BeginTransaction();
        nextTransaction.Rollback();
    }

    [Test]
    public void ManagedReaderCloseTracksSqlRollbackAfterDrainingIt()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");

        using var transaction = connection.BeginTransaction();
        connection.ExecuteNonQuery("INSERT INTO data VALUES (1);");

        using (var command = new SqliteCommand("SELECT value FROM data; ROLLBACK;", connection, transaction))
        using (var reader = command.ExecuteReader())
            reader.Read().Should().BeTrue();

        connection.Invoking(static value => value.ExecuteNonQuery("SELECT 1;"))
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(Data.Sqlite.Properties.Resources.TransactionCompleted);
        transaction.Rollback();
        connection.Transaction.Should().BeNull();
        transaction.Connection.Should().BeNull();
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM data;").Should().Be(0);
    }
}
