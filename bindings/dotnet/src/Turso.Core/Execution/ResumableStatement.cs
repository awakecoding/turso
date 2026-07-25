using System.Collections.ObjectModel;

namespace Turso.Core.Execution;

public enum ResumableStatementState
{
    Ready,
    Row,
    Yielded,
    Done,
    Disposed,
}

public enum ResumableStatementStepResult
{
    Row,
    Done,
    Yielded,
}

public sealed class StatementYieldedException : InvalidOperationException
{
    public StatementYieldedException()
        : base("Statement yielded. Call Resume before stepping again.")
    {
    }
}

public sealed class ResumableStatement : IDisposable
{
    private readonly SqlValue[] _registers;
    private readonly bool[] _openCursors;
    private readonly int[] _cursorPositions;
    private readonly SqlValue[]?[] _materializedRows;
    private readonly long[] _materializedRowIds;
    private readonly IReadOnlyList<SqlValue[]>?[] _joinCursorRows;
    private readonly SorterRuntime?[] _sorters;
    private readonly object?[] _accumulatorContexts;
    private readonly bool[] _accumulatorInitialized;
    private readonly List<SqlValue[]>?[] _distinctSets;
    private readonly int[] _rowSetPositions;
    private readonly WorkTableRuntime?[] _workTables;
    private readonly IReadOnlyList<VdbeCursorSource?>? _cursorSources;
    private readonly IReadOnlyList<VdbeWriteTarget?>? _writeTargets;
    private readonly VdbeTransactionContext _transaction = new();
    private VdbeParameterBinding? _binding;
    private ProgramCounter _instructionPointer;
    private ReadOnlyCollection<SqlValue>? _currentRow;
    private bool _disposed;

    public ResumableStatement(
        VdbeProgram program,
        IReadOnlyList<VdbeCursorSource?>? cursorSources = null,
        IReadOnlyList<VdbeWriteTarget?>? writeTargets = null,
        VdbeParameterBinding? parameterBinding = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (cursorSources is not null && cursorSources.Count != program.CursorCount)
        {
            throw new ArgumentException(
                $"Expected {program.CursorCount} cursor sources but received {cursorSources.Count}.",
                nameof(cursorSources));
        }

        if (writeTargets is not null && writeTargets.Count != program.CursorCount)
        {
            throw new ArgumentException(
                $"Expected {program.CursorCount} write targets but received {writeTargets.Count}.",
                nameof(writeTargets));
        }

        if (parameterBinding is not null)
            ValidateBindingWidth(program, parameterBinding);

        Program = program;
        _registers = new SqlValue[program.RegisterCount];
        _openCursors = new bool[program.CursorCount];
        _cursorPositions = new int[program.CursorCount];
        _materializedRows = new SqlValue[program.CursorCount][];
        _materializedRowIds = new long[program.CursorCount];
        _joinCursorRows = new IReadOnlyList<SqlValue[]>?[program.CursorCount];
        _sorters = new SorterRuntime?[program.SorterCount];
        _accumulatorContexts = new object?[program.AccumulatorCount];
        _accumulatorInitialized = new bool[program.AccumulatorCount];
        _distinctSets = new List<SqlValue[]>?[program.DistinctSetCount];
        _rowSetPositions = new int[program.DistinctSetCount];
        _workTables = new WorkTableRuntime?[program.WorkTableCount];
        _cursorSources = cursorSources;
        _writeTargets = writeTargets;
        _binding = parameterBinding;
        State = ResumableStatementState.Ready;
    }

    public VdbeProgram Program { get; }

    /// <summary>The parameter binding the program's <c>LoadParameter</c> opcodes read, or
    /// <see langword="null"/> when none has been supplied yet. A <see cref="Reset"/> preserves it (matching
    /// SQLite's <c>sqlite3_reset</c>, which does not clear bindings); <see cref="Rebind"/> replaces it.</summary>
    public VdbeParameterBinding? ParameterBinding => _binding;

    public ResumableStatementState State { get; private set; }

    public ProgramCounter InstructionPointer => _instructionPointer;

    public IReadOnlyList<SqlValue>? CurrentRow => _currentRow;

    /// <summary>The number of rows a write program has mutated so far, i.e. the
    /// rows-affected count an INSERT/UPDATE/DELETE reports.</summary>
    public int RowsAffected { get; private set; }

    /// <summary>The rowid recorded by the most recent <c>Commit</c> of an INSERT
    /// program, or <see langword="null"/> for UPDATE/DELETE and empty inserts.</summary>
    public long? LastInsertRowId { get; private set; }

    /// <summary>Whether the program currently has a transaction open through a
    /// <c>BeginTransaction</c> or <c>Savepoint</c> opcode that has not yet been committed or rolled back.
    /// This tracks the interpreter's register-scoped transaction state machine, not any durable store.</summary>
    public bool InTransaction => _transaction.InTransaction;

    /// <summary>The number of open transaction/savepoint frames: the outermost transaction plus any nested
    /// savepoints. Zero when no transaction is open.</summary>
    public int TransactionDepth => _transaction.Depth;

    /// <summary>The open savepoint names from outermost to innermost, with the anonymous
    /// <c>BeginTransaction</c> root reported as <see langword="null"/>. Exposed so callers can observe the
    /// transaction state machine directly.</summary>
    public IReadOnlyList<string?> TransactionSavepoints => _transaction.SavepointNames;

    public StatementStepResult Step()
    {
        return StepResumable() switch
        {
            ResumableStatementStepResult.Row => StatementStepResult.Row,
            ResumableStatementStepResult.Done => StatementStepResult.Done,
            ResumableStatementStepResult.Yielded => throw new StatementYieldedException(),
            _ => throw new InvalidOperationException("Unknown resumable statement step result."),
        };
    }

