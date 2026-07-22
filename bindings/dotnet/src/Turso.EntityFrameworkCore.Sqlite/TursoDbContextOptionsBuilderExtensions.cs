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
    public static DbContextOptionsBuilder UseTurso(
        this DbContextOptionsBuilder optionsBuilder,
        string? connectionString,
        Action<SqliteDbContextOptionsBuilder>? sqliteOptionsAction = null)
    {
        optionsBuilder.UseSqlite(connectionString, sqliteOptionsAction);
        return UseTursoServices(optionsBuilder, UsesManagedLocalProvider(connectionString));
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

        optionsBuilder.UseSqlite(connection, contextOwnsConnection, sqliteOptionsAction);
        return UseTursoServices(optionsBuilder, UsesManagedLocalProvider(connection.ConnectionString));
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
            .ReplaceService<IQueryableMethodTranslatingExpressionVisitorFactory, TursoSqliteQueryableMethodTranslatingExpressionVisitorFactory>()
            .ReplaceService<IRelationalParameterBasedSqlProcessorFactory, TursoSqliteParameterBasedSqlProcessorFactory>()
            .ReplaceService<IUpdateSqlGenerator, TursoSqliteUpdateSqlGenerator>();

        return usesManagedLocalProvider
            ? configuredOptions.ReplaceService<IMigrationsSqlGenerator, TursoManagedSqliteMigrationsSqlGenerator>()
            : configuredOptions;
    }

    private static bool UsesManagedLocalProvider(string? connectionString)
    {
        if (connectionString is null)
            return false;

        var connectionOptions = new TursoSqliteConnectionStringBuilder(connectionString);
        return !IsRemoteTursoUrl(connectionOptions.DataSource)
            && (!connectionOptions.IsLocalProviderConfigured
                || connectionOptions.LocalProvider == TursoLocalProvider.Managed);
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
