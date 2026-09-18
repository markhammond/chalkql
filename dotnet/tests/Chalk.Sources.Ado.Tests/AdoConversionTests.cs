using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Ir;

namespace Chalk.Sources.Ado.Tests;

/// <summary>
/// The reader-to-Arrow half of §3: batch boundaries, NULLs, and the placeholder rewrite each
/// driver needs.
/// </summary>
public sealed class AdoConversionTests
{
    private static readonly IReadOnlyList<ColumnDescriptor> Columns =
    [
        new() { Name = "id", Type = ChalkType.Int64() },
        new() { Name = "v", Type = ChalkType.Int64(nullable: true) },
    ];

    /// <summary>
    /// A batch size that divides the row count, one that does not, and one larger than it all
    /// produce the same rows. The middle case is the one that finds an off-by-one at the boundary,
    /// which is why seven is in the list.
    /// </summary>
    [Theory]
    [InlineData(1, 20)]
    [InlineData(7, 20)]
    [InlineData(7, 21)]
    [InlineData(4096, 20)]
    [InlineData(4096, 0)]
    public async Task Rows_survive_every_batch_size(int batchSize, int rowCount)
    {
        // Every third value is NULL, so a null lands inside a batch, on a boundary, and last.
        var provider = new FakeProvider
        {
            Rows =
            [
                .. Enumerable.Range(0, rowCount).Select(
                    i => new object?[] { (long)i, i % 3 == 0 ? null : (long)(i * 10) }),
            ],
        };

        var source = Build(provider);
        using var arena = new ExecutionArena();
        var read = new List<object?[]>();
        var batches = 0;

        await foreach (var batch in source.ScanAsync(
            Scan(batchSize), Context(arena), TestContext.Current.CancellationToken))
        {
            using (batch)
            {
                Assert.True(batch.Length <= batchSize, "a batch was larger than the requested size");
                batches++;
                for (var row = 0; row < batch.Length; row++)
                {
                    read.Add(
                    [
                        ((Int64Array)batch.Column(0)).GetValue(row),
                        ((Int64Array)batch.Column(1)).GetValue(row),
                    ]);
                }
            }
        }

        Assert.Equal(rowCount, read.Count);
        for (var i = 0; i < rowCount; i++)
        {
            Assert.Equal((long)i, read[i][0]);
            Assert.Equal(i % 3 == 0 ? null : (long)(i * 10), read[i][1]);
        }

        Assert.Equal(rowCount == 0 ? 0 : (rowCount + batchSize - 1) / batchSize, batches);
        Assert.Equal(0, arena.OutstandingBytes);
    }

    /// <summary>
    /// The placeholder styles of §3. The planner always writes <c>?</c> — that is what Calcite's
    /// writer emits — and the adapter rewrites into what the driver takes.
    /// </summary>
    [Theory]
    [InlineData(ParameterPlaceholder.Question, "SELECT * FROM t WHERE a = ? AND b > ?")]
    [InlineData(ParameterPlaceholder.NamedDollar, "SELECT * FROM t WHERE a = $p0 AND b > $p1")]
    [InlineData(ParameterPlaceholder.NamedAt, "SELECT * FROM t WHERE a = @p0 AND b > @p1")]
    [InlineData(ParameterPlaceholder.OrdinalDollar, "SELECT * FROM t WHERE a = $1 AND b > $2")]
    public void Placeholders_are_rewritten_into_the_drivers_style(
        ParameterPlaceholder style, string expected)
    {
        var (sql, names) = AdoParameters.Rewrite("SELECT * FROM t WHERE a = ? AND b > ?", style);

        Assert.Equal(expected, sql);
        Assert.Equal(2, names.Count);
    }

    /// <summary>
    /// A <c>?</c> inside a literal is data. The rewrite is a scanner rather than a replace for
    /// exactly this: changing it would change what the query asks.
    /// </summary>
    [Theory]
    [InlineData("SELECT '?' FROM t WHERE a = ?", "SELECT '?' FROM t WHERE a = $p0")]
    [InlineData("SELECT \"a?b\" FROM t WHERE a = ?", "SELECT \"a?b\" FROM t WHERE a = $p0")]
    [InlineData("SELECT 'it''s ?' FROM t WHERE a = ?", "SELECT 'it''s ?' FROM t WHERE a = $p0")]
    [InlineData("SELECT '?' FROM t", "SELECT '?' FROM t")]
    public void A_question_mark_inside_a_literal_is_left_alone(string sql, string expected)
    {
        var (rewritten, names) = AdoParameters.Rewrite(sql, ParameterPlaceholder.NamedDollar);

        Assert.Equal(expected, rewritten);
        Assert.Equal(expected.Contains("$p0", StringComparison.Ordinal) ? 1 : 0, names.Count);
    }

    // ------------------------------------------------------------------ the column names (F35)

    /// <summary>
    /// Every value is read by position, so a result set whose columns are not the ones the plan
    /// named would be mapped silently onto the wrong fields — two same-typed columns swapped by a
    /// schema that drifted after registration is the case that motivates the check.
    /// </summary>
    [Fact]
    public async Task A_result_set_whose_columns_are_not_the_planned_ones_is_refused_naming_both()
    {
        var provider = new FakeProvider
        {
            Names = ["v", "id"],
            Rows = [[1L, 10L]],
        };

        var failure = await Assert.ThrowsAsync<SourceContractException>(
            async () =>
            {
                var source = Build(provider);
                using var arena = new ExecutionArena();
                await foreach (var batch in source.ScanAsync(
                    Scan(4096), Context(arena), TestContext.Current.CancellationToken))
                {
                    batch.Dispose();
                }
            });

        Assert.Contains("names column 0 'v'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("expects 'id'", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A source that reports its identifiers in its own case has not swapped anything, and folding
    /// unquoted names is what several of them do.
    /// </summary>
    [Fact]
    public async Task A_difference_of_case_alone_is_not_a_mismatch()
    {
        var provider = new FakeProvider { Names = ["ID", "V"], Rows = [[1L, 10L]] };
        var source = Build(provider);
        using var arena = new ExecutionArena();
        var rows = 0;

        await foreach (var batch in source.ScanAsync(
            Scan(4096), Context(arena), TestContext.Current.CancellationToken))
        {
            using (batch)
            {
                rows += batch.Length;
            }
        }

        Assert.Equal(1, rows);
    }

    private static AdoSource Build(FakeProvider provider) =>
        new AdoSourceBuilder("fake", provider.Connect, "main")
            .Dialect(DialectProfiles.Ansi)
            .AddTable("t", Columns)
            .Build();

    private static ScanRequest Scan(int batchSize) => new()
    {
        Table = "t",
        Projection = [0, 1],
        OutputSchema = ArrowTypeMapping.ToArrowSchema(Columns),
        BatchSize = batchSize,
    };

    private static ScanContext Context(ExecutionArena arena) =>
        new() { Stats = new ExecutionStats(), Arena = arena };
}
