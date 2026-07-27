using Turso.Data.Native;

namespace Turso.Tests;

internal static class NativeProviderTestFixture
{
    internal static void EnsureRegistered()
    {
        NativeCompanionAvailability.RequireSdkKit();
        NativeProviderRegistration.Register();
    }
}
