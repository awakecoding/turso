using AwesomeAssertions;
using Turso.Core;
using Turso.Raw.Public;
using Turso.Raw.Public.Handles;

namespace Turso.Tests;

public class RawManagedAdapterCompatibilityShimTests
{
    [Test]
    public void RawHandlesReleaseCoreOwnedManagedAdapters()
    {
        var databaseAdapter = new DisposalTrackingDatabaseAdapter();
        var statementAdapter = new DisposalTrackingStatementAdapter();
        var databaseHandle = TursoDatabaseHandle.FromManaged(databaseAdapter);
        var statementHandle = TursoStatementHandle.FromManaged(statementAdapter);

        statementHandle.Dispose();
        databaseHandle.Dispose();

        statementAdapter.Disposed.Should().BeTrue();
        databaseAdapter.Disposed.Should().BeTrue();
    }

    [Test]
    public void RawLegacyManagedConnectionFactoryUsesTheCoreAdapter()
    {
        using var embeddedDatabase = new EmbeddedDatabase();
        using var databaseHandle = TursoDatabaseHandle.FromManaged(embeddedDatabase.Connect());
        using var statement = TursoBindings.PrepareStatement(databaseHandle, "SELECT 1 AS value;");

        TursoBindings.Read(statement).Should().BeTrue();
        TursoBindings.GetValue(statement, 0).IntValue.Should().Be(1);
    }

    private sealed class DisposalTrackingDatabaseAdapter : IManagedDatabaseAdapter
    {
        public bool Disposed { get; private set; }

        public IManagedConnectionAdapter Connect() => throw new NotSupportedException();

        public IManagedConnectionAdapter Connection => throw new NotSupportedException();

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class DisposalTrackingStatementAdapter : IManagedStatementAdapter
    {
        public bool Disposed { get; private set; }

        public int ParameterCount => throw new NotSupportedException();

        public int RowsAffected => throw new NotSupportedException();

        public void Bind(int index, SqlValue value) => throw new NotSupportedException();

        public int GetParameterIndex(string name) => throw new NotSupportedException();

        public StatementStepResult Step() => throw new NotSupportedException();

        public bool HasRows() => throw new NotSupportedException();

        public void Reset() => throw new NotSupportedException();

        public void ClearBindings() => throw new NotSupportedException();

        public SqlValue GetValue(int ordinal) => throw new NotSupportedException();

        public string GetColumnName(int ordinal) => throw new NotSupportedException();

        public int GetColumnCount() => throw new NotSupportedException();

        public string? GetParameterName(int index) => throw new NotSupportedException();

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
