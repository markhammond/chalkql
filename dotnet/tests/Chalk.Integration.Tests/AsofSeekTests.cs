using System.Globalization;
using Apache.Arrow;
using Chalk.Client;
using Chalk.Ir;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// An ASOF join that seeks an index (D338, design 63 §4, F182). The plain join reads every right row
/// and partitions them, so over a long log the cost follows the log, not the rows asked for. Over an
/// in-process table with an ordered index on its keys and then its time, the planner may now seek
/// the index once for each distinct key and time of the left input, and read one row of each range.
/// Every answer here is checked against the same statement over a copy of the table with no index,
/// which only the plain join can answer.
/// </summary>
public sealed class AsofSeekTests(SharedSidecar sidecar) : IClassFixture<SharedSidecar>
{
    private static readonly DateTime Day = new(2026, 10, 12);

    /// <summary>Each comparison, inner and LEFT, gives the plain join's rows.</summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised, ">=", "ASOF")]
    [InlineData(ExecutionEngine.Vectorised, ">", "ASOF")]
    [InlineData(ExecutionEngine.Vectorised, "<=", "ASOF")]
    [InlineData(ExecutionEngine.Vectorised, "<", "ASOF")]
    [InlineData(ExecutionEngine.Vectorised, ">=", "LEFT ASOF")]
    [InlineData(ExecutionEngine.Vectorised, "<", "LEFT ASOF")]
    [InlineData(ExecutionEngine.Reference, ">=", "ASOF")]
    [InlineData(ExecutionEngine.Reference, "<", "LEFT ASOF")]
    public async Task A_seek_gives_the_plain_joins_rows(ExecutionEngine executor, string comparison, string join)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(executor);

        var sql = $"SELECT p.id, m.price FROM positions p {join} JOIN {{0}} m "
            + $"MATCH_CONDITION p.as_of {comparison} m.ts ON p.symbol = m.symbol ORDER BY p.id";
        var seek = await engine.PrepareAsync(string.Format(CultureInfo.InvariantCulture, sql, "marks"));
        var plain = await engine.PrepareAsync(string.Format(CultureInfo.InvariantCulture, sql, "marks_plain"));

