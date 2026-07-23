using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Turso.Data.Sqlite;

namespace Turso.Tests;

public class ManagedEfCheckConstraintMigrationTests
{
    [Test]
    public async Task EnsureCreatedRejectsCheckConstraintsBeforeSchemaMutation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CheckConstraintContext>()
            .UseTurso(connection)
            .Options;
        await using var context = new CheckConstraintContext(options);

        var ensureCreated = async () => await context.Database.EnsureCreatedAsync();

        await ensureCreated.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*check constraints*");

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"type\" = 'table';";

        (await command.ExecuteScalarAsync()).Should().Be(0L);
    }

    [Test]
    public void ManagedMigrationsRejectAddedCheckConstraints()
    {
        using var context = CreateContext("Data Source=:memory:;Local Provider=Managed");
        var operation = new AddCheckConstraintOperation
        {
            Name = "CK_Items_Name",
            Table = "Items",
            Sql = "\"Name\" <> ''"
        };

        var generate = () => context.GetService<IMigrationsSqlGenerator>().Generate([operation]);

        generate.Should().Throw<NotSupportedException>()
            .WithMessage("*check constraints*");
    }

    private static CheckConstraintContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<CheckConstraintContext>()
            .UseTurso(connectionString)
            .Options;

        return new CheckConstraintContext(options);
    }

    private sealed class CheckConstraintContext(DbContextOptions<CheckConstraintContext> options) : DbContext(options)
    {
        public DbSet<CheckConstrainedItem> Items => Set<CheckConstrainedItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<CheckConstrainedItem>()
                .ToTable(table => table.HasCheckConstraint("CK_Items_Name", "\"Name\" <> ''"));
    }

    private sealed class CheckConstrainedItem
    {
        public long Id { get; set; }

        public string Name { get; set; } = "";
    }
}
