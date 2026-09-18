using System.Globalization;
using Chalk.Client;

namespace Chalk.TestKit;

/// <summary>
/// One corpus query: the SQL a host writes, the SQL the planner sees (they differ for the `@name`
/// and `$n` styles, which the client rewrites — D27, V13), and the <c>-- expect:</c> assertions.
/// </summary>
public sealed class CorpusQuery
{
    public required string Name { get; init; }

    /// <summary>Which corpus this query belongs to — <c>m1</c> or <c>m2</c>. Also its plans directory.</summary>
    public required string Milestone { get; init; }

    /// <summary>What a host would write, parameter styles and all.</summary>
    public required string Sql { get; init; }

    /// <summary>
    /// What the planner is given: identical to <see cref="Sql"/> unless
    /// <c>corpus/queries/m1-rewritten/</c> holds a twin, which it does for the five parameter-style
    /// queries. <c>ParameterRewriterCorpusTests</c> asserts the rewriter still produces exactly this.
    /// </summary>
    public required string PlannerSql { get; init; }

    /// <summary>The <c>-- expect:</c> lines, without the prefix.</summary>
    public required IReadOnlyList<string> Expectations { get; init; }

    /// <summary>
    /// How this query's rows are compared (D166, <c>25-coverage-graft.md</c> §3), from its
    /// <c>-- compare:</c> header. <see cref="ResultComparisonMode.Default"/> when the file does not
    /// say, which is the rule of <c>05-testing.md</c> §5: a total order compares row by row and
    /// anything else compares as a multiset.
    /// </summary>
    public ResultComparisonMode Comparison { get; init; }

    /// <summary>
    /// The SQL dialect this query is written in, from its <c>-- conformance:</c> header (D34).
    /// <see cref="SqlConformance.Default"/> — standard SQL — when the file does not say.
    /// </summary>
    public required SqlConformance Conformance { get; init; }

    /// <summary>
    /// The dialect function libraries this query asks for, from its <c>-- libraries:</c> header
    /// (D60). Empty when the file does not say, which is the standard operators alone.
    /// </summary>
    public required IReadOnlyList<SqlLibrary> Libraries { get; init; }

    /// <summary>
    /// A cross-source join policy this query is planned under, from its <c>-- join-policy:</c>
    /// header (D104, M5). Null when the file does not say, which is the catalog's own policy.
    /// </summary>
    public required Chalk.Catalog.CrossSourceJoinPolicy? JoinPolicy { get; init; }

    /// <summary>Planning options for this query: its dialect and libraries, at the given pushdown level.</summary>
    public PrepareOptions PrepareOptions(PushdownLevel pushdown = PushdownLevel.Full) =>
        new()
        {
            Pushdown = pushdown,
            Conformance = Conformance,
            Libraries = Libraries,
            JoinPolicy = JoinPolicy,
        };

    /// <summary>The same, with capabilities the planner must ignore (D87).</summary>
    public PrepareOptions PrepareOptions(IReadOnlyList<DisabledCapability> disabled) => new()
    {
        Pushdown = PushdownLevel.Full,
        Conformance = Conformance,
        Libraries = Libraries,
        DisabledCapabilities = disabled,
        JoinPolicy = JoinPolicy,
    };

    /// <summary>The same, with a policy this run supplies instead of the file's.</summary>
    public PrepareOptions PrepareOptions(
        Chalk.Catalog.CrossSourceJoinPolicy? policy, PushdownLevel pushdown = PushdownLevel.Full) =>
        new()
        {
            Pushdown = pushdown,
            Conformance = Conformance,
            Libraries = Libraries,
            JoinPolicy = policy ?? JoinPolicy,
        };

    /// <summary>True when the host-visible SQL is not what the planner sees.</summary>
    public bool IsRewritten => !string.Equals(Sql, PlannerSql, StringComparison.Ordinal);

    public override string ToString() => Name;
}

