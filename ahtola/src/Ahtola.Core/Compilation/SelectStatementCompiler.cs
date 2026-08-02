using Ahtola.Core.Execution;
using Ahtola.Core.Storage;

namespace Ahtola.Core.Compilation;

public sealed class StatementCompilationException : InvalidOperationException
{
    public StatementCompilationException(string message) : base(message)
    {
    }
}

/// <summary>
/// Lowers source-less projections and single-table scans into executable VDBE programs. Projection
/// expressions support constants, late-bound parameters, columns/rowid, nested arithmetic, and supported
/// scalar functions; unsupported expression families cause the whole statement to remain on the evaluator.
/// </summary>
internal sealed class SelectStatementCompiler
{
    private readonly Func<Expression, bool> _isConstant;
    private readonly Func<Expression, SqlValue> _fold;
    private readonly Func<TableSource, ScanTarget?> _resolveScanTarget;
    private readonly Func<Expression, ScanTarget, VdbeRowPredicate?> _compilePredicate;
    private readonly Func<Expression, ScanTarget, bool> _canEmitNativePredicate;
    private readonly Func<Expression, ScanTarget, VdbeRowIdPredicate?> _compileRowIdPredicate;
    private readonly Func<SelectStatement, ScanTarget, VdbeRowEquality?> _compileDistinctEquality;
    private readonly Func<FunctionExpression, VdbeScalarFunction?> _compileScalarFunction;
    private readonly VdbeNumericAffinity _numericAffinity;
    private readonly VdbeNumericAffinity _moduloAffinity;
    private readonly VdbeNumericAffinity _integerAffinity;

    public SelectStatementCompiler(
        Func<Expression, bool> isConstant,
        Func<Expression, SqlValue> fold,
        Func<TableSource, ScanTarget?> resolveScanTarget,
        Func<Expression, ScanTarget, VdbeRowPredicate?> compilePredicate,
        Func<Expression, ScanTarget, bool> canEmitNativePredicate,
        Func<Expression, ScanTarget, VdbeRowIdPredicate?> compileRowIdPredicate,
        Func<SelectStatement, ScanTarget, VdbeRowEquality?> compileDistinctEquality,
        Func<FunctionExpression, VdbeScalarFunction?> compileScalarFunction,
        VdbeNumericAffinity numericAffinity,
        VdbeNumericAffinity moduloAffinity,
        VdbeNumericAffinity integerAffinity)
    {
        ArgumentNullException.ThrowIfNull(isConstant);
        ArgumentNullException.ThrowIfNull(fold);
        ArgumentNullException.ThrowIfNull(resolveScanTarget);
        ArgumentNullException.ThrowIfNull(compilePredicate);
        ArgumentNullException.ThrowIfNull(canEmitNativePredicate);
        ArgumentNullException.ThrowIfNull(compileRowIdPredicate);
        ArgumentNullException.ThrowIfNull(compileDistinctEquality);
        ArgumentNullException.ThrowIfNull(compileScalarFunction);
        ArgumentNullException.ThrowIfNull(numericAffinity);
        ArgumentNullException.ThrowIfNull(moduloAffinity);
        ArgumentNullException.ThrowIfNull(integerAffinity);
        _isConstant = isConstant;
        _fold = fold;
        _resolveScanTarget = resolveScanTarget;
        _compilePredicate = compilePredicate;
        _canEmitNativePredicate = canEmitNativePredicate;
        _compileRowIdPredicate = compileRowIdPredicate;
        _compileDistinctEquality = compileDistinctEquality;
        _compileScalarFunction = compileScalarFunction;
        _numericAffinity = numericAffinity;
        _moduloAffinity = moduloAffinity;
        _integerAffinity = integerAffinity;
    }

