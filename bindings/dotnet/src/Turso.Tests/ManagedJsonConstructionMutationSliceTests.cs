using AwesomeAssertions;
using Turso.Core;

namespace Turso.Tests;

public class ManagedJsonConstructionMutationSliceTests
{
    [Test]
    public void ConstructorsAndQuotePreserveJsonAndBoundTextSemantics()
    {
        AssertText(
            "json_array(1, 1.5, 'a\"b', NULL)",
            "[1,1.5,\"a\\\"b\",null]");
        AssertText(
            "json_object('x', 1, 'nested', json_array(2, 3))",
            "{\"x\":1,\"nested\":[2,3]}");
        AssertText(
            "json_quote(json_object('a', 1))",
            "{\"a\":1}");
        AssertText(
            "json_quote('a\"b')",
            "\"a\\\"b\"");
        AssertText(
            "json_array(?1)",
            "[\"{\\\"a\\\":1}\"]",
            SqlValue.Text("{\"a\":1}"));
    }

    [Test]
    public void MutatorsHandleNestedPathsSequentialUpdatesAndJsonValues()
    {
        AssertText(
            "json_set('{}', '$.items[0].name', 'Ada', '$.items[0].active', 1)",
            "{\"items\":[{\"name\":\"Ada\",\"active\":1}]}");
        AssertText(
            "json_insert('{\"a\":1,\"items\":[10]}', '$.a', 2, '$.items[#]', 20)",
            "{\"a\":1,\"items\":[10,20]}");
        AssertText(
            "json_replace('{\"profile\":{\"old\":1},\"other\":0}', '$.profile', json_object('name', 'Ada'), '$.other', NULL)",
            "{\"profile\":{\"name\":\"Ada\"},\"other\":null}");
        AssertText(
            "json_remove('{\"a\":[1,2,3],\"b\":{\"x\":1,\"y\":2}}', '$.a[0]', '$.a[#-1]', '$.b.x')",
            "{\"a\":[2],\"b\":{\"y\":2}}");
        AssertText(
            "json_patch('{\"user\":{\"name\":\"Ada\",\"age\":20},\"keep\":true}', '{\"user\":{\"age\":21},\"keep\":null,\"roles\":[\"admin\"]}')",
            "{\"user\":{\"name\":\"Ada\",\"age\":21},\"roles\":[\"admin\"]}");
        AssertInteger("json_array_length('{\"items\":[1,2,3]}', '$.items')", 3);
        AssertNull("json_array_length('{\"items\":[1,2,3]}', '$.missing')");
    }

    [Test]
    public void ErrorPositionAndUnsupportedInputsHaveExplicitFailures()
    {
        AssertInteger("json_error_position('{]')", 2);
        AssertInteger("json_error_position('{\"a\":}')", 6);
        AssertInteger("json_error_position('{\"a\":[1,true,null]}')", 0);
        AssertNull("json_error_position(NULL)");

        AssertText("json_set('{\"x\":1}', '$.x[', 1)", "{\"x\":1}");
        Assert.Throws<EmbeddedSqlException>(() => Scalar("json_object('only-key')"));
        Assert.Throws<EmbeddedSqlException>(() => Scalar("json_array(x'00')"));
        Assert.Throws<EmbeddedSqlException>(() => Scalar("json_set('{}', '$.x[', 1)"));
        Assert.Throws<EmbeddedSqlException>(() => Scalar("json('{unquoted:1}')"));
        var unsupported = Assert.Throws<EmbeddedSqlException>(() => Scalar("jsonb_array(1)"));
        unsupported!.Message.Should().Be("no such function: JSONB_ARRAY");
    }

    private static SqlValue Scalar(string expression, params SqlValue[] parameters)
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare("SELECT " + expression + ";");
        for (int i = 0; i < parameters.Length; i++)
            statement.Bind(i + 1, parameters[i]);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }

    private static void AssertText(string expression, string expected, params SqlValue[] parameters)
        => Scalar(expression, parameters).Should().Be(SqlValue.Text(expected), because: expression);

    private static void AssertInteger(string expression, long expected)
        => Scalar(expression).Should().Be(SqlValue.Integer(expected), because: expression);

    private static void AssertNull(string expression)
        => Scalar(expression).Should().Be(SqlValue.Null, because: expression);
}
