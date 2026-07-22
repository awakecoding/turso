using Turso.Core.Execution;

namespace Turso.Core.Compilation;

/// <summary>
/// One term of a compound SELECT: a compiled child <see cref="VdbeProgram"/> together with the live
/// row sources its read cursors iterate at execution time. A term is any program that streams its
/// result through <c>ResultRow</c> (a constant projection, a table scan, a sorted scan, a join, or an
/// aggregation) — <see cref="CompoundProgramBuilder"/> sequences these streams without knowing how
/// each term was built, exactly as the tree-walking evaluator sequences its per-term result sets.
/// </summary>
/// <param name="Program">The compiled child program.</param>
/// <param name="CursorSources">
/// The child's read-cursor row sources, one per cursor, in cursor-index order. Its length must equal
/// <see cref="VdbeProgram.CursorCount"/>; a term with no cursors (e.g. a constant projection) supplies
/// an empty list.
/// </param>
public sealed record CompoundTerm(VdbeProgram Program, IReadOnlyList<VdbeCursorSource> CursorSources);

/// <summary>
/// Sequences the result streams of two or more compiled child programs into one runnable
/// <see cref="VdbeProgram"/>, lowering compound SELECT execution — <c>UNION ALL</c>,
/// <c>UNION</c>/<c>DISTINCT</c>, <c>INTERSECT</c>, and <c>EXCEPT</c> — onto the resumable state machine
/// rather than a tree-walking evaluator or an AST-only wrapper. <c>UNION</c> variants run each term to
/// exhaustion in order, emitting its rows, then fall through to the next; the set operations build every
/// non-primary term into a probe set first, then stream the primary term, emitting only the rows that
/// satisfy the operation's membership condition.
/// </summary>
/// <remarks>
/// The builder owns only the mechanical splice: it relocates each term's registers, cursors, sorters,
/// accumulators, distinct sets, parameter slots, and jump targets into disjoint ranges, drops every
/// non-final term's trailing <c>Halt</c> so control falls through to the next term, concatenates the
/// terms' cursor sources, and validates that every term projects the same number of result columns. It
/// re-uses the full existing opcode set unchanged for <c>UNION ALL</c>; the only compound-specific
/// primitive is <see cref="DistinctResultRowInstruction"/>, which <see cref="BuildUnionDistinct"/>
/// substitutes for each term's <c>ResultRow</c> against one shared distinct set.
/// <para>
/// Row-value semantics stay with the caller, exactly as the scan, join, sorted-scan, and aggregate
/// builders delegate theirs: <c>UNION</c>/<c>DISTINCT</c> de-duplication is driven by a caller-supplied
/// <see cref="VdbeRowEquality"/> so the emitted program matches the evaluator's row-equality contract
/// (NULL==NULL together with affinity- and collation-aware comparison) rather than re-deriving it here.
/// </para>
/// <para>
/// <c>UNION ALL</c> preserves any de-duplication a term already performs internally (its
/// <c>DistinctResultRow</c> opcodes and distinct sets are relocated intact), so a distinct sub-query can
/// appear as a <c>UNION ALL</c> term. <see cref="BuildUnionDistinct"/> layers one outer distinct set over
/// terms that do not already de-duplicate; because same-operator <c>UNION</c> chains are associative and
/// idempotent, a router should flatten them into a single <see cref="BuildUnionDistinct"/> call over all
/// terms rather than nesting distinct compounds.
/// </para>
/// </remarks>
public static class CompoundProgramBuilder
{
    /// <summary>
    /// Sequences <paramref name="terms"/> with <c>UNION ALL</c> semantics: every row of every term is
    /// emitted, in term order, with no de-duplication. Any internal de-duplication a term performs is
    /// preserved. Requires at least two terms, all projecting the same number of columns.
    /// </summary>
    public static CompoundTerm BuildUnionAll(IReadOnlyList<CompoundTerm> terms)
        => Build(terms, distinctEquality: null);

