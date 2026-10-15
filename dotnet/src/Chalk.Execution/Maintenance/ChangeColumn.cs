using System.Buffers.Binary;
using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution.Maintenance;

/// <summary>
/// One column of the rows a maintained operator writes in an update (D343,
/// <c>docs/design/68-incremental-maintenance.md</c> §1), in buffers it owns and reuses from one update
/// to the next, published as a <see cref="ColumnView"/> in the layout the executor's kernels read.
/// </summary>
/// <remarks>
/// The buffers are managed rather than the execution's arena: what a maintained operator writes in one
/// update is read by the operators above it in the same update, and its stores outlive every
/// execution. STRING and BINARY are written with offsets, booleans a byte per row, as inside the
/// pipeline. A LIST or a composite is refused when the operator is built (§8).
/// </remarks>
internal sealed class ChangeColumn
{
    private readonly ColumnKind _kind;
    private readonly int _width;
    private readonly bool _variable;
    private byte[] _values = [];
    private byte[] _offsets = new byte[4];
    private byte[] _validity = [];
    private int _count;
    private int _nulls;
    private int _data;

    /// <summary>Whether the rows since <see cref="Clear"/> have a bitmap: only once one is NULL.</summary>
    private bool _bitmap;

    public ChangeColumn(ChalkType type)
    {
        Type = type;
        _kind = Kinds.Of(type, "a maintained operator's column");
        _width = ColumnKinds.Width(_kind);
        _variable = ColumnKinds.IsVariableLength(_kind);
    }

    public ChalkType Type { get; }

    /// <summary>Rows written since <see cref="Clear"/>.</summary>
    public int Count => _count;

    /// <summary>Bytes the column holds on to.</summary>
    public long Bytes => _values.Length + _offsets.Length + _validity.Length;

    /// <summary>Forgets the rows, keeping the buffers for the next update.</summary>
    public void Clear()
    {
        _count = 0;
        _nulls = 0;
        _data = 0;
        _bitmap = false;
    }

    /// <summary>Forgets the rows and lets the buffers go, as an update far larger than the next grew them.</summary>
    public void Trim()
    {
        Clear();
        _values = [];
        _offsets = new byte[4];
        _validity = [];
    }

    /// <summary>Copies row <paramref name="row"/> of <paramref name="source"/>, a column of this type.</summary>
    public void Append(in ColumnView source, int row)
    {
        if (!source.IsValid(row))
        {
            AppendNull();
            return;
        }

        switch (_kind)
        {
            case ColumnKind.Boolean:
                AppendBoolean(source.BoolAt(row));
                return;
            case ColumnKind.Utf8 or ColumnKind.Binary:
                AppendBytes(source.VarValue(row));
                return;
            default:
                AppendLane(source.RawLanes(_width).Slice(row * _width, _width));
                return;
        }
    }

    /// <summary>Copies slot <paramref name="slot"/> of <paramref name="source"/>, a stored column of this type.</summary>
    public void Append(StoredColumn source, int slot)
    {
        if (!source.IsValid(slot))
        {
            AppendNull();
            return;
        }

        if (_variable)
        {
            AppendBytes(source.Bytes(slot));
        }
        else
        {
            AppendLane(source.Lane(slot));
        }
    }

    /// <summary>A fixed-width value, as its lane's bytes.</summary>
    public void AppendLane(ReadOnlySpan<byte> lane)
    {
        Reserve(_width);
        lane[.._width].CopyTo(_values.AsSpan(_count * _width));
        Valid();
    }

    public void AppendBoolean(bool value)
    {
        Reserve(1);
        _values[_count] = (byte)(value ? 1 : 0);
        Valid();
    }

    public void AppendInt64(long value)
    {
        Reserve(8);
        BinaryPrimitives.WriteInt64LittleEndian(_values.AsSpan(_count * 8), value);
        Valid();
    }

    /// <summary>A STRING or BINARY value's bytes.</summary>
    public void AppendBytes(ReadOnlySpan<byte> value)
    {
        Reserve(value.Length);
        value.CopyTo(_values.AsSpan(_data));
        _data += value.Length;
        Valid();
    }

    public void AppendNull()
    {
        Reserve(0);
        if (!_variable)
        {
            _values.AsSpan(_count * _width, _width).Clear();
        }

        if (!_bitmap)
        {
            // The first NULL since the column was cleared: every row before it was valid.
            EnsureValidity();
            Validity.SetAll(_validity, _count);
            _bitmap = true;
        }

        EnsureValidity();
        BitUtility.ClearBit(_validity, _count);
        _nulls++;
        Advance();
    }

    /// <summary>The rows written, as a view valid until the next <see cref="Clear"/> or append.</summary>
    public ColumnView View() => new()
    {
        Type = Type,
        Length = _count,
        Values = _variable ? _values.AsMemory(0, _data) : _values.AsMemory(0, _count * _width),
        Validity = _bitmap ? _validity.AsMemory(0, Validity.ByteCount(_count)) : default,
        Offsets = _variable ? _offsets.AsMemory(0, (_count + 1) * 4) : default,
        NullCount = _nulls,
    };

    private void Valid()
    {
        if (_bitmap)
        {
            EnsureValidity();
            BitUtility.SetBit(_validity, _count);
        }

        Advance();
    }

    private void Advance()
    {
        _count++;
        if (_variable)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_offsets.AsSpan(_count * 4), _data);
        }
    }

    /// <summary>
    /// Room for one more row of <paramref name="bytes"/> value bytes. The validity bitmap is only
    /// materialised once a NULL arrives; until then every bit before it is valid by construction.
    /// </summary>
    private void Reserve(int bytes)
    {
        if (_variable)
        {
            Grow(ref _values, _data + bytes);
            Grow(ref _offsets, (_count + 2) * 4);
        }
        else
        {
            Grow(ref _values, (_count + 1) * _width);
        }
    }

    private void EnsureValidity()
    {
        var needed = Validity.ByteCount(_count + 1);
        if (_validity.Length < needed)
        {
            var grown = new byte[Math.Max(needed, Math.Max(8, _validity.Length * 2))];
            _validity.AsSpan().CopyTo(grown);
            _validity = grown;
        }
    }

    private static void Grow(ref byte[] buffer, int needed)
    {
        if (buffer.Length >= needed)
        {
            return;
        }

        var grown = new byte[Math.Max(needed, Math.Max(64, buffer.Length * 2))];
        buffer.AsSpan().CopyTo(grown);
        buffer = grown;
    }
}

/// <summary>The layouts maintained operators keep, and the refusal of the ones they do not (§8).</summary>
internal static class Kinds
{
    /// <summary>The layout of <paramref name="type"/>, refusing a LIST, a composite and BINARY in what is kept.</summary>
    public static ColumnKind Of(ChalkType type, string what)
    {
        var kind = ColumnKinds.Of(type);
        if (kind is ColumnKind.List or ColumnKind.Composite or ColumnKind.Binary)
        {
            throw new NotMaintainedException(
                $"{what} is {IrTypes.Describe(type.ToProto())}, and a maintained view keeps no LIST, "
                + "composite or BINARY value.");
        }

        return kind;
    }

    /// <summary>Whether a maintained operator can keep a value of <paramref name="type"/>.</summary>
    public static bool Kept(ChalkType type) =>
        ColumnKinds.Of(type) is not (ColumnKind.List or ColumnKind.Composite or ColumnKind.Binary);
}
