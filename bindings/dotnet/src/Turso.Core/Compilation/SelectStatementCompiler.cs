using Turso.Core.Execution;

namespace Turso.Core.Compilation;

/// <summary>
/// Raised when a statement cannot be lowered into the currently supported
/// bytecode opcode subset. Callers may catch this to fall back to another
/// execution strategy, or surface it as a hard error for <c>EXPLAIN</c>.
/// </summary>
public sealed class StatementCompilationException : InvalidOperationException
{
    public StatementCompilationException(string message) : base(message)
    {
    }
}

/// <summary>
/// Lowers a coherent subset of <see cref="SelectStatement"/> into a
/// <see cref="VdbeProgram"/> built from the supported opcode subset. Two shapes
/// are handled:
/// <list type="bullet">
///   <item>
///     a source-less projection list whose expressions fold to compile-time
///     constants, emitted as <c>LoadConstant</c> / <c>ResultRow</c> / <c>Halt</c>; and
///   </item>
///   <item>
///     a single base-table scan projecting bare or qualifying stars, bare columns, and/or constants, with an
///     optional <c>WHERE</c> filter, emitted as a real cursor loop
///     (<c>OpenReadCursor</c>, <c>Rewind</c>, <c>Column</c>, <c>Filter</c>,
///     <c>ResultRow</c>, <c>Next</c>, <c>CloseCursor</c>, <c>Halt</c>).
///   </item>
/// </list>
/// </summary>
/// <remarks>
/// The compiler intentionally rejects anything it cannot represent so the caller
/// can retain the existing tree-walking evaluator for unsupported statements. It
/// does not own SQL semantics: constant detection/folding, table resolution, and
/// predicate evaluation are supplied by the caller so the emitted program matches
/// the evaluator exactly.
/// </remarks>
internal sealed class SelectStatementCompiler
{
    private readonly Func<Expression, bool> _isConstant;
    private readonly Func<Expression, SqlValue> _fold;
    private readonly Func<TableSource, ScanTarget?> _resolveScanTarget;
    private readonly Func<Expression, ScanTarget, VdbeRowPredicate?> _compilePredicate;
    private readonly Func<Expression, ScanTarget, VdbeRowIdPredicate?> _compileRowIdPredicate;
    private readonly Func<SelectStatement, ScanTarget, VdbeRowEquality?> _compileDistinctEquality;

    public SelectStatementCompiler(
        Func<Expression, bool> isConstant,
        Func<Expression, SqlValue> fold,
        Func<TableSource, ScanTarget?> resolveScanTarget,
        Func<Expression, ScanTarget, VdbeRowPredicate?> compilePredicate,
        Func<Expression, ScanTarget, VdbeRowIdPredicate?> compileRowIdPredicate,
        Func<SelectStatement, ScanTarget, VdbeRowEquality?> compileDistinctEquality)
    {
        ArgumentNullException.ThrowIfNull(isConstant);
        ArgumentNullException.ThrowIfNull(fold);
        ArgumentNullException.ThrowIfNull(resolveScanTarget);
        ArgumentNullException.ThrowIfNull(compilePredicate);
        ArgumentNullException.ThrowIfNull(compileRowIdPredicate);
        ArgumentNullException.ThrowIfNull(compileDistinctEquality);
        _isConstant = isConstant;
        _fold = fold;
        _resolveScanTarget = resolveScanTarget;
        _compilePredicate = compilePredicate;
        _compileRowIdPredicate = compileRowIdPredicate;
        _compileDistinctEquality = compileDistinctEquality;
    }

