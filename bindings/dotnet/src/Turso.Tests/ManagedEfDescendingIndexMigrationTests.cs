using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Turso.Tests;

public class ManagedEfDescendingIndexMigrationTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void ManagedMigrationsRejectDescendingIndexEncodingBeforeSqlGeneration(bool useEmptySortOrders)
    {
        var options = new DbContextOptionsBuilder<DescendingIndexMigrationContext>()
            .UseTurso("Data Source=:memory:;Local Provider=Managed")
            .Options;
        using var context = new DescendingIndexMigrationContext(options);
        var createTable = new CreateTableOperation { Name = "Items" };
        createTable.Columns.Add(new AddColumnOperation
        {
            Table = "Items",
            Name = "Rank",
            ClrType = typeof(int),
            ColumnType = "INTEGER",
            IsNullable = false
        });
        var createIndex = new CreateIndexOperation
        {
            Name = "IX_Items_Rank",
            Table = "Items",
            Columns = ["Rank"],
            IsDescending = useEmptySortOrders ? [] : [true]
        };

        var generate = () => context.GetService<IMigrationsSqlGenerator>().Generate([createTable, createIndex]);

        generate.Should().Throw<NotSupportedException>()
            .WithMessage(
                "The managed local provider does not support descending indexes ('IX_Items_Rank' on 'Items').");
    }

    private sealed class DescendingIndexMigrationContext(
        DbContextOptions<DescendingIndexMigrationContext> options) : DbContext(options);
}