    /// <summary>
    /// Sequences <paramref name="terms"/> with <c>UNION</c>/<c>DISTINCT</c> semantics: each distinct row
    /// is emitted once, at its first occurrence across all terms, in arrival order.
    /// De-duplication uses <paramref name="rowEquality"/>, so the caller owns the exact row-equality
    /// contract. Requires at least two terms, all projecting the same number of columns, and none of
    /// which already de-duplicates internally (flatten same-operator chains into one call instead of
    /// nesting distinct compounds).
    /// </summary>
    public static CompoundTerm BuildUnionDistinct(IReadOnlyList<CompoundTerm> terms, VdbeRowEquality rowEquality)
    {
        ArgumentNullException.ThrowIfNull(rowEquality);
        return Build(terms, rowEquality);
    }

    /// <summary>
    /// Combines <paramref name="terms"/> with <c>INTERSECT</c> semantics: each distinct row that appears
    /// in <em>every</em> term is emitted once, in the first-term first-occurrence order the primary term's
    /// cursors supply. Membership and de-duplication use <paramref name="rowEquality"/>, so the caller owns
    /// the exact row-equality contract. Requires at least two terms, all projecting the same number of
    /// columns, and none of which already uses row sets internally (flatten same-operator chains into one
    /// call instead of nesting set-operation compounds).
    /// </summary>
    public static CompoundTerm BuildIntersect(IReadOnlyList<CompoundTerm> terms, VdbeRowEquality rowEquality)
    {
        ArgumentNullException.ThrowIfNull(rowEquality);
        return BuildSetOperation(terms, rowEquality, CompoundMembershipMode.PresentInAll);
    }

    /// <summary>
    /// Combines <paramref name="terms"/> with left-associative <c>EXCEPT</c> semantics: each distinct row
    /// of the first term that appears in <em>none</em> of the remaining terms (equivalently, is not in
    /// their union — <c>A EXCEPT B EXCEPT C</c> is <c>A</c> minus <c>(B ∪ C)</c>) is emitted once, in the
    /// first-term first-occurrence order the primary term's cursors supply. Membership and de-duplication
    /// use <paramref name="rowEquality"/>, so the caller owns the exact row-equality contract. Requires at
    /// least two terms, all projecting the same number of columns, and none of which already uses row sets
    /// internally.
    /// </summary>
    public static CompoundTerm BuildExcept(IReadOnlyList<CompoundTerm> terms, VdbeRowEquality rowEquality)
    {
        ArgumentNullException.ThrowIfNull(rowEquality);
        return BuildSetOperation(terms, rowEquality, CompoundMembershipMode.AbsentFromAll);
    }

