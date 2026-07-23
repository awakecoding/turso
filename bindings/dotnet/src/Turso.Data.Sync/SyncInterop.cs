using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Turso.Data.Sync;

internal enum SyncStatusCode : uint
{
    Ok = 0,
    Done = 1,
    Row = 2,
    Io = 3,
    Busy = 4,
    Interrupt = 5,
    BusySnapshot = 6,
    Error = 127,
    Misuse = 128,
    Constraint = 129,
    Readonly = 130,
    DatabaseFull = 131,
    NotADatabase = 132,
    Corrupt = 133,
    IoError = 134,
}

internal enum SyncValueType : uint
{
    Unknown = 0,
    Integer = 1,
    Real = 2,
    Text = 3,
    Blob = 4,
    Null = 5,
}

internal enum SyncIoRequestType : int
{
    None = 0,
    Http = 1,
    FullRead = 2,
    FullWrite = 3,
}

internal enum SyncOperationResultType : int
{
    None = 0,
    Connection = 1,
    Changes = 2,
    Stats = 3,
}

[StructLayout(LayoutKind.Sequential)]
internal struct SyncSlice
{
    public IntPtr Pointer;
    public nuint Length;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SyncDatabaseConfig
{
    public ulong AsyncIo;
    public IntPtr Path;
    public IntPtr ExperimentalFeatures;
    public IntPtr Vfs;
    public IntPtr EncryptionCipher;
    public IntPtr EncryptionHexKey;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SyncReplicaConfig
{
    public IntPtr Path;
    public IntPtr RemoteUrl;
    public IntPtr ClientName;
    public int LongPollTimeoutMilliseconds;
    [MarshalAs(UnmanagedType.I1)]
    public bool BootstrapIfEmpty;
    public int ReservedBytes;
    public int PartialBootstrapStrategyPrefix;
    public IntPtr PartialBootstrapStrategyQuery;
    public nuint PartialBootstrapSegmentSize;
    [MarshalAs(UnmanagedType.I1)]
    public bool PartialBootstrapPrefetch;
    public IntPtr RemoteEncryptionKey;
    public IntPtr RemoteEncryptionCipher;
    public nuint PushOperationsThreshold;
    public nuint PullBytesThreshold;
    [MarshalAs(UnmanagedType.I1)]
    public bool LogicalMvccPull;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SyncHttpRequest
{
    public SyncSlice Url;
    public SyncSlice Method;
    public SyncSlice Path;
    public SyncSlice Body;
    public int Headers;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SyncHttpHeader
{
    public SyncSlice Key;
    public SyncSlice Value;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SyncFullReadRequest
{
    public SyncSlice Path;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SyncFullWriteRequest
{
    public SyncSlice Path;
    public SyncSlice Content;
}

internal sealed class SyncDatabaseHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SyncDatabaseHandle() : base(ownsHandle: true)
    {
    }

    public static SyncDatabaseHandle FromRaw(IntPtr value)
    {
        var handle = new SyncDatabaseHandle();
        handle.SetHandle(value);
        return handle;
    }

    protected override bool ReleaseHandle()
    {
        SyncInterop.DatabaseDeinit(handle);
        return true;
    }
}

internal sealed class SyncConnectionHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SyncConnectionHandle() : base(ownsHandle: true)
    {
    }

    public static SyncConnectionHandle FromRaw(IntPtr value)
    {
        var handle = new SyncConnectionHandle();
        handle.SetHandle(value);
        return handle;
    }

    protected override bool ReleaseHandle()
    {
        _ = SyncInterop.ConnectionClose(handle, out var error);
        if (error != IntPtr.Zero)
            SyncInterop.StringDeinit(error);
        SyncInterop.ConnectionDeinit(handle);
        return true;
    }
}

internal sealed class SyncOperationHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SyncOperationHandle() : base(ownsHandle: true)
    {
    }

    public static SyncOperationHandle FromRaw(IntPtr value)
    {
        var handle = new SyncOperationHandle();
        handle.SetHandle(value);
        return handle;
    }

    protected override bool ReleaseHandle()
    {
        SyncInterop.OperationDeinit(handle);
        return true;
    }
}

internal sealed class SyncIoItemHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SyncIoItemHandle() : base(ownsHandle: true)
    {
    }

    public static SyncIoItemHandle FromRaw(IntPtr value)
    {
        var handle = new SyncIoItemHandle();
        handle.SetHandle(value);
        return handle;
    }

    protected override bool ReleaseHandle()
    {
        SyncInterop.IoItemDeinit(handle);
        return true;
    }
}

internal sealed class SyncChangesHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SyncChangesHandle() : base(ownsHandle: true)
    {
    }

    public static SyncChangesHandle FromRaw(IntPtr value)
    {
        var handle = new SyncChangesHandle();
        handle.SetHandle(value);
        return handle;
    }

    public IntPtr Consume()
    {
        var value = DangerousGetHandle();
        SetHandleAsInvalid();
        return value;
    }

    protected override bool ReleaseHandle()
    {
        SyncInterop.ChangesDeinit(handle);
        return true;
    }
}

internal sealed class SyncStatementHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SyncStatementHandle() : base(ownsHandle: true)
    {
    }

