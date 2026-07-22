using Turso.Core.Execution;

namespace Turso.Core.Compilation;

/// <summary>
/// The kind of value an aggregate output column projects.
/// </summary>
public enum AggregateOutputKind
{
    /// <summary>A grouped column, read from the finalized group's saved group key.</summary>
    GroupKey,

    /// <summary>The finalized result of one aggregate accumulator.</summary>
    Aggregate,

    /// <summary>A folded compile-time constant.</summary>
    Constant,
}

/// <summary>
/// One output column of an aggregate result row: a grouped key column, the finalized
/// value of an aggregate, or a folded constant. Mirrors the SELECT compiler's projection
/// lowering but is expressed in primitives so the builder stays free of AST and SQL
/// semantics.
/// </summary>
public readonly record struct AggregateOutput
{
    private AggregateOutput(AggregateOutputKind kind, int index, SqlValue constant)
    {
        Kind = kind;
        Index = index;
        Constant = constant;
    }

    public AggregateOutputKind Kind { get; }

    /// <summary>The group-key ordinal (<see cref="AggregateOutputKind.GroupKey"/>) or the
    /// accumulator ordinal (<see cref="AggregateOutputKind.Aggregate"/>) this output reads.</summary>
    public int Index { get; }

    /// <summary>The value emitted for a constant output.</summary>
    public SqlValue Constant { get; }

    /// <summary>Projects the grouped value of the group-by column at <paramref name="keyIndex"/>.</summary>
    public static AggregateOutput ForGroupKey(int keyIndex)
    {
        if (keyIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(keyIndex));

        return new AggregateOutput(AggregateOutputKind.GroupKey, keyIndex, default);
    }

    /// <summary>Projects the finalized result of the aggregate at <paramref name="accumulatorIndex"/>.</summary>
    public static AggregateOutput ForAggregate(int accumulatorIndex)
    {
        if (accumulatorIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(accumulatorIndex));

        return new AggregateOutput(AggregateOutputKind.Aggregate, accumulatorIndex, default);
    }

    /// <summary>Projects a folded compile-time constant.</summary>
    public static AggregateOutput ForConstant(SqlValue value)
        => new(AggregateOutputKind.Constant, 0, value);
}

/// <summary>
/// One aggregate function of an aggregation: the <see cref="VdbeAggregate"/> supplying the
/// accumulation semantics together with the scanned-row column ordinals that feed its
/// argument tuple. An empty <see cref="ArgumentColumns"/> models a nullary aggregate such
/// as <c>COUNT(*)</c>.
/// </summary>
public sealed record AggregateFunctionSpec(VdbeAggregate Aggregate, IReadOnlyList<int> ArgumentColumns)
{
    public int Arity => ArgumentColumns.Count;
}

/// <summary>
/// A post-aggregation filter evaluated from a materialized tuple of group-key, aggregate, or
/// constant values. It models <c>HAVING</c>: every accumulator is finalized before the predicate
/// runs, and a false predicate skips only that result row.
/// </summary>
public sealed record AggregateHavingFilter(
    IReadOnlyList<AggregateOutput> Inputs,
    VdbeRowPredicate Predicate,
    string Description);

