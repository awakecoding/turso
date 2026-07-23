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
    public override IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
        ValidateOperations(operations);
        return base.Generate(operations, model, options);
    }

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
        ValidateForeignKeyShape(operation);
        ValidateReferentialActions(operation);
        base.ForeignKeyConstraint(operation, model, builder);
    }

    private static void ValidateForeignKeyShape(AddForeignKeyOperation operation)
    {
        if (operation.Columns.Count() == 1 && operation.PrincipalColumns is { Length: 1 })
            return;

        throw new NotSupportedException(
            $"The managed local provider requires exactly one child column and one explicitly named parent column for '{operation.Name}' " +
            $"on '{operation.Table}'.");
    }

    private static void ValidateReferentialActions(AddForeignKeyOperation operation)
    {
        if (operation.OnDelete == ReferentialAction.NoAction
            && operation.OnUpdate == ReferentialAction.NoAction)
        {
            return;
        }

        throw new NotSupportedException(
            $"The managed local provider does not support foreign key referential actions for '{operation.Name}' " +
            $"on '{operation.Table}' (ON DELETE {operation.OnDelete}, ON UPDATE {operation.OnUpdate}). " +
            "Configure both actions as NoAction.");
    }

    private static void ValidateOperations(IReadOnlyList<MigrationOperation> operations)
    {
        foreach (var operation in operations)
        {
            if (operation is CreateIndexOperation { Filter: not null } createIndex)
            {
                throw new NotSupportedException(
                    $"The managed local provider does not support filtered indexes ('{createIndex.Name}' on '{createIndex.Table}').");
            }
        }
    }
}
