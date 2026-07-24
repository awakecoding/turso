using System.Net.Http;

namespace Turso;

/// <summary>
/// Selects how an embedded replica is partially bootstrapped.
/// </summary>
public enum TursoPartialBootstrapKind
{
    /// <summary>
    /// Bootstrap the pages covered by an initial byte prefix.
    /// </summary>
    Prefix,

    /// <summary>
    /// Bootstrap the pages touched by a server-side SQL query.
    /// </summary>
    Query,
}

/// <summary>
/// Configures partial bootstrap and lazy page loading for an embedded replica.
/// </summary>
public sealed class TursoPartialBootstrapOptions
{
    private TursoPartialBootstrapOptions(
        TursoPartialBootstrapKind kind,
        int prefixLength,
        string? query,
        long? segmentSize,
        bool prefetch)
    {
        if (segmentSize is <= 0)
            throw new ArgumentOutOfRangeException(nameof(segmentSize), segmentSize, "Segment size must be positive.");

        Kind = kind;
        PrefixLength = prefixLength;
        Query = query;
        SegmentSize = segmentSize;
        Prefetch = prefetch;
    }

    /// <summary>
    /// Creates a prefix strategy that bootstraps pages within the first <paramref name="length"/> bytes.
    /// </summary>
    public static TursoPartialBootstrapOptions Prefix(
        int length,
        long? segmentSize = null,
        bool prefetch = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        return new TursoPartialBootstrapOptions(
            TursoPartialBootstrapKind.Prefix,
            length,
            query: null,
            segmentSize,
            prefetch);
    }

    /// <summary>
    /// Creates a query strategy that bootstraps pages touched by <paramref name="query"/> on the server.
    /// </summary>
    public static TursoPartialBootstrapOptions QueryPages(
        string query,
        long? segmentSize = null,
        bool prefetch = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        return new TursoPartialBootstrapOptions(
            TursoPartialBootstrapKind.Query,
            prefixLength: 0,
            query,
            segmentSize,
            prefetch);
    }

    /// <summary>
    /// Gets the selected bootstrap strategy.
    /// </summary>
    public TursoPartialBootstrapKind Kind { get; }

    /// <summary>
    /// Gets the prefix length in bytes, or zero for a query strategy.
    /// </summary>
    public int PrefixLength { get; }

    /// <summary>
    /// Gets the server-side bootstrap query, or <see langword="null"/> for a prefix strategy.
    /// </summary>
    public string? Query { get; }

    /// <summary>
    /// Gets the lazy-loading segment size in bytes, or <see langword="null"/> for the SDK default.
    /// </summary>
    public long? SegmentSize { get; }

    /// <summary>
    /// Gets whether adjacent pages are prefetched during lazy loading.
    /// </summary>
    public bool Prefetch { get; }
}

/// <summary>
/// Selects the cipher configured for an encrypted Turso Cloud database.
/// </summary>
public enum TursoRemoteEncryptionCipher
{
    Aes256Gcm,
    Aes128Gcm,
    ChaCha20Poly1305,
    Aegis128L,
    Aegis128X2,
    Aegis128X4,
    Aegis256,
    Aegis256X2,
    Aegis256X4,
}

/// <summary>
/// Configures access to an encrypted Turso Cloud database.
/// </summary>
public sealed class TursoRemoteEncryptionOptions
{
    /// <summary>
    /// Initializes remote encryption with the base64-encoded key and server-side cipher.
    /// </summary>
    public TursoRemoteEncryptionOptions(string base64Key, TursoRemoteEncryptionCipher cipher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Key);
        Base64Key = base64Key;
        Cipher = cipher;
    }

    /// <summary>
    /// Gets the base64-encoded remote encryption key.
    /// </summary>
    public string Base64Key { get; }

    /// <summary>
    /// Gets the cipher configured on the remote database.
    /// </summary>
    public TursoRemoteEncryptionCipher Cipher { get; }

    internal int ReservedBytes => Cipher switch
    {
        TursoRemoteEncryptionCipher.Aes256Gcm
            or TursoRemoteEncryptionCipher.Aes128Gcm
            or TursoRemoteEncryptionCipher.ChaCha20Poly1305 => 28,
        TursoRemoteEncryptionCipher.Aegis128L
            or TursoRemoteEncryptionCipher.Aegis128X2
            or TursoRemoteEncryptionCipher.Aegis128X4 => 32,
        TursoRemoteEncryptionCipher.Aegis256
            or TursoRemoteEncryptionCipher.Aegis256X2
            or TursoRemoteEncryptionCipher.Aegis256X4 => 48,
        _ => throw new ArgumentOutOfRangeException(nameof(Cipher), Cipher, "Unknown remote encryption cipher."),
    };

    internal string NativeName => Cipher switch
    {
        TursoRemoteEncryptionCipher.Aes256Gcm => "aes256gcm",
        TursoRemoteEncryptionCipher.Aes128Gcm => "aes128gcm",
        TursoRemoteEncryptionCipher.ChaCha20Poly1305 => "chacha20poly1305",
        TursoRemoteEncryptionCipher.Aegis128L => "aegis128l",
        TursoRemoteEncryptionCipher.Aegis128X2 => "aegis128x2",
        TursoRemoteEncryptionCipher.Aegis128X4 => "aegis128x4",
        TursoRemoteEncryptionCipher.Aegis256 => "aegis256",
        TursoRemoteEncryptionCipher.Aegis256X2 => "aegis256x2",
        TursoRemoteEncryptionCipher.Aegis256X4 => "aegis256x4",
        _ => throw new ArgumentOutOfRangeException(nameof(Cipher), Cipher, "Unknown remote encryption cipher."),
    };
}