/// <summary>
/// Lowers whole-table (scalar) and <c>GROUP BY</c> aggregations into runnable
/// <see cref="VdbeProgram"/>s built from the aggregate opcode family (<c>AggReset</c>,
/// <c>AggStep</c>, <c>AggFinalize</c>) plus, for grouping, the sorter opcodes and
/// <c>SameGroup</c>/<c>Goto</c> control flow. So aggregation runs entirely through the
/// resumable state machine rather than the tree-walking evaluator.
/// </summary>
/// <remarks>
/// The builder owns only the program's control flow and register/jump layout. Accumulation
/// semantics (<see cref="VdbeAggregate"/>), the group ordering used to make groups
/// contiguous (<see cref="VdbeRowComparer"/>), group equality (<see cref="VdbeGroupComparer"/>),
/// and the WHERE predicate (<see cref="VdbeRowPredicate"/>) are all supplied by the caller,
/// exactly as the scan and sorted-scan builders delegate their semantics. The emitted
/// program is data-free: the scanned rows are bound at execution time through a
/// <see cref="VdbeCursorSource"/>.
/// </remarks>
public static class AggregateProgramBuilder
{
    /// <summary>
    /// Builds a whole-table aggregation with no <c>GROUP BY</c>. The program scans the
    /// table, folds every row into the accumulators, and always emits exactly one result
    /// row — even over an empty table, where each aggregate finalizes its empty-input value
    /// (<c>COUNT</c> → 0, <c>SUM</c> → NULL).
    /// <code>
    ///   0            OpenReadCursor
    ///   1..N         AggReset (one per accumulator)
    ///                Rewind        -> closeAddr        (empty table)
    ///   loopStart    [Filter       -> nextAddr]        (WHERE)
    ///                [Column arg reads] AggStep         (per aggregate)
    ///   nextAddr     Next          -> loopStart
    ///   closeAddr    CloseCursor
    ///                AggFinalize (per accumulator) -> aggOut
    ///                Copy/LoadConstant per output register
    ///                [Copy HAVING inputs; FilterRegisters -> Halt]
    ///                ResultRow
    ///                Halt
    /// </code>
    /// </summary>
    public static VdbeProgram BuildScalar(
        string tableName,
        int tableColumnCount,
        IReadOnlyList<AggregateFunctionSpec> aggregates,
        IReadOnlyList<AggregateOutput> outputs,
        VdbeRowPredicate? predicate = null,
        AggregateHavingFilter? having = null)
    {
        ValidateCommon(tableName, tableColumnCount, aggregates, outputs);
        foreach (var output in outputs)
        {
            if (output.Kind == AggregateOutputKind.GroupKey)
            {
                throw new ArgumentException(
                    "A scalar aggregation has no group key to project; use BuildGrouped.",
                    nameof(outputs));
            }

            ValidateAggregateOutput(output, aggregates.Count, groupKeyCount: 0);
        }

        ValidateHaving(having, aggregates.Count, groupKeyCount: 0);

        var argOffsets = ComputeArgOffsets(aggregates, out var totalArgs);
        var argBase = 0;
        var aggOutBase = totalArgs;
        var outBase = totalArgs + aggregates.Count;
        var havingBase = outBase + outputs.Count;
        var registerCount = havingBase + (having?.Inputs.Count ?? 0);

        var cursor = new Cursor(0);
        var ins = new List<VdbeInstruction>
        {
            new OpenReadCursorInstruction(cursor, tableName, tableColumnCount),
        };

        for (var i = 0; i < aggregates.Count; i++)
            ins.Add(new AggResetInstruction(new Accumulator(i)));

        var rewindIndex = ins.Count;
        ins.Add(new RewindCursorInstruction(cursor, new ProgramCounter(0)));

        var loopStart = ins.Count;
        var filterIndex = -1;
        if (predicate is not null)
        {
            filterIndex = ins.Count;
            ins.Add(new FilterInstruction(cursor, predicate, new ProgramCounter(0), string.Empty));
        }

        EmitCursorSteps(ins, cursor, aggregates, argOffsets, argBase);

        var nextAddr = ins.Count;
        ins.Add(new NextInstruction(cursor, new ProgramCounter(loopStart)));

        var closeAddr = ins.Count;
        ins.Add(new CloseCursorInstruction(cursor));
        EmitFinalizeAndOutput(ins, aggregates, outputs, aggOutBase, outBase, savedKeyBase: 0, having, havingBase);
        ins.Add(new HaltInstruction());

        ins[rewindIndex] = new RewindCursorInstruction(cursor, new ProgramCounter(closeAddr));
        if (filterIndex >= 0)
        {
            ins[filterIndex] = new FilterInstruction(
                cursor,
                predicate!,
                new ProgramCounter(nextAddr),
                $"skip row when WHERE is false, goto {nextAddr}");
        }

        return new VdbeProgram(
            registerCount,
            cursorCount: 1,
            ins,
            sorterCount: 0,
            accumulatorCount: aggregates.Count);
    }

