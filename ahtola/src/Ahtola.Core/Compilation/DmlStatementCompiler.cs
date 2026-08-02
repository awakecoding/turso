using Ahtola.Core.Execution;

namespace Ahtola.Core.Compilation;

/// <summary>
/// The kind of row mutation a lowered DML program performs, selecting the
/// mutation opcode emitted into the cursor loop.
/// </summary>
public enum DmlKind
{
    Insert,
    Update,
    Delete,
}

/// <summary>
/// One emitted RETURNING output register: a column read from the affected row, a
/// read of the affected row's rowid, or a folded compile-time constant. Mirrors the
/// SELECT compiler's projection lowering but adds the rowid pseudo-column so
/// <c>RETURNING rowid</c> stays on the compiled path.
/// </summary>
/// <remarks>
/// This is the bare-projection descriptor: it expresses exactly a column, a rowid, or a constant per
/// output register. Richer RETURNING items — arithmetic over the affected row's columns/rowid and
/// constants — are expressed with the composable <see cref="DmlReturningExpression"/> tree instead, which
/// the <see cref="DmlStatementCompiler.Compile(DmlKind, string, int, VdbeRowPredicate, IReadOnlyList{DmlReturningExpression}, VdbeWriteTarget)"/>
/// overload lowers. Each <see cref="DmlProjectionOp"/> maps one-to-one onto a <see cref="DmlReturningExpression"/>
/// leaf, so the two descriptors share one lowering path.
/// </remarks>
public readonly record struct DmlProjectionOp(bool IsColumn, bool IsRowId, int ColumnIndex, SqlValue Constant)
{
    public static DmlProjectionOp ForColumn(int columnIndex) => new(true, false, columnIndex, default);

    public static DmlProjectionOp ForRowId() => new(false, true, 0, default);

    public static DmlProjectionOp ForConstant(SqlValue value) => new(false, false, 0, value);

    /// <summary>Projects this bare op onto the equivalent <see cref="DmlReturningExpression"/> leaf so
    /// both descriptor shapes lower through one path.</summary>
    internal DmlReturningExpression ToExpression() => this switch
    {
        { IsColumn: true } => DmlReturningExpression.Column(ColumnIndex),
        { IsRowId: true } => DmlReturningExpression.RowId(),
        _ => DmlReturningExpression.Constant(Constant),
    };
}

/// <summary>
/// A lowered INSERT/UPDATE/DELETE: the emitted <see cref="VdbeProgram"/> together
/// with the write targets its cursor mutates at execution time.
/// </summary>
public sealed record CompiledDml(
    VdbeProgram Program,
    IReadOnlyList<VdbeWriteTarget> WriteTargets)
{
    internal IReadOnlyList<VdbeWriteTarget?> RuntimeWriteTargets { get; init; } = WriteTargets;

    internal IReadOnlyList<VdbeCursorSource?>? CursorSources { get; init; }

    internal IReadOnlyList<int>? ParameterIndices { get; init; }
}

internal sealed record DmlReturningProgram(
    IReadOnlyList<VdbeInstruction> Instructions,
    int OutputCount,
    int RegisterCount,
    IReadOnlyList<int> ParameterIndices);

/// <summary>
/// A DML scan filter over either declared row values alone or those values together with the
/// hidden rowid. The two forms remain distinct so the executor never smuggles rowids into the
/// declared-column tuple exposed to ordinary row predicates.
/// </summary>
internal sealed class DmlRowFilter
{
    private DmlRowFilter(VdbeRowPredicate? rowPredicate, VdbeRowIdPredicate? rowIdPredicate)
    {
        RowPredicate = rowPredicate;
        RowIdPredicate = rowIdPredicate;
    }

    public VdbeRowPredicate? RowPredicate { get; }

    public VdbeRowIdPredicate? RowIdPredicate { get; }

    public static DmlRowFilter ForRow(VdbeRowPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new DmlRowFilter(predicate, null);
    }