/// <summary>
/// Controls HTTP transport ownership and timeouts for one embedded replica.
/// </summary>
public sealed class TursoSyncHttpPolicy
{
    /// <summary>
    /// Initializes an HTTP policy.
    /// </summary>
    /// <param name="messageHandler">
    /// Optional application-provided handler. The replica does not dispose it unless
    /// <paramref name="disposeMessageHandler"/> is <see langword="true"/>.
    /// </param>
    /// <param name="requestTimeout">
    /// Per-request timeout. The default is infinite so long polling is governed by
    /// <see cref="TursoReplicaOptions.LongPollTimeout"/>.
    /// </param>
    /// <param name="disposeMessageHandler">
    /// Whether the embedded replica owns and disposes <paramref name="messageHandler"/>.
    /// </param>
    public TursoSyncHttpPolicy(
        HttpMessageHandler? messageHandler = null,
        bool disposeMessageHandler = false,
        TimeSpan? requestTimeout = null)
    {
        var timeout = requestTimeout ?? Timeout.InfiniteTimeSpan;
        if (timeout != Timeout.InfiniteTimeSpan
            && (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue))
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                timeout,
                $"Request timeout must be between 1 and {int.MaxValue} milliseconds, or infinite.");
        }

        MessageHandler = messageHandler;
        DisposeMessageHandler = disposeMessageHandler;
        RequestTimeout = timeout;
    }

    /// <summary>
    /// Gets the application-provided HTTP message handler, if any.
    /// </summary>
    public HttpMessageHandler? MessageHandler { get; }

    /// <summary>
    /// Gets whether the embedded replica owns and disposes <see cref="MessageHandler"/>.
    /// </summary>
    public bool DisposeMessageHandler { get; }

    /// <summary>
    /// Gets the per-request HTTP timeout.
    /// </summary>
    public TimeSpan RequestTimeout { get; }
}

/// <summary>
/// Identifies a synchronization phase.
/// </summary>
public enum TursoSyncProgressStage
{
    Pushing,
    Pulling,
    Applying,
    Completed,
}

/// <summary>
/// Describes progress through one explicit synchronization operation.
/// </summary>
public sealed record TursoSyncProgress(TursoSyncProgressStage Stage);

/// <summary>
/// Configures one explicit synchronization operation.
/// </summary>
public sealed class TursoSyncOptions
{
    /// <summary>
    /// Initializes synchronization options.
    /// </summary>
    public TursoSyncOptions(IProgress<TursoSyncProgress>? progress = null)
    {
        Progress = progress;
    }

    /// <summary>
    /// Gets the phase progress observer, if any.
    /// </summary>
    public IProgress<TursoSyncProgress>? Progress { get; }
}

/// <summary>
/// Identifies the observable result of a successful synchronization.
/// </summary>
public enum TursoSyncOutcome
{
    UpToDate,
    RemoteChangesApplied,
}

/// <summary>
/// Contains a snapshot of native sync-engine statistics.
/// </summary>
public sealed record TursoSyncStatistics(
    long CdcOperations,
    long MainWalSize,
    long RevertWalSize,
    DateTimeOffset? LastPull,
    DateTimeOffset? LastPush,
    long NetworkSentBytes,
    long NetworkReceivedBytes,
    string? Revision);

/// <summary>
/// Describes a completed explicit synchronization.
/// </summary>
public sealed record TursoSyncResult(TursoSyncOutcome Outcome, TursoSyncStatistics Statistics);