    private static CompoundTerm Build(IReadOnlyList<CompoundTerm> terms, VdbeRowEquality? distinctEquality)
    {
        ArgumentNullException.ThrowIfNull(terms);
        if (terms.Count < 2)
            throw new ArgumentException("A compound select needs at least two terms.", nameof(terms));

        var count = terms.Count;
        var columnCount = -1;
        for (var i = 0; i < count; i++)
        {
            var term = terms[i]
                ?? throw new ArgumentException($"Compound term {i} must not be null.", nameof(terms));
            ArgumentNullException.ThrowIfNull(term.Program);
            ArgumentNullException.ThrowIfNull(term.CursorSources);
            if (term.CursorSources.Count != term.Program.CursorCount)
            {
                throw new ArgumentException(
                    $"Compound term {i} supplies {term.CursorSources.Count} cursor sources for a {term.Program.CursorCount}-cursor program.",
                    nameof(terms));
            }

            if (distinctEquality is not null && term.Program.DistinctSetCount != 0)
            {
                throw new ArgumentException(
                    $"Compound term {i} already de-duplicates internally; flatten same-operator UNION chains into one BuildUnionDistinct call instead of nesting distinct compounds.",
                    nameof(terms));
            }

            var termColumns = ResultColumnCount(term.Program, i);
            if (columnCount < 0)
                columnCount = termColumns;
            else if (columnCount != termColumns)
            {
                throw new ArgumentException(
                    $"SELECTs to the left and right of a compound operator do not have the same number of result columns ({columnCount} vs {termColumns}).",
                    nameof(terms));
            }
        }

        // Lay out each term's resources in disjoint ranges. Registers, cursors, sorters, accumulators,
        // and distinct sets are relocated by the running totals of the preceding terms; jump targets by
        // the running count of emitted instructions. Every term except the last drops its trailing Halt
        // so control falls through to the next term's first instruction.
        var registerBase = new int[count];
        var cursorBase = new int[count];
        var sorterBase = new int[count];
        var accumulatorBase = new int[count];
        var distinctBase = new int[count];
        var instructionBase = new int[count];
        var parameterSlotBase = new int[count];

        var totalRegisters = 0;
        var totalCursors = 0;
        var totalSorters = 0;
        var totalAccumulators = 0;
        var totalDistinctSets = 0;
        var totalInstructions = 0;
        var totalParameterSlots = 0;
        for (var i = 0; i < count; i++)
        {
            var program = terms[i].Program;
            registerBase[i] = totalRegisters;
            cursorBase[i] = totalCursors;
            sorterBase[i] = totalSorters;
            accumulatorBase[i] = totalAccumulators;
            distinctBase[i] = totalDistinctSets;
            instructionBase[i] = totalInstructions;
            parameterSlotBase[i] = totalParameterSlots;

            totalRegisters += program.RegisterCount;
            totalCursors += program.CursorCount;
            totalSorters += program.SorterCount;
            totalAccumulators += program.AccumulatorCount;
            totalDistinctSets += program.DistinctSetCount;
            totalInstructions += KeptInstructionCount(program, isLast: i == count - 1);
            totalParameterSlots += program.ParameterSlotCount;
        }

        // The outer distinct set (for BuildUnionDistinct) is allocated after every term's own sets; the
        // pre-flight validation guarantees terms carry none, so this is set 0 in practice.
        var outerDistinctSet = totalDistinctSets;
        var combinedDistinctSets = distinctEquality is null ? totalDistinctSets : totalDistinctSets + 1;

        var instructions = new List<VdbeInstruction>(totalInstructions);
        var cursorSources = new List<VdbeCursorSource>(totalCursors);
        for (var i = 0; i < count; i++)
        {
            var term = terms[i];
            var program = term.Program;
            var kept = KeptInstructionCount(program, isLast: i == count - 1);
            for (var j = 0; j < kept; j++)
            {
                instructions.Add(Relocate(
                    program.Instructions[j],
                    registerBase[i],
                    cursorBase[i],
                    sorterBase[i],
                    accumulatorBase[i],
                    distinctBase[i],
                    instructionBase[i],
                    parameterSlotBase[i],
                    distinctEquality,
                    outerDistinctSet));
            }

            cursorSources.AddRange(term.CursorSources);
        }

        var combined = new VdbeProgram(
            totalRegisters,
            totalCursors,
            instructions,
            totalSorters,
            totalAccumulators,
            combinedDistinctSets,
            totalParameterSlots);
        return new CompoundTerm(combined, cursorSources);
    }