        Assert.True(PlanWalker.Has(seek.Plan, Rel.KindOneofCase.AsOfLookupJoin), "the indexed table is sought");
        Assert.False(PlanWalker.Has(plain.Plan, Rel.KindOneofCase.AsOfLookupJoin));
        Assert.Equal((await RowsAsync(engine, plain)).Rows, (await RowsAsync(engine, seek)).Rows);
    }

    /// <summary>
    /// Keys beyond ASCII, sought straight from the left rows' columns. The index orders text as UTF-16,
    /// in which U+E000 sorts after 😀 where as UTF-8 it sorts before it, and each seek finds its own key's
    /// row: six symbols that have marks, and two near them that have none.
    /// </summary>
    [Theory]
    [InlineData(">=")]
    [InlineData("<")]
    public async Task A_seek_on_text_beyond_ascii_gives_the_plain_joins_rows(string comparison)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(ExecutionEngine.Vectorised);

        var sql = $"SELECT p.id, m.price FROM text_positions p LEFT ASOF JOIN {{0}} m "
            + $"MATCH_CONDITION p.as_of {comparison} m.ts ON p.symbol = m.symbol ORDER BY p.id";
        var seek = await engine.PrepareAsync(string.Format(CultureInfo.InvariantCulture, sql, "text"));
        var plain = await engine.PrepareAsync(string.Format(CultureInfo.InvariantCulture, sql, "text_plain"));

        Assert.True(PlanWalker.Has(seek.Plan, Rel.KindOneofCase.AsOfLookupJoin), "the indexed table is sought");
        var expected = (await RowsAsync(engine, plain)).Rows;
        Assert.Equal(expected, (await RowsAsync(engine, seek)).Rows);
        Assert.Equal(8, expected.Count);
        Assert.Equal(6, expected.Count(row => !row.EndsWith("|∅", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The rows read follow the seeks: of seven positions, five have a key and a time and seek a
    /// range, and two of those find a mark, one row each, of a log of 20 000. The plain join reads
    /// the whole log.
    /// </summary>
    [Fact]
    public async Task The_rows_read_follow_the_seeks_not_the_log()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(ExecutionEngine.Vectorised);

        var seek = await engine.PrepareAsync(
            "SELECT p.id, m.price FROM positions p ASOF JOIN marks m MATCH_CONDITION p.as_of >= m.ts "
            + "ON p.symbol = m.symbol");
        var plain = await engine.PrepareAsync(
            "SELECT p.id, m.price FROM positions p ASOF JOIN marks_plain m MATCH_CONDITION p.as_of >= m.ts "
            + "ON p.symbol = m.symbol");

        var (_, sought) = await RowsAsync(engine, seek);
        var (_, read) = await RowsAsync(engine, plain);

        Assert.Equal(Positions.Length + 2, sought);
        Assert.Equal(Positions.Length + Marks.Length, read);
    }

    /// <summary>
    /// A condition on the right input that holds a key column to one value, <c>ON m.symbol =
    /// 'EUR'</c>, is part of each seek's range.
    /// </summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task A_constant_key_is_part_of_the_seek(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(executor);

        const string sql =
            "SELECT p.id, m.price FROM positions p ASOF JOIN {0} m MATCH_CONDITION p.as_of >= m.ts "
            + "ON m.symbol = 'EUR' ORDER BY p.id";
        var seek = await engine.PrepareAsync(string.Format(CultureInfo.InvariantCulture, sql, "marks"));
        var plain = await engine.PrepareAsync(string.Format(CultureInfo.InvariantCulture, sql, "marks_plain"));

        Assert.True(PlanWalker.Has(seek.Plan, Rel.KindOneofCase.AsOfLookupJoin));
        var (rows, _) = await RowsAsync(engine, seek);
        Assert.Equal((await RowsAsync(engine, plain)).Rows, rows);
        Assert.NotEmpty(rows);
    }

    /// <summary>
    /// Where several marks share the keys and the closest time, the seek takes the first its read
    /// reaches — for <c>&gt;=</c>, which reads backwards, the last of them in the index's order —
    /// and the plain join the first in the table's order. A unique index has no such ties.
    /// </summary>
    [Fact]
    public async Task Ties_go_to_the_first_row_the_read_reaches()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(ExecutionEngine.Vectorised);

        const string sql =
            "SELECT m.price FROM positions p ASOF JOIN {0} m MATCH_CONDITION p.as_of >= m.ts "
            + "ON p.symbol = m.symbol WHERE p.id = 6";
        var sought = await engine.PrepareAsync(string.Format(CultureInfo.InvariantCulture, sql, "ties"));
        Assert.True(PlanWalker.Has(sought.Plan, Rel.KindOneofCase.AsOfLookupJoin));
        var (seek, _) = await RowsAsync(engine, sought);
        var (plain, _) = await RowsAsync(
            engine, await engine.PrepareAsync(string.Format(CultureInfo.InvariantCulture, sql, "ties_plain")));

        Assert.Equal(["2"], seek);
        Assert.Equal(["1"], plain);
    }

    private static readonly Position[] Positions =
    [
        new(1, "EUR", Day.AddHours(10)),
        new(2, "GBP", Day.AddHours(12)),
        new(3, "EUR", Day.AddHours(-1)),
        new(4, "JPY", Day.AddHours(10)),
        new(5, null, Day.AddHours(10)),
        new(6, "CHF", Day.AddHours(10)),
        new(7, "EUR", null),
    ];

    /// <summary>
    /// A log of 20 000 marks, a mark a minute for EUR and GBP from midnight, and two that are NULL
    /// in time. JPY has none, so a JPY position matches nothing.
    /// </summary>
    private static readonly Mark[] Marks =
    [
        .. Enumerable.Range(0, 10_000).SelectMany(i => new[]
        {
            new Mark("EUR", Day.AddMinutes(i), 1.0m + (i * 0.0001m)),
            new Mark("GBP", Day.AddMinutes(i), 2.0m + (i * 0.0001m)),
        }),
        new Mark("EUR", null, 9m),
        new Mark("CHF", null, 9m),
    ];

    /// <summary>
    /// Two CHF marks at the same time, a tie the index keeps in the table's order, among enough USD
    /// marks that the planner seeks rather than reads.
    /// </summary>
    private static readonly Mark[] Ties =
    [
        new("CHF", Day.AddHours(9), 1m),
        new("CHF", Day.AddHours(9), 2m),
        .. Enumerable.Range(0, 5_000).Select(i => new Mark("USD", Day.AddMinutes(i), 3m)),
    ];

    private static readonly string[] Texts = ["A", "Z", "é", "Ω", "\uE000", "\U0001F600"];

    /// <summary>A mark a minute under each of <see cref="Texts"/>.</summary>
    private static readonly Mark[] TextMarks =
    [
        .. Enumerable.Range(0, 1_000).SelectMany(i =>
            Texts.Select((symbol, s) => new Mark(symbol, Day.AddMinutes(i), s + (i * 0.0001m)))),
    ];

    /// <summary>A position under each of <see cref="Texts"/>, and two under symbols beside them that have no mark.</summary>
    private static readonly Position[] TextPositions =
    [
        .. Texts.Select((symbol, i) => new Position(10 + i, symbol, Day.AddHours(5))),
        new(30, "\uE001", Day.AddHours(5)),
        new(31, "\U0001F601", Day.AddHours(5)),
    ];

    private async Task<ChalkEngine> CreateEngineAsync(ExecutionEngine executor) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "asof-seek",
            Sources =
            [
                new PocoSourceBuilder("mem")
                    .NamingPolicy(PocoNamingPolicy.SnakeCase)
                    .AddTable("positions", Positions)
                    .AddTable("marks", Marks, t => t.Index(m => m.Symbol, m => m.Ts))
                    .AddTable("marks_plain", Marks)
                    .AddTable("ties", Ties, t => t.Index(m => m.Symbol, m => m.Ts))
                    .AddTable("ties_plain", Ties)
                    .AddTable("text_positions", TextPositions)
                    .AddTable("text", TextMarks, t => t.Index(m => m.Symbol, m => m.Ts))
                    .AddTable("text_plain", TextMarks)
                    .Build(),
            ],
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { Engine = executor },
        });

    /// <summary>Each row as its values joined by '|', NULL as '∅', and the rows the sources read.</summary>
    private static async Task<(List<string> Rows, long Scanned)> RowsAsync(ChalkEngine engine, PreparedQuery prepared)
    {
        var rows = new List<string>();
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>)[]);
        await foreach (var batch in execution.Batches)
        {
            for (var i = 0; i < batch.Length; i++)
            {
                var values = new string[batch.ColumnCount];
                for (var c = 0; c < batch.ColumnCount; c++)
                {
                    values[c] = batch.Column(c) switch
                    {
                        var column when column.IsNull(i) => "∅",
                        Int32Array ints => ints.GetValue(i)!.Value.ToString(CultureInfo.InvariantCulture),
                        Decimal128Array decimals => decimals.GetValue(i)!.Value.ToString("0.####", CultureInfo.InvariantCulture),
                        var other => other.GetType().Name,
                    };
                }

                rows.Add(string.Join('|', values));
            }

            batch.Dispose();
        }

        return (rows, execution.Stats.RowsScanned);
    }

    public sealed record Position(int Id, string? Symbol, DateTime? AsOf);

    public sealed record Mark(string Symbol, DateTime? Ts, decimal Price);
}
