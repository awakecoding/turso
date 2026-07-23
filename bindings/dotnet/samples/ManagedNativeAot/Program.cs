using Turso.Core;

var databasePath = Path.Combine(AppContext.BaseDirectory, "managed-nativeaot-smoke.db");
DeleteDatabaseFiles(databasePath);

try
{
    using var database = EmbeddedDatabase.OpenFile(databasePath);
    using var connection = database.Connect();

    Execute(connection, "CREATE TABLE items (id INTEGER PRIMARY KEY, value TEXT NOT NULL)");
    Execute(connection, "INSERT INTO items (value) VALUES ('managed'), ('nativeaot')");

    using var query = connection.Prepare("SELECT COUNT(*) FROM items");
    if (query.Step() != StatementStepResult.Row || query.GetValue(0).AsInteger() != 2)
        throw new InvalidOperationException("The managed NativeAOT smoke query returned an unexpected result.");
    if (query.Step() != StatementStepResult.Done)
        throw new InvalidOperationException("The managed NativeAOT smoke query returned more than one row.");

    Console.WriteLine("Managed NativeAOT smoke succeeded.");
}
finally
{
    DeleteDatabaseFiles(databasePath);
}

static void Execute(EmbeddedConnection connection, string sql)
{
    using var statement = connection.Prepare(sql);
    if (statement.Step() != StatementStepResult.Done)
        throw new InvalidOperationException($"Expected '{sql}' to complete without rows.");
}

static void DeleteDatabaseFiles(string databasePath)
{
    File.Delete(databasePath);
    File.Delete(databasePath + "-wal");
    File.Delete(databasePath + "-shm");
}
