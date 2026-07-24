using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Sqlite.Storage.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using Turso.EntityFrameworkCore.Sqlite.Query.Internal;
using Turso.EntityFrameworkCore.Sqlite.Storage.Internal;
using Turso.EntityFrameworkCore.Sqlite.Update.Internal;
using Turso.EntityFrameworkCore.Sqlite.Migrations.Internal;
using TursoSqliteConnection = Turso.Data.Sqlite.SqliteConnection;
using TursoSqliteConnectionStringBuilder = Turso.Data.Sqlite.SqliteConnectionStringBuilder;
using TursoLocalProvider = Turso.TursoLocalProvider;

namespace Microsoft.EntityFrameworkCore;

public static class TursoDbContextOptionsBuilderExtensions
{
    private const int SupportedEntityFrameworkCoreMajorVersion = 9;

    public static DbContextOptionsBuilder UseTurso(
        this DbContextOptionsBuilder optionsBuilder,
        string? connectionString,
        Action<SqliteDbContextOptionsBuilder>? sqliteOptionsAction = null)
    {
        EnsureSupportedEntityFrameworkCoreVersion();
        var usesManagedLocalProvider = UsesManagedLocalProvider(connectionString);
        optionsBuilder.UseSqlite(connectionString, sqliteOptionsAction);
        return UseTursoServices(optionsBuilder, usesManagedLocalProvider);
    }

    public static DbContextOptionsBuilder UseTurso(
        this DbContextOptionsBuilder optionsBuilder,
        TursoSqliteConnection connection,
        Action<SqliteDbContextOptionsBuilder>? sqliteOptionsAction = null)
        => UseTurso(optionsBuilder, connection, contextOwnsConnection: false, sqliteOptionsAction);

    public static DbContextOptionsBuilder UseTurso(
        this DbContextOptionsBuilder optionsBuilder,
        TursoSqliteConnection connection,
        bool contextOwnsConnection,
        Action<SqliteDbContextOptionsBuilder>? sqliteOptionsAction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        EnsureSupportedEntityFrameworkCoreVersion();
        var usesManagedLocalProvider = UsesManagedLocalProvider(connection.ConnectionString);
        optionsBuilder.UseSqlite(connection, contextOwnsConnection, sqliteOptionsAction);
        return UseTursoServices(optionsBuilder, usesManagedLocalProvider);
    }

    public static DbContextOptionsBuilder<TContext> UseTurso<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        string? connectionString,
        Action<SqliteDbContextOptionsBuilder>? sqliteOptionsAction = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseTurso((DbContextOptionsBuilder)optionsBuilder, connectionString, sqliteOptionsAction);

    public static DbContextOptionsBuilder<TContext> UseTurso<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        TursoSqliteConnection connection,
        Action<SqliteDbContextOptionsBuilder>? sqliteOptionsAction = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseTurso((DbContextOptionsBuilder)optionsBuilder, connection, sqliteOptionsAction);

    public static DbContextOptionsBuilder<TContext> UseTurso<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        TursoSqliteConnection connection,
        bool contextOwnsConnection,
        Action<SqliteDbContextOptionsBuilder>? sqliteOptionsAction = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseTurso((DbContextOptionsBuilder)optionsBuilder, connection, contextOwnsConnection, sqliteOptionsAction);

    private static DbContextOptionsBuilder UseTursoServices(
        DbContextOptionsBuilder optionsBuilder,
        bool usesManagedLocalProvider)
    {
        var configuredOptions = optionsBuilder
            .ReplaceService<ISqliteRelationalConnection, TursoSqliteRelationalConnection>()
            .ReplaceService<IRelationalDatabaseCreator, TursoSqliteDatabaseCreator>()
            .ReplaceService<IQuerySqlGeneratorFactory, TursoSqliteQuerySqlGeneratorFactory>()
            .ReplaceService<IUpdateSqlGenerator, TursoSqliteUpdateSqlGenerator>();

        return usesManagedLocalProvider
            ? configuredOptions
                .ReplaceService<IQuerySqlGeneratorFactory, TursoManagedSqliteQuerySqlGeneratorFactory>()
                .ReplaceService<IQueryableMethodTranslatingExpressionVisitorFactory, TursoManagedSqliteQueryableMethodTranslatingExpressionVisitorFactory>()
                .ReplaceService<IMigrationsSqlGenerator, TursoManagedSqliteMigrationsSqlGenerator>()
                .ReplaceService<IRelationalParameterBasedSqlProcessorFactory, TursoSqliteParameterBasedSqlProcessorFactory>()
            : configuredOptions
                .ReplaceService<IQueryableMethodTranslatingExpressionVisitorFactory, TursoSqliteQueryableMethodTranslatingExpressionVisitorFactory>();
    }

    private static bool UsesManagedLocalProvider(string? connectionString)
    {
        if (connectionString is null)
            return false;

        var connectionOptions = new TursoSqliteConnectionStringBuilder(connectionString);
        if (IsRemoteTursoUrl(connectionOptions.DataSource))
        {
            throw new NotSupportedException(
                "UseTurso supports only local Turso databases. Remote URLs require retry and transaction semantics that are not implemented yet; use TursoConnection directly for remote ADO.NET access.");
        }

        return !connectionOptions.IsLocalProviderConfigured
            || connectionOptions.LocalProvider == TursoLocalProvider.Managed;
    }

    private static void EnsureSupportedEntityFrameworkCoreVersion()
    {
        var loadedVersion = typeof(DbContext).Assembly.GetName().Version;
        if (loadedVersion?.Major != SupportedEntityFrameworkCoreMajorVersion)
        {
            throw new NotSupportedException(
                $"Turso.EntityFrameworkCore.Sqlite supports EF Core 9.x, but EF Core {loadedVersion?.ToString() ?? "with an unknown version"} is loaded.");
        }
    }

    private static bool IsRemoteTursoUrl(string dataSource)
    {
        return Uri.TryCreate(dataSource, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals("libsql", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase));
    }
}
