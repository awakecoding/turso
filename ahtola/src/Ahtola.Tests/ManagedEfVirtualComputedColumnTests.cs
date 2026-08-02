using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public class ManagedEfVirtualComputedColumnTests
{
    [Test]
    public async Task EnsureCreatedRejectsVirtualComputedColumnsBeforeSchemaMutation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<VirtualComputedColumnContext>()
            .UseAhtola(connection)
            .Options;
        await using var context = new VirtualComputedColumnContext(options);

        var ensureCreated = async () => await context.Database.EnsureCreatedAsync();

        await ensureCreated.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*virtual computed columns*");

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"type\" = 'table';";

        (await command.ExecuteScalarAsync()).Should().Be(0L);
    }

    private sealed class VirtualComputedColumnContext(
        DbContextOptions<VirtualComputedColumnContext> options) : DbContext(options)
    {
        public DbSet<VirtualComputedColumnItem> Items => Set<VirtualComputedColumnItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<VirtualComputedColumnItem>()
                .Property(item => item.NameLength)
                .HasComputedColumnSql("length(\"Name\")");
    }

    private sealed class VirtualComputedColumnItem
    {
        public long Id { get; set; }

        public string Name { get; set; } = "";

        public int? NameLength { get; set; }
    }
}
