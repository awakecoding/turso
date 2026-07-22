using AwesomeAssertions;
using Turso.Core;

namespace Turso.Tests;

public sealed class ManagedModuloAndPrintfConformanceTests
{
    [Test]
    public void ModuloCoercesValuesAndPreservesSqliteResultTypes()
    {
        using var connection = new EmbeddedDatabase().Connect();

        var values = ReadRow(
            connection,
            """
            SELECT
                10 + 11 % 3 * 2,
                38 % 10.35,
                38.43 % 13,
                0 % 12.0,
                '10' % '3',
                '10.0' % '3',
                '123abc' % 2,
                x'3130' % 3,
                'a' % 'a',
                183 % NULL,
                183 % 0,
                -9223372036854775808 % -1
            """);

        values.Should().Equal(
            SqlValue.Integer(14),
            SqlValue.Real(8),
            SqlValue.Real(12),
            SqlValue.Real(0),
            SqlValue.Integer(1),
            SqlValue.Real(1),
            SqlValue.Integer(1),
            SqlValue.Integer(1),
            SqlValue.Null,
            SqlValue.Null,
            SqlValue.Null,
            SqlValue.Integer(0));
    }

    [Test]
    public void PrintfSupportsOnlyTheDocumentedUnmodifiedVerbs()
    {
        using var connection = new EmbeddedDatabase().Connect();

        var value = ReadRow(
            connection,
            """
            SELECT printf(
                's=%s|null=%s|d=%d|i=%i|u=%u|x=%x|X=%X|o=%o|f=%f|e=%e|E=%E|g=%g|G=%G|c=%c|q=%q|Q=%Q|w=%w|blob=%s|%%',
                'text', NULL, '123abc', 3.9, -1, 255, 255, 8, 42.5, 23000000.0, 23000000.0,
                1234567.0, 1234567.0, 'hello', 'it''s', 'it''s', 'col"name', x'410042')
            """)[0];

        value.Should().Be(SqlValue.Text(
            "s=text|null=|d=123|i=3|u=18446744073709551615|x=ff|X=FF|o=10|f=42.500000"
            + "|e=2.300000e+07|E=2.300000E+07|g=1.23457e+06|G=1.23457E+06|c=h"
            + "|q=it''s|Q='it''s'|w=col\"\"name|blob=A|%"));

        ReadRow(connection, "SELECT printf()")[0].Should().Be(SqlValue.Null);
        ReadRow(connection, "SELECT printf(NULL)")[0].Should().Be(SqlValue.Null);
        ReadRow(connection, "SELECT printf(123)")[0].Should().Be(SqlValue.Text("123"));
        ReadRow(connection, "SELECT printf('%d|%s')")[0].Should().Be(SqlValue.Text("0|"));
    }

    [TestCase("%05d")]
    [TestCase("%.2f")]
    [TestCase("%*d")]
    [TestCase("%p")]
    [TestCase("%s", "1.0")]
    public void PrintfRejectsUnsupportedFormatModifiersVerbsAndConversions(string format, string? argument = null)
    {
        using var connection = new EmbeddedDatabase().Connect();

        var value = argument ?? "42";
        var exception = Assert.Throws<EmbeddedSqlException>(() => ReadRow(connection, $"SELECT printf('{format}', {value})"));

        if (argument is null)
            exception!.Message.Should().Be($"unsupported printf format specifier: %{format[1]}");
        else
            exception!.Message.Should().Be("printf() text rendering does not support real values.");
    }

    private static SqlValue[] ReadRow(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);

        var values = new SqlValue[statement.GetColumnCount()];
        for (var ordinal = 0; ordinal < values.Length; ordinal++)
            values[ordinal] = statement.GetValue(ordinal);

        statement.Step().Should().Be(StatementStepResult.Done);
        return values;
    }
}
