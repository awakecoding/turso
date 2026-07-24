using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Turso.Core;

namespace Turso.Tests;

[NonParallelizable]
public sealed class ManagedConstraintSemanticsTests
{
    [Test]
    public void CheckAndTableUniqueConstraintsMatchSqliteAndKeepStatementsAtomic()
    {
        string[] setup =
        [
            """
            CREATE TABLE items(
                id INTEGER PRIMARY KEY,
                quantity INTEGER CHECK (quantity > 0),
                limit_value INTEGER,
                CONSTRAINT within_limit CHECK (quantity <= limit_value),
                CONSTRAINT item_quantity UNIQUE (id, quantity)
            );
            """,
            "INSERT INTO items VALUES (1, 2, 5), (2, 3, 6);",
        ];

        AssertErrorMatchesSqlite(setup, "INSERT INTO items VALUES (3, 1, 5), (4, -1, 5);");
        AssertQueryMatchesSqlite(setup, "SELECT id, quantity, limit_value FROM items ORDER BY id;");

        AssertErrorMatchesSqlite(setup, "UPDATE items SET quantity = limit_value + 1;");
        AssertQueryMatchesSqlite(setup, "SELECT id, quantity, limit_value FROM items ORDER BY id;");

        AssertErrorMatchesSqlite(setup, "INSERT INTO items VALUES (1, 2, 9);");
    }

    [Test]
    public void NullChecksAndConstraintConflictClausesMatchSqlite()
    {
        AssertQueryMatchesSqlite(
            [
                """
                CREATE TABLE entries(
                    id INTEGER PRIMARY KEY,
                    code TEXT UNIQUE ON CONFLICT IGNORE,
                    required INTEGER NOT NULL ON CONFLICT REPLACE DEFAULT (2 + 3),
                    value INTEGER CHECK (value > 0)
                );
                """,
                "INSERT INTO entries VALUES (1, 'a', NULL, NULL);",
                "INSERT INTO entries VALUES (2, 'a', 9, 1);",
                "INSERT OR IGNORE INTO entries VALUES (3, 'b', 9, -1);",
            ],
            "SELECT id, code, required, value FROM entries ORDER BY id;");
    }

