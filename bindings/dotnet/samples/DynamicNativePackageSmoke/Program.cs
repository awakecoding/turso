using Turso;
using Turso.Data.Sqlite;
using Turso.Data.Sync;

NativeAbiContract.Validate();

using (var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Native"))
{
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT 'héllo 🌍';";
    if (command.ExecuteScalar() is not "héllo 🌍")
        throw new InvalidOperationException("The dynamic native provider failed its UTF-8 round trip.");

    command.CommandText = "SELECT * FROM \"missing_ß\";";
    try
    {
        command.ExecuteScalar();
        throw new InvalidOperationException("The dynamic native provider did not return an owned error.");
    }
    catch (SqliteException exception) when (exception.Message.Contains("missing_ß", StringComparison.Ordinal))
    {
    }
}

ReplicaProviderRegistration.Register();
var factoryType = typeof(ReplicaProviderRegistration).Assembly.GetType(
    "Turso.Data.Sync.SyncReplicaProviderFactory",
    throwOnError: true)!;
var factory = (TursoReplicaProviderFactory)Activator.CreateInstance(factoryType, nonPublic: true)!;
using (var replica = factory.OpenReplica(
           new TursoReplicaOptions(
               ":memory:",
               new Uri("http://127.0.0.1:1"),
               authToken: null,
               bootstrapIfEmpty: false)))
{
    using var statement = replica.PrepareStatement("SELECT 'héllo 🌍'");
    if (!statement.Read() || statement.GetValue(0).StringValue != "héllo 🌍" || statement.Read())
        throw new InvalidOperationException("The dynamic Sync companion failed its UTF-8 round trip.");

    try
    {
        replica.PrepareStatement("SELECT * FROM \"missing_ß\"").Dispose();
        throw new InvalidOperationException("The dynamic Sync companion did not return an owned error.");
    }
    catch (TursoException exception) when (exception.Message.Contains("missing_ß", StringComparison.Ordinal))
    {
    }
}

Console.WriteLine("Dynamic native package smoke passed.");
