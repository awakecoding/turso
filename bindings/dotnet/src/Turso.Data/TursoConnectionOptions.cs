using System.Globalization;
using ManagedEncryptionOptions = Turso.Core.Storage.TursoEncryptionOptions;

namespace Turso;

public class TursoConnectionOptions
{
    private readonly TursoConnectionStringBuilder _builder;

    private TursoConnectionOptions(TursoConnectionStringBuilder builder)
    {
        _builder = builder;
    }

    public string GetConnectionString() => _builder.ConnectionString;

    public string? this[string keyword]
    {
        get => _builder.GetOption(keyword);
        set => _builder[keyword] = value ?? string.Empty;
    }

    public int DefaultTimeout => _builder.DefaultTimeout;

    public string DataSource => _builder.DataSource;

    public string Mode => _builder.Mode;

    public string Cache => _builder.Cache;

    public string AuthToken => _builder.AuthToken;

    public string ReplicaPath => _builder.ReplicaPath;

    public bool ReadYourWrites => _builder.ReadYourWrites;

    public bool Pooling => _builder.Pooling;

    public int SyncInterval => _builder.SyncInterval;

    public bool? Tls => _builder.Tls;

    public TursoLocalProvider LocalProvider => _builder.IsLocalProviderConfigured
        ? _builder.LocalProvider
        : IsRemote
            ? TursoLocalProvider.Native
            : TursoLocalProvider.Managed;

    public bool IsRemote => IsRemoteDataSource(DataSource);

    public bool IsReplica => IsRemote && !string.IsNullOrWhiteSpace(ReplicaPath);

    public TursoEncryptionCipher? GetEncryptionCipher() => _builder.GetEncryptionCipher();

    internal ManagedLocalOpenOptions GetManagedLocalOpenOptions()
    {
        var mode = ParseManagedOpenMode(Mode);
        var dataSource = string.IsNullOrEmpty(DataSource) ? ":memory:" : DataSource;
        if (mode is ManagedLocalOpenMode.ReadOnly or ManagedLocalOpenMode.ReadWrite
            && dataSource == ":memory:")
        {
            throw new InvalidOperationException($"Mode={Mode} requires an existing database file when Local Provider=Managed.");
        }

        if (mode is ManagedLocalOpenMode.ReadOnly or ManagedLocalOpenMode.ReadWrite && !File.Exists(dataSource))
            throw new InvalidOperationException($"Mode={Mode} requires an existing database file when Local Provider=Managed.");

        var cache = Cache;
        if (!string.IsNullOrWhiteSpace(cache)
            && !cache.Equals("Default", StringComparison.OrdinalIgnoreCase)
            && !cache.Equals("Private", StringComparison.OrdinalIgnoreCase))
        {
            if (cache.Equals("Shared", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    "Cache=Shared is not supported when Local Provider=Managed because managed connections do not share page caches.");
            }

            throw new ArgumentException($"Invalid Cache value for Local Provider=Managed: {cache}.", nameof(Cache));
        }

        if (!string.IsNullOrWhiteSpace(_builder.GetOption("Password")))
            throw new NotSupportedException("Password is not supported when Local Provider=Managed because the managed engine does not provide encryption.");
        if (!string.IsNullOrWhiteSpace(_builder.GetOption("Vfs")))
        {
            throw new NotSupportedException(
                "Vfs is not supported when Local Provider=Managed because the managed engine does not use native SQLite VFS implementations.");
        }

        if (_builder.GetOption("Foreign Keys") is not null)
            throw new NotSupportedException("Foreign Keys is not supported when Local Provider=Managed.");
        if (_builder.GetOption("Recursive Triggers") is not null)
            throw new NotSupportedException("Recursive Triggers is not supported when Local Provider=Managed.");
        var timeout = DefaultTimeout;
        if (timeout < 0)
            throw new ArgumentOutOfRangeException(nameof(DefaultTimeout), timeout, "Default Timeout cannot be negative.");

        var managedDataSource = mode == ManagedLocalOpenMode.Memory ? ":memory:" : dataSource;
        return new ManagedLocalOpenOptions(
            managedDataSource,
            mode == ManagedLocalOpenMode.ReadOnly,
            CreateManagedEncryptionOptions(mode, managedDataSource));
    }

    public Uri GetRemoteUri()
    {
        if (!Uri.TryCreate(DataSource, UriKind.Absolute, out var uri) || !IsRemoteScheme(uri.Scheme))
            throw new InvalidOperationException($"Data Source is not a remote Turso URL: {DataSource}");

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Remote Turso URLs must not include query strings or fragments.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Remote Turso URLs must not include embedded user information; use Auth Token instead.");
        if (string.IsNullOrEmpty(uri.Host))
            throw new InvalidOperationException("Remote Turso URLs must include a host.");

        var scheme = uri.Scheme.ToLowerInvariant() switch
        {
            "libsql" => Tls == false ? "http" : "https",
            "http" => ValidateTls(uri.Scheme, expectedTls: false),
            "https" => ValidateTls(uri.Scheme, expectedTls: true),
            "ws" => ValidateTls(uri.Scheme, expectedTls: false, normalizedScheme: "http"),
            "wss" => ValidateTls(uri.Scheme, expectedTls: true, normalizedScheme: "https"),
            _ => throw new InvalidOperationException($"Unsupported remote Turso URL scheme: {uri.Scheme}")
        };

        var builder = new UriBuilder(uri)
        {
            Scheme = scheme,
            Port = uri.IsDefaultPort ? -1 : uri.Port,
            UserName = string.Empty,
            Password = string.Empty,
        };

        return builder.Uri;
    }

