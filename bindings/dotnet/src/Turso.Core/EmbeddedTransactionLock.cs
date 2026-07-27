using System.Runtime.CompilerServices;
using Turso.Core.Storage;

namespace Turso.Core;

/// <summary>
/// Raised when a managed statement cannot take the lock a SQLite connection
/// would need. This is the managed engine's <c>SQLITE_BUSY</c>.
/// </summary>
public sealed class EmbeddedBusyException : EmbeddedSqlException
{
    /// <summary>Creates a busy failure carrying SQLite's message.</summary>
    public EmbeddedBusyException()
        : base("database is locked")
    {
    }
}

/// <summary>
/// The write reservation a managed transaction holds on one database, modeling
/// SQLite's RESERVED and EXCLUSIVE locks for managed connections.
/// </summary>
/// <remarks>
/// This lock is process-local by design. A managed physical database is owned
/// exclusively by one process for its whole lifetime (see
/// <c>docs/managed-wal-interoperability-contract.md</c>), so every connection
/// that can contend for a write is in this process. The lock is layered above
/// the pager and adds no cross-process boundary of its own, so it neither
/// relaxes nor duplicates that ownership guard.
///
/// A holder is identified by the owning object rather than by thread, because a
/// managed transaction is owned by a connection and can be advanced from
/// different threads across awaits.
/// </remarks>
internal sealed class EmbeddedTransactionLock
{
    private readonly object _gate = new();
    private object? _owner;
    private int _holds;
    private bool _excludesReaders;

    /// <summary>Whether <paramref name="owner"/> currently holds this lock.</summary>
    internal bool IsHeldBy(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_gate)
            return ReferenceEquals(_owner, owner);
    }

    /// <summary>
    /// Takes the write reservation for <paramref name="owner"/>, or throws busy
    /// when another owner holds it. SQLite's default <c>busy_timeout</c> is zero,
    /// so contention fails immediately instead of waiting.
    /// </summary>
    /// <param name="owner">The connection taking the reservation.</param>
    /// <param name="excludeReaders">
    /// Whether the reservation also excludes other owners' reads, which SQLite's
    /// EXCLUSIVE lock does only under a rollback journal.
    /// </param>
    internal void Enter(object owner, bool excludeReaders)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_gate)
        {
            if (_owner is not null && !ReferenceEquals(_owner, owner))
                throw new EmbeddedBusyException();

            _owner = owner;
            _holds = checked(_holds + 1);
            _excludesReaders |= excludeReaders;
        }
    }

    /// <summary>Releases one reservation taken by <paramref name="owner"/>.</summary>
    internal void Exit(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_gate)
        {
            if (!ReferenceEquals(_owner, owner))
                throw new InvalidOperationException("The managed transaction write reservation was lost.");

            _holds--;
            if (_holds != 0)
                return;

            _owner = null;
            _excludesReaders = false;
        }
    }

    /// <summary>
    /// Throws busy when another owner holds a reservation that blocks writes.
    /// Autocommit statements use this instead of taking the reservation, because
    /// they are already serialized by the owning database and hold no lock across
    /// statements.
    /// </summary>
    internal void ThrowIfWriteBlocked(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_gate)
        {
            if (_owner is not null && !ReferenceEquals(_owner, owner))
                throw new EmbeddedBusyException();
        }
    }

    /// <summary>
    /// Throws busy when another owner holds a reader-excluding reservation.
    /// </summary>
    internal void ThrowIfReadBlocked(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_gate)
        {
            if (_excludesReaders && _owner is not null && !ReferenceEquals(_owner, owner))
                throw new EmbeddedBusyException();
        }
    }
}

/// <summary>
/// Brokers one <see cref="EmbeddedTransactionLock"/> per file-backed database so
/// every managed connection opened on the same path contends for the same
/// reservation. In-memory databases own their lock directly, because the only
/// way two connections share one is by sharing the database instance itself.
/// </summary>
internal static class EmbeddedTransactionLockRegistry
{
    private sealed class LockScope
    {
        private readonly Dictionary<string, EmbeddedTransactionLock> _locks = new(StringComparer.Ordinal);

        internal EmbeddedTransactionLock Get(string key)
        {
            lock (_locks)
            {
                if (!_locks.TryGetValue(key, out var transactionLock))
                {
                    transactionLock = new EmbeddedTransactionLock();
                    _locks.Add(key, transactionLock);
                }

                return transactionLock;
            }
        }
    }

    private static readonly ConditionalWeakTable<IFileSystem, LockScope> FileSystemScopes = new();
    private static readonly LockScope PhysicalFileSystemScope = new();

    internal static EmbeddedTransactionLock Get(IFileSystem fileSystem, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(databasePath);

        var unwrapped = TursoEncryptionFileSystem.Unwrap(fileSystem);
        if (unwrapped is not PhysicalFileSystem)
            return FileSystemScopes.GetValue(unwrapped, static _ => new LockScope()).Get(databasePath);

        var key = Path.GetFullPath(databasePath);
        return PhysicalFileSystemScope.Get(OperatingSystem.IsWindows() ? key.ToUpperInvariant() : key);
    }
}
