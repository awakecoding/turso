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
            if (operation is CreateTableOperation createTableWithDefaultSql)
            {
                foreach (var column in createTableWithDefaultSql.Columns)
                {
                    ValidateDefaultValueSql(column);
                    ValidateComputedColumn(column);
                }

                foreach (var foreignKey in createTableWithDefaultSql.ForeignKeys)
                {
                    ValidateForeignKeyShape(foreignKey);
                    ValidateReferentialActions(foreignKey);
                }
            }

            if (operation is AddColumnOperation or AlterColumnOperation)
            {
                ValidateDefaultValueSql((ColumnOperation)operation);
                ValidateComputedColumn((ColumnOperation)operation);
            }

            if (operation is CreateTableOperation { CheckConstraints.Count: > 0 } createTable)
            {
                throw new NotSupportedException(
                    $"The managed local provider does not support check constraints on '{createTable.Name}'.");
            }

            if (operation is CreateTableOperation { UniqueConstraints.Count: > 0 } createTableWithUniqueConstraint)
            {
                throw new NotSupportedException(
                    $"The managed local provider does not support unique constraints on '{createTableWithUniqueConstraint.Name}'. " +
                    "Use a unique index instead.");
            }

            if (operation is AddUniqueConstraintOperation addUniqueConstraint)
            {
                throw new NotSupportedException(
                    $"The managed local provider does not support unique constraints ('{addUniqueConstraint.Name}' on '{addUniqueConstraint.Table}'). " +
                    "Use a unique index instead.");
            }

            if (operation is DropUniqueConstraintOperation dropUniqueConstraint)
            {
                throw new NotSupportedException(
                    $"The managed local provider does not support unique constraints ('{dropUniqueConstraint.Name}' on '{dropUniqueConstraint.Table}'). " +
                    "Use a unique index instead.");
            }

            if (operation is AddCheckConstraintOperation addCheckConstraint)
            {
                throw new NotSupportedException(
                    $"The managed local provider does not support check constraints on '{addCheckConstraint.Table}'.");
            }

            if (operation is DropCheckConstraintOperation dropCheckConstraint)
            {
                throw new NotSupportedException(
                    $"The managed local provider does not support dropping check constraints ('{dropCheckConstraint.Name}' on '{dropCheckConstraint.Table}').");
            }

            if (operation is SqlOperation)
            {
                throw new NotSupportedException(
                    "The managed local provider does not support raw SQL migration operations. " +
                    "Use modeled migration operations so managed-local compatibility can be validated before schema mutation.");
            }

            if (operation is CreateIndexOperation { Filter: not null } createIndex)
            {
                throw new NotSupportedException(
                    $"The managed local provider does not support filtered indexes ('{createIndex.Name}' on '{createIndex.Table}').");
            }

            if (operation is RenameIndexOperation renameIndex)
            {
                throw new NotSupportedException(
                    $"The managed local provider does not support renaming indexes ('{renameIndex.Name}' on '{renameIndex.Table}').");
            }

            if (operation is CreateIndexOperation { IsDescending: { } sortOrders } descendingIndex
                && (sortOrders.Length == 0 || sortOrders.Any(static descending => descending)))
            {
                throw new NotSupportedException(
                    $"The managed local provider does not support descending indexes ('{descendingIndex.Name}' on '{descendingIndex.Table}').");
            }
        }
    }

    private static void ValidateDefaultValueSql(ColumnOperation operation)
    {
        if (operation.DefaultValueSql is not null)
        {
            throw new NotSupportedException(
                $"The managed local provider does not support default SQL expressions for '{operation.Name}' on '{operation.Table}'. " +
                "Use a modeled literal default value instead.");
        }
    }

    private static void ValidateComputedColumn(ColumnOperation operation)
    {
        if (operation.ComputedColumnSql is not null && operation.IsStored is not true)
        {
            throw new NotSupportedException(
                $"The managed local provider does not support virtual computed columns for '{operation.Name}' on '{operation.Table}'. " +
                "Declare the computed column as STORED.");
        }
    }
}