    public static TursoConnectionOptions Parse(string connectionString)
    {
        return new TursoConnectionOptions(new TursoConnectionStringBuilder(connectionString));
    }

    internal bool TryGetManagedPoolKey(out ManagedConnectionPoolKey key)
    {
        key = default;
        if (!Pooling || IsRemote || LocalProvider != TursoLocalProvider.Managed)
            return false;

        var mode = ParseManagedOpenMode(Mode);
        var dataSource = string.IsNullOrEmpty(DataSource) ? ":memory:" : DataSource;
        if (mode == ManagedLocalOpenMode.Memory
            || dataSource.Equals(":memory:", StringComparison.Ordinal)
            || GetEncryptionCipher().HasValue
            || _builder.GetOption("Encryption Key") is not null)
        {
            return false;
        }

        key = ManagedConnectionPoolKey.Create(
            dataSource,
            mode == ManagedLocalOpenMode.ReadOnly);
        return true;
    }

    private static bool IsRemoteDataSource(string dataSource)
    {
        return Uri.TryCreate(dataSource, UriKind.Absolute, out var uri)
               && IsRemoteScheme(uri.Scheme);
    }

    private static bool IsRemoteScheme(string scheme)
    {
        return scheme.Equals("libsql", StringComparison.OrdinalIgnoreCase)
               || scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
               || scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
               || scheme.Equals("ws", StringComparison.OrdinalIgnoreCase)
               || scheme.Equals("wss", StringComparison.OrdinalIgnoreCase);
    }

    private string ValidateTls(string scheme, bool expectedTls, string? normalizedScheme = null)
    {
        if (Tls.HasValue && Tls.Value != expectedTls)
        {
            var actual = Tls.Value.ToString(CultureInfo.InvariantCulture);
            throw new InvalidOperationException($"Tls={actual} conflicts with the {scheme} URL scheme.");
        }

        return normalizedScheme ?? scheme;
    }

    private static ManagedLocalOpenMode ParseManagedOpenMode(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode)
            || mode.Equals("ReadWriteCreate", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("rwc", StringComparison.OrdinalIgnoreCase))
        {
            return ManagedLocalOpenMode.ReadWriteCreate;
        }

        if (mode.Equals("ReadWrite", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("rw", StringComparison.OrdinalIgnoreCase))
        {
            return ManagedLocalOpenMode.ReadWrite;
        }

        if (mode.Equals("ReadOnly", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("ro", StringComparison.OrdinalIgnoreCase))
        {
            return ManagedLocalOpenMode.ReadOnly;
        }

        if (mode.Equals("Memory", StringComparison.OrdinalIgnoreCase))
            return ManagedLocalOpenMode.Memory;

        throw new ArgumentException($"Invalid Mode value for Local Provider=Managed: {mode}.", nameof(mode));
    }

    private ManagedEncryptionOptions? CreateManagedEncryptionOptions(
        ManagedLocalOpenMode mode,
        string dataSource)
    {
        var cipher = _builder.GetOption("Encryption Cipher");
        var key = _builder.GetOption("Encryption Key");
        if (string.IsNullOrWhiteSpace(cipher))
        {
            if (key is not null)
                throw new NotSupportedException("Encryption is not available for the managed engine.");

            return null;
        }

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Encryption Key is required when Encryption Cipher is specified.");
        if (mode == ManagedLocalOpenMode.Memory || dataSource == ":memory:")
        {
            throw new NotSupportedException(
                "Encryption is supported only for file-backed databases when Local Provider=Managed.");
        }

        return cipher.ToLowerInvariant() switch
        {
            "aes128gcm" => ManagedEncryptionOptions.FromHex(
                Turso.Core.Storage.TursoEncryptionCipher.Aes128Gcm,
                key),
            "aes256gcm" => ManagedEncryptionOptions.FromHex(
                Turso.Core.Storage.TursoEncryptionCipher.Aes256Gcm,
                key),
            _ => throw new NotSupportedException(
                "Local Provider=Managed supports only Turso encrypted format version 0 with "
                + "AES128GCM (cipher ID 1) or AES256GCM (cipher ID 2); cipher fallback is not permitted."),
        };
    }
}

internal readonly record struct ManagedLocalOpenOptions(
    string DataSource,
    bool ReadOnly,
    ManagedEncryptionOptions? Encryption) : IDisposable
{
    public void Dispose() => Encryption?.Dispose();
}

internal enum ManagedLocalOpenMode
{
    ReadWriteCreate,
    ReadWrite,
    ReadOnly,
    Memory,
}