/// <summary>Reads <c>corpus/queries/</c>. Shared by the executor, corpus and integration tests.</summary>
public static class CorpusQueries
{
    private static readonly Lazy<IReadOnlyList<CorpusQuery>> Cached =
        new(() => Load(RepoLayout.Corpus.FullName));

    private static readonly Lazy<IReadOnlyList<CorpusQuery>> CachedM2 =
        new(() => Read(Path.Combine(RepoLayout.Corpus.FullName, "queries", "m2"), rewrittenDir: null));

    private static readonly Lazy<IReadOnlyList<CorpusQuery>> CachedM7 =
        new(() => Read(Path.Combine(RepoLayout.Corpus.FullName, "queries", "m7-pushdown"), rewrittenDir: null));

    private static readonly Lazy<IReadOnlyList<CorpusQuery>> CachedM3 =
        new(() => Read(Path.Combine(RepoLayout.Corpus.FullName, "queries", "m3-joins"), rewrittenDir: null));

    private static readonly Lazy<IReadOnlyList<CorpusQuery>> CachedM4 =
        new(() => Read(Path.Combine(RepoLayout.Corpus.FullName, "queries", "m4-window"), rewrittenDir: null));

    private static readonly Lazy<IReadOnlyList<CorpusQuery>> CachedM5 =
        new(() => Read(
            Path.Combine(RepoLayout.Corpus.FullName, "queries", "m5-windows-ii"), rewrittenDir: null));

    private static readonly Lazy<IReadOnlyList<CorpusQuery>> CachedM6 =
        new(() => Read(
            Path.Combine(RepoLayout.Corpus.FullName, "queries", "m6-setop"), rewrittenDir: null));

    private static readonly Lazy<IReadOnlyList<CorpusQuery>> CachedM6Udf =
        new(() => Read(
            Path.Combine(RepoLayout.Corpus.FullName, "queries", "m6-udf"), rewrittenDir: null));

    private static readonly Lazy<IReadOnlyList<CorpusQuery>> CachedM9 =
        new(() => Read(
            Path.Combine(RepoLayout.Corpus.FullName, "queries", "m9-federation"), rewrittenDir: null));

    private static readonly Lazy<IReadOnlyList<CorpusQuery>> CachedM6UdfRemote =
        new(() => Read(
            Path.Combine(RepoLayout.Corpus.FullName, "queries", "m6-udf-remote"), rewrittenDir: null));

    private static readonly Lazy<IReadOnlyList<CorpusQuery>> CachedErrors =
        new(() =>
        [
            .. Read(Path.Combine(RepoLayout.Corpus.FullName, "queries", "m1-errors"), rewrittenDir: null),
            .. Read(Path.Combine(RepoLayout.Corpus.FullName, "queries", "m4-window-errors"), rewrittenDir: null),
            .. Read(
                Path.Combine(RepoLayout.Corpus.FullName, "queries", "m5-windows-ii-errors"),
                rewrittenDir: null),
            .. Read(
                Path.Combine(RepoLayout.Corpus.FullName, "queries", "m6-setop-errors"),
                rewrittenDir: null),
            .. Read(
                Path.Combine(RepoLayout.Corpus.FullName, "queries", "m6-udf-errors"),
                rewrittenDir: null),
        ]);

    /// <summary>The cross-cutting graft family (D165, step 25). Not a milestone, hence the name.</summary>
    private static readonly Lazy<IReadOnlyList<CorpusQuery>> CachedEdge =
        new(() => Read(
            Path.Combine(RepoLayout.Corpus.FullName, "queries", "edge"), rewrittenDir: null));

    /// <summary>The federation queries that must fail at planning (§6's corpus 09).</summary>
    private static readonly Lazy<IReadOnlyList<CorpusQuery>> CachedM9Errors =
        new(() => Read(
            Path.Combine(RepoLayout.Corpus.FullName, "queries", "m9-federation-errors"),
            rewrittenDir: null));

