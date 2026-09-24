using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Execution.Vectors;
using Chalk.Sources;

namespace Chalk.Execution.Expressions;

/// <summary>
/// One field of a COMPOSITE (D291, ADR 0077): the composite's field column, as a view of the composite's rows.
/// </summary>
/// <remarks>
/// A borrow, never a copy: when the composite has no NULL row the answer is the field's own view, and
/// otherwise it is the same view with a validity of the field's <em>and</em> the composite's — a NULL
/// composite reads as NULL in every field, whatever the field holds underneath. The only thing written
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
            // No composite constant exists (D291): a scalar here is a typed NULL, and so is its field.
            return Vector.FromScalar(ScalarValue.Null(Type), length);
        }

        var composite = value.View;

        // A composite value the engine finished has no offset and fields of its own length, so the field is
        // its view exactly, null count and all; a sliced one is sliced field by field.
        var child = composite.Children![_index];
        var field = (composite.Offset == 0 && child.Length == composite.Length
            ? child
            : composite.FieldView(_index)) with { Type = Type };
        var compositeBits = composite.ValidityBits();
        if (compositeBits.IsEmpty || composite.NullCount == 0)
        {
            return value.IsTransient ? Vector.Transient(field, length) : Vector.FromView(field, length);
        }

        // Field ∧ composite, written at the field view's own offset.
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

/// <summary>
/// A <c>CASE</c> or a <c>COALESCE</c> over composite values of one type (D295): the choice is made per
/// lane as any <c>CASE</c> makes it — the first clause whose condition holds, or the first operand
/// that holds a value — and the answer is the chosen branch's composite, field by field. Each field
/// is written from the chosen branch's field, as a scalar <c>CASE</c> over the same conditions would
/// write it, and the composite's validity is the chosen branch's.
/// </summary>
/// <remarks>
/// A lane that chooses nothing, or chooses a NULL composite, is a NULL composite. Its nullable fields
/// are NULL and its non-nullable fields hold their storage default, as the composite writer leaves
/// them (D294): a non-nullable field column never holds a NULL. Every branch is evaluated over the
/// whole batch and chosen from, as <see cref="IfThenExpr"/> does, so the selection is the batch's
/// and a sharing parent sees one answer per batch (D299).
/// </remarks>
internal sealed class CompositeChoiceExpr : VectorExprBase
{
    private readonly IVectorExpr[] _conditions;
    private readonly IVectorExpr[] _branches;
    private readonly bool _coalesce;
    private readonly Vector[] _vectors;
    private readonly ColumnView[] _fields;
    private int[] _choice = [];

    /// <summary>A <c>CASE</c>: one branch per condition, then the <c>ELSE</c>.</summary>
    public static CompositeChoiceExpr Case(
        ChalkType type, IVectorExpr[] conditions, IVectorExpr[] results, IVectorExpr elseBranch) =>
        new(type, conditions, [.. results, elseBranch], coalesce: false);

    /// <summary>A <c>COALESCE</c>: the first operand that holds a value.</summary>
    public static CompositeChoiceExpr Coalesce(ChalkType type, IVectorExpr[] operands) =>
        new(type, [], operands, coalesce: true);

    private CompositeChoiceExpr(
        ChalkType type, IVectorExpr[] conditions, IVectorExpr[] branches, bool coalesce)
        : base(type)
    {
        _conditions = conditions;
        _branches = branches;
        _coalesce = coalesce;
        _vectors = new Vector[branches.Length];
        _fields = new ColumnView[branches.Length];
    }

    public override Vector Evaluate(EvalContext context)
    {
        var length = context.Length;
        var choice = CoalesceExpr.Choices(ref _choice, length);
        if (_coalesce)
        {
            for (var k = 0; k < _branches.Length; k++)
            {
                _vectors[k] = _branches[k].Evaluate(context);
            }

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
        }
        else
        {
            // Conditions first, so a lane that has already matched is left alone by later clauses;
            // the ELSE is the last branch.
            var otherwise = _branches.Length - 1;
            choice.Fill(otherwise);
            for (var k = 0; k < _conditions.Length; k++)
            {
                var condition = _conditions[k].Evaluate(context);
                var lanes = Lanes<byte>.From(condition);
                for (var i = 0; i < length; i++)
                {
                    if (choice[i] == otherwise && lanes.IsValid(i) && lanes[i] != 0)
                    {
                        choice[i] = k;
                    }
                }
            }

            for (var k = 0; k < _branches.Length; k++)
            {
                _vectors[k] = _branches[k].Evaluate(context);
            }
        }

        // The composite's own validity: the chosen branch's. A lane whose choice is a NULL composite,
        // or nothing at all, is marked -1 so that every field below writes its NULL row.
        var bits = Scratch.BeginValidity(length);
        var nulls = 0;
        for (var i = 0; i < length; i++)
        {
            var pick = choice[i];
            if (pick >= 0 && IsValidAt(_vectors[pick], i))
            {
                BitUtility.SetBit(bits, i);
            }
            else
            {
                choice[i] = -1;
                nulls++;
            }
        }

        var fieldTypes = Type.Fields;
        for (var f = 0; f < fieldTypes.Count; f++)
        {
            for (var k = 0; k < _vectors.Length; k++)
            {
                // A scalar branch is a typed NULL composite (no composite constant exists, D291), which
                // no lane chose above; its field view is never read.
                _fields[k] = _vectors[k].IsScalar ? default : _vectors[k].View.FieldView(f);
            }

            WriteField(Scratch.Field(f), fieldTypes[f].Type, length, choice);
        }

        return Scratch.Finish(length, nulls);
    }

    /// <summary>One field of the answer, lane by lane from the chosen branch's field.</summary>
    private void WriteField(VectorScratch field, ChalkType type, int length, ReadOnlySpan<int> choice)
    {
        var kind = ColumnKinds.Of(type);
        if (ColumnKinds.IsVariableLength(kind))
        {
            field.BeginVarLen(length, nullable: type.Nullable);
            for (var i = 0; i < length; i++)
            {
                var pick = choice[i];
                if (pick >= 0 && _fields[pick].IsValid(i))
                {
                    field.AppendValue(_fields[pick].VarValue(i));
                }
                else if (type.Nullable)
                {
                    field.AppendNull();
                }
                else
                {
                    field.AppendValue(default);
                }
            }

            return;
        }

        var width = ColumnKinds.Width(kind);
        var lanes = field.RawValues(length);
        var bits = type.Nullable ? field.BeginValidity(length) : default;
        if (!type.Nullable)
        {
            field.NoValidity();
        }

        for (var i = 0; i < length; i++)
        {
            var lane = lanes.Slice(i * width, width);
            var pick = choice[i];
            if (pick < 0 || !_fields[pick].IsValid(i))
            {
                lane.Clear();
                continue;
            }

            if (kind == ColumnKind.Boolean)
            {
                // A source's booleans may be bit-packed and the engine's are a byte a row (§6.4).
                lane[0] = (byte)(_fields[pick].BoolAt(i) ? 1 : 0);
            }
            else
            {
                _fields[pick].RawLanes(width).Slice(i * width, width).CopyTo(lane);
            }

            if (type.Nullable)
            {
                BitUtility.SetBit(bits, i);
            }
        }
    }
}
