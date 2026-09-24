using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Sources;

namespace Chalk.Execution.Vectors;

/// <summary>
/// One expression node's reusable output buffer. The scratch is grown to the widest batch the
/// execution meets and then never allocates again, which is what keeps the per-row allocation budget
/// (ADR 0007, "the allocation budget is per column per batch").
/// </summary>
/// <remarks>
/// Since step 20 (D61, D64) the buffers are rented from the execution's arena rather than grown on
/// the heap, and what is handed back is a <see cref="ColumnView"/> rather than an Arrow array — no
/// object graph per batch, and nothing plan-shaped left on the heap for a warm tree to hold. The view
/// wraps this scratch without owning it, so it is only valid until the node evaluates its next batch
/// — <see cref="Vector.IsTransient"/> says so. An operator that puts a value into a batch it hands
/// on copies it first.
/// </remarks>
internal sealed class VectorScratch : IArenaScratch, IColumnSink
{
    private readonly ColumnKind _kind;
    private readonly int _width;

    private ArenaBuffer _values = new();
    private ArenaBuffer _validity = new();
    private ArenaBuffer _data = new();

    // Offsets live as bytes so a view can wrap them without a copy per batch.
    private ArenaBuffer _offsets = new();

    private bool _useValidity;
    private int _varCursor;
    private int _varUsed;
    private int _varNulls;

    /// <summary>A COMPOSITE's field scratch, one per field in order (D291), and null for every other kind.</summary>
    private readonly VectorScratch[]? _fields;

    /// <summary>The finished field views a composite view points at, reused batch after batch.</summary>
    private readonly ColumnView[]? _fieldViews;

    public VectorScratch(ChalkType type)
    {
        Type = type;
        _kind = ColumnKinds.Of(type);
        _width = ColumnKinds.Width(_kind);
        if (_kind == ColumnKind.Composite)
        {
            _fields = [.. type.Fields.Select(f => new VectorScratch(f.Type))];
            _fieldViews = new ColumnView[_fields.Length];
        }

        ScratchScope.Register(this);
    }

    public ChalkType Type { get; }

    public ColumnKind Kind => _kind;

    public void Acquire(ExecutionArena arena)
    {
        _values.Acquire(arena);
        _validity.Acquire(arena);
        _data.Acquire(arena);
        _offsets.Acquire(arena);
        foreach (var field in _fields ?? [])
        {
            field.Acquire(arena);
        }
    }

    public void Release()
    {
        _values.Release();
        _validity.Release();
        _data.Release();
        _offsets.Release();
        _useValidity = false;
        foreach (var field in _fields ?? [])
        {
            field.Release();
        }
    }

    /// <summary>Field <paramref name="index"/>'s scratch, for a COMPOSITE (D291).</summary>
    public VectorScratch Field(int index) =>
        _fields?[index] ?? throw new InvalidOperationException("This scratch is not a COMPOSITE's.");

    /// <summary>The typed value lanes for a batch of <paramref name="length"/> rows.</summary>
    public Span<T> Values<T>(int length)
        where T : unmanaged
    {
        var bytes = length * Unsafe.SizeOf<T>();
        _values.Ensure(bytes);
        return MemoryMarshal.Cast<byte, T>(_values.Bytes.AsSpan(0, bytes));
    }

    /// <summary>The raw value lanes, for the 16-byte layouts.</summary>
    public Span<byte> RawValues(int length)
    {
        _values.Ensure(length * _width);
        return _values.Bytes.AsSpan(0, length * _width);
    }

    /// <summary>A zeroed validity bitmap to fill in. Calling this makes the result nullable.</summary>
    public Span<byte> BeginValidity(int length)
    {
        var bytes = Validity.ByteCount(length);
        _validity.Ensure(bytes);
        _validity.Bytes.AsSpan(0, bytes).Clear();
        _useValidity = true;
        return _validity.Bytes.AsSpan(0, bytes);
    }

    /// <summary>Declares that every row of the next result holds a value.</summary>
    public void NoValidity() => _useValidity = false;

    /// <summary>
    /// The bitmap as it stands, for a kernel that has to skip null lanes — checked arithmetic and
    /// division, whose garbage lanes would otherwise raise (§6.4). Empty when every row is valid.
    /// </summary>
    public ReadOnlySpan<byte> CurrentValidity(int length) =>
        _useValidity ? _validity.Bytes.AsSpan(0, Validity.ByteCount(length)) : default;

    /// <summary>
    /// The bitmap as written so far, to narrow rather than to fill: a Tier 2 kernel sets the bits it
    /// means, and the engine intersects the selection and the strict arguments into them afterwards.
    /// Empty when the result is not nullable.
    /// </summary>
    public Span<byte> MutableValidity(int length) =>
        _useValidity ? _validity.Bytes.AsSpan(0, Validity.ByteCount(length)) : default;

    /// <summary>Wraps the scratch as a column view. No Arrow object, no allocation (D61).</summary>
    public Vector Finish(int length, int nullCount)
    {
        if (_fields is not null)
        {
            return FinishComposite(length, nullCount);
        }

        return Vector.Transient(
            new ColumnView
            {
                Type = Type,
                Length = length,
                Values = _values.Bytes.AsMemory(0, length * _width),
                Validity = _useValidity
                    ? _validity.Bytes.AsMemory(0, Validity.ByteCount(length))
                    : default,
                NullCount = nullCount,
            },
            length);
    }