    // Lowers a homogeneous INTERSECT or EXCEPT chain. Every non-primary term is emitted first, each
    // materialized into its own probe set by rewriting its ResultRow into RowSetInsert; the primary
    // term (term 0) is emitted last, its ResultRow rewritten into a CompoundResultRow that tests each
    // streamed row against the fully built probe sets and de-duplicates its output. Because the primary
    // term runs last and unchanged apart from the emit rewrite, the output preserves first-term
    // first-occurrence order exactly as the primary term's cursors supply it.
    private static CompoundTerm BuildSetOperation(
        IReadOnlyList<CompoundTerm> terms,
        VdbeRowEquality rowEquality,
        CompoundMembershipMode mode)
    {
        ArgumentNullException.ThrowIfNull(terms);
        if (terms.Count < 2)
            throw new ArgumentException("A compound select needs at least two terms.", nameof(terms));

        var count = terms.Count;
        var columnCount = -1;
        for (var i = 0; i < count; i++)
        {
            var term = terms[i]
                ?? throw new ArgumentException($"Compound term {i} must not be null.", nameof(terms));
            ArgumentNullException.ThrowIfNull(term.Program);
            ArgumentNullException.ThrowIfNull(term.CursorSources);
            if (term.CursorSources.Count != term.Program.CursorCount)
            {
                throw new ArgumentException(
                    $"Compound term {i} supplies {term.CursorSources.Count} cursor sources for a {term.Program.CursorCount}-cursor program.",
                    nameof(terms));
            }

            if (term.Program.DistinctSetCount != 0)
            {
                throw new ArgumentException(
                    $"Compound term {i} already uses row sets internally; flatten same-operator INTERSECT/EXCEPT chains into one call instead of nesting set-operation compounds.",
                    nameof(terms));
            }

            var termColumns = ResultColumnCount(term.Program, i);
            if (columnCount < 0)
                columnCount = termColumns;
            else if (columnCount != termColumns)
            {
                throw new ArgumentException(
                    $"SELECTs to the left and right of a compound operator do not have the same number of result columns ({columnCount} vs {termColumns}).",
                    nameof(terms));
            }
        }

        // Emit the non-primary terms (indices 1..n-1) first, then the primary term (index 0) last, so the
        // primary can test membership against fully built probe sets while streaming in its own order.
        var emissionOrder = new int[count];
        for (var slot = 0; slot < count - 1; slot++)
            emissionOrder[slot] = slot + 1;
        emissionOrder[count - 1] = 0;

        // Registers, cursors, sorters, accumulators, and jump targets are internal to the combined program,
        // so their bases are indexed by emission slot to land in disjoint ranges in emission order. Terms
        // carry no row sets of their own (validated above).
        var registerBase = new int[count];
        var cursorBase = new int[count];
        var sorterBase = new int[count];
        var accumulatorBase = new int[count];
        var instructionBase = new int[count];

        var totalRegisters = 0;
        var totalCursors = 0;
        var totalSorters = 0;
        var totalAccumulators = 0;
        var totalInstructions = 0;
        for (var slot = 0; slot < count; slot++)
        {
            var program = terms[emissionOrder[slot]].Program;
            registerBase[slot] = totalRegisters;
            cursorBase[slot] = totalCursors;
            sorterBase[slot] = totalSorters;
            accumulatorBase[slot] = totalAccumulators;
            instructionBase[slot] = totalInstructions;

            totalRegisters += program.RegisterCount;
            totalCursors += program.CursorCount;
            totalSorters += program.SorterCount;
            totalAccumulators += program.AccumulatorCount;
            totalInstructions += KeptInstructionCount(program, isLast: slot == count - 1);
        }

        // Parameter slots are the combined program's external binding interface: a caller binds by
        // input-term order (term 0's slots, then term 1's, …), so their bases must be allocated by term
        // identity, independent of the emission order that runs the primary term (term 0) last. Indexing by
        // term index keeps the combined slot space identical to the UNION path (Build lays parameters out
        // the same way), so input binding [A, B] maps A EXCEPT B to A minus B rather than B minus A.
        var parameterSlotBaseByTerm = new int[count];
        var totalParameterSlots = 0;
        for (var term = 0; term < count; term++)
        {
            parameterSlotBaseByTerm[term] = totalParameterSlots;
            totalParameterSlots += terms[term].Program.ParameterSlotCount;
        }

        // Row-set layout: each of the n-1 probe sets is owned by its aux emission slot; the primary term's
        // output de-duplication set follows them.
        var membershipSets = new int[count - 1];
        for (var slot = 0; slot < count - 1; slot++)
            membershipSets[slot] = slot;
        var outputSet = count - 1;
        var totalRowSets = count;

        var instructions = new List<VdbeInstruction>(totalInstructions);
        var cursorSources = new List<VdbeCursorSource>(totalCursors);
        for (var slot = 0; slot < count; slot++)
        {
            var term = terms[emissionOrder[slot]];
            var program = term.Program;
            var isPrimary = slot == count - 1;
            var kept = KeptInstructionCount(program, isLast: isPrimary);
            for (var j = 0; j < kept; j++)
            {
                instructions.Add(RelocateSetOperation(
                    program.Instructions[j],
                    registerBase[slot],
                    cursorBase[slot],
                    sorterBase[slot],
                    accumulatorBase[slot],
                    instructionBase[slot],
                    parameterSlotBaseByTerm[emissionOrder[slot]],
                    rowEquality,
                    isPrimary,
                    isPrimary ? outputSet : membershipSets[slot],
                    membershipSets,
                    mode));
            }

            cursorSources.AddRange(term.CursorSources);
        }

        var combinedSetOp = new VdbeProgram(
            totalRegisters,
            totalCursors,
            instructions,
            totalSorters,
            totalAccumulators,
            totalRowSets,
            totalParameterSlots);
        return new CompoundTerm(combinedSetOp, cursorSources);
    }

