using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution.Expressions;

/// <summary>
/// A call to a client-bodied function (D78, D79). The kernel is resolved at plan compilation, so a
/// missing or mistyped implementation is an error from <c>PrepareAsync</c>, never mid-stream.
/// </summary>
/// <remarks>
/// <para>
/// Volatility decides how often the kernel runs. <c>IMMUTABLE</c> and <c>VOLATILE</c> are evaluated
/// per batch over every lane — the planner has already made sure a volatile call is neither folded
/// nor de-duplicated. A <c>STABLE</c> call whose arguments are all constant is evaluated once per
/// execution and broadcast, which is what makes <c>as_of()</c> one value across a whole result.
/// </para>
/// <para>
/// Validity is the engine's: whatever the kernel wrote is narrowed to the batch's selected lanes,
/// and for a strict function it is also intersected with the arguments' own validity — so a kernel
/// that answers on a NULL lane cannot make one appear.
/// </para>
/// <para>
/// A call of an <c>IMMUTABLE</c> or <c>STABLE</c> function that several expressions of one operator
/// name is compiled once and answers once per batch (D293); the compiler does that for every
/// non-trivial subtree now, through <see cref="SharedExpr"/> (D299). A <c>VOLATILE</c> call is never
/// shared, nor is any subtree that holds one.
/// </para>
/// </remarks>
internal sealed class UserScalarExpr : VectorExprBase
{
    private readonly IVectorFunction _kernel;
    private readonly IVectorExpr[] _args;
    private readonly bool _strict;
    private readonly bool _stable;
    private readonly bool _constant;
    private readonly ColumnView[] _views;
    private readonly VectorScratch?[] _broadcast;
    private readonly VectorScratch? _stableScratch;
    private int _stableGeneration = -1;

    public UserScalarExpr(
        ChalkType type,
        IVectorFunction kernel,
        IVectorExpr[] args,
        bool strict,
        Volatility volatility)
        : base(type)
    {
        _kernel = kernel;
        _args = args;
        _strict = strict;
        _stable = volatility == Volatility.Stable;
        _views = new ColumnView[args.Length];
        _broadcast = new VectorScratch?[args.Length];
        _constant = args.All(a => a is LiteralExpr);
        _stableScratch = _stable && _constant ? new VectorScratch(type) : null;
    }

    public override Vector Evaluate(EvalContext context)
    {
        var length = context.Length;
        return _stableScratch is not null
            ? Broadcast(context, length)
            : Run(context, length, Scratch);
    }

    /// <summary>Evaluates the kernel over <paramref name="length"/> lanes into <paramref name="into"/>.</summary>
    private Vector Run(EvalContext context, int length, VectorScratch into)
    {
        for (var i = 0; i < _args.Length; i++)
        {
            var value = _args[i].Evaluate(context);
            _views[i] = value.IsScalar ? BroadcastArgument(i, value, length) : value.View;
        }

        var call = new FunctionContext
        {
            RowCount = length,
            Now = context.Now,
            Arena = context.Arena,
        };
        _kernel.Invoke(_views.AsSpan(0, _args.Length), into.Writer, in call);

        if (ColumnKinds.IsVariableLength(ColumnKinds.Of(into.Type)))
        {
            return into.FinishVarLen();
        }

        // A kernel that answered on no lane at all — a strict function over a NULL constant — never
        // asked for the value buffer, so it is sized here before the view is taken.
        _ = into.RawValues(length);

        var validity = into.MutableValidity(length);
        if (validity.IsEmpty)
        {
            return into.Finish(length, 0);
        }

        if (_strict)
        {
            for (var i = 0; i < _args.Length; i++)
            {
                Validity.AndFrom(validity, _views[i], length);
            }
        }

        var selection = context.SelectionMask;
        if (!selection.IsEmpty)
        {
            for (var b = 0; b < Validity.ByteCount(length); b++)
            {
                validity[b] &= selection[b];
            }
        }

        return into.Finish(length, Validity.CountNulls(validity, length));
    }

    /// <summary>
    /// A <c>STABLE</c> call of constants: evaluated once per execution on a single lane, then copied
    /// across the batch. The generation counter is what says "this is a new execution".
    /// </summary>
    private Vector Broadcast(EvalContext context, int length)
    {
        var stable = _stableScratch!;
        if (_stableGeneration != context.Generation)
        {
            Run(context, 1, stable);
            _stableGeneration = context.Generation;
        }

        if (Kind == ColumnKind.Composite)
        {
            // D291: the one row a STABLE composite call answered, spread field by field.
            return Scratch.BroadcastRow(stable.FinishWritten(1), length, context.SelectionMask);
        }

        var one = stable.Finish(1, 0);
        if (length == 1)
        {
            return one;
        }

        var width = Width;
        var source = stable.RawValues(1);
        var lanes = Scratch.RawValues(length);
        var valid = stable.MutableValidity(1);
        var isNull = !valid.IsEmpty && !BitUtility.GetBit(valid, 0);

        var bits = Scratch.BeginValidity(length);
        for (var row = 0; row < length; row++)
        {
            source[..width].CopyTo(lanes[(row * width)..]);
            if (!isNull)
            {
                BitUtility.SetBit(bits, row);
            }
        }

        var selection = context.SelectionMask;
        if (!selection.IsEmpty)
        {
            for (var b = 0; b < Validity.ByteCount(length); b++)
            {
                bits[b] &= selection[b];
            }
        }

        return Scratch.Finish(length, Validity.CountNulls(bits, length));
    }

    /// <summary>A constant argument, spread over the batch so the kernel sees one shape only.</summary>
    private ColumnView BroadcastArgument(int index, in Vector value, int length)
    {
        var scratch = _broadcast[index] ??= new VectorScratch(_args[index].Type);
        var kind = ColumnKinds.Of(scratch.Type);
        if (ColumnKinds.IsVariableLength(kind))
        {
            scratch.BeginVarLen(length, nullable: true);
            for (var row = 0; row < length; row++)
            {
                if (value.Scalar.IsNull)
                {
                    scratch.AppendNull();
                }
                else
                {
                    scratch.AppendValue(value.Scalar.ReadBytes());
                }
            }

            return scratch.FinishVarLen().View;
        }

        var width = ColumnKinds.Width(kind);
        var lanes = scratch.RawValues(length);
        var bits = scratch.BeginValidity(length);
        for (var row = 0; row < length; row++)
        {
            if (value.Scalar.IsNull)
            {
                lanes.Slice(row * width, width).Clear();
                continue;
            }

            ScalarLanes.Write(kind, value.Scalar, lanes.Slice(row * width, width));
            BitUtility.SetBit(bits, row);
        }

        return scratch.Finish(length, Validity.CountNulls(bits, length)).View;
    }
}
