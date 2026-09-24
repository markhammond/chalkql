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
/// A node the compiler <em>shares</em> (D293) — one call of an <c>IMMUTABLE</c> or <c>STABLE</c>
/// function that several expressions of one operator name — answers once per batch: it remembers
/// the batch it last answered, by the context's batch sequence, and hands the same vector to every
/// expression that asks within it. The batch is a sufficient key because an operator evaluates all
/// of its expressions under the batch's one selection: no expression narrows the context for a
/// sub-expression (a CASE evaluates every branch over the whole batch). One that did would have to
/// make the selection part of the key. A <c>VOLATILE</c> call is never shared.
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

    /// <summary>Whether the compiler hands this node to more than one expression (D293).</summary>
    private bool _shared;

    /// <summary>
    /// The execution and batch the shared node last answered, and what it answered. The execution
    /// too, so that a context no operator ever pointed at a batch cannot carry an answer over.
    /// </summary>
    private long _answeredBatch = -1;
    private int _answeredGeneration = -1;
    private Vector _answer;

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

    /// <summary>
    /// Marks this node as answering for more than one expression of its operator (D293). Only the
    /// compiler calls it, and only for a call whose function is not <c>VOLATILE</c>.
    /// </summary>
    public void Share() => _shared = true;

    public override Vector Evaluate(EvalContext context)
    {
        if (_shared
            && _answeredBatch == context.BatchSequence
            && _answeredGeneration == context.Generation)
        {
            return _answer;
        }

        var length = context.Length;
        var answer = _stableScratch is not null
            ? Broadcast(context, length)
            : Run(context, length, Scratch);
        if (_shared)
        {
            _answeredBatch = context.BatchSequence;
            _answeredGeneration = context.Generation;
            _answer = answer;
        }

        return answer;
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

        if (Kind == ColumnKind.Struct)
        {
            // D291: the one row a STABLE struct call answered, spread field by field.
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
