using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Turso.Data.Sqlite;

namespace Turso.Tests;

public class ManagedEfGeneratedKeyMigrationTests
{
    [TestCase("Managed", false)]
    [TestCase("Native", true)]
    public async Task EnsureCreatedAndSaveChangesSupportConventionalGeneratedKeys(
        string localProvider,
        bool expectsAutoincrement)
    {
        if (localProvider == "Native")
            NativeProviderTestFixture.EnsureRegistered();

        await using var connection = new SqliteConnection(
            $"Data Source=:memory:;Local Provider={localProvider}");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<GeneratedKeyContext>()
            .UseTurso(connection)
            .Options;
        await using var context = new GeneratedKeyContext(options);

        var createScript = context.Database.GenerateCreateScript();
        createScript.Contains("AUTOINCREMENT").Should().Be(expectsAutoincrement);
        (await context.Database.EnsureCreatedAsync()).Should().BeTrue();

        var item = new GeneratedKeyItem { Name = localProvider };
        context.Items.Add(item);
        await context.SaveChangesAsync();

        item.Id.Should().BeGreaterThan(0);
        (await context.Items.SingleAsync()).Name.Should().Be(localProvider);
    }

    private sealed class GeneratedKeyContext(DbContextOptions<GeneratedKeyContext> options) : DbContext(options)
    {
        public DbSet<GeneratedKeyItem> Items => Set<GeneratedKeyItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<GeneratedKeyItem>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id).ValueGeneratedOnAdd();
                entity.Property(item => item.Name).IsRequired();
            });
        }
    }

    private sealed class GeneratedKeyItem
    {
        public long Id { get; set; }

        public string Name { get; set; } = "";
    }
}
