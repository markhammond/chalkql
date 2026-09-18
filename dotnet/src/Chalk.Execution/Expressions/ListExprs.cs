using Chalk.Catalog;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using Array = System.Array;

namespace Chalk.Execution.Expressions;

/// <summary>
/// <c>CARDINALITY(list)</c> — the number of elements, and NULL for a NULL list (D58,
/// <c>14-windows-ii.md</c> §5). One subtraction per row off the offsets buffer.
/// </summary>
internal sealed class CardinalityExpr : VectorExprBase
{
    private readonly IVectorExpr _list;

    public CardinalityExpr(ChalkType type, IVectorExpr list)
        : base(type) => _list = list;

    public override Vector Evaluate(EvalContext context)
    {
        var length = context.Length;
        var list = _list.Evaluate(context);
        var nulls = InheritValidity(length, list, context.SelectionMask);
        var values = Scratch.Values<int>(length);

        if (list.IsScalar)
        {
            values.Fill(list.Scalar.IsNull ? 0 : list.Scalar.Elements.Count);
            return Scratch.Finish(length, nulls);
        }

        var offsets = list.View.OffsetLanes();
        for (var i = 0; i < length; i++)
        {
            values[i] = offsets[i + 1] - offsets[i];
        }

        return Scratch.Finish(length, nulls);
    }
}

/// <summary>
/// <c>list[index]</c> — SQL's <c>ITEM</c> (D58). One-based; NULL when the list is NULL, when the
/// index is NULL, and when the index is outside <c>1 .. CARDINALITY(list)</c>, which is what makes
/// this operator total rather than an error.
/// </summary>
/// <remarks>
/// The element is copied through a <see cref="ColumnCopier"/> of the element's own type rather than
/// read as a value, so every element kind — including DECIMAL, UUID and STRING — works without this
/// node knowing anything about layouts.
/// </remarks>
internal sealed class ListItemExpr : IVectorExpr
{
    private readonly IVectorExpr _list;
    private readonly IVectorExpr _index;
    private readonly ColumnCopier _output;

    public ListItemExpr(ChalkType type, IVectorExpr list, IVectorExpr index)
    {
        Type = type;
        _list = list;
        _index = index;
        _output = new ColumnCopier(type);
    }

    public ChalkType Type { get; }

    public Vector Evaluate(EvalContext context)
    {
        var length = context.Length;
        var list = _list.Evaluate(context);
        var index = _index.Evaluate(context);

        _output.Begin();
        for (var i = 0; i < length; i++)
        {
            if (!Present(list, i) || !Present(index, i))
            {
                _output.AppendNulls(1);
                continue;
            }

            var wanted = index.IsScalar ? (int)index.Scalar.Integer : IndexAt(index.View, i);
            if (list.IsScalar)
            {
                var elements = list.Scalar.Elements;
                if (wanted < 1 || wanted > elements.Count)
                {
                    _output.AppendNulls(1);
                }
                else
                {
                    _output.AppendConstant(elements[wanted - 1], 1);
                }

                continue;
            }

            var offsets = list.View.OffsetLanes();
            var start = offsets[i];
            var count = offsets[i + 1] - start;
            if (wanted < 1 || wanted > count)
            {
                _output.AppendNulls(1);
                continue;
            }

            _output.AppendRow(list.View.Child, start + wanted - 1);
        }

        // A view over the copier's own arena buffers, valid until this node evaluates again — the
        // same bargain VectorScratch makes for every other kernel (§6.4, D61).
        return Vector.Transient(_output.FinishView(), length);
    }

    private static bool Present(in Vector vector, int row) =>
        vector.IsScalar ? !vector.Scalar.IsNull : vector.View.IsValid(row);

    /// <summary>The index, which the IR types as I32 (I-IR-2 casts anything else on the way in).</summary>
    private static int IndexAt(in ColumnView view, int row) => view.Lanes<int>()[row];
}

/// <summary>
/// <c>ARRAY_TO_STRING(list, separator)</c> — the elements joined by the separator, NULL elements
/// skipped, NULL for a NULL list or a NULL separator, and the empty string for an empty list
/// (D58; Calcite's BIG_QUERY library spells it).
/// </summary>
internal sealed class ArrayToStringExpr : VectorExprBase
{
    private readonly IVectorExpr _list;
    private readonly IVectorExpr _separator;
    private byte[] _buffer = new byte[64];

    public ArrayToStringExpr(ChalkType type, IVectorExpr list, IVectorExpr separator)
        : base(type)
    {
        _list = list;
        _separator = separator;
    }

    public override Vector Evaluate(EvalContext context)
    {
        var length = context.Length;
        var list = _list.Evaluate(context);
        var separator = _separator.Evaluate(context);

        Scratch.BeginVarLen(length, nullable: true);
        for (var i = 0; i < length; i++)
        {
            if (!Present(list, i) || !Present(separator, i))
            {
                Scratch.AppendNull();
                continue;
            }

            var glue = separator.IsScalar
                ? separator.Scalar.ReadBytes()
                : separator.View.VarValue(i);

            var used = 0;
            var first = true;

            // Written twice rather than through an iterator: an enumerator per row is an allocation
            // per row, which the engine does not do (ADR 0007).
            if (list.IsScalar)
            {
                foreach (var element in list.Scalar.Elements)
                {
                    if (element.IsNull)
                    {
                        continue;
                    }

                    Append(element.ReadBytes(), glue, ref used, ref first);
                }
            }
            else
            {
                var offsets = list.View.OffsetLanes();
                var elements = list.View.Child;
                for (var e = offsets[i]; e < offsets[i + 1]; e++)
                {
                    if (elements.IsValid(e))
                    {
                        Append(elements.VarValue(e), glue, ref used, ref first);
                    }
                }
            }

            Scratch.AppendValue(_buffer.AsSpan(0, used));
        }

        return Scratch.FinishVarLen();
    }

    private void Append(
        ReadOnlySpan<byte> element, ReadOnlySpan<byte> glue, ref int used, ref bool first)
    {
        if (!first)
        {
            Reserve(used + glue.Length);
            glue.CopyTo(_buffer.AsSpan(used));
            used += glue.Length;
        }

        Reserve(used + element.Length);
        element.CopyTo(_buffer.AsSpan(used));
        used += element.Length;
        first = false;
    }

    private static bool Present(in Vector vector, int row) =>
        vector.IsScalar ? !vector.Scalar.IsNull : vector.View.IsValid(row);

    private void Reserve(int required)
    {
        if (_buffer.Length < required)
        {
            Array.Resize(ref _buffer, Math.Max(required, _buffer.Length * 2));
        }
    }
}
