using System.Runtime.InteropServices;
using System.Text;
using Chalk.Catalog;
using Chalk.Execution.Numeric;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution.Aggregation;

/// <summary>
/// What every holistic aggregate shares (D57, <c>14-windows-ii.md</c> §3): the group's values, kept
/// whole because the answer depends on all of them, and the scratch the emit pass sorts through.
/// </summary>
/// <remarks>
/// NULL values are ignored by all of them and an empty or all-NULL group is NULL, which is SQL's rule
/// for every aggregate but <c>COUNT</c>.
/// </remarks>
internal abstract class HolisticAccumulator : MeasureAccumulator
{
    private int[] _scratch = [];

    protected HolisticAccumulator(
        ChalkType resultType, ChalkType valueType, ChalkType? keyType, bool descending)
        : base(resultType)
    {
        ValueType = valueType;
        ValueKind = ColumnKinds.Of(valueType);
        KeyKind = keyType is { } key ? ColumnKinds.Of(key) : ValueKind;
        Descending = descending;
        Store = new GroupedValueStore(ValueKind, KeyKind);
    }

    /// <summary>Whether the operator has to read an order key for this aggregate.</summary>
    public override bool NeedsOrderKey => HasOrderKey;

    protected bool HasOrderKey { get; init; }

    protected ChalkType ValueType { get; }

    protected ColumnKind ValueKind { get; }

    protected ColumnKind KeyKind { get; }

    protected bool Descending { get; }

    protected GroupedValueStore Store { get; }

    public override void EnsureCapacity(int groups) => Store.EnsureGroups(groups);

    protected override void BeginCore(ExecutionArena arena) => Store.Begin(arena);

    protected override void ReleaseCore()
    {
        Store.Release();
        Give(ref _scratch);
    }

    public override void Add(int group, ReadOnlySpan<byte> lane, bool valid) =>
        Store.Append(group, lane, valid, default, keyValid: false);

    public override void AddOrdered(
        int group, ReadOnlySpan<byte> lane, bool valid, ReadOnlySpan<byte> key, bool keyValid) =>
        Store.Append(group, lane, valid, key, keyValid);

    /// <summary>The group's entries, in the aggregate's order, NULLs kept.</summary>
    protected int Collect(int group, out int[] entries)
    {
        var count = Store.Count(group);
        if (_scratch.Length < Math.Max(count, 1))
        {
            _scratch = Grow(_scratch, Math.Max(count, 1));
        }

        entries = _scratch;
        var collected = Store.Collect(group, entries);
        if (HasOrderKey)
        {
            Store.SortByKey(entries, collected, Descending);
        }

        return collected;
    }

    /// <summary>
    /// The group's entries, in the aggregate's order and with the NULL values dropped. Returns how
    /// many there are; zero means the result is NULL.
    /// </summary>
    protected int Ordered(int group, out int[] entries)
    {
        var collected = Collect(group, out entries);

        // NULLs are ignored by every holistic aggregate, and they are dropped after the sort so the
        // surviving order is the one the query asked for.
        var kept = 0;
        for (var i = 0; i < collected; i++)
        {
            if (Store.IsValid(entries[i]))
            {
                entries[kept++] = entries[i];
            }
        }

        return kept;
    }
}

/// <summary>
/// <c>PERCENTILE_CONT</c> and <c>PERCENTILE_DISC</c> (D57). The value is the {@code WITHIN GROUP}
/// ordering's expression — Calcite puts the fraction in the argument list and the value in the
/// collation — so the group's values are sorted by themselves and the fraction picks one.
/// </summary>
/// <remarks>
/// <c>CONT</c> interpolates linearly between the two neighbours of {@code (n - 1) × fraction} and is
/// always FP64; <c>DISC</c> takes the first value whose cumulative distribution reaches the fraction,
/// which is index {@code ceil(n × fraction) - 1}, and keeps the value's own type.
/// </remarks>
internal sealed class PercentileAccumulator : HolisticAccumulator
{
    private readonly double _fraction;
    private readonly bool _continuous;

    public PercentileAccumulator(
        ChalkType resultType, ChalkType valueType, double fraction, bool continuous, bool descending)
        : base(resultType, valueType, keyType: null, descending)
    {
        _fraction = fraction;
        _continuous = continuous;
        if (continuous && !IrTypes.IsNumeric(valueType.Kind))
        {
            throw new UnsupportedFeatureException(
                $"PERCENTILE_CONT over {valueType}",
                "It interpolates between two values, so it is defined for the numeric kinds "
                + "(docs/design/14-windows-ii.md §3).");
        }
    }

    public override void Emit(ColumnCopier copier, int group)
    {
        var count = Ordered(group, out var entries);
        if (count == 0)
        {
            copier.AppendNulls(1);
            return;
        }

        // Sorted by the value itself: for a percentile the WITHIN GROUP expression *is* the value.
        Store.SortByValue(entries, count);
        if (Descending)
        {
            System.Array.Reverse(entries, 0, count);
        }

        if (!_continuous)
        {
            var index = (int)Math.Ceiling(count * _fraction) - 1;
            copier.AppendRaw(Store.Value(entries[Math.Clamp(index, 0, count - 1)]), valid: true);
            return;
        }

        var position = (count - 1) * _fraction;
        var below = (int)Math.Floor(position);
        var above = Math.Min(below + 1, count - 1);
        var weight = position - below;
        var value = (Number(entries[below]) * (1 - weight)) + (Number(entries[above]) * weight);

        Span<byte> lane = stackalloc byte[ResultWidth];
        MemoryMarshal.Write(lane, value);
        copier.AppendRaw(lane, valid: true);
    }

