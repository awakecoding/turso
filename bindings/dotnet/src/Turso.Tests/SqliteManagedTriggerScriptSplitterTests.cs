using AwesomeAssertions;
using Turso.Data.Sqlite;

namespace Turso.Tests;

public sealed class SqliteManagedTriggerScriptSplitterTests
{
    [Test]
    public void ManagedProviderExecutesMultiStatementTriggerScriptWithQuotedDelimiters()
    {
        using var connection = OpenManagedConnection();

        connection.ExecuteNonQuery("""
            CREATE TABLE [source; data](value TEXT);
            CREATE TABLE "audit; data"(value TEXT);
            CREATE TRIGGER IF NOT EXISTS `insert; trigger` AFTER INSERT ON [source; data]
            BEGIN
                /* The delimiter-like text ; END BEGIN is part of this comment. */
                INSERT INTO "audit; data" VALUES (CASE WHEN 1 THEN 'insert; BEGIN END' ELSE 'unreachable' END);
                -- This comment also contains ; END.
                INSERT INTO "audit; data" VALUES ('second insert');
            END;
            CREATE TRIGGER "update; trigger" AFTER UPDATE ON [source; data]
            BEGIN
                INSERT INTO "audit; data" VALUES ('update');
            END;
            INSERT INTO [source; data] VALUES ('source; value');
            UPDATE [source; data] SET value = 'updated; value';
            """);

        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM \"audit; data\";").Should().Be(3);
    }

    [Test]
    public void ManagedProviderPreservesMalformedTriggerScriptForCoreDiagnostics()
    {
        using var connection = OpenManagedConnection();

        var exception = Assert.Throws<SqliteException>(() => connection.ExecuteNonQuery("""
            CREATE TABLE source(value TEXT);
            CREATE TABLE audit(value TEXT);
            CREATE TRIGGER source_audit AFTER INSERT ON source
            BEGIN
                INSERT INTO audit VALUES ('inserted');
            END trailing;
            """));

        exception!.Message.Should().Contain("Expected End");
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger';").Should().Be(0);
    }

    private static SqliteConnection OpenManagedConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        return connection;
    }
}
