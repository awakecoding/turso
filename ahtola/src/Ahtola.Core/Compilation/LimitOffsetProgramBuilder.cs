using Ahtola.Core.Execution;

namespace Ahtola.Core.Compilation;

/// <summary>
/// Lowers <c>LIMIT</c>/<c>OFFSET</c> onto an already-compiled result-streaming <see cref="VdbeProgram"/>
/// by gating its result-row emissions, so the row count is bounded entirely inside the resumable state
/// machine rather than by trimming a materialized result set in the evaluator. It is a program-to-program
/// transform in the same spirit as <see cref="CompoundProgramBuilder"/>: it relocates the child program's
/// instructions (only jump targets shift, since new registers are appended after the existing ones),
/// seeds one or two counter registers in a prologue, and wraps every <c>ResultRow</c> with an
/// <see cref="OffsetGateInstruction"/> and/or <see cref="LimitGateInstruction"/> that skip and stop the
/// stream. The underlying program keeps executing its own scan/sorter/compound logic; nothing is
/// precomputed and the evaluator is never consulted.
/// </summary>
/// <remarks>
/// <para>
/// Exact semantics, matched to the tree-walking evaluator's <c>ApplyDistinctLimit</c>:
/// </para>
/// <list type="bullet">
///   <item>OFFSET is clamped to non-negative (<c>Math.Max(0, offset)</c>): a negative offset skips
///     nothing. The first <c>offset</c> emitted candidates are discarded before any row is produced.</item>
///   <item>OFFSET is applied before LIMIT: skipped rows are never counted against LIMIT.</item>
///   <item>A non-negative LIMIT emits exactly that many rows; <c>LIMIT 0</c> emits none.</item>
///   <item>A null or negative LIMIT is unbounded and is lowered by simply not emitting a limit gate.</item>
///   <item>When neither gate is needed (offset ≤ 0 and unbounded limit) the program is returned
///     unchanged — a faithful no-op lowering.</item>
/// </list>
/// <para>
/// Composition: because a shared pair of counters gates <em>every</em> <c>ResultRow</c> in the program,
/// this composes with any program whose output is emitted through unconditional <c>ResultRow</c> opcodes —
/// direct table scans, sorted scans, joins, aggregations, constant and source-less scalar-function
/// projections, row-aware aggregates whose <see cref="DistinctGateInstruction"/> has already skipped
/// duplicate candidates, and <c>UNION ALL</c> compounds (whose per-term <c>ResultRow</c>s share the
/// counters, so LIMIT/OFFSET spans the concatenated stream). Programs that combine de-duplication and
/// emission in conditional primitives (<see cref="DistinctResultRowInstruction"/>,
/// <see cref="CompoundResultRowInstruction"/>, <see cref="RowSetInsertInstruction"/> — i.e. direct
/// <c>DISTINCT</c>/<c>UNION</c>, <c>INTERSECT</c>, <c>EXCEPT</c>) are rejected with
/// <see cref="StatementCompilationException"/>, because a pre-emit gate counts candidates rather than
/// emitted rows and so cannot bound those streams exactly.
/// </para>
/// <para>
/// The transform owns only control flow; it never inspects row values, so LIMIT/OFFSET semantics stay
/// independent of the SQL types flowing through the gated registers. Wiring this lowering into the SQL
/// pipeline (resolving the LIMIT/OFFSET expressions and routing eligible statements here) belongs to the
/// database layer, exactly as the scan/sorter/compound builders leave their routing to the caller.
/// </para>
/// </remarks>
public static class LimitOffsetProgramBuilder
{
    /// <summary>
    /// Wraps <paramref name="program"/> so it emits at most <paramref name="limit"/> rows after skipping
    /// the first <paramref name="offset"/> rows. Returns the program unchanged when no gating is needed.
    /// </summary>
    /// <param name="program">The result-streaming program to gate.</param>
    /// <param name="offset">The number of leading rows to skip; values ≤ 0 skip nothing.</param>
    /// <param name="limit">The maximum number of rows to emit; <see langword="null"/> or a negative value
    /// is unbounded.</param>
    public static VdbeProgram Apply(VdbeProgram program, long offset, long? limit)
    {
        ArgumentNullException.ThrowIfNull(program);

        // Clamp to the evaluator's semantics: negative offset skips nothing; null/negative limit is
        // unbounded. Only a non-negative limit gates the stream.
        var effectiveOffset = Math.Max(0L, offset);
        var needOffset = effectiveOffset > 0;
        var needLimit = limit is >= 0;

        // Neither gate needed: the program already yields exactly the right rows. Returning it unchanged
        // is a faithful no-op lowering of "no OFFSET, unbounded LIMIT".
        if (!needOffset && !needLimit)
            return program;

        var instructions = program.Instructions;
        var count = instructions.Count;

        // A pre-emit gate counts every candidate that reaches it, which equals the emitted-row count only
        // when the emission is unconditional. Reject conditional/compound emitters rather than lower them
        // inexactly, and reject an already-gated program rather than nest gates.
        var resultRowCount = 0;
        foreach (var instruction in instructions)
        {
            switch (instruction)
            {
                case ResultRowInstruction:
                    resultRowCount++;
                    break;
                case DistinctResultRowInstruction:
                case CompoundResultRowInstruction:
                case RowSetInsertInstruction:
                    throw new StatementCompilationException(
                        "LIMIT/OFFSET lowering supports only unconditional ResultRow emissions; a program that emits through DistinctResultRow, CompoundResultRow, or RowSetInsert (UNION/DISTINCT, INTERSECT, EXCEPT) cannot be gated exactly.");
                case OffsetGateInstruction:
                case LimitGateInstruction:
                    throw new StatementCompilationException(
                        "Program is already LIMIT/OFFSET gated; apply the limit/offset lowering once.");
            }
        }

        if (resultRowCount == 0)
        {
            throw new StatementCompilationException(
                "LIMIT/OFFSET lowering requires a program that emits at least one result row.");
        }

        var prologueLength = (needOffset ? 1 : 0) + (needLimit ? 1 : 0);
        var gatesPerResult = prologueLength;

        // New counter registers are appended after the program's existing registers, so no register index
        // in the copied instructions changes — only jump targets shift.
        var offsetCounter = new Register(program.RegisterCount);
        var limitCounter = new Register(program.RegisterCount + (needOffset ? 1 : 0));
        var registerCount = program.RegisterCount + prologueLength;

        // blockStart[i] is the new address of old instruction i's emitted block (its inserted gates, if
        // any, followed by the instruction itself). Each ResultRow inserts `gatesPerResult` instructions
        // before itself, so the address shift accumulates as ResultRows are passed. blockStart[count] is
        // the total instruction count of the lowered program.
        var blockStart = new int[count + 1];
        var resultsSeen = 0;
        for (var i = 0; i < count; i++)
        {
            blockStart[i] = prologueLength + i + (resultsSeen * gatesPerResult);
            if (instructions[i] is ResultRowInstruction)
                resultsSeen++;
        }

        blockStart[count] = prologueLength + count + (resultsSeen * gatesPerResult);

        // The limit gate's done target is the program's terminating Halt (validated to be the last
        // instruction, hence never a ResultRow, so its block start is the Halt itself).
        var doneTarget = new ProgramCounter(blockStart[count - 1]);

        var lowered = new List<VdbeInstruction>(blockStart[count]);

        // Prologue: seed the counters. OFFSET first, so the register layout is stable when only one gate
        // is present.
        if (needOffset)
            lowered.Add(new LoadConstantInstruction(offsetCounter, SqlValue.Integer(effectiveOffset)));
        if (needLimit)
            lowered.Add(new LoadConstantInstruction(limitCounter, SqlValue.Integer(limit!.Value)));

        for (var i = 0; i < count; i++)
        {
            var instruction = instructions[i];
            if (instruction is ResultRowInstruction resultRow)
            {
                // Gate order matters: OFFSET first (skips to the loop-advance after this ResultRow without
                // counting against LIMIT), then LIMIT (stops the stream once the allowance is spent).
                if (needOffset)
                    lowered.Add(new OffsetGateInstruction(offsetCounter, new ProgramCounter(blockStart[i + 1])));
                if (needLimit)
                    lowered.Add(new LimitGateInstruction(limitCounter, doneTarget));

                lowered.Add(new ResultRowInstruction(resultRow.Values));
            }
            else
            {
                lowered.Add(RemapTargets(instruction, blockStart));
            }
        }

        return new VdbeProgram(
            registerCount,
            program.CursorCount,
            lowered,
            program.SorterCount,
            program.AccumulatorCount,
            program.DistinctSetCount,
            program.ParameterSlotCount,
            windowBufferCount: program.WindowBufferCount);
    }

