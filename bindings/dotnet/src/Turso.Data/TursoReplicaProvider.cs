using System.Reflection;
using System.Runtime.Loader;

namespace Turso;

/// <summary>
/// Registers the optional embedded-replica implementation.
/// </summary>
public static class TursoReplicaProvider
{
    private const string ReplicaProviderAssemblyName = "Turso.Data.Sync";
    private const string ReplicaProviderRegistrationTypeName = "Turso.Data.Sync.ReplicaProviderRegistration";
    private static TursoReplicaProviderFactory? s_factory;

    /// <summary>
    /// Registers the embedded-replica factory supplied by the optional companion assembly.
    /// </summary>
    public static void Register(TursoReplicaProviderFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var registeredFactory = Interlocked.CompareExchange(ref s_factory, factory, null);
        if (registeredFactory is not null && registeredFactory.GetType() != factory.GetType())
        {
            throw new InvalidOperationException(
                $"An embedded replica provider factory of type {registeredFactory.GetType().FullName} is already registered.");
        }
    }

    internal static TursoReplicaDatabase OpenReplica(TursoReplicaOptions options)
    {
        return GetFactory().OpenReplica(options);
    }

    internal static Task<TursoReplicaDatabase> OpenReplicaAsync(
        TursoReplicaOptions options,
        CancellationToken cancellationToken)
    {
        return GetFactory().OpenReplicaAsync(options, cancellationToken);
    }

    private static TursoReplicaProviderFactory GetFactory()
    {
        var factory = Volatile.Read(ref s_factory);
        if (factory is null)
        {
            TryRegisterCompanion();
            factory = Volatile.Read(ref s_factory);
        }

        return factory
            ?? throw new NotSupportedException(
                "Embedded replica connections are not supported yet by the .NET provider. " +
                "Add the matching Turso.Data.Sqlite.Sync companion package to enable them.");
    }

    private static void TryRegisterCompanion()
    {
        try
        {
            var loadContext = AssemblyLoadContext.GetLoadContext(typeof(TursoReplicaProvider).Assembly);
            var assembly = loadContext?.LoadFromAssemblyName(new AssemblyName(ReplicaProviderAssemblyName))
                ?? Assembly.Load(new AssemblyName(ReplicaProviderAssemblyName));
            var registrationType = assembly.GetType(ReplicaProviderRegistrationTypeName, throwOnError: true)!;
            var register = registrationType.GetMethod(
                "Register",
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException(ReplicaProviderRegistrationTypeName, "Register");
            register.Invoke(null, null);
        }
        catch (FileNotFoundException)
        {
        }
    }
}

/// <summary>
/// Describes an embedded replica requested through <see cref="TursoConnection"/>.
/// </summary>
public sealed class TursoReplicaOptions
{
    /// <summary>
    /// Initializes embedded-replica connection options.
    /// </summary>
    public TursoReplicaOptions(
        string path,
        Uri remoteUri,
        string? authToken)
        : this(path, remoteUri, authToken, bootstrapIfEmpty: true)
    {
    }

    /// <summary>
    /// Initializes embedded-replica connection options.
    /// </summary>
    public TursoReplicaOptions(
        string path,
        Uri remoteUri,
        string? authToken,
        bool bootstrapIfEmpty = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(remoteUri);

        Path = path;
        RemoteUri = remoteUri;
        AuthToken = authToken;
        BootstrapIfEmpty = bootstrapIfEmpty;
    }

    /// <summary>
    /// Gets the local path of the replica database.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Gets the normalized HTTP(S) URL of the remote database.
    /// </summary>
    public Uri RemoteUri { get; }

    /// <summary>
    /// Gets the bearer token sent to the remote database, if configured.
    /// </summary>
    public string? AuthToken { get; }

    /// <summary>
    /// Gets whether a missing local replica is bootstrapped from the remote database.
    /// </summary>
    public bool BootstrapIfEmpty { get; }
}

/// <summary>
/// Contract implemented by the optional embedded-replica companion assembly.
/// </summary>
public abstract class TursoReplicaProviderFactory
{
    /// <summary>
    /// Opens an embedded replica and its local native SQL connection.
    /// </summary>
    public abstract TursoReplicaDatabase OpenReplica(TursoReplicaOptions options);

    /// <summary>
    /// Asynchronously opens an embedded replica and its local native SQL connection.
    /// </summary>
    public virtual Task<TursoReplicaDatabase> OpenReplicaAsync(
        TursoReplicaOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(OpenReplica(options));
    }
}

/// <summary>
/// A native SQL connection backed by an embedded replica.
/// </summary>
public abstract class TursoReplicaDatabase : TursoNativeDatabase
{
    /// <summary>
    /// Pushes local changes and pulls and applies remote changes.
    /// </summary>
    public abstract Task SyncAsync(CancellationToken cancellationToken);
}
