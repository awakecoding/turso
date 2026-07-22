using AwesomeAssertions;
using Turso;
using Turso.Data.Sqlite;
using Turso.Raw.Public;
using Turso.Raw.Public.Value;

namespace Turso.Tests;

public class ManagedRawBindingTests
{
    [Test]
    public void ManagedRawHandlesPrepareBindAndReadWithoutNativeInterop()
    {
        using var database = TursoBindings.OpenManagedDatabase(":memory:");
        using (var create = TursoBindings.PrepareStatement(database, "CREATE TABLE values_table(id INTEGER, name TEXT);"))
        {
            TursoBindings.Read(create).Should().BeFalse();
        }

        using (var insert = TursoBindings.PrepareStatement(database, "INSERT INTO values_table VALUES (?, :name);"))
        {
            TursoBindings.GetParameterCount(insert).Should().Be(2);
            TursoBindings.BindParameter(insert, 1, TursoValue.Int(7));
            TursoBindings.BindNamedParameter(insert, ":name", TursoValue.String("Ada")).Should().Be(2);
            TursoBindings.Read(insert).Should().BeFalse();
            TursoBindings.RowsAffected(insert).Should().Be(1);
        }

        using var select = TursoBindings.PrepareStatement(database, "SELECT id, name FROM values_table;");
        TursoBindings.GetFieldCount(select).Should().Be(2);
        TursoBindings.GetName(select, 0).Should().Be("id");
        TursoBindings.HasRows(select).Should().BeTrue();
        TursoBindings.Read(select).Should().BeTrue();
        TursoBindings.GetValue(select, 0).IntValue.Should().Be(7);
        TursoBindings.GetValue(select, 1).StringValue.Should().Be("Ada");
        TursoBindings.Read(select).Should().BeFalse();
    }

    [Test]
    public void ManagedRawMetadataDoesNotExecuteUnboundStatements()
    {
        using var database = TursoBindings.OpenManagedDatabase(":memory:");
        using var statement = TursoBindings.PrepareStatement(database, "SELECT ? AS value;");

        TursoBindings.GetFieldCount(statement).Should().Be(1);
        TursoBindings.GetName(statement, 0).Should().Be("value");
        TursoBindings.BindParameter(statement, 1, TursoValue.Int(7));
        TursoBindings.Read(statement).Should().BeTrue();
        TursoBindings.GetValue(statement, 0).IntValue.Should().Be(7);
    }

    [Test]
    public void ManagedRawScalarFunctionsUseManagedValuesAndCanBeRemoved()
    {
        using var database = TursoBindings.OpenManagedDatabase(":memory:");
        TursoBindings.RegisterManagedScalarFunction(
            database,
            "double_value",
            1,
            arguments => TursoValue.Int(arguments[0].IntValue * 2));

        using (var select = TursoBindings.PrepareStatement(database, "SELECT double_value(3);"))
        {
            TursoBindings.Read(select).Should().BeTrue();
            TursoBindings.GetValue(select, 0).IntValue.Should().Be(6);
        }

        TursoBindings.UnregisterFunction(database, "double_value");
        Assert.Throws<TursoException>(() =>
        {
            using var missing = TursoBindings.PrepareStatement(database, "SELECT double_value(3);");
            TursoBindings.Read(missing);
        });
    }

    [Test]
    public void ProviderCanExplicitlySelectTheManagedEngineForTests()
    {
        using var connection = new TursoConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE values_table(value INTEGER);");

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO values_table VALUES (?);";
            insert.Parameters.Add(new TursoParameter { Value = 7 });
            insert.ExecuteNonQuery().Should().Be(1);
        }

