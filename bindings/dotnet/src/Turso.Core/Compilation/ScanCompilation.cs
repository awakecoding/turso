using Turso.Core.Execution;

namespace Turso.Core.Compilation;

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
internal sealed record ScanTarget(
    string TableName,
    string Qualifier,
    string[] Columns,
    IReadOnlyList<SqlValue[]> Rows,
    Func<string, int?> ResolveColumnIndex);

/// <summary>
/// A lowered <see cref="SelectStatement"/>: the emitted <see cref="VdbeProgram"/>
/// together with the live row sources its cursors iterate at execution time.
/// </summary>
internal sealed record CompiledSelect(
    VdbeProgram Program,
    IReadOnlyList<VdbeCursorSource> CursorSources);