    /// <summary>
    /// Builds a <c>GROUP BY</c> aggregation. The program materializes every scanned row into
    /// a sorter ordered by <paramref name="groupOrderComparer"/> so rows of one group are
    /// contiguous, then walks the sorted rows once: it accumulates rows of the current group,
    /// detects each group boundary with <paramref name="groupComparer"/>, and finalizes and
    /// emits one result row per group. An empty table produces no rows.
    /// <code>
    ///   OpenReadCursor / OpenSorter
    ///   Rewind        -> sortAddr                     (empty table)
    ///   loopStart     [Filter] Column* SorterInsert
    ///                 Next -> loopStart / CloseCursor
    ///   sortAddr      SorterSort -> closeAddr          (empty sorter: no groups)
    ///   prime         SorterData; save key; AggReset*; AggStep*
    ///                 SorterNext -> drainLoop
    ///                 Goto       -> finalizeLast        (single-row group)
    ///   drainLoop     SorterData; load current key
    ///                 SameGroup -> sameStep             (still the same group)
    ///                 AggFinalize*; output; [FilterRegisters]; ResultRow; AggReset*; save new key
    ///   sameStep      AggStep*
    ///                 SorterNext -> drainLoop
    ///   finalizeLast  AggFinalize*; output; ResultRow  (last group)
    ///   closeAddr     CloseSorter; Halt
    /// </code>
    /// </summary>
    public static VdbeProgram BuildGrouped(
        string tableName,
        int tableColumnCount,
        IReadOnlyList<int> groupKeyColumns,
        IReadOnlyList<AggregateFunctionSpec> aggregates,
        IReadOnlyList<AggregateOutput> outputs,
        VdbeRowComparer groupOrderComparer,
        VdbeGroupComparer groupComparer,
        VdbeRowPredicate? predicate = null,
        AggregateHavingFilter? having = null)
    {
        ValidateCommon(tableName, tableColumnCount, aggregates, outputs);
        ArgumentNullException.ThrowIfNull(groupKeyColumns);
        ArgumentNullException.ThrowIfNull(groupOrderComparer);
        ArgumentNullException.ThrowIfNull(groupComparer);
        if (groupKeyColumns.Count == 0)
            throw new ArgumentException("A grouped aggregation needs at least one group-key column.", nameof(groupKeyColumns));

        foreach (var column in groupKeyColumns)
        {
            if (column < 0 || column >= tableColumnCount)
            {
                throw new ArgumentException(
                    $"Group-key column {column} is outside the {tableColumnCount}-column table.",
                    nameof(groupKeyColumns));
            }
        }

        foreach (var output in outputs)
            ValidateAggregateOutput(output, aggregates.Count, groupKeyColumns.Count);
        ValidateHaving(having, aggregates.Count, groupKeyColumns.Count);

        var group = groupKeyColumns.Count;
        var argOffsets = ComputeArgOffsets(aggregates, out var totalArgs);
        var stagingBase = 0;
        var savedKeyBase = tableColumnCount;
        var currentKeyBase = tableColumnCount + group;
        var argBase = tableColumnCount + (2 * group);
        var aggOutBase = argBase + totalArgs;
        var outBase = aggOutBase + aggregates.Count;
        var havingBase = outBase + outputs.Count;
        var registerCount = havingBase + (having?.Inputs.Count ?? 0);

        var cursor = new Cursor(0);
        var sorter = new Sorter(0);
        var stagingRange = new RegisterRange(new Register(stagingBase), tableColumnCount);
        var savedKeyRange = new RegisterRange(new Register(savedKeyBase), group);
        var currentKeyRange = new RegisterRange(new Register(currentKeyBase), group);

        var ins = new List<VdbeInstruction>
        {
            new OpenReadCursorInstruction(cursor, tableName, tableColumnCount),
            new OpenSorterInstruction(sorter, groupOrderComparer, tableColumnCount),
        };

        var rewindIndex = ins.Count;
        ins.Add(new RewindCursorInstruction(cursor, new ProgramCounter(0)));

        var loopStart = ins.Count;
        var filterIndex = -1;
        if (predicate is not null)
        {
            filterIndex = ins.Count;
            ins.Add(new FilterInstruction(cursor, predicate, new ProgramCounter(0), string.Empty));
        }

        for (var column = 0; column < tableColumnCount; column++)
            ins.Add(new ColumnInstruction(cursor, column, new Register(stagingBase + column)));

        ins.Add(new SorterInsertInstruction(sorter, stagingRange));

        var nextIngestAddr = ins.Count;
        ins.Add(new NextInstruction(cursor, new ProgramCounter(loopStart)));
        ins.Add(new CloseCursorInstruction(cursor));

        var sortIndex = ins.Count;
        ins.Add(new SorterSortInstruction(sorter, new ProgramCounter(0)));

        // Backpatch the ingest-phase jumps now that their targets are known.
        ins[rewindIndex] = new RewindCursorInstruction(cursor, new ProgramCounter(sortIndex));
        if (filterIndex >= 0)
        {
            ins[filterIndex] = new FilterInstruction(
                cursor,
                predicate!,
                new ProgramCounter(nextIngestAddr),
                $"skip row when WHERE is false, goto {nextIngestAddr}");
        }

        // Prime the first group from the first sorted row.
        ins.Add(new SorterDataInstruction(sorter, stagingRange));
        for (var j = 0; j < group; j++)
            ins.Add(new CopyInstruction(new Register(stagingBase + groupKeyColumns[j]), new Register(savedKeyBase + j)));

        for (var i = 0; i < aggregates.Count; i++)
            ins.Add(new AggResetInstruction(new Accumulator(i)));

        EmitStagingSteps(ins, aggregates, argOffsets, argBase, stagingBase);

        var primeNextIndex = ins.Count;
        ins.Add(new SorterNextInstruction(sorter, new ProgramCounter(0)));
        var primeGotoIndex = ins.Count;
        ins.Add(new GotoInstruction(new ProgramCounter(0)));

        var drainLoop = ins.Count;
        ins.Add(new SorterDataInstruction(sorter, stagingRange));
        for (var j = 0; j < group; j++)
            ins.Add(new CopyInstruction(new Register(stagingBase + groupKeyColumns[j]), new Register(currentKeyBase + j)));

        var sameGroupIndex = ins.Count;
        ins.Add(new SameGroupInstruction(currentKeyRange, savedKeyRange, groupComparer, new ProgramCounter(0)));

        // New group boundary: finalize and emit the previous group, then start a new one.
        EmitFinalizeAndOutput(ins, aggregates, outputs, aggOutBase, outBase, savedKeyBase, having, havingBase);
        for (var i = 0; i < aggregates.Count; i++)
            ins.Add(new AggResetInstruction(new Accumulator(i)));
        for (var j = 0; j < group; j++)
            ins.Add(new CopyInstruction(new Register(currentKeyBase + j), new Register(savedKeyBase + j)));

        var sameStep = ins.Count;
        EmitStagingSteps(ins, aggregates, argOffsets, argBase, stagingBase);
        ins.Add(new SorterNextInstruction(sorter, new ProgramCounter(drainLoop)));

        var finalizeLast = ins.Count;
        EmitFinalizeAndOutput(ins, aggregates, outputs, aggOutBase, outBase, savedKeyBase, having, havingBase);

        var closeAddr = ins.Count;
        ins.Add(new CloseSorterInstruction(sorter));
        ins.Add(new HaltInstruction());

        // Backpatch the forward jumps of the drain phase.
        ins[sortIndex] = new SorterSortInstruction(sorter, new ProgramCounter(closeAddr));
        ins[primeNextIndex] = new SorterNextInstruction(sorter, new ProgramCounter(drainLoop));
        ins[primeGotoIndex] = new GotoInstruction(new ProgramCounter(finalizeLast));
        ins[sameGroupIndex] = new SameGroupInstruction(
            currentKeyRange,
            savedKeyRange,
            groupComparer,
            new ProgramCounter(sameStep));

        return new VdbeProgram(
            registerCount,
            cursorCount: 1,
            ins,
            sorterCount: 1,
            accumulatorCount: aggregates.Count);
    }

