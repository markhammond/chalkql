using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Execution.Numeric;
using Chalk.Execution.Vectors;

namespace Chalk.Execution.Expressions;

/// <summary>
/// COALESCE: the first operand that holds a value. Its own node rather than a kernel because the null
/// logic is the point (§6.4). The reference planner rewrites COALESCE to CASE, but the IR permits it
/// and a third-party planner may send it, so it is implemented and tested on its own.
/// </summary>
internal sealed class CoalesceExpr : VectorExprBase
{
    private readonly IVectorExpr[] _operands;
    private readonly Vector[] _vectors;
    private int[] _choice = [];

    public CoalesceExpr(ChalkType type, IVectorExpr[] operands)
        : base(type)
    {
        _operands = operands;
        _vectors = new Vector[operands.Length];
    }

    public override Vector Evaluate(EvalContext context)
    {
        var length = context.Length;
        for (var k = 0; k < _operands.Length; k++)
        {
            _vectors[k] = _operands[k].Evaluate(context);
        }

        var choice = Choices(ref _choice, length);
        for (var i = 0; i < length; i++)
        {
            choice[i] = -1;
            for (var k = 0; k < _vectors.Length; k++)
            {
                if (IsValidAt(_vectors[k], i))
                {
                    choice[i] = k;
                    break;
                }
            }
        }

        return Assemble(length, _vectors, choice);
    }

    internal static Span<int> Choices(ref int[] buffer, int length)
    {
        if (buffer.Length < length)
        {
            buffer = new int[length];
        }

        return buffer.AsSpan(0, length);
    }

}

/// <summary>
/// NULLIF(a, b): NULL where <c>a = b</c>, else a. The equality is the ordinary <c>=</c> kernel, so
/// NaN and DECIMAL scale behave exactly as they do in a comparison.
/// </summary>
internal sealed class NullIfExpr : VectorExprBase
{
    private readonly IVectorExpr _value;
    private readonly IVectorExpr _equal;
    private readonly Vector[] _vectors = new Vector[1];
    private int[] _choice = [];

    public NullIfExpr(ChalkType type, IVectorExpr value, IVectorExpr equal)
        : base(type)
    {
        _value = value;
        _equal = equal;
    }

    public override Vector Evaluate(EvalContext context)
    {
        var equality = _equal.Evaluate(context);
        var value = _value.Evaluate(context);
        var length = context.Length;
        _vectors[0] = value;

        var choice = CoalesceExpr.Choices(ref _choice, length);
        var lanes = Lanes<byte>.From(equality);
        for (var i = 0; i < length; i++)
        {
            var equal = lanes.IsValid(i) && lanes[i] != 0;
            choice[i] = equal ? -1 : 0;
        }

        return Assemble(length, _vectors, choice);
    }
}

/// <summary>
/// <c>CASE WHEN … THEN … ELSE … END</c>. Every branch is evaluated and then selected from, which is
/// what a vectorised engine can do and a row engine cannot; the IR forbids side effects, so the only
/// visible difference would be an error raised in an untaken branch.
/// </summary>
internal sealed class IfThenExpr : VectorExprBase
{
    private readonly IVectorExpr[] _conditions;
    private readonly IVectorExpr[] _results;
    private readonly IVectorExpr _elseBranch;
    private readonly Vector[] _vectors;
    private int[] _choice = [];

    public IfThenExpr(
        ChalkType type, IVectorExpr[] conditions, IVectorExpr[] results, IVectorExpr elseBranch)
        : base(type)
    {
        _conditions = conditions;
        _results = results;
        _elseBranch = elseBranch;
        _vectors = new Vector[results.Length + 1];
    }

    public override Vector Evaluate(EvalContext context)
    {
        var length = context.Length;
        var choice = CoalesceExpr.Choices(ref _choice, length);
        choice.Fill(_results.Length);

        // Conditions first, so a lane that has already matched is left alone by later clauses.
        for (var k = 0; k < _conditions.Length; k++)
        {
            var condition = _conditions[k].Evaluate(context);
            var lanes = Lanes<byte>.From(condition);
            for (var i = 0; i < length; i++)
            {
                if (choice[i] == _results.Length && lanes.IsValid(i) && lanes[i] != 0)
                {
                    choice[i] = k;
                }
            }
        }

        for (var k = 0; k < _results.Length; k++)
        {
            _vectors[k] = _results[k].Evaluate(context);
        }

        _vectors[^1] = _elseBranch.Evaluate(context);
        return Assemble(length, _vectors, choice);
    }
}