    /// <summary>The federation queries that must fail at planning.</summary>
    public static IReadOnlyList<CorpusQuery> LoadM9Errors() => CachedM9Errors.Value;

    /// <summary>
    /// The <c>edge</c> family (D165): the adversarial small-fixture queries of the coverage graft,
    /// which cut across sort, join, window, set operation and UNNEST and so belong to no milestone.
    /// </summary>
    public static IReadOnlyList<CorpusQuery> LoadEdge() => CachedEdge.Value;

    /// <summary>The same, from a given corpus root.</summary>
    public static IReadOnlyList<CorpusQuery> LoadEdge(string corpusRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusRoot);
        return Read(Path.Combine(corpusRoot, "queries", "edge"), rewrittenDir: null);
    }

    /// <summary>Every M1 query, ordered by file name. Read once and cached; the corpus is immutable.</summary>
    public static IReadOnlyList<CorpusQuery> Load() => Cached.Value;

    /// <summary>Every M1 query under an explicit corpus root.</summary>
    public static IReadOnlyList<CorpusQuery> Load(string corpusRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusRoot);
        return Read(
            Path.Combine(corpusRoot, "queries", "m1"),
            Path.Combine(corpusRoot, "queries", "m1-rewritten"));
    }

    /// <summary>
    /// Every M2 query — the index corpus (D40), ordered by file name. Read once and cached.
    /// </summary>
    public static IReadOnlyList<CorpusQuery> LoadM2() => CachedM2.Value;

    /// <summary>Every M2 query under an explicit corpus root.</summary>
    public static IReadOnlyList<CorpusQuery> LoadM2(string corpusRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusRoot);
        return Read(Path.Combine(corpusRoot, "queries", "m2"), rewrittenDir: null);
    }

    /// <summary>Every join query — the step 17 corpus (D46), ordered by file name.</summary>
    public static IReadOnlyList<CorpusQuery> LoadM3() => CachedM3.Value;

    /// <summary>Every join query under an explicit corpus root.</summary>
    public static IReadOnlyList<CorpusQuery> LoadM3(string corpusRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusRoot);
        return Read(Path.Combine(corpusRoot, "queries", "m3-joins"), rewrittenDir: null);
    }

    /// <summary>Every window query — the step 18 corpus (D53), ordered by file name.</summary>
    public static IReadOnlyList<CorpusQuery> LoadM4() => CachedM4.Value;

    /// <summary>Every window query under an explicit corpus root.</summary>
    public static IReadOnlyList<CorpusQuery> LoadM4(string corpusRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusRoot);
        return Read(Path.Combine(corpusRoot, "queries", "m4-window"), rewrittenDir: null);
    }

    /// <summary>Every windows-II query — the step 19 corpus (D55–D60, D66–D68).</summary>
    public static IReadOnlyList<CorpusQuery> LoadM5() => CachedM5.Value;

    /// <summary>Every windows-II query under an explicit corpus root.</summary>
    public static IReadOnlyList<CorpusQuery> LoadM5(string corpusRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusRoot);
        return Read(Path.Combine(corpusRoot, "queries", "m5-windows-ii"), rewrittenDir: null);
    }

    /// <summary>Every set-operation query — the step 20 corpus (D69).</summary>
    public static IReadOnlyList<CorpusQuery> LoadM6() => CachedM6.Value;

    /// <summary>Every set-operation query under an explicit corpus root.</summary>
    public static IReadOnlyList<CorpusQuery> LoadM6(string corpusRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusRoot);
        return Read(Path.Combine(corpusRoot, "queries", "m6-setop"), rewrittenDir: null);
    }

    /// <summary>Every user-defined-function query — the step 22 corpus (D77–D81).</summary>
    public static IReadOnlyList<CorpusQuery> LoadM6Udf() => CachedM6Udf.Value;

    /// <summary>The same, from a given corpus root.</summary>
    public static IReadOnlyList<CorpusQuery> LoadM6Udf(string corpusRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusRoot);
        return Read(Path.Combine(corpusRoot, "queries", "m6-udf"), rewrittenDir: null);
    }

    /// <summary>
    /// The two step 22 queries that need a remote source (§5's 12 and 13). They live in a family of
    /// their own for the reason ADR 0020 gives: a corpus query addresses one catalog, and these want
    /// the one with a database behind it.
    /// </summary>
    public static IReadOnlyList<CorpusQuery> LoadM6UdfRemote() => CachedM6UdfRemote.Value;

    /// <summary>The federation corpus — the step 23 family (D103–D110).</summary>
    public static IReadOnlyList<CorpusQuery> LoadM9() => CachedM9.Value;

    /// <summary>The same, from a given corpus root.</summary>
    public static IReadOnlyList<CorpusQuery> LoadM9(string corpusRoot) =>
        Read(Path.Combine(corpusRoot, "queries", "m9-federation"), rewrittenDir: null);

    /// <summary>The queries that must fail, with their expected error kind.</summary>
    /// <summary>
    /// Every query in <c>corpus/queries/m7-tenancy</c> — the entitlement corpus (§8). Each one is
    /// run as <em>every</em> principal, which is what makes it a corpus rather than a set of cases:
    /// a refusal shows at prepare, and it shows for one principal and not another.
    /// </summary>
    public static IReadOnlyList<CorpusQuery> LoadM7Tenancy() => CachedM7Tenancy.Value;

    private static readonly Lazy<IReadOnlyList<CorpusQuery>> CachedM7Tenancy =
        new(() => Read(Path.Combine(RepoLayout.Corpus.FullName, "queries", "m7-tenancy"), rewrittenDir: null));

    /// <summary>
    /// Every query in <c>corpus/queries/m7-adversarial</c> — the adversarial battery (D251,
    /// <c>docs/design/32-adversarial-entitlements.md</c>): statements written to subvert the leaf
    /// rewrite, run as every principal by the same theories the tenancy corpus is run by.
    /// </summary>
    public static IReadOnlyList<CorpusQuery> LoadM7Adversarial() => CachedM7Adversarial.Value;

    private static readonly Lazy<IReadOnlyList<CorpusQuery>> CachedM7Adversarial =
        new(() => Read(
            Path.Combine(RepoLayout.Corpus.FullName, "queries", "m7-adversarial"), rewrittenDir: null));

    /// <summary>
    /// What a statement of the two entitlement families binds, by name (D251 class 1, D261).
    /// Configuration in code, as <c>docs/handoff-m1.md</c> asks and as <c>DifferentialRunner</c>
    /// already does for the M1 corpus: a value in a header would be a second dialect to parse, and
    /// the guess a probe makes is the test's own rather than the statement's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An adversarial guess is deliberately a value no fixture row holds whole. Every protected
    /// value carries a canary (D252), so a statement that quoted one would put a forbidden token in
    /// its own plan text and the detector would report the attacker's own guess as a leak.
    /// </para>
    /// <para>
    /// A test verdict's parameter is the opposite case and is a real identifier (D261): confirming
    /// one is the whole of what the verdict grants, and the value travels as a <em>parameter</em>
    /// rather than in the statement, which is what a host's own prepared statement does — the shape
    /// is the host's and the value the end user's (§0).
    /// </para>
    /// </remarks>
    public static IReadOnlyList<object?>? Parameters(string name) =>
        name switch
        {
            "03_probe_equality_against_a_parameter" => ["T"],

            // The identifier a support desk was handed: member 1's, who is in O1.
            "29_members_test_in_the_select_list" => [TenancyFixture.Members[0].NationalId],
            "30_members_test_in_the_where" => [TenancyFixture.Members[0].NationalId],
            "31_members_test_not_equals" => [TenancyFixture.Members[0].NationalId],
            // A list of two: member 1's, in O1, and member 4's, in O2.
            "32_members_test_in_list" =>
                [TenancyFixture.Members[0].NationalId, TenancyFixture.Members[3].NationalId],
            // Member 3's, in O2 — the organisation whose three rows clear the floor of three.
            "33_members_count_filtered_by_org" => [TenancyFixture.Members[2].NationalId],
            "34_members_count_filtered" => [TenancyFixture.Members[2].NationalId],

            // The adversarial family's probes over a tested column (D261, class 1 and class 5).
            "42_probe_test_upper" => [TenancyFixture.Members[0].NationalId],
            "44_probe_test_case_branch" => [TenancyFixture.Members[0].NationalId],
            _ => null,
        };

    /// <summary>Every query in {@code corpus/queries/m7-pushdown} — the pushdown corpus (D87).</summary>
    public static IReadOnlyList<CorpusQuery> LoadM7() => CachedM7.Value;

    /// <summary>The same, from a given corpus root.</summary>
    public static IReadOnlyList<CorpusQuery> LoadM7(string corpusRoot) =>
        Read(Path.Combine(corpusRoot, "queries", "m7-pushdown"), rewrittenDir: null);

    public static IReadOnlyList<CorpusQuery> LoadErrors() => CachedErrors.Value;

    /// <summary>
    /// Every corpus query — M1, then M2's index queries, the join queries, the window queries and
    /// the windows-II queries — which is what the plan-shape suites walk. The differential suite
    /// splits the two window families off into classes of their own: the reference executor
    /// materialises every frame and expands every list, so they want a fixture it can do that on
    /// (D53), and their row-scanned baselines are a different claim, because a window's required
    /// ordering turns a scan into an index-ordered lookup that still reads every row.
    /// </summary>
    public static readonly IReadOnlyList<CorpusQuery> LoadAllCache =
        [.. Load(), .. LoadM2(), .. LoadM3(), .. LoadM4(), .. LoadM5(), .. LoadM6(), .. LoadM6Udf()];

    public static IReadOnlyList<CorpusQuery> LoadAll() => LoadAllCache;

    private static IReadOnlyList<CorpusQuery> Read(string dir, string? rewrittenDir)
    {
        var milestone = new DirectoryInfo(dir).Name;
        var directory = new DirectoryInfo(dir);
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException($"corpus directory {directory.FullName} does not exist");
        }

        var queries = new List<CorpusQuery>();
        foreach (var file in directory.GetFiles("*.sql").OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(file.Name);
            var text = File.ReadAllText(file.FullName);
            var sql = StripComments(text);

            var rewritten = rewrittenDir is null ? null : Path.Combine(rewrittenDir, name + ".sql");
            var plannerSql = rewritten is not null && File.Exists(rewritten)
                ? File.ReadAllText(rewritten).Trim()
                : sql;

            queries.Add(new CorpusQuery
            {
                Name = name,
                Milestone = milestone,
                Sql = sql,
                PlannerSql = plannerSql,
                Expectations = Expectations(text),
                Comparison = Comparison(name, text),
                Conformance = Conformance(name, text),
                Libraries = Libraries(name, text),
                JoinPolicy = JoinPolicy(name, text),
            });
        }

        return queries;
    }

    private static string StripComments(string text) =>
        string.Join(
            Environment.NewLine,
            text.Split('\n').Where(line => !line.StartsWith("--", StringComparison.Ordinal)))
            .Trim();

    /// <summary>
    /// The <c>-- compare:</c> header (D166). The only value a corpus file may ask for is
    /// <c>top-k-under-ties</c>: the two stricter modes are derived from the plan and are not a
    /// query author's to choose.
    /// </summary>
    private static ResultComparisonMode Comparison(string name, string text)
    {
        var line = text.Split('\n')
            .FirstOrDefault(l => l.StartsWith("-- compare:", StringComparison.Ordinal));
        if (line is null)
        {
            return ResultComparisonMode.Default;
        }

        var wanted = line["-- compare:".Length..].Trim();
        return wanted switch
        {
            "top-k-under-ties" => ResultComparisonMode.TopKUnderTies,
            _ => throw new FormatException(
                $"{name}: '-- compare:' has no mode '{wanted}'; the only one a query may ask for is "
                + "'top-k-under-ties'"),
        };
    }

    private static IReadOnlyList<string> Expectations(string text) =>
        text.Split('\n')
            .Where(line => line.StartsWith("-- expect:", StringComparison.Ordinal))
            .Select(line => line["-- expect:".Length..].Trim())
            .ToArray();

    /// <summary>
    /// The <c>-- conformance: &lt;NAME&gt;</c> header, naming a Calcite conformance level the way the
    /// proto spells it (<c>LENIENT</c>, <c>STRICT_2003</c>, …). Absent means <c>DEFAULT</c>.
    /// </summary>
    private static SqlConformance Conformance(string name, string text)
    {
        var line = text.Split('\n')
            .FirstOrDefault(l => l.StartsWith("-- conformance:", StringComparison.Ordinal));
        if (line is null)
        {
            return SqlConformance.Default;
        }

        var wanted = line["-- conformance:".Length..].Trim().Replace("_", string.Empty, StringComparison.Ordinal);
        foreach (var value in Enum.GetValues<SqlConformance>())
        {
            if (string.Equals(value.ToString(), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        throw new FormatException($"{name}: unknown '-- conformance:' level '{line.Trim()}'");
    }

    /// <summary>
    /// The <c>-- join-policy:</c> header (D104, M5): a comma-separated list of
    /// <c>field=value</c> settings, plus an optional
    /// <c>pairs=[&lt;left&gt; -&gt; &lt;right&gt; allowed=A|B preferred=C]</c> list. <c>*</c> on a
    /// side matches every source. Absent means the catalog's own policy.
    /// </summary>
    /// <remarks>
    /// Deliberately terse. A corpus query is a SQL file with a header, and three of M5's thirteen
    /// are <em>about</em> the policy — which strategy a pair may use, what a local join may fetch —
    /// so the policy has to be sayable in the file rather than in a test that shadows it.
    /// </remarks>
    private static Chalk.Catalog.CrossSourceJoinPolicy? JoinPolicy(string name, string text)
    {
        var line = text.Split('\n')
            .FirstOrDefault(l => l.StartsWith("-- join-policy:", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var body = line["-- join-policy:".Length..].Trim();
        var pairs = new List<Chalk.Catalog.SourcePairRule>();
        var open = body.IndexOf("pairs=[", StringComparison.Ordinal);
        if (open >= 0)
        {
            var close = body.IndexOf(']', open);
            if (close < 0)
            {
                throw new FormatException($"{name}: '-- join-policy:' has an unclosed pairs=[…]");
            }

            foreach (var rule in body[(open + "pairs=[".Length)..close].Split(';'))
            {
                if (rule.Trim().Length > 0)
                {
                    pairs.Add(PairRule(name, rule));
                }
            }

            body = body[..open] + body[(close + 1)..];
        }

        long broadcastMaxRows = 0;
        var lookupMaxCalls = 0;
        long localJoinMaxRows = 0;
        long unknownRowCount = 0;
        var defaultStrategy = Chalk.Catalog.JoinStrategy.Unspecified;
        foreach (var setting in body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = setting.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                throw new FormatException($"{name}: '-- join-policy:' setting '{setting}' is not field=value");
            }

            var field = setting[..eq].Trim();
            var value = setting[(eq + 1)..].Trim();
            switch (field)
            {
                case "broadcast_max_rows": broadcastMaxRows = long.Parse(value, CultureInfo.InvariantCulture); break;
                case "lookup_max_calls": lookupMaxCalls = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "local_join_max_rows": localJoinMaxRows = long.Parse(value, CultureInfo.InvariantCulture); break;
                case "unknown_row_count": unknownRowCount = long.Parse(value, CultureInfo.InvariantCulture); break;
                case "default": defaultStrategy = Strategy(name, value); break;
                default: throw new FormatException($"{name}: '-- join-policy:' has no field '{field}'");
            }
        }

        return new Chalk.Catalog.CrossSourceJoinPolicy
        {
            DefaultStrategy = defaultStrategy,
            BroadcastMaxRows = broadcastMaxRows,
            LookupMaxCalls = lookupMaxCalls,
            LocalJoinMaxRows = localJoinMaxRows,
            UnknownRowCountAssumption = unknownRowCount,
            Pairs = pairs,
        };
    }

    /// <summary><c>&lt;left&gt; -&gt; &lt;right&gt; allowed=A|B preferred=C</c>.</summary>
    private static Chalk.Catalog.SourcePairRule PairRule(string name, string rule)
    {
        var arrow = rule.IndexOf("->", StringComparison.Ordinal);
        if (arrow < 0)
        {
            throw new FormatException($"{name}: '-- join-policy:' pair '{rule}' has no '->'");
        }

        var left = rule[..arrow].Trim();
        var rest = rule[(arrow + 2)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var right = rest.Length > 0 ? rest[0] : "*";
        var allowed = new List<Chalk.Catalog.JoinStrategy>();
        var preferred = Chalk.Catalog.JoinStrategy.Unspecified;
        for (var i = 1; i < rest.Length; i++)
        {
            var eq = rest[i].IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                throw new FormatException($"{name}: '-- join-policy:' pair setting '{rest[i]}' is not field=value");
            }

            var field = rest[i][..eq];
            var value = rest[i][(eq + 1)..];
            switch (field)
            {
                case "allowed":
                    foreach (var one in value.Split('|', StringSplitOptions.RemoveEmptyEntries))
                    {
                        allowed.Add(Strategy(name, one));
                    }

                    break;
                case "preferred": preferred = Strategy(name, value); break;
                default: throw new FormatException($"{name}: '-- join-policy:' pair has no field '{field}'");
            }
        }

        return new Chalk.Catalog.SourcePairRule
        {
            LeftSource = left == "*" ? string.Empty : left,
            RightSource = right == "*" ? string.Empty : right,
            Allowed = allowed,
            Preferred = preferred,
        };
    }

    private static Chalk.Catalog.JoinStrategy Strategy(string name, string value) =>
        Enum.GetValues<Chalk.Catalog.JoinStrategy>()
            .FirstOrDefault(
                s => string.Equals(s.ToString(), value.Trim(), StringComparison.OrdinalIgnoreCase),
                Chalk.Catalog.JoinStrategy.Unspecified) is var found
            && found != Chalk.Catalog.JoinStrategy.Unspecified
                ? found
                : throw new FormatException($"{name}: '-- join-policy:' has no strategy '{value}'");

    /// <summary>
    /// The <c>-- libraries: &lt;NAME&gt;[, &lt;NAME&gt;]</c> header, naming Calcite's dialect
    /// function libraries the way the proto spells them (<c>POSTGRESQL</c>, <c>BIG_QUERY</c>, …).
    /// Absent means the standard operators alone (D60).
    /// </summary>
    private static IReadOnlyList<SqlLibrary> Libraries(string name, string text)
    {
        var line = text.Split('\n')
            .FirstOrDefault(l => l.StartsWith("-- libraries:", StringComparison.Ordinal));
        if (line is null)
        {
            return [];
        }

        var libraries = new List<SqlLibrary>();
        foreach (var part in line["-- libraries:".Length..].Split(','))
        {
            var wanted = part.Trim().Replace("_", string.Empty, StringComparison.Ordinal);
            if (wanted.Length == 0)
            {
                continue;
            }

            var match = Enum.GetValues<SqlLibrary>()
                .FirstOrDefault(v => string.Equals(v.ToString(), wanted, StringComparison.OrdinalIgnoreCase));
            if (match == default)
            {
                throw new FormatException($"{name}: unknown '-- libraries:' entry '{part.Trim()}'");
            }

            libraries.Add(match);
        }

        return libraries;
    }
}
