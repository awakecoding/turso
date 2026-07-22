using System.Runtime.InteropServices;
using Turso.Core;

namespace Turso.Raw.Public.Handles;

public class TursoStatementHandle() : SafeHandle(IntPtr.Zero, true)
{
    private IManagedStatementAdapter? _managedStatement;

    protected override bool ReleaseHandle()
    {
        if (_managedStatement is { } managedStatement)
        {
            try
            {
                managedStatement.Dispose();
            }
            finally
            {
                handle = IntPtr.Zero;
                _managedStatement = null;
            }

            return true;
        }

        _ = TursoInterop.StatementFinalize(handle, out var errorPtr);
        if (errorPtr != IntPtr.Zero)
            TursoInterop.FreeString(errorPtr);

        TursoInterop.StatementDeinit(handle);
        handle = IntPtr.Zero;
        return true;
    }

    public void ThrowIfInvalid()
    {
        if (IsClosed || IsInvalid)
            throw new NullReferenceException("statement is invalid");
    }

    public static TursoStatementHandle FromPtr(IntPtr ptr)
    {
        var handle = new TursoStatementHandle();
        handle.SetHandle(ptr);
        return handle;
    }

    public static TursoStatementHandle FromManaged(
        EmbeddedConnection connection,
        string sql,
        EmbeddedStatement statement)
        => FromManaged(ManagedStatementAdapter.FromPreparedStatement(
            ManagedConnectionAdapter.Wrap(connection),
            sql,
            statement));

    public static TursoStatementHandle FromManaged(IManagedStatementAdapter statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

        var handle = new TursoStatementHandle
        {
            _managedStatement = statement,
        };
        handle.SetHandle(new IntPtr(1));
        return handle;
    }

    internal bool IsManaged => _managedStatement is not null;

    internal IManagedStatementAdapter ManagedStatement
        => _managedStatement ?? throw new InvalidOperationException("managed statement is invalid");

    public override bool IsInvalid => IsManaged ? false : handle == IntPtr.Zero;

}
