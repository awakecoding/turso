using System.Text.Json;
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

EnsureNoNativeCompanionWasRestored();

Console.WriteLine("Managed package consumer succeeded.");

static void EnsureNoNativeCompanionWasRestored()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        var assetsPath = Path.Combine(directory.FullName, "obj", "project.assets.json");
        if (!File.Exists(assetsPath))
            continue;

        using var assetsStream = File.OpenRead(assetsPath);
        using var assets = JsonDocument.Parse(assetsStream);
        var nativePackage = assets.RootElement
            .GetProperty("libraries")
            .EnumerateObject()
            .Select(library => library.Name)
            .FirstOrDefault(IsNativeCompanionPackage);
        if (nativePackage is not null)
        {
            throw new InvalidOperationException(
                $"The managed Turso package consumer must not restore native companion package {nativePackage}.");
        }

        return;
    }

    throw new FileNotFoundException("Could not locate the managed consumer restore graph.");
}

static bool IsNativeCompanionPackage(string packageIdentity)
    => packageIdentity.StartsWith("Turso.Raw/", StringComparison.OrdinalIgnoreCase) ||
       packageIdentity.StartsWith("Turso.Data.Native/", StringComparison.OrdinalIgnoreCase) ||
       packageIdentity.StartsWith("Turso.Data.Sqlite.Native/", StringComparison.OrdinalIgnoreCase) ||
       packageIdentity.StartsWith("Turso.Data.Sqlite.NativeAot", StringComparison.OrdinalIgnoreCase);