    public static DmlRowFilter ForRowId(VdbeRowIdPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new DmlRowFilter(null, predicate);
    }
}

/// <summary>
/// Lowers a bounded subset of INSERT/UPDATE/DELETE into a <see cref="VdbeProgram"/>
/// built from real cursor/mutation opcodes. Like <see cref="SelectStatementCompiler"/>
/// the compiler owns only the program's control flow and jump layout: SQL semantics
/// (predicate evaluation, row building, constraint enforcement, RETURNING projection)
/// are supplied by the caller so the emitted program matches the evaluator exactly.
/// </summary>
/// <remarks>
/// The public descriptor path keeps the original single-loop layout:
/// <code>
///   0            OpenWriteCursor
///   1            Rewind        -> commitAddr (nothing to mutate)
///   loopStart    [Filter|FilterRowId -> nextAddr] (UPDATE/DELETE with WHERE)
///   mutateAddr   Insert|Update|Delete
///                [projection block per RETURNING clause: Column/RowId/LoadConstant
///                 leaves and Arithmetic nodes computing into output registers r[0..R-1],
///                 followed by ResultRow r[0..R-1]]         (RETURNING only)
///   nextAddr     Next          -> loopStart
///   commitAddr   Commit
///                CloseCursor
///                Halt
/// </code>
/// The managed SQL route uses a second read cursor for generic RETURNING expressions: the write loop
/// buffers all affected rows, then a source-ordered read loop evaluates RETURNING before Commit. Keeping
/// mutation callbacks ahead of every projection matches the evaluator's observable order while projection
/// failures still discard the buffered statement.
/// The projection block has no internal jumps, so its length is measured while it is lowered and the
/// jump targets (<c>nextAddr</c>, <c>commitAddr</c>) are derived from it. A RETURNING item may be a bare
/// column/rowid/constant or a composable arithmetic expression over them; the latter reads its operands
/// into scratch registers <c>r[R..]</c> and folds them with <see cref="ArithmeticInstruction"/>s whose
/// value/NULL/error semantics live in <see cref="VdbeArithmetic"/>. Because every projection instruction —
/// including the operand reads and the arithmetic folds — runs after the mutation opcode and before
/// <c>Commit</c>, the projection observes the post-mutation (INSERT/UPDATE) or pre-delete (DELETE) row
/// snapshot, a projection error (e.g. an arithmetic type error) propagates before anything is persisted,
/// and a constraint failure raised by <c>Commit</c> discards the buffered rows, preserving statement
/// atomicity.
/// </remarks>
public static class DmlStatementCompiler
{
    /// <summary>
    /// Lowers a DML statement whose RETURNING clause is a list of bare column/rowid/constant projections.
    /// Each <see cref="DmlProjectionOp"/> is projected onto the equivalent <see cref="DmlReturningExpression"/>
    /// leaf and lowered through the shared expression path.
    /// </summary>
    public static CompiledDml Compile(
        DmlKind kind,
        string tableName,
        int columnCount,
        VdbeRowPredicate? predicate,
        IReadOnlyList<DmlProjectionOp> returningOps,
        VdbeWriteTarget writeTarget)
    {
        ArgumentNullException.ThrowIfNull(returningOps);
        var returning = new DmlReturningExpression[returningOps.Count];
        for (var index = 0; index < returningOps.Count; index++)
            returning[index] = returningOps[index].ToExpression();

        return Compile(kind, tableName, columnCount, predicate, returning, writeTarget);
    }

