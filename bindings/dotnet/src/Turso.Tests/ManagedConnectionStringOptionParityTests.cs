using AwesomeAssertions;
using Turso.Data.Sqlite;
using Turso.Raw.Public;

namespace Turso.Tests;

public class ManagedConnectionStringOptionParityTests
{
    [Test]
    public void ManagedMemoryModeUsesManagedMemoryAndAppliesDefaultTimeout()
    {
        var path = CreateDatabasePath();
        try
        {
            var builder = new TursoConnectionStringBuilder(
                $"Data Source={path};Mode=Memory;Cache=Private;Default Timeout=7;Pooling=False;Local Provider=Managed");

            builder.Mode.Should().Be("Memory");
            builder.Cache.Should().Be("Private");
            TursoConnectionOptions.Parse(builder.ConnectionString).Mode.Should().Be("Memory");

            using var connection = new TursoConnection(builder.ConnectionString);
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE data(value INTEGER);";
            create.CommandTimeout.Should().Be(7);
            create.ExecuteNonQuery().Should().Be(0);

            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO data VALUES (42);";
            insert.CommandTimeout.Should().Be(7);
            insert.ExecuteNonQuery().Should().Be(1);
            File.Exists(path).Should().BeFalse("Mode=Memory must not create the Data Source file");

            using var select = connection.CreateCommand();
            select.CommandText = "SELECT value FROM data;";
            Convert.ToInt64(select.ExecuteScalar()).Should().Be(42);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ManagedReadOnlyModeMapsToQueryOnlyAndCannotBeDisabled()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var writer = new TursoConnection($"Data Source={path};Local Provider=Managed"))
            {
                writer.Open();
                writer.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");
                writer.ExecuteNonQuery("INSERT INTO data VALUES (42);");
            }

            using var readOnly = new TursoConnection($"Data Source={path};Mode=ReadOnly;Local Provider=Managed");
            readOnly.Open();

            using var queryOnly = readOnly.CreateCommand();
            queryOnly.CommandText = "PRAGMA query_only;";
            Convert.ToInt64(queryOnly.ExecuteScalar()).Should().Be(1);

            using var write = readOnly.CreateCommand();
            write.CommandText = "INSERT INTO data VALUES (99);";
            write.Invoking(static command => command.ExecuteNonQuery()).Should().Throw<TursoException>();

            using var disableQueryOnly = readOnly.CreateCommand();
            disableQueryOnly.CommandText = "PRAGMA query_only = OFF;";
            disableQueryOnly.Invoking(static command => command.ExecuteNonQuery())
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("PRAGMA query_only cannot be disabled when Mode=ReadOnly and Local Provider=Managed.");
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ManagedReadWriteModeRequiresExistingDatabaseFile()
    {
        var path = CreateDatabasePath();
        try
        {
            using var connection = new TursoConnection($"Data Source={path};Mode=ReadWrite;Local Provider=Managed");

            connection.Invoking(static value => value.Open())
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("Mode=ReadWrite requires an existing database file when Local Provider=Managed.");
            File.Exists(path).Should().BeFalse();
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void SqliteFacadeManagedMemoryModeHonorsOptionsWithoutNativeInterop()
    {
        var path = CreateDatabasePath();
        try
        {
            using var connection = new SqliteConnection(
                $"Data Source={path};Mode=Memory;Cache=Private;Default Timeout=11;Local Provider=Managed");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE data(value INTEGER);";

            command.CommandTimeout.Should().Be(11);
            command.ExecuteNonQuery().Should().Be(0);
            File.Exists(path).Should().BeFalse("Mode=Memory must not create the Data Source file");
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [TestCase("Password=secret", "Password is not supported when Local Provider=Managed because the managed engine does not provide encryption.")]
    [TestCase("Encryption Key=0011", "Encryption is not available for the managed engine.")]
    [TestCase("Vfs=win32-longpath", "Vfs is not supported when Local Provider=Managed because the managed engine does not use native SQLite VFS implementations.")]
    [TestCase("Foreign Keys=True", "Foreign Keys is not supported when Local Provider=Managed.")]
    [TestCase("Recursive Triggers=True", "Recursive Triggers is not supported when Local Provider=Managed.")]
    public void ManagedProviderRejectsConnectionOptionsItCannotEnforce(string option, string message)
    {
        using var connection = new TursoConnection($"Data Source=:memory:;Local Provider=Managed;{option}");

        connection.Invoking(static value => value.Open())
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage(message);
    }

    [Test]
    public void ManagedProviderValidatesNegativeDefaultTimeoutBeforeOpening()
    {
        using var connection = new TursoConnection("Data Source=:memory:;Default Timeout=-1;Local Provider=Managed");

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => connection.Open());

        exception!.ParamName.Should().Be("DefaultTimeout");
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-connection-option-parity-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"options-{Guid.NewGuid():N}.db");
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
