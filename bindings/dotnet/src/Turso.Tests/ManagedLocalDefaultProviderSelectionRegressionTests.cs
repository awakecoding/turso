using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Turso.Data.Sqlite;
using Turso.EntityFrameworkCore.Sqlite.Migrations.Internal;

namespace Turso.Tests;

public class ManagedLocalDefaultProviderSelectionRegressionTests
{
    [Test]
    public void ImplicitLocalConnectionsUseTheManagedProvider()
    {
        using var tursoConnection = new TursoConnection("Data Source=:memory:");
        tursoConnection.Open();

        GetPrivateField(tursoConnection, "_managedDatabase").Should().NotBeNull();
        GetPrivateField(tursoConnection, "_nativeDatabase").Should().BeNull();

        using var sqliteConnection = new SqliteConnection("Data Source=:memory:");
        sqliteConnection.Open();

        GetPrivateField(sqliteConnection, "_managedDatabase").Should().NotBeNull();
        GetPrivateField(sqliteConnection, "_database").Should().BeNull();
    }

    [Test]
    public void ExplicitNativeLocalConnectionsUseTheNativeProvider()
    {
        NativeProviderTestFixture.EnsureRegistered();

        using var tursoConnection = new TursoConnection("Data Source=:memory:;Local Provider=Native");
        tursoConnection.Open();

        GetPrivateField(tursoConnection, "_managedDatabase").Should().BeNull();
        GetPrivateField(tursoConnection, "_nativeDatabase").Should().NotBeNull();

        using var sqliteConnection = new SqliteConnection("Data Source=:memory:;Local Provider=Native");
        sqliteConnection.Open();

        GetPrivateField(sqliteConnection, "_managedDatabase").Should().BeNull();
        GetPrivateField(sqliteConnection, "_database").Should().NotBeNull();
    }

    [Test]
    public void RemoteConnectionWithoutLocalProviderUsesRemoteExecution()
    {
        using var connection = new TursoConnection("Data Source=libsql://example.com");

        connection.Open();

        connection.State.Should().Be(System.Data.ConnectionState.Open);
        connection.CanCreateBatch.Should().BeTrue();
    }

    [Test]
    public void RemoteConnectionRejectsExplicitManagedLocalProvider()
    {
        using var connection = new TursoConnection("Data Source=libsql://example.com;Local Provider=Managed");

        connection.Invoking(static value => value.Open())
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage("Local Provider=Managed is supported only for local database connections.");
    }

    [TestCase("Data Source=:memory:", true)]
    [TestCase("Data Source=:memory:;Local Provider=Native", false)]
    [TestCase("Data Source=libsql://example.com", false)]
    public void UseTursoSelectsManagedMigrationsOnlyForImplicitLocalConnections(
        string connectionString,
        bool usesManagedMigrations)
    {
        var options = new DbContextOptionsBuilder<ProviderSelectionContext>()
            .UseTurso(connectionString)
            .Options;
        using var context = new ProviderSelectionContext(options);

        (context.GetService<IMigrationsSqlGenerator>() is TursoManagedSqliteMigrationsSqlGenerator)
            .Should()
            .Be(usesManagedMigrations);
    }

    private static object? GetPrivateField(object connection, string fieldName)
    {
        var field = connection.GetType().GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Unable to retrieve {fieldName} from {connection.GetType().Name}.");
        return field.GetValue(connection);
    }

    private sealed class ProviderSelectionContext(DbContextOptions<ProviderSelectionContext> options) : DbContext(options);
}
