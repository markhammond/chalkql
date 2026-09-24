using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Execution.Vectors;
using Chalk.Sources;

namespace Chalk.Execution.Expressions;

/// <summary>
/// One field of a STRUCT (D291, ADR 0077): the struct's field column, as a view of the struct's rows.
/// </summary>
/// <remarks>
/// A borrow, never a copy: when the struct has no NULL row the answer is the field's own view, and
/// otherwise it is the same view with a validity of the field's <em>and</em> the struct's — a NULL
/// struct reads as NULL in every field, whatever the field holds underneath. The only thing written
/// is that bitmap, into this node's scratch, aligned with the field view's offset so the values stay
/// where they are.
/// </remarks>
internal sealed class FieldAccessExpr : VectorExprBase
{
    private readonly IVectorExpr _input;
    private readonly int _index;
    private ArenaBuffer _bits = new();

    public FieldAccessExpr(ChalkType type, IVectorExpr input, int index)
        : base(type)
    {
        _input = input;
        _index = index;
        ScratchScope.Register(new BitsScratch(this));
    }

    public override Vector Evaluate(EvalContext context)
    {
        var length = context.Length;
        var value = _input.Evaluate(context);
        if (value.IsScalar)
        {
            // No struct constant exists (D291): a scalar here is a typed NULL, and so is its field.
            return Vector.FromScalar(ScalarValue.Null(Type), length);
        }

        var composite = value.View;

        // A struct the engine finished has no offset and fields of its own length, so the field is
        // its view exactly, null count and all; a sliced one is sliced field by field.
        var child = composite.Children![_index];
        var field = (composite.Offset == 0 && child.Length == composite.Length
            ? child
            : composite.StructField(_index)) with { Type = Type };
        var structBits = composite.ValidityBits();
        if (structBits.IsEmpty || composite.NullCount == 0)
        {
            return value.IsTransient ? Vector.Transient(field, length) : Vector.FromView(field, length);
        }

        // Field ∧ struct, written at the field view's own offset.
        var offset = field.Offset;
        var bytes = Validity.ByteCount(offset + length);
        _bits.Ensure(Math.Max(bytes, 1));
        var bits = _bits.Bytes.AsSpan(0, bytes);
        bits.Clear();
        var nulls = 0;
        for (var row = 0; row < length; row++)
        {
            if (composite.IsValid(row) && field.IsValid(row))
            {
                BitUtility.SetBit(bits, offset + row);
            }
            else
            {
                nulls++;
            }
        }

        var narrowed = field with
        {
            Validity = _bits.Bytes.AsMemory(0, bytes),
            NullCount = nulls,
        };
        return Vector.Transient(narrowed, length);
    }

    /// <summary>Hands the node's bitmap buffer to the execution's arena and back.</summary>
    private sealed class BitsScratch(FieldAccessExpr owner) : IArenaScratch
    {
        public void Acquire(ExecutionArena arena) => owner._bits.Acquire(arena);

        public void Release() => owner._bits.Release();
    }
}