    // A non-final term drops its trailing Halt so execution falls through to the next term; the final
    // term keeps its Halt as the combined program's terminator.
    private static int KeptInstructionCount(VdbeProgram program, bool isLast)
        => isLast ? program.Instructions.Count : program.Instructions.Count - 1;

    // The number of result columns a term projects, verifying every result-row emission in the term is
    // the same width. Both plain and distinct result rows count so a distinct sub-term is measurable.
    private static int ResultColumnCount(VdbeProgram program, int termIndex)
    {
        int? width = null;
        foreach (var instruction in program.Instructions)
        {
            var emitted = instruction switch
            {
                ResultRowInstruction result => (int?)result.Values.Count,
                DistinctResultRowInstruction distinct => distinct.Values.Count,
                CompoundResultRowInstruction compound => compound.Values.Count,
                _ => null,
            };

            if (emitted is not int columns)
                continue;

            if (width is null)
                width = columns;
            else if (width != columns)
            {
                throw new ArgumentException(
                    $"Compound term {termIndex} emits result rows of differing widths ({width} vs {columns}).",
                    nameof(program));
            }
        }

        return width
            ?? throw new ArgumentException(
                $"Compound term {termIndex} does not emit any result rows.",
                nameof(program));
    }

    // Rebuilds one instruction with its register/cursor/sorter/accumulator/distinct-set indices and jump
    // targets shifted into the term's disjoint range. When a distinct equality is supplied, plain result
    // rows become distinct result rows against the shared outer set; existing distinct result rows keep
    // their own equality and have their set index relocated. Set-operation opcodes carried by a nested
    // sub-term keep their equality and mode and have their row-set indices relocated.
    private static VdbeInstruction Relocate(
        VdbeInstruction instruction,
        int registerBase,
        int cursorBase,
        int sorterBase,
        int accumulatorBase,
        int distinctBase,
        int instructionBase,
        int parameterSlotBase,
        VdbeRowEquality? distinctEquality,
        int outerDistinctSet)
    {
        if (RelocateStructural(instruction, registerBase, cursorBase, sorterBase, accumulatorBase, instructionBase, parameterSlotBase)
            is { } structural)
        {
            return structural;
        }

        Register Reg(Register register) => new(register.Index + registerBase);
        RegisterRange Range(RegisterRange range) => new(Reg(range.Start), range.Count);

        return instruction switch
        {
            DistinctResultRowInstruction x => new DistinctResultRowInstruction(Range(x.Values), x.Equality, x.DistinctSetIndex + distinctBase),
            RowSetInsertInstruction x => new RowSetInsertInstruction(Range(x.Values), x.Equality, x.RowSetIndex + distinctBase),
            CompoundResultRowInstruction x => new CompoundResultRowInstruction(
                Range(x.Values),
                x.Equality,
                x.OutputSetIndex + distinctBase,
                RelocateSetIndices(x.MembershipSetIndices, distinctBase),
                x.Mode),
            ResultRowInstruction x => distinctEquality is null
                ? new ResultRowInstruction(Range(x.Values))
                : new DistinctResultRowInstruction(Range(x.Values), distinctEquality, outerDistinctSet),
            _ => throw new StatementCompilationException(
                $"Cannot sequence unsupported opcode {instruction.Opcode} into a compound program."),
        };
    }