    /// <summary>
    /// Lowers a DML statement whose RETURNING clause is a list of composable
    /// <see cref="DmlReturningExpression"/> trees, so a RETURNING item may be a bare column/rowid/constant
    /// or an arithmetic expression over them. The emitted projection block computes each item into an
    /// output register after the mutation opcode and before <c>Commit</c>, so the arithmetic observes the
    /// post-mutation (INSERT/UPDATE) or pre-delete (DELETE) row snapshot with the same value/NULL/error
    /// semantics as the SELECT and VALUES arithmetic routes.
    /// </summary>
    /// <exception cref="StatementCompilationException">An INSERT carries a predicate, or the DML kind is
    /// unsupported.</exception>
    /// <exception cref="VdbeProgramValidationException">The lowered projection produces invalid bytecode,
    /// e.g. a RETURNING column outside the write cursor's columns.</exception>
    public static CompiledDml Compile(
        DmlKind kind,
        string tableName,
        int columnCount,
        VdbeRowPredicate? predicate,
        IReadOnlyList<DmlReturningExpression> returning,
        VdbeWriteTarget writeTarget)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(returning);
        ArgumentNullException.ThrowIfNull(writeTarget);
        if (kind == DmlKind.Insert && predicate is not null)
            throw new StatementCompilationException("INSERT programs do not filter rows.");