    public static SyncStatementHandle FromRaw(IntPtr value)
    {
        var handle = new SyncStatementHandle();
        handle.SetHandle(value);
        return handle;
    }

    protected override bool ReleaseHandle()
    {
        _ = SyncInterop.StatementFinalize(handle, out var error);
        if (error != IntPtr.Zero)
            SyncInterop.StringDeinit(error);
        SyncInterop.StatementDeinit(handle);
        return true;
    }
}

internal static class SyncInterop
{
    private const string DllName = "turso_sync_sdk_kit";

    [DllImport(DllName, EntryPoint = "turso_sync_database_new", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode DatabaseNew(
        ref SyncDatabaseConfig databaseConfig,
        ref SyncReplicaConfig replicaConfig,
        out IntPtr database,
        out IntPtr error);

    [DllImport(DllName, EntryPoint = "turso_sync_database_create", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode DatabaseCreate(
        SyncDatabaseHandle database,
        out IntPtr operation,
        out IntPtr error);

    [DllImport(DllName, EntryPoint = "turso_sync_database_connect", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode DatabaseConnect(
        SyncDatabaseHandle database,
        out IntPtr operation,
        out IntPtr error);

    [DllImport(DllName, EntryPoint = "turso_sync_database_push_changes", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode DatabasePushChanges(
        SyncDatabaseHandle database,
        out IntPtr operation,
        out IntPtr error);

    [DllImport(DllName, EntryPoint = "turso_sync_database_wait_changes", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode DatabaseWaitChanges(
        SyncDatabaseHandle database,
        out IntPtr operation,
        out IntPtr error);

    [DllImport(DllName, EntryPoint = "turso_sync_database_apply_changes", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode DatabaseApplyChanges(
        SyncDatabaseHandle database,
        IntPtr changes,
        out IntPtr operation,
        out IntPtr error);

    [DllImport(DllName, EntryPoint = "turso_sync_operation_resume", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode OperationResume(SyncOperationHandle operation, out IntPtr error);

    [DllImport(DllName, EntryPoint = "turso_sync_operation_result_kind", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncOperationResultType OperationResultKind(SyncOperationHandle operation);

    [DllImport(DllName, EntryPoint = "turso_sync_operation_result_extract_connection", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode OperationExtractConnection(SyncOperationHandle operation, out IntPtr connection);

    [DllImport(DllName, EntryPoint = "turso_sync_operation_result_extract_changes", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode OperationExtractChanges(SyncOperationHandle operation, out IntPtr changes);

    [DllImport(DllName, EntryPoint = "turso_sync_database_io_take_item", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode DatabaseTakeIoItem(
        SyncDatabaseHandle database,
        out IntPtr item,
        out IntPtr error);

    [DllImport(DllName, EntryPoint = "turso_sync_database_io_step_callbacks", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode DatabaseStepIoCallbacks(SyncDatabaseHandle database, out IntPtr error);

    [DllImport(DllName, EntryPoint = "turso_sync_database_io_request_kind", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncIoRequestType IoRequestKind(SyncIoItemHandle item);

    [DllImport(DllName, EntryPoint = "turso_sync_database_io_request_http", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode IoRequestHttp(SyncIoItemHandle item, out SyncHttpRequest request);

    [DllImport(DllName, EntryPoint = "turso_sync_database_io_request_http_header", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode IoRequestHttpHeader(
        SyncIoItemHandle item,
        nuint index,
        out SyncHttpHeader header);

    [DllImport(DllName, EntryPoint = "turso_sync_database_io_request_full_read", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode IoRequestFullRead(SyncIoItemHandle item, out SyncFullReadRequest request);

    [DllImport(DllName, EntryPoint = "turso_sync_database_io_request_full_write", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode IoRequestFullWrite(SyncIoItemHandle item, out SyncFullWriteRequest request);

    [DllImport(DllName, EntryPoint = "turso_sync_database_io_poison", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode IoPoison(SyncIoItemHandle item, ref SyncSlice error);

    [DllImport(DllName, EntryPoint = "turso_sync_database_io_status", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode IoStatus(SyncIoItemHandle item, int status);

    [DllImport(DllName, EntryPoint = "turso_sync_database_io_push_buffer", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode IoPushBuffer(SyncIoItemHandle item, ref SyncSlice buffer);

    [DllImport(DllName, EntryPoint = "turso_sync_database_io_done", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode IoDone(SyncIoItemHandle item);

    [DllImport(DllName, EntryPoint = "turso_sync_database_deinit", CallingConvention = CallingConvention.Cdecl)]
    public static extern void DatabaseDeinit(IntPtr database);

    [DllImport(DllName, EntryPoint = "turso_sync_operation_deinit", CallingConvention = CallingConvention.Cdecl)]
    public static extern void OperationDeinit(IntPtr operation);

    [DllImport(DllName, EntryPoint = "turso_sync_database_io_item_deinit", CallingConvention = CallingConvention.Cdecl)]
    public static extern void IoItemDeinit(IntPtr item);

    [DllImport(DllName, EntryPoint = "turso_sync_changes_deinit", CallingConvention = CallingConvention.Cdecl)]
    public static extern void ChangesDeinit(IntPtr changes);

    [DllImport(DllName, EntryPoint = "turso_connection_close", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode ConnectionClose(IntPtr connection, out IntPtr error);

    [DllImport(DllName, EntryPoint = "turso_connection_deinit", CallingConvention = CallingConvention.Cdecl)]
    public static extern void ConnectionDeinit(IntPtr connection);

    [DllImport(DllName, EntryPoint = "turso_connection_prepare_single", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode ConnectionPrepareSingle(
        SyncConnectionHandle connection,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
        out IntPtr statement,
        out IntPtr error);

    [DllImport(DllName, EntryPoint = "turso_statement_finalize", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode StatementFinalize(IntPtr statement, out IntPtr error);

    [DllImport(DllName, EntryPoint = "turso_statement_deinit", CallingConvention = CallingConvention.Cdecl)]
    public static extern void StatementDeinit(IntPtr statement);

    [DllImport(DllName, EntryPoint = "turso_statement_step", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode StatementStep(SyncStatementHandle statement, out IntPtr error);

    [DllImport(DllName, EntryPoint = "turso_statement_run_io", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode StatementRunIo(SyncStatementHandle statement, out IntPtr error);

    [DllImport(DllName, EntryPoint = "turso_statement_n_change", CallingConvention = CallingConvention.Cdecl)]
    public static extern long StatementRowsAffected(SyncStatementHandle statement);

    [DllImport(DllName, EntryPoint = "turso_statement_column_count", CallingConvention = CallingConvention.Cdecl)]
    public static extern long StatementColumnCount(SyncStatementHandle statement);

    [DllImport(DllName, EntryPoint = "turso_statement_column_name", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr StatementColumnName(SyncStatementHandle statement, nuint index);

    [DllImport(DllName, EntryPoint = "turso_statement_row_value_kind", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncValueType StatementRowValueKind(SyncStatementHandle statement, nuint index);

    [DllImport(DllName, EntryPoint = "turso_statement_row_value_bytes_count", CallingConvention = CallingConvention.Cdecl)]
    public static extern long StatementRowValueBytesCount(SyncStatementHandle statement, nuint index);

    [DllImport(DllName, EntryPoint = "turso_statement_row_value_bytes_ptr", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr StatementRowValueBytesPtr(SyncStatementHandle statement, nuint index);

    [DllImport(DllName, EntryPoint = "turso_statement_row_value_int", CallingConvention = CallingConvention.Cdecl)]
    public static extern long StatementRowValueInt(SyncStatementHandle statement, nuint index);

    [DllImport(DllName, EntryPoint = "turso_statement_row_value_double", CallingConvention = CallingConvention.Cdecl)]
    public static extern double StatementRowValueDouble(SyncStatementHandle statement, nuint index);

    [DllImport(DllName, EntryPoint = "turso_statement_named_position", CallingConvention = CallingConvention.Cdecl)]
    public static extern long StatementNamedPosition(
        SyncStatementHandle statement,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(DllName, EntryPoint = "turso_statement_parameters_count", CallingConvention = CallingConvention.Cdecl)]
    public static extern long StatementParameterCount(SyncStatementHandle statement);

    [DllImport(DllName, EntryPoint = "turso_statement_parameter_name", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr StatementParameterName(SyncStatementHandle statement, long index);

    [DllImport(DllName, EntryPoint = "turso_statement_bind_positional_null", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode StatementBindNull(SyncStatementHandle statement, nuint position);

    [DllImport(DllName, EntryPoint = "turso_statement_bind_positional_int", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode StatementBindInt(SyncStatementHandle statement, nuint position, long value);

    [DllImport(DllName, EntryPoint = "turso_statement_bind_positional_double", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode StatementBindDouble(SyncStatementHandle statement, nuint position, double value);

    [DllImport(DllName, EntryPoint = "turso_statement_bind_positional_text", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode StatementBindText(
        SyncStatementHandle statement,
        nuint position,
        IntPtr value,
        nuint length);

    [DllImport(DllName, EntryPoint = "turso_statement_bind_positional_blob", CallingConvention = CallingConvention.Cdecl)]
    public static extern SyncStatusCode StatementBindBlob(
        SyncStatementHandle statement,
        nuint position,
        IntPtr value,
        nuint length);

    [DllImport(DllName, EntryPoint = "turso_str_deinit", CallingConvention = CallingConvention.Cdecl)]
    public static extern void StringDeinit(IntPtr value);
}