    // Rebuilds one instruction of a set-operation term. Structural opcodes are relocated in common; the
    // term's ResultRow is rewritten into the compound-specific emit: the primary term's becomes a
    // CompoundResultRow that filters by membership against the probe sets and de-duplicates output, while
    // each auxiliary term's becomes a RowSetInsert that materializes its probe set. Set-operation terms
    // carry no row sets of their own, so no other emit-family opcode can appear.
    private static VdbeInstruction RelocateSetOperation(
        VdbeInstruction instruction,
        int registerBase,
        int cursorBase,
        int sorterBase,
        int accumulatorBase,
        int instructionBase,
        int parameterSlotBase,
        VdbeRowEquality equality,
        bool isPrimary,
        int outputOrProbeSet,
        IReadOnlyList<int> membershipSets,
        CompoundMembershipMode mode)
    {
        if (RelocateStructural(instruction, registerBase, cursorBase, sorterBase, accumulatorBase, instructionBase, parameterSlotBase)
            is { } structural)
        {
            return structural;
        }

        Register Reg(Register register) => new(register.Index + registerBase);
        RegisterRange Range(RegisterRange range) => new(Reg(range.Start), range.Count);

        return instruction switch
        {
            ResultRowInstruction x => isPrimary
                ? new CompoundResultRowInstruction(Range(x.Values), equality, outputOrProbeSet, membershipSets, mode)
                : new RowSetInsertInstruction(Range(x.Values), equality, outputOrProbeSet),
            _ => throw new StatementCompilationException(
                $"Cannot sequence unsupported opcode {instruction.Opcode} into a {(isPrimary ? "primary" : "auxiliary")} set-operation term."),
        };
    }

