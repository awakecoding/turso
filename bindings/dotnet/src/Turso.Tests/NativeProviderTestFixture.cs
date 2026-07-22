using Turso.Data.Native;

namespace Turso.Tests;

internal static class NativeProviderTestFixture
{
    internal static void EnsureRegistered() => NativeProviderRegistration.Register();
}
