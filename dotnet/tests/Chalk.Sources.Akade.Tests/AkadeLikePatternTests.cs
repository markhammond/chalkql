using System.Text;
using System.Text.RegularExpressions;
using Akade.IndexedSet;
using Chalk.Client;
using Chalk.Client.Rpc;
using Chalk.Ir;
using Chalk.Sources.Poco;

namespace Chalk.Sources.Akade.Tests;

/// <summary>
/// D312–D314 through the engine, over an Akade prefix index: the three-operand <c>LIKE</c> is a
/// lookup on the trie, a parameter pattern is a lookup that re-checks its own <c>LIKE</c> — for any
/// value, and not at all when the value is a bare prefix — and a malformed escape or pattern is
/// refused before anything runs.
/// </summary>
public sealed class AkadeLikePatternTests(AkadeLikePatternTests.Engines engines)
    : IClassFixture<AkadeLikePatternTests.Engines>
{
    public sealed record Item(int Id, string Sku);

    /// <summary>The eleven rows every LIKE case is about, then filler no pattern here matches.</summary>
    public static readonly Item[] Rows =
    [
        new(1, "KB_1"), new(2, "KBx1"), new(3, "KB\\x1"), new(4, "100%"), new(5, "1000"),
        new(6, "é"), new(7, "😀"), new(8, "kb_1"), new(9, "a!b"), new(10, "ab"), new(11, "ab!"),
        .. Enumerable.Range(12, 9_989).Select(id => new Item(id, "Z" + id)),
    ];

    [Theory]
    [InlineData("KB%", null)]
    [InlineData("KB\\_%", "\\")]
    [InlineData("KB!_%", "!")]
    [InlineData("KB!%%", "!")]
    [InlineData("é%", null)]
    public async Task An_escaped_or_plain_prefix_is_a_lookup_that_reads_only_what_it_produces(
        string pattern, string? escape)
    {
        var sql = "SELECT id FROM items WHERE sku LIKE '" + pattern + "'"
            + (escape is null ? string.Empty : " ESCAPE '" + escape + "'");

        var (ids, scanned, plan) = await engines.RunAsync(engines.Large, sql, []);

        Assert.Contains("IndexLookup", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Filter", plan, StringComparison.Ordinal);
        Assert.Equal(Expected(pattern, escape), ids);
        Assert.Equal(ids.Count, scanned);
    }

    [Theory]
    [InlineData("KB%", null)]
    [InlineData("KB_1", null)]
    [InlineData("KB\\_%", null)]
    [InlineData("KB\\_%", "\\")]
    [InlineData("100!%", "!")]
    [InlineData("_", null)]
    [InlineData("%1", null)]
    [InlineData("a!!b", "!")]
    [InlineData("kb%", null)]
    [InlineData("", null)]
    public async Task A_parameter_pattern_is_answered_through_the_lookup_for_any_value(
        string pattern, string? escape)
    {
        var sql = "SELECT id FROM items WHERE sku LIKE ?"
            + (escape is null ? string.Empty : " ESCAPE '" + escape + "'");

        var (ids, scanned, plan) = await engines.RunAsync(engines.Large, sql, [pattern]);

        Assert.Contains("IndexLookup", plan, StringComparison.Ordinal);
        Assert.Equal(Expected(pattern, escape), ids);

        // The range is the value's literal start; a bare prefix is decided by it and reads no more.
        Chalk.Sources.LikePattern.LiteralStart(pattern, escape, out var bare);
        if (bare)
        {
            Assert.Equal(ids.Count, scanned);
        }
    }

    /// <summary>
    /// D314: the same statement over eleven rows, where a scan is cheaper than the trie and the LIKE
    /// is a per-row filter — which used to refuse to prepare because its pattern was a parameter.
    /// </summary>
    [Theory]
    [InlineData("KB%")]
    [InlineData("%1")]
    [InlineData("_")]
    public async Task A_parameter_pattern_on_a_small_set_is_a_filter_and_answers_the_same(string pattern)
    {
        var (ids, _, _) = await engines.RunAsync(engines.Small, "SELECT id FROM items WHERE sku LIKE ?", [pattern]);

        Assert.Equal(Expected(pattern, null, Rows[..11]), ids);
    }

    [Fact]
    public async Task A_null_pattern_matches_nothing_and_reads_nothing()
    {
        var (ids, scanned, _) = await engines.RunAsync(engines.Large, "SELECT id FROM items WHERE sku LIKE ?", [null]);

        Assert.Empty(ids);
        Assert.Equal(0, scanned);
    }

    /// <summary>D312: a malformed value is refused when the execution is asked for, not mid-stream.</summary>
    [Theory]
    [InlineData("ab!")]
    [InlineData("a!b")]
    public async Task A_malformed_parameter_pattern_is_refused_at_execute(string pattern)
    {
        foreach (var engine in new[] { engines.Large, engines.Small })
        {
            var query = await engine.PrepareAsync("SELECT id FROM items WHERE sku LIKE ? ESCAPE '!'");

            var error = await Assert.ThrowsAsync<ParameterBindingException>(
                async () => await engine.ExecuteAsync(query, [pattern]));

            Assert.Equal(ChalkErrorCodes.ParameterBinding, error.Code);
            Assert.Contains("parameter ?0", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(pattern, error.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>D312: a malformed literal is refused at prepare, with its position.</summary>
    [Theory]
    [InlineData("SELECT id FROM items WHERE sku LIKE 'ab!' ESCAPE '!'")]
    [InlineData("SELECT id FROM items WHERE sku LIKE 'a!b' ESCAPE '!'")]
    [InlineData("SELECT id FROM items WHERE sku LIKE 'ab%' ESCAPE '!!'")]
    [InlineData("SELECT id FROM items WHERE sku LIKE 'ab%' ESCAPE ''")]
    [InlineData("SELECT id FROM items WHERE sku LIKE 'ab%' ESCAPE ?")]
    public async Task A_malformed_literal_is_refused_at_prepare(string sql)
    {
        var error = await Assert.ThrowsAsync<PlanningException>(
            async () => await engines.Large.PrepareAsync(sql));

        Assert.Equal(PlanErrorKinds.Validation, error.Kind);
        Assert.NotNull(error.Position);
    }

    /// <summary>What the SQL standard's LIKE keeps, from the rows themselves: the oracle these are held to.</summary>
    private static List<int> Expected(string pattern, string? escape, Item[]? rows = null)
    {
        var regex = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (escape is not null && c == escape[0] && i + 1 < pattern.Length)
            {
                regex.Append(Regex.Escape(pattern[++i].ToString()));
            }
            else if (c == '%')
            {
                regex.Append(".*");
            }
            else if (c == '_')
            {
                // One character, which is one code point: a surrogate pair or a single unit.
                regex.Append("(?:[\\uD800-\\uDBFF][\\uDC00-\\uDFFF]|.)");
            }
            else
            {
                regex.Append(Regex.Escape(c.ToString()));
            }
        }

        var matcher = new Regex(regex.Append('$').ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant);
        return [.. (rows ?? Rows).Where(r => matcher.IsMatch(r.Sku)).Select(r => r.Id).Order()];
    }

    /// <summary>One sidecar, and an engine over each set: ten thousand rows, and the first eleven.</summary>
    public sealed class Engines : IAsyncLifetime
    {
        private PlannerProcess? _sidecar;

        public ChalkEngine Large { get; private set; } = null!;

        public ChalkEngine Small { get; private set; } = null!;

        public async ValueTask InitializeAsync()
        {
            _sidecar = await PlannerProcess.StartAsync();
            Large = await EngineAsync("large", Rows);
            Small = await EngineAsync("small", Rows[..11]);
        }

        public async ValueTask DisposeAsync()
        {
            await Large.DisposeAsync();
            await Small.DisposeAsync();
            if (_sidecar is not null)
            {
                await _sidecar.DisposeAsync();
            }
        }

        public async Task<(List<int> Ids, long Scanned, string Plan)> RunAsync(
            ChalkEngine engine, string sql, IReadOnlyList<object?> parameters)
        {
            var query = await engine.PrepareAsync(sql);
            await using var execution = await engine.ExecuteAsync(query, parameters);
            var ids = new List<int>();
            await foreach (var batch in execution.Batches)
            {
                using (batch)
                {
                    ids.AddRange(Chalk.Arrow.RecordBatchExtensions.ToRows(batch).Select(r => Convert.ToInt32(r[0])));
                }
            }

            ids.Sort();
            return (ids, execution.Stats.RowsScanned, query.Plan.ToPlanText());
        }

        private async Task<ChalkEngine> EngineAsync(string id, Item[] rows)
        {
            var set = rows.ToIndexedSet(x => x.Id).WithPrefixIndex(x => x.Sku).Build();
            var source = AkadeSource.From(id, set)
                .TableName("items")
                .NamingPolicy(PocoNamingPolicy.SnakeCase)
                .Build();

            return await ChalkEngine.CreateAsync(new ChalkEngineOptions
            {
                ContextId = id,
                Sources = [source],
                Planner = _sidecar!.CreatePlanner(),
            });
        }
    }
}