        using var select = connection.CreateCommand();
        select.CommandText = "SELECT value FROM values_table;";
        select.ExecuteScalar().Should().Be(7L);
    }

    [Test]
    public void ProviderTransactionsUseManagedConnectionState()
    {
        using var connection = new TursoConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE values_table(value INTEGER);");

        using (var transaction = connection.BeginTransaction())
        {
            connection.ExecuteNonQuery("INSERT INTO values_table VALUES (1);");
            transaction.Rollback();
        }

        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM values_table;";
        count.ExecuteScalar().Should().Be(0L);
    }

    [Test]
    public void SqliteFacadeFunctionsRunThroughManagedRegistrations()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.CreateFunction<long, long>("double_value", static value => value * 2);
        connection.CreateFunction<long>(
            "sum_values",
            static values => values.Sum(static value => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)));
        connection.Open();

        using var select = connection.CreateCommand();
        select.CommandText = "SELECT double_value(3);";
        select.ExecuteScalar().Should().Be(6L);
        select.CommandText = "SELECT sum_values(3, 4);";
        select.ExecuteScalar().Should().Be(7L);

        connection.CreateFunction<long, long>("double_value", default);
        select.CommandText = "SELECT double_value(3);";
        Assert.Throws<SqliteException>(() => select.ExecuteScalar());
    }

    [Test]
    public void SqliteFacadeAggregatesAndCollationsRunThroughManagedRegistrations()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.CreateAggregate<string, string>("concat_values", string.Empty, static (accumulator, value) => accumulator + value);
        connection.CreateCollation("reverse_text", static (left, right) => -string.CompareOrdinal(left, right));
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE values_table(value TEXT);");
        connection.ExecuteNonQuery("INSERT INTO values_table VALUES ('a'), ('b');");

        connection.ExecuteScalar<string>("SELECT concat_values(value) FROM values_table;").Should().Be("ab");
        connection.ExecuteScalar<long>("SELECT 'a' > 'b' COLLATE reverse_text;").Should().Be(1);
        connection.ExecuteScalar<string>("SELECT value FROM values_table ORDER BY value COLLATE reverse_text LIMIT 1;")
            .Should().Be("b");

        connection.CreateAggregate<string, string>("concat_values", string.Empty, default);
        connection.CreateCollation<string>("reverse_text", string.Empty, null);

        Assert.Throws<SqliteException>(() => connection.ExecuteScalar("SELECT concat_values(value) FROM values_table;"));
        Assert.Throws<SqliteException>(() => connection.ExecuteScalar("SELECT 'a' = 'A' COLLATE reverse_text;"));
    }

    [Test]
    public void SqliteFacadeManagedDiagnosticsMatchNativeCallbackErrors()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        var missingTable = Assert.Throws<SqliteException>(() => connection.ExecuteScalar("SELECT value FROM missing_table;"))!;
        missingTable.SqliteErrorCode.Should().Be(1);
        missingTable.Message.Should().Be(Turso.Data.Sqlite.Properties.Resources.SqliteNativeError(1, "no such table: missing_table"));

        connection.CreateFunction<long>("scalar_failure", () => throw new SqliteException("Scalar failed", 200));
        var scalarFailure = Assert.Throws<SqliteException>(() => connection.ExecuteScalar<long>("SELECT scalar_failure();"))!;
        scalarFailure.SqliteErrorCode.Should().Be(200);
        scalarFailure.Message.Should().Be(Turso.Data.Sqlite.Properties.Resources.SqliteNativeError(200, "Scalar failed"));

        connection.CreateFunction("null_value", (long value) => value);
        var nullValue = Assert.Throws<SqliteException>(() => connection.ExecuteScalar<long>("SELECT null_value(NULL);"))!;
        nullValue.SqliteErrorCode.Should().Be(1);
        nullValue.Message.Should().Be(Turso.Data.Sqlite.Properties.Resources.SqliteNativeError(
            1,
            Turso.Data.Sqlite.Properties.Resources.UDFCalledWithNull("null_value", 0)));

        connection.ExecuteNonQuery("CREATE TABLE Data(Value TEXT); INSERT INTO Data VALUES ('X');");
        connection.CreateAggregate("aggregate_failure", "seed", (string _) => throw new SqliteException("Aggregate failed", 201));
        var aggregateFailure = Assert.Throws<SqliteException>(() => connection.ExecuteScalar<string>("SELECT aggregate_failure() FROM Data;"))!;
        aggregateFailure.SqliteErrorCode.Should().Be(201);
        aggregateFailure.Message.Should().Be(Turso.Data.Sqlite.Properties.Resources.SqliteNativeError(201, "Aggregate failed"));

        connection.CreateCollation("collation_failure", static (_, _) => throw new SqliteException("Collation failed", 202));
        var collationFailure = Assert.Throws<SqliteException>(() => connection.ExecuteScalar<long>("SELECT 'a' = 'b' COLLATE collation_failure;"))!;
        collationFailure.SqliteErrorCode.Should().Be(202);
        collationFailure.Message.Should().Be(Turso.Data.Sqlite.Properties.Resources.SqliteNativeError(202, "Collation failed"));

        connection.ExecuteScalar<long>("SELECT 1;").Should().Be(1);
    }

    [Test]
    public void SqliteFacadeManagedCallbacksMapUnexpectedExceptionsToSqliteErrors()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE Data(Value TEXT); INSERT INTO Data VALUES ('X');");
        connection.CreateFunction<long>("scalar_unexpected", () => throw new InvalidOperationException("Scalar unexpected"));
        connection.CreateAggregate("aggregate_unexpected", "seed", (string _) => throw new InvalidOperationException("Aggregate unexpected"));
        connection.CreateCollation("collation_unexpected", static (_, _) => throw new InvalidOperationException("Collation unexpected"));

        Assert.Throws<SqliteException>(() => connection.ExecuteScalar<long>("SELECT scalar_unexpected();"))!
            .SqliteErrorCode.Should().Be(1);
        Assert.Throws<SqliteException>(() => connection.ExecuteScalar<string>("SELECT aggregate_unexpected() FROM Data;"))!
            .SqliteErrorCode.Should().Be(1);
        Assert.Throws<SqliteException>(() => connection.ExecuteScalar<long>(
            "SELECT 'a' = 'b' COLLATE collation_unexpected;"))!
            .SqliteErrorCode.Should().Be(1);
    }

    [Test]
    public void SqliteFacadeSchemaDiscoveryUsesManagedCatalogAndPragmas()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE users(id INTEGER PRIMARY KEY, name TEXT DEFAULT 'unknown');");

        var tables = connection.GetSchema("Tables");
        tables.Rows.Cast<System.Data.DataRow>()
            .Should().ContainSingle(row => (string)row["TABLE_NAME"] == "users");

        var columns = connection.GetSchema("Columns");
        columns.Rows.Cast<System.Data.DataRow>().Should().Contain(row =>
            (string)row["TABLE_NAME"] == "users"
            && (string)row["COLUMN_NAME"] == "name"
            && (string)row["DATA_TYPE"] == "TEXT");
    }

    [Test]
    public void SqliteFacadeSurfacesManagedIndexesThroughCatalogAndPragmas()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE items(id INTEGER, code TEXT);");
        connection.ExecuteNonQuery("CREATE UNIQUE INDEX items_code ON items(code);");

        // The index must not be reported as a table.
        var tables = connection.GetSchema("Tables");
        tables.Rows.Cast<System.Data.DataRow>()
            .Should().ContainSingle(row => (string)row["TABLE_NAME"] == "items");

        // The index is discoverable through the managed catalog.
        connection.ExecuteScalar<string>(
            "SELECT tbl_name FROM sqlite_master WHERE type = 'index' AND name = 'items_code';")
            .Should().Be("items");

        // PRAGMA index_list / index_info flow through the managed engine via facade commands.
        using (var listCommand = connection.CreateCommand())
        {
            listCommand.CommandText = "PRAGMA index_list(items);";
            using var reader = listCommand.ExecuteReader();
            reader.Read().Should().BeTrue();
            reader.GetString(1).Should().Be("items_code");
            reader.GetInt64(2).Should().Be(1);
            reader.Read().Should().BeFalse();
        }

        using (var infoCommand = connection.CreateCommand())
        {
            infoCommand.CommandText = "PRAGMA index_info(items_code);";
            using var infoReader = infoCommand.ExecuteReader();
            infoReader.Read().Should().BeTrue();
            infoReader.GetString(2).Should().Be("code");
        }

        // The UNIQUE index is enforced through the facade.
        connection.ExecuteNonQuery("INSERT INTO items VALUES (1, 'a');");
        Assert.Throws<SqliteException>(() => connection.ExecuteNonQuery("INSERT INTO items VALUES (2, 'a');"));
    }
}
