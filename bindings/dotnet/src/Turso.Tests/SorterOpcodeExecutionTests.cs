using AwesomeAssertions;
using Turso.Core;
using Turso.Core.Execution;

namespace Turso.Tests;

// Opcode-level coverage for the sorter family. Programs are built by hand from the
// public Execution contract and run through the resumable state machine, so the
// tests exercise the interpreter and validator directly rather than any wiring.
public class SorterOpcodeExecutionTests
{
    private static readonly VdbeRowComparer AscendingFirstColumn =
        (left, right) => left[0].AsInteger().CompareTo(right[0].AsInteger());

    private static readonly VdbeRowComparer DescendingFirstColumn =
        (left, right) => right[0].AsInteger().CompareTo(left[0].AsInteger());

    // Compares the first column only, leaving equal-key rows to the sorter's stable order.
    private static readonly VdbeRowComparer AscendingFirstColumnStable =
        (left, right) => left[0].AsInteger().CompareTo(right[0].AsInteger());

    [Test]
    public void SorterOrdersInsertedRowsAndSnapshotsRegisterValues()
    {
        // r0 is overwritten between inserts, so ordered output proves records are copied.
        var program = SingleColumnSorterProgram(AscendingFirstColumn, 3, 1, 2);

        var rows = RunToCompletion(program);

        rows.Should().HaveCount(3);
        rows[0].Should().Equal(SqlValue.Integer(1));
        rows[1].Should().Equal(SqlValue.Integer(2));
        rows[2].Should().Equal(SqlValue.Integer(3));
    }

    [Test]
    public void SorterHonorsDescendingComparer()
    {
        var program = SingleColumnSorterProgram(DescendingFirstColumn, 3, 1, 2);

        var rows = RunToCompletion(program);

        rows.Select(row => row[0].AsInteger()).Should().Equal(3, 2, 1);
    }

    [Test]
    public void EmptySorterDrainsToNoRowsThroughTheEmptyTarget()
    {
        // 0 OpenSorter / 1 SorterSort -> 5 / 2 SorterData / 3 ResultRow / 4 SorterNext -> 2
        // 5 CloseSorter / 6 Halt. The empty target must skip the whole drain loop.
        VdbeInstruction[] instructions =
        [
            new OpenSorterInstruction(new Sorter(0), AscendingFirstColumn, 1),
            new SorterSortInstruction(new Sorter(0), new ProgramCounter(5)),
            new SorterDataInstruction(new Sorter(0), new RegisterRange(new Register(0), 1)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new SorterNextInstruction(new Sorter(0), new ProgramCounter(2)),
            new CloseSorterInstruction(new Sorter(0)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 1, cursorCount: 0, instructions, sorterCount: 1);

        RunToCompletion(program).Should().BeEmpty();
    }

    [Test]
    public void SorterKeepsInsertionOrderForEqualKeys()
    {
        // key/tag rows compared on the key only; equal keys must preserve insertion order.
        VdbeInstruction[] instructions =
        [
            new OpenSorterInstruction(new Sorter(0), AscendingFirstColumnStable, 2),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("a")),
            new SorterInsertInstruction(new Sorter(0), new RegisterRange(new Register(0), 2)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("b")),
            new SorterInsertInstruction(new Sorter(0), new RegisterRange(new Register(0), 2)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(0)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("c")),
            new SorterInsertInstruction(new Sorter(0), new RegisterRange(new Register(0), 2)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("d")),
            new SorterInsertInstruction(new Sorter(0), new RegisterRange(new Register(0), 2)),
            new SorterSortInstruction(new Sorter(0), new ProgramCounter(17)),
            new SorterDataInstruction(new Sorter(0), new RegisterRange(new Register(0), 2)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 2)),
            new SorterNextInstruction(new Sorter(0), new ProgramCounter(14)),
            new CloseSorterInstruction(new Sorter(0)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 0, instructions, sorterCount: 1);

        var rows = RunToCompletion(program);

        rows.Select(row => row[1].AsText()).Should().Equal("c", "a", "b", "d");
        rows.Select(row => row[0].AsInteger()).Should().Equal(0, 1, 1, 1);
    }

    [Test]
    public void SorterObservesCancellationRequestedByANonThrowingComparer()
    {
        using var cancellation = new CancellationTokenSource();
        var comparisonRequestedCancellation = false;
        VdbeRowComparer comparer = (left, right) =>
        {
            if (!comparisonRequestedCancellation)
            {
                comparisonRequestedCancellation = true;
                cancellation.Cancel();
            }

            return left[0].AsInteger().CompareTo(right[0].AsInteger());
        };
        var program = SingleColumnSorterProgram(comparer, 3, 1, 2);
        using var statement = new ResumableStatement(program);

        Assert.Throws<OperationCanceledException>(
            () => statement.StepResumable(cancellation.Token));
        statement.State.Should().Be(ResumableStatementState.Ready);

        DrainRows(statement).Select(row => row[0].AsInteger()).Should().Equal(1, 2, 3);
    }

    [Test]
    public void ResetReplaysASorterProgramFromTheStart()
    {
        var program = SingleColumnSorterProgram(AscendingFirstColumn, 5, 4);

        using var statement = new ResumableStatement(program);
        DrainRows(statement).Select(row => row[0].AsInteger()).Should().Equal(4, 5);

        statement.Reset();

        DrainRows(statement).Select(row => row[0].AsInteger()).Should().Equal(4, 5);
    }

    [Test]
    public void ValidationRejectsMalformedSorterBytecode()
    {
        var comparer = AscendingFirstColumn;

        // Sorter index beyond the declared count.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [new OpenSorterInstruction(new Sorter(0), comparer, 1), new HaltInstruction()],
            sorterCount: 0));

