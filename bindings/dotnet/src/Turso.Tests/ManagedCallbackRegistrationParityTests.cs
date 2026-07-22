using AwesomeAssertions;
using Turso.Data.Sqlite;

namespace Turso.Tests;

public class ManagedCallbackRegistrationParityTests
{
    [Test]
    public void ManagedCallbacksPreserveSignatureSpecificRegistrationsAcrossReopen()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.CreateFunction<long, string>("callback_arity", static value => $"one:{value}");
        connection.CreateFunction<long, long, string>("callback_arity", static (left, right) => $"two:{left + right}");
        connection.CreateFunction("callback_arity", static arguments => $"variadic:{arguments.Length}");
        connection.CreateAggregate<long, long>("callback_total", 0L, static (total, value) => total + value);
        connection.CreateAggregate<long>("callback_total", 0L, static (total, arguments) => total + (arguments.Length * 100));

        connection.Open();
        connection.Close();
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE CallbackValues(Value INTEGER); INSERT INTO CallbackValues VALUES (2), (3);");

        connection.ExecuteScalar<string>("SELECT callback_arity(3);").Should().Be("one:3");
        connection.ExecuteScalar<string>("SELECT callback_arity(3, 4);").Should().Be("two:7");
        connection.ExecuteScalar<string>("SELECT callback_arity(3, 4, 5);").Should().Be("variadic:3");
        connection.ExecuteScalar<long>("SELECT callback_total(Value) FROM CallbackValues;").Should().Be(5);
        connection.ExecuteScalar<long>("SELECT callback_total(Value, Value) FROM CallbackValues;").Should().Be(400);
    }
}
