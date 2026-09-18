using Chalk.Catalog;
using Chalk.Execution.Vectors;

namespace Chalk.Execution.Expressions;

/// <summary>A constant. It never materialises into an array (§6.4).</summary>
internal sealed class LiteralExpr : IVectorExpr
{
    private readonly ScalarValue _value;

    public LiteralExpr(ScalarValue value)
    {
        _value = value;
        Type = value.Type;
    }

    public ChalkType Type { get; }

    public Vector Evaluate(EvalContext context) => Vector.FromScalar(_value, context.Length);
}

/// <summary>A bound dynamic parameter — also a constant for the length of one execution.</summary>
internal sealed class ParameterExpr : IVectorExpr
{
    private readonly int _index;

    public ParameterExpr(int index, ChalkType type)
    {
        _index = index;
        Type = type;
    }

    public ChalkType Type { get; }

    public Vector Evaluate(EvalContext context) =>
        Vector.FromScalar(context.Parameters[_index], context.Length);
}

/// <summary>
/// A field of the input row. The batch's own view is handed straight on: a pass-through column is
/// borrowed rather than copied (§6.1).
/// </summary>
internal sealed class FieldRefExpr : IVectorExpr
{
    private readonly int _index;

    public FieldRefExpr(int index, ChalkType type)
    {
        _index = index;
        Type = type;
    }

    public ChalkType Type { get; }

    /// <summary>Which input column this reads, so an operator can forward it without evaluating.</summary>
    public int Index => _index;

    public Vector Evaluate(EvalContext context)
    {
        var batch = context.Batch
            ?? throw new InvalidOperationException("A field reference was evaluated with no input batch.");
        return Vector.FromView(batch.Column(_index), context.Length);
    }
}

/// <summary>
/// A BOOL field, unpacked from Arrow's bit-packed representation into the engine's byte per row so
/// that every boolean a kernel meets has the same shape (§6.4).
/// </summary>
internal sealed class BooleanFieldRefExpr : VectorExprBase
{
    private readonly int _index;

    public BooleanFieldRefExpr(int index, ChalkType type)
        : base(type) => _index = index;

    public override Vector Evaluate(EvalContext context)
    {
        var batch = context.Batch
            ?? throw new InvalidOperationException("A field reference was evaluated with no input batch.");
        var source = batch.Column(_index);
        var length = context.Length;
        var values = Scratch.Values<byte>(length);
        for (var i = 0; i < length; i++)
        {
            values[i] = (byte)(source.BoolAt(i) ? 1 : 0);
        }

        var nulls = InheritValidity(length, Vector.FromView(source, length), context.SelectionMask);
        return Scratch.Finish(length, nulls);
    }
}
