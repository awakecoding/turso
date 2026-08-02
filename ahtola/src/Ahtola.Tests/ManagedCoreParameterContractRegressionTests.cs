using System.Data;
using System.Reflection;
using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedCoreParameterContractRegressionTests
{
    [Test]
    public void CoreParameterContractExposesMetadataAndPreservesClearResetRebind()
    {
        typeof(IManagedStatementAdapter).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Should().NotContain("Turso.Raw");

        using var database = ManagedDatabaseAdapter.Open(":memory:");
        using var connection = database.Connect();
        using var statement = connection.Prepare("SELECT ?1, $name, ?;");

        var parameters = statement.ParameterMetadata;
        parameters.Count.Should().Be(3);
        parameters.GetParameter(1).Should().Be(new ManagedParameter(1, "?1"));
        parameters.GetParameter(2).Should().Be(new ManagedParameter(2, "$name"));
        parameters.GetParameter(3).Should().Be(new ManagedParameter(3, null));
        parameters.GetParameterIndex("$name").Should().Be(2);

        statement.Bind(1, SqlValue.Integer(7));
        statement.Bind(2, SqlValue.Text("first"));
        statement.Bind(3, SqlValue.Blob([1, 2]));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).AsInteger().Should().Be(7);
        statement.GetValue(1).AsText().Should().Be("first");
        statement.GetValue(2).AsBlob().ToArray().Should().Equal(1, 2);

        statement.ClearBindings();
        statement.GetValue(0).AsInteger().Should().Be(7);
        statement.Reset();

        Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
            .Message.Should().Be("Missing value for parameter ?1.");

        statement.Bind(1, SqlValue.Integer(8));
        statement.Bind(2, SqlValue.Text("second"));
        statement.Bind(3, SqlValue.Null);
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).AsInteger().Should().Be(8);
        statement.GetValue(1).AsText().Should().Be("second");
        statement.GetValue(2).Kind.Should().Be(SqlValueKind.Null);
    }

    [Test]
    public void ManagedSqliteFacadeBindsTypedNumberedNamedAndPositionalValuesAfterRebind()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        GetPrivateField(connection, "_database").Should().BeNull();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ?1, :name, ?, $blob, $nullable;";
        BindSqliteParameters(command, 7L, "first", "positional", [1, 2, 3], DBNull.Value);

        AssertSqliteValues(command, 7L, "first", "positional", [1, 2, 3], isNull: true);

        command.Parameters.Clear();
        Assert.Throws<InvalidOperationException>(() => command.ExecuteScalar())!
            .Message.Should().Be("Missing parameter values for ?1.");

        BindSqliteParameters(command, 8L, "second", "rebound", [4, 5], DBNull.Value);
        AssertSqliteValues(command, 8L, "second", "rebound", [4, 5], isNull: true);

        var outputParameter = new SqliteParameter();
        Assert.Throws<ArgumentException>(() => outputParameter.Direction = ParameterDirection.Output);
    }

    [Test]
    public void ManagedSqliteFacadeMapsCoreStatementErrorsWithoutRawExceptionTranslation()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT missing_managed_parameter_function(?1);";
        command.Parameters.AddWithValue("?1", 1L);

        Assert.Throws<SqliteException>(() => command.ExecuteScalar())!
            .SqliteErrorCode.Should().Be(1);
    }

    [Test]
    public void ManagedAhtolaFacadeBindsNumberedNamedAndPositionalValuesAfterRebind()
    {
        using var connection = new AhtolaConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        GetPrivateField(connection, "_nativeDatabase").Should().BeNull();

        using var command = new AhtolaCommand(connection);
        command.CommandText = "SELECT ?1, $name, ?;";
        BindAhtolaParameters(command, 7L, "first", "positional");
        AssertAhtolaValues(command, 7L, "first", "positional");

        command.Parameters.Clear();
        Assert.Throws<InvalidOperationException>(() => command.ExecuteScalar())!
            .Message.Should().Be("Missing value for parameter ?1.");

        BindAhtolaParameters(command, 8L, "second", "rebound");
        AssertAhtolaValues(command, 8L, "second", "rebound");

        var outputParameter = new AhtolaParameter();
        Assert.Throws<ArgumentException>(() => outputParameter.Direction = ParameterDirection.Output);
    }

    private static void BindSqliteParameters(
        SqliteCommand command,
        long numbered,
        string named,
        string positional,
        byte[] blob,
        object nullable)
    {
        command.Parameters.AddWithValue(null, positional);
        command.Parameters.AddWithValue("name", named);
        command.Parameters.AddWithValue("?1", numbered);
        var blobParameter = command.Parameters.Add("$blob", SqliteType.Blob);
        blobParameter.DbType = DbType.Binary;
        blobParameter.Value = blob;
        command.Parameters.AddWithValue("$nullable", nullable);
    }

    private static void AssertSqliteValues(
        SqliteCommand command,
        long numbered,
        string named,
        string positional,
        byte[] blob,
        bool isNull)
    {
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        reader.GetInt64(0).Should().Be(numbered);
        reader.GetString(1).Should().Be(named);
        reader.GetString(2).Should().Be(positional);
        reader.GetFieldValue<byte[]>(3).Should().Equal(blob);
        reader.IsDBNull(4).Should().Be(isNull);
        reader.Read().Should().BeFalse();
    }

    private static void BindAhtolaParameters(AhtolaCommand command, long numbered, string named, string positional)
    {
        command.Parameters.Add(new AhtolaParameter("?1", numbered));
        command.Parameters.Add(new AhtolaParameter("$name", named));
        command.Parameters.Add(new AhtolaParameter(positional));
    }

    private static void AssertAhtolaValues(AhtolaCommand command, long numbered, string named, string positional)
    {
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        reader.GetInt64(0).Should().Be(numbered);
        reader.GetString(1).Should().Be(named);
        reader.GetString(2).Should().Be(positional);
        reader.Read().Should().BeFalse();
    }

    private static object? GetPrivateField(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Expected {instance.GetType().Name}.{fieldName}.");
        return field.GetValue(instance);
    }
}
