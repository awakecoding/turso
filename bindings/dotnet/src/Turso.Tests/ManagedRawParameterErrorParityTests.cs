using AwesomeAssertions;
using Turso.Raw.Public;
using Turso.Raw.Public.Value;

namespace Turso.Tests;

public class ManagedRawParameterErrorParityTests
{
    [Test]
    public void ManagedRawOutOfRangeBindMapsToNativeMisuseDiagnostic()
    {
        using var database = TursoBindings.OpenManagedDatabase(":memory:");
        using var statement = TursoBindings.PrepareStatement(database, "SELECT ?1;");

        Assert.Throws<TursoException>(() => TursoBindings.BindParameter(statement, 2, TursoValue.Int(7)))!
            .Message.Should().Be("Turso native call failed with status Misuse.");

        TursoBindings.BindParameter(statement, 1, TursoValue.Int(8));
        TursoBindings.Read(statement).Should().BeTrue();
        TursoBindings.GetValue(statement, 0).IntValue.Should().Be(8);
    }
}
