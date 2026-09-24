using Chalk.Sources;

namespace Chalk.Execution.Expressions;

/// <summary>
/// Tier 1 on top of Tier 2 (D79): a registered delegate, wrapped as the same
/// <see cref="IVectorFunction"/> a host would write by hand. There is one lane loop in the engine and
/// the two tiers cannot drift, which is the point of building it this way.
/// </summary>
/// <remarks>
/// A <c>STRICT</c> function's NULL lanes are skipped here, before the delegate is called — that is
/// what the flag means, and it also spares the host writing the check. A non-strict function sees
/// every lane and decides for itself, which is why its arguments are declared with nullable CLR
/// types.
/// </remarks>
internal abstract class Tier1Kernel : IVectorFunction
{
    /// <summary>
    /// The compiled writer of a STRUCT result (D294), a <c>StructWriter&lt;TOut&gt;</c> built once at
    /// binding; null for every scalar result.
    /// </summary>
    private readonly object? _struct;

    protected Tier1Kernel(FunctionSignature signature, bool strict, object? structWriter = null)
    {
        Signature = signature;
        Strict = strict;
        _struct = structWriter;
    }

    public FunctionSignature Signature { get; }

    protected bool Strict { get; }

    public abstract void Invoke(ReadOnlySpan<ColumnView> args, ColumnWriter result, in FunctionContext context);

