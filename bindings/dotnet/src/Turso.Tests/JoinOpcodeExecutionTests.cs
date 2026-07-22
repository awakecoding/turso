using AwesomeAssertions;
using Turso.Core;
using Turso.Core.Execution;

namespace Turso.Tests;

// Opcode-level coverage for the join primitives added to the execution contract: FilterRegisters
// (the register-range predicate gate a join ON condition needs) and JumpIf (the control-flow jump
// the LEFT OUTER match flag drives). Programs are hand-built from the public Execution contract and
// run through the resumable state machine, so these tests exercise the interpreter, validator, and
// EXPLAIN renderer directly rather than the JoinProgramBuilder lowering.
public class JoinOpcodeExecutionTests
{
    private const long JumpTakenMarker = 20;
    private const long FallThroughMarker = 10;

    [Test]
    public void FilterRegistersFallsThroughWhenThePredicateHolds()
    {
        var program = SingleRegisterFilter(row => row[0].AsInteger() == 1, SqlValue.Integer(1));

        var rows = RunToCompletion(program);

        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Integer(1));
    }

    [Test]
    public void FilterRegistersJumpsToItsFalseTargetWhenThePredicateFails()
    {
        var program = SingleRegisterFilter(row => row[0].AsInteger() == 1, SqlValue.Integer(2));

        RunToCompletion(program).Should().BeEmpty();
    }

    [Test]
    public void FilterRegistersPassesTheWholeRegisterBlockToThePredicate()
    {
        // r0=3, r1=4; the predicate sums the two-register tuple, proving the whole block is presented.
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(3)),
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(4)),
            new FilterRegistersInstruction(
                new RegisterRange(new Register(0), 2),
                row => row.Length == 2 && row[0].AsInteger() + row[1].AsInteger() == 7,
                new ProgramCounter(4),
                "sum must be 7"),
            new ResultRowInstruction(new RegisterRange(new Register(0), 2)),
            new HaltInstruction(),
        ];

        var rows = RunToCompletion(new VdbeProgram(2, cursorCount: 0, instructions));

        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Integer(3), SqlValue.Integer(4));
    }

    [Test]
    public void JumpIfTakesTheJumpForANonZeroInteger()
    {
        JumpIfOutcome(SqlValue.Integer(5)).Should().Be(JumpTakenMarker);
    }

    [Test]
    public void JumpIfFallsThroughForZeroNullAndNonIntegerKinds()
    {
        JumpIfOutcome(SqlValue.Integer(0)).Should().Be(FallThroughMarker);
        JumpIfOutcome(SqlValue.Null).Should().Be(FallThroughMarker);
        JumpIfOutcome(SqlValue.Text("x")).Should().Be(FallThroughMarker);
        JumpIfOutcome(SqlValue.Real(1.5)).Should().Be(FallThroughMarker);
    }

    [Test]
    public void HandBuiltNestedLoopProducesTheCartesianProduct()
    {
        // 0 OpenRead c0 / 1 OpenRead c1 / 2 Rewind c0 -> 9 / 3 Rewind c1 -> 8 (per outer row)
        // 4 Column c0.0 -> r0 / 5 Column c1.0 -> r1 / 6 ResultRow r[0..1] / 7 Next c1 -> 4
        // 8 Next c0 -> 3 / 9 Close c1 / 10 Close c0 / 11 Halt
        VdbeInstruction[] instructions =
        [
            new OpenReadCursorInstruction(new Cursor(0), "l", 1),
            new OpenReadCursorInstruction(new Cursor(1), "r", 1),
            new RewindCursorInstruction(new Cursor(0), new ProgramCounter(9)),
            new RewindCursorInstruction(new Cursor(1), new ProgramCounter(8)),
            new ColumnInstruction(new Cursor(0), 0, new Register(0)),
            new ColumnInstruction(new Cursor(1), 0, new Register(1)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 2)),
            new NextInstruction(new Cursor(1), new ProgramCounter(4)),
            new NextInstruction(new Cursor(0), new ProgramCounter(3)),
            new CloseCursorInstruction(new Cursor(1)),
            new CloseCursorInstruction(new Cursor(0)),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(2, cursorCount: 2, instructions);

        var rows = Run(program, Rows([1], [2]), Rows([10], [20]));

        rows.Select(row => (row[0].AsInteger(), row[1].AsInteger())).Should().Equal(
            (1, 10), (1, 20), (2, 10), (2, 20));
    }

    [Test]
    public void HandBuiltNestedLoopWithFilterRegistersProducesTheEquiJoin()
    {
        // Same nested loop, but a FilterRegisters gate over r[0..1] skips non-matching pairs.
        // 0 OpenRead c0 / 1 OpenRead c1 / 2 Rewind c0 -> 10 / 3 Rewind c1 -> 9
        // 4 Column c0.0 -> r0 / 5 Column c1.0 -> r1 / 6 FilterRegisters r[0..1] -> 8
        // 7 ResultRow r[0..1] / 8 Next c1 -> 4 / 9 Next c0 -> 3 / 10 Close c1 / 11 Close c0 / 12 Halt
        VdbeInstruction[] instructions =
        [
            new OpenReadCursorInstruction(new Cursor(0), "l", 1),
            new OpenReadCursorInstruction(new Cursor(1), "r", 1),
            new RewindCursorInstruction(new Cursor(0), new ProgramCounter(10)),
            new RewindCursorInstruction(new Cursor(1), new ProgramCounter(9)),
            new ColumnInstruction(new Cursor(0), 0, new Register(0)),
            new ColumnInstruction(new Cursor(1), 0, new Register(1)),
            new FilterRegistersInstruction(
                new RegisterRange(new Register(0), 2),
                row => row[0].AsInteger() == row[1].AsInteger(),
                new ProgramCounter(8),
                "keep matching pair"),
            new ResultRowInstruction(new RegisterRange(new Register(0), 2)),
            new NextInstruction(new Cursor(1), new ProgramCounter(4)),
            new NextInstruction(new Cursor(0), new ProgramCounter(3)),
            new CloseCursorInstruction(new Cursor(1)),
            new CloseCursorInstruction(new Cursor(0)),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(2, cursorCount: 2, instructions);

        var rows = Run(program, Rows([1], [2], [3]), Rows([2], [1], [5]));

        rows.Select(row => (row[0].AsInteger(), row[1].AsInteger())).Should().Equal((1, 1), (2, 2));
    }

    [Test]
    public void ValidationRejectsFilterRegistersWithANullPredicate()
    {
        VdbeInstruction[] instructions =
        [
            new FilterRegistersInstruction(
                new RegisterRange(new Register(0), 1),
                null!,
                new ProgramCounter(1),
                "bad"),
            new HaltInstruction(),
        ];

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(1, cursorCount: 0, instructions));
    }

    [Test]
    public void ValidationRejectsFilterRegistersReadingOutsideTheRegisterRange()
    {
        VdbeInstruction[] instructions =
        [
            new FilterRegistersInstruction(
                new RegisterRange(new Register(0), 4),
                _ => true,
                new ProgramCounter(1),
                "too wide"),
            new HaltInstruction(),
        ];

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(1, cursorCount: 0, instructions));
    }

    [Test]
    public void ValidationRejectsJumpIfWithAnUnknownRegister()
    {
        VdbeInstruction[] instructions =
        [
            new JumpIfInstruction(new Register(5), new ProgramCounter(1)),
            new HaltInstruction(),
        ];

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(1, cursorCount: 0, instructions));
    }

    [Test]
    public void ValidationRejectsJumpIfTargetingOutsideTheProgram()
    {
        VdbeInstruction[] instructions =
        [
            new JumpIfInstruction(new Register(0), new ProgramCounter(9)),
            new HaltInstruction(),
        ];

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(1, cursorCount: 0, instructions));
    }

    [Test]
    public void ExplainDescribesFilterRegistersWithItsRangeAndDescription()
    {
        var instruction = new FilterRegistersInstruction(
            new RegisterRange(new Register(2), 3),
            _ => true,
            new ProgramCounter(11),
            "skip pair when join predicate is false, goto 11");

        var (p1, p2, p3, p4, comment) = VdbeExplain.Describe(instruction);

        p1.Should().Be(2);
        p2.Should().Be(11);
        p3.Should().Be(3);
        p4.Should().BeNull();
        comment.Should().Be("skip pair when join predicate is false, goto 11");
    }

    [Test]
    public void ExplainDescribesJumpIfWithItsRegisterAndTarget()
    {
        var instruction = new JumpIfInstruction(new Register(4), new ProgramCounter(19));

        var (p1, p2, p3, p4, comment) = VdbeExplain.Describe(instruction);

        p1.Should().Be(4);
        p2.Should().Be(19);
        p3.Should().Be(0);
        p4.Should().BeNull();
        comment.Should().Be("goto 19 if r[4]");
    }

    [Test]
    public void SteppingAfterDisposeThrowsObjectDisposedException()
    {
        var program = SingleRegisterFilter(_ => true, SqlValue.Integer(1));
        var statement = new ResumableStatement(program);
        statement.Dispose();

        Assert.Throws<ObjectDisposedException>(() => statement.StepResumable());
    }

    // 0 LoadConstant r0=<seed> / 1 FilterRegisters r[0..0] -> 3 / 2 ResultRow r[0..0] / 3 Halt.
    // A true predicate falls through to the ResultRow; a false predicate jumps past it to Halt.
    private static VdbeProgram SingleRegisterFilter(VdbeRowPredicate predicate, SqlValue seed)
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), seed),
            new FilterRegistersInstruction(
                new RegisterRange(new Register(0), 1),
                predicate,
                new ProgramCounter(3),
                "gate"),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];

        return new VdbeProgram(1, cursorCount: 0, instructions);
    }

    // Drives JumpIf: seeds r0 with the value, jumps to a marker-setting block when truthy, and
    // falls through to a different marker otherwise, emitting the resulting marker as a single row.
    private static long JumpIfOutcome(SqlValue value)
    {
        // 0 LoadConstant r0=value / 1 JumpIf r0 -> 5 / 2 LoadConstant r1=10 / 3 ResultRow r[1..1]
        // 4 Goto 7 / 5 LoadConstant r1=20 / 6 ResultRow r[1..1] / 7 Halt
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), value),
            new JumpIfInstruction(new Register(0), new ProgramCounter(5)),
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(FallThroughMarker)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new GotoInstruction(new ProgramCounter(7)),
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(JumpTakenMarker)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var rows = RunToCompletion(new VdbeProgram(2, cursorCount: 0, instructions));

        rows.Should().ContainSingle();
        return rows[0][0].AsInteger();
    }

    private static VdbeCursorSource Rows(params int[][] rows)
    {
        var materialized = new List<SqlValue[]>(rows.Length);
        foreach (var row in rows)
        {
            var values = new SqlValue[row.Length];
            for (var column = 0; column < row.Length; column++)
                values[column] = SqlValue.Integer(row[column]);

            materialized.Add(values);
        }

        return new VdbeCursorSource(materialized);
    }

    private static List<SqlValue[]> Run(VdbeProgram program, VdbeCursorSource left, VdbeCursorSource right)
    {
        using var statement = new ResumableStatement(program, [left, right]);
        return Drain(statement);
    }

    private static List<SqlValue[]> RunToCompletion(VdbeProgram program)
    {
        using var statement = new ResumableStatement(program);
        return Drain(statement);
    }

    private static List<SqlValue[]> Drain(ResumableStatement statement)
    {
        var rows = new List<SqlValue[]>();
        while (true)
        {
            var result = statement.StepResumable();
            if (result == ResumableStatementStepResult.Row)
                rows.Add([.. statement.CurrentRow!]);
            else if (result == ResumableStatementStepResult.Done)
                break;
            else
                throw new InvalidOperationException($"Unexpected step result {result}.");
        }

        return rows;
    }
}