        // Used before being opened.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new SorterInsertInstruction(new Sorter(0), new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ],
            sorterCount: 1));

        // Opened twice.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [
                new OpenSorterInstruction(new Sorter(0), comparer, 1),
                new OpenSorterInstruction(new Sorter(0), comparer, 1),
                new HaltInstruction(),
            ],
            sorterCount: 1));

        // Null comparer.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [new OpenSorterInstruction(new Sorter(0), null!, 1), new HaltInstruction()],
            sorterCount: 1));

        // Non-positive column count.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [new OpenSorterInstruction(new Sorter(0), comparer, 0), new HaltInstruction()],
            sorterCount: 1));

        // Record width does not match the sorter's column count.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new OpenSorterInstruction(new Sorter(0), comparer, 2),
                new SorterInsertInstruction(new Sorter(0), new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ],
            sorterCount: 1));

        // Closed before being opened.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [new CloseSorterInstruction(new Sorter(0)), new HaltInstruction()],
            sorterCount: 1));

        // Jump target outside the program.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [
                new OpenSorterInstruction(new Sorter(0), comparer, 1),
                new SorterSortInstruction(new Sorter(0), new ProgramCounter(99)),
                new HaltInstruction(),
            ],
            sorterCount: 1));
    }

    [Test]
    public void ConstructorRejectsNegativeSorterCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [new HaltInstruction()],
            sorterCount: -1));
    }

    [Test]
    public void SorterHandleRejectsNegativeIndex()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Sorter(-1));
    }

    // Builds: OpenSorter / (LoadConstant, SorterInsert)* / SorterSort / drain loop / CloseSorter / Halt
    private static VdbeProgram SingleColumnSorterProgram(VdbeRowComparer comparer, params long[] values)
    {
        var instructions = new List<VdbeInstruction>
        {
            new OpenSorterInstruction(new Sorter(0), comparer, 1),
        };

        foreach (var value in values)
        {
            instructions.Add(new LoadConstantInstruction(new Register(0), SqlValue.Integer(value)));
            instructions.Add(new SorterInsertInstruction(new Sorter(0), new RegisterRange(new Register(0), 1)));
        }

        var sortAddr = instructions.Count;
        var drainLoop = sortAddr + 1;
        var drainDone = drainLoop + 3;

        instructions.Add(new SorterSortInstruction(new Sorter(0), new ProgramCounter(drainDone)));
        instructions.Add(new SorterDataInstruction(new Sorter(0), new RegisterRange(new Register(0), 1)));
        instructions.Add(new ResultRowInstruction(new RegisterRange(new Register(0), 1)));
        instructions.Add(new SorterNextInstruction(new Sorter(0), new ProgramCounter(drainLoop)));
        instructions.Add(new CloseSorterInstruction(new Sorter(0)));
        instructions.Add(new HaltInstruction());

        return new VdbeProgram(registerCount: 1, cursorCount: 0, instructions, sorterCount: 1);
    }

    private static List<SqlValue[]> RunToCompletion(VdbeProgram program, params VdbeCursorSource[] cursorSources)
    {
        using var statement = new ResumableStatement(
            program,
            cursorSources.Length == 0 ? null : cursorSources);
        return DrainRows(statement);
    }

    private static List<SqlValue[]> DrainRows(ResumableStatement statement)
    {
        var rows = new List<SqlValue[]>();
        while (true)
        {
            var result = statement.StepResumable();
            if (result == ResumableStatementStepResult.Row)
            {
                rows.Add([.. statement.CurrentRow!]);
            }
            else if (result == ResumableStatementStepResult.Done)
            {
                break;
            }
            else
            {
                throw new InvalidOperationException($"Unexpected step result {result}.");
            }
        }

        return rows;
    }
}
