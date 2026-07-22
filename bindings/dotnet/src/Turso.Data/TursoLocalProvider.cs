namespace Turso;

/// <summary>
/// Selects the implementation used for local database connections.
/// </summary>
public enum TursoLocalProvider
{
    /// <summary>
    /// Uses the native Turso SDK.
    /// </summary>
    Native,

    /// <summary>
    /// Uses the managed local engine.
    /// </summary>
    Managed,
}