    /// <summary>
    /// Attempts to lower <paramref name="statement"/> into a runnable program.
    /// Returns <see langword="false"/> (leaving <paramref name="compiled"/> null)
    /// when the statement falls outside the supported subset.
    /// </summary>
    public bool TryCompile(SelectStatement statement, out CompiledSelect compiled)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return statement.Source is null
            ? TryCompileConstant(statement, out compiled)
            : TryCompileScan(statement, out compiled);
    }

    private bool TryCompileConstant(SelectStatement statement, out CompiledSelect compiled)
    {
        compiled = null!;
        if (!IsConstantProjection(statement))
            return false;

        var registerCount = statement.Projections.Count;
        var instructions = new List<VdbeInstruction>(registerCount + 2);

        // Fold each projection to a constant and load it into its own register.
        for (var index = 0; index < registerCount; index++)
        {
            instructions.Add(new LoadConstantInstruction(
                new Register(index),
                _fold(statement.Projections[index].Expression)));
        }

        // Emit the single result row spanning the loaded registers, then halt.
        instructions.Add(new ResultRowInstruction(new RegisterRange(new Register(0), registerCount)));
        instructions.Add(new HaltInstruction());

        compiled = new CompiledSelect(
            new VdbeProgram(registerCount, cursorCount: 0, instructions),
            []);
        return true;
    }

    // A source-less SELECT with no clauses beyond a projection list whose every
    // projection is a compile-time-constant scalar expression.
    private bool IsConstantProjection(SelectStatement statement)
    {
        if (statement.Where is not null
            || statement.Having is not null
            || statement.Distinct
            || statement.GroupBy.Count != 0
            || statement.OrderBy.Count != 0
            || statement.Limit is not null
            || statement.Offset is not null
            || statement.Projections.Count == 0)
        {
            return false;
        }

        foreach (var projection in statement.Projections)
        {
            if (projection.Expression is StarExpression or QualifiedStarExpression
                || !_isConstant(projection.Expression))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryCompileScan(SelectStatement statement, out CompiledSelect compiled)
    {
        compiled = null!;

        // The scan subset covers only the row-at-a-time pipeline. Clauses that
        // reshape or reorder the result set stay with the tree-walking evaluator.
        if (statement.Having is not null
            || statement.GroupBy.Count != 0
            || statement.OrderBy.Count != 0
            || statement.Limit is not null
            || statement.Offset is not null
            || statement.Projections.Count == 0)
        {
            return false;
        }

        var target = _resolveScanTarget(statement.Source!);
        if (target is null)
            return false;

        VdbeRowEquality? distinctEquality = null;
        if (statement.Distinct)
        {
            distinctEquality = _compileDistinctEquality(statement, target);
            if (distinctEquality is null)
                return false;
        }

        // Lower every projection into a column read or a folded constant load.
        var projectionOps = new List<ProjectionOp>();
        foreach (var projection in statement.Projections)
        {
            if (!TryLowerProjection(projection.Expression, target, projectionOps))
                return false;
        }

        if (projectionOps.Count == 0)
            return false;

        VdbeRowPredicate? predicate = null;
        VdbeRowIdPredicate? rowIdPredicate = null;
        if (statement.Where is not null)
        {
            predicate = _compilePredicate(statement.Where, target);
            if (predicate is null)
            {
                rowIdPredicate = _compileRowIdPredicate(statement.Where, target);
            }

            if (predicate is null && rowIdPredicate is null)
                return false;
        }

        var program = BuildScanProgram(target, projectionOps, predicate, rowIdPredicate, distinctEquality);
        compiled = new CompiledSelect(program, [new VdbeCursorSource(target.Rows, target.RowIds)]);
        return true;
    }

    private bool TryLowerProjection(Expression expression, ScanTarget target, List<ProjectionOp> ops)
    {
        switch (expression)
        {
            case StarExpression:
                // "*" expands to every column of the single scanned table, in order.
                if (target.Columns.Length == 0)
                    return false;

                for (var index = 0; index < target.Columns.Length; index++)
                    ops.Add(ProjectionOp.ForColumn(index));
                return true;
            case ColumnExpression column when target.ResolveColumnIndex(column.Name) is { } columnIndex:
                ops.Add(ProjectionOp.ForColumn(columnIndex));
                return true;
            case ColumnExpression column when IsTargetRowIdReference(column, target):
                ops.Add(ProjectionOp.ForRowId());
                return true;
            case QualifiedStarExpression qualifiedStar
                when string.Equals(qualifiedStar.Qualifier, target.Qualifier, StringComparison.OrdinalIgnoreCase):
                // A single scan has exactly one raw output shape, so its resolved qualifier expands
                // to the same declared-column sequence as the evaluator.
                if (target.Columns.Length == 0)
                    return false;

                for (var index = 0; index < target.Columns.Length; index++)
                    ops.Add(ProjectionOp.ForColumn(index));
                return true;
            case QualifiedStarExpression:
                // An unmatched qualifier must reach the evaluator, which owns its diagnostic.
                return false;
            default:
                if (_isConstant(expression))
                {
                    ops.Add(ProjectionOp.ForConstant(_fold(expression)));
                    return true;
                }

                return false;
        }
    }

    private static bool IsTargetRowIdReference(ColumnExpression column, ScanTarget target)
    {
        if (!target.HasRowId || target.ResolveColumnIndex(column.Name) is not null)
            return false;

        var separator = column.Name.IndexOf('.');
        var bareName = separator < 0 ? column.Name : column.Name[(separator + 1)..];
        return EmbeddedTable.IsRowidAliasName(bareName)
            && (separator < 0
                || string.Equals(
                    column.Name[..separator],
                    target.Qualifier,
                    StringComparison.OrdinalIgnoreCase));
    }

    private static VdbeProgram BuildScanProgram(
        ScanTarget target,
        IReadOnlyList<ProjectionOp> projectionOps,
        VdbeRowPredicate? predicate,
        VdbeRowIdPredicate? rowIdPredicate,
        VdbeRowEquality? distinctEquality)
    {
        if (predicate is not null && rowIdPredicate is not null)
            throw new ArgumentException("A scan can have either a row predicate or a rowid predicate.");

        var cursor = new Cursor(0);
        var registerCount = projectionOps.Count;
        var filterCount = predicate is null && rowIdPredicate is null ? 0 : 1;

        // Fixed layout so jump targets can be computed up front:
        //   0            OpenReadCursor
        //   1            Rewind        -> closeAddr (empty table)
        //   loopStart    [Filter       -> nextAddr]  (when WHERE present)
        //   bodyStart..  Column / LoadConstant per output register
        //   resultRow    ResultRow r[0..registerCount-1]
        //   nextAddr     Next          -> loopStart
        //   closeAddr    CloseCursor
        //   haltAddr     Halt
        var loopStart = 2;
        var bodyStart = loopStart + filterCount;
        var resultRowAddr = bodyStart + registerCount;
        var nextAddr = resultRowAddr + 1;
        var closeAddr = nextAddr + 1;

        var instructions = new List<VdbeInstruction>(closeAddr + 2)
        {
            new OpenReadCursorInstruction(cursor, target.TableName, target.Columns.Length),
            new RewindCursorInstruction(cursor, new ProgramCounter(closeAddr)),
        };

        if (predicate is not null)
        {
            instructions.Add(new FilterInstruction(
                cursor,
                predicate,
                new ProgramCounter(nextAddr),
                $"skip row when WHERE is false, goto {nextAddr}"));
        }
        else if (rowIdPredicate is not null)
        {
            instructions.Add(new FilterRowIdInstruction(
                cursor,
                rowIdPredicate,
                new ProgramCounter(nextAddr),
                $"skip row when WHERE is false, goto {nextAddr}"));
        }

        for (var register = 0; register < registerCount; register++)
        {
            var op = projectionOps[register];
            instructions.Add(op.Kind switch
            {
                ProjectionKind.Column => new ColumnInstruction(cursor, op.ColumnIndex, new Register(register)),
                ProjectionKind.RowId => new RowIdInstruction(cursor, new Register(register)),
                ProjectionKind.Constant => new LoadConstantInstruction(new Register(register), op.Constant),
                _ => throw new InvalidOperationException($"Unsupported projection kind {op.Kind}."),
            });
        }

        var resultRange = new RegisterRange(new Register(0), registerCount);
        instructions.Add(distinctEquality is null
            ? new ResultRowInstruction(resultRange)
            : new DistinctResultRowInstruction(resultRange, distinctEquality, DistinctSetIndex: 0));
        instructions.Add(new NextInstruction(cursor, new ProgramCounter(loopStart)));
        instructions.Add(new CloseCursorInstruction(cursor));
        instructions.Add(new HaltInstruction());

        return new VdbeProgram(
            registerCount,
            cursorCount: 1,
            instructions,
            distinctSetCount: distinctEquality is null ? 0 : 1);
    }

    // One emitted output register: either a column read from the cursor row or a
    // folded compile-time constant.
    private enum ProjectionKind
    {
        Column,
        RowId,
        Constant,
    }

    private readonly record struct ProjectionOp(ProjectionKind Kind, int ColumnIndex, SqlValue Constant)
    {
        public static ProjectionOp ForColumn(int columnIndex) => new(ProjectionKind.Column, columnIndex, default);

        public static ProjectionOp ForRowId() => new(ProjectionKind.RowId, 0, default);

        public static ProjectionOp ForConstant(SqlValue value) => new(ProjectionKind.Constant, 0, value);
    }
}
