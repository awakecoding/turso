using System.Runtime.InteropServices;
using Turso.Raw.Data;
using Turso.Raw.Public;
using Turso.Raw.Public.Handles;

namespace Turso.Raw;

internal static class TursoInterop
{
    private const string DllName = "turso_dotnet";

    [DllImport(DllName, EntryPoint = "db_open", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr OpenDatabase([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr errorPtr);

    [DllImport(DllName, EntryPoint = "db_open_with_encryption", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr OpenDatabaseWithEncryption(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? cipher,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? hexkey,
        out IntPtr errorPtr);

    [DllImport(DllName, EntryPoint = "db_close", CallingConvention = CallingConvention.Cdecl)]
    public static extern void CloseDatabase(IntPtr db);

    [DllImport(DllName, EntryPoint = "db_register_scalar_function", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool RegisterScalarFunction(
        TursoDatabaseHandle db,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int argc,
        [MarshalAs(UnmanagedType.I1)] bool deterministic,
        IntPtr context,
        TursoScalarFunctionCallback callback,
        TursoContextDestructorCallback contextDestructor,
        TursoValueDestructorCallback valueDestructor,
        out IntPtr errorPtr);

    [DllImport(DllName, EntryPoint = "db_register_aggregate_function", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool RegisterAggregateFunction(
        TursoDatabaseHandle db,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int argc,
        [MarshalAs(UnmanagedType.I1)] bool deterministic,
        IntPtr context,
        TursoAggregateInitCallback init,
        TursoAggregateStepCallback step,
        TursoAggregateFinalCallback finalize,
        TursoContextDestructorCallback contextDestructor,
        TursoContextDestructorCallback aggregateDestructor,
        TursoValueDestructorCallback valueDestructor,
        out IntPtr errorPtr);

    [DllImport(DllName, EntryPoint = "db_unregister_function", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool UnregisterFunction(
        TursoDatabaseHandle db,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        out IntPtr errorPtr);

    [DllImport(DllName, EntryPoint = "db_register_collation", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool RegisterCollation(
        TursoDatabaseHandle db,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        IntPtr context,
        TursoCollationCallback callback,
        TursoContextDestructorCallback contextDestructor,
        out IntPtr errorPtr);

    [DllImport(DllName, EntryPoint = "db_unregister_collation", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool UnregisterCollation(
        TursoDatabaseHandle db,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        out IntPtr errorPtr);

    [DllImport(DllName, EntryPoint = "db_enable_load_extension", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool EnableLoadExtension(
        TursoDatabaseHandle db,
        [MarshalAs(UnmanagedType.I1)] bool enabled);

    [DllImport(DllName, EntryPoint = "db_load_extension", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool LoadExtension(
        TursoDatabaseHandle db,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        out IntPtr errorPtr);

    [DllImport(DllName, EntryPoint = "free_string", CallingConvention = CallingConvention.Cdecl)]
    public static extern void FreeString(IntPtr stringPtr);

    [DllImport(DllName, EntryPoint = "db_prepare_statement", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr PrepareStatement(TursoDatabaseHandle db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, out IntPtr errorPtr);

    [DllImport(DllName, EntryPoint = "free_statement", CallingConvention = CallingConvention.Cdecl)]
    public static extern void FreeStatement(IntPtr statement);

    [DllImport(DllName, EntryPoint = "bind_parameter", CallingConvention = CallingConvention.Cdecl)]
    public static extern void BindParameter(TursoStatementHandle statement, int index, IntPtr tursoValue);

    [DllImport(DllName, EntryPoint = "bind_named_parameter", CallingConvention = CallingConvention.Cdecl)]
    public static extern int BindNamedParameter(TursoStatementHandle statement, [MarshalAs(UnmanagedType.LPUTF8Str)] string parameterName, IntPtr tursoValue);

    [DllImport(DllName, EntryPoint = "db_statement_execute_step", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool StatementExecuteStep(TursoStatementHandle statement, out IntPtr errorPtr);

    [DllImport(DllName, EntryPoint = "db_statement_nchange", CallingConvention = CallingConvention.Cdecl)]
    public static extern long StatementRowsAffected(TursoStatementHandle statement);

    [DllImport(DllName, EntryPoint = "db_statement_get_value", CallingConvention = CallingConvention.Cdecl)]
    public static extern TursoNativeValue GetValueFromStatement(TursoStatementHandle statement, int columnIndex);

    [DllImport(DllName, EntryPoint = "db_statement_num_columns", CallingConvention = CallingConvention.Cdecl)]
    public static extern int StatementNumColumns(TursoStatementHandle statement);

    [DllImport(DllName, EntryPoint = "db_statement_column_name", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr StatementColumnName(TursoStatementHandle statement, int index);

    [DllImport(DllName, EntryPoint = "db_statement_has_rows", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool StatementHasRows(TursoStatementHandle statement);

    [DllImport(DllName, EntryPoint = "db_statement_parameter_count", CallingConvention = CallingConvention.Cdecl)]
    public static extern int StatementParameterCount(TursoStatementHandle statement);

    [DllImport(DllName, EntryPoint = "db_statement_parameter_name", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr StatementParameterName(TursoStatementHandle statement, int index);

}
