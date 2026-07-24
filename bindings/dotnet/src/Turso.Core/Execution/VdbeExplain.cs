using System.Globalization;
using System.Text;

namespace Turso.Core.Execution;

/// <summary>
/// Renders <see cref="VdbeInstruction"/>s into the <c>addr/opcode/p1/p2/p3/p4/comment</c>
/// shape that <c>EXPLAIN</c> reports. It mirrors the descriptions the wired database
/// emits for the shared opcodes and extends them to the sorter opcode family, so a
/// program that materializes and orders rows can be described end to end while its
/// wiring into the statement pipeline is completed by the database layer.
/// </summary>
public static class VdbeExplain
{
    /// <summary>The column names an <c>EXPLAIN</c> result set exposes.</summary>
    public static string[] Columns() => ["addr", "opcode", "p1", "p2", "p3", "p4", "comment"];

    /// <summary>Describes a whole program as one <c>EXPLAIN</c> row per instruction, with
    /// the address counting up from zero.</summary>
    public static IReadOnlyList<SqlValue[]> Describe(VdbeProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        var rows = new List<SqlValue[]>(program.Instructions.Count);
        for (var address = 0; address < program.Instructions.Count; address++)
        {
            var instruction = program.Instructions[address];
            var (p1, p2, p3, p4, comment) = Describe(instruction);
            rows.Add(
            [
                SqlValue.Integer(address),
                SqlValue.Text(instruction.Opcode.ToString()),
                SqlValue.Integer(p1),
                SqlValue.Integer(p2),
                SqlValue.Integer(p3),
                p4 is null ? SqlValue.Null : SqlValue.Text(p4),
                SqlValue.Text(comment),
            ]);
        }

        return rows;
    }