    /// <summary>
    /// A COMPOSITE's view (D291): its own validity over <paramref name="length"/> rows and each field's
    /// view as that field's scratch finishes it — every field written for every row, whatever the
    /// composite's own validity says.
    /// </summary>
    private Vector FinishComposite(int length, int nullCount)
    {
        for (var i = 0; i < _fields!.Length; i++)
        {
            _fieldViews![i] = _fields[i].FinishWritten(length);
        }

        return Vector.Transient(
            new ColumnView
            {
                Type = Type,
                Length = length,
                Validity = _useValidity
                    ? _validity.Bytes.AsMemory(0, Validity.ByteCount(length))
                    : default,
                NullCount = nullCount,
                Children = _fieldViews,
            },
            length);
    }

    /// <summary>
    /// This scratch as a finished view of <paramref name="length"/> rows, whatever wrote it: the
    /// appended rows of a variable-length one, or the lanes and bitmap of a fixed one, sized first so
    /// that a field no row wrote — every composite NULL — is still a view of the right length.
    /// </summary>
    public ColumnView FinishWritten(int length)
    {
        if (_fields is not null)
        {
            var validity = MutableValidity(length);
            return FinishComposite(length, validity.IsEmpty ? 0 : Validity.CountNulls(validity, length)).View;
        }

        if (ColumnKinds.IsVariableLength(_kind))
        {
            return FinishVarLen().View;
        }

        _ = RawValues(length);
        var bits = MutableValidity(length);
        return Finish(length, bits.IsEmpty ? 0 : Validity.CountNulls(bits, length)).View;
    }

    /// <summary>
    /// Spreads row 0 of <paramref name="one"/> over <paramref name="length"/> rows of this scratch —
    /// the STABLE broadcast (D79), field by field for a COMPOSITE (D291). The row's validity is the
    /// broadcast's, narrowed to <paramref name="selection"/> at the top level only: a field's own
    /// validity is its own.
    /// </summary>
    public Vector BroadcastRow(in ColumnView one, int length, ReadOnlySpan<byte> selection)
    {
        var view = BroadcastInto(one, length, selection);
        return Vector.Transient(view, length);
    }

    private ColumnView BroadcastInto(in ColumnView one, int length, ReadOnlySpan<byte> selection)
    {
        var valid = one.IsValid(0);
        if (_fields is not null)
        {
            for (var i = 0; i < _fields.Length; i++)
            {
                _fields[i].BroadcastInto(one.FieldView(i), length, default);
            }
        }
        else if (ColumnKinds.IsVariableLength(_kind))
        {
            BeginVarLen(length, nullable: !valid || !selection.IsEmpty);
            var bytes = valid ? one.VarValue(0) : default;
            for (var row = 0; row < length; row++)
            {
                if (valid && (selection.IsEmpty || BitUtility.GetBit(selection, row)))
                {
                    AppendValue(bytes);
                }
                else
                {
                    AppendNull();
                }
            }

            return FinishVarLen().View;
        }
        else
        {
            var lanes = RawValues(length);
            var source = one.RawLanes(_width);
            for (var row = 0; row < length; row++)
            {
                source[.._width].CopyTo(lanes[(row * _width)..]);
            }
        }

        var bits = BeginValidity(length);
        if (valid)
        {
            Validity.SetAll(bits, length);
        }

        if (!selection.IsEmpty)
        {
            for (var b = 0; b < Validity.ByteCount(length); b++)
            {
                bits[b] &= selection[b];
            }
        }

        var nulls = Validity.CountNulls(bits, length);
        return _fields is not null
            ? FinishComposite(length, nulls).View
            : Finish(length, nulls).View;
    }

    /// <summary>The public writer over this scratch, for a Tier 2 kernel (D79).</summary>
    public ColumnWriter Writer => _writer ??= ColumnWriters.Over(this);

    private ColumnWriter? _writer;

    ColumnWriter IColumnSink.Child(int index) => Field(index).Writer;

    void IColumnSink.SetValid(int row) => BitUtility.SetBit(_validity.Bytes.AsSpan(), row);

    void IColumnSink.BeginVarLength(int length, bool nullable) => BeginVarLen(length, nullable);

    /// <summary>Starts a variable-length result of <paramref name="length"/> rows.</summary>
    public void BeginVarLen(int length, bool nullable)
    {
        _offsets.Ensure((length + 1) * sizeof(int));
        OffsetLanes[0] = 0;
        _varCursor = 0;
        _varUsed = 0;
        _varNulls = 0;
        if (nullable)
        {
            BeginValidity(length);
        }
        else
        {
            NoValidity();
        }
    }

    /// <summary>Appends one value to a variable-length result.</summary>
    public void AppendValue(ReadOnlySpan<byte> bytes)
    {
        _data.EnsureKeeping(_varUsed + bytes.Length, _varUsed);
        bytes.CopyTo(_data.Bytes.AsSpan(_varUsed));
        _varUsed += bytes.Length;
        OffsetLanes[++_varCursor] = _varUsed;
        if (_useValidity)
        {
            BitUtility.SetBit(_validity.Bytes.AsSpan(), _varCursor - 1);
        }
    }

    /// <summary>Appends a NULL to a variable-length result. The bitmap bit stays clear.</summary>
    public void AppendNull()
    {
        OffsetLanes[++_varCursor] = _varUsed;
        _varNulls++;
    }

    /// <summary>Wraps the variable-length scratch as a column view.</summary>
    public Vector FinishVarLen()
    {
        var length = _varCursor;
        return Vector.Transient(
            new ColumnView
            {
                Type = Type,
                Length = length,
                Values = _data.Bytes.AsMemory(0, _varUsed),
                Offsets = _offsets.Bytes.AsMemory(0, (length + 1) * sizeof(int)),
                Validity = _useValidity
                    ? _validity.Bytes.AsMemory(0, Validity.ByteCount(Math.Max(length, 1)))
                    : default,
                NullCount = _varNulls,
            },
            length);
    }

    private Span<int> OffsetLanes => MemoryMarshal.Cast<byte, int>(_offsets.Bytes.AsSpan());
}
