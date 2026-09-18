using Chalk.Arrow;
using Chalk.Client;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A third oracle for the queries where one is cheap (docs/design/05-testing.md §5, queries 07, 10,
/// 11, 12, 18, 21, 24, 27). The differential tests prove the two Chalk engines agree; LINQ over the
/// same fixture catches the case where they agree because they share a misunderstanding of SQL.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class LinqOracleTests(SharedSidecar sidecar)
{
    private static readonly CorpusFixture Fixture = CorpusFixture.Create(barMinutes: 2_880);

    [Fact]
    public async Task Query_07_group_by_symbol()
    {
        var rows = await RunAsync("07_group_by_basic");

        var expected = Fixture.Bars
            .GroupBy(b => b.Symbol)
            .Select(g => new object?[]
            {
                g.Key,
                (long)g.Count(),
                g.Sum(b => b.Volume),
                g.Min(b => b.Low),
                g.Max(b => b.High),
            })
            .OrderBy(r => (Utf8String)r[0]!);

        AssertSameMultiset(expected, rows);
    }

    [Fact]
    public async Task Query_10_distinct_symbol()
    {
        var rows = await RunAsync("10_distinct");

        AssertSameMultiset(
            Fixture.Bars.Select(b => b.Symbol).Distinct().Select(s => new object?[] { s }),
            rows);
    }

    [Fact]
    public async Task Query_11_count_distinct_symbol()
    {
        var rows = await RunAsync("11_count_distinct");

        Assert.Single(rows);
        Assert.Equal((long)Fixture.Bars.Select(b => b.Symbol).Distinct().Count(), rows[0][0]);
    }

    [Fact]
    public async Task Query_12_top_ten_by_close_descending()
    {
        var rows = await RunAsync("12_topn");

        var expected = Fixture.Bars
            .Where(b => b.Volume > 5000)
            .OrderByDescending(b => b.Close)
            .Take(10)
            .Select(b => b.Close)
            .ToArray();

        Assert.Equal(expected.Length, rows.Count);
        // TopN's tie-breaking is unspecified, so compare the ordering key rather than whole rows.
        Assert.Equal(expected, rows.Select(r => (double)r[2]!).ToArray());
    }

    [Fact]
    public async Task Query_18_having()
    {
        var rows = await RunAsync("18_having");

        var expected = Fixture.Bars
            .GroupBy(b => b.Symbol)
            .Select(g => new { g.Key, Vol = g.Sum(b => b.Volume) })
            .Where(g => g.Vol > 1_000_000)
            .Select(g => new object?[] { g.Key, g.Vol });

        AssertSameMultiset(expected, rows);
    }

    [Fact]
    public async Task Query_21_global_aggregate_over_nulls()
    {
        var rows = await RunAsync("21_global_aggregate_nulls");

        Assert.Single(rows);
        Assert.Equal((long)Fixture.Bars.Count, rows[0][0]);
        Assert.Equal((long)Fixture.Bars.Count(b => b.Vwap is not null), rows[0][1]);

        var sum = Fixture.Bars.Where(b => b.Vwap is not null).Sum(b => b.Vwap!.Value);
        Assert.Equal(sum, (decimal)rows[0][2]!);
    }

    [Fact]
    public async Task Query_24_extract_hour()
    {
        var rows = await RunAsync("24_extract_hour");

        var expected = Fixture.Bars
            .GroupBy(b => (long)b.Ts.Hour)
            .OrderBy(g => g.Key)
            .Select(g => new object?[] { g.Key, g.Average(b => b.Close) })
            .ToArray();

        Assert.Equal(expected.Length, rows.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i][0], rows[i][0]);
            AssertClose((double)expected[i][1]!, (double)rows[i][1]!);
        }
    }

    [Fact]
    public async Task Query_27_tpch_q6()
    {
        var rows = await RunAsync("27_tpch_q6");

        var from = new DateOnly(1994, 1, 1);
        var to = new DateOnly(1995, 1, 1);
        var expected = Fixture.LineItems
            .Where(l => l.ShipDate >= from && l.ShipDate < to
                && l.Discount >= 0.05m && l.Discount <= 0.07m
                && l.Quantity < 24m)
            .Sum(l => l.ExtendedPrice * l.Discount);

        Assert.Single(rows);
        Assert.Equal(expected, (decimal)rows[0][0]!);
    }

    // ---- the windows-II families DuckDB has no answer for (14-windows-ii.md §9) ----------------

    /// <summary>
    /// D55's hop, against a literal enumeration of every window a row falls in. DuckDB has no HOP,
    /// so this and the session test below are what stand between the two Chalk engines and a shared
    /// misunderstanding of what a hopping window is.
    /// </summary>
    [Fact]
    public async Task Windows_ii_query_01_hop_group_by()
    {
        var rows = await RunM5Async("01_hop_group_by");

        var slide = TimeSpan.FromMinutes(5).Ticks;
        var size = TimeSpan.FromMinutes(15).Ticks;
        var epoch = DateTime.UnixEpoch.Ticks;

        var expected = SmallBars()
            .SelectMany(b =>
            {
                var t = b.Ts.Ticks - epoch;
                var floor = t - Modulo(t, slide);
                var starts = new List<long>();
                for (var start = floor; start > t - size; start -= slide)
                {
                    starts.Add(start);
                }

                starts.Reverse();
                return starts.Select(start => (b.Symbol, Start: start, b.Volume));
            })
            .GroupBy(x => (x.Symbol, x.Start))
            .Select(g => new object?[]
            {
                g.Key.Symbol,
                g.Key.Start * 100,
                g.Sum(x => x.Volume),
            });

        AssertSameMultiset(expected, rows);
    }

    /// <summary>
    /// D55's session, against a naive scan: a new session wherever the gap from the previous trade
    /// reaches two hours, and NULL bounds for a trade with no time at all.
    /// </summary>
    [Fact]
    public async Task Windows_ii_query_02_session_windows()
    {
        var rows = await RunM5Async("02_session_windows");

        var gap = TimeSpan.FromHours(2).Ticks;
        var expected = new List<object?[]>();
        foreach (var group in Fixture.SparseTrades.GroupBy(t => t.Symbol))
        {
            var timed = group.Where(t => t.Ts is not null).OrderBy(t => t.Ts!.Value).ToList();
            var i = 0;
            while (i < timed.Count)
            {
                var first = timed[i].Ts!.Value.Ticks;
                var last = first;
                var j = i;
                while (j + 1 < timed.Count && timed[j + 1].Ts!.Value.Ticks < last + gap)
                {
                    j++;
                    last = timed[j].Ts!.Value.Ticks;
                }

                for (var k = i; k <= j; k++)
                {
                    expected.Add(
                    [
                        group.Key,
                        Nanoseconds(timed[k].Ts!.Value.Ticks),
                        Nanoseconds(first),
                        Nanoseconds(last + gap),
                    ]);
                }

                i = j + 1;
            }

            foreach (var trade in group.Where(t => t.Ts is null))
            {
                expected.Add([group.Key, null, null, null]);
            }
        }

        AssertSameMultiset(expected, rows);
    }

    /// <summary>The bars the windows-II corpus runs on: the same generator at 1 000 minutes.</summary>
    private static IReadOnlyList<Bar> SmallBars() => Fixture.BarsSmall;

    /// <summary>A modulo that floors, so a time before the epoch lands on the boundary below it.</summary>
    private static long Modulo(long value, long divisor)
    {
        var remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }

    /// <summary>A .NET tick is 100 ns, and the fixtures' timestamps are TIMESTAMP(9).</summary>
    private static long Nanoseconds(long ticks) => (ticks - DateTime.UnixEpoch.Ticks) * 100;

    /// <summary>
    /// A windows-II query, as <em>storage</em> rows: a TIMESTAMP(9) is the nanosecond count it is,
    /// not a <c>DateTimeOffset</c>, so the LINQ side can compute one and the multiset can sort by it.
    /// </summary>
    private async Task<List<object?[]>> RunM5Async(string name)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var query = CorpusQueries.LoadM5().Single(q => q.Name == name);
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = Fixture.Sources,
            Planner = sidecar.CreatePlanner(),
        });

        var prepared = await engine.PrepareAsync(query.Sql, query.PrepareOptions());
        var (batches, _) = await DifferentialRunner.RunAsync(engine, prepared, query);
        try
        {
            return BatchReader.ToStorageRows(batches);
        }
        finally
        {
            DifferentialRunner.Dispose(batches);
        }
    }

    private async Task<List<object?[]>> RunAsync(string name) =>
        await RunAsync(CorpusQueries.Load().Single(q => q.Name == name));

    private async Task<List<object?[]>> RunAsync(CorpusQuery query)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = Fixture.Sources,
            Planner = sidecar.CreatePlanner(),
        });

        var prepared = await engine.PrepareAsync(query.Sql, query.PrepareOptions());
        var (batches, _) = await DifferentialRunner.RunAsync(engine, prepared, query);
        try
        {
            return batches.ToRows();
        }
        finally
        {
            DifferentialRunner.Dispose(batches);
        }
    }

    /// <summary>
    /// Compares as multisets: a hash aggregate emits groups in first-seen order, which the IR does not
    /// promise, so row order is not part of the contract unless the query says ORDER BY.
    /// </summary>
    private static void AssertSameMultiset<T>(IEnumerable<T> expected, List<object?[]> actual)
    {
        var expectedRows = expected
            .Select(e => e as object?[] ?? Flatten(e!))
            .Select(Describe)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actualRows = actual.Select(Describe).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(expectedRows, actualRows);
    }

    private static object?[] Flatten(object value) =>
        value.GetType().GetProperties().Select(p => p.GetValue(value)).ToArray();

    private static string Describe(object?[] row) =>
        string.Join('|', row.Select(v => v switch
        {
            null => "NULL",
            double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => v.ToString(),
        }));

    /// <summary>
    /// Floating-point aggregates legitimately differ in the last bits between a sequential and a
    /// vectorised summation (A6), so four ULPs is the tolerance the design fixes.
    /// </summary>
    private static void AssertClose(double expected, double actual)
    {
        if (expected.Equals(actual))
        {
            return;
        }

        var ulps = Math.Abs(BitConverter.DoubleToInt64Bits(expected) - BitConverter.DoubleToInt64Bits(actual));
        Assert.True(ulps <= 4, $"expected {expected:R} but got {actual:R} ({ulps} ULPs apart)");
    }
}
