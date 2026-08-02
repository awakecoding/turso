using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Sqlite.Metadata.Internal;

namespace Ahtola.EntityFrameworkCore.Sqlite.Migrations.Internal;

public sealed class AhtolaManagedSqliteMigrationsSqlGenerator(
    MigrationsSqlGeneratorDependencies dependencies,
    IRelationalAnnotationProvider migrationsAnnotations)
    : SqliteMigrationsSqlGenerator(dependencies, migrationsAnnotations)
{
    public override IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
        if (options.HasFlag(MigrationsSqlGenerationOptions.Idempotent))
        {
            throw new NotSupportedException(
                "The managed local provider does not support idempotent migration scripts because the engine cannot conditionally execute DDL blocks.");
        }

        ValidateOperations(operations, model);
        return base.Generate(RewriteDropColumnOperations(operations), model, options);
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
        base.ForeignKeyConstraint(operation, model, builder);
    }

    private IReadOnlyList<MigrationOperation> RewriteDropColumnOperations(
        IReadOnlyList<MigrationOperation> operations)
    {
        var rewritten = new MigrationOperation[operations.Count];
        var sql = Dependencies.SqlGenerationHelper;
        for (var index = 0; index < operations.Count; index++)
        {
            rewritten[index] = operations[index] is DropColumnOperation dropColumn
                ? new SqlOperation
                {
                    Sql = "ALTER TABLE "
                        + sql.DelimitIdentifier(dropColumn.Table, dropColumn.Schema)
                        + " DROP COLUMN "
                        + sql.DelimitIdentifier(dropColumn.Name)
                        + sql.StatementTerminator,
                }
                : operations[index];
        }

        return rewritten;
    }

    private static void ValidateOperations(IReadOnlyList<MigrationOperation> operations, IModel? model)
    {
        foreach (var operation in operations)
        {
            if (operation is CreateTableOperation createTableWithDefaultSql)
            {
                foreach (var column in createTableWithDefaultSql.Columns)
                    ValidateComputedColumn(column);

            }

            if (operation is AddColumnOperation or AlterColumnOperation)
                ValidateComputedColumn((ColumnOperation)operation);

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

            if (operation is CreateIndexOperation createIndex)
                ValidateCreateIndex(createIndex);

            if (operation is RenameIndexOperation renameIndex)
                ValidateRenameIndex(renameIndex, model);

            if (operation is RenameTableOperation renameTable)
                ValidateRenameTable(renameTable, model);

            if (operation is RenameColumnOperation renameColumn)
                ValidateRenameColumn(renameColumn, model);
        }
    }

    private static void ValidateRenameTable(RenameTableOperation operation, IModel? model)
    {
        if (operation.NewName is null || operation.NewName == operation.Name)
            return;

        var targetTable = model?.GetRelationalModel()
            .FindTable(operation.NewName, operation.NewSchema);
        if (targetTable is null)
        {
            throw new NotSupportedException(
                $"The managed local provider can rename table '{operation.Name}' only when the target model contains '{operation.NewName}'.");
        }

        if (targetTable.ForeignKeyConstraints.Any()
            || targetTable.ReferencingForeignKeyConstraints.Any()
            || targetTable.Triggers.Any())
        {
            throw new NotSupportedException(
                $"The managed local provider cannot safely rename table '{operation.Name}' because the target table '{operation.NewName}' has foreign key or trigger dependencies.");
        }
    }

    private static void ValidateRenameColumn(RenameColumnOperation operation, IModel? model)
    {
        var targetTable = model?.GetRelationalModel()
            .FindTable(operation.Table, operation.Schema);
        var targetColumn = targetTable?.FindColumn(operation.NewName);
        if (targetTable is null || targetColumn is null)
        {
            throw new NotSupportedException(
                $"The managed local provider can rename column '{operation.Name}' on '{operation.Table}' only when the target model contains '{operation.NewName}'.");
        }

        var hasForeignKeyDependency = targetTable.ForeignKeyConstraints
                .Concat(targetTable.ReferencingForeignKeyConstraints)
                .Any(foreignKey => foreignKey.Columns.Contains(targetColumn)
                    || foreignKey.PrincipalColumns.Contains(targetColumn));
        var hasTableConstraintDependency = targetTable.PrimaryKey is { Columns.Count: > 1 } primaryKey
                && primaryKey.Columns.Contains(targetColumn)
            || targetTable.UniqueConstraints.Any(uniqueConstraint =>
                uniqueConstraint != targetTable.PrimaryKey
                && uniqueConstraint.Columns.Contains(targetColumn));
        if (hasForeignKeyDependency
            || hasTableConstraintDependency
            || targetTable.Triggers.Any()
            || targetTable.Columns.Any(column => column.ComputedColumnSql is not null))
        {
            throw new NotSupportedException(
                $"The managed local provider cannot safely rename column '{operation.Name}' on '{operation.Table}' because the target table has foreign key, table-constraint, trigger, or computed-column dependencies.");
        }
    }

    private static void ValidateRenameIndex(RenameIndexOperation operation, IModel? model)
    {
        var targetIndex = operation.Table is null
            ? null
            : model?.GetRelationalModel()
                .FindTable(operation.Table, operation.Schema)?
                .Indexes.FirstOrDefault(index => index.Name == operation.NewName);
        if (targetIndex is null)
        {
            throw new NotSupportedException(
                $"The managed local provider can rename index '{operation.Name}' on '{operation.Table}' only when the target model contains '{operation.NewName}'.");
        }

        ValidateCreateIndex(CreateIndexOperation.CreateFrom(targetIndex));
    }

    private static void ValidateCreateIndex(CreateIndexOperation operation)
    {
        if (operation.Filter is not null)
        {
            throw new NotSupportedException(
                $"The managed local provider does not support filtered indexes ('{operation.Name}' on '{operation.Table}').");
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