    public bool TryCompile(SelectStatement statement, out CompiledSelect compiled)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return statement.Source is null
            ? TryCompileSourceLess(statement, out compiled)
            : TryCompileScan(statement, out compiled);
    }

    private bool TryCompileSourceLess(SelectStatement statement, out CompiledSelect compiled)
    {
        compiled = null!;
        if (statement.Where is not null
            || statement.Having is not null
            || statement.Distinct
            || statement.GroupBy.Count != 0
            || statement.OrderBy.Count != 0
            || statement.Limit is not null
            || statement.Offset is not null
            || statement.Projections.Count == 0
            || statement.Projections.Any(projection =>
                projection.Expression is StarExpression or QualifiedStarExpression))
        {
            return false;
        }

        var outputCount = statement.Projections.Count;
        var body = new List<VdbeInstruction>();
        var emitter = CreateEmitter(target: null, cursor: null, outputCount, body, programCounterBase: 0);
        for (var index = 0; index < outputCount; index++)
        {
            if (!emitter.TryEmit(statement.Projections[index].Expression, new Register(index)))
                return false;
        }

        body.Add(new ResultRowInstruction(new RegisterRange(new Register(0), outputCount)));
        body.Add(new HaltInstruction());
        compiled = new CompiledSelect(
            new VdbeProgram(
                emitter.RegisterCount,
                cursorCount: 0,
                body,
                parameterSlotCount: emitter.ParameterIndices.Count),
            [],
            emitter.ParameterIndices);
        return true;
    }

    private bool TryCompileScan(SelectStatement statement, out CompiledSelect compiled)
    {
        compiled = null!;

        // P1-21 reverse traversal: `ORDER BY rowid DESC` on a single table with no
        // WHERE/GROUP BY/HAVING/LIMIT/OFFSET/DISTINCT lowers to a backward table scan
        // (Last/Prev) instead of the sorter route, so rows are visited in descending
        // rowid order without materializing a sorter. Only the bare rowid/_rowid_/oid
        // reference is detected here; the INTEGER-PK-alias column name (e.g. `id`)
        // resolves to a declared column (`IsTargetRowIdReference` returns false) and
        // stays on the sorter path. Index-backed backward walks need the TableAccessPlan
        // optimizer seam (absent) and are intentionally not handled. The cursor walks
        // positions backward, which is rowid-descending ONLY when RowIds is ascending
        // (CommitInserts appends batch rowids in INSERT order without re-sorting rowid
        // tables) — `RowIdsAreAscending` guards that invariant, declining to the sorter
        // when explicit out-of-order rowid INSERTs broke the sort.
        if (statement.OrderBy.Count == 1
            && statement.OrderBy[0].Descending
            && statement.Where is null
            && statement.GroupBy.Count == 0
            && statement.Having is null
            && statement.Limit is null
            && statement.Offset is null
            && !statement.Distinct
            && _resolveScanTarget(statement.Source!) is { } descTarget
            && descTarget.HasRowId
            && RowIdsAreAscending(descTarget.RowIds)
            && statement.OrderBy[0].Expression is ColumnExpression descColumn
            && IsTargetRowIdReference(descColumn, descTarget))
        {
            return TryCompileReverseRowidScan(statement, descTarget, out compiled);
        }

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

        if (!TryExpandProjections(statement.Projections, target, out var projections))
            return false;

        // Step 2: rowid-equality seek. When WHERE is `rowid = <int literal>` (bare rowid
        // or table-qualified, integer literal), emit a SeekRowid point-lookup instead of
        // a full scan + FilterRowId. Parameters (runtime-typed — a text-bound @p would
        // diverge: SeekRowid jumps NotFound while the scan coerces via affinity), rowid-
        // ALIAS equality (`id = N` on INTEGER PRIMARY KEY — IsTargetRowIdReference returns
        // false for declared columns), and range predicates (`>`, `BETWEEN` — Step 3)
        // stay on the scan path: still correct, just not seek-optimized. Distinct is gated
        // out (would need DistinctResultRow + a distinct set); the top-of-method guards
        // already exclude GroupBy/Having/OrderBy/Limit/Offset.
        if (!statement.Distinct
            && TryGetRowIdSeekOperand(statement.Where, target, out var rowIdValue))
        {
            var seekCursor = new Cursor(0);
            var seekBody = new List<VdbeInstruction>();
            // OpenRead(0), rhsLoad(1), SeekRowid(2), projections(3..), ResultRow, Close, Halt.
            // programCounterBase = 2 makes projection-internal jump targets line up: the
            // rhs load occupies body[0] with a phantom pc of 2 (harmless — LoadConstant has
            // no jumps), so projections start at body[1] -> pc 3, matching their actual slot
            // after the SeekRowid instruction inserted at pc 2.
            const int seekProgramCounterBase = 2;
            var seekEmitter = CreateEmitter(
                target,
                seekCursor,
                projections.Count + 1,
                seekBody,
                seekProgramCounterBase);
            var rowIdRegister = new Register(projections.Count);
            if (!seekEmitter.TryEmit(rowIdValue, rowIdRegister))
                return false;

            for (var index = 0; index < projections.Count; index++)
            {
                var projection = projections[index];
                if (projection.ColumnIndex is { } columnIndex)
                    seekBody.Add(new ColumnInstruction(seekCursor, columnIndex, new Register(index)));
                else if (!seekEmitter.TryEmit(projection.Expression!, new Register(index)))
                    return false;
            }

            if (seekBody.Count == 0 || seekBody[0] is not LoadConstantInstruction rhsLoad)
                return false;

            seekBody.RemoveAt(0);
            var projectionCount = seekBody.Count;
            var notFoundTarget = new ProgramCounter(projectionCount + 4);
            var seekInstructions = new List<VdbeInstruction>(projectionCount + 6)
            {
                new OpenReadCursorInstruction(
                    seekCursor,
                    target.IndexName is null
                        ? target.TableName
                        : $"{target.TableName} USING INDEX {target.IndexName}",
                    target.Columns.Length),
                rhsLoad,
                new SeekRowidInstruction(
                    seekCursor,
                    rowIdRegister,
                    notFoundTarget,
                    $"seek cursor {seekCursor.Index} to rowid r[{rowIdRegister.Index}], goto {notFoundTarget.Offset} if not found"),
            };
            seekInstructions.AddRange(seekBody);
            seekInstructions.Add(new ResultRowInstruction(new RegisterRange(new Register(0), projections.Count)));
            seekInstructions.Add(new CloseCursorInstruction(seekCursor));
            seekInstructions.Add(new HaltInstruction());

            compiled = new CompiledSelect(
                new VdbeProgram(
                    Math.Max(seekEmitter.RegisterCount, rowIdRegister.Index + 1),
                    cursorCount: 1,
                    seekInstructions,
                    parameterSlotCount: seekEmitter.ParameterIndices.Count),
                [new VdbeCursorSource(target.Rows, target.RowIds)],
                seekEmitter.ParameterIndices);
            return true;
        }

        // Step 3: rowid-range seek. When WHERE is `rowid >|>=|<|<= <int literal>` (or the
        // swapped form) or `rowid BETWEEN <int literal> AND <int literal>` (non-negated),
        // emit a SeekRowidRange that lands on the first row whose rowid satisfies the start
        // predicate, followed by a FilterRowId enforcing the full WHERE over the matching
        // range, then the projection loop. Bounds must be integer literals (a text literal
        // '2' coerces via affinity on the scan path, so seeking on it would diverge; a
        // late-bound parameter is runtime-typed for the same reason). The same Distinct /
        // GroupBy / Having / OrderBy / Limit / Offset gates as Step 2 apply.
        if (!statement.Distinct
            && TryGetRowIdRangeSeekOperand(statement.Where, target, out var startBound, out var startOp, out var endBound, out var endOp))
        {
            var seekCursor = new Cursor(0);
            var seekBody = new List<VdbeInstruction>();
            // OpenRead(0), LoadConstant(start)(1), [LoadConstant(end)(2)], SeekRowidRange,
            // FilterRowId, projections.., ResultRow, Next(loopTarget=FilterRowId), Close, Halt.
            // programCounterBase = prefixCount + 2 so projection-internal jump targets line up
            // with their actual slots after the SeekRowidRange + FilterRowId instructions.
            var hasEnd = endBound is not null;
            var prefixCount = 2 + (hasEnd ? 1 : 0);
            var baseForProjections = prefixCount + 2;
            var seekEmitter = CreateEmitter(
                target,
                seekCursor,
                projections.Count + (hasEnd ? 2 : 1),
                seekBody,
                baseForProjections);
            var startRowIdRegister = new Register(projections.Count);
            Register? endRowIdRegister = hasEnd ? new Register(projections.Count + 1) : null;

            for (var index = 0; index < projections.Count; index++)
            {
                var projection = projections[index];
                if (projection.ColumnIndex is { } columnIndex)
                    seekBody.Add(new ColumnInstruction(seekCursor, columnIndex, new Register(index)));
                else if (!seekEmitter.TryEmit(projection.Expression!, new Register(index)))
                    return false;
            }

            // Fold the integer-literal bounds to longs for the FilterRowId closure. The bounds
            // are validated as SqlValueKind.Integer literals by TryGetRowIdRangeSeekOperand, so
            // no affinity/coercion path is exercised (a text '2' stays on the scan path).
            var startValue = ((LiteralExpression)startBound).Value.AsInteger();
            long? endValue = endBound is LiteralExpression endLiteral
                ? endLiteral.Value.AsInteger()
                : null;
            var capturedStartOp = startOp;
            var capturedEndOp = endOp;
            VdbeRowIdPredicate rangePredicate = (row, rowId) =>
            {
                if (!SatisfiesRowId(rowId, capturedStartOp, startValue))
                    return false;
                if (endValue is { } upper && capturedEndOp is { } upperOp
                    && !SatisfiesRowId(rowId, upperOp, upper))
                    return false;
                return true;
            };

            var rangeFilterAddr = prefixCount + 1;
            var rangeResultRowAddr = baseForProjections + seekBody.Count;
            var rangeNextAddr = rangeResultRowAddr + 1;
            var rangeCloseAddr = rangeNextAddr + 1;
            var seekInstructions = new List<VdbeInstruction>(rangeCloseAddr + 2)
            {
                new OpenReadCursorInstruction(
                    seekCursor,
                    target.IndexName is null
                        ? target.TableName
                        : $"{target.TableName} USING INDEX {target.IndexName}",
                    target.Columns.Length),
                new LoadConstantInstruction(startRowIdRegister, ((LiteralExpression)startBound).Value),
            };
            if (hasEnd)
                seekInstructions.Add(new LoadConstantInstruction(endRowIdRegister!.Value, ((LiteralExpression)endBound!).Value));
            seekInstructions.Add(new SeekRowidRangeInstruction(
                seekCursor,
                startRowIdRegister,
                startOp,
                endRowIdRegister,
                endOp,
                new ProgramCounter(rangeCloseAddr),
                $"seek cursor {seekCursor.Index} to first rowid r[{startRowIdRegister.Index}] {DescribeOperator(startOp)}{DescribeBound(endOp)}"));
            seekInstructions.Add(new FilterRowIdInstruction(
                seekCursor,
                rangePredicate,
                new ProgramCounter(rangeNextAddr),
                $"skip row when range WHERE is false, goto {rangeNextAddr}"));
            seekInstructions.AddRange(seekBody);
            seekInstructions.Add(new ResultRowInstruction(new RegisterRange(new Register(0), projections.Count)));
            seekInstructions.Add(new NextInstruction(seekCursor, new ProgramCounter(rangeFilterAddr)));
            seekInstructions.Add(new CloseCursorInstruction(seekCursor));
            seekInstructions.Add(new HaltInstruction());

            compiled = new CompiledSelect(
                new VdbeProgram(
                    Math.Max(seekEmitter.RegisterCount, (hasEnd ? 2 : 1) + projections.Count),
                    cursorCount: 1,
                    seekInstructions,
                    parameterSlotCount: seekEmitter.ParameterIndices.Count),
                [new VdbeCursorSource(target.Rows, target.RowIds)],
                seekEmitter.ParameterIndices);
            return true;
        }

        VdbeRowPredicate? predicate = null;
        VdbeRowIdPredicate? rowIdPredicate = null;
        Register? predicateRegister = null;
        var predicateInstructionCount = 0;
        var cursor = new Cursor(0);
        var body = new List<VdbeInstruction>();
        var nativePredicateRequested = statement.Where is not null
            && _canEmitNativePredicate(statement.Where, target);
        const int loopStart = 2;
        var bodyStart = loopStart + (statement.Where is null || nativePredicateRequested ? 0 : 1);
        var emitter = CreateEmitter(
            target,
            cursor,
            projections.Count,
            body,
            bodyStart + (nativePredicateRequested ? 1 : 0));
        if (statement.Where is not null)
        {
            if (nativePredicateRequested)
            {
                if (!emitter.CanEmitNativePredicate(statement.Where)
                    || !emitter.TryEmitPredicate(statement.Where, out var emittedPredicate))
                {
                    return false;
                }

                predicateRegister = emittedPredicate;
                predicateInstructionCount = body.Count;
            }
            else
            {
                predicate = _compilePredicate(statement.Where, target);
            }

            if (predicate is null && predicateRegister is null)
                rowIdPredicate = _compileRowIdPredicate(statement.Where, target);
            if (predicate is null && predicateRegister is null && rowIdPredicate is null)
                return false;
        }

        for (var index = 0; index < projections.Count; index++)
        {
            var projection = projections[index];
            if (projection.ColumnIndex is { } columnIndex)
            {
                body.Add(new ColumnInstruction(cursor, columnIndex, new Register(index)));
            }
            else if (!emitter.TryEmit(projection.Expression!, new Register(index)))
            {
                return false;
            }
        }

        var filterCount = bodyStart - loopStart;
        var resultRowAddr = loopStart + filterCount + body.Count + (predicateRegister is null ? 0 : 1);
        var nextAddr = resultRowAddr + 1;
        var closeAddr = nextAddr + 1;
        if (predicateRegister is { } register)
            body.Insert(
                predicateInstructionCount,
                new JumpIfNotTrueInstruction(register, new ProgramCounter(nextAddr)));
        var instructions = new List<VdbeInstruction>(closeAddr + 2)
        {
            new OpenReadCursorInstruction(
                cursor,
                target.IndexName is null
                    ? target.TableName
                    : $"{target.TableName} USING INDEX {target.IndexName}",
                target.Columns.Length),
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

        instructions.AddRange(body);
        var output = new RegisterRange(new Register(0), projections.Count);
        instructions.Add(distinctEquality is null
            ? new ResultRowInstruction(output)
            : new DistinctResultRowInstruction(output, distinctEquality, DistinctSetIndex: 0));
        instructions.Add(new NextInstruction(cursor, new ProgramCounter(loopStart)));
        instructions.Add(new CloseCursorInstruction(cursor));
        instructions.Add(new HaltInstruction());

        compiled = new CompiledSelect(
            new VdbeProgram(
                emitter.RegisterCount,
                cursorCount: 1,
                instructions,
                distinctSetCount: distinctEquality is null ? 0 : 1,
                parameterSlotCount: emitter.ParameterIndices.Count),
            [new VdbeCursorSource(target.Rows, target.RowIds)],
            emitter.ParameterIndices);
        return true;
    }

    /// <summary>
    /// Compiles an <c>ORDER BY rowid DESC</c> single-table scan as a backward table scan
    /// (<c>Last</c>/<c>Prev</c>) instead of the sorter route. Mirrors the forward-scan
    /// path of <see cref="TryCompileScan"/> but emits <c>Last</c>/<c>Prev</c> in place of
    /// <c>Rewind</c>/<c>Next</c> and carries no WHERE/predicate (the gate excludes it).
    /// </summary>
    private bool TryCompileReverseRowidScan(SelectStatement statement, ScanTarget target, out CompiledSelect compiled)
    {
        compiled = null!;
        if (!TryExpandProjections(statement.Projections, target, out var projections))
            return false;

        var cursor = new Cursor(0);
        var body = new List<VdbeInstruction>();
        const int loopStart = 2;
        var emitter = CreateEmitter(target, cursor, projections.Count, body, loopStart);

        for (var index = 0; index < projections.Count; index++)
        {
            var projection = projections[index];
            if (projection.ColumnIndex is { } columnIndex)
            {
                body.Add(new ColumnInstruction(cursor, columnIndex, new Register(index)));
            }
            else if (!emitter.TryEmit(projection.Expression!, new Register(index)))
            {
                return false;
            }
        }

        var resultRowAddr = loopStart + body.Count;
        var nextAddr = resultRowAddr + 1;
        var closeAddr = nextAddr + 1;
        var instructions = new List<VdbeInstruction>(closeAddr + 2)
        {
            new OpenReadCursorInstruction(
                cursor,
                target.IndexName is null
                    ? target.TableName
                    : $"{target.TableName} USING INDEX {target.IndexName}",
                target.Columns.Length),
            new LastCursorInstruction(cursor, new ProgramCounter(closeAddr)),
        };

        instructions.AddRange(body);
        var output = new RegisterRange(new Register(0), projections.Count);
        instructions.Add(new ResultRowInstruction(output));
        instructions.Add(new PrevInstruction(cursor, new ProgramCounter(loopStart)));
        instructions.Add(new CloseCursorInstruction(cursor));
        instructions.Add(new HaltInstruction());

        compiled = new CompiledSelect(
            new VdbeProgram(
                emitter.RegisterCount,
                cursorCount: 1,
                instructions,
                distinctSetCount: 0,
                parameterSlotCount: emitter.ParameterIndices.Count),
            [new VdbeCursorSource(target.Rows, target.RowIds)],
            emitter.ParameterIndices);
        return true;
    }

    private ExpressionEmitter CreateEmitter(
        ScanTarget? target,
        Cursor? cursor,
        int outputCount,
        List<VdbeInstruction> instructions,
        int programCounterBase)
        => new(
            target,
            cursor,
            outputCount,
            instructions,
            programCounterBase,
            allowControlFlow: true,
            _isConstant,
            _fold,
            _compileScalarFunction,
            _numericAffinity,
            _moduloAffinity,
            _integerAffinity);

    internal static bool TryExpandProjections(
        IReadOnlyList<Projection> source,
        ScanTarget target,
        out List<ProjectionSource> expanded)
    {
        expanded = new List<ProjectionSource>();
        foreach (var projection in source)
        {
            switch (projection.Expression)
            {
                case StarExpression:
                    if (target.Columns.Length == 0)
                        return false;
                    for (var index = 0; index < target.Columns.Length; index++)
                        expanded.Add(ProjectionSource.ForColumn(index));
                    break;
                case QualifiedStarExpression qualified
                    when string.Equals(qualified.Qualifier, target.Qualifier, StringComparison.OrdinalIgnoreCase):
                    if (target.Columns.Length == 0)
                        return false;
                    for (var index = 0; index < target.Columns.Length; index++)
                        expanded.Add(ProjectionSource.ForColumn(index));
                    break;
                case QualifiedStarExpression:
                    return false;
                default:
                    expanded.Add(ProjectionSource.ForExpression(projection.Expression));
                    break;
            }
        }

        return expanded.Count != 0;
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

    /// <summary>
    /// The reverse rowid scan walks cursor positions backward (Last/Prev), which yields
    /// rowid-descending order ONLY when <paramref name="rowIds"/> is stored ascending.
    /// CommitInserts appends batch rowids in INSERT order without re-sorting rowid
    /// tables, so explicit out-of-order rowid INSERTs leave the list unsorted; emitting
    /// Last/Prev against that would silently produce reverse-INSERT order (a divergence
    /// from SQLite's backward B-tree walk). The single-row insert path keeps the list
    /// sorted, and auto-rowid batch inserts are monotonic, so the common case is
    /// ascending — the check is O(n) per compile of an <c>ORDER BY rowid DESC</c> query,
    /// strictly cheaper than the O(n log n) sort it avoids. When this returns false the
    /// gate declines and the caller routes the statement through the sorter (correct,
    /// just not optimized).
    /// </summary>
    private static bool RowIdsAreAscending(IReadOnlyList<long>? rowIds)
    {
        if (rowIds is null || rowIds.Count <= 1)
            return true;

        for (var index = 1; index < rowIds.Count; index++)
        {
            if (rowIds[index] <= rowIds[index - 1])
                return false;
        }

        return true;
    }

    /// <summary>
    /// Detects a `rowid = integer-literal` (or `integer-literal = rowid`) equality predicate
    /// eligible for the SeekRowid point-lookup fast path. The rhs must be an integer
    /// literal known at compile time: a text literal '2' coerces to 2 via INTEGER
    /// affinity in the scan path today, so seeking on it would diverge (SeekRowid jumps
    /// NotFound while the scan matches); a late-bound parameter is runtime-typed and is
    /// left to the scan for the same reason. `IS` is treated like `=` (IS with an
    /// integer is equality).
    /// </summary>
    private static bool TryGetRowIdSeekOperand(Expression? where, ScanTarget target, out Expression rowIdValue)
    {
        rowIdValue = null!;
        if (where is not BinaryExpression { Operator: BinaryOperator.Equal or BinaryOperator.Is } binary)
            return false;

        if (binary.Left is ColumnExpression leftColumn
            && IsTargetRowIdReference(leftColumn, target)
            && binary.Right is LiteralExpression rightLiteral
            && rightLiteral.Value.Kind == SqlValueKind.Integer)
        {
            rowIdValue = binary.Right;
            return true;
        }

        if (binary.Right is ColumnExpression rightColumn
            && IsTargetRowIdReference(rightColumn, target)
            && binary.Left is LiteralExpression leftLiteral
            && leftLiteral.Value.Kind == SqlValueKind.Integer)
        {
            rowIdValue = binary.Left;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Detects a rowid range predicate eligible for the SeekRowidRange fast path:
    /// `rowid &gt;|&gt;=|&lt;|&lt;= &lt;int-literal&gt;` (or the swapped form) or
    /// `rowid BETWEEN &lt;int-literal&gt; AND &lt;int-literal&gt;` (non-negated). The
    /// bounds must be integer literals known at compile time: a text literal '2'
    /// coerces via affinity in the scan path today, so seeking on it would diverge
    /// (SeekRowidRange skips non-matching start positions while the scan coerces); a
    /// late-bound parameter is runtime-typed and stays on the scan for the same
    /// reason. Negated BETWEEN (`NOT BETWEEN`) is excluded because it is a
    /// disjunction of two open ranges, not a single contiguous range. Returns the
    /// start bound expression + operator and, for BETWEEN, the end bound + operator.
    /// </summary>
    private static bool TryGetRowIdRangeSeekOperand(
        Expression? where,
        ScanTarget target,
        out Expression startBound,
        out VdbeComparisonOperator startOp,
        out Expression? endBound,
        out VdbeComparisonOperator? endOp)
    {
        startBound = null!;
        startOp = default;
        endBound = null;
        endOp = null;

        if (where is BetweenExpression { Negated: false } between
            && between.Value is ColumnExpression betweenColumn
            && IsTargetRowIdReference(betweenColumn, target)
            && between.Lower is LiteralExpression lowerLiteral
            && lowerLiteral.Value.Kind == SqlValueKind.Integer
            && between.Upper is LiteralExpression upperLiteral
            && upperLiteral.Value.Kind == SqlValueKind.Integer)
        {
            startBound = between.Lower;
            startOp = VdbeComparisonOperator.GreaterThanOrEqual;
            endBound = between.Upper;
            endOp = VdbeComparisonOperator.LessThanOrEqual;
            return true;
        }

        if (where is not BinaryExpression { Operator: BinaryOperator.GreaterThan
                                                    or BinaryOperator.GreaterThanOrEqual
                                                    or BinaryOperator.LessThan
                                                    or BinaryOperator.LessThanOrEqual } binary)
            return false;

        var directOp = binary.Operator switch
        {
            BinaryOperator.GreaterThan => VdbeComparisonOperator.GreaterThan,
            BinaryOperator.GreaterThanOrEqual => VdbeComparisonOperator.GreaterThanOrEqual,
            BinaryOperator.LessThan => VdbeComparisonOperator.LessThan,
            BinaryOperator.LessThanOrEqual => VdbeComparisonOperator.LessThanOrEqual,
            _ => (VdbeComparisonOperator?)null,
        };
        if (directOp is not { } directComparison)
            return false;

        // `rowid OP literal`: the operator applies directly.
        if (binary.Left is ColumnExpression leftColumn
            && IsTargetRowIdReference(leftColumn, target)
            && binary.Right is LiteralExpression rightLiteral
            && rightLiteral.Value.Kind == SqlValueKind.Integer)
        {
            startBound = binary.Right;
            startOp = directComparison;
            return true;
        }

        // `literal OP rowid`: flip the operator so the rowid is on the left of the
        // comparison the SeekRowidRange handler evaluates (Satisfies(rowId, op, bound)).
        if (binary.Right is ColumnExpression rightColumn
            && IsTargetRowIdReference(rightColumn, target)
            && binary.Left is LiteralExpression leftLiteral
            && leftLiteral.Value.Kind == SqlValueKind.Integer)
        {
            startBound = binary.Left;
            startOp = FlipComparisonOperator(directComparison);
            return true;
        }

        return false;
    }

    private static VdbeComparisonOperator FlipComparisonOperator(VdbeComparisonOperator op)
        => op switch
        {
            VdbeComparisonOperator.GreaterThan => VdbeComparisonOperator.LessThan,
            VdbeComparisonOperator.GreaterThanOrEqual => VdbeComparisonOperator.LessThanOrEqual,
            VdbeComparisonOperator.LessThan => VdbeComparisonOperator.GreaterThan,
            VdbeComparisonOperator.LessThanOrEqual => VdbeComparisonOperator.GreaterThanOrEqual,
            VdbeComparisonOperator.Equal => VdbeComparisonOperator.Equal,
            VdbeComparisonOperator.NotEqual => VdbeComparisonOperator.NotEqual,
            VdbeComparisonOperator.Is => VdbeComparisonOperator.Is,
            VdbeComparisonOperator.IsNot => VdbeComparisonOperator.IsNot,
            _ => op,
        };

    // Whether a long rowid satisfies the supplied comparison against a bound. Mirrors the
    // runtime Satisfies helper in ResumableStatement so the compiler's FilterRowId closure
    // for the range-seek path uses the same semantics as the SeekRowidRange start search.
    private static bool SatisfiesRowId(long rowId, VdbeComparisonOperator op, long bound)
    {
        return op switch
        {
            VdbeComparisonOperator.GreaterThan => rowId > bound,
            VdbeComparisonOperator.GreaterThanOrEqual => rowId >= bound,
            VdbeComparisonOperator.LessThan => rowId < bound,
            VdbeComparisonOperator.LessThanOrEqual => rowId <= bound,
            VdbeComparisonOperator.Equal => rowId == bound,
            VdbeComparisonOperator.NotEqual => rowId != bound,
            VdbeComparisonOperator.Is => rowId == bound,
            VdbeComparisonOperator.IsNot => rowId != bound,
            _ => false,
        };
    }

    private static string DescribeOperator(VdbeComparisonOperator? op)
        => op switch
        {
            VdbeComparisonOperator.GreaterThan => ">",
            VdbeComparisonOperator.GreaterThanOrEqual => ">=",
            VdbeComparisonOperator.LessThan => "<",
            VdbeComparisonOperator.LessThanOrEqual => "<=",
            VdbeComparisonOperator.Equal => "=",
            VdbeComparisonOperator.NotEqual => "!=",
            VdbeComparisonOperator.Is => "IS",
            VdbeComparisonOperator.IsNot => "IS NOT",
            null => string.Empty,
            _ => string.Empty,
        };

    private static string DescribeBound(VdbeComparisonOperator? endOp)
        => endOp is null ? string.Empty : $"..{DescribeOperator(endOp)}";

    internal readonly record struct ProjectionSource(Expression? Expression, int? ColumnIndex)
    {
        public static ProjectionSource ForExpression(Expression expression) => new(expression, null);

        public static ProjectionSource ForColumn(int columnIndex) => new(null, columnIndex);
    }

    internal sealed class ExpressionEmitter
    {
        private readonly ScanTarget? _target;
        private readonly Cursor? _cursor;
        private readonly List<VdbeInstruction> _instructions;
        private readonly int _programCounterBase;
        private readonly bool _allowControlFlow;
        private readonly Func<Expression, bool> _isConstant;
        private readonly Func<Expression, SqlValue> _fold;
        private readonly Func<FunctionExpression, VdbeScalarFunction?> _compileScalarFunction;
        private readonly VdbeNumericAffinity _numericAffinity;
        private readonly VdbeNumericAffinity _moduloAffinity;
        private readonly VdbeNumericAffinity _integerAffinity;
        private readonly Dictionary<int, int> _parameterSlots = [];
        private readonly List<int> _parameterIndices = [];
        private int _nextRegister;
        private int _constantFoldingSuppression;

        public ExpressionEmitter(
            ScanTarget? target,
            Cursor? cursor,
            int firstScratchRegister,
            List<VdbeInstruction> instructions,
            int programCounterBase,
            bool allowControlFlow,
            Func<Expression, bool> isConstant,
            Func<Expression, SqlValue> fold,
            Func<FunctionExpression, VdbeScalarFunction?> compileScalarFunction,
            VdbeNumericAffinity numericAffinity,
            VdbeNumericAffinity moduloAffinity,
            VdbeNumericAffinity integerAffinity)
        {
            _target = target;
            _cursor = cursor;
            _nextRegister = firstScratchRegister;
            _instructions = instructions;
            _programCounterBase = programCounterBase;
            _allowControlFlow = allowControlFlow;
            _isConstant = isConstant;
            _fold = fold;
            _compileScalarFunction = compileScalarFunction;
            _numericAffinity = numericAffinity;
            _moduloAffinity = moduloAffinity;
            _integerAffinity = integerAffinity;
        }

        public int RegisterCount => _nextRegister;

        public IReadOnlyList<int> ParameterIndices => _parameterIndices;

        public bool CanEmitNativePredicate(Expression expression)
            => expression is BinaryExpression binary && TryGetComparisonMetadata(binary, out _);

        public bool TryEmitPredicate(Expression expression, out Register destination)
        {
            destination = new Register(_nextRegister++);
            return TryEmitComparison(expression, destination);
        }

        public bool TryEmit(Expression expression, Register destination)
        {
            if (_constantFoldingSuppression == 0 && _isConstant(expression))
            {
                _instructions.Add(new LoadConstantInstruction(destination, _fold(expression)));
                return true;
            }

            switch (expression)
            {
                case LiteralExpression literal:
                    _instructions.Add(new LoadConstantInstruction(destination, literal.Value));
                    return true;
                case ParameterExpression parameter:
                    _instructions.Add(new LoadParameterInstruction(
                        destination,
                        new ParameterSlot(GetParameterSlot(parameter.Index))));
                    return true;
                case ColumnExpression column when _target is not null && _cursor is not null:
                    if (_target.ResolveColumnIndex(column.Name) is { } columnIndex)
                    {
                        _instructions.Add(new ColumnInstruction(_cursor.Value, columnIndex, destination));
                        return true;
                    }

                    if (IsTargetRowIdReference(column, _target))
                    {
                        _instructions.Add(new RowIdInstruction(_cursor.Value, destination));
                        return true;
                    }

                    return false;
                case BinaryExpression binary when TryMapArithmeticOperator(binary.Operator, out var arithmetic):
                    var operands = Allocate(2);
                    if (!TryEmit(binary.Left, operands.Start)
                        || !TryEmit(binary.Right, new Register(operands.Start.Index + 1)))
                    {
                        return false;
                    }

                    var affinity = GetAffinity(arithmetic);
                    _instructions.Add(new NumericAffinityInstruction(operands.Start, affinity));
                    _instructions.Add(new NumericAffinityInstruction(
                        new Register(operands.Start.Index + 1),
                        affinity));
                    _instructions.Add(new ArithmeticInstruction(destination, arithmetic, operands));
                    return true;
                case UnaryExpression unary when TryMapArithmeticOperator(unary.Operator, out var unaryArithmetic):
                    var operand = Allocate(1);
                    if (!TryEmit(unary.Operand, operand.Start))
                        return false;

                    if (unaryArithmetic != ArithmeticOperator.Identity)
                        _instructions.Add(new NumericAffinityInstruction(operand.Start, GetAffinity(unaryArithmetic)));
                    _instructions.Add(new ArithmeticInstruction(destination, unaryArithmetic, operand));
                    return true;
                case CastExpression cast:
                    if (!TryEmit(cast.Expression, destination))
                        return false;

                    _instructions.Add(new CastInstruction(destination, cast.TypeName));
                    return true;
                case CaseExpression @case when _allowControlFlow:
                    return TryEmitCase(@case, destination);
                case FunctionExpression function:
                    var scalar = _compileScalarFunction(function);
                    if (scalar is null)
                        return false;

                    var arguments = Allocate(function.Arguments.Count);
                    for (var index = 0; index < function.Arguments.Count; index++)
                    {
                        if (!TryEmit(
                                function.Arguments[index],
                                new Register(arguments.Start.Index + index)))
                        {
                            return false;
                        }
                    }

                    _instructions.Add(new FunctionInstruction(destination, scalar, arguments));
                    return true;
                case CollationExpression collation:
                    return TryEmit(collation.Expression, destination);
                default:
                    return false;
            }
        }

        private bool TryEmitComparison(Expression expression, Register destination)
        {
            if (expression is not BinaryExpression binary
                || !TryGetComparisonMetadata(binary, out var comparison))
            {
                return false;
            }

            var operands = Allocate(2);
            if (!TryEmit(binary.Left, operands.Start)
                || !TryEmit(binary.Right, new Register(operands.Start.Index + 1)))
            {
                return false;
            }

            _instructions.Add(new CompareInstruction(
                destination,
                comparison.Operator,
                operands.Start,
                new Register(operands.Start.Index + 1),
                comparison.LeftAffinity,
                comparison.RightAffinity,
                comparison.Collation));
            return true;
        }

        private bool TryEmitCase(CaseExpression expression, Register destination)
            => expression.Operand is null
                ? TryEmitSearchedCase(expression, destination)
                : TryEmitSimpleCase(expression, destination);

        private bool TryEmitSearchedCase(CaseExpression expression, Register destination)
        {
            if (expression.Clauses.Count == 0)
                return false;

            var endJumps = new List<int>(expression.Clauses.Count);
            _constantFoldingSuppression++;
            try
            {
                foreach (var clause in expression.Clauses)
                {
                    var condition = Allocate(1).Start;
                    if (!TryEmit(clause.When, condition))
                        return false;

                    var skipThenIndex = _instructions.Count;
                    _instructions.Add(new JumpIfNotTrueInstruction(condition, new ProgramCounter(0)));
                    if (!TryEmit(clause.Then, destination))
                        return false;

                    endJumps.Add(_instructions.Count);
                    _instructions.Add(new GotoInstruction(new ProgramCounter(0)));
                    _instructions[skipThenIndex] = new JumpIfNotTrueInstruction(
                        condition,
                        CurrentProgramCounter());
                }

                if (expression.Else is null)
                    _instructions.Add(new LoadConstantInstruction(destination, SqlValue.Null));
                else if (!TryEmit(expression.Else, destination))
                    return false;

                var end = CurrentProgramCounter();
                foreach (var jumpIndex in endJumps)
                    _instructions[jumpIndex] = new GotoInstruction(end);
                return true;
            }
            finally
            {
                _constantFoldingSuppression--;
            }
        }

        private bool TryEmitSimpleCase(CaseExpression expression, Register destination)
        {
            if (expression.Operand is null
                || expression.Clauses.Count == 0
                || !IsSimpleCaseComparisonValue(expression.Operand)
                || expression.Clauses.Any(clause => !IsSimpleCaseComparisonValue(clause.When)))
            {
                return false;
            }

            var endJumps = new List<int>(expression.Clauses.Count);
            _constantFoldingSuppression++;
            try
            {
                var operand = Allocate(1).Start;
                if (!TryEmit(expression.Operand, operand))
                    return false;

                foreach (var clause in expression.Clauses)
                {
                    var when = Allocate(1).Start;
                    var condition = Allocate(1).Start;
                    if (!TryEmit(clause.When, when))
                        return false;

                    _instructions.Add(new CompareInstruction(
                        condition,
                        VdbeComparisonOperator.Equal,
                        operand,
                        when,
                        LeftAffinity: null,
                        RightAffinity: null,
                        Collation: null));
                    var skipThenIndex = _instructions.Count;
                    _instructions.Add(new JumpIfNotTrueInstruction(condition, new ProgramCounter(0)));
                    if (!TryEmit(clause.Then, destination))
                        return false;

                    endJumps.Add(_instructions.Count);
                    _instructions.Add(new GotoInstruction(new ProgramCounter(0)));
                    _instructions[skipThenIndex] = new JumpIfNotTrueInstruction(
                        condition,
                        CurrentProgramCounter());
                }

                if (expression.Else is null)
                    _instructions.Add(new LoadConstantInstruction(destination, SqlValue.Null));
                else if (!TryEmit(expression.Else, destination))
                    return false;

                var end = CurrentProgramCounter();
                foreach (var jumpIndex in endJumps)
                    _instructions[jumpIndex] = new GotoInstruction(end);
                return true;
            }
            finally
            {
                _constantFoldingSuppression--;
            }
        }

        private static bool IsSimpleCaseComparisonValue(Expression expression)
            => expression is LiteralExpression or ParameterExpression;

        private ProgramCounter CurrentProgramCounter()
            => new(_programCounterBase + _instructions.Count);

        private RegisterRange Allocate(int count)
        {
            var start = new Register(_nextRegister);
            _nextRegister += count;
            return new RegisterRange(start, count);
        }

        private int GetParameterSlot(int parameterIndex)
        {
            if (_parameterSlots.TryGetValue(parameterIndex, out var slot))
                return slot;

            slot = _parameterIndices.Count;
            _parameterSlots.Add(parameterIndex, slot);
            _parameterIndices.Add(parameterIndex);
            return slot;
        }

        private bool TryGetComparisonMetadata(
            BinaryExpression expression,
            out VdbeComparisonMetadata metadata)
        {
            metadata = null!;
            if (!TryMapComparisonOperator(expression.Operator, out var operation))
                return false;

            var collation = GetExplicitCollation(expression.Left)
                ?? GetExplicitCollation(expression.Right)
                ?? GetDeclaredCollation(expression.Left)
                ?? GetDeclaredCollation(expression.Right);
            if (!SqliteIndexRecordComparer.IsSupportedCollation(collation))
                return false;

            metadata = new VdbeComparisonMetadata(
                operation,
                GetDeclaredAffinity(expression.Left),
                GetDeclaredAffinity(expression.Right),
                collation);
            return true;
        }

        private VdbeValueAffinity? GetDeclaredAffinity(Expression expression)
        {
            var column = UnwrapCollation(expression) as ColumnExpression;
            if (column is null || _target?.ColumnDefinitions is null)
                return null;

            var index = _target.ResolveColumnIndex(column.Name);
            if (index is null || index.Value >= _target.ColumnDefinitions.Count)
                return null;

            return _target.ColumnDefinitions[index.Value] is { StrictAny: false } definition
                ? ToVdbeAffinity(EmbeddedTable.GetDeclaredColumnAffinity(definition))
                : null;
        }

        private string? GetDeclaredCollation(Expression expression)
        {
            var column = UnwrapCollation(expression) as ColumnExpression;
            if (column is null || _target?.ColumnDefinitions is null)
                return null;

            var index = _target.ResolveColumnIndex(column.Name);
            return index is { } value && value < _target.ColumnDefinitions.Count
                ? _target.ColumnDefinitions[value]?.Collation
                : null;
        }

        private static Expression UnwrapCollation(Expression expression)
        {
            while (expression is CollationExpression collation)
                expression = collation.Expression;
            return expression;
        }

        private static string? GetExplicitCollation(Expression expression)
        {
            string? collation = null;
            while (expression is CollationExpression wrapper)
            {
                collation ??= wrapper.Name;
                expression = wrapper.Expression;
            }

            return collation;
        }

        private static VdbeValueAffinity ToVdbeAffinity(ColumnAffinity affinity)
            => affinity switch
            {
                ColumnAffinity.Blob => VdbeValueAffinity.Blob,
                ColumnAffinity.Text => VdbeValueAffinity.Text,
                ColumnAffinity.Numeric => VdbeValueAffinity.Numeric,
                ColumnAffinity.Integer => VdbeValueAffinity.Integer,
                ColumnAffinity.Real => VdbeValueAffinity.Real,
                _ => throw new InvalidOperationException($"Unknown column affinity {affinity}."),
            };

        private static bool TryMapArithmeticOperator(BinaryOperator op, out ArithmeticOperator arithmetic)
        {
            switch (op)
            {
                case BinaryOperator.Add:
                    arithmetic = ArithmeticOperator.Add;
                    return true;
                case BinaryOperator.Subtract:
                    arithmetic = ArithmeticOperator.Subtract;
                    return true;
                case BinaryOperator.Multiply:
                    arithmetic = ArithmeticOperator.Multiply;
                    return true;
                case BinaryOperator.Divide:
                    arithmetic = ArithmeticOperator.Divide;
                    return true;
                case BinaryOperator.Modulo:
                    arithmetic = ArithmeticOperator.Modulo;
                    return true;
                case BinaryOperator.BitwiseAnd:
                    arithmetic = ArithmeticOperator.BitwiseAnd;
                    return true;
                case BinaryOperator.BitwiseOr:
                    arithmetic = ArithmeticOperator.BitwiseOr;
                    return true;
                case BinaryOperator.ShiftLeft:
                    arithmetic = ArithmeticOperator.ShiftLeft;
                    return true;
                case BinaryOperator.ShiftRight:
                    arithmetic = ArithmeticOperator.ShiftRight;
                    return true;
                default:
                    arithmetic = default;
                    return false;
            }
        }

        private static bool TryMapArithmeticOperator(UnaryOperator op, out ArithmeticOperator arithmetic)
        {
            switch (op)
            {
                case UnaryOperator.Plus:
                    arithmetic = ArithmeticOperator.Identity;
                    return true;
                case UnaryOperator.Negate:
                    arithmetic = ArithmeticOperator.Negate;
                    return true;
                case UnaryOperator.BitwiseNot:
                    arithmetic = ArithmeticOperator.BitwiseNot;
                    return true;
                default:
                    arithmetic = default;
                    return false;
            }
        }

        private static bool TryMapComparisonOperator(
            BinaryOperator op,
            out VdbeComparisonOperator comparison)
        {
            switch (op)
            {
                case BinaryOperator.Is:
                    comparison = VdbeComparisonOperator.Is;
                    return true;
                case BinaryOperator.IsNot:
                    comparison = VdbeComparisonOperator.IsNot;
                    return true;
                case BinaryOperator.Equal:
                    comparison = VdbeComparisonOperator.Equal;
                    return true;
                case BinaryOperator.NotEqual:
                    comparison = VdbeComparisonOperator.NotEqual;
                    return true;
                case BinaryOperator.LessThan:
                    comparison = VdbeComparisonOperator.LessThan;
                    return true;
                case BinaryOperator.LessThanOrEqual:
                    comparison = VdbeComparisonOperator.LessThanOrEqual;
                    return true;
                case BinaryOperator.GreaterThan:
                    comparison = VdbeComparisonOperator.GreaterThan;
                    return true;
                case BinaryOperator.GreaterThanOrEqual:
                    comparison = VdbeComparisonOperator.GreaterThanOrEqual;
                    return true;
                default:
                    comparison = default;
                    return false;
            }
        }

        private VdbeNumericAffinity GetAffinity(ArithmeticOperator op)
        {
            return op switch
            {
                ArithmeticOperator.Modulo => _moduloAffinity,
                ArithmeticOperator.BitwiseAnd
                    or ArithmeticOperator.BitwiseOr
                    or ArithmeticOperator.ShiftLeft
                    or ArithmeticOperator.ShiftRight
                    or ArithmeticOperator.BitwiseNot => _integerAffinity,
                _ => _numericAffinity,
            };
        }

        private sealed record VdbeComparisonMetadata(
            VdbeComparisonOperator Operator,
            VdbeValueAffinity? LeftAffinity,
            VdbeValueAffinity? RightAffinity,
            string? Collation);
    }
}