        return CompileWithFilter(
            kind,
            tableName,
            columnCount,
            predicate is null ? null : DmlRowFilter.ForRow(predicate),
            returning,
            writeTarget);
    }

    internal static CompiledDml CompileWithFilter(
        DmlKind kind,
        string tableName,
        int columnCount,
        DmlRowFilter? filter,
        IReadOnlyList<DmlReturningExpression> returning,
        VdbeWriteTarget writeTarget)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(returning);
        ArgumentNullException.ThrowIfNull(writeTarget);
        if (kind == DmlKind.Insert && filter is not null)
            throw new StatementCompilationException("INSERT programs do not filter rows.");

        var program = BuildProgram(kind, tableName, columnCount, filter, returning);
        return new CompiledDml(program, [writeTarget]);
    }

    internal static CompiledDml CompileWithFilter(
        DmlKind kind,
        string tableName,
        int columnCount,
        DmlRowFilter? filter,
        DmlReturningProgram returning,
        VdbeWriteTarget writeTarget,
        VdbeCursorSource returningSource)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(returning);
        ArgumentNullException.ThrowIfNull(writeTarget);
        ArgumentNullException.ThrowIfNull(returningSource);
        if (kind == DmlKind.Insert && filter is not null)
            throw new StatementCompilationException("INSERT programs do not filter rows.");
        if (returning.OutputCount <= 0)
            throw new StatementCompilationException("A compiled RETURNING program must produce at least one output.");

        var program = BuildTwoPhaseProgram(kind, tableName, columnCount, filter, returning);
        return new CompiledDml(program, [writeTarget])
        {
            RuntimeWriteTargets = [writeTarget, null],
            CursorSources = [null, returningSource],
            ParameterIndices = returning.ParameterIndices,
        };
    }

    private static VdbeProgram BuildProgram(
        DmlKind kind,
        string tableName,
        int columnCount,
        DmlRowFilter? filter,
        IReadOnlyList<DmlReturningExpression> returning)
    {
        var cursor = new Cursor(0);
        var hasFilter = filter is not null;
        var hasReturning = returning.Count > 0;

        // Lower the RETURNING projections into their (jump-free) instruction block up front so the loop's
        // jump targets can be derived from its measured length. An empty RETURNING clause emits no block
        // and needs no registers.
        var projectionBlock = new List<VdbeInstruction>();
        var registerCount = 0;
        if (hasReturning)
        {
            var allocator = new RegisterAllocator(returning.Count);
            for (var register = 0; register < returning.Count; register++)
                EmitExpression(cursor, returning[register], new Register(register), projectionBlock, allocator);

            projectionBlock.Add(new ResultRowInstruction(new RegisterRange(new Register(0), returning.Count)));
            registerCount = allocator.HighWaterMark;
        }

        var loopStart = 2;
        var filterCount = hasFilter ? 1 : 0;
        var mutateAddr = loopStart + filterCount;
        var nextAddr = mutateAddr + 1 + projectionBlock.Count;
        var commitAddr = nextAddr + 1;

        var instructions = new List<VdbeInstruction>(commitAddr + 3)
        {
            new OpenWriteCursorInstruction(cursor, tableName, columnCount),
            new RewindCursorInstruction(cursor, new ProgramCounter(commitAddr)),
        };

        if (filter?.RowPredicate is { } rowPredicate)
        {
            instructions.Add(new FilterInstruction(
                cursor,
                rowPredicate,
                new ProgramCounter(nextAddr),
                $"skip row when WHERE is false, goto {nextAddr}"));
        }
        else if (filter?.RowIdPredicate is { } rowIdPredicate)
        {
            instructions.Add(new FilterRowIdInstruction(
                cursor,
                rowIdPredicate,
                new ProgramCounter(nextAddr),
                $"skip row when WHERE is false, goto {nextAddr}"));
        }
        else if (filter is not null)
        {
            throw new StatementCompilationException("DML filter has no predicate.");
        }

        instructions.Add(kind switch
        {
            DmlKind.Insert => new InsertInstruction(cursor),
            DmlKind.Update => new UpdateInstruction(cursor),
            DmlKind.Delete => new DeleteInstruction(cursor),
            _ => throw new StatementCompilationException($"Unsupported DML kind {kind}."),
        });

        instructions.AddRange(projectionBlock);

        instructions.Add(new NextInstruction(cursor, new ProgramCounter(loopStart)));
        instructions.Add(new CommitInstruction(cursor));
        instructions.Add(new CloseCursorInstruction(cursor));
        instructions.Add(new HaltInstruction());

        return new VdbeProgram(registerCount, cursorCount: 1, instructions);
    }

    private static VdbeProgram BuildTwoPhaseProgram(
        DmlKind kind,
        string tableName,
        int columnCount,
        DmlRowFilter? filter,
        DmlReturningProgram returning)
    {
        var writeCursor = new Cursor(0);
        var returningCursor = new Cursor(1);
        const int mutationLoopStart = 2;
        var filterCount = filter is null ? 0 : 1;
        var mutateAddr = mutationLoopStart + filterCount;
        var mutationNextAddr = mutateAddr + 1;
        var returningOpenAddr = mutationNextAddr + 1;
        var returningLoopStart = returningOpenAddr + 2;
        var resultRowAddr = returningLoopStart + returning.Instructions.Count;
        var returningNextAddr = resultRowAddr + 1;
        var returningCloseAddr = returningNextAddr + 1;
        var commitAddr = returningCloseAddr + 1;

        var instructions = new List<VdbeInstruction>(commitAddr + 3)
        {
            new OpenWriteCursorInstruction(writeCursor, tableName, columnCount),
            new RewindCursorInstruction(writeCursor, new ProgramCounter(returningOpenAddr)),
        };

        AddFilter(instructions, writeCursor, filter, mutationNextAddr);
        instructions.Add(Mutation(kind, writeCursor));
        instructions.Add(new NextInstruction(writeCursor, new ProgramCounter(mutationLoopStart)));

        instructions.Add(new OpenReadCursorInstruction(returningCursor, tableName, columnCount));
        instructions.Add(new RewindCursorInstruction(returningCursor, new ProgramCounter(commitAddr)));
        instructions.AddRange(returning.Instructions);
        instructions.Add(new ResultRowInstruction(
            new RegisterRange(new Register(0), returning.OutputCount)));
        instructions.Add(new NextInstruction(returningCursor, new ProgramCounter(returningLoopStart)));
        instructions.Add(new CloseCursorInstruction(returningCursor));
        instructions.Add(new CommitInstruction(writeCursor));
        instructions.Add(new CloseCursorInstruction(writeCursor));
        instructions.Add(new HaltInstruction());

        return new VdbeProgram(
            returning.RegisterCount,
            cursorCount: 2,
            instructions,
            parameterSlotCount: returning.ParameterIndices.Count);
    }

    private static void AddFilter(
        List<VdbeInstruction> instructions,
        Cursor cursor,
        DmlRowFilter? filter,
        int falseTarget)
    {
        if (filter?.RowPredicate is { } rowPredicate)
        {
            instructions.Add(new FilterInstruction(
                cursor,
                rowPredicate,
                new ProgramCounter(falseTarget),
                $"skip row when WHERE is false, goto {falseTarget}"));
        }
        else if (filter?.RowIdPredicate is { } rowIdPredicate)
        {
            instructions.Add(new FilterRowIdInstruction(
                cursor,
                rowIdPredicate,
                new ProgramCounter(falseTarget),
                $"skip row when WHERE is false, goto {falseTarget}"));
        }
        else if (filter is not null)
        {
            throw new StatementCompilationException("DML filter has no predicate.");
        }
    }

    private static VdbeInstruction Mutation(DmlKind kind, Cursor cursor)
        => kind switch
        {
            DmlKind.Insert => new InsertInstruction(cursor),
            DmlKind.Update => new UpdateInstruction(cursor),
            DmlKind.Delete => new DeleteInstruction(cursor),
            _ => throw new StatementCompilationException($"Unsupported DML kind {kind}."),
        };

    // Emits the instructions that compute <paramref name="expression"/> into <paramref name="destination"/>,
    // appending them to <paramref name="block"/>. Leaves read the affected row's column/rowid or bake a
    // constant directly into the destination; an arithmetic node evaluates its operands into a contiguous
    // scratch operand block (recursively, using registers above that block for any nested folds) and then
    // folds them with a single ArithmeticInstruction. The destination always sits below the operand block,
    // so writing the fold never clobbers an operand register — and even an overlapping destination would be
    // safe because ArithmeticInstruction snapshots its operands before writing.
    private static void EmitExpression(
        Cursor cursor,
        DmlReturningExpression expression,
        Register destination,
        List<VdbeInstruction> block,
        RegisterAllocator allocator)
    {
        switch (expression)
        {
            case DmlColumnReturning column:
                block.Add(new ColumnInstruction(cursor, column.ColumnIndex, destination));
                break;
            case DmlRowIdReturning:
                block.Add(new RowIdInstruction(cursor, destination));
                break;
            case DmlConstantReturning constant:
                block.Add(new LoadConstantInstruction(destination, constant.Value));
                break;
            case DmlArithmeticReturning arithmetic:
                {
                    var arity = arithmetic.Operands.Count;
                    var frame = allocator.Enter();
                    var operandStart = allocator.Reserve(arity);
                    for (var index = 0; index < arity; index++)
                        EmitExpression(cursor, arithmetic.Operands[index], new Register(operandStart + index), block, allocator);

                    block.Add(new ArithmeticInstruction(
                        destination,
                        arithmetic.Operator,
                        new RegisterRange(new Register(operandStart), arity)));
                    allocator.Leave(frame);
                    break;
                }

            default:
                throw new StatementCompilationException(
                    $"Unsupported RETURNING expression {expression.GetType().Name}.");
        }
    }

    // A scratch-register bump allocator for the projection block. Output registers occupy r[0..R-1];
    // scratch registers for arithmetic operands start at R and grow upward. Enter/Leave bracket a nested
    // allocation so an operand block (and any registers a nested fold used above it) is freed once its
    // result has been consumed, while HighWaterMark records the peak so the program declares exactly the
    // registers it needs.
    private sealed class RegisterAllocator
    {
        private int _next;

        public RegisterAllocator(int firstScratch)
        {
            _next = firstScratch;
            HighWaterMark = firstScratch;
        }

        public int HighWaterMark { get; private set; }

        public int Enter() => _next;

        public void Leave(int mark) => _next = mark;

        public int Reserve(int count)
        {
            var start = _next;
            _next += count;
            if (_next > HighWaterMark)
                HighWaterMark = _next;

            return start;
        }
    }
}
