using Ahtola.Core.Execution;

namespace Ahtola.Core.Compilation;

/// <summary>
/// Describes a single base table that a <see cref="SelectStatement"/> can scan
/// directly. The caller supplies the live row list plus a column resolver so the
/// compiler owns the scan structure while SQL semantics stay in the evaluator.
/// </summary>
/// <param name="TableName">The catalog name of the scanned table.</param>
/// <param name="Qualifier">The alias (or table name) used to qualify columns.</param>
/// <param name="Columns">The table's columns in declaration order.</param>
/// <param name="Rows">The live rows the emitted cursor iterates.</param>
/// <param name="ResolveColumnIndex">
/// Maps a (possibly qualified) column reference to its ordinal, or <c>null</c> when
/// the reference does not name a column of this table.
/// </param>
/// <param name="RowIds">The optional hidden rowids aligned with <paramref name="Rows"/>.</param>
/// <param name="IndexName">The selected logical index, when the rows are in index order.</param>
/// <param name="ColumnDefinitions">The immutable column metadata aligned with <paramref name="Columns"/>.</param>
/// <param name="QualifiedColumnDefinitions">Column metadata keyed by qualified SQL name.</param>
internal sealed record ScanTarget(
    string TableName,
    string Qualifier,
    string[] Columns,
    IReadOnlyList<SqlValue[]> Rows,
    Func<string, int?> ResolveColumnIndex,
    IReadOnlyList<long>? RowIds = null,
    string? IndexName = null,
    IReadOnlyList<EmbeddedColumn?>? ColumnDefinitions = null,
    IReadOnlyDictionary<string, EmbeddedColumn>? QualifiedColumnDefinitions = null)
{
    public bool HasRowId => RowIds is not null;
}

/// <summary>
/// A lowered <see cref="SelectStatement"/>: the emitted <see cref="VdbeProgram"/>
/// together with the live row sources its cursors iterate at execution time.
/// </summary>
internal sealed record CompiledSelect(
    VdbeProgram Program,
    IReadOnlyList<VdbeCursorSource> CursorSources,
    IReadOnlyList<int>? ParameterIndices = null);