    [Test]
    public void ExpressionDefaultsDeclaredTypesChecksAndTableUniqueRoundTripThroughFileCatalog()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(
                    connection,
                    """
                    CREATE TABLE metrics(
                        id INTEGER PRIMARY KEY,
                        amount DOUBLE PRECISION DEFAULT (abs(-4) + 1),
                        label CHARACTER VARYING(20) DEFAULT (upper('x')),
                        created TEXT DEFAULT CURRENT_TIMESTAMP,
                        CONSTRAINT positive CHECK (amount > 0),
                        CONSTRAINT metric_value UNIQUE (label, amount) ON CONFLICT IGNORE
                    );
                    """);
                Execute(connection, "INSERT INTO metrics(id) VALUES (1);");
                Execute(connection, "INSERT INTO metrics(id) VALUES (2);");
                ScalarInteger(connection, "SELECT COUNT(*) FROM metrics;").Should().Be(1);
            }

            using (var sqlite = new MsData.SqliteConnection($"Data Source={path}"))
            {
                sqlite.Open();
                ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
                ScalarInteger(
                    sqlite,
                    "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'sqlite_autoindex_metrics_1' AND sql IS NULL;")
                    .Should().Be(1);
                var schemaSql = ScalarText(sqlite, "SELECT sql FROM sqlite_schema WHERE name = 'metrics';");
                schemaSql.Should().Contain("DOUBLE PRECISION")
                    .And.Contain("CHARACTER VARYING(20)")
                    .And.Contain("DEFAULT (abs(-4) + 1)")
                    .And.Contain("CONSTRAINT \"positive\" CHECK (amount > 0)")
                    .And.Contain("UNIQUE (\"label\", \"amount\") ON CONFLICT IGNORE");
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                var tableInfo = ReadRows(connection, "PRAGMA table_info(metrics);");
                tableInfo[1][2].Should().Be(SqlValue.Text("DOUBLE PRECISION"));
                tableInfo[1][4].Should().Be(SqlValue.Text("abs(-4) + 1"));
                tableInfo[2][2].Should().Be(SqlValue.Text("CHARACTER VARYING(20)"));

                Execute(connection, "INSERT INTO metrics(id, label) VALUES (2, 'Y');");
                Action invalidUpdate = () => Execute(connection, "UPDATE metrics SET amount = -1;");
                invalidUpdate.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("CHECK constraint failed: positive");

                ReadRows(connection, "SELECT id, amount, label, typeof(amount) FROM metrics ORDER BY id;")
                    .Should().BeEquivalentTo(
                    [
                        new[] { SqlValue.Integer(1), SqlValue.Real(5), SqlValue.Text("X"), SqlValue.Text("real") },
                        new[] { SqlValue.Integer(2), SqlValue.Real(5), SqlValue.Text("Y"), SqlValue.Text("real") },
                    ],
                    options => options.WithStrictOrdering());
            }
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ExplicitNullAndDateTimeDefaultsMatchSqlite()
    {
        AssertQueryMatchesSqlite(
            [
                "CREATE TABLE values_table(a TEXT NULL, b NULL, created TEXT DEFAULT (datetime('now')));",
                "INSERT INTO values_table(a) VALUES ('x');",
            ],
            "SELECT type, name, tbl_name FROM sqlite_master WHERE type = 'table';");

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(
            connection,
            "CREATE TABLE values_table(a TEXT NULL, b NULL, created TEXT DEFAULT (datetime('now')));");
        Execute(connection, "INSERT INTO values_table(a) VALUES ('x');");
        var info = ReadRows(connection, "PRAGMA table_info(values_table);");
        info[0][2].Should().Be(SqlValue.Text("TEXT"));
        info[1][2].Should().Be(SqlValue.Text(string.Empty));
        ScalarInteger(connection, "SELECT created IS NOT NULL FROM values_table;").Should().Be(1);
    }

    [Test]
    public void AlterAddColumnValidatesChecksAndAcceptsSignedLiteralDefaults()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(id INTEGER);");
        Execute(connection, "INSERT INTO values_table VALUES (1);");

        Action invalid = () => Execute(
            connection,
            "ALTER TABLE values_table ADD COLUMN invalid INTEGER DEFAULT -1 CHECK (invalid > 0);");
        invalid.Should().Throw<EmbeddedSqlException>().WithMessage("CHECK constraint failed: invalid > 0");

        Execute(connection, "ALTER TABLE values_table ADD COLUMN valid INTEGER DEFAULT -1;");
        ScalarInteger(connection, "SELECT valid FROM values_table;").Should().Be(-1);
    }

    [Test]
    public void UniqueUpdatesUseSqliteRowwiseConflictOrder()
    {
        string[] setup =
        [
            "CREATE TABLE values_table(value INTEGER UNIQUE);",
            "INSERT INTO values_table VALUES (1), (2);",
        ];

        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        foreach (var sql in setup)
            Execute(managed, sql);
        Action managedSwap = () => Execute(managed, "UPDATE values_table SET value = 3 - value;");
        managedSwap.Should().Throw<EmbeddedSqlException>()
            .WithMessage("UNIQUE constraint failed: values_table.value");
        ReadRows(managed, "SELECT value FROM values_table ORDER BY value;")
            .Select(row => row[0].AsInteger())
            .Should().Equal(1, 2);

        Execute(managed, "CREATE TABLE configured(value INTEGER UNIQUE ON CONFLICT IGNORE);");
        Execute(managed, "INSERT INTO configured VALUES (1), (2);");
        Execute(managed, "UPDATE configured SET value = 1;");
        ScalarInteger(managed, "SELECT COUNT(*) FROM configured;").Should().Be(2);
    }

    [Test]
    public void RenameRejectsSchemasThatRequireConstraintExpressionRewriting()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(a INTEGER, b INTEGER, UNIQUE(a, b), CHECK(a < b));");

        Action rename = () => Execute(connection, "ALTER TABLE values_table RENAME COLUMN a TO first;");
        rename.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*schema token rewriting*");
        Execute(connection, "INSERT INTO values_table VALUES (1, 2);");
    }

    private static void AssertQueryMatchesSqlite(IReadOnlyList<string> setup, string query)
    {
        var managed = RunManaged(setup, query);
        var sqlite = RunSqlite(setup, query);
        managed.Should().BeEquivalentTo(sqlite, options => options.WithStrictOrdering());
    }

    private static void AssertErrorMatchesSqlite(IReadOnlyList<string> setup, string command)
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var sql in setup)
            Execute(managed, sql);

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var sql in setup)
            Execute(sqlite, sql);

        var managedError = Assert.Catch<EmbeddedSqlException>(() => Execute(managed, command))!;
        var sqliteError = Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, command))!;
        sqliteError.Message.Should().Contain(managedError.Message);

        ReadRows(managed, "SELECT id, quantity, limit_value FROM items ORDER BY id;")
            .Select(row => row.Select(ToObject).ToArray())
            .Should().BeEquivalentTo(
                ReadRows(sqlite, "SELECT id, quantity, limit_value FROM items ORDER BY id;"),
                options => options.WithStrictOrdering());
    }

    private static IReadOnlyList<object?[]> RunManaged(IReadOnlyList<string> setup, string query)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        foreach (var sql in setup)
            Execute(connection, sql);
        return ReadRows(connection, query).Select(row => row.Select(ToObject).ToArray()).ToArray();
    }

    private static IReadOnlyList<object?[]> RunSqlite(IReadOnlyList<string> setup, string query)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var sql in setup)
            Execute(connection, sql);
        return ReadRows(connection, query);
    }

    private static object? ToObject(SqlValue value)
        => value.Kind switch
        {
            SqlValueKind.Null => null,
            SqlValueKind.Integer => value.AsInteger(),
            SqlValueKind.Real => value.AsReal(),
            SqlValueKind.Text => value.AsText(),
            SqlValueKind.Blob => value.AsBlob().ToArray(),
            _ => throw new InvalidOperationException($"Unknown SQL value kind {value.Kind}."),
        };

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static void Execute(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var row = new SqlValue[statement.ColumnCount];
            for (var index = 0; index < row.Length; index++)
                row[index] = statement.GetValue(index);
            rows.Add(row);
        }
        return rows;
    }

    private static IReadOnlyList<object?[]> ReadRows(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var index = 0; index < row.Length; index++)
                row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            rows.Add(row);
        }
        return rows;
    }

    private static long ScalarInteger(EmbeddedConnection connection, string sql)
        => ReadRows(connection, sql).Single().Single().AsInteger();

    private static long ScalarInteger(MsData.SqliteConnection connection, string sql)
        => Convert.ToInt64(Scalar(connection, sql));

    private static string ScalarText(MsData.SqliteConnection connection, string sql)
        => (string)Scalar(connection, sql)!;

    private static object? Scalar(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "managed-constraint-semantics");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}.db");
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