    // Relocates the register/cursor/sorter/accumulator/parameter-slot indices and jump targets shared by
    // every non emit-family opcode, returning null for the emit family (ResultRow, DistinctResultRow,
    // RowSetInsert, CompoundResultRow) so each caller rewrites it according to its own compound semantics.
    private static VdbeInstruction? RelocateStructural(
        VdbeInstruction instruction,
        int registerBase,
        int cursorBase,
        int sorterBase,
        int accumulatorBase,
        int instructionBase,
        int parameterSlotBase)
    {
        Register Reg(Register register) => new(register.Index + registerBase);
        Cursor Cur(Cursor cursor) => new(cursor.Index + cursorBase);
        Sorter Sort(Sorter sorter) => new(sorter.Index + sorterBase);
        Accumulator Acc(Accumulator accumulator) => new(accumulator.Index + accumulatorBase);
        ProgramCounter Pc(ProgramCounter counter) => new(counter.Offset + instructionBase);
        RegisterRange Range(RegisterRange range) => new(Reg(range.Start), range.Count);
        ParameterSlot Slot(ParameterSlot slot) => new(slot.Index + parameterSlotBase);

        return instruction switch
        {
            LoadConstantInstruction x => new LoadConstantInstruction(Reg(x.Destination), x.Value),
            LoadParameterInstruction x => new LoadParameterInstruction(Reg(x.Destination), Slot(x.Slot)),
            CopyInstruction x => new CopyInstruction(Reg(x.Source), Reg(x.Destination)),
            FunctionInstruction x => new FunctionInstruction(Reg(x.Destination), x.Function, Range(x.Arguments)),
            ArithmeticInstruction x => new ArithmeticInstruction(Reg(x.Destination), x.Operator, Range(x.Operands)),
            OpenReadCursorInstruction x => new OpenReadCursorInstruction(Cur(x.Cursor), x.TableName, x.ColumnCount),
            OpenWriteCursorInstruction x => new OpenWriteCursorInstruction(Cur(x.Cursor), x.TableName, x.ColumnCount),
            CloseCursorInstruction x => new CloseCursorInstruction(Cur(x.Cursor)),
            OpenSorterInstruction x => new OpenSorterInstruction(Sort(x.Sorter), x.Comparer, x.ColumnCount),
            SorterInsertInstruction x => new SorterInsertInstruction(Sort(x.Sorter), Range(x.Record)),
            SorterSortInstruction x => new SorterSortInstruction(Sort(x.Sorter), Pc(x.EmptyTarget)),
            SorterDataInstruction x => new SorterDataInstruction(Sort(x.Sorter), Range(x.Destination)),
            SorterNextInstruction x => new SorterNextInstruction(Sort(x.Sorter), Pc(x.LoopTarget)),
            CloseSorterInstruction x => new CloseSorterInstruction(Sort(x.Sorter)),
            GotoInstruction x => new GotoInstruction(Pc(x.Target)),
            JumpIfInstruction x => new JumpIfInstruction(Reg(x.Register), Pc(x.Target)),
            AggResetInstruction x => new AggResetInstruction(Acc(x.Accumulator)),
            AggStepInstruction x => new AggStepInstruction(Acc(x.Accumulator), x.Aggregate, Range(x.Arguments)),
            AggFinalizeInstruction x => new AggFinalizeInstruction(Acc(x.Accumulator), x.Aggregate, Reg(x.Destination)),
            SameGroupInstruction x => new SameGroupInstruction(Range(x.CurrentKey), Range(x.SavedKey), x.Comparer, Pc(x.SameGroupTarget)),
            RewindCursorInstruction x => new RewindCursorInstruction(Cur(x.Cursor), Pc(x.EmptyTarget)),
            ColumnInstruction x => new ColumnInstruction(Cur(x.Cursor), x.ColumnIndex, Reg(x.Destination)),
            RowIdInstruction x => new RowIdInstruction(Cur(x.Cursor), Reg(x.Destination)),
            DeleteInstruction x => new DeleteInstruction(Cur(x.Cursor)),
            InsertInstruction x => new InsertInstruction(Cur(x.Cursor)),
            UpdateInstruction x => new UpdateInstruction(Cur(x.Cursor)),
            CommitInstruction x => new CommitInstruction(Cur(x.Cursor)),
            FilterInstruction x => new FilterInstruction(Cur(x.Cursor), x.Predicate, Pc(x.FalseTarget), x.Description),
            FilterRowIdInstruction x => new FilterRowIdInstruction(Cur(x.Cursor), x.Predicate, Pc(x.FalseTarget), x.Description),
            FilterRegistersInstruction x => new FilterRegistersInstruction(Range(x.Row), x.Predicate, Pc(x.FalseTarget), x.Description),
            NextInstruction x => new NextInstruction(Cur(x.Cursor), Pc(x.LoopTarget)),
            YieldInstruction => instruction,
            HaltInstruction => instruction,
            ResultRowInstruction => null,
            DistinctResultRowInstruction => null,
            RowSetInsertInstruction => null,
            CompoundResultRowInstruction => null,
            _ => throw new StatementCompilationException(
                $"Cannot sequence unsupported opcode {instruction.Opcode} into a compound program."),
        };
    }

    private static int[] RelocateSetIndices(IReadOnlyList<int> indices, int distinctBase)
    {
        var relocated = new int[indices.Count];
        for (var i = 0; i < indices.Count; i++)
            relocated[i] = indices[i] + distinctBase;

        return relocated;
    }
}
