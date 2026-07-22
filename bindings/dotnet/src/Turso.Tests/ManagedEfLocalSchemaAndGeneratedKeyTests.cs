using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Turso.Data.Sqlite;

namespace Turso.Tests;

public class ManagedEfLocalSchemaAndGeneratedKeyTests
{
    [Test]
    public async Task EnsureCreatedAndSaveChangesReturnGeneratedKeyWithoutJournalPragma()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ManagedSchemaContext>()
            .UseTurso(connection)
            .Options;

        await using var context = new ManagedSchemaContext(options);
        context.Database.GenerateCreateScript().Should().NotContain("AUTOINCREMENT");
        (await context.Database.EnsureCreatedAsync()).Should().BeTrue();

        var item = new ManagedSchemaItem { Name = "managed" };
        context.Items.Add(item);
        await context.SaveChangesAsync();

        item.Id.Should().BeGreaterThan(0);
        (await context.Items.SingleAsync()).Name.Should().Be("managed");
    }

    [Test]
    public void NativeLocalProviderRetainsAutoincrementSchemaGeneration()
    {
        var options = new DbContextOptionsBuilder<ManagedSchemaContext>()
            .UseTurso("Data Source=:memory:")
            .Options;

        using var context = new ManagedSchemaContext(options);

        context.Database.GenerateCreateScript().Should().Contain("AUTOINCREMENT");
    }

    private sealed class ManagedSchemaContext(DbContextOptions<ManagedSchemaContext> options) : DbContext(options)
    {
        public DbSet<ManagedSchemaItem> Items => Set<ManagedSchemaItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ManagedSchemaItem>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id).ValueGeneratedOnAdd();
                entity.Property(item => item.Name).IsRequired();
            });
        }
    }

    private sealed class ManagedSchemaItem
    {
        public long Id { get; set; }

        public string Name { get; set; } = "";
    }
}
