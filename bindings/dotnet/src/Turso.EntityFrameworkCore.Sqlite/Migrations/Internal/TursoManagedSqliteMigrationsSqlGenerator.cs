using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Sqlite.Metadata.Internal;

namespace Turso.EntityFrameworkCore.Sqlite.Migrations.Internal;

public sealed class TursoManagedSqliteMigrationsSqlGenerator(
    MigrationsSqlGeneratorDependencies dependencies,
    IRelationalAnnotationProvider migrationsAnnotations)
    : SqliteMigrationsSqlGenerator(dependencies, migrationsAnnotations)
{
    protected override void ColumnDefinition(
        string? schema,
        string table,
        string name,
        ColumnOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        var autoincrement = operation.FindAnnotation(SqliteAnnotationNames.Autoincrement);
        var legacyAutoincrement = operation.FindAnnotation(SqliteAnnotationNames.LegacyAutoincrement);
        if (autoincrement is null && legacyAutoincrement is null)
        {
            base.ColumnDefinition(schema, table, name, operation, model, builder);
            return;
        }

        operation.RemoveAnnotation(SqliteAnnotationNames.Autoincrement);
        operation.RemoveAnnotation(SqliteAnnotationNames.LegacyAutoincrement);
        try
        {
            base.ColumnDefinition(schema, table, name, operation, model, builder);
        }
        finally
        {
            if (autoincrement is not null)
                operation.SetAnnotation(autoincrement.Name, autoincrement.Value);

            if (legacyAutoincrement is not null)
                operation.SetAnnotation(legacyAutoincrement.Name, legacyAutoincrement.Value);
        }
    }

    protected override void ForeignKeyConstraint(
        AddForeignKeyOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        var onDelete = operation.OnDelete;
        var onUpdate = operation.OnUpdate;
        operation.OnDelete = ReferentialAction.NoAction;
        operation.OnUpdate = ReferentialAction.NoAction;
        try
        {
            base.ForeignKeyConstraint(operation, model, builder);
        }
        finally
        {
            operation.OnDelete = onDelete;
            operation.OnUpdate = onUpdate;
        }
    }
}
