using System.Data;
using System.Data.Common;

namespace Turso;

/// <summary>
/// Fills a <see cref="DataSet"/> from a Turso database and writes changes back to it.
/// </summary>
/// <remarks>
/// The adapter deliberately types its commands as <see cref="DbCommand"/> so that both
/// ADO.NET surfaces in this package - <see cref="TursoConnection"/> and the
/// <c>Turso.Data.Sqlite</c> facade - can use one adapter implementation.
/// </remarks>
public sealed class TursoDataAdapter : DbDataAdapter
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TursoDataAdapter"/> class.
    /// </summary>
    public TursoDataAdapter()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TursoDataAdapter"/> class using the
    /// specified select command.
    /// </summary>
    /// <param name="selectCommand">The command used to fill the dataset.</param>
    public TursoDataAdapter(DbCommand selectCommand)
    {
        SelectCommand = selectCommand;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TursoDataAdapter"/> class using the
    /// specified select statement and connection.
    /// </summary>
    /// <param name="selectCommandText">The SQL statement used to fill the dataset.</param>
    /// <param name="connection">The connection the statement runs on.</param>
    public TursoDataAdapter(string selectCommandText, DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var command = connection.CreateCommand();
        command.CommandText = selectCommandText;
        SelectCommand = command;
    }

    /// <summary>
    /// Occurs before a command is executed against the data source for a changed row.
    /// </summary>
    public event EventHandler<TursoRowUpdatingEventArgs>? RowUpdating;

    /// <summary>
    /// Occurs after a command is executed against the data source for a changed row.
    /// </summary>
    public event EventHandler<TursoRowUpdatedEventArgs>? RowUpdated;

    /// <inheritdoc />
    protected override RowUpdatingEventArgs CreateRowUpdatingEvent(
        DataRow dataRow,
        IDbCommand? command,
        StatementType statementType,
        DataTableMapping tableMapping)
        => new TursoRowUpdatingEventArgs(dataRow, command, statementType, tableMapping);

    /// <inheritdoc />
    protected override RowUpdatedEventArgs CreateRowUpdatedEvent(
        DataRow dataRow,
        IDbCommand? command,
        StatementType statementType,
        DataTableMapping tableMapping)
        => new TursoRowUpdatedEventArgs(dataRow, command, statementType, tableMapping);

    /// <inheritdoc />
    protected override void OnRowUpdating(RowUpdatingEventArgs value)
        => RowUpdating?.Invoke(this, (TursoRowUpdatingEventArgs)value);

    /// <inheritdoc />
    protected override void OnRowUpdated(RowUpdatedEventArgs value)
        => RowUpdated?.Invoke(this, (TursoRowUpdatedEventArgs)value);
}
