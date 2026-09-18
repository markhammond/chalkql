using Chalk.Execution.Operators;

namespace Chalk.Execution.Tests;

/// <summary>
/// The introsort the blocking sort uses where it still compares rows (D267 b): it must order a range
/// the way the framework's own sort does, at every length and every starting arrangement, and it must
/// do it without allocating — which is the reason it exists at all.
/// </summary>
public sealed class RowSortTests
{
    private const int Seed = 20260916;

    public static TheoryData<int> Lengths() => new(0, 1, 2, 3, 16, 17, 64, 257, 1_000, 5_000);

    [Theory]
    [MemberData(nameof(Lengths))]
    public void A_sorted_range_holds_every_value_in_order(int length)
    {
        var random = new Random(Seed);
        foreach (var (what, keys) in Arrangements(random, length))
        {
            var rows = new int[length];
            for (var i = 0; i < length; i++)
            {
                rows[i] = i;
            }

            RowSort.Sort(rows.AsSpan(), new ByKey(keys));

            var sorted = keys.Order().ToArray();
            var actual = rows.Select(row => keys[row]).ToArray();
            Assert.Equal(length, rows.Distinct().Count());
            Assert.True(
                sorted.SequenceEqual(actual),
                $"{what} of {length} (seed {Seed}) did not come back in key order.");
        }
    }

    [Fact]
    public void Sorting_a_range_allocates_nothing()
    {
        var random = new Random(Seed);
        var keys = new int[20_000];
        var rows = new int[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            keys[i] = random.Next(64);
            rows[i] = i;
        }

        // Warm the generic instantiation, so the measurement below is the sort and not the JIT.
        RowSort.Sort(rows.AsSpan(0, 32), new ByKey(keys));

        var before = GC.GetAllocatedBytesForCurrentThread();
        RowSort.Sort(rows.AsSpan(), new ByKey(keys));
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>The arrangements a quicksort pivot is most likely to get wrong.</summary>
    private static IEnumerable<(string What, int[] Keys)> Arrangements(Random random, int length)
    {
        yield return ("ascending", [.. Enumerable.Range(0, length)]);
        yield return ("descending", [.. Enumerable.Range(0, length).Reverse()]);
        yield return ("all equal", [.. Enumerable.Repeat(7, length)]);
        yield return ("two values", [.. Enumerable.Range(0, length).Select(i => i % 2)]);
        yield return ("random", [.. Enumerable.Range(0, length).Select(_ => random.Next(length + 1))]);
        yield return (
            "organ pipe",
            [.. Enumerable.Range(0, length).Select(i => Math.Min(i, length - 1 - i))]);
    }

    private readonly struct ByKey(int[] keys) : IComparer<int>
    {
        public int Compare(int left, int right) => keys[left].CompareTo(keys[right]);
    }
}
