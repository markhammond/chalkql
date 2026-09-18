using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Execution.Vectors;

namespace Chalk.Execution.Expressions;

/// <summary>
/// Shared machinery for compiled expressions: the reusable output scratch, and the null handling that
/// §6.4 keeps out of the kernels — a result is valid exactly where every operand is.
/// </summary>
internal abstract class VectorExprBase : IVectorExpr
{
    protected VectorExprBase(ChalkType type)
    {
        Type = type;
        Kind = ColumnKinds.Of(type);
        Width = ColumnKinds.Width(Kind);
        Scratch = new VectorScratch(type);
    }

    public ChalkType Type { get; }

    protected ColumnKind Kind { get; }

    protected int Width { get; }

    protected VectorScratch Scratch { get; }

    public abstract Vector Evaluate(EvalContext context);

    /// <summary>
    /// Result validity = the operand's validity, narrowed to <paramref name="selection"/>. Returns
    /// the null count. Every caller passes <c>context.SelectionMask</c>: there is no overload
    /// without it, because forgetting it is a guarded kernel raising on a row the query excluded.
    /// </summary>
    protected int InheritValidity(int length, in Vector operand, ReadOnlySpan<byte> selection)
    {
        if (operand.IsScalar)
        {
            if (!operand.Scalar.IsNull && selection.IsEmpty)
            {
                Scratch.NoValidity();
                return 0;
            }

            var bits = Scratch.BeginValidity(length);
            if (operand.Scalar.IsNull)
            {
                bits.Clear();
                return length;
            }

            selection[..Validity.ByteCount(length)].CopyTo(bits);
            return Validity.CountNulls(bits, length);
        }

        if (operand.View.ValidityBits().IsEmpty && selection.IsEmpty)
        {
            Scratch.NoValidity();
            return 0;
        }

        var validity = Scratch.BeginValidity(length);
        Validity.CopyFrom(validity, operand.View, length);
        Narrow(validity, selection, length);
        return Validity.CountNulls(validity, length);
    }

    /// <summary>Clears the bits of every lane the batch did not select.</summary>
    private static void Narrow(Span<byte> bits, ReadOnlySpan<byte> selection, int length)
    {
        if (selection.IsEmpty)
        {
            return;
        }

        var bytes = Validity.ByteCount(length);
        for (var b = 0; b < bytes; b++)
        {
            bits[b] &= selection[b];
        }
    }

    /// <summary>
    /// Starts a writable validity bitmap seeded from one operand's, for a kernel that has to clear
    /// further bits as it goes — the casts, whose <c>CAST_FAILURE_NULL</c> turns a failure into a NULL.
    /// </summary>
    protected Span<byte> BeginInheritedValidity(
        int length, in Vector operand, ReadOnlySpan<byte> selection)
    {
        var bits = Scratch.BeginValidity(length);
        Validity.SetAll(bits, length);
        Narrow(bits, selection, length);
        Intersect(bits, operand, length);
        return bits;
    }

    /// <summary>
    /// Result validity = the intersection of both operands', narrowed to the selected lanes.
    /// </summary>
    protected int IntersectValidity(
        int length, in Vector left, in Vector right, ReadOnlySpan<byte> selection)
    {
        if (selection.IsEmpty && IsAllValid(left) && IsAllValid(right))
        {
            Scratch.NoValidity();
            return 0;
        }

        var bits = Scratch.BeginValidity(length);
        Validity.SetAll(bits, length);
        Narrow(bits, selection, length);
        Intersect(bits, left, length);
        Intersect(bits, right, length);
        return Validity.CountNulls(bits, length);
    }

    /// <summary>
    /// Result validity = the intersection of every operand's, narrowed to the selected lanes.
    /// </summary>
    protected int IntersectValidity(
        int length, ReadOnlySpan<Vector> operands, ReadOnlySpan<byte> selection)
    {
        var allValid = selection.IsEmpty;
        foreach (var operand in operands)
        {
            allValid &= IsAllValid(operand);
        }

        if (allValid)
        {
            Scratch.NoValidity();
            return 0;
        }

        var bits = Scratch.BeginValidity(length);
        Validity.SetAll(bits, length);
        Narrow(bits, selection, length);
        foreach (var operand in operands)
        {
            Intersect(bits, operand, length);
        }

        return Validity.CountNulls(bits, length);
    }

    /// <summary>
    /// Builds the result by taking, for each row, the lane of one already-evaluated operand — or a NULL.
    /// COALESCE, NULLIF, CASE and the identity casts all reduce to this.
    /// </summary>
    /// <param name="choice">Per row: the index into <paramref name="sources"/>, or -1 for NULL.</param>
    protected Vector Assemble(int length, ReadOnlySpan<Vector> sources, ReadOnlySpan<int> choice)
    {
        if (ColumnKinds.IsVariableLength(Kind))
        {
            Scratch.BeginVarLen(length, nullable: true);
            for (var i = 0; i < length; i++)
            {
                var pick = choice[i];
                if (pick < 0 || !IsValidAt(sources[pick], i))
                {
                    Scratch.AppendNull();
                    continue;
                }

                var source = sources[pick];
                Scratch.AppendValue(source.IsScalar
                    ? source.Scalar.ReadBytes()
                    : source.View.VarValue(i));
            }

            return Scratch.FinishVarLen();
        }

        var values = Scratch.RawValues(length);
        var bits = Scratch.BeginValidity(length);
        var nulls = 0;
        for (var i = 0; i < length; i++)
        {
            var pick = choice[i];
            if (pick < 0 || !IsValidAt(sources[pick], i))
            {
                values.Slice(i * Width, Width).Clear();
                nulls++;
                continue;
            }

            BitUtility.SetBit(bits, i);
            var source = sources[pick];
            if (source.IsScalar)
            {
                WriteScalarLane(source.Scalar, values.Slice(i * Width, Width));
            }
            else
            {
                source.View.RawLanes(Width)
                    .Slice(i * Width, Width)
                    .CopyTo(values[(i * Width)..]);
            }
        }

        if (nulls == 0)
        {
            Scratch.NoValidity();
        }

        return Scratch.Finish(length, nulls);
    }

    /// <summary>Writes one broadcast constant into a fixed-width lane.</summary>
    protected void WriteScalarLane(ScalarValue value, Span<byte> lane) =>
        ScalarLanes.Write(Kind, value, lane);

    /// <summary>Whether one row of a vector holds a value, array or scalar.</summary>
    protected static bool IsValidAt(in Vector vector, int row) => vector.IsScalar
        ? !vector.Scalar.IsNull
        : vector.View.IsValid(row);

    private static bool IsAllValid(in Vector vector) => vector.IsScalar
        ? !vector.Scalar.IsNull
        : vector.View.ValidityBits().IsEmpty;

    private static void Intersect(Span<byte> bits, in Vector operand, int length)
    {
        if (operand.IsScalar)
        {
            if (operand.Scalar.IsNull)
            {
                bits.Clear();
            }

            return;
        }

        Validity.AndFrom(bits, operand.View, length);
    }
}
