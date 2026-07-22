using System.Runtime.InteropServices;
using Turso.Core;

namespace Turso.Raw.Public.Handles;

public class TursoDatabaseHandle() : SafeHandle(IntPtr.Zero, true)
{
    private IntPtr _database;
    private IManagedDatabaseAdapter? _managedDatabase;

    protected override bool ReleaseHandle()
    {
        if (_managedDatabase is { } managedDatabase)
        {
            try
            {
                managedDatabase.Dispose();
            }
            finally
            {
                handle = IntPtr.Zero;
                _managedDatabase = null;
            }

            return true;
        }

        if (handle != IntPtr.Zero)
        {
            _ = TursoInterop.ConnectionClose(handle, out var errorPtr);
            if (errorPtr != IntPtr.Zero)
                TursoInterop.FreeString(errorPtr);

            TursoInterop.ConnectionDeinit(handle);
        }

        if (_database != IntPtr.Zero)
            TursoInterop.DatabaseDeinit(_database);

        handle = IntPtr.Zero;
        _database = IntPtr.Zero;
        return true;
    }

    public void ThrowIfInvalid()
    {
        if (IsClosed || IsInvalid)
            throw new NullReferenceException("database is invalid");
    }

    public static TursoDatabaseHandle FromPtrs(IntPtr database, IntPtr connection)
    {
        var handle = new TursoDatabaseHandle();
        handle._database = database;
        handle.SetHandle(connection);
        return handle;
    }

    public static TursoDatabaseHandle FromManaged(EmbeddedConnection connection)
        => FromManaged(connection, owner: null);

    public static TursoDatabaseHandle FromManaged(EmbeddedConnection connection, EmbeddedDatabase? owner)
        => FromManaged(ManagedDatabaseAdapter.FromConnection(connection, owner));

    public static TursoDatabaseHandle FromManaged(IManagedDatabaseAdapter database)
    {
        ArgumentNullException.ThrowIfNull(database);
        var handle = new TursoDatabaseHandle
        {
            _managedDatabase = database,
        };
        handle.SetHandle(new IntPtr(1));
        return handle;
    }

    public bool IsManaged => _managedDatabase is not null;

    internal IManagedConnectionAdapter ManagedConnection
        => _managedDatabase?.Connection ?? throw new InvalidOperationException("managed database is invalid");

    public override bool IsInvalid => IsManaged ? false : handle == IntPtr.Zero || _database == IntPtr.Zero;
}
