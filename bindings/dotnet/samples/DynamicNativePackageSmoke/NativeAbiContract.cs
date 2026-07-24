using System.Reflection;
using System.Runtime.InteropServices;
using Turso.Data.Sync;
using Turso.Raw.Public;

internal static class NativeAbiContract
{
    public static void Validate()
    {
        var rawInterop = typeof(TursoExtensionValue).Assembly.GetType("Turso.Raw.TursoInterop", true)!;
        var syncInterop = typeof(ReplicaProviderRegistration).Assembly.GetType("Turso.Data.Sync.SyncInterop", true)!;
        using var rawLibrary = LoadLibrary("turso_sdk_kit");
        using var syncLibrary = LoadLibrary("turso_sync_sdk_kit");

        ValidateExports(rawInterop, rawLibrary.Handle);
        ValidateExports(syncInterop, syncLibrary.Handle);
        ValidateRawLayout(rawInterop);
        ValidateSyncLayout(syncInterop);
    }

    private static void ValidateRawLayout(Type interop)
    {
        RequireEqual(1u, Invoke<uint>(interop, "AbiVersion"), "Raw ABI version");
        ValidateEnum(interop, 1, interop.Assembly.GetType("Turso.Raw.TursoStatusCode", true)!);
        ValidateEnum(interop, 2, interop.Assembly.GetType("Turso.Raw.TursoNativeValueType", true)!);
        ValidateEnum(interop, 3, typeof(TursoExtensionValueType));
        ValidateStruct(
            interop,
            4,
            interop.Assembly.GetType("Turso.Raw.TursoDatabaseConfig", true)!,
            "AsyncIo",
            "Path",
            "ExperimentalFeatures",
            "Vfs",
            "EncryptionCipher",
            "EncryptionHexKey");
        ValidateStruct(interop, 5, typeof(TursoExtensionValue), "ValueType", "Value");
        ValidateStruct(interop, 6, typeof(TursoExtensionValueUnion));
    }

    private static void ValidateSyncLayout(Type interop)
    {
        RequireEqual(1u, Invoke<uint>(interop, "AbiVersion"), "Sync ABI version");
        ValidateEnum(interop, 1, interop.Assembly.GetType("Turso.Data.Sync.SyncIoRequestType", true)!);
        ValidateEnum(interop, 2, interop.Assembly.GetType("Turso.Data.Sync.SyncOperationResultType", true)!);
        ValidateStruct(interop, 3, interop.Assembly.GetType("Turso.Data.Sync.SyncSlice", true)!, "Pointer", "Length");
        ValidateStruct(
            interop,
            4,
            interop.Assembly.GetType("Turso.Data.Sync.SyncDatabaseConfig", true)!,
            "AsyncIo",
            "Path",
            "ExperimentalFeatures",
            "Vfs",
            "EncryptionCipher",
            "EncryptionHexKey");
        ValidateStruct(
            interop,
            5,
            interop.Assembly.GetType("Turso.Data.Sync.SyncReplicaConfig", true)!,
            "Path",
            "RemoteUrl",
            "ClientName",
            "LongPollTimeoutMilliseconds",
            "BootstrapIfEmpty",
            "ReservedBytes",
            "PartialBootstrapStrategyPrefix",
            "PartialBootstrapStrategyQuery",
            "PartialBootstrapSegmentSize",
            "PartialBootstrapPrefetch",
            "RemoteEncryptionKey",
            "RemoteEncryptionCipher",
            "PushOperationsThreshold",
            "PullBytesThreshold",
            "LogicalMvccPull");
        ValidateStruct(
            interop,
            6,
            interop.Assembly.GetType("Turso.Data.Sync.SyncHttpRequest", true)!,
            "Url",
            "Method",
            "Path",
            "Body",
            "Headers");
        ValidateStruct(
            interop,
            7,
            interop.Assembly.GetType("Turso.Data.Sync.SyncHttpHeader", true)!,
            "Key",
            "Value");
        ValidateStruct(
            interop,
            8,
            interop.Assembly.GetType("Turso.Data.Sync.SyncFullReadRequest", true)!,
            "Path");
        ValidateStruct(
            interop,
            9,
            interop.Assembly.GetType("Turso.Data.Sync.SyncFullWriteRequest", true)!,
            "Path",
            "Content");
    }

    private static void ValidateEnum(Type interop, uint nativeType, Type managedType)
    {
        var managedSize = (nuint)Marshal.SizeOf(Enum.GetUnderlyingType(managedType));
        RequireEqual(managedSize, Invoke<nuint>(interop, "AbiSizeOf", nativeType), managedType.FullName!);
    }

    private static void ValidateStruct(Type interop, uint nativeType, Type managedType, params string[] fields)
    {
        RequireEqual(
            (nuint)Marshal.SizeOf(managedType),
            Invoke<nuint>(interop, "AbiSizeOf", nativeType),
            $"{managedType.FullName} size");
        for (uint field = 0; field < fields.Length; field++)
        {
            RequireEqual(
                (nuint)Marshal.OffsetOf(managedType, fields[field]).ToInt64(),
                Invoke<nuint>(interop, "AbiOffsetOf", nativeType, field),
                $"{managedType.FullName}.{fields[field]} offset");
        }
    }

    private static void ValidateExports(Type interop, IntPtr library)
    {
        var imports = interop
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Select(method => method.GetCustomAttribute<DllImportAttribute>())
            .Where(import => import is not null);
        foreach (var import in imports)
        {
            var symbol = import!.EntryPoint;
            if (string.IsNullOrEmpty(symbol) || !NativeLibrary.TryGetExport(library, symbol, out _))
                throw new InvalidOperationException($"Missing native export {symbol}.");
        }
    }

    private static T Invoke<T>(Type interop, string method, params object[] arguments)
        => (T)interop.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, arguments)!;

    private static void RequireEqual<T>(T expected, T actual, string contract)
        where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
            throw new InvalidOperationException($"{contract}: managed={expected}, native={actual}.");
    }

    private static NativeLibraryHandle LoadLibrary(string baseName)
    {
        var fileName = OperatingSystem.IsWindows()
            ? $"{baseName}.dll"
            : OperatingSystem.IsMacOS()
                ? $"lib{baseName}.dylib"
                : $"lib{baseName}.so";
        return new NativeLibraryHandle(NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, fileName)));
    }

    private sealed class NativeLibraryHandle(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; } = handle;

        public void Dispose() => NativeLibrary.Free(Handle);
    }
}
