using Turso.Data.Sqlite;

namespace Turso.Tests;

public sealed class ManagedIncrementalBlobBoundaryTests
{
    [Test]
    public void ManagedBlobConstructionRejectsBeforeTableLookupOrNativeInterop()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        var exception = Assert.Throws<NotSupportedException>(
            () => new SqliteBlob(connection, "missing_table", "missing_column", long.MinValue));

        Assert.That(exception!.Message, Is.EqualTo(Data.Sqlite.Properties.Resources.ManagedIncrementalBlobNotSupported));
    }
}
