using System.Reflection;
using System.Runtime.InteropServices;

namespace Turso.Tests;

/// <summary>
/// The managed lane runs without the Rust companions built, so tests that genuinely require a
/// native companion skip instead of failing. This keeps the managed engine suite runnable on a
/// host that has no Rust toolchain while still failing loudly for managed regressions.
/// </summary>
internal static class NativeCompanionAvailability
{
    private static readonly Lazy<bool> SdkKitAvailable =
        new(() => Probe("Turso.Raw", "turso_sdk_kit"));

    private static readonly Lazy<bool> SyncSdkKitAvailable =
        new(() => Probe("Turso.Data.Sync", "turso_sync_sdk_kit"));

    internal static void RequireSdkKit()
    {
        if (!SdkKitAvailable.Value)
            Assert.Ignore("The turso_sdk_kit native companion is not available for this test run.");
    }

    internal static void RequireSyncSdkKit()
    {
        if (!SyncSdkKitAvailable.Value)
            Assert.Ignore("The turso_sync_sdk_kit native companion is not available for this test run.");
    }

    private static bool Probe(string assemblyName, string libraryName)
    {
        Assembly assembly;
        try
        {
            assembly = Assembly.Load(assemblyName);
        }
        catch (Exception exception) when (exception is FileNotFoundException or BadImageFormatException)
        {
            return false;
        }

        // Resolving through the owning assembly applies the same probing and any registered
        // DllImport resolver that the real P/Invoke declarations use.
        if (!NativeLibrary.TryLoad(libraryName, assembly, searchPath: null, out var handle))
            return false;

        NativeLibrary.Free(handle);
        return true;
    }
}
