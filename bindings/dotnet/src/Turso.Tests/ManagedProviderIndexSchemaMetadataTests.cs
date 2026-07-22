using System.Data;
using AwesomeAssertions;
using Turso.Data.Sqlite;

namespace Turso.Tests;

public sealed class ManagedProviderIndexSchemaMetadataTests
{
    [Test]
    public void GetSchemaExposesManagedIndexMetadataAndColumns()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("""
            CREATE TABLE products(id INTEGER PRIMARY KEY, sku TEXT, region TEXT);
            CREATE UNIQUE INDEX ux_products_sku_region ON products(sku, region);
            CREATE INDEX ix_products_region ON products(region);
            """);

        var collections = connection.GetSchema();
        var indexesCollection = collections.Rows.Cast<DataRow>()
            .Single(row => (string)row["CollectionName"] == "Indexes");
        indexesCollection["NumberOfRestrictions"].Should().Be(4);
        indexesCollection["NumberOfIdentifierParts"].Should().Be(4);
        indexesCollection["NumberOfRestrictions"].GetType().Should().Be(typeof(int));

        var indexes = connection.GetSchema("Indexes");
        indexes.TableName.Should().Be("Indexes");
        indexes.Columns.Cast<DataColumn>().Select(column => (column.ColumnName, column.DataType))
            .Should().Equal(
                ("TABLE_CATALOG", typeof(string)),
                ("TABLE_SCHEMA", typeof(string)),
                ("TABLE_NAME", typeof(string)),
                ("INDEX_NAME", typeof(string)),
                ("IS_UNIQUE", typeof(bool)),
                ("ORIGIN", typeof(string)),
                ("IS_PARTIAL", typeof(bool)));

        var uniqueIndex = indexes.Rows.Cast<DataRow>()
            .Single(row => (string)row["INDEX_NAME"] == "ux_products_sku_region");
        uniqueIndex["TABLE_CATALOG"].Should().Be("main");
        uniqueIndex["TABLE_SCHEMA"].Should().Be(DBNull.Value);
        uniqueIndex["TABLE_NAME"].Should().Be("products");
        uniqueIndex["IS_UNIQUE"].Should().Be(true);
        uniqueIndex["IS_UNIQUE"].GetType().Should().Be(typeof(bool));
        uniqueIndex["ORIGIN"].Should().Be("c");
        uniqueIndex["IS_PARTIAL"].Should().Be(false);

        var indexColumns = connection.GetSchema("IndexColumns", [null, null, "products", "ux_products_sku_region"]);
        indexColumns.TableName.Should().Be("IndexColumns");
        indexColumns.Columns.Cast<DataColumn>().Select(column => (column.ColumnName, column.DataType))
            .Should().Equal(
                ("TABLE_CATALOG", typeof(string)),
                ("TABLE_SCHEMA", typeof(string)),
                ("TABLE_NAME", typeof(string)),
                ("INDEX_NAME", typeof(string)),
                ("ORDINAL_POSITION", typeof(int)),
                ("COLUMN_ORDINAL", typeof(int)),
                ("COLUMN_NAME", typeof(string)));
        indexColumns.Rows.Cast<DataRow>()
            .Select(row => ((int)row["ORDINAL_POSITION"], (int)row["COLUMN_ORDINAL"], (string)row["COLUMN_NAME"]))
            .Should().Equal((0, 1, "sku"), (1, 2, "region"));
    }

    [Test]
    public void GetSchemaFiltersManagedIndexMetadataAndRejectsExtraRestrictions()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("""
            CREATE TABLE products(id INTEGER PRIMARY KEY, sku TEXT, region TEXT);
            CREATE UNIQUE INDEX ux_products_sku_region ON products(sku, region);
            CREATE INDEX ix_products_region ON products(region);
            """);

        var filtered = connection.GetSchema("IndexColumns", [null, null, "PRODUCTS", "UX_PRODUCTS_SKU_REGION", "REGION"]);
        filtered.Rows.Cast<DataRow>().Should().ContainSingle();
        filtered.Rows[0]["COLUMN_NAME"].Should().Be("region");
        filtered.Rows[0]["ORDINAL_POSITION"].Should().Be(1);

        Assert.Throws<ArgumentException>(() => connection.GetSchema("Indexes", new string?[5]))!
            .Message.Should().Contain("Indexes");
        Assert.Throws<ArgumentException>(() => connection.GetSchema("IndexColumns", new string?[6]))!
            .Message.Should().Contain("IndexColumns");
    }
}