    // Steps every aggregate from the live cursor row: gathers each aggregate's argument
    // columns into its contiguous argument block, then folds the block into its accumulator.
    private static void EmitCursorSteps(
        List<VdbeInstruction> ins,
        Cursor cursor,
        IReadOnlyList<AggregateFunctionSpec> aggregates,
        int[] argOffsets,
        int argBase)
    {
        for (var i = 0; i < aggregates.Count; i++)
        {
            var spec = aggregates[i];
            for (var k = 0; k < spec.Arity; k++)
                ins.Add(new ColumnInstruction(cursor, spec.ArgumentColumns[k], new Register(argBase + argOffsets[i] + k)));

            ins.Add(new AggStepInstruction(
                new Accumulator(i),
                spec.Aggregate,
                new RegisterRange(new Register(argBase + argOffsets[i]), spec.Arity)));
        }
    }

    // Steps every aggregate from the materialized staging row: gathers each aggregate's
    // argument columns out of staging into its argument block, then folds the block in.
    private static void EmitStagingSteps(
        List<VdbeInstruction> ins,
        IReadOnlyList<AggregateFunctionSpec> aggregates,
        int[] argOffsets,
        int argBase,
        int stagingBase)
    {
        for (var i = 0; i < aggregates.Count; i++)
        {
            var spec = aggregates[i];
            for (var k = 0; k < spec.Arity; k++)
                ins.Add(new CopyInstruction(new Register(stagingBase + spec.ArgumentColumns[k]), new Register(argBase + argOffsets[i] + k)));

            ins.Add(new AggStepInstruction(
                new Accumulator(i),
                spec.Aggregate,
                new RegisterRange(new Register(argBase + argOffsets[i]), spec.Arity)));
        }
    }