    private double Number(int entry)
    {
        var lane = Store.Value(entry);
        return ValueKind switch
        {
            ColumnKind.Int8 => (sbyte)lane[0],
            ColumnKind.Int16 => MemoryMarshal.Read<short>(lane),
            ColumnKind.Int32 => MemoryMarshal.Read<int>(lane),
            ColumnKind.Int64 => MemoryMarshal.Read<long>(lane),
            ColumnKind.Float => MemoryMarshal.Read<float>(lane),
            ColumnKind.Double => MemoryMarshal.Read<double>(lane),
            _ => (double)Decimals.Read(lane, ValueType.Scale),
        };
    }
}

/// <summary>
/// <c>LISTAGG</c> — and PostgreSQL's <c>STRING_AGG</c>, which Calcite maps onto it — concatenating
/// the group's strings in the {@code WITHIN GROUP} order with a separator between them (D57).
/// </summary>
internal sealed class ListAggAccumulator : HolisticAccumulator
{
    private readonly byte[] _separator;
    private byte[] _buffer = [];

    public ListAggAccumulator(
        ChalkType resultType, ChalkType valueType, ChalkType? keyType, string separator, bool descending)
        : base(resultType, valueType, keyType, descending)
    {
        HasOrderKey = keyType is not null;
        _separator = Encoding.UTF8.GetBytes(separator);
        if (ValueKind != ColumnKind.Utf8)
        {
            throw new UnsupportedFeatureException(
                $"LISTAGG over {valueType}",
                "It concatenates strings (docs/design/14-windows-ii.md §3).");
        }
    }

    protected override void ReleaseCore()
    {
        base.ReleaseCore();
        Give(ref _buffer);
    }

    public override void Emit(ColumnCopier copier, int group)
    {
        var count = Ordered(group, out var entries);
        if (count == 0)
        {
            copier.AppendNulls(1);
            return;
        }

        var used = 0;
        for (var i = 0; i < count; i++)
        {
            var value = Store.Value(entries[i]);
            var needed = used + value.Length + (i == 0 ? 0 : _separator.Length);
            if (_buffer.Length < needed)
            {
                _buffer = Grow(_buffer, needed);
            }

            if (i > 0)
            {
                _separator.CopyTo(_buffer.AsSpan(used));
                used += _separator.Length;
            }

            value.CopyTo(_buffer.AsSpan(used));
            used += value.Length;
        }

        copier.AppendRaw(_buffer.AsSpan(0, used), valid: true);
    }
}

/// <summary>
/// <c>ARRAY_AGG</c> (D57): the group's values as a <c>LIST</c>, in the {@code ORDER BY} order.
/// </summary>
/// <remarks>
/// Unlike its siblings this one keeps NULL <em>elements</em> — PostgreSQL's rule, and the only way a
/// list can record that a row had no value — but an empty or all-NULL group is still NULL rather than
/// an empty list, which is also PostgreSQL's.
/// </remarks>
internal sealed class ArrayAggAccumulator : HolisticAccumulator
{
    public ArrayAggAccumulator(
        ChalkType resultType, ChalkType valueType, ChalkType? keyType, bool descending)
        : base(resultType, valueType, keyType, descending) => HasOrderKey = keyType is not null;

    public override void Emit(ColumnCopier copier, int group)
    {
        // Ordered() drops the NULLs, which ARRAY_AGG keeps, so the ordering is done here instead.
        var collected = Collect(group, out var entries);
        if (collected == 0)
        {
            copier.AppendNulls(1);
            return;
        }

        var anyValue = false;
        for (var i = 0; i < collected; i++)
        {
            anyValue |= Store.IsValid(entries[i]);
        }

        if (!anyValue)
        {
            copier.AppendNulls(1);
            return;
        }

        for (var i = 0; i < collected; i++)
        {
            var entry = entries[i];
            if (Store.IsValid(entry))
            {
                copier.Elements.AppendRaw(Store.Value(entry), valid: true);
            }
            else
            {
                copier.Elements.AppendRaw(default, valid: false);
            }

            copier.ElementAppended();
        }

        copier.EndList(valid: true);
    }
}

/// <summary>
/// <c>MODE</c> (D57): the most frequent non-NULL value, ties broken by the smallest value — Calcite
/// leaves the tie unspecified and Chalk pins it, because an unspecified answer is not testable.
/// </summary>
internal sealed class ModeAccumulator : HolisticAccumulator
{
    public ModeAccumulator(ChalkType resultType, ChalkType valueType)
        : base(resultType, valueType, keyType: null, descending: false)
    {
    }

    public override void Emit(ColumnCopier copier, int group)
    {
        var count = Ordered(group, out var entries);
        if (count == 0)
        {
            copier.AppendNulls(1);
            return;
        }

        // Sorted ascending, equal values are adjacent, and the first longest run therefore holds the
        // smallest of the most frequent values — which is the tie rule.
        Store.SortByValue(entries, count);
        var best = 0;
        var bestRun = 0;
        var i = 0;
        while (i < count)
        {
            var j = i + 1;
            while (j < count
                && LaneComparer.Compare(ValueKind, Store.Value(entries[i]), Store.Value(entries[j])) == 0)
            {
                j++;
            }

            if (j - i > bestRun)
            {
                bestRun = j - i;
                best = i;
            }

            i = j;
        }

        copier.AppendRaw(Store.Value(entries[best]), valid: true);
    }
}
