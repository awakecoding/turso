using Turso.Core.Execution;

namespace Turso.Core.Compilation;

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
    private readonly Func<Expression, ScanTarget, VdbeRowIdPredicate?> _compileRowIdPredicate;
    private readonly Func<SelectStatement, ScanTarget, VdbeRowEquality?> _compileDistinctEquality;
    private readonly Func<FunctionExpression, VdbeScalarFunction?> _compileScalarFunction;
    private readonly VdbeNumericAffinity _numericAffinity;
    private readonly VdbeNumericAffinity _moduloAffinity;

    public SelectStatementCompiler(
        Func<Expression, bool> isConstant,
        Func<Expression, SqlValue> fold,
        Func<TableSource, ScanTarget?> resolveScanTarget,
        Func<Expression, ScanTarget, VdbeRowPredicate?> compilePredicate,
        Func<Expression, ScanTarget, VdbeRowIdPredicate?> compileRowIdPredicate,
        Func<SelectStatement, ScanTarget, VdbeRowEquality?> compileDistinctEquality,
        Func<FunctionExpression, VdbeScalarFunction?> compileScalarFunction,
        VdbeNumericAffinity numericAffinity,
        VdbeNumericAffinity moduloAffinity)
    {
        ArgumentNullException.ThrowIfNull(isConstant);
        ArgumentNullException.ThrowIfNull(fold);
        ArgumentNullException.ThrowIfNull(resolveScanTarget);
        ArgumentNullException.ThrowIfNull(compilePredicate);
        ArgumentNullException.ThrowIfNull(compileRowIdPredicate);
        ArgumentNullException.ThrowIfNull(compileDistinctEquality);
        ArgumentNullException.ThrowIfNull(compileScalarFunction);
        ArgumentNullException.ThrowIfNull(numericAffinity);
        ArgumentNullException.ThrowIfNull(moduloAffinity);
        _isConstant = isConstant;
        _fold = fold;
        _resolveScanTarget = resolveScanTarget;
        _compilePredicate = compilePredicate;
        _compileRowIdPredicate = compileRowIdPredicate;
        _compileDistinctEquality = compileDistinctEquality;
        _compileScalarFunction = compileScalarFunction;
        _numericAffinity = numericAffinity;
        _moduloAffinity = moduloAffinity;
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
        var emitter = CreateEmitter(target: null, cursor: null, outputCount, body);
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

        VdbeRowPredicate? predicate = null;
        VdbeRowIdPredicate? rowIdPredicate = null;
        if (statement.Where is not null)
        {
            predicate = _compilePredicate(statement.Where, target);
            if (predicate is null)
                rowIdPredicate = _compileRowIdPredicate(statement.Where, target);
            if (predicate is null && rowIdPredicate is null)
                return false;
        }

        var cursor = new Cursor(0);
        var body = new List<VdbeInstruction>();
        var emitter = CreateEmitter(target, cursor, projections.Count, body);
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

        const int loopStart = 2;
        var filterCount = predicate is null && rowIdPredicate is null ? 0 : 1;
        var resultRowAddr = loopStart + filterCount + body.Count;
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

    private ProjectionEmitter CreateEmitter(
        ScanTarget? target,
        Cursor? cursor,
        int outputCount,
        List<VdbeInstruction> instructions)
        => new(
            target,
            cursor,
            outputCount,
            instructions,
            _isConstant,
            _fold,
            _compileScalarFunction,
            _numericAffinity,
            _moduloAffinity);

    private static bool TryExpandProjections(
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

    private readonly record struct ProjectionSource(Expression? Expression, int? ColumnIndex)
    {
        public static ProjectionSource ForExpression(Expression expression) => new(expression, null);

        public static ProjectionSource ForColumn(int columnIndex) => new(null, columnIndex);
    }

    private sealed class ProjectionEmitter
    {
        private readonly ScanTarget? _target;
        private readonly Cursor? _cursor;
        private readonly List<VdbeInstruction> _instructions;
        private readonly Func<Expression, bool> _isConstant;
        private readonly Func<Expression, SqlValue> _fold;
        private readonly Func<FunctionExpression, VdbeScalarFunction?> _compileScalarFunction;
        private readonly VdbeNumericAffinity _numericAffinity;
        private readonly VdbeNumericAffinity _moduloAffinity;
        private readonly Dictionary<int, int> _parameterSlots = [];
        private readonly List<int> _parameterIndices = [];
        private int _nextRegister;

        public ProjectionEmitter(
            ScanTarget? target,
            Cursor? cursor,
            int firstScratchRegister,
            List<VdbeInstruction> instructions,
            Func<Expression, bool> isConstant,
            Func<Expression, SqlValue> fold,
            Func<FunctionExpression, VdbeScalarFunction?> compileScalarFunction,
            VdbeNumericAffinity numericAffinity,
            VdbeNumericAffinity moduloAffinity)
        {
            _target = target;
            _cursor = cursor;
            _nextRegister = firstScratchRegister;
            _instructions = instructions;
            _isConstant = isConstant;
            _fold = fold;
            _compileScalarFunction = compileScalarFunction;
            _numericAffinity = numericAffinity;
            _moduloAffinity = moduloAffinity;
        }

        public int RegisterCount => _nextRegister;

        public IReadOnlyList<int> ParameterIndices => _parameterIndices;

        public bool TryEmit(Expression expression, Register destination)
        {
            if (_isConstant(expression))
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

                    var affinity = arithmetic == ArithmeticOperator.Modulo ? _moduloAffinity : _numericAffinity;
                    _instructions.Add(new NumericAffinityInstruction(operands.Start, affinity));
                    _instructions.Add(new NumericAffinityInstruction(
                        new Register(operands.Start.Index + 1),
                        affinity));
                    _instructions.Add(new ArithmeticInstruction(destination, arithmetic, operands));
                    return true;
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
                default:
                    arithmetic = default;
                    return false;
            }
        }
    }
}
