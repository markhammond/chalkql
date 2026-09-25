using Chalk.Sources;

namespace Chalk.Execution.Expressions;

/// <summary>
/// Tier 1 on top of Tier 2 (D79): a registered delegate, wrapped as the same
/// <see cref="IVectorFunction"/> a host would write by hand. There is one lane loop in the engine and
/// the two tiers cannot drift, which is the point of building it this way.
/// </summary>
/// <remarks>
/// <para>
/// A <c>STRICT</c> function's NULL lanes are skipped here, before the delegate is called — that is
/// what the flag means, and it also spares the host writing the check. A non-strict function sees
/// every lane and decides for itself, which is why its arguments are declared with nullable CLR
/// types.
/// </para>
/// <para>
/// Every type parameter <c>allows ref struct</c> (D304): a STRING or BINARY argument is handed to the
/// delegate as a <c>ReadOnlySpan&lt;byte&gt;</c> over the lane, and a result may be one, copied into
/// the column before the next call. The compiler keeps both inside the call, which is the point.
/// </remarks>
internal abstract class Tier1Kernel : IVectorFunction
{
    /// <summary>
    /// The compiled writer of a COMPOSITE result (D294), a <c>CompositeWriter&lt;TOut&gt;</c> built once at
    /// binding; null for every scalar result.
    /// </summary>
    private readonly object? _composite;

    /// <summary>The result's lane format (D298): a DECIMAL's scale, a TIMESTAMP's unit, and whose it is.</summary>
    private readonly LaneFormat _result;

    protected Tier1Kernel(FunctionSignature signature, bool strict, object? compositeWriter = null)
    {
        Signature = signature;
        Strict = strict;
        _composite = compositeWriter;
        _result = new LaneFormat(signature.ReturnType, $"the result of '{signature.Name}'");
    }

    public FunctionSignature Signature { get; }

    /// <summary>Argument <paramref name="index"/>'s lane format, built once when the kernel is bound.</summary>
    protected LaneFormat ArgumentFormat(int index) =>
        new(Signature.Parameters[index], $"argument {index + 1} of '{Signature.Name}'");

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
        where TOut : allows ref struct
    {
        if (_composite is CompositeWriter<TOut> record)
        {
            RunComposite(args, result, length, produce, record);
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

                LaneCodec.Append(result, length, row, produce(row), in _result);
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

            LaneCodec.Write(result, length, row, value, in _result);
            Apache.Arrow.BitUtility.SetBit(validity, row);
        }
    }

    /// <summary>
    /// The lane loop of a COMPOSITE result (D294): one delegate call per lane, one write per field, and
    /// the composite's own validity. A strict lane is skipped as a scalar's is, and still gets its row in
    /// every field, which is what keeps the fields aligned with the composite.
    /// </summary>
    private void RunComposite<TOut>(
        ReadOnlySpan<ColumnView> args,
        ColumnWriter result,
        int length,
        LaneProducer<TOut> produce,
        CompositeWriter<TOut> record)
        where TOut : allows ref struct
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

    /// <summary>The composite writer a kernel of this result type needs, or null for a scalar result.</summary>
    protected static object? CompositeWriterFor<TOut>(FunctionSignature signature)
        where TOut : allows ref struct
        =>
        signature.ReturnType.Kind == Ir.TypeKind.Composite
            ? CompositeWriters.For<TOut>(signature.ReturnType, $"the result of '{signature.Name}'")
            : null;

    /// <summary>One row's answer. A struct-free delegate: it closes over nothing this code allocates.</summary>
    protected delegate TOut LaneProducer<out TOut>(int row)
        where TOut : allows ref struct;
}

internal sealed class Tier1Kernel0<TOut> : Tier1Kernel
    where TOut : allows ref struct
{
    private readonly Func<TOut> _f;
    private readonly LaneProducer<TOut> _produce;

    public Tier1Kernel0(FunctionSignature signature, bool strict, Func<TOut> f)
        : base(signature, strict, CompositeWriterFor<TOut>(signature))
    {
        _f = f;
        _produce = _ => _f();
    }

    public override void Invoke(ReadOnlySpan<ColumnView> args, ColumnWriter result, in FunctionContext context) =>
        Run(args, result, context.RowCount, _produce);
}

