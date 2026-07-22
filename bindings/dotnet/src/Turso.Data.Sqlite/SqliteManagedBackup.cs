using Turso.Core;

namespace Turso.Data.Sqlite;

internal static class SqliteManagedBackup
{
    internal static void Copy(SqliteConnection source, SqliteConnection destination, string destinationName, string sourceName)
    {
        if (!source.IsManagedConnection || !destination.IsManagedConnection)
            throw new InvalidOperationException("Managed backup requires managed source and destination connections.");
        ArgumentNullException.ThrowIfNull(destinationName);
        ArgumentNullException.ThrowIfNull(sourceName);
        if (!string.Equals(sourceName, "main", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(destinationName, "main", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(Properties.Resources.ManagedBackupAttachedDatabasesNotSupported);
        }
        if (ReferenceEquals(source, destination))
            throw new ArgumentException(Properties.Resources.ManagedBackupSameConnectionNotSupported, nameof(destination));
        if (source.Transaction is not null || destination.Transaction is not null
            || source.HasOpenReader || destination.HasOpenReader)
        {
            throw new SqliteException(Properties.Resources.SqliteNativeError(5, "database is locked"), 5);
        }

        try
        {
            source.ManagedConnection.CopySnapshotTo(destination.ManagedConnection);
        }
        catch (ManagedSnapshotException exception)
        {
            throw ToSqliteException(exception);
        }
        catch (EmbeddedSqlException exception)
        {
            throw SqliteCommand.ToSqliteException(exception);
        }
    }

    private static Exception ToSqliteException(ManagedSnapshotException exception)
    {
        return exception.Failure switch
        {
            ManagedSnapshotFailure.DestinationNotEmpty
                => new InvalidOperationException(Properties.Resources.ManagedBackupDestinationMustBeEmpty),
            ManagedSnapshotFailure.UnsupportedSchemaObject
                => new NotSupportedException(Properties.Resources.ManagedBackupSchemaObjectNotSupported(exception.ObjectName)),
            ManagedSnapshotFailure.RowidNotAccessible
                => new NotSupportedException(Properties.Resources.ManagedBackupRowidNotAccessible(exception.ObjectName)),
            ManagedSnapshotFailure.ColumnCountMismatch
                => new InvalidOperationException(Properties.Resources.ManagedBackupColumnCountMismatch(exception.ObjectName)),
            _ => throw new InvalidOperationException($"Unknown managed snapshot failure {exception.Failure}."),
        };
    }
}
