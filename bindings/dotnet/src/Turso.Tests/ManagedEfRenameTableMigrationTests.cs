using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Turso.Data.Sqlite;

namespace Turso.Tests;

public sealed class ManagedEfRenameTableMigrationTests
{
    [Test]
    public async Task ManagedMigrationsRejectTableRenamesBeforeGeneratingCommands()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<RenameTableMigrationContext>()
            .UseTurso(connection)
            .Options;
        await using var context = new RenameTableMigrationContext(options);

        var createTable = new CreateTableOperation { Name = "Parents" };
        createTable.Columns.Add(new AddColumnOperation
        {
            Table = "Parents",
            Name = "Id",
            ClrType = typeof(long),
            ColumnType = "INTEGER",
            IsNullable = false,
        });

        var generate = () => context.GetService<IMigrationsSqlGenerator>().Generate(
        [
            createTable,
            new RenameTableOperation { Name = "Parents", NewName = "RenamedParents" },
        ]);

        generate.Should().Throw<NotSupportedException>()
            .WithMessage("*renaming tables*");

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"type\" = 'table' AND \"name\" IN ('Parents', 'RenamedParents');";

        (await command.ExecuteScalarAsync()).Should().Be(0L);
    }

    private sealed class RenameTableMigrationContext(
        DbContextOptions<RenameTableMigrationContext> options) : DbContext(options);
}
