using System.Data;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Sqlite.Storage.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using TursoSqliteConnection = Turso.Data.Sqlite.SqliteConnection;
using TursoSqliteConnectionStringBuilder = Turso.Data.Sqlite.SqliteConnectionStringBuilder;
using TursoSqliteException = Turso.Data.Sqlite.SqliteException;
using TursoSqliteOpenMode = Turso.Data.Sqlite.SqliteOpenMode;

namespace Turso.EntityFrameworkCore.Sqlite.Storage.Internal;

public class TursoSqliteDatabaseCreator(
    RelationalDatabaseCreatorDependencies dependencies,
    ISqliteRelationalConnection connection,
    IRawSqlCommandBuilder rawSqlCommandBuilder)
    : RelationalDatabaseCreator(dependencies)
{
    private const int SQLITE_CANTOPEN = 14;

    public override void Create()
    {
        Dependencies.Connection.Open();
        try
        {
            var connectionOptions = new TursoSqliteConnectionStringBuilder(connection.ConnectionString);
            if (!connectionOptions.IsLocalProviderConfigured
                || connectionOptions.LocalProvider == TursoLocalProvider.Managed)
                return;

            rawSqlCommandBuilder.Build("PRAGMA journal_mode = 'wal';")
                .ExecuteNonQuery(
                    new RelationalCommandParameterObject(
                        Dependencies.Connection,
                        null,
                        null,
                        null,
                        Dependencies.CommandLogger,
                        CommandSource.Migrations));
        }
        finally
        {
            Dependencies.Connection.Close();
        }
    }

    public override bool Exists()
    {
        var connectionOptions = new TursoSqliteConnectionStringBuilder(connection.ConnectionString);
        if (connectionOptions.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || connectionOptions.Mode == TursoSqliteOpenMode.Memory)
        {
            return true;
        }

        if ((!connectionOptions.IsLocalProviderConfigured
                || connectionOptions.LocalProvider == Turso.TursoLocalProvider.Managed)
            && File.Exists(connectionOptions.DataSource))
        {
            return true;
        }

        using var readOnlyConnection = connection.CreateReadOnlyConnection();
        try
        {
            readOnlyConnection.Open(errorsExpected: true);
        }
        catch (TursoSqliteException ex) when (ex.SqliteErrorCode == SQLITE_CANTOPEN)
        {
            return false;
        }
        finally
        {
            readOnlyConnection.Close();
        }

        return true;
    }

    public override bool HasTables()
    {
        var count = (long)rawSqlCommandBuilder
            .Build("SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"type\" = 'table' AND \"rootpage\" IS NOT NULL;")
            .ExecuteScalar(
                new RelationalCommandParameterObject(
                    Dependencies.Connection,
                    null,
                    null,
                    null,
                    Dependencies.CommandLogger,
                    CommandSource.Migrations))!;

        return count != 0;
    }

    public override void Delete()
    {
        var dbConnection = Dependencies.Connection.DbConnection;
        var wasOpen = dbConnection.State == ConnectionState.Open;
        var path = dbConnection.DataSource;
        var connectionOptions = new TursoSqliteConnectionStringBuilder(connection.ConnectionString);

        if (!wasOpen
            && UsesManagedLocalProvider(connectionOptions)
            && !IsRemoteTursoUrl(connectionOptions.DataSource)
            && !string.IsNullOrEmpty(path)
            && !path.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            TursoSqliteConnection.ClearAllPools();
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
            return;
        }

        if (!wasOpen)
            Dependencies.Connection.Open();

        Dependencies.Connection.Close();

        if (!string.IsNullOrEmpty(path) && !path.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            TursoSqliteConnection.ClearAllPools();
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
        else if (wasOpen)
        {
            TursoSqliteConnection.ClearPool(new TursoSqliteConnection(Dependencies.Connection.ConnectionString));
        }

        if (wasOpen)
            Dependencies.Connection.Open();
    }

    private static bool UsesManagedLocalProvider(TursoSqliteConnectionStringBuilder connectionOptions)
        => !connectionOptions.IsLocalProviderConfigured
            || connectionOptions.LocalProvider == Turso.TursoLocalProvider.Managed;

    private static bool IsRemoteTursoUrl(string dataSource)
        => Uri.TryCreate(dataSource, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals("libsql", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase));
}