    /// <summary>Whether every argument holds a value at this row.</summary>
    protected static bool AllValid(ReadOnlySpan<ColumnView> args, int row)
    {
        foreach (ref readonly var view in args)
        {
            if (!view.IsValid(row))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The shared shape of every arity: a validity bitmap, then a lane loop that either appends
    /// (variable-length results) or writes a lane, setting the bit when the delegate answered.
    /// </summary>
    protected void Run<TOut>(
        ReadOnlySpan<ColumnView> args, ColumnWriter result, int length, LaneProducer<TOut> produce)
    {
        if (_struct is StructWriter<TOut> record)
        {
            RunStruct(args, result, length, produce, record);
            return;
        }

        if (LaneCodec.IsVariableLength<TOut>())
        {
            result.BeginVarLength(length, nullable: true);
            for (var row = 0; row < length; row++)
            {
                if (Strict && !AllValid(args, row))
                {
                    result.AppendNull();
                    continue;
                }

                LaneCodec.Append(result, length, row, produce(row));
            }

            return;
        }

        var validity = result.BeginValidity(length);
        for (var row = 0; row < length; row++)
        {
            if (Strict && !AllValid(args, row))
            {
                continue;
            }

            var value = produce(row);
            if (LaneCodec.IsNull(value))
            {
                continue;
            }

            LaneCodec.Write(result, length, row, value);
            Apache.Arrow.BitUtility.SetBit(validity, row);
        }
    }

    /// <summary>
    /// The lane loop of a STRUCT result (D294): one delegate call per lane, one write per field, and
    /// the struct's own validity. A strict lane is skipped as a scalar's is, and still gets its row in
    /// every field, which is what keeps the fields aligned with the struct.
    /// </summary>
    private void RunStruct<TOut>(
        ReadOnlySpan<ColumnView> args,
        ColumnWriter result,
        int length,
        LaneProducer<TOut> produce,
        StructWriter<TOut> record)
    {
        var validity = result.BeginValidity(length);
        record.Begin(result, length);
        for (var row = 0; row < length; row++)
        {
            if (Strict && !AllValid(args, row))
            {
                record.WriteNull(row);
                continue;
            }

            if (record.Write(produce(row), row))
            {
                Apache.Arrow.BitUtility.SetBit(validity, row);
            }
        }
    }

    /// <summary>The struct writer a kernel of this result type needs, or null for a scalar result.</summary>
    protected static object? StructWriterFor<TOut>(FunctionSignature signature) =>
        signature.ReturnType.Kind == Ir.TypeKind.Struct
            ? StructWriters.For<TOut>(signature.ReturnType)
            : null;

    /// <summary>One row's answer. A struct-free delegate: it closes over nothing this code allocates.</summary>
    protected delegate TOut LaneProducer<out TOut>(int row);
}

internal sealed class Tier1Kernel0<TOut> : Tier1Kernel
{
    private readonly Func<TOut> _f;
    private readonly LaneProducer<TOut> _produce;

    public Tier1Kernel0(FunctionSignature signature, bool strict, Func<TOut> f)
        : base(signature, strict, StructWriterFor<TOut>(signature))
    {
        _f = f;
        _produce = _ => _f();
    }

    public override void Invoke(ReadOnlySpan<ColumnView> args, ColumnWriter result, in FunctionContext context) =>
        Run(args, result, context.RowCount, _produce);
}

internal sealed class Tier1Kernel1<T1, TOut> : Tier1Kernel
{
    private readonly Func<T1, TOut> _f;
    private readonly LaneProducer<TOut> _produce;
    private ColumnView _a1;

    public Tier1Kernel1(FunctionSignature signature, bool strict, Func<T1, TOut> f)
        : base(signature, strict, StructWriterFor<TOut>(signature))
    {
        _f = f;
        _produce = Produce;
    }

    public override void Invoke(ReadOnlySpan<ColumnView> args, ColumnWriter result, in FunctionContext context)
    {
        _a1 = args[0];
        Run(args, result, context.RowCount, _produce);
    }

    private TOut Produce(int row) => _f(LaneCodec.Read<T1>(_a1, row));
}

internal sealed class Tier1Kernel2<T1, T2, TOut> : Tier1Kernel
{
    private readonly Func<T1, T2, TOut> _f;
    private readonly LaneProducer<TOut> _produce;
    private ColumnView _a1;
    private ColumnView _a2;

    public Tier1Kernel2(FunctionSignature signature, bool strict, Func<T1, T2, TOut> f)
        : base(signature, strict, StructWriterFor<TOut>(signature))
    {
        _f = f;
        _produce = Produce;
    }

    public override void Invoke(ReadOnlySpan<ColumnView> args, ColumnWriter result, in FunctionContext context)
    {
        _a1 = args[0];
        _a2 = args[1];
        Run(args, result, context.RowCount, _produce);
    }

    private TOut Produce(int row) => _f(LaneCodec.Read<T1>(_a1, row), LaneCodec.Read<T2>(_a2, row));
}

internal sealed class Tier1Kernel3<T1, T2, T3, TOut> : Tier1Kernel
{
    private readonly Func<T1, T2, T3, TOut> _f;
    private readonly LaneProducer<TOut> _produce;
    private ColumnView _a1;
    private ColumnView _a2;
    private ColumnView _a3;

    public Tier1Kernel3(FunctionSignature signature, bool strict, Func<T1, T2, T3, TOut> f)
        : base(signature, strict, StructWriterFor<TOut>(signature))
    {
        _f = f;
        _produce = Produce;
    }

    public override void Invoke(ReadOnlySpan<ColumnView> args, ColumnWriter result, in FunctionContext context)
    {
        _a1 = args[0];
        _a2 = args[1];
        _a3 = args[2];
        Run(args, result, context.RowCount, _produce);
    }

    private TOut Produce(int row) => _f(
        LaneCodec.Read<T1>(_a1, row), LaneCodec.Read<T2>(_a2, row), LaneCodec.Read<T3>(_a3, row));
}

internal sealed class Tier1Kernel4<T1, T2, T3, T4, TOut> : Tier1Kernel
{
    private readonly Func<T1, T2, T3, T4, TOut> _f;
    private readonly LaneProducer<TOut> _produce;
    private ColumnView _a1;
    private ColumnView _a2;
    private ColumnView _a3;
    private ColumnView _a4;

    public Tier1Kernel4(FunctionSignature signature, bool strict, Func<T1, T2, T3, T4, TOut> f)
        : base(signature, strict, StructWriterFor<TOut>(signature))
    {
        _f = f;
        _produce = Produce;
    }

    public override void Invoke(ReadOnlySpan<ColumnView> args, ColumnWriter result, in FunctionContext context)
    {
        _a1 = args[0];
        _a2 = args[1];
        _a3 = args[2];
        _a4 = args[3];
        Run(args, result, context.RowCount, _produce);
    }

    private TOut Produce(int row) => _f(
        LaneCodec.Read<T1>(_a1, row),
        LaneCodec.Read<T2>(_a2, row),
        LaneCodec.Read<T3>(_a3, row),
        LaneCodec.Read<T4>(_a4, row));
}

internal sealed class Tier1Kernel5<T1, T2, T3, T4, T5, TOut> : Tier1Kernel
{
    private readonly Func<T1, T2, T3, T4, T5, TOut> _f;
    private readonly LaneProducer<TOut> _produce;
    private ColumnView _a1;
    private ColumnView _a2;
    private ColumnView _a3;
    private ColumnView _a4;
    private ColumnView _a5;

    public Tier1Kernel5(FunctionSignature signature, bool strict, Func<T1, T2, T3, T4, T5, TOut> f)
        : base(signature, strict, StructWriterFor<TOut>(signature))
    {
        _f = f;
        _produce = Produce;
    }

    public override void Invoke(ReadOnlySpan<ColumnView> args, ColumnWriter result, in FunctionContext context)
    {
        _a1 = args[0];
        _a2 = args[1];
        _a3 = args[2];
        _a4 = args[3];
        _a5 = args[4];
        Run(args, result, context.RowCount, _produce);
    }

    private TOut Produce(int row) => _f(
        LaneCodec.Read<T1>(_a1, row),
        LaneCodec.Read<T2>(_a2, row),
        LaneCodec.Read<T3>(_a3, row),
        LaneCodec.Read<T4>(_a4, row),
        LaneCodec.Read<T5>(_a5, row));
}

internal sealed class Tier1Kernel6<T1, T2, T3, T4, T5, T6, TOut> : Tier1Kernel
{
    private readonly Func<T1, T2, T3, T4, T5, T6, TOut> _f;
    private readonly LaneProducer<TOut> _produce;
    private ColumnView _a1;
    private ColumnView _a2;
    private ColumnView _a3;
    private ColumnView _a4;
    private ColumnView _a5;
    private ColumnView _a6;

    public Tier1Kernel6(
        FunctionSignature signature, bool strict, Func<T1, T2, T3, T4, T5, T6, TOut> f)
        : base(signature, strict, StructWriterFor<TOut>(signature))
    {
        _f = f;
        _produce = Produce;
    }

    public override void Invoke(ReadOnlySpan<ColumnView> args, ColumnWriter result, in FunctionContext context)
    {
        _a1 = args[0];
        _a2 = args[1];
        _a3 = args[2];
        _a4 = args[3];
        _a5 = args[4];
        _a6 = args[5];
        Run(args, result, context.RowCount, _produce);
    }

    private TOut Produce(int row) => _f(
        LaneCodec.Read<T1>(_a1, row),
        LaneCodec.Read<T2>(_a2, row),
        LaneCodec.Read<T3>(_a3, row),
        LaneCodec.Read<T4>(_a4, row),
        LaneCodec.Read<T5>(_a5, row),
        LaneCodec.Read<T6>(_a6, row));
}
