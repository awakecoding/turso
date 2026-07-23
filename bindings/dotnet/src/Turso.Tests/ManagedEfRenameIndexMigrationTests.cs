using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Turso.Data.Sqlite;

namespace Turso.Tests;

public class ManagedEfRenameIndexMigrationTests
{
    [Test]
    public async Task ManagedMigrationsRejectIndexRenamesBeforeSchemaMutation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<RenameIndexMigrationContext>()
            .UseTurso(connection)
            .Options;
        await using var context = new RenameIndexMigrationContext(options);

        var createTable = new CreateTableOperation { Name = "Items" };
        createTable.Columns.Add(new AddColumnOperation
        {
            Table = "Items",
            Name = "Id",
            ClrType = typeof(long),
            ColumnType = "INTEGER",
            IsNullable = false
        });
        var renameIndex = new RenameIndexOperation
        {
            Name = "IX_Items_Id",
            Table = "Items",
            NewName = "IX_Items_Renamed"
        };
        var generate = () => context.GetService<IMigrationsSqlGenerator>()
            .Generate([createTable, renameIndex]);

        generate.Should().Throw<NotSupportedException>()
            .WithMessage("*renaming indexes*");

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"type\" = 'table' AND \"name\" = 'Items';";

        (await command.ExecuteScalarAsync()).Should().Be(0L);
    }

    private sealed class RenameIndexMigrationContext(
        DbContextOptions<RenameIndexMigrationContext> options) : DbContext(options);
}
