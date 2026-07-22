using Turso.Data.Sqlite;

using var connection = new SqliteConnection("Data Source=:memory:");
connection.Open();

using var command = connection.CreateCommand();
command.CommandText = "SELECT 1";

if (command.ExecuteScalar() is not 1L)
    throw new InvalidOperationException("The managed Turso package consumer returned an unexpected result.");

if (AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
        string.Equals(assembly.GetName().Name, "Turso.Raw", StringComparison.Ordinal)))
{
    throw new InvalidOperationException("The managed Turso package consumer must not load Turso.Raw.");
}

Console.WriteLine("Managed package consumer succeeded.");
