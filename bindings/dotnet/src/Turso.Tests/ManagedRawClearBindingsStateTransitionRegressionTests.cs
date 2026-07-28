using AwesomeAssertions;
using Turso.Raw.Public;
using Turso.Raw.Public.Value;

namespace Turso.Tests;

public class ManagedRawClearBindingsStateTransitionRegressionTests
{
    [Test]
    public void ManagedRawClearBindingsAfterHasRowsClearsBeforeSteppingAndAtReset()
    {
        using var database = TursoBindings.OpenManagedDatabase(":memory:");
        using var statement = TursoBindings.PrepareStatement(database, "SELECT ?1 AS value;");

        TursoBindings.BindParameter(statement, 1, TursoValue.Int(7));
        TursoBindings.HasRows(statement).Should().BeTrue();

        TursoBindings.ClearBindings(statement);
        Assert.Throws<TursoException>(() => TursoBindings.Read(statement))!
            .Message.Should().Be("Missing value for parameter ?1.");

        TursoBindings.BindParameter(statement, 1, TursoValue.Int(8));
        TursoBindings.Read(statement).Should().BeTrue();
        TursoBindings.GetValue(statement, 0).IntValue.Should().Be(8);

        TursoBindings.Reset(statement);
        Assert.Throws<TursoException>(() => TursoBindings.Read(statement))!
            .Message.Should().Be("Missing value for parameter ?1.");
    }

    [Test]
    public void ManagedRawClearBindingsDefersOnlyCurrentRowsAndDisposesReplacement()
    {
        using var database = TursoBindings.OpenManagedDatabase(":memory:");
        var statement = TursoBindings.PrepareStatement(database, "SELECT ?1 AS value;");

        TursoBindings.BindParameter(statement, 1, TursoValue.Int(7));
        TursoBindings.ClearBindings(statement);
        Assert.Throws<TursoException>(() => TursoBindings.Read(statement))!
            .Message.Should().Be("Missing value for parameter ?1.");

        TursoBindings.BindParameter(statement, 1, TursoValue.Int(8));
        TursoBindings.Read(statement).Should().BeTrue();
        TursoBindings.ClearBindings(statement);
        TursoBindings.GetValue(statement, 0).IntValue.Should().Be(8);

        TursoBindings.Reset(statement);
        Assert.Throws<TursoException>(() => TursoBindings.Read(statement))!
            .Message.Should().Be("Missing value for parameter ?1.");

        TursoBindings.BindParameter(statement, 1, TursoValue.Int(9));
        TursoBindings.Read(statement).Should().BeTrue();
        TursoBindings.GetValue(statement, 0).IntValue.Should().Be(9);

        statement.Dispose();

        Assert.Throws<NullReferenceException>(() => TursoBindings.ClearBindings(statement))!
            .Message.Should().Be("statement is invalid");
    }
}
