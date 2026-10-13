using Chalk.Catalog;
using Chalk.Execution.Aggregation;
using Chalk.Execution.Vectors;
using Chalk.Sources;

namespace Chalk.Execution.Maintenance;

/// <summary>
/// Values encoded as bytes, for the dictionaries maintained operators keep (D343, design 68 §2): a
/// row's identity, and its join or grouping key.
/// </summary>
/// <remarks>
/// <para>
/// Each value is a tag, 0 for NULL and 1 otherwise, then its bytes: a fixed-width lane as the batch
/// holds it, a boolean as 0 or 1, a STRING as its length and its UTF-8 bytes.
/// </para>
/// <para>
/// A key is <em>normalised</em> as the executor's joins and aggregates compare: every NaN is one value
/// and -0.0 is 0.0 (<see cref="LaneAccess.Normalise"/>). An identity is not: a row is the same row only
/// when its bits are, so a −0.0 that becomes 0.0 is a change a view still sends.
/// </para>
/// </remarks>
internal sealed class KeyEncoder
{
    private readonly byte[] _scratch = new byte[16];
    private byte[] _buffer = new byte[64];
    private int _length;

    /// <summary>The bytes encoded since <see cref="Reset"/>.</summary>
    public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, _length);

    public void Reset() => _length = 0;

    /// <summary>Encodes one value of a batch column. False when it is NULL.</summary>
    public bool Add(in ColumnView column, int row, ColumnKind kind, int width, bool normalise)
    {
        if (!column.IsValid(row))
        {
            Put(0);
            return false;
        }

        Put(1);
        switch (kind)
        {
            case ColumnKind.Boolean:
                Put((byte)(column.BoolAt(row) ? 1 : 0));
                break;
            case ColumnKind.Utf8 or ColumnKind.Binary:
                PutBytes(column.VarValue(row));
                break;
            case ColumnKind.Float or ColumnKind.Double when normalise:
                column.RawLanes(width).Slice(row * width, width).CopyTo(_scratch);
                PutLane(LaneAccess.Normalise(kind, _scratch.AsSpan(0, width)));
                break;
            default:
                PutLane(column.RawLanes(width).Slice(row * width, width));
                break;
        }

        return true;
    }

    /// <summary>Encodes one stored value. False when it is NULL.</summary>
    public bool Add(StoredColumn column, int slot, bool normalise)
    {
        if (!column.IsValid(slot))
        {
            Put(0);
            return false;
        }

        Put(1);
        if (column.Variable)
        {
            PutBytes(column.Bytes(slot));
        }
        else if (normalise && column.Kind is ColumnKind.Float or ColumnKind.Double)
        {
            column.Lane(slot).CopyTo(_scratch);
            PutLane(LaneAccess.Normalise(column.Kind, _scratch.AsSpan(0, column.Width)));
        }
        else
        {
            PutLane(column.Lane(slot));
        }

        return true;
    }

    private void Put(byte value)
    {
        Reserve(1);
        _buffer[_length++] = value;
    }

    private void PutLane(ReadOnlySpan<byte> lane)
    {
        Reserve(lane.Length);
        lane.CopyTo(_buffer.AsSpan(_length));
        _length += lane.Length;
    }

    private void PutBytes(ReadOnlySpan<byte> value)
    {
        Reserve(4 + value.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_length), value.Length);
        _length += 4;
        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
    }

    private void Reserve(int bytes)
    {
        if (_length + bytes > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_length + bytes, _buffer.Length * 2));
        }
    }
}

/// <summary>A row's columns as an encoder reads them: kind and width, once.</summary>
internal readonly struct KeyColumns
{
    public KeyColumns(IReadOnlyList<ChalkType> types, IReadOnlyList<int> columns)
    {
        Columns = [.. columns];
        KindsOf = [.. columns.Select(c => Kinds.Of(types[c], "a maintained key column"))];
        Widths = [.. KindsOf.Select(ColumnKinds.Width)];
    }

    public int[] Columns { get; }

    public ColumnKind[] KindsOf { get; }

    public int[] Widths { get; }

    public int Count => Columns.Length;

    /// <summary>Every column of a row of <paramref name="types"/>, in order.</summary>
    public static KeyColumns All(IReadOnlyList<ChalkType> types) =>
        new(types, [.. Enumerable.Range(0, types.Count)]);

    /// <summary>Encodes the columns of a batch row. False when any of them is NULL.</summary>
    public bool Encode(KeyEncoder encoder, ReadOnlySpan<ColumnView> columns, int row, bool normalise)
    {
        encoder.Reset();
        var complete = true;
        for (var i = 0; i < Columns.Length; i++)
        {
            complete &= encoder.Add(columns[Columns[i]], row, KindsOf[i], Widths[i], normalise);
        }

        return complete;
    }

    /// <summary>
    /// Encodes the stored columns <paramref name="stored"/>, each the store's column for the key's
    /// <c>i</c>th column. False when any of them is NULL.
    /// </summary>
    public bool Encode(KeyEncoder encoder, RowStore store, ReadOnlySpan<int> stored, int slot, bool normalise)
    {
        encoder.Reset();
        var complete = true;
        for (var i = 0; i < stored.Length; i++)
        {
            complete &= encoder.Add(store.Column(stored[i]), slot, normalise);
        }

        return complete;
    }
}

/// <summary>
/// Byte arrays compared by content, looked up by a span without allocating: the keys of every
/// dictionary a maintained operator keeps.
/// </summary>
internal sealed class ByteKeys : IEqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
{
    public static readonly ByteKeys Instance = new();

    private ByteKeys()
    {
    }

    public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

    public int GetHashCode(byte[] obj) => Hash(obj);

    public bool Equals(ReadOnlySpan<byte> alternate, byte[] other) => alternate.SequenceEqual(other);

    public int GetHashCode(ReadOnlySpan<byte> alternate) => Hash(alternate);

    public byte[] Create(ReadOnlySpan<byte> alternate) => alternate.ToArray();

    private static int Hash(ReadOnlySpan<byte> bytes)
    {
        var hash = new HashCode();
        hash.AddBytes(bytes);
        return hash.ToHashCode();
    }
}

/// <summary>Lanes ordered as MIN and MAX order them (<c>02-ir.md</c> §3), for a sorted set of values.</summary>
internal sealed class LaneOrder(ColumnKind kind) : IComparer<byte[]>
{
    public int Compare(byte[]? x, byte[]? y) => LaneComparer.Compare(kind, x, y);
}