    /// <summary>
    /// Applies <see cref="Apply(VdbeProgram, long, long?)"/> to a compiled <see cref="CompoundTerm"/>,
    /// gating its program while preserving its cursor sources unchanged (LIMIT/OFFSET adds no cursors).
    /// This is how a <c>UNION ALL</c> compound composes with LIMIT/OFFSET.
    /// </summary>
    public static CompoundTerm Apply(CompoundTerm term, long offset, long? limit)
    {
        ArgumentNullException.ThrowIfNull(term);
        ArgumentNullException.ThrowIfNull(term.Program);
        var gated = Apply(term.Program, offset, limit);
        return ReferenceEquals(gated, term.Program) ? term : term with { Program = gated };
    }

    // Rebuilds one non-ResultRow instruction with its jump targets remapped through the block-start table.
    // Registers, cursors, sorters, accumulators, and distinct sets are unchanged because the transform only
    // appends new registers; opcodes without jump targets are returned as-is. The emit family is never seen
    // here: ResultRow is handled by the caller, and the conditional emitters were rejected up front.
    private static VdbeInstruction RemapTargets(VdbeInstruction instruction, int[] blockStart)
    {
        ProgramCounter Pc(ProgramCounter counter) => new(blockStart[counter.Offset]);

        return instruction switch
        {
            RewindCursorInstruction x => new RewindCursorInstruction(x.Cursor, Pc(x.EmptyTarget)),
            FilterInstruction x => new FilterInstruction(x.Cursor, x.Predicate, Pc(x.FalseTarget), x.Description),
            FilterRowIdInstruction x => new FilterRowIdInstruction(x.Cursor, x.Predicate, Pc(x.FalseTarget), x.Description),
            FilterRegistersInstruction x => new FilterRegistersInstruction(x.Row, x.Predicate, Pc(x.FalseTarget), x.Description),
            DistinctGateInstruction x => new DistinctGateInstruction(
                x.Values,
                x.Equality,
                x.DistinctSetIndex,
                Pc(x.DuplicateTarget)),
            NextInstruction x => new NextInstruction(x.Cursor, Pc(x.LoopTarget)),
            SorterSortInstruction x => new SorterSortInstruction(x.Sorter, Pc(x.EmptyTarget)),
            SorterNextInstruction x => new SorterNextInstruction(x.Sorter, Pc(x.LoopTarget)),
            WindowBufferComputeInstruction x => new WindowBufferComputeInstruction(x.Buffer, Pc(x.EmptyTarget)),
            WindowBufferNextInstruction x => new WindowBufferNextInstruction(x.Buffer, Pc(x.LoopTarget)),
            GotoInstruction x => new GotoInstruction(Pc(x.Target)),
            JumpIfInstruction x => new JumpIfInstruction(x.Register, Pc(x.Target)),
            JumpIfNotTrueInstruction x => new JumpIfNotTrueInstruction(x.Value, Pc(x.FalseTarget)),
            SameGroupInstruction x => new SameGroupInstruction(x.CurrentKey, x.SavedKey, x.Comparer, Pc(x.SameGroupTarget)),
            LoadConstantInstruction
                or LoadParameterInstruction
                or CopyInstruction
                or OpenReadCursorInstruction
                or OpenWriteCursorInstruction
                or CloseCursorInstruction
                or ColumnInstruction
                or RowIdInstruction
                or DeleteInstruction
                or InsertInstruction
                or UpdateInstruction
                or CommitInstruction
                or OpenSorterInstruction
                or SorterInsertInstruction
                or SorterDataInstruction
                or CloseSorterInstruction
                or OpenWindowBufferInstruction
                or WindowBufferInsertInstruction
                or WindowBufferDataInstruction
                or CloseWindowBufferInstruction
                or AggResetInstruction
                or AggStepInstruction
                or AggFinalizeInstruction
                or FunctionInstruction
                or ArithmeticInstruction
                or NumericAffinityInstruction
                or CompareInstruction
                or CastInstruction
                or GroupKeyInstruction
                or YieldInstruction
                or HaltInstruction => instruction,
            _ => throw new StatementCompilationException(
                $"Cannot lower opcode {instruction.Opcode} into a LIMIT/OFFSET program."),
        };
    }
}
