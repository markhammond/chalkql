using Chalk.Catalog;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution.Reference;

/// <summary>
/// The reference implementations of the row-expanding nodes of steps 19 and 20 (D55, D66, D69): a
/// hop, a session, an unnest and the set operations, each written literally from the IR's own words
/// and sharing nothing with the vectorised operators (I4).
/// </summary>
internal static class ReferenceExpansions
{
    /// <summary>
    /// The set operations as multiset arithmetic over lists, which is what SQL says they are (D69,
    /// <c>15-zero-allocation-execution.md</c> §6). NULLs compare equal here — they are values in a
    /// row, not a predicate — and the <c>ALL</c> forms keep multiplicities: <c>INTERSECT ALL</c>
    /// emits the smallest count across the inputs, <c>EXCEPT ALL</c> the first input's count less
    /// the others'. Output order is arrival order, which is the operators' too; nothing about a set
    /// operation's result is ordered, so a corpus query that cares sorts.
    /// </summary>
    public static List<object?[]> SetOp(SetOpKind kind, IReadOnlyList<List<object?[]>> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (kind == SetOpKind.UnionAll)
        {
            var all = new List<object?[]>();
            foreach (var input in inputs)
            {
                all.AddRange(input);
            }

            return all;
        }

        if (kind == SetOpKind.UnionDistinct)
        {
            var seen = new HashSet<RowKey>();
            var union = new List<object?[]>();
            foreach (var input in inputs)
            {
                foreach (var row in input)
                {
                    if (seen.Add(new RowKey(row)))
                    {
                        union.Add(row);
                    }
                }
            }

            return union;
        }

        // INTERSECT and EXCEPT: count the first input's rows, then let each further input reduce
        // those counts — to the minimum for an intersection, by subtraction for a difference.
        var counts = new Dictionary<RowKey, int>();
        var order = new List<object?[]>();
        foreach (var row in inputs[0])
        {
            var key = new RowKey(row);
            if (!counts.TryGetValue(key, out var already))
            {
                order.Add(row);
                already = 0;
            }

            counts[key] = already + 1;
        }

        for (var i = 1; i < inputs.Count; i++)
        {
            var other = new Dictionary<RowKey, int>();
            foreach (var row in inputs[i])
            {
                var key = new RowKey(row);
                other[key] = other.TryGetValue(key, out var found) ? found + 1 : 1;
            }

            foreach (var key in counts.Keys.ToList())
            {
                var mine = counts[key];
                var theirs = other.TryGetValue(key, out var found) ? found : 0;
                counts[key] = kind switch
                {
                    SetOpKind.IntersectAll or SetOpKind.IntersectDistinct => Math.Min(mine, theirs),
                    SetOpKind.ExceptAll => mine - theirs,
                    _ => theirs > 0 ? 0 : mine,
                };
            }
        }

        var distinct = kind is SetOpKind.IntersectDistinct or SetOpKind.ExceptDistinct;
        var result = new List<object?[]>();
        foreach (var row in order)
        {
            var remaining = counts[new RowKey(row)];
            if (remaining <= 0)
            {
                continue;
            }

            for (var n = 0; n < (distinct ? 1 : remaining); n++)
            {
                result.Add(row);
            }
        }

        return result;
    }

    /// <summary>One row as a dictionary key, with NULLs equal to each other and to nothing else.</summary>
    private sealed class RowKey : IEquatable<RowKey>
    {
        private readonly object?[] _values;
        private readonly int _hash;

        public RowKey(object?[] values)
        {
            _values = values;
            var hash = new HashCode();
            foreach (var value in values)
            {
                hash.Add(Normalise(value));
            }

            _hash = hash.ToHashCode();
        }

        public bool Equals(RowKey? other)
        {
            if (other is null || other._values.Length != _values.Length)
            {
                return false;
            }

            for (var i = 0; i < _values.Length; i++)
            {
                if (!Equals(Normalise(_values[i]), Normalise(other._values[i])))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as RowKey);

        public override int GetHashCode() => _hash;

        /// <summary>
        /// Grouping compares floating point by value: every NaN is one value and -0.0 is 0.0, which
        /// is what the hash aggregate does too, so the two engines cannot disagree here.
        /// </summary>
        private static object? Normalise(object? value) => value switch
        {
            double d when double.IsNaN(d) => double.NaN,
            double d when d == 0d => 0d,
            float f when float.IsNaN(f) => float.NaN,
            float f when f == 0f => 0f,
            byte[] bytes => Convert.ToHexStringLower(bytes),
            _ => value,
        };
    }

