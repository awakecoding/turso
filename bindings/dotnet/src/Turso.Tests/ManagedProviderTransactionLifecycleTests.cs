using AwesomeAssertions;
using Turso.Data.Sqlite;

namespace Turso.Tests;

public class ManagedProviderTransactionLifecycleTests
{
    [Test]
    public void ClosingManagedConnectionRollsBackAndDetachesActiveTransaction()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"managed-transaction-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = new SqliteConnection($"Data Source={path};Local Provider=Managed");
            connection.Open();
            connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");

            var transaction = connection.BeginTransaction();
            connection.ExecuteNonQuery("INSERT INTO data VALUES (1);");

            connection.Close();

            connection.Transaction.Should().BeNull();
            transaction.Connection.Should().BeNull();

            connection.Open();
            connection.ExecuteScalar<long>("SELECT COUNT(*) FROM data;").Should().Be(0);

            using var nextTransaction = connection.BeginTransaction();
            nextTransaction.Rollback();
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var candidate = path + suffix;
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
        }
    }
}
