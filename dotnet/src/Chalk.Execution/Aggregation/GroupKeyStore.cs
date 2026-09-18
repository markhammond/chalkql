using Chalk.Execution.Numeric;
using Chalk.Execution.Vectors;

namespace Chalk.Execution.Aggregation;

/// <summary>
/// One grouping key column's values, kept per group id in a growable typed store (§6.6). NULL keys
/// group together — <c>02-ir.md</c> §4 — so a null hashes to a fixed salt and equals another null.
/// </summary>
/// <remarks>
/// The buffers scale with the number of groups, which is data and not plan shape, so they are rented
/// from the execution's arena between <see cref="Begin"/> and <see cref="Release"/> and never outlive
/// the run (ADR 0012).
/// </remarks>
internal abstract class GroupKeyStore : ArenaScratch
{
    protected GroupKeyStore(ColumnKind kind)
    {
        Kind = kind;
        Width = ColumnKinds.Width(kind);
    }

    protected ColumnKind Kind { get; }

    protected int Width { get; }

    public static GroupKeyStore Create(ColumnKind kind) => ColumnKinds.IsVariableLength(kind)
        ? new VariableKeyStore(kind)
        : new FixedKeyStore(kind);

    /// <summary>
    /// Makes room for <paramref name="groups"/> groups before the first one arrives, from the plan's
    /// estimate (D258.3). Only what the store can size from a group count: a variable-width key's
    /// payload depends on the keys themselves and still grows by doubling.
    /// </summary>
    public abstract void Reserve(int groups);

    /// <summary>
    /// The largest reservation a hint may ask for, in elements. Past this the store's own doubling
    /// would not fit in an <see cref="int"/>, and a plan estimate is not worth an overflow.
    /// </summary>
    protected const int MaximumReservation = int.MaxValue / 32;

    /// <summary>Records the key of a newly created group.</summary>
    public abstract void Append(ReadOnlySpan<byte> lane, bool valid);

    /// <summary>Whether the stored key of <paramref name="group"/> is the same value.</summary>
    public abstract bool Matches(int group, ReadOnlySpan<byte> lane, bool valid);

    /// <summary>Writes the stored key of <paramref name="group"/> to an output column.</summary>
    public abstract void Emit(ColumnCopier copier, int group);

    /// <summary>
    /// The stored key of <paramref name="group"/> as bytes. Read once per group by the perfect
    /// hash's search (D255) and never on the row path; a NULL group's bytes are not a key and the
    /// search skips it.
    /// </summary>
    public abstract ReadOnlySpan<byte> Key(int group);

    /// <summary>
    /// A lane's hash for a key set that has no image of its own — the set operators' distinct store.
    /// The hash aggregate hashes a <see cref="KeyImage"/> instead (D255).
    /// </summary>
    public static ulong Hash(ReadOnlySpan<byte> lane, bool valid) =>
        valid ? Hashing.Bytes(lane) : Hashing.NullSalt;
}

/// <summary>A key of fixed width: every kind but STRING and BINARY.</summary>
internal sealed class FixedKeyStore : GroupKeyStore
{
    private byte[] _values = [];
    private byte[] _valid = [];
    private int _count;

    public FixedKeyStore(ColumnKind kind)
        : base(kind)
    {
    }

    public override void Reserve(int groups)
    {
        if (groups is <= 0 or > MaximumReservation)
        {
            return;
        }

        var required = groups * Width;
        if (_values.Length < required)
        {
            _values = Grow(_values, required);
            _valid = Grow(_valid, _values.Length / Width);
        }
    }

    public override void Append(ReadOnlySpan<byte> lane, bool valid)
    {
        var required = (_count + 1) * Width;
        if (_values.Length < required)
        {
            _values = Grow(_values, required);
            _valid = Grow(_valid, _values.Length / Width);
        }

        var slot = _values.AsSpan(_count * Width, Width);
        slot.Clear();
        if (valid)
        {
            lane[..Width].CopyTo(slot);
        }

        _valid[_count] = (byte)(valid ? 1 : 0);
        _count++;
    }

    public override bool Matches(int group, ReadOnlySpan<byte> lane, bool valid)
    {
        if ((_valid[group] != 0) != valid)
        {
            return false;
        }

        return !valid || _values.AsSpan(group * Width, Width).SequenceEqual(lane[..Width]);
    }

    public override void Emit(ColumnCopier copier, int group) =>
        copier.AppendRaw(_values.AsSpan(group * Width, Width), _valid[group] != 0);

    public override ReadOnlySpan<byte> Key(int group) => _values.AsSpan(group * Width, Width);

    protected override void ReleaseCore()
    {
        Give(ref _values);
        Give(ref _valid);
        _count = 0;
    }
}

/// <summary>A key of variable width: STRING and BINARY, packed into one growing data buffer.</summary>
internal sealed class VariableKeyStore : GroupKeyStore
{
    private byte[] _data = [];
    private int[] _offsets = [];
    private byte[] _valid = [];
    private int _count;
    private int _used;

    public VariableKeyStore(ColumnKind kind)
        : base(kind)
    {
    }

    public override void Reserve(int groups)
    {
        if (groups is <= 0 or > MaximumReservation)
        {
            return;
        }

        // The offsets and the validity bytes are a group each; the payload is not, so it is left to
        // grow as it did (D258.3).
        if (_offsets.Length < groups + 2)
        {
            _offsets = Grow(_offsets, groups + 2);
            _valid = Grow(_valid, _offsets.Length);
        }
    }

    public override void Append(ReadOnlySpan<byte> lane, bool valid)
    {
        if (_offsets.Length < _count + 2)
        {
            // A grown rental is zeroed past what was kept, so offsets[0] is 0 on the first append.
            _offsets = Grow(_offsets, _count + 2);
            _valid = Grow(_valid, _offsets.Length);
        }

        if (valid && lane.Length > 0)
        {
            if (_data.Length < _used + lane.Length)
            {
                _data = Grow(_data, _used + lane.Length);
            }

            lane.CopyTo(_data.AsSpan(_used));
            _used += lane.Length;
        }

        _valid[_count] = (byte)(valid ? 1 : 0);
        _offsets[++_count] = _used;
    }

    public override bool Matches(int group, ReadOnlySpan<byte> lane, bool valid)
    {
        if ((_valid[group] != 0) != valid)
        {
            return false;
        }

        return !valid || Stored(group).SequenceEqual(lane);
    }

    public override void Emit(ColumnCopier copier, int group) =>
        copier.AppendRaw(Stored(group), _valid[group] != 0);

    public override ReadOnlySpan<byte> Key(int group) => Stored(group);

    protected override void ReleaseCore()
    {
        Give(ref _data);
        Give(ref _offsets);
        Give(ref _valid);
        _count = 0;
        _used = 0;
    }

    private ReadOnlySpan<byte> Stored(int group) =>
        _data.AsSpan(_offsets[group], _offsets[group + 1] - _offsets[group]);
}
