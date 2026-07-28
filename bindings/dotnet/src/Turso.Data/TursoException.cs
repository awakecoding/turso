using Turso.Core;

namespace Turso;

public class TursoException : Exception
{
    public TursoException(string message) : base(message)
    {
    }

    internal TursoException(string message, Exception innerException) : base(message, innerException)
    {
    }

    internal static TursoException FromCore(EmbeddedSqlException exception)
        => new(exception.Message, exception);

    internal static TursoException FromCorePreparation(EmbeddedSqlException exception)
        => new($"Unable to prepare statement: Parse error: {exception.Message}", exception);
}
