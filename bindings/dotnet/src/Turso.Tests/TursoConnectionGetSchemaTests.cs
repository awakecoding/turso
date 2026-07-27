using System.Data;
using System.Data.Common;
using AwesomeAssertions;
using Turso.Data.Sqlite;

namespace Turso.Tests;

/// <summary>
/// <see cref="TursoConnection"/> and the <see cref="SqliteConnection"/> facade now share one
/// schema-collection implementation, so these tests assert the two surfaces answer identically
/// rather than asserting each in isolation. Both read the catalog with ordinary SQL on the owning
/// connection, which is what lets a remote or replica connection describe the database it is
/// attached to instead of reporting an empty catalog.
/// </summary>
public sealed class TursoConnectionGetSchemaTests
{
    private static readonly string[] Collections =
    [
        "MetaDataCollections",
        "ReservedWords",
        "Tables",
        "Columns",
        "Indexes",
        "IndexColumns",
    ];

    private static readonly string[] SeedStatements =
    [
        "CREATE TABLE products(id INTEGER PRIMARY KEY, name TEXT NOT NULL, price REAL)",
        "CREATE TABLE orders(id INTEGER PRIMARY KEY, product_id INTEGER)",
        "CREATE VIEW product_names AS SELECT name FROM products",
        "CREATE UNIQUE INDEX ix_products_name ON products(name)",
        "CREATE INDEX ix_orders_product ON orders(product_id)",
    ];

    [Test]
    [TestCaseSource(nameof(Collections))]
    public void TursoConnectionAndTheFacadeReturnTheSameCollection(string collectionName)
    {
        using var turso = OpenTurso();
        using var facade = OpenFacade();

        Describe(turso.GetSchema(collectionName)).Should().Equal(Describe(facade.GetSchema(collectionName)));
    }

    [Test]
    public void TursoConnectionReportsUserTablesAndViews()
    {
        using var connection = OpenTurso();

        Column(connection.GetSchema("Tables"), "TABLE_NAME")
            .Should().Equal("orders", "product_names", "products");
        Column(connection.GetSchema("Tables", ["main", null, null, "VIEW"]), "TABLE_NAME")
            .Should().Equal("product_names");
        Column(connection.GetSchema("Tables", ["main", null, "products", null]), "TABLE_TYPE")
            .Should().Equal("BASE TABLE");
    }

    [Test]
    public void TursoConnectionReportsColumnsIndexesAndIndexColumns()
    {
        using var connection = OpenTurso();

        Column(connection.GetSchema("Columns", [null, null, "products", null]), "COLUMN_NAME")
            .Should().Equal("id", "name", "price");
        connection.GetSchema("Columns", [null, null, "products", "name"]).Rows[0]["IS_NULLABLE"]
            .Should().Be(false);
        connection.GetSchema("Columns", [null, null, "products", "price"]).Rows[0]["DATA_TYPE"]
            .Should().Be("REAL");

        Column(connection.GetSchema("Indexes", [null, null, "products", null]), "INDEX_NAME")
            .Should().Equal("ix_products_name");
        connection.GetSchema("Indexes", [null, null, "products", "ix_products_name"]).Rows[0]["IS_UNIQUE"]
            .Should().Be(true);
        Column(connection.GetSchema("IndexColumns", [null, null, "products", "ix_products_name", null]), "COLUMN_NAME")
            .Should().Equal("name");
    }

    [Test]
    public void TursoConnectionHonorsCatalogAndSchemaRestrictions()
    {
        using var connection = OpenTurso();

        connection.GetSchema("Tables", ["other", null, null, null]).Rows.Count.Should().Be(0);
        connection.GetSchema("Tables", [null, "dbo", null, null]).Rows.Count.Should().Be(0);
        connection.GetSchema("Columns", ["other", null, "products", "name"]).Rows.Count.Should().Be(0);
        connection.GetSchema("Indexes", ["other", null, "products", null]).Rows.Count.Should().Be(0);
        connection.GetSchema("IndexColumns", ["other", null, "products", null, null]).Rows.Count.Should().Be(0);
    }

    [Test]
    public void TursoConnectionDefaultsToMetaDataCollections()
    {
        using var connection = OpenTurso();

        connection.GetSchema().TableName.Should().Be("MetaDataCollections");
        Column(connection.GetSchema(), "CollectionName")
            .Should().Equal(Collections);
    }

    [Test]
    public void TursoConnectionRejectsAnUnknownCollectionWithTheSameMessageAsTheFacade()
    {
        using var turso = OpenTurso();
        using var facade = OpenFacade();

        var tursoError = Assert.Throws<ArgumentException>(() => turso.GetSchema("Procedures"));
        var facadeError = Assert.Throws<ArgumentException>(() => facade.GetSchema("Procedures"));

        tursoError!.Message.Should().Be("Unknown collection: Procedures.");
        tursoError.Message.Should().Be(facadeError!.Message);
    }

    [Test]
    public void TursoConnectionRejectsTooManyRestrictions()
    {
        using var connection = OpenTurso();

        Assert.Throws<ArgumentException>(() => connection.GetSchema("Tables", ["main", null, null, null, "extra"]))!
            .Message.Should().Be("Too many restrictions specified for collection Tables.");
    }

    [Test]
    public void TursoConnectionRejectsSchemaRequestsWhileClosed()
    {
        using var connection = new TursoConnection("Data Source=:memory:;Local Provider=Managed");

        Assert.Throws<InvalidOperationException>(() => connection.GetSchema("Tables"))!
            .Message.Should().Be("The connection is not open.");
    }

    [Test]
    public void ClosedConnectionsStillDescribeCollectionsThatNeedNoCatalog()
    {
        using var connection = new TursoConnection("Data Source=:memory:;Local Provider=Managed");

        connection.GetSchema("MetaDataCollections").Rows.Count.Should().Be(Collections.Length);
        connection.GetSchema("ReservedWords").Rows.Count.Should().BeGreaterThan(0);
    }

    private static List<string> Describe(DataTable table)
    {
        var lines = new List<string>
        {
            table.TableName,
            string.Join('|', table.Columns.Cast<DataColumn>().Select(column => column.ColumnName + ':' + column.DataType.Name)),
        };

        lines.AddRange(table.Rows.Cast<DataRow>().Select(row =>
            string.Join('|', row.ItemArray.Select(value => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)))));
        return lines;
    }

    private static List<string> Column(DataTable table, string columnName)
        => table.Rows.Cast<DataRow>()
            .Select(row => Convert.ToString(row[columnName], System.Globalization.CultureInfo.InvariantCulture)!)
            .ToList();

    private static TursoConnection OpenTurso()
    {
        var connection = new TursoConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        Seed(connection);
        return connection;
    }

    private static SqliteConnection OpenFacade()
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
