using AwesomeAssertions;
using Turso.Core;
using Turso.Core.Storage;
using Turso.Raw.Public;
using Turso.Raw.Public.Handles;
using Turso.Raw.Public.Value;

namespace Turso.Tests;

public sealed class CoreManagedAdapterOwnershipBoundaryLifecycleTests
{
    [Test]
    public void ConnectionOnlyRawHandleCompletesManagedStatementAndCloseLifecycle()
    {
        using var database = new EmbeddedDatabase();
        var connection = database.Connect();
        var databaseHandle = TursoDatabaseHandle.FromManaged(connection);
        var statementHandle = TursoBindings.PrepareStatement(databaseHandle, "SELECT ?1 AS value;");

        TursoBindings.BindParameter(statementHandle, 1, TursoValue.Int(7));
        TursoBindings.Read(statementHandle).Should().BeTrue();
        TursoBindings.GetValue(statementHandle, 0).IntValue.Should().Be(7);

        TursoBindings.Reset(statementHandle);
        TursoBindings.Read(statementHandle).Should().BeTrue();
        TursoBindings.ClearBindings(statementHandle);
        TursoBindings.Reset(statementHandle);
        Assert.Throws<TursoException>(() => TursoBindings.Read(statementHandle));

        statementHandle.Dispose();
        statementHandle.Dispose();
        databaseHandle.Dispose();
        databaseHandle.Dispose();

        statementHandle.IsClosed.Should().BeTrue();
        databaseHandle.IsClosed.Should().BeTrue();
        Assert.Throws<ObjectDisposedException>(() => connection.Prepare("SELECT 1;"));
    }

    [Test]
    public void DatabaseOwnedRawHandleDisposesItsManagedOwnerOnce()
    {
        var fileSystem = new OwnershipTrackingFileSystem();
        var database = EmbeddedDatabase.OpenFile("managed-adapter-owner.db", fileSystem);
        var databaseHandle = TursoDatabaseHandle.FromManaged(database.Connect(), database);

        fileSystem.OpenHandleCount.Should().BeGreaterThan(0);

        databaseHandle.Dispose();
        databaseHandle.Dispose();

        fileSystem.OpenHandleCount.Should().Be(0);
    }

    private sealed class OwnershipTrackingFileSystem : IFileSystem
    {
        private readonly object _gate = new();
        private readonly InMemoryFileSystem _inner = new();
        private readonly HashSet<OwnershipTrackingFile> _openFiles = [];

        public int OpenHandleCount
        {
            get
            {
                lock (_gate)
                    return _openFiles.Count;
            }
        }

        public bool FileExists(string path) => _inner.FileExists(path);

        public IFile OpenFile(string path, FileOpenMode mode, bool readOnly = false)
        {
            var file = new OwnershipTrackingFile(_inner.OpenFile(path, mode, readOnly), this);
            lock (_gate)
                _openFiles.Add(file);
            return file;
        }

        public void DeleteFile(string path) => _inner.DeleteFile(path);

        private void Close(OwnershipTrackingFile file)
        {
            lock (_gate)
                _openFiles.Remove(file);
        }

        private sealed class OwnershipTrackingFile(IFile inner, OwnershipTrackingFileSystem owner) : IFile
        {
            private bool _disposed;

            public long Length => inner.Length;

            public bool IsReadOnly => inner.IsReadOnly;

            public int Read(long position, Span<byte> destination) => inner.Read(position, destination);

            public void Write(long position, ReadOnlySpan<byte> source) => inner.Write(position, source);

            public void SetLength(long length) => inner.SetLength(length);

            public void FlushToDisk() => inner.FlushToDisk();

            public void Dispose()
            {
                if (_disposed)
                    return;

                _disposed = true;
                try
                {
                    inner.Dispose();
                }
                finally
                {
                    owner.Close(this);
                }
            }
        }
    }
}