    public ResumableStatementStepResult StepResumable()
    {
        ThrowIfDisposed();

        if (State == ResumableStatementState.Yielded)
        {
            throw new InvalidOperationException(
                "Statement is yielded. Call Resume before stepping again.");
        }

        if (State == ResumableStatementState.Done)
            return ResumableStatementStepResult.Done;

        _currentRow = null;
        while (_instructionPointer.Offset < Program.Instructions.Count)
        {
            var instruction = Program.Instructions[_instructionPointer.Offset];
            switch (instruction)
            {
                case LoadConstantInstruction loadConstant:
                    _registers[loadConstant.Destination.Index] = loadConstant.Value;
                    AdvanceInstructionPointer();
                    break;
                case LoadParameterInstruction loadParameter:
                    _registers[loadParameter.Destination.Index] = RequireBinding().Get(loadParameter.Slot);
                    AdvanceInstructionPointer();
                    break;
                case CopyInstruction copy:
                    _registers[copy.Destination.Index] = _registers[copy.Source.Index];
                    AdvanceInstructionPointer();
                    break;
                case FunctionInstruction function:
                    {
                        // Snapshot the argument registers into a private tuple before invoking the
                        // delegate, so the function can neither observe a later register write nor mutate
                        // the register file, and write the (immutable) result only on success — a throwing
                        // delegate propagates out of the step with the destination register untouched.
                        var arguments = ReadRegisters(function.Arguments);
                        _registers[function.Destination.Index] = function.Function.Invoke(arguments);
                        AdvanceInstructionPointer();
                        break;
                    }
                case ArithmeticInstruction arithmetic:
                    {
                        // Snapshot the operand registers before computing so the destination may overlap an
                        // operand and a throwing evaluation (a type error) propagates out of the step with
                        // the destination register left untouched — no half-computed result is published.
                        var operands = ReadRegisters(arithmetic.Operands);
                        _registers[arithmetic.Destination.Index] =
                            VdbeArithmetic.Evaluate(arithmetic.Operator, operands);
                        AdvanceInstructionPointer();
                        break;
                    }
                case NumericAffinityInstruction numericAffinity:
                    {
                        var value = _registers[numericAffinity.Value.Index];
                        _registers[numericAffinity.Value.Index] = numericAffinity.Affinity.Apply(value);
                        AdvanceInstructionPointer();
                        break;
                    }
                case OpenReadCursorInstruction open:
                    OpenCursor(open.Cursor);
                    _cursorPositions[open.Cursor.Index] = -1;
                    _materializedRows[open.Cursor.Index] = null;
                    AdvanceInstructionPointer();
                    break;
                case OpenJoinCursorInstruction openJoin:
                    {
                        var rows = openJoin.Plan.Materialize();
                        OpenCursor(openJoin.Cursor);
                        _joinCursorRows[openJoin.Cursor.Index] = rows;
                        _cursorPositions[openJoin.Cursor.Index] = -1;
                        _materializedRows[openJoin.Cursor.Index] = null;
                        AdvanceInstructionPointer();
                        break;
                    }
                case OpenWriteCursorInstruction openWrite:
                    OpenCursor(openWrite.Cursor);
                    _cursorPositions[openWrite.Cursor.Index] = -1;
                    _materializedRows[openWrite.Cursor.Index] = null;
                    AdvanceInstructionPointer();
                    break;
                case CloseCursorInstruction close:
                    CloseCursor(close.Cursor);
                    _joinCursorRows[close.Cursor.Index] = null;
                    AdvanceInstructionPointer();
                    break;
                case RewindCursorInstruction rewind:
                    {
                        _materializedRows[rewind.Cursor.Index] = null;
                        if (CursorRowCount(rewind.Cursor) == 0)
                        {
                            _instructionPointer = rewind.EmptyTarget;
                        }
                        else
                        {
                            _cursorPositions[rewind.Cursor.Index] = 0;
                            AdvanceInstructionPointer();
                        }

                        break;
                    }
                case ColumnInstruction column:
                    {
                        var row = CurrentCursorRow(column.Cursor);
                        _registers[column.Destination.Index] = row[column.ColumnIndex];
                        AdvanceInstructionPointer();
                        break;
                    }
                case RowIdInstruction rowId:
                    {
                        _registers[rowId.Destination.Index] = SqlValue.Integer(CurrentCursorRowId(rowId.Cursor));
                        AdvanceInstructionPointer();
                        break;
                    }
                case FilterInstruction filter:
                    {
                        var row = CurrentCursorRow(filter.Cursor);
                        if (filter.Predicate(row))
                            AdvanceInstructionPointer();
                        else
                            _instructionPointer = filter.FalseTarget;

                        break;
                    }
                case FilterRowIdInstruction filterRowId:
                    {
                        var row = CurrentCursorRow(filterRowId.Cursor);
                        var rowId = CurrentCursorRowId(filterRowId.Cursor);
                        if (filterRowId.Predicate(row, rowId))
                            AdvanceInstructionPointer();
                        else
                            _instructionPointer = filterRowId.FalseTarget;

                        break;
                    }
                case FilterRegistersInstruction filterRegisters:
                    {
                        var row = ReadRegisters(filterRegisters.Row);
                        if (filterRegisters.Predicate(row))
                            AdvanceInstructionPointer();
                        else
                            _instructionPointer = filterRegisters.FalseTarget;

                        break;
                    }
                case ProjectRegistersInstruction project:
                    {
                        var input = ReadRegisters(project.Input);
                        var output = project.Transform(input)
                            ?? throw new InvalidOperationException("A register projection returned null.");
                        if (output.Length != project.Output.Count)
                        {
                            throw new InvalidOperationException(
                                $"A register projection declared {project.Output.Count} outputs but returned {output.Length}.");
                        }

                        Array.Copy(output, 0, _registers, project.Output.Start.Index, output.Length);
                        AdvanceInstructionPointer();
                        break;
                    }
                case DistinctFilterInstruction distinctFilter:
                    {
                        var candidate = ReadRegisters(distinctFilter.Values);
                        if (RowSetContains(
                                distinctFilter.DistinctSetIndex,
                                candidate,
                                distinctFilter.Equality))
                        {
                            _instructionPointer = distinctFilter.DuplicateTarget;
                        }
                        else
                        {
                            (_distinctSets[distinctFilter.DistinctSetIndex] ??= []).Add(candidate);
                            AdvanceInstructionPointer();
                        }

                        break;
                    }
                case NextInstruction next:
                    {
                        _materializedRows[next.Cursor.Index] = null;
                        var position = _cursorPositions[next.Cursor.Index] + 1;
                        _cursorPositions[next.Cursor.Index] = position;
                        if (position < CursorRowCount(next.Cursor))
                            _instructionPointer = next.LoopTarget;
                        else
                            AdvanceInstructionPointer();

                        break;
                    }
                case DeleteInstruction delete:
                    {
                        var target = RequireWriteTarget(delete.Cursor);
                        var deleteRow = target.DeleteRow
                            ?? throw new InvalidOperationException(
                                $"Cursor {delete.Cursor.Index} has no delete action bound.");
                        deleteRow(_cursorPositions[delete.Cursor.Index]);
                        RowsAffected = checked(RowsAffected + 1);
                        AdvanceInstructionPointer();
                        break;
                    }
                case InsertInstruction insert:
                    {
                        MutateCursorRow(insert.Cursor);
                        AdvanceInstructionPointer();
                        break;
                    }
                case UpdateInstruction update:
                    {
                        MutateCursorRow(update.Cursor);
                        AdvanceInstructionPointer();
                        break;
                    }
                case CommitInstruction commit:
                    {
                        var target = RequireWriteTarget(commit.Cursor);
                        LastInsertRowId = target.Commit();
                        AdvanceInstructionPointer();
                        break;
                    }
                case OpenSorterInstruction openSorter:
                    OpenSorter(openSorter);
                    AdvanceInstructionPointer();
                    break;
                case SorterInsertInstruction sorterInsert:
                    {
                        var runtime = RequireOpenSorter(sorterInsert.Sorter);
                        runtime.Insert(ReadRegisters(sorterInsert.Record));
                        AdvanceInstructionPointer();
                        break;
                    }
                case SorterSortInstruction sorterSort:
                    {
                        var runtime = RequireOpenSorter(sorterSort.Sorter);
                        if (runtime.Sort())
                            AdvanceInstructionPointer();
                        else
                            _instructionPointer = sorterSort.EmptyTarget;

                        break;
                    }
                case SorterDataInstruction sorterData:
                    {
                        var runtime = RequireOpenSorter(sorterData.Sorter);
                        var record = runtime.Current();
                        Array.Copy(record, 0, _registers, sorterData.Destination.Start.Index, record.Length);
                        AdvanceInstructionPointer();
                        break;
                    }
                case SorterNextInstruction sorterNext:
                    {
                        var runtime = RequireOpenSorter(sorterNext.Sorter);
                        if (runtime.MoveNext())
                            _instructionPointer = sorterNext.LoopTarget;
                        else
                            AdvanceInstructionPointer();

                        break;
                    }
                case CloseSorterInstruction closeSorter:
                    CloseSorter(closeSorter.Sorter);
                    AdvanceInstructionPointer();
                    break;
                case GotoInstruction gotoInstruction:
                    _instructionPointer = gotoInstruction.Target;
                    break;
                case JumpIfInstruction jumpIf:
                    {
                        var flag = _registers[jumpIf.Register.Index];
                        if (flag.Kind == SqlValueKind.Integer && flag.AsInteger() != 0)
                            _instructionPointer = jumpIf.Target;
                        else
                            AdvanceInstructionPointer();

                        break;
                    }
                case AggResetInstruction aggReset:
                    _accumulatorInitialized[aggReset.Accumulator.Index] = false;
                    _accumulatorContexts[aggReset.Accumulator.Index] = null;
                    AdvanceInstructionPointer();
                    break;
                case AggStepInstruction aggStep:
                    {
                        var index = aggStep.Accumulator.Index;
                        if (!_accumulatorInitialized[index])
                        {
                            _accumulatorContexts[index] = aggStep.Aggregate.CreateContext();
                            _accumulatorInitialized[index] = true;
                        }

                        _accumulatorContexts[index] = aggStep.Aggregate.Accumulate(
                            _accumulatorContexts[index],
                            ReadRegisters(aggStep.Arguments));
                        AdvanceInstructionPointer();
                        break;
                    }
                case AggFinalizeInstruction aggFinalize:
                    {
                        var index = aggFinalize.Accumulator.Index;
                        // Finalizing an accumulator that was reset but never stepped yields the
                        // aggregate's empty-input value, so empty groups still produce a result.
                        var context = _accumulatorInitialized[index]
                            ? _accumulatorContexts[index]
                            : aggFinalize.Aggregate.CreateContext();
                        _registers[aggFinalize.Destination.Index] = aggFinalize.Aggregate.Finalize(context);
                        AdvanceInstructionPointer();
                        break;
                    }
                case SameGroupInstruction sameGroup:
                    {
                        var current = ReadRegisters(sameGroup.CurrentKey);
                        var saved = ReadRegisters(sameGroup.SavedKey);
                        if (sameGroup.Comparer(current, saved))
                            _instructionPointer = sameGroup.SameGroupTarget;
                        else
                            AdvanceInstructionPointer();

                        break;
                    }
                case YieldInstruction:
                    AdvanceInstructionPointer();
                    State = ResumableStatementState.Yielded;
                    return ResumableStatementStepResult.Yielded;
                case ResultRowInstruction resultRow:
                    _currentRow = Array.AsReadOnly(ReadRegisters(resultRow.Values));
                    AdvanceInstructionPointer();
                    State = ResumableStatementState.Row;
                    return ResumableStatementStepResult.Row;
                case DistinctResultRowInstruction distinctRow:
                    {
                        // Compare the candidate against every row already emitted through this
                        // distinct set. Duplicates advance without producing a row (continuing the
                        // dispatch loop); the first occurrence of a row is recorded and yielded.
                        var candidate = ReadRegisters(distinctRow.Values);
                        var seen = _distinctSets[distinctRow.DistinctSetIndex] ??= [];
                        var duplicate = false;
                        foreach (var emitted in seen)
                        {
                            if (distinctRow.Equality(emitted, candidate))
                            {
                                duplicate = true;
                                break;
                            }
                        }

                        AdvanceInstructionPointer();
                        if (duplicate)
                            break;

                        seen.Add(candidate);
                        _currentRow = Array.AsReadOnly(candidate);
                        State = ResumableStatementState.Row;
                        return ResumableStatementStepResult.Row;
                    }
                case RowSetInsertInstruction rowSetInsert:
                    {
                        // Record the candidate into its probe set for later membership tests, keeping one
                        // representative per distinct tuple. Never produces a row: control just advances.
                        var candidate = ReadRegisters(rowSetInsert.Values);
                        var set = _distinctSets[rowSetInsert.RowSetIndex] ??= [];
                        var present = false;
                        foreach (var stored in set)
                        {
                            if (rowSetInsert.Equality(stored, candidate))
                            {
                                present = true;
                                break;
                            }
                        }

                        if (!present)
                            set.Add(candidate);

                        AdvanceInstructionPointer();
                        break;
                    }
                case RowSetRewindInstruction rowSetRewind:
                    {
                        var set = _distinctSets[rowSetRewind.RowSetIndex];
                        _rowSetPositions[rowSetRewind.RowSetIndex] = 0;
                        if (set is null || set.Count == 0)
                        {
                            _instructionPointer = rowSetRewind.EmptyTarget;
                            break;
                        }

                        CopyRowSetRow(set[0], rowSetRewind.Destination);
                        AdvanceInstructionPointer();
                        break;
                    }
                case RowSetNextInstruction rowSetNext:
                    {
                        var set = _distinctSets[rowSetNext.RowSetIndex]
                            ?? throw new InvalidOperationException(
                                $"Cannot advance unopened row set {rowSetNext.RowSetIndex}.");
                        var position = checked(++_rowSetPositions[rowSetNext.RowSetIndex]);
                        if (position < set.Count)
                        {
                            CopyRowSetRow(set[position], rowSetNext.Destination);
                            _instructionPointer = rowSetNext.LoopTarget;
                        }
                        else
                        {
                            AdvanceInstructionPointer();
                        }

                        break;
                    }
                case CompoundResultRowInstruction compoundRow:
                    {
                        // Emit the candidate only when it satisfies the membership condition against every
                        // probe set and is novel to the output set. A failing or duplicate candidate advances
                        // without producing a row, so the primary term keeps streaming in its own order.
                        var candidate = ReadRegisters(compoundRow.Values);
                        var passesMembership = true;
                        foreach (var membershipSetIndex in compoundRow.MembershipSetIndices)
                        {
                            var contained = RowSetContains(membershipSetIndex, candidate, compoundRow.Equality);
                            var required = compoundRow.Mode == CompoundMembershipMode.PresentInAll;
                            if (contained != required)
                            {
                                passesMembership = false;
                                break;
                            }
                        }

                        AdvanceInstructionPointer();
                        if (!passesMembership)
                            break;

                        var output = _distinctSets[compoundRow.OutputSetIndex] ??= [];
                        var duplicate = false;
                        foreach (var emitted in output)
                        {
                            if (compoundRow.Equality(emitted, candidate))
                            {
                                duplicate = true;
                                break;
                            }
                        }

                        if (duplicate)
                            break;

                        output.Add(candidate);
                        _currentRow = Array.AsReadOnly(candidate);
                        State = ResumableStatementState.Row;
                        return ResumableStatementStepResult.Row;
                    }
                case GuardedRowInstruction guardedRow:
                    {
                        var candidate = ReadRegisters(guardedRow.Values);
                        var accepted = true;
                        foreach (var guard in guardedRow.Guards)
                        {
                            switch (guard)
                            {
                                case DistinctRowGuard distinctGuard:
                                    accepted = TryInsertRowSet(
                                        distinctGuard.RowSetIndex,
                                        candidate,
                                        distinctGuard.Equality);
                                    break;
                                case MembershipRowGuard membershipGuard:
                                    foreach (var rowSetIndex in membershipGuard.RowSetIndices)
                                    {
                                        var contained = RowSetContains(
                                            rowSetIndex,
                                            candidate,
                                            membershipGuard.Equality);
                                        var required =
                                            membershipGuard.Mode == CompoundMembershipMode.PresentInAll;
                                        if (contained != required)
                                        {
                                            accepted = false;
                                            break;
                                        }
                                    }

                                    break;
                                default:
                                    throw new InvalidOperationException(
                                        $"Validated guarded row contains unsupported guard {guard.GetType().Name}.");
                            }

                            if (!accepted)
                                break;
                        }

                        AdvanceInstructionPointer();
                        if (!accepted)
                            break;

                        switch (guardedRow.Destination)
                        {
                            case ResultRowDestination:
                                _currentRow = Array.AsReadOnly(candidate);
                                State = ResumableStatementState.Row;
                                return ResumableStatementStepResult.Row;
                            case RowSetDestination destination:
                                TryInsertRowSet(destination.RowSetIndex, candidate, destination.Equality);
                                break;
                            default:
                                throw new InvalidOperationException(
                                    $"Validated guarded row contains unsupported destination {guardedRow.Destination.GetType().Name}.");
                        }

                        break;
                    }
                case OffsetGateInstruction offsetGate:
                    {
                        // Skip the first `offset` candidate rows: while the counter is positive, decrement
                        // it and jump to the loop-advance instruction after the gated result row. Skipped
                        // rows never reach the limit gate, so they are not counted against LIMIT.
                        var counter = _registers[offsetGate.Counter.Index];
                        if (counter.Kind == SqlValueKind.Integer && counter.AsInteger() > 0)
                        {
                            _registers[offsetGate.Counter.Index] = SqlValue.Integer(counter.AsInteger() - 1);
                            _instructionPointer = offsetGate.SkipTarget;
                        }
                        else
                        {
                            AdvanceInstructionPointer();
                        }

                        break;
                    }
                case LimitGateInstruction limitGate:
                    {
                        // Emit exactly `limit` rows: while the counter is positive, decrement it and fall
                        // through so the gated result row is emitted; once it reaches zero, jump to the
                        // program's terminating Halt so no further rows are produced.
                        var counter = _registers[limitGate.Counter.Index];
                        if (counter.Kind == SqlValueKind.Integer && counter.AsInteger() > 0)
                        {
                            _registers[limitGate.Counter.Index] = SqlValue.Integer(counter.AsInteger() - 1);
                            AdvanceInstructionPointer();
                        }
                        else
                        {
                            _instructionPointer = limitGate.DoneTarget;
                        }

                        break;
                    }
                case BeginTransactionInstruction:
                    _transaction.Begin(_registers);
                    AdvanceInstructionPointer();
                    break;
                case CommitTransactionInstruction:
                    _transaction.Commit();
                    AdvanceInstructionPointer();
                    break;
                case RollbackTransactionInstruction:
                    _transaction.Rollback(_registers);
                    AdvanceInstructionPointer();
                    break;
                case SavepointInstruction savepoint:
                    _transaction.Savepoint(savepoint.Name, _registers);
                    AdvanceInstructionPointer();
                    break;
                case ReleaseSavepointInstruction release:
                    _transaction.Release(release.Name);
                    AdvanceInstructionPointer();
                    break;
                case RollbackToSavepointInstruction rollbackTo:
                    _transaction.RollbackTo(rollbackTo.Name, _registers);
                    AdvanceInstructionPointer();
                    break;
                case OpenWorkTableInstruction openWorkTable:
                    OpenWorkTable(openWorkTable);
                    AdvanceInstructionPointer();
                    break;
                case SeedWorkTableInstruction seed:
                    {
                        var runtime = RequireOpenWorkTable(seed.WorkTable);
                        runtime.Seed(ReadRegisters(seed.Row));
                        AdvanceInstructionPointer();
                        break;
                    }
                case WorkTableStepInstruction step:
                    {
                        // Dequeue the next frontier row in FIFO (breadth-first) order into the destination
                        // register block and remember it as the worktable's current row, or fall through to
                        // the loop-exit target when the frontier is drained.
                        var runtime = RequireOpenWorkTable(step.WorkTable);
                        if (runtime.TryStep(out var row))
                        {
                            Array.Copy(row, 0, _registers, step.Destination.Start.Index, row.Length);
                            AdvanceInstructionPointer();
                        }
                        else
                        {
                            _instructionPointer = step.DoneTarget;
                        }

                        break;
                    }
                case WorkTableExpandInstruction expand:
                    {
                        // Expand the current frontier row (held in the source registers) one generation
                        // deeper, enqueuing each descendant under the worktable's dedup and guards. Produces
                        // no result row: the loop's ResultRow emits the dequeued row, this only grows the queue.
                        var runtime = RequireOpenWorkTable(expand.WorkTable);
                        runtime.Expand(ReadRegisters(expand.Source), expand.Transform);
                        AdvanceInstructionPointer();
                        break;
                    }
                case WorkTableExpandGenerationInstruction expandGeneration:
                    {
                        var runtime = RequireOpenWorkTable(expandGeneration.WorkTable);
                        runtime.ExpandGeneration(
                            ReadRegisters(expandGeneration.Source),
                            expandGeneration.Transform);
                        AdvanceInstructionPointer();
                        break;
                    }
                case CloseWorkTableInstruction closeWorkTable:
                    CloseWorkTable(closeWorkTable.WorkTable);
                    AdvanceInstructionPointer();
                    break;
                case HaltInstruction:
                    Array.Clear(_openCursors);
                    Array.Clear(_joinCursorRows);
                    AdvanceInstructionPointer();
                    State = ResumableStatementState.Done;
                    return ResumableStatementStepResult.Done;
                default:
                    throw new InvalidOperationException(
                        $"Validated VDBE program contains unsupported opcode {instruction.Opcode}.");
            }
        }

        throw new InvalidOperationException("Validated VDBE program ended without halting.");
    }

