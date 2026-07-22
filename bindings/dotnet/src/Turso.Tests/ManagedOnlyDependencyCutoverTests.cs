using System.Reflection;
using AwesomeAssertions;

namespace Turso.Tests;

public class ManagedOnlyDependencyCutoverTests
{
    [Test]
    public void ManagedProviderAssemblyDoesNotReferenceTursoRaw()
    {
        typeof(TursoConnection)
            .Assembly
            .GetReferencedAssemblies()
            .Select(assemblyName => assemblyName.Name)
            .Should()
            .NotContain("Turso.Raw");
    }

    [Test]
    public void ExplicitNativeProviderUsesRegisteredCompanion()
    {
        NativeProviderTestFixture.EnsureRegistered();
        using var connection = new TursoConnection("Data Source=:memory:;Local Provider=Native");
        connection.Open();

        var nativeDatabase = typeof(TursoConnection)
            .GetField("_nativeDatabase", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(connection);
        nativeDatabase.Should().NotBeNull();
    }
}
