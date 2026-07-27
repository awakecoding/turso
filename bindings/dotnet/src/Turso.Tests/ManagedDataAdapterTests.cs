using System.Data;
using System.Data.Common;
using AwesomeAssertions;
using Turso.Data.Sqlite;

namespace Turso.Tests;

/// <summary>
/// Covers the disconnected ADO.NET surface: <see cref="TursoDataAdapter"/>,
/// <see cref="TursoCommandBuilder"/> and the reader schema tables they depend on. The round
/// trips assert against the database after <c>Update</c> rather than against the
/// <see cref="DataSet"/>, because an adapter that reports success without persisting the row
/// is worse than no adapter at all.
/// </summary>
public sealed class ManagedDataAdapterTests
{
    private const string SeedScript = """
        CREATE TABLE person(id INTEGER PRIMARY KEY, name TEXT NOT NULL, score INTEGER);
        INSERT INTO person(id, name, score) VALUES (1, 'ada', 10), (2, 'grace', 20), (3, 'alan', 30);
        """;

    private static readonly string[] SeedStatements = SeedScript.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Test]
    public void ManagedTursoConnectionRoundTripsADataSetThroughTheAdapter()
    {
        using var connection = OpenManagedTurso();
        RoundTrip(connection);
    }

    [Test]
    public void ManagedSqliteFacadeRoundTripsADataSetThroughTheAdapter()
    {
        using var connection = OpenManagedFacade();
        RoundTrip(connection);
    }

    [Test]
    public void NativeTursoConnectionRoundTripsADataSetThroughTheAdapter()
    {
        NativeCompanionAvailability.RequireSdkKit();
        using var connection = OpenNativeTurso();
        RoundTrip(connection);
    }

    [Test]
    public void AdapterFillProjectsEveryRowAndColumn()
    {
        using var connection = OpenManagedTurso();
        using var adapter = new TursoDataAdapter("SELECT id, name, score FROM person ORDER BY id", connection);

        var dataSet = new DataSet();
        adapter.Fill(dataSet, "person").Should().Be(3);

        var table = dataSet.Tables["person"]!;
        table.Columns.Cast<DataColumn>().Select(column => column.ColumnName)
            .Should().Equal("id", "name", "score");
        table.Columns.Cast<DataColumn>().Select(column => column.DataType)
            .Should().Equal(typeof(long), typeof(string), typeof(long));
        table.Rows.Cast<DataRow>().Select(row => (string)row["name"])
            .Should().Equal("ada", "grace", "alan");
    }

    [Test]
    public void CommandBuilderGeneratesQuotedSingleTableStatements()
    {
        using var connection = OpenManagedTurso();
        using var adapter = new TursoDataAdapter("SELECT id, name, score FROM person", connection);
        using var builder = new TursoCommandBuilder(adapter);

        builder.GetInsertCommand().CommandText
            .Should().Be("INSERT INTO \"person\" (\"id\", \"name\", \"score\") VALUES (@p1, @p2, @p3)");
        builder.GetDeleteCommand().CommandText.Should().StartWith("DELETE FROM \"person\" WHERE ");
        builder.GetUpdateCommand().CommandText.Should().StartWith("UPDATE \"person\" SET ");
    }

    [Test]
    public void CommandBuilderRestoresParameterSizeFromTheSchemaTable()
    {
        // DbCommandBuilder assigns Size = 0 before ApplyParameterInfo runs. A provider that
        // honours Size would then bind "" for every string, so the builder has to put the
        // schema table's ColumnSize back.
        using var connection = OpenManagedTurso();
        using var adapter = new TursoDataAdapter("SELECT id, name, score FROM person", connection);
        using var builder = new TursoCommandBuilder(adapter);

        builder.GetInsertCommand().Parameters.Cast<DbParameter>().Select(parameter => parameter.Size)
            .Should().AllSatisfy(size => size.Should().NotBe(0));
    }

    [Test]
    public void CommandBuilderBindsOriginalValuesInTheConcurrencyPredicate()
    {
        // DbParameter.SourceVersion has a no-op base setter. Without the provider override the
        // builder's WHERE clause would read Current values and never match, so every update
        // would fail with DBConcurrencyException.
        using var connection = OpenManagedTurso();
        using var adapter = new TursoDataAdapter("SELECT id, name, score FROM person", connection);
        using var builder = new TursoCommandBuilder(adapter);

        builder.GetUpdateCommand().Parameters.Cast<DbParameter>()
            .Where(parameter => parameter.SourceVersion == DataRowVersion.Original)
            .Should().NotBeEmpty();
    }

    [Test]
    public void TursoParameterRoundTripsSourceVersion()
    {
        var parameter = new TursoParameter { SourceVersion = DataRowVersion.Original };
        parameter.SourceVersion.Should().Be(DataRowVersion.Original);
    }

    [Test]
    public void SqliteParameterRoundTripsSourceVersion()
    {
        var parameter = new SqliteParameter { SourceVersion = DataRowVersion.Original };
        parameter.SourceVersion.Should().Be(DataRowVersion.Original);
    }

    [Test]
    public void TursoDataReaderPublishesAKeyAwareSchemaTable()
    {
        using var connection = OpenManagedTurso();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, score FROM person";
        using var reader = command.ExecuteReader(CommandBehavior.KeyInfo);

        var schema = reader.GetSchemaTable()!;
        schema.Rows.Cast<DataRow>().Select(row => (string)row[SchemaTableColumn.ColumnName])
            .Should().Equal("id", "name", "score");
        schema.Rows.Cast<DataRow>().Select(row => row[SchemaTableColumn.IsKey])
            .Should().Equal(true, false, false);
        schema.Rows.Cast<DataRow>().Select(row => row[SchemaTableColumn.AllowDBNull])
            .Should().Equal(true, false, true);
        schema.Rows.Cast<DataRow>().Select(row => (string)row[SchemaTableColumn.BaseTableName])
            .Should().AllBe("person");
    }

    [Test]
    public void AdapterFillSchemaProjectsColumnMetadataButNotARowidPrimaryKey()
    {
        // Verified against Microsoft.Data.Sqlite: for `id INTEGER PRIMARY KEY` it also publishes
        // IsKey=True/IsUnique=False/AllowDBNull=True, and System.Data's SchemaMapping declines to
        // promote that to a DataTable primary key. Matching the oracle matters more than inventing
        // uniqueness metadata SQLite does not expose for a rowid alias.
        using var connection = OpenManagedTurso();
        using var adapter = new TursoDataAdapter("SELECT id, name, score FROM person", connection);

        var dataSet = new DataSet();
        var tables = adapter.FillSchema(dataSet, SchemaType.Source, "person");

        tables.Should().HaveCount(1);
        tables[0].Columns.Cast<DataColumn>().Select(column => column.ColumnName)
            .Should().Equal("id", "name", "score");
        tables[0].Columns["name"]!.AllowDBNull.Should().BeFalse();
        tables[0].Rows.Count.Should().Be(0);
        tables[0].PrimaryKey.Should().BeEmpty();
    }

    [Test]
    public void AdapterUpdatesWithoutAnInferredPrimaryKeyBecauseTheBuilderUsesIsKeyDirectly()
    {
        using var connection = OpenManagedTurso();
        using var adapter = new TursoDataAdapter("SELECT id, name, score FROM person", connection)
        {
            MissingSchemaAction = MissingSchemaAction.AddWithKey,
        };
        using var builder = new TursoCommandBuilder(adapter);

        builder.GetUpdateCommand().CommandText.Should().Contain("\"id\" = @");

        var dataSet = new DataSet();
        adapter.Fill(dataSet, "person");
        dataSet.Tables["person"]!.Rows[0]["score"] = 99L;
        adapter.Update(dataSet, "person").Should().Be(1);

        Read(connection, "SELECT score FROM person WHERE id = 1").Should().Equal("99");
    }

    [Test]
    public void FacadeReaderPublishesDeclaredTypesBeforeTheFirstRead()
    {
        // DbDataAdapter maps the result schema before it fetches a row, so GetFieldType has to
        // answer from the declared type instead of throwing. Microsoft.Data.Sqlite is the oracle:
        // it reports Int64/String/Int64 and INTEGER/TEXT/INTEGER here.
        using var connection = OpenManagedFacade();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, score FROM person";
        using var reader = command.ExecuteReader();

        Enumerable.Range(0, reader.FieldCount).Select(reader.GetFieldType)
            .Should().Equal(typeof(long), typeof(string), typeof(long));
        Enumerable.Range(0, reader.FieldCount).Select(reader.GetDataTypeName)
            .Should().Equal("INTEGER", "TEXT", "INTEGER");
    }

    [Test]
    public void BothFactoriesPublishTheDisconnectedSurface()
    {
        TursoFactory.Instance.CanCreateDataAdapter.Should().BeTrue();
        TursoFactory.Instance.CanCreateCommandBuilder.Should().BeTrue();
        TursoFactory.Instance.CreateDataAdapter().Should().BeOfType<TursoDataAdapter>();
        TursoFactory.Instance.CreateCommandBuilder().Should().BeOfType<TursoCommandBuilder>();

        SqliteFactory.Instance.CanCreateDataAdapter.Should().BeTrue();
        SqliteFactory.Instance.CanCreateCommandBuilder.Should().BeTrue();
        SqliteFactory.Instance.CreateDataAdapter().Should().BeOfType<TursoDataAdapter>();
        SqliteFactory.Instance.CreateCommandBuilder().Should().BeOfType<TursoCommandBuilder>();
    }

    [Test]
    public void AdapterRaisesRowUpdatingAndRowUpdatedForEveryChangedRow()
    {
        using var connection = OpenManagedTurso();
        using var adapter = new TursoDataAdapter("SELECT id, name, score FROM person ORDER BY id", connection);
        using var builder = new TursoCommandBuilder(adapter);

        var updating = new List<StatementType>();
        var updated = new List<StatementType>();
        adapter.RowUpdating += (_, args) => updating.Add(args.StatementType);
        adapter.RowUpdated += (_, args) => updated.Add(args.StatementType);

        var dataSet = new DataSet();
        adapter.Fill(dataSet, "person");
        var table = dataSet.Tables["person"]!;
        table.Rows[0]["score"] = 11L;
        table.Rows[1].Delete();
        table.Rows.Add(4L, "linus", 40L);
        adapter.Update(dataSet, "person");

        updating.Should().BeEquivalentTo(
            new[] { StatementType.Update, StatementType.Delete, StatementType.Insert },
            options => options.WithoutStrictOrdering());
        updated.Should().BeEquivalentTo(updating);
    }

    private static void RoundTrip(DbConnection connection)
    {
        using var adapter = new TursoDataAdapter("SELECT id, name, score FROM person ORDER BY id", connection);
        using var builder = new TursoCommandBuilder(adapter);

        var dataSet = new DataSet();
        adapter.Fill(dataSet, "person").Should().Be(3);

        var table = dataSet.Tables["person"]!;
        table.Rows[0]["score"] = 11L;
        table.Rows[1].Delete();
        table.Rows.Add(4L, "linus", 40L);

        adapter.Update(dataSet, "person").Should().Be(3);
        table.GetChanges().Should().BeNull();

        Read(connection, "SELECT id, name, score FROM person ORDER BY id")
            .Should().Equal("1|ada|11", "3|alan|30", "4|linus|40");

        // A second round trip proves the adapter left the dataset in a state that can be
        // filled and updated again, not just that the first write happened to work.
        table.Rows[0]["name"] = "ada lovelace";
        adapter.Update(dataSet, "person").Should().Be(1);
        Read(connection, "SELECT name FROM person WHERE id = 1").Should().Equal("ada lovelace");
    }

    private static List<string> Read(DbConnection connection, string sql)
    {
        var rows = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var values = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                values[i] = reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!;

            rows.Add(string.Join('|', values));
        }

        return rows;
    }

    private static TursoConnection OpenManagedTurso()
    {
        var connection = new TursoConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        Seed(connection);
        return connection;
    }

    private static TursoConnection OpenNativeTurso()
    {
        var connection = new TursoConnection("Data Source=:memory:;Local Provider=Native");
        connection.Open();
        Seed(connection);
        return connection;
    }

    private static SqliteConnection OpenManagedFacade()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        Seed(connection);
        return connection;
    }

    private static void Seed(DbConnection connection)
    {
        foreach (var statement in SeedStatements)
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }
    }
}