    // Finalizes every accumulator into its output register, builds the result row into the
    // output block, and emits it. Group-key outputs read the saved (finalizing) group key.
    private static void EmitFinalizeAndOutput(
        List<VdbeInstruction> ins,
        IReadOnlyList<AggregateFunctionSpec> aggregates,
        IReadOnlyList<AggregateOutput> outputs,
        int aggOutBase,
        int outBase,
        int savedKeyBase,
        AggregateHavingFilter? having,
        int havingBase)
    {
        for (var i = 0; i < aggregates.Count; i++)
        {
            ins.Add(new AggFinalizeInstruction(
                new Accumulator(i),
                aggregates[i].Aggregate,
                new Register(aggOutBase + i)));
        }

        for (var o = 0; o < outputs.Count; o++)
        {
            var output = outputs[o];
            var destination = new Register(outBase + o);
            ins.Add(EmitOutput(output, destination, aggOutBase, savedKeyBase));
        }

        if (having is not null)
        {
            for (var input = 0; input < having.Inputs.Count; input++)
            {
                ins.Add(EmitOutput(
                    having.Inputs[input],
                    new Register(havingBase + input),
                    aggOutBase,
                    savedKeyBase));
            }

            var filterAddress = ins.Count;
            ins.Add(new FilterRegistersInstruction(
                new RegisterRange(new Register(havingBase), having.Inputs.Count),
                having.Predicate,
                new ProgramCounter(filterAddress + 2),
                having.Description));
        }

        ins.Add(new ResultRowInstruction(new RegisterRange(new Register(outBase), outputs.Count)));
    }

    private static VdbeInstruction EmitOutput(
        AggregateOutput output,
        Register destination,
        int aggOutBase,
        int savedKeyBase)
    {
        return output.Kind switch
        {
            AggregateOutputKind.GroupKey => new CopyInstruction(new Register(savedKeyBase + output.Index), destination),
            AggregateOutputKind.Aggregate => new CopyInstruction(new Register(aggOutBase + output.Index), destination),
            AggregateOutputKind.Constant => new LoadConstantInstruction(destination, output.Constant),
            _ => throw new ArgumentOutOfRangeException(nameof(output), "Unknown aggregate output kind."),
        };
    }

    private static int[] ComputeArgOffsets(IReadOnlyList<AggregateFunctionSpec> aggregates, out int totalArgs)
    {
        var offsets = new int[aggregates.Count];
        var running = 0;
        for (var i = 0; i < aggregates.Count; i++)
        {
            offsets[i] = running;
            running += aggregates[i].Arity;
        }

        totalArgs = running;
        return offsets;
    }

    private static void ValidateCommon(
        string tableName,
        int tableColumnCount,
        IReadOnlyList<AggregateFunctionSpec> aggregates,
        IReadOnlyList<AggregateOutput> outputs)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(aggregates);
        ArgumentNullException.ThrowIfNull(outputs);
        if (tableColumnCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(tableColumnCount), "An aggregation needs at least one column.");
        if (aggregates.Count == 0)
            throw new ArgumentException("An aggregation must declare at least one aggregate.", nameof(aggregates));
        if (outputs.Count == 0)
            throw new ArgumentException("An aggregation must project at least one output column.", nameof(outputs));

        foreach (var spec in aggregates)
        {
            if (spec is null)
                throw new ArgumentException("Aggregate specifications must not be null.", nameof(aggregates));
            if (spec.Aggregate is null)
                throw new ArgumentException("Aggregate specifications must supply an aggregate.", nameof(aggregates));
            ArgumentNullException.ThrowIfNull(spec.ArgumentColumns);

            foreach (var column in spec.ArgumentColumns)
            {
                if (column < 0 || column >= tableColumnCount)
                {
                    throw new ArgumentException(
                        $"Aggregate argument column {column} is outside the {tableColumnCount}-column table.",
                        nameof(aggregates));
                }
            }
        }
    }

    private static void ValidateAggregateOutput(AggregateOutput output, int aggregateCount, int groupKeyCount)
    {
        switch (output.Kind)
        {
            case AggregateOutputKind.GroupKey when output.Index >= groupKeyCount:
                throw new ArgumentException(
                    $"Output projects group key {output.Index}, but the aggregation groups on {groupKeyCount} columns.",
                    nameof(output));
            case AggregateOutputKind.Aggregate when output.Index >= aggregateCount:
                throw new ArgumentException(
                    $"Output projects aggregate {output.Index}, but the aggregation declares {aggregateCount} aggregates.",
                    nameof(output));
            default:
                break;
        }
    }

    private static void ValidateHaving(AggregateHavingFilter? having, int aggregateCount, int groupKeyCount)
    {
        if (having is null)
            return;

        ArgumentNullException.ThrowIfNull(having.Inputs);
        ArgumentNullException.ThrowIfNull(having.Predicate);
        ArgumentNullException.ThrowIfNull(having.Description);
        foreach (var input in having.Inputs)
            ValidateAggregateOutput(input, aggregateCount, groupKeyCount);
    }
}
