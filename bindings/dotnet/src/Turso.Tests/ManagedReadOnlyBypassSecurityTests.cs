using AwesomeAssertions;
using Turso.Data.Sqlite;
using Turso.Raw.Public;

namespace Turso.Tests;

public class ManagedReadOnlyBypassSecurityTests
{
    private const string QueryOnlyDisabledMessage =
        "PRAGMA query_only cannot be disabled when Mode=ReadOnly and Local Provider=Managed.";

    [TestCase("PRAGMA main.query_only = OFF;")]
    [TestCase("pRaGmA   mAiN . qUeRy_OnLy ( off );")]
    [TestCase("/* leading comment */ PRAGMA \"main\" . \"query_only\" = 'false';")]
    [TestCase("PRAGMA query_only = 0.0;")]
    [TestCase("PRAGMA query_only = '2';")]
    public void TursoCommandManagedReadOnlyRejectsAllQueryOnlyDisableForms(string sql)
    {
        var path = CreateDatabasePath();
        try
        {
            SeedTursoDatabase(path);

            using var connection = new TursoConnection($"Data Source={path};Mode=ReadOnly;Local Provider=Managed");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = sql;

            Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery())!
                .Message.Should().Be(QueryOnlyDisabledMessage);

            ReadTursoQueryOnly(connection).Should().Be(1);
            ReadTursoValue(connection).Should().Be(7);
            AssertTursoWritesBlocked(connection);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void TursoCommandManagedReadOnlyAllowsReadsAndRejectsBypassInsideTransaction()
    {
        var path = CreateDatabasePath();
        try
        {
            SeedTursoDatabase(path);

            using var connection = new TursoConnection($"Data Source={path};Mode=ReadOnly;Local Provider=Managed");
            connection.Open();

            using (var queryOnly = connection.CreateCommand())
            {
                queryOnly.CommandText = "PrAgMa main.query_only = ON;";
                queryOnly.ExecuteNonQuery().Should().Be(0);
            }

            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "PRAGMA main.query_only = OFF;";
            Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery())!
                .Message.Should().Be(QueryOnlyDisabledMessage);

            command.CommandText = "INSERT INTO data VALUES (99);";
            Assert.Throws<TursoException>(() => command.ExecuteNonQuery());
            transaction.Rollback();

            ReadTursoValue(connection).Should().Be(7);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [TestCase("PRAGMA main.query_only = OFF;")]
    [TestCase("pRaGmA   mAiN . qUeRy_OnLy ( off );")]
    [TestCase("/* leading comment */ PRAGMA \"main\" . \"query_only\" = 'false';")]
    [TestCase("PRAGMA query_only = 0.0;")]
    [TestCase("PRAGMA query_only = '2';")]
    public void SqliteCommandManagedReadOnlyRejectsAllQueryOnlyDisableForms(string sql)
    {
        var path = CreateDatabasePath();
        try
        {
            SeedSqliteDatabase(path);

            using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Local Provider=Managed");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = sql;

            Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery())!
                .Message.Should().Be(QueryOnlyDisabledMessage);

            connection.ExecuteScalar<long>("PRAGMA query_only;").Should().Be(1);
            connection.ExecuteScalar<long>("SELECT value FROM data;").Should().Be(7);
            AssertSqliteWritesBlocked(connection);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void SqliteCommandManagedReadOnlyAllowsReadsAndRejectsBypassInsideTransaction()
    {
        var path = CreateDatabasePath();
        try
        {
            SeedSqliteDatabase(path);

            using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Local Provider=Managed");
            connection.Open();

            using (var queryOnly = connection.CreateCommand())
            {
                queryOnly.CommandText = "PrAgMa main.query_only = ON;";
                queryOnly.ExecuteNonQuery().Should().Be(0);
            }

            using var transaction = connection.BeginTransaction();
            using var command = new SqliteCommand("PRAGMA main.query_only = OFF;", connection, transaction);
            Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery())!
                .Message.Should().Be(QueryOnlyDisabledMessage);

            command.CommandText = "INSERT INTO data VALUES (99);";
            var exception = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
            exception!.SqliteErrorCode.Should().Be(8);
            transaction.Rollback();

            connection.ExecuteScalar<long>("SELECT value FROM data;").Should().Be(7);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    private static void AssertTursoWritesBlocked(TursoConnection connection)
    {
        using var dml = connection.CreateCommand();
        dml.CommandText = "INSERT INTO data VALUES (99);";
        Assert.Throws<TursoException>(() => dml.ExecuteNonQuery());

        using var ddl = connection.CreateCommand();
        ddl.CommandText = "CREATE TABLE blocked(value INTEGER);";
        Assert.Throws<TursoException>(() => ddl.ExecuteNonQuery());
    }

    private static void AssertSqliteWritesBlocked(SqliteConnection connection)
    {
        var dml = Assert.Throws<SqliteException>(() => connection.ExecuteNonQuery("INSERT INTO data VALUES (99);"));
        dml!.SqliteErrorCode.Should().Be(8);

        var ddl = Assert.Throws<SqliteException>(() => connection.ExecuteNonQuery("CREATE TABLE blocked(value INTEGER);"));
        ddl!.SqliteErrorCode.Should().Be(8);
    }

    private static long ReadTursoQueryOnly(TursoConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA query_only;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static long ReadTursoValue(TursoConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM data;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void SeedTursoDatabase(string path)
    {
        using var connection = new TursoConnection($"Data Source={path};Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");
        connection.ExecuteNonQuery("INSERT INTO data VALUES (7);");
    }

    private static void SeedSqliteDatabase(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");
        connection.ExecuteNonQuery("INSERT INTO data VALUES (7);");
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-readonly-bypass-security-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"readonly-{Guid.NewGuid():N}.db");
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