internal sealed class Tier1Kernel1<T1, TOut> : Tier1Kernel
    where T1 : allows ref struct
    where TOut : allows ref struct
{
    private readonly Func<T1, TOut> _f;
    private readonly LaneProducer<TOut> _produce;
    private ColumnView _a1;
    private readonly LaneFormat _f1;

    public Tier1Kernel1(FunctionSignature signature, bool strict, Func<T1, TOut> f)
        : base(signature, strict, CompositeWriterFor<TOut>(signature))
    {
        _f = f;
        _produce = Produce;
        _f1 = ArgumentFormat(0);
    }

    public override void Invoke(ReadOnlySpan<ColumnView> args, ColumnWriter result, in FunctionContext context)
    {
        _a1 = args[0];
        Run(args, result, context.RowCount, _produce);
    }

    private TOut Produce(int row) => _f(LaneCodec.Read<T1>(_a1, row, in _f1));
}

internal sealed class Tier1Kernel2<T1, T2, TOut> : Tier1Kernel
    where T1 : allows ref struct
    where T2 : allows ref struct
    where TOut : allows ref struct
{
    private readonly Func<T1, T2, TOut> _f;
    private readonly LaneProducer<TOut> _produce;
    private ColumnView _a1;
    private ColumnView _a2;
    private readonly LaneFormat _f1;
    private readonly LaneFormat _f2;

    public Tier1Kernel2(FunctionSignature signature, bool strict, Func<T1, T2, TOut> f)
        : base(signature, strict, CompositeWriterFor<TOut>(signature))
    {
        _f = f;
        _produce = Produce;
        _f1 = ArgumentFormat(0);
        _f2 = ArgumentFormat(1);
    }

    public override void Invoke(ReadOnlySpan<ColumnView> args, ColumnWriter result, in FunctionContext context)
    {
        _a1 = args[0];
        _a2 = args[1];
        Run(args, result, context.RowCount, _produce);
    }

    private TOut Produce(int row) => _f(LaneCodec.Read<T1>(_a1, row, in _f1), LaneCodec.Read<T2>(_a2, row, in _f2));
}

internal sealed class Tier1Kernel3<T1, T2, T3, TOut> : Tier1Kernel
    where T1 : allows ref struct
    where T2 : allows ref struct
    where T3 : allows ref struct
    where TOut : allows ref struct
{
    private readonly Func<T1, T2, T3, TOut> _f;
    private readonly LaneProducer<TOut> _produce;
    private ColumnView _a1;
    private ColumnView _a2;
    private ColumnView _a3;
    private readonly LaneFormat _f1;
    private readonly LaneFormat _f2;
    private readonly LaneFormat _f3;

    public Tier1Kernel3(FunctionSignature signature, bool strict, Func<T1, T2, T3, TOut> f)
        : base(signature, strict, CompositeWriterFor<TOut>(signature))
    {
        _f = f;
        _produce = Produce;
        _f1 = ArgumentFormat(0);
        _f2 = ArgumentFormat(1);
        _f3 = ArgumentFormat(2);
    }

    public override void Invoke(ReadOnlySpan<ColumnView> args, ColumnWriter result, in FunctionContext context)
    {
        _a1 = args[0];
        _a2 = args[1];
        _a3 = args[2];
        Run(args, result, context.RowCount, _produce);
    }

    private TOut Produce(int row) => _f(
        LaneCodec.Read<T1>(_a1, row, in _f1), LaneCodec.Read<T2>(_a2, row, in _f2), LaneCodec.Read<T3>(_a3, row, in _f3));
}