/// <summary>
/// <c>value IN (constants…)</c>. The options are literals or bound parameters, so the set is built
/// once for the execution and probed once per row (§6.4). Three-valued: NULL when the value is NULL,
/// or when nothing matched and some option was NULL.
/// </summary>
internal sealed class InListExpr : VectorExprBase
{
    private readonly IVectorExpr _value;
    private readonly IVectorExpr[] _options;
    private readonly ColumnKind _valueKind;
    private readonly bool _floatingPoint;

    private HashSet<long>? _integers;
    private HashSet<double>? _doubles;
    private ByteSet? _bytes;
    private bool _anyNull;
    private int _built = -1;

    public InListExpr(ChalkType type, IVectorExpr value, IVectorExpr[] options)
        : base(type)
    {
        _value = value;
        _options = options;
        _valueKind = ColumnKinds.Of(value.Type);
        _floatingPoint = ColumnKinds.IsFloatingPoint(value.Type.Kind);
    }

    public override Vector Evaluate(EvalContext context)
    {
        Build(context);
        var value = _value.Evaluate(context);
        var length = context.Length;
        var result = Scratch.Values<byte>(length);
        var bits = Scratch.BeginValidity(length);
        var nulls = 0;

        for (var i = 0; i < length; i++)
        {
            bool? answer;
            if (!IsValidAt(value, i))
            {
                answer = null;
            }
            else
            {
                var hit = Contains(value, i);
                answer = hit ? true : _anyNull ? null : false;
            }

            if (answer is null)
            {
                nulls++;
                result[i] = 0;
            }
            else
            {
                BitUtility.SetBit(bits, i);
                result[i] = (byte)(answer.Value ? 1 : 0);
            }
        }

        if (nulls == 0)
        {
            Scratch.NoValidity();
        }

        return Scratch.Finish(length, nulls);
    }

    private bool Contains(in Vector value, int row)
    {
        switch (_valueKind)
        {
            case ColumnKind.Float:
            {
                var v = Lanes<float>.From(value)[row];
                return !float.IsNaN(v) && _doubles!.Contains(v);
            }

            case ColumnKind.Double:
            {
                var v = Lanes<double>.From(value)[row];
                return !double.IsNaN(v) && _doubles!.Contains(v);
            }

            case ColumnKind.Utf8:
            case ColumnKind.Binary:
                return _bytes!.Contains(VarOperand.From(value)[row]);

            case ColumnKind.Decimal128:
            case ColumnKind.Bytes16:
                return _bytes!.Contains(RawLanes.From(value, 16)[row]);

            default:
                return _integers!.Contains(ReadInteger(value, row));
        }
    }

    private long ReadInteger(in Vector value, int row) => _valueKind switch
    {
        ColumnKind.Boolean => Lanes<byte>.From(value)[row],
        ColumnKind.Int8 => Lanes<sbyte>.From(value)[row],
        ColumnKind.Int16 => Lanes<short>.From(value)[row],
        ColumnKind.Int32 => Lanes<int>.From(value)[row],
        _ => Lanes<long>.From(value)[row],
    };

    private void Build(EvalContext context)
    {
        if (_built == context.Generation)
        {
            return;
        }

        // A bound parameter may be an option, so the set belongs to one execution, not to the plan.
        _built = context.Generation;
        _anyNull = false;
        if (_floatingPoint)
        {
            _doubles = [];
        }
        else if (_valueKind is ColumnKind.Utf8 or ColumnKind.Binary
                 or ColumnKind.Decimal128 or ColumnKind.Bytes16)
        {
            _bytes = new ByteSet(_options.Length);
        }
        else
        {
            _integers = [];
        }

        foreach (var option in _options)
        {
            // I-IR-4 restricts IN options to literals and parameters, both of which are scalars here.
            var scalar = option.Evaluate(context).Scalar;
            if (scalar.IsNull)
            {
                _anyNull = true;
                continue;
            }

            switch (_valueKind)
            {
                case ColumnKind.Float:
                    _doubles!.Add(scalar.Single);
                    break;
                case ColumnKind.Double:
                    _doubles!.Add(scalar.Double);
                    break;
                case ColumnKind.Utf8:
                case ColumnKind.Binary:
                case ColumnKind.Decimal128:
                case ColumnKind.Bytes16:
                    _bytes!.Add(scalar.ReadBytes());
                    break;
                default:
                    _integers!.Add(scalar.Integer);
                    break;
            }
        }
    }
}