    public void Resume()
    {
        ThrowIfDisposed();
        if (State != ResumableStatementState.Yielded)
            throw new InvalidOperationException("Only a yielded statement can be resumed.");

        State = ResumableStatementState.Ready;
    }

    public void Reset()
    {
        ThrowIfDisposed();

        Array.Clear(_registers);
        Array.Clear(_openCursors);
        Array.Clear(_cursorPositions);
        Array.Clear(_materializedRows);
        Array.Clear(_materializedRowIds);
        Array.Clear(_joinCursorRows);
        Array.Clear(_sorters);
        Array.Clear(_accumulatorContexts);
        Array.Clear(_accumulatorInitialized);
        Array.Clear(_distinctSets);
        Array.Clear(_rowSetPositions);
        Array.Clear(_workTables);
        _transaction.Reset();
        _currentRow = null;
        _instructionPointer = default;
        RowsAffected = 0;
        LastInsertRowId = null;
        // The parameter binding is intentionally preserved across Reset, mirroring SQLite's
        // sqlite3_reset (which rewinds execution but keeps bindings), so a program re-runs with the same
        // parameters. Rebind replaces it explicitly.
        State = ResumableStatementState.Ready;
    }

    /// <summary>
    /// Replaces the statement's parameter binding, so the next run reads fresh late-bound values without
    /// rebuilding the program. The binding's width must match the program's
    /// <see cref="VdbeProgram.ParameterSlotCount"/>. Rebinding is only allowed from the
    /// <see cref="ResumableStatementState.Ready"/> state (a freshly constructed statement or one that has
    /// been <see cref="Reset"/>), so it can never change parameters that an in-flight run has already read.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="parameterBinding"/> is null.</exception>
    /// <exception cref="VdbeParameterBindingException">The binding's width does not match the program.</exception>
    /// <exception cref="InvalidOperationException">The statement is not in the Ready state.</exception>
    public void Rebind(VdbeParameterBinding parameterBinding)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(parameterBinding);
        if (State != ResumableStatementState.Ready)
        {
            throw new InvalidOperationException(
                "Parameters can only be rebound from the Ready state; call Reset before rebinding a statement that has started, yielded, or finished.");
        }