internal sealed class Tier1Kernel4<T1, T2, T3, T4, TOut> : Tier1Kernel
    where T1 : allows ref struct
    where T2 : allows ref struct
    where T3 : allows ref struct
    where T4 : allows ref struct
    where TOut : allows ref struct
{
    private readonly Func<T1, T2, T3, T4, TOut> _f;
    private readonly LaneProducer<TOut> _produce;
    private ColumnView _a1;
    private ColumnView _a2;
    private ColumnView _a3;
    private ColumnView _a4;
    private readonly LaneFormat _f1;
    private readonly LaneFormat _f2;
    private readonly LaneFormat _f3;
    private readonly LaneFormat _f4;

    public Tier1Kernel4(FunctionSignature signature, bool strict, Func<T1, T2, T3, T4, TOut> f)
        : base(signature, strict, CompositeWriterFor<TOut>(signature))
    {
        _f = f;
        _produce = Produce;
        _f1 = ArgumentFormat(0);
        _f2 = ArgumentFormat(1);
        _f3 = ArgumentFormat(2);
        _f4 = ArgumentFormat(3);
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
        LaneCodec.Read<T1>(_a1, row, in _f1),
        LaneCodec.Read<T2>(_a2, row, in _f2),
        LaneCodec.Read<T3>(_a3, row, in _f3),
        LaneCodec.Read<T4>(_a4, row, in _f4));
}

internal sealed class Tier1Kernel5<T1, T2, T3, T4, T5, TOut> : Tier1Kernel
    where T1 : allows ref struct
    where T2 : allows ref struct
    where T3 : allows ref struct
    where T4 : allows ref struct
    where T5 : allows ref struct
    where TOut : allows ref struct
{
    private readonly Func<T1, T2, T3, T4, T5, TOut> _f;
    private readonly LaneProducer<TOut> _produce;
    private ColumnView _a1;
    private ColumnView _a2;
    private ColumnView _a3;
    private ColumnView _a4;
    private ColumnView _a5;
    private readonly LaneFormat _f1;
    private readonly LaneFormat _f2;
    private readonly LaneFormat _f3;
    private readonly LaneFormat _f4;
    private readonly LaneFormat _f5;

    public Tier1Kernel5(FunctionSignature signature, bool strict, Func<T1, T2, T3, T4, T5, TOut> f)
        : base(signature, strict, CompositeWriterFor<TOut>(signature))
    {
        _f = f;
        _produce = Produce;
        _f1 = ArgumentFormat(0);
        _f2 = ArgumentFormat(1);
        _f3 = ArgumentFormat(2);
        _f4 = ArgumentFormat(3);
        _f5 = ArgumentFormat(4);
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
        LaneCodec.Read<T1>(_a1, row, in _f1),
        LaneCodec.Read<T2>(_a2, row, in _f2),
        LaneCodec.Read<T3>(_a3, row, in _f3),
        LaneCodec.Read<T4>(_a4, row, in _f4),
        LaneCodec.Read<T5>(_a5, row, in _f5));
}

internal sealed class Tier1Kernel6<T1, T2, T3, T4, T5, T6, TOut> : Tier1Kernel
    where T1 : allows ref struct
    where T2 : allows ref struct
    where T3 : allows ref struct
    where T4 : allows ref struct
    where T5 : allows ref struct
    where T6 : allows ref struct
    where TOut : allows ref struct
{
    private readonly Func<T1, T2, T3, T4, T5, T6, TOut> _f;
    private readonly LaneProducer<TOut> _produce;
    private ColumnView _a1;
    private ColumnView _a2;
    private ColumnView _a3;
    private ColumnView _a4;
    private ColumnView _a5;
    private ColumnView _a6;
    private readonly LaneFormat _f1;
    private readonly LaneFormat _f2;
    private readonly LaneFormat _f3;
    private readonly LaneFormat _f4;
    private readonly LaneFormat _f5;
    private readonly LaneFormat _f6;

    public Tier1Kernel6(
        FunctionSignature signature, bool strict, Func<T1, T2, T3, T4, T5, T6, TOut> f)
        : base(signature, strict, CompositeWriterFor<TOut>(signature))
    {
        _f = f;
        _produce = Produce;
        _f1 = ArgumentFormat(0);
        _f2 = ArgumentFormat(1);
        _f3 = ArgumentFormat(2);
        _f4 = ArgumentFormat(3);
        _f5 = ArgumentFormat(4);
        _f6 = ArgumentFormat(5);
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
        LaneCodec.Read<T1>(_a1, row, in _f1),
        LaneCodec.Read<T2>(_a2, row, in _f2),
        LaneCodec.Read<T3>(_a3, row, in _f3),
        LaneCodec.Read<T4>(_a4, row, in _f4),
        LaneCodec.Read<T5>(_a5, row, in _f5),
        LaneCodec.Read<T6>(_a6, row, in _f6));
}