    /// <summary>
    /// <c>HOP</c>: every window whose start is a multiple of the slide from the epoch and which
    /// contains the row's time, enumerated by counting down from that time and reported upwards.
    /// A NULL time, and a time that falls in no window, produce no rows.
    /// </summary>
    public static List<object?[]> Hop(
        Hop hop, IReadOnlyList<object?[]> input, ReferenceInterpreter interpreter)
    {
        var timeType = ChalkType.FromProto(hop.Input.RowType.Fields[(int)hop.TimeColumn].Type);
        var slide = Units(interpreter, hop.Slide, timeType, "HOP's slide");
        var size = Units(interpreter, hop.Size, timeType, "HOP's size");
        var column = (int)hop.TimeColumn;

        var rows = new List<object?[]>();
        foreach (var row in input)
        {
            if (row[column] is not long time)
            {
                continue;
            }

            // Written out rather than shared with WindowBoundsMath: an oracle that borrows the
            // engine's arithmetic cannot disagree with it (I4).
            var floor = time - Modulo(time, slide);
            var starts = new List<long>();
            for (var start = floor; start > time - size; start -= slide)
            {
                starts.Add(start);
            }

            starts.Reverse();
            foreach (var start in starts)
            {
                var output = new object?[row.Length + 2];
                row.CopyTo(output, 0);
                output[row.Length] = start;
                output[row.Length + 1] = start + size;
                rows.Add(output);
            }
        }

        return rows;
    }

    /// <summary>
    /// <c>SESSION</c>: rows grouped by their partition keys, then split wherever the gap from the
    /// previous time reaches <c>gap</c>. A NULL time belongs to no session.
    /// </summary>
    public static List<object?[]> Session(
        Session session, IReadOnlyList<object?[]> input, ReferenceInterpreter interpreter)
    {
        var timeType = ChalkType.FromProto(session.Input.RowType.Fields[(int)session.TimeColumn].Type);
        var gap = Units(interpreter, session.Gap, timeType, "SESSION's gap");
        var column = (int)session.TimeColumn;
        var keys = session.PartitionKeys.Select(k => (int)k).ToArray();

        // Grouped and sorted here rather than trusted from the input: the oracle must not depend on
        // the ordering the plan promises, because that promise is one of the things under test.
        var groups = new Dictionary<string, List<object?[]>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var row in input)
        {
            var key = string.Join(
                "", keys.Select(k => row[k]?.ToString() ?? " "));
            if (!groups.TryGetValue(key, out var group))
            {
                group = [];
                groups[key] = group;
                order.Add(key);
            }

            group.Add(row);
        }

        var rows = new List<object?[]>();
        foreach (var key in order)
        {
            var group = groups[key];
            var timed = group.Where(r => r[column] is long).OrderBy(r => (long)r[column]!).ToList();
            var untimed = group.Where(r => r[column] is not long).ToList();

            var i = 0;
            while (i < timed.Count)
            {
                var first = (long)timed[i][column]!;
                var last = first;
                var j = i;
                while (j + 1 < timed.Count && (long)timed[j + 1][column]! < last + gap)
                {
                    j++;
                    last = (long)timed[j][column]!;
                }

                for (var k = i; k <= j; k++)
                {
                    rows.Add(Extend(timed[k], first, last + gap));
                }

                i = j + 1;
            }

            foreach (var row in untimed)
            {
                rows.Add(Extend(row, null, null));
            }
        }

        return rows;
    }

    /// <summary>
    /// <c>UNNEST</c>: one row per element, in list order, with the element and — when asked — its
    /// 1-based position. <c>keep_empty</c> turns an empty or NULL list into one NULL-padded row
    /// instead of none.
    /// </summary>
    public static List<object?[]> Unnest(Unnest unnest, IReadOnlyList<object?[]> input)
    {
        var column = (int)unnest.ListColumn;
        var extra = unnest.WithOrdinality ? 2 : 1;
        var rows = new List<object?[]>();
        foreach (var row in input)
        {
            var list = row[column] as object?[];
            if (list is null || list.Length == 0)
            {
                if (unnest.KeepEmpty)
                {
                    rows.Add(Widen(row, extra));
                }

                continue;
            }

            for (var i = 0; i < list.Length; i++)
            {
                var output = Widen(row, extra);
                output[row.Length] = list[i];
                if (unnest.WithOrdinality)
                {
                    output[row.Length + 1] = (long)(i + 1);
                }

                rows.Add(output);
            }
        }

        return rows;
    }

    private static object?[] Extend(object?[] row, object? start, object? end)
    {
        var output = new object?[row.Length + 2];
        row.CopyTo(output, 0);
        output[row.Length] = start;
        output[row.Length + 1] = end;
        return output;
    }

    private static object?[] Widen(object?[] row, int extra)
    {
        var output = new object?[row.Length + extra];
        row.CopyTo(output, 0);
        return output;
    }

    /// <summary>The interval, in the units the time column counts.</summary>
    private static long Units(
        ReferenceInterpreter interpreter, Expr expr, ChalkType time, string what)
    {
        if (interpreter.Evaluate(expr, []) is not long micros || micros <= 0)
        {
            throw new UnsupportedFeatureException(
                what, "A window's slide, size and gap are positive interval constants.");
        }

        if (time.Kind == TypeKind.Date)
        {
            return micros / 86_400_000_000L;
        }

        var perSecond = IrTypes.TimestampUnitsPerSecond((uint)time.Precision);
        return perSecond >= 1_000_000L
            ? micros * (perSecond / 1_000_000L)
            : micros / (1_000_000L / perSecond);
    }

    /// <summary>A modulo that floors, so a value before the epoch lands on the boundary below it.</summary>
    private static long Modulo(long value, long divisor)
    {
        var remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }
}
