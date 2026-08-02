namespace Turso;

/// <summary>
/// Identifies the public ADO.NET facade whose behavior a capability contract describes.
/// </summary>
public enum TursoConnectionFacade
{
    TursoData,
    Sqlite,
}

/// <summary>
/// Identifies the execution mode behind a connection.
/// </summary>
public enum TursoConnectionMode
{
    ManagedLocal,
    NativeLocal,
    RemoteHrana,
    EmbeddedReplica,
}

/// <summary>
/// Describes the operations supported by a facade and execution mode.
/// </summary>
public sealed class TursoConnectionCapabilities
{
    private static readonly TursoConnectionCapabilities TursoManagedLocal = new(
        TursoConnectionFacade.TursoData,
        TursoConnectionMode.ManagedLocal,
        canCreateBatch: true,
        supportsAsyncOperations: true,
        supportsTransactions: true,
        supportsSavepoints: true,
        supportsAttach: true,
        supportsPooling: true);

    private static readonly TursoConnectionCapabilities TursoNativeLocal = new(
        TursoConnectionFacade.TursoData,
        TursoConnectionMode.NativeLocal,
        canCreateBatch: true,
        supportsAsyncOperations: true,
        supportsTransactions: true,
        supportsSavepoints: true,
        supportsAttach: true);

    private static readonly TursoConnectionCapabilities TursoRemoteHrana = new(
        TursoConnectionFacade.TursoData,
        TursoConnectionMode.RemoteHrana,
        canCreateBatch: true,
        supportsAsyncOperations: true,
        supportsTransactions: true,
        supportsSavepoints: true);

    private static readonly TursoConnectionCapabilities TursoEmbeddedReplica = new(
        TursoConnectionFacade.TursoData,
        TursoConnectionMode.EmbeddedReplica,
        canCreateBatch: false,
        supportsAsyncOperations: true,
        supportsTransactions: true,
        supportsSavepoints: true,
        supportsSync: true);

    private static readonly TursoConnectionCapabilities SqliteManagedLocal = new(
        TursoConnectionFacade.Sqlite,
        TursoConnectionMode.ManagedLocal,
        canCreateBatch: true,
        supportsAsyncOperations: true,
        supportsTransactions: true,
        supportsSavepoints: true,
        supportsBackup: true,
        supportsIncrementalBlob: true,
        supportsUserDefinedFunctions: true,
        supportsUserDefinedAggregates: true,
        supportsCustomCollations: true,
        supportsAttach: true,
        supportsPooling: true);

    private static readonly TursoConnectionCapabilities SqliteNativeLocal = new(
        TursoConnectionFacade.Sqlite,
        TursoConnectionMode.NativeLocal,
        canCreateBatch: true,
        supportsAsyncOperations: true,
        supportsTransactions: true,
        supportsSavepoints: true,
        supportsBackup: true,
        supportsIncrementalBlob: true,
        supportsUserDefinedFunctions: true,
        supportsUserDefinedAggregates: true,
        supportsCustomCollations: true,
        supportsExtensions: true,
        supportsAttach: true);

    private TursoConnectionCapabilities(
        TursoConnectionFacade facade,
        TursoConnectionMode mode,
        bool canCreateBatch,
        bool supportsAsyncOperations,
        bool supportsTransactions,
        bool supportsSavepoints,
        bool supportsBackup = false,
        bool supportsIncrementalBlob = false,
        bool supportsUserDefinedFunctions = false,
        bool supportsUserDefinedAggregates = false,
        bool supportsCustomCollations = false,
        bool supportsExtensions = false,
        bool supportsAttach = false,
        bool supportsPooling = false,
        bool supportsSync = false)
    {
        Facade = facade;
        Mode = mode;
        CanCreateBatch = canCreateBatch;
        SupportsAsyncOperations = supportsAsyncOperations;
        SupportsTransactions = supportsTransactions;
        SupportsSavepoints = supportsSavepoints;
        SupportsBackup = supportsBackup;
        SupportsIncrementalBlob = supportsIncrementalBlob;
        SupportsUserDefinedFunctions = supportsUserDefinedFunctions;
        SupportsUserDefinedAggregates = supportsUserDefinedAggregates;
        SupportsCustomCollations = supportsCustomCollations;
        SupportsExtensions = supportsExtensions;
        SupportsAttach = supportsAttach;
        SupportsPooling = supportsPooling;
        SupportsSync = supportsSync;
    }

    public TursoConnectionFacade Facade { get; }

    public TursoConnectionMode Mode { get; }

    public bool CanCreateBatch { get; }

    public bool SupportsAsyncOperations { get; }

    public bool SupportsTransactions { get; }

    public bool SupportsSavepoints { get; }

    public bool SupportsBackup { get; }

    public bool SupportsIncrementalBlob { get; }

    public bool SupportsUserDefinedFunctions { get; }

    public bool SupportsUserDefinedAggregates { get; }

    public bool SupportsCustomCollations { get; }

    public bool SupportsExtensions { get; }

    public bool SupportsAttach { get; }

    public bool SupportsPooling { get; }

    public bool SupportsSync { get; }

    internal static TursoConnectionCapabilities ForTurso(TursoConnectionOptions options)
    {
        if (options.IsReplica)
            return TursoEmbeddedReplica;
        if (options.IsRemote)
            return TursoRemoteHrana;
        return options.LocalProvider == TursoLocalProvider.Managed
            ? TursoManagedLocal
            : TursoNativeLocal;
    }

    internal static TursoConnectionCapabilities ForSqlite(TursoLocalProvider provider)
        => provider == TursoLocalProvider.Managed
            ? SqliteManagedLocal
            : SqliteNativeLocal;

    internal static bool IsRemoteDataSource(string dataSource)
        => Uri.TryCreate(dataSource, UriKind.Absolute, out var uri)
           && uri.Scheme is "libsql" or "http" or "https" or "ws" or "wss";
}
