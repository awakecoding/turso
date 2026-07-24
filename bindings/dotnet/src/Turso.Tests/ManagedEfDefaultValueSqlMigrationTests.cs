using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Turso.Data.Sqlite;

namespace Turso.Tests;

public class ManagedEfDefaultValueSqlMigrationTests
{
    [Test]
    public async Task EnsureCreatedPersistsDefaultValueSql()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<DefaultValueSqlContext>()
            .UseTurso(connection)
            .Options;
        await using var context = new DefaultValueSqlContext(options);

        (await context.Database.EnsureCreatedAsync()).Should().BeTrue();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"sql\" FROM \"sqlite_master\" WHERE \"name\" = 'Items';";

        (await command.ExecuteScalarAsync()).Should().BeOfType<string>()
            .Which.Should().Contain("DEFAULT (CURRENT_TIMESTAMP)");
    }

    private sealed class DefaultValueSqlContext(DbContextOptions<DefaultValueSqlContext> options) : DbContext(options)
    {
        public DbSet<DefaultedItem> Items => Set<DefaultedItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<DefaultedItem>()
                .Property(item => item.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP");
    }

    private sealed class DefaultedItem
    {
        public long Id { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}