        ValidateBindingWidth(Program, parameterBinding);
        _binding = parameterBinding;
    }

    public SqlValue GetRegister(Register register)
    {
        ThrowIfDisposed();
        ValidateRegister(register);
        return _registers[register.Index];
    }

    public bool IsCursorOpen(Cursor cursor)
    {
        ThrowIfDisposed();
        ValidateCursor(cursor);
        return _openCursors[cursor.Index];
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Array.Clear(_registers);
        Array.Clear(_openCursors);
        Array.Clear(_materializedRows);
        Array.Clear(_joinCursorRows);
        Array.Clear(_sorters);
        Array.Clear(_accumulatorContexts);
        Array.Clear(_accumulatorInitialized);
        Array.Clear(_distinctSets);
        Array.Clear(_rowSetPositions);
        Array.Clear(_workTables);
        _transaction.Reset();
        _binding = null;
        _currentRow = null;
        State = ResumableStatementState.Disposed;
        _disposed = true;
    }

    private void OpenCursor(Cursor cursor)
    {
        if (_openCursors[cursor.Index])
            throw new InvalidOperationException($"Cursor {cursor.Index} is already open.");

        _openCursors[cursor.Index] = true;
    }

    private void CloseCursor(Cursor cursor)
    {
        if (!_openCursors[cursor.Index])
            throw new InvalidOperationException($"Cursor {cursor.Index} is not open.");

        _openCursors[cursor.Index] = false;
    }

    private void OpenSorter(OpenSorterInstruction instruction)
    {
        if (_sorters[instruction.Sorter.Index] is not null)
            throw new InvalidOperationException($"Sorter {instruction.Sorter.Index} is already open.");

        _sorters[instruction.Sorter.Index] = new SorterRuntime(instruction.Comparer, instruction.ColumnCount);
    }

    private void CloseSorter(Sorter sorter)
    {
        if (_sorters[sorter.Index] is null)
            throw new InvalidOperationException($"Sorter {sorter.Index} is not open.");

        _sorters[sorter.Index] = null;
    }

    private SorterRuntime RequireOpenSorter(Sorter sorter)
        => _sorters[sorter.Index]
            ?? throw new InvalidOperationException($"Sorter {sorter.Index} is not open.");

    private void OpenWorkTable(OpenWorkTableInstruction instruction)
    {
        if (_workTables[instruction.WorkTable.Index] is not null)
            throw new InvalidOperationException($"Work table {instruction.WorkTable.Index} is already open.");

        _workTables[instruction.WorkTable.Index] = new WorkTableRuntime(
            instruction.ColumnCount,
            instruction.Mode,
            instruction.MaxRows,
            instruction.MaxDepth,
            instruction.Equality);
    }

    private void CloseWorkTable(WorkTable workTable)
    {
        if (_workTables[workTable.Index] is null)
            throw new InvalidOperationException($"Work table {workTable.Index} is not open.");

        _workTables[workTable.Index] = null;
    }

    private WorkTableRuntime RequireOpenWorkTable(WorkTable workTable)
        => _workTables[workTable.Index]
            ?? throw new InvalidOperationException($"Work table {workTable.Index} is not open.");

    // The binding a LoadParameter opcode reads. A program that references parameter slots must be run
    // with a matching binding; a missing binding is a hard error rather than a silent NULL, so an unbound
    // parameter can never be mistaken for a bound NULL value.
    private VdbeParameterBinding RequireBinding()
        => _binding
            ?? throw new VdbeParameterBindingException(
                $"The program reads {Program.ParameterSlotCount} parameter slot(s) but no binding was supplied; construct the statement with a binding or call Rebind.");

    private static void ValidateBindingWidth(VdbeProgram program, VdbeParameterBinding binding)
    {
        if (binding.Count != program.ParameterSlotCount)
        {
            throw new VdbeParameterBindingException(
                $"The binding supplies {binding.Count} parameter slot(s) but the program declares {program.ParameterSlotCount}.");
        }
    }

    private VdbeCursorSource RequireCursorSource(Cursor cursor)
    {
        var source = _cursorSources is not null && cursor.Index < _cursorSources.Count
            ? _cursorSources[cursor.Index]
            : null;

        return source
            ?? throw new InvalidOperationException(
                $"Cursor {cursor.Index} has no bound row source.");
    }

    private VdbeWriteTarget? WriteTargetOrNull(Cursor cursor)
        => _writeTargets is not null && cursor.Index < _writeTargets.Count
            ? _writeTargets[cursor.Index]
            : null;

    private VdbeWriteTarget RequireWriteTarget(Cursor cursor)
        => WriteTargetOrNull(cursor)
            ?? throw new InvalidOperationException(
                $"Cursor {cursor.Index} has no bound write target.");

    // A cursor's iteration length comes from its write target (INSERT value rows or
    // scanned UPDATE/DELETE rows) or, failing that, its read source.
    private int CursorRowCount(Cursor cursor)
    {
        if (_joinCursorRows[cursor.Index] is { } joinedRows)
            return joinedRows.Count;

        var writeTarget = WriteTargetOrNull(cursor);
        return writeTarget is not null
            ? writeTarget.RowCount
            : RequireCursorSource(cursor).Rows.Count;
    }

    // Runs a mutation delegate for the current position and materializes the written
    // (row, rowid) so a following Column/RowId observes the new values, not the source.
    private void MutateCursorRow(Cursor cursor)
    {
        var target = RequireWriteTarget(cursor);
        var mutate = target.MutateRow
            ?? throw new InvalidOperationException(
                $"Cursor {cursor.Index} has no mutation action bound.");
        var mutation = mutate(_cursorPositions[cursor.Index]);
        _materializedRows[cursor.Index] = mutation.Row;
        _materializedRowIds[cursor.Index] = mutation.RowId;
        RowsAffected = checked(RowsAffected + 1);
    }

    private SqlValue[] CurrentCursorRow(Cursor cursor)
    {
        // A mutation opcode materializes the written row; until then the row comes
        // from the write target's scan rows (UPDATE/DELETE) or the read source.
        if (_materializedRows[cursor.Index] is { } materialized)
            return materialized;

        var position = _cursorPositions[cursor.Index];
        var count = CursorRowCount(cursor);
        if (position < 0 || position >= count)
            throw new InvalidOperationException($"Cursor {cursor.Index} is not positioned on a row.");

        var writeTarget = WriteTargetOrNull(cursor);
        if (writeTarget?.GetRow is { } getRow)
            return getRow(position);

        if (_joinCursorRows[cursor.Index] is { } joinedRows)
            return joinedRows[position];

        return RequireCursorSource(cursor).Rows[position];
    }

    private long CurrentCursorRowId(Cursor cursor)
    {
        if (_joinCursorRows[cursor.Index] is not null)
        {
            throw new InvalidOperationException(
                $"Join cursor {cursor.Index} exposes source rowids as hidden columns, not as one cursor rowid.");
        }

        if (_materializedRows[cursor.Index] is not null)
            return _materializedRowIds[cursor.Index];

        var position = _cursorPositions[cursor.Index];
        var count = CursorRowCount(cursor);
        if (position < 0 || position >= count)
            throw new InvalidOperationException($"Cursor {cursor.Index} is not positioned on a row.");

        var source = _cursorSources is not null && cursor.Index < _cursorSources.Count
            ? _cursorSources[cursor.Index]
            : null;
        if (source?.RowIds is { } rowIds)
            return rowIds[position];

        var target = RequireWriteTarget(cursor);
        var getRowId = target.GetRowId
            ?? throw new InvalidOperationException(
                $"Cursor {cursor.Index} has no rowid source bound.");
        return getRowId(position);
    }

    private SqlValue[] ReadRegisters(RegisterRange range)
    {
        var values = new SqlValue[range.Count];
        Array.Copy(_registers, range.Start.Index, values, 0, range.Count);
        return values;
    }

    // Whether the candidate is present in the row set under the supplied equality. An unpopulated set
    // (null) holds no rows, so membership is false — INTERSECT against an empty term yields nothing and
    // EXCEPT against an empty term keeps every candidate.
    private bool RowSetContains(int rowSetIndex, SqlValue[] candidate, VdbeRowEquality equality)
    {
        var set = _distinctSets[rowSetIndex];
        if (set is null)
            return false;

        foreach (var stored in set)
        {
            if (equality(stored, candidate))
                return true;
        }

        return false;
    }

    private bool TryInsertRowSet(int rowSetIndex, SqlValue[] candidate, VdbeRowEquality equality)
    {
        if (RowSetContains(rowSetIndex, candidate, equality))
            return false;

        (_distinctSets[rowSetIndex] ??= []).Add(candidate);
        return true;
    }

    private void CopyRowSetRow(SqlValue[] row, RegisterRange destination)
    {
        if (row.Length != destination.Count)
        {
            throw new InvalidOperationException(
                $"Row-set row has {row.Length} columns but destination has {destination.Count} registers.");
        }

        Array.Copy(row, 0, _registers, destination.Start.Index, row.Length);
    }

    private void AdvanceInstructionPointer()
    {
        _instructionPointer = new ProgramCounter(checked(_instructionPointer.Offset + 1));
    }

    private void ValidateRegister(Register register)
    {
        if (register.Index >= Program.RegisterCount)
            throw new ArgumentOutOfRangeException(nameof(register));
    }

    private void ValidateCursor(Cursor cursor)
    {
        if (cursor.Index >= Program.CursorCount)
            throw new ArgumentOutOfRangeException(nameof(cursor));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    // Holds one sorter's buffered records and its drain cursor. Records are copied on
    // insert so overwriting the source registers between iterations cannot mutate rows
    // already stored. Sorting is stable: equal-key rows keep their insertion order.
    private sealed class SorterRuntime
    {
        private readonly VdbeRowComparer _comparer;
        private readonly int _columnCount;
        private readonly List<SqlValue[]> _rows = [];
        private bool _sorted;
        private int _position = -1;

        public SorterRuntime(VdbeRowComparer comparer, int columnCount)
        {
            _comparer = comparer;
            _columnCount = columnCount;
        }

        public void Insert(SqlValue[] record)
        {
            if (_sorted)
                throw new InvalidOperationException("Cannot insert into a sorter after it has been sorted.");
            if (record.Length != _columnCount)
            {
                throw new InvalidOperationException(
                    $"Sorter stores {_columnCount}-column records but received {record.Length} values.");
            }

            _rows.Add(record);
        }

        // Sorts the buffered records and positions on the first one. Returns false (and
        // leaves the sorter unpositioned) when there is nothing to drain.
        public bool Sort()
        {
            _sorted = true;
            if (_rows.Count == 0)
            {
                _position = -1;
                return false;
            }

            // Stable order: decorate with insertion index and break ties on it so the
            // underlying unstable sort cannot reorder equal-key rows.
            var order = new int[_rows.Count];
            for (var index = 0; index < order.Length; index++)
                order[index] = index;

            Array.Sort(order, (left, right) =>
            {
                var comparison = _comparer(_rows[left], _rows[right]);
                return comparison != 0 ? comparison : left.CompareTo(right);
            });

            var sorted = new List<SqlValue[]>(_rows.Count);
            foreach (var index in order)
                sorted.Add(_rows[index]);

            _rows.Clear();
            _rows.AddRange(sorted);
            _position = 0;
            return true;
        }

        public SqlValue[] Current()
        {
            if (!_sorted)
                throw new InvalidOperationException("Sorter must be sorted before reading a record.");
            if (_position < 0 || _position >= _rows.Count)
                throw new InvalidOperationException("Sorter is not positioned on a record.");

            return _rows[_position];
        }

        // Advances to the next ordered record, returning whether one remains.
        public bool MoveNext()
        {
            if (!_sorted)
                throw new InvalidOperationException("Sorter must be sorted before advancing.");

            _position++;
            return _position < _rows.Count;
        }
    }

    // Holds one recursive worktable's runtime state: the FIFO frontier of (row, depth) pairs, the optional
    // de-duplication set (for UNION/DISTINCT), the admitted-row count for the row guard, and the depth of
    // the row most recently dequeued by Step (which the following Expand expands from). Every admitted row is
    // snapshotted on admission (see TryAdmit), so neither overwriting the source registers between iterations
    // nor a transform that reuses a single output buffer across the rows it emits can mutate a queued
    // frontier row or a recorded distinct representative. The recursion itself — FIFO ordering, re-feeding
    // descendants, de-duplication, depth bounding, and the row cap — lives here and is driven step by step by
    // the interpreter loop; the transform delegate only computes one generation from one row.
    private sealed class WorkTableRuntime
    {
        private readonly int _columnCount;
        private readonly WorkTableDedupMode _mode;
        private readonly int _maxRows;
        private readonly int _maxDepth;
        private readonly VdbeRowEquality? _equality;
        private readonly Queue<(SqlValue[] Row, int Depth)> _frontier = new();
        private readonly List<SqlValue[]>? _seen;
        private readonly List<SqlValue[]> _generation = [];
        private int _admitted;
        private bool _hasCurrent;
        private int _currentDepth;

        public WorkTableRuntime(
            int columnCount,
            WorkTableDedupMode mode,
            int maxRows,
            int maxDepth,
            VdbeRowEquality? equality)
        {
            _columnCount = columnCount;
            _mode = mode;
            _maxRows = maxRows;
            _maxDepth = maxDepth;
            _equality = equality;
            _seen = mode == WorkTableDedupMode.Distinct ? [] : null;
        }

        // Admits a seed (anchor) row at depth 0. Distinct duplicates are dropped; admission counts against
        // the row guard.
        public void Seed(SqlValue[] row)
        {
            RequireWidth(row);
            TryAdmit(row, depth: 0);
        }

        // Dequeues the next frontier row and records its depth as the current expansion depth. Returns false
        // (and clears the current row) when the frontier is drained.
        public bool TryStep(out SqlValue[] row)
        {
            if (_frontier.Count == 0)
            {
                _hasCurrent = false;
                row = [];
                return false;
            }

            var (dequeued, depth) = _frontier.Dequeue();
            _hasCurrent = true;
            _currentDepth = depth;
            row = dequeued;
            return true;
        }

        // Expands the current frontier row one generation deeper. The depth guard cuts expansion off once the
        // current row sits at MaxDepth, so no descendant beyond the bounded slice is ever produced.
        public void Expand(SqlValue[] frontierRow, VdbeRecursiveTransform transform)
        {
            if (!_hasCurrent)
            {
                throw new InvalidOperationException(
                    "Work table has no current row to expand; a WorkTableStep must dequeue a row before WorkTableExpand.");
            }

            if (_currentDepth >= _maxDepth)
                return;

            var children = transform(frontierRow)
                ?? throw new InvalidOperationException("A recursive transform must not return a null row list.");

            var childDepth = checked(_currentDepth + 1);
            foreach (var child in children)
            {
                if (child is null)
                    throw new InvalidOperationException("A recursive transform must not return a null row.");

                RequireWidth(child);
                TryAdmit(child, childDepth);
            }
        }

        public void ExpandGeneration(
            SqlValue[] frontierRow,
            VdbeRecursiveGenerationTransform transform)
        {
            if (!_hasCurrent)
            {
                throw new InvalidOperationException(
                    "Work table has no current row to expand; a WorkTableStep must dequeue a row before WorkTableExpandGeneration.");
            }

            if (_currentDepth >= _maxDepth)
                return;

            RequireWidth(frontierRow);
            _generation.Add([.. frontierRow]);
            if (_frontier.TryPeek(out var next) && next.Depth == _currentDepth)
                return;

            var frontier = _generation.ToArray();
            _generation.Clear();
            var children = transform(frontier)
                ?? throw new InvalidOperationException(
                    "A recursive generation transform must not return a null row list.");
            var childDepth = checked(_currentDepth + 1);
            foreach (var child in children)
            {
                if (child is null)
                    throw new InvalidOperationException(
                        "A recursive generation transform must not return a null row.");

                RequireWidth(child);
                TryAdmit(child, childDepth);
            }
        }

        // Admits a row: dropped as a duplicate under Distinct, otherwise counted against the row guard,
        // recorded for future de-duplication, and enqueued for later draining. Returns whether it was admitted.
        //
        // Admission is the ownership boundary. `row` is transient storage the caller may keep mutating: a
        // seed's register snapshot is discarded after this call, but more importantly a recursive transform
        // is free to reuse one output buffer across the rows it emits and across successive expansions.
        // Snapshot the row here so the de-duplication representative and the queued frontier entry reference
        // storage this runtime owns and never mutates in place. Without the copy a later overwrite of that
        // buffer would rewrite an already-admitted row, corrupting the frontier (a queued row would surface
        // with the wrong values) and the distinct set (a genuinely new row would be misread as a duplicate).
        // The dedup scan compares the caller's `row` before copying, so the snapshot adds no work to the
        // rejection path.
        private bool TryAdmit(SqlValue[] row, int depth)
        {
            if (_seen is not null)
            {
                foreach (var stored in _seen)
                {
                    if (_equality!(stored, row))
                        return false;
                }
            }

            if (_admitted >= _maxRows)
                throw new RecursiveWorkTableOverflowException(_maxRows);

            var owned = CloneRow(row);
            _admitted++;
            _seen?.Add(owned);
            _frontier.Enqueue((owned, depth));
            return true;
        }

        // Shallow snapshot of a record. SqlValue is an immutable value type and blob payloads are exposed as
        // read-only memory, so copying the array elements clones the row faithfully without duplicating (or
        // ever exposing mutable) blob storage.
        private static SqlValue[] CloneRow(SqlValue[] row)
        {
            var copy = new SqlValue[row.Length];
            Array.Copy(row, copy, row.Length);
            return copy;
        }

        private void RequireWidth(SqlValue[] row)
        {
            if (row.Length != _columnCount)
            {
                throw new InvalidOperationException(
                    $"Work table stores {_columnCount}-column records but received {row.Length} values.");
            }
        }
    }
}
