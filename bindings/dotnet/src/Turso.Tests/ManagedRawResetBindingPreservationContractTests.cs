using AwesomeAssertions;
using Turso.Raw.Public;
using Turso.Raw.Public.Value;

namespace Turso.Tests;

public class ManagedRawResetBindingPreservationContractTests
{
    [Test]
    public void ManagedRawResetPreservesBindingsAndMetadataAfterSteppingARow()
    {
        using var database = TursoBindings.OpenManagedDatabase(":memory:");
        using var statement = TursoBindings.PrepareStatement(database, "SELECT ?1 AS retained_value;");

        TursoBindings.GetParameterCount(statement).Should().Be(1);
        TursoBindings.GetParameterName(statement, 1).Should().Be("?1");
        TursoBindings.GetFieldCount(statement).Should().Be(1);
        TursoBindings.GetName(statement, 0).Should().Be("retained_value");
        TursoBindings.BindParameter(statement, 1, TursoValue.Int(7));
        TursoBindings.Read(statement).Should().BeTrue();
        TursoBindings.GetValue(statement, 0).IntValue.Should().Be(7);

        TursoBindings.Reset(statement);

        TursoBindings.GetParameterCount(statement).Should().Be(1);
        TursoBindings.GetParameterName(statement, 1).Should().Be("?1");
        TursoBindings.GetFieldCount(statement).Should().Be(1);
        TursoBindings.GetName(statement, 0).Should().Be("retained_value");
        TursoBindings.Read(statement).Should().BeTrue();
        TursoBindings.GetValue(statement, 0).IntValue.Should().Be(7);
        TursoBindings.Read(statement).Should().BeFalse();
    }

    [Test]
    public void ManagedRawClearBindingsThenResetRemovesBindings()
    {
        using var database = TursoBindings.OpenManagedDatabase(":memory:");
        using var statement = TursoBindings.PrepareStatement(database, "SELECT ?1;");

        TursoBindings.BindParameter(statement, 1, TursoValue.Int(7));
        TursoBindings.ClearBindings(statement);
        TursoBindings.Reset(statement);

        Assert.Throws<TursoException>(() => TursoBindings.Read(statement))!
            .Message.Should().Be("Missing value for parameter ?1.");
    }

    [Test]
    public void ManagedRawClearBindingsAfterSteppingDefersToResetAndAllowsRecovery()
    {
        using var database = TursoBindings.OpenManagedDatabase(":memory:");
        using var statement = TursoBindings.PrepareStatement(database, "SELECT ?1 AS value;");

        TursoBindings.BindParameter(statement, 1, TursoValue.Int(7));
        TursoBindings.Read(statement).Should().BeTrue();
        TursoBindings.ClearBindings(statement);
        TursoBindings.GetValue(statement, 0).IntValue.Should().Be(7);

        TursoBindings.Reset(statement);

        Assert.Throws<TursoException>(() => TursoBindings.Read(statement))!
            .Message.Should().Be("Missing value for parameter ?1.");

        TursoBindings.BindParameter(statement, 1, TursoValue.Int(9));
        TursoBindings.Read(statement).Should().BeTrue();
        TursoBindings.GetValue(statement, 0).IntValue.Should().Be(9);

        TursoBindings.Reset(statement);
        TursoBindings.Read(statement).Should().BeTrue();
        TursoBindings.GetValue(statement, 0).IntValue.Should().Be(9);
    }

    [Test]
    public void ManagedRawLifecycleOperationsRejectDisposedStatements()
    {
        using var database = TursoBindings.OpenManagedDatabase(":memory:");
        var statement = TursoBindings.PrepareStatement(database, "SELECT ?1;");
        statement.Dispose();

        Assert.Throws<NullReferenceException>(() => TursoBindings.Reset(statement))!
            .Message.Should().Be("statement is invalid");
        Assert.Throws<NullReferenceException>(() => TursoBindings.ClearBindings(statement))!
            .Message.Should().Be("statement is invalid");
        Assert.Throws<NullReferenceException>(() => TursoBindings.Read(statement))!
            .Message.Should().Be("statement is invalid");
    }
}