    /// <summary>Describes a single instruction as its <c>(p1, p2, p3, p4, comment)</c> tuple.</summary>
    public static (long P1, long P2, long P3, string? P4, string Comment) Describe(VdbeInstruction instruction)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        return instruction switch
        {
            LoadConstantInstruction load => (
                load.Destination.Index,
                0,
                0,
                FormatValue(load.Value),
                $"r[{load.Destination.Index}]={FormatValue(load.Value)}"),
            LoadParameterInstruction loadParameter => (
                loadParameter.Destination.Index,
                loadParameter.Slot.Index,
                0,
                $"param[{loadParameter.Slot.Index}]",
                $"r[{loadParameter.Destination.Index}]=param[{loadParameter.Slot.Index}]"),
            CopyInstruction copy => (
                copy.Source.Index,
                copy.Destination.Index,
                0,
                null,
                $"r[{copy.Destination.Index}]=r[{copy.Source.Index}]"),
            FunctionInstruction function => (
                function.Destination.Index,
                function.Arguments.Start.Index,
                function.Arguments.Count,
                function.Function.Name,
                $"r[{function.Destination.Index}]={function.Function.Name}({FormatRange(function.Arguments)})"),
            ArithmeticInstruction arithmetic => (
                arithmetic.Destination.Index,
                arithmetic.Operands.Start.Index,
                arithmetic.Operands.Count,
                VdbeArithmetic.Symbol(arithmetic.Operator),
                FormatArithmetic(arithmetic)),
            NumericAffinityInstruction numericAffinity => (
                numericAffinity.Value.Index,
                0,
                0,
                numericAffinity.Affinity.Name,
                $"r[{numericAffinity.Value.Index}]={numericAffinity.Affinity.Name}(r[{numericAffinity.Value.Index}])"),
            OpenReadCursorInstruction open => (
                open.Cursor.Index,
                0,
                open.ColumnCount,
                open.TableName,
                open.TableName is null
                    ? $"open read cursor {open.Cursor.Index}"
                    : $"open read cursor {open.Cursor.Index} on {open.TableName} ({open.ColumnCount} cols)"),
            OpenJoinCursorInstruction openJoin => (
                openJoin.Cursor.Index,
                openJoin.Plan.SourceCount,
                openJoin.Plan.RecordColumnCount,
                openJoin.Plan.Description,
                $"materialize {openJoin.Plan.Description} into cursor {openJoin.Cursor.Index} ({openJoin.Plan.RecordColumnCount} cols)"),
            OpenWriteCursorInstruction openWrite => (
                openWrite.Cursor.Index,
                0,
                openWrite.ColumnCount,
                openWrite.TableName,
                $"open write cursor {openWrite.Cursor.Index} on {openWrite.TableName} ({openWrite.ColumnCount} cols)"),
            CloseCursorInstruction close => (close.Cursor.Index, 0, 0, null, $"close cursor {close.Cursor.Index}"),
            RewindCursorInstruction rewind => (
                rewind.Cursor.Index,
                rewind.EmptyTarget.Offset,
                0,
                null,
                $"rewind cursor {rewind.Cursor.Index}, goto {rewind.EmptyTarget.Offset} if empty"),
            ColumnInstruction column => (
                column.Cursor.Index,
                column.ColumnIndex,
                column.Destination.Index,
                null,
                $"r[{column.Destination.Index}]=c{column.Cursor.Index}.col[{column.ColumnIndex}]"),
            RowIdInstruction rowId => (
                rowId.Cursor.Index,
                rowId.Destination.Index,
                0,
                null,
                $"r[{rowId.Destination.Index}]=c{rowId.Cursor.Index}.rowid"),
            FilterInstruction filter => (
                filter.Cursor.Index,
                filter.FalseTarget.Offset,
                0,
                null,
                filter.Description),
            FilterRowIdInstruction filterRowId => (
                filterRowId.Cursor.Index,
                filterRowId.FalseTarget.Offset,
                0,
                null,
                filterRowId.Description),
            FilterRegistersInstruction filterRegisters => (
                filterRegisters.Row.Start.Index,
                filterRegisters.FalseTarget.Offset,
                filterRegisters.Row.Count,
                null,
                filterRegisters.Description),
            ProjectRegistersInstruction project => (
                project.Input.Start.Index,
                project.Output.Start.Index,
                project.Output.Count,
                null,
                project.Description),
            DistinctFilterInstruction distinctFilter => (
                distinctFilter.Values.Start.Index,
                distinctFilter.DuplicateTarget.Offset,
                distinctFilter.DistinctSetIndex,
                null,
                $"goto {distinctFilter.DuplicateTarget.Offset} if {FormatRange(distinctFilter.Values)} is in distinct set {distinctFilter.DistinctSetIndex}"),
            NextInstruction next => (
                next.Cursor.Index,
                next.LoopTarget.Offset,
                0,
                null,
                $"next cursor {next.Cursor.Index}, goto {next.LoopTarget.Offset} if more rows"),
            OpenSorterInstruction openSorter => (
                openSorter.Sorter.Index,
                0,
                openSorter.ColumnCount,
                null,
                $"open sorter {openSorter.Sorter.Index} ({openSorter.ColumnCount} cols)"),
            SorterInsertInstruction sorterInsert => (
                sorterInsert.Sorter.Index,
                sorterInsert.Record.Start.Index,
                sorterInsert.Record.Count,
                null,
                $"sorter {sorterInsert.Sorter.Index} insert {FormatRange(sorterInsert.Record)}"),
            SorterSortInstruction sorterSort => (
                sorterSort.Sorter.Index,
                sorterSort.EmptyTarget.Offset,
                0,
                null,
                $"sort sorter {sorterSort.Sorter.Index}, goto {sorterSort.EmptyTarget.Offset} if empty"),
            SorterDataInstruction sorterData => (
                sorterData.Sorter.Index,
                sorterData.Destination.Start.Index,
                sorterData.Destination.Count,
                null,
                $"{FormatRange(sorterData.Destination)}=sorter {sorterData.Sorter.Index} data"),
            SorterNextInstruction sorterNext => (
                sorterNext.Sorter.Index,
                sorterNext.LoopTarget.Offset,
                0,
                null,
                $"next sorter {sorterNext.Sorter.Index}, goto {sorterNext.LoopTarget.Offset} if more rows"),
            CloseSorterInstruction closeSorter => (
                closeSorter.Sorter.Index,
                0,
                0,
                null,
                $"close sorter {closeSorter.Sorter.Index}"),
            GotoInstruction gotoInstruction => (
                0,
                gotoInstruction.Target.Offset,
                0,
                null,
                $"goto {gotoInstruction.Target.Offset}"),
            JumpIfInstruction jumpIf => (
                jumpIf.Register.Index,
                jumpIf.Target.Offset,
                0,
                null,
                $"goto {jumpIf.Target.Offset} if r[{jumpIf.Register.Index}]"),
            AggResetInstruction aggReset => (
                aggReset.Accumulator.Index,
                0,
                0,
                null,
                $"reset accumulator {aggReset.Accumulator.Index}"),
            AggStepInstruction aggStep => (
                aggStep.Accumulator.Index,
                aggStep.Arguments.Start.Index,
                aggStep.Arguments.Count,
                aggStep.Aggregate.Name,
                $"accumulator {aggStep.Accumulator.Index}={aggStep.Aggregate.Name} step {FormatRange(aggStep.Arguments)}"),
            AggFinalizeInstruction aggFinalize => (
                aggFinalize.Accumulator.Index,
                aggFinalize.Destination.Index,
                0,
                aggFinalize.Aggregate.Name,
                $"r[{aggFinalize.Destination.Index}]={aggFinalize.Aggregate.Name} finalize accumulator {aggFinalize.Accumulator.Index}"),
            SameGroupInstruction sameGroup => (
                sameGroup.CurrentKey.Start.Index,
                sameGroup.SameGroupTarget.Offset,
                sameGroup.SavedKey.Start.Index,
                null,
                $"goto {sameGroup.SameGroupTarget.Offset} if group {FormatRange(sameGroup.CurrentKey)}=={FormatRange(sameGroup.SavedKey)}"),
            DeleteInstruction delete => (
                delete.Cursor.Index,
                0,
                0,
                null,
                $"delete current row of cursor {delete.Cursor.Index}"),
            InsertInstruction insert => (
                insert.Cursor.Index,
                0,
                0,
                null,
                $"insert row into cursor {insert.Cursor.Index}"),
            UpdateInstruction update => (
                update.Cursor.Index,
                0,
                0,
                null,
                $"update current row of cursor {update.Cursor.Index}"),
            CommitInstruction commit => (
                commit.Cursor.Index,
                0,
                0,
                null,
                $"commit mutations of cursor {commit.Cursor.Index}"),
            ResultRowInstruction result => (
                result.Values.Start.Index,
                result.Values.Count,
                0,
                null,
                FormatResultRow(result.Values)),
            DistinctResultRowInstruction distinct => (
                distinct.Values.Start.Index,
                distinct.Values.Count,
                distinct.DistinctSetIndex,
                null,
                $"{FormatResultRow(distinct.Values)} if new to distinct set {distinct.DistinctSetIndex}"),
            RowSetInsertInstruction rowSetInsert => (
                rowSetInsert.Values.Start.Index,
                rowSetInsert.Values.Count,
                rowSetInsert.RowSetIndex,
                null,
                $"insert {FormatRange(rowSetInsert.Values)} into row set {rowSetInsert.RowSetIndex}"),
            CompoundResultRowInstruction compound => (
                compound.Values.Start.Index,
                compound.Values.Count,
                compound.OutputSetIndex,
                FormatSetList(compound.MembershipSetIndices),
                $"{FormatResultRow(compound.Values)} if new to distinct set {compound.OutputSetIndex} and {FormatMembership(compound.Mode)} {FormatSetList(compound.MembershipSetIndices)}"),
            OffsetGateInstruction offsetGate => (
                offsetGate.Counter.Index,
                offsetGate.SkipTarget.Offset,
                0,
                null,
                $"goto {offsetGate.SkipTarget.Offset} and decrement r[{offsetGate.Counter.Index}] while r[{offsetGate.Counter.Index}]>0"),
            LimitGateInstruction limitGate => (
                limitGate.Counter.Index,
                limitGate.DoneTarget.Offset,
                0,
                null,
                $"goto {limitGate.DoneTarget.Offset} when r[{limitGate.Counter.Index}]<=0, else decrement r[{limitGate.Counter.Index}]"),
            BeginTransactionInstruction => (0, 0, 0, null, "begin transaction"),
            CommitTransactionInstruction => (0, 0, 0, null, "commit transaction"),
            RollbackTransactionInstruction => (0, 0, 0, null, "rollback transaction"),
            SavepointInstruction savepoint => (0, 0, 0, savepoint.Name, $"open savepoint {savepoint.Name}"),
            ReleaseSavepointInstruction release => (0, 0, 0, release.Name, $"release savepoint {release.Name}"),
            RollbackToSavepointInstruction rollbackTo => (0, 0, 0, rollbackTo.Name, $"rollback to savepoint {rollbackTo.Name}"),
            OpenWorkTableInstruction openWorkTable => (
                openWorkTable.WorkTable.Index,
                openWorkTable.MaxRows,
                openWorkTable.MaxDepth,
                FormatDedupMode(openWorkTable.Mode),
                $"open work table {openWorkTable.WorkTable.Index} ({openWorkTable.ColumnCount} cols, {FormatDedupMode(openWorkTable.Mode)}, <={openWorkTable.MaxRows} rows, depth<={openWorkTable.MaxDepth})"),
            SeedWorkTableInstruction seed => (
                seed.WorkTable.Index,
                seed.Row.Start.Index,
                seed.Row.Count,
                null,
                $"seed work table {seed.WorkTable.Index} with {FormatRange(seed.Row)}"),
            WorkTableStepInstruction step => (
                step.WorkTable.Index,
                step.DoneTarget.Offset,
                step.Destination.Start.Index,
                null,
                $"{FormatRange(step.Destination)}=work table {step.WorkTable.Index} next, goto {step.DoneTarget.Offset} if drained"),
            WorkTableExpandInstruction expand => (
                expand.WorkTable.Index,
                expand.Source.Start.Index,
                expand.Source.Count,
                null,
                $"expand work table {expand.WorkTable.Index} from {FormatRange(expand.Source)}"),
            CloseWorkTableInstruction closeWorkTable => (
                closeWorkTable.WorkTable.Index,
                0,
                0,
                null,
                $"close work table {closeWorkTable.WorkTable.Index}"),
            YieldInstruction => (0, 0, 0, null, "yield"),
            HaltInstruction => (0, 0, 0, null, "halt"),
            _ => throw new VdbeProgramValidationException(
                $"Cannot describe unsupported opcode {instruction.Opcode}."),
        };
    }

    private static string FormatRange(RegisterRange range)
    {
        if (range.Count == 0)
            return "r[]";

        var start = range.Start.Index;
        return range.Count == 1
            ? $"r[{start}]"
            : $"r[{start}..{start + range.Count - 1}]";
    }

    private static string FormatArithmetic(ArithmeticInstruction arithmetic)
    {
        var symbol = VdbeArithmetic.Symbol(arithmetic.Operator);
        var destination = arithmetic.Destination.Index;
        var start = arithmetic.Operands.Start.Index;
        // Unary operators render as a prefix over their single operand; binary operators render infix.
        return arithmetic.Operands.Count == 1
            ? $"r[{destination}]={symbol}r[{start}]"
            : $"r[{destination}]=r[{start}] {symbol} r[{start + 1}]";
    }

    private static string FormatResultRow(RegisterRange range)
        => $"output={FormatRange(range)}";

    private static string FormatMembership(CompoundMembershipMode mode) => mode switch
    {
        CompoundMembershipMode.PresentInAll => "present in all of",
        CompoundMembershipMode.AbsentFromAll => "absent from all of",
        _ => throw new VdbeProgramValidationException($"Unknown compound membership mode {mode}."),
    };

    private static string FormatDedupMode(WorkTableDedupMode mode) => mode switch
    {
        WorkTableDedupMode.KeepAll => "union all",
        WorkTableDedupMode.Distinct => "distinct",
        _ => throw new VdbeProgramValidationException($"Unknown work table dedup mode {mode}."),
    };

    private static string FormatSetList(IReadOnlyList<int> setIndices)
    {
        if (setIndices is null || setIndices.Count == 0)
            return "sets {}";

        return $"sets {{{string.Join(",", setIndices)}}}";
    }

    private static string FormatValue(SqlValue value)
    {
        return value.Kind switch
        {
            SqlValueKind.Null => "NULL",
            SqlValueKind.Integer => value.AsInteger().ToString(CultureInfo.InvariantCulture),
            SqlValueKind.Real => value.AsReal().ToString("R", CultureInfo.InvariantCulture),
            SqlValueKind.Text => $"'{value.AsText()}'",
            SqlValueKind.Blob => FormatBlob(value.AsBlob().Span),
            _ => throw new VdbeProgramValidationException($"Unknown SQL value kind {value.Kind}."),
        };
    }

    private static string FormatBlob(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2 + 3);
        builder.Append("x'");
        foreach (var b in bytes)
            builder.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        builder.Append('\'');
        return builder.ToString();
    }
}
