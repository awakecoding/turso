using AwesomeAssertions;
using Turso.Core;
using Turso.Core.Storage;

namespace Turso.Tests;

public class WithoutRowidKeySchemaPrerequisiteTests
{
    [Test]
    public void PrimaryKeySchemaProjectsAndEncodesTermsInDeclarationOrder()
    {
        var schema = new SqlitePrimaryKeySchema(
        [
            new SqlitePrimaryKeyTerm(
                2,
                "tail",
                SqliteKeySortOrder.Ascending,
                SqliteKeyCollation.FromName("binary")),
            new SqlitePrimaryKeyTerm(
                0,
                "head",
                SqliteKeySortOrder.Ascending,
                SqliteKeyCollation.Binary),
        ]);

        schema.Terms.Select(term => term.Collation.Name).Should().Equal("BINARY", "BINARY");
        schema.ProjectKey([SqlValue.Integer(1), SqlValue.Text("ignored"), SqlValue.Integer(3)])
            .Should()
            .Equal(SqlValue.Integer(3), SqlValue.Integer(1));
        SqliteRecordCodec.Decode(
                schema.EncodeKeyPrefix(
                    [SqlValue.Integer(1), SqlValue.Text("ignored"), SqlValue.Integer(3)]))
            .Should()
            .Equal(SqlValue.Integer(3), SqlValue.Integer(1));
        schema.EnsureSupportedByBinaryAscendingIndexWriter();
    }

    [Test]
    public void PrimaryKeySchemaRejectsEveryUnsupportedWriterOrderAndCollation()
    {
        var schema = new SqlitePrimaryKeySchema(
        [
            new SqlitePrimaryKeyTerm(
                0,
                "descending_unknown",
                SqliteKeySortOrder.Descending,
                SqliteKeyCollation.Unavailable),
            new SqlitePrimaryKeyTerm(
                1,
                "nocase",
                SqliteKeySortOrder.Ascending,
                SqliteKeyCollation.FromName("nocase")),
        ]);

        var act = () => schema.EnsureSupportedByBinaryAscendingIndexWriter();

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*descending_unknown*descending*")
            .WithMessage("*descending_unknown*collation metadata*")
            .WithMessage("*nocase*NOCASE*");
    }

    [Test]
    public void PrimaryKeySchemaRejectsAmbiguousTermMetadata()
    {
        Assert.Throws<ArgumentException>(() => new SqlitePrimaryKeySchema(
        [
            new SqlitePrimaryKeyTerm(
                0,
                "first",
                SqliteKeySortOrder.Ascending,
                SqliteKeyCollation.Binary),
            new SqlitePrimaryKeyTerm(
                0,
                "second",
                SqliteKeySortOrder.Ascending,
                SqliteKeyCollation.Binary),
        ]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlitePrimaryKeySchema(
        [
            new SqlitePrimaryKeyTerm(
                0,
                "invalid_direction",
                (SqliteKeySortOrder)123,
                SqliteKeyCollation.Binary),
        ]));
    }

    [Test]
    public void WithoutRowidBinaryAscendingPrimaryKeyPersistsAndUnsupportedTermsRejectBeforeWalWrite()
    {
        const string path = "without-rowid-key-schema-prerequisite.db";
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE retained(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, "INSERT INTO retained VALUES (1, 'durable');");
            Execute(
                connection,
                "CREATE TABLE supported(k TEXT COLLATE NOCASE, value TEXT, PRIMARY KEY(k COLLATE BINARY ASC)) WITHOUT ROWID;");
            Execute(connection, "INSERT INTO supported VALUES ('key', 'persisted');");

            var writesBeforeReject = faults.GetOperationCount(FileSystemOperation.Write);
            faults.FailNext(FileSystemOperation.Write);

            var act = () => Execute(
                connection,
                "CREATE TABLE rejected(k TEXT COLLATE NOCASE, value TEXT, PRIMARY KEY(k COLLATE NOCASE ASC)) WITHOUT ROWID;");

            var exception = act.Should().Throw<EmbeddedSqlException>().Which;
            exception.Message.Should().Contain("uses NOCASE collation");
            faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBeforeReject);
            Scalar(connection, "SELECT value FROM retained WHERE id = 1;").AsText().Should().Be("durable");
            Scalar(connection, "SELECT value FROM supported WHERE k = 'key';").AsText().Should().Be("persisted");
            Assert.Throws<EmbeddedSqlException>(() => Scalar(connection, "SELECT COUNT(*) FROM rejected;"));
            faults.ClearScheduled();
        }

        using var recovered = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var recoveredConnection = recovered.Connect();
        Scalar(recoveredConnection, "SELECT value FROM retained WHERE id = 1;").AsText().Should().Be("durable");
        Scalar(recoveredConnection, "SELECT value FROM supported WHERE k = 'key';").AsText().Should().Be("persisted");
        Assert.Throws<EmbeddedSqlException>(() => Scalar(recoveredConnection, "SELECT COUNT(*) FROM rejected;"));
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static SqlValue Scalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }
}
