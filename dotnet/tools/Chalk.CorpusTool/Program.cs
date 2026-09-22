using Chalk.Client;
using Chalk.Ir;
using Chalk.TestKit;
using Google.Protobuf;
using PlanRequest = Chalk.Client.PlanRequest;

namespace Chalk.CorpusTool;

/// <summary>
/// <c>record</c> | <c>verify</c> | <c>print</c> over <c>corpus/</c> (docs/design/05-testing.md §4).
///
/// <para>
/// Recording is how a planning change becomes reviewable: the <c>.json</c> twin of every plan lands
/// in the diff, and a changed <c>plan_digest</c> in a pull request is the signal that a rule or cost
/// change altered planning behaviour.
/// </para>
/// </summary>
internal static class Program
{
    private static readonly PushdownLevel[] Levels = [PushdownLevel.Full, PushdownLevel.None];

    private static async Task<int> Main(string[] args)
    {
        try
        {
            return args.Length == 0 ? Usage() : args[0] switch
            {
                "record" => await RecordAsync(Options.Parse(args)).ConfigureAwait(false),
                "verify" => Verify(Options.Parse(args)),
                "print" => Print(Options.Parse(args)),
                _ => Usage(),
            };
        }
        catch (Exception e) when (e is ChalkException or ArgumentException or IOException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            Chalk corpus tool.

              record --corpus <dir> --planner <http://host:port>
                  Plans every query in <dir>/queries/{m1,m2,m3-joins,m4-window,m5-windows-ii,m6-setop,m6-udf} at FULL and NONE against a running
                  sidecar and writes <dir>/plans/{m1,m2,m3-joins,m4-window,m5-windows-ii,m6-setop,m6-udf}/*.{binpb,json,digest} plus
                  <dir>/schemas/corpus.{binpb,json}.

              verify --corpus <dir>
                  Re-reads every recorded plan, validates it and checks its digest. No sidecar needed.

              print --corpus <dir> --name <query>
                  Prints a recorded plan.
            """);
        return 2;
    }

    private static async Task<int> RecordAsync(Options options)
    {
        var planner = options.PlannerAddress
            ?? throw new ArgumentException("record needs --planner <http://host:port>");

        var fixture = CorpusFixture.Create();
        WriteSchemas(options.Corpus, fixture);

        await using var grpc = new GrpcQueryPlanner(new GrpcPlannerOptions { Address = new Uri(planner) });
        var info = await grpc.GetInfoAsync().ConfigureAwait(false);
        Console.WriteLine(
            $"planner {info.PlannerVersion} (calcite {info.CalciteVersion}, ir {info.MinIrVersion}..{info.MaxIrVersion}, "
            + $"config {info.PlannerConfigHash})");

        var recorded = 0;
        var counted = 0;
        foreach (var (milestone, queries) in new[]
        {
            ("m1", CorpusQueries.Load(options.Corpus)),
            ("m2", CorpusQueries.LoadM2(options.Corpus)),
            ("m3-joins", CorpusQueries.LoadM3(options.Corpus)),
            ("m4-window", CorpusQueries.LoadM4(options.Corpus)),
            ("m5-windows-ii", CorpusQueries.LoadM5(options.Corpus)),
            ("m6-setop", CorpusQueries.LoadM6(options.Corpus)),
            ("m6-udf", CorpusQueries.LoadM6Udf(options.Corpus)),

            // Not a milestone (D165): the coverage graft's cross-cutting family, which is why it is
            // named for what it is rather than for when it landed.
            ("edge", CorpusQueries.LoadEdge(options.Corpus)),
        })
        {
            var plansDir = Path.Combine(options.Corpus, "plans", milestone);
            Directory.CreateDirectory(plansDir);
            recorded += await RecordAsync(grpc, fixture, queries, plansDir).ConfigureAwait(false);
            counted += queries.Count;
        }

        Console.WriteLine($"recorded {recorded} plans for {counted} queries into {options.Corpus}/plans");
        return 0;
    }

    private static async Task<int> RecordAsync(
        GrpcQueryPlanner grpc,
        CorpusFixture fixture,
        IReadOnlyList<CorpusQuery> queries,
        string plansDir)
    {
        // Not `await using`: disposing a RecordingPlanner disposes the channel underneath it, and
        // the two milestones share one sidecar connection.
        var recorder = new RecordingPlanner(grpc, plansDir);
        await recorder.RegisterCatalogAsync(
            new CatalogRegistration { Catalog = fixture.Catalog }).ConfigureAwait(false);

        var recorded = 0;
        foreach (var query in queries)
        {
            // Record against the rewriter's own output, not the checked-in twin: RecordedPlanner
            // keys plans on the exact SQL text, and ChalkEngine will ask for whatever the rewriter
            // produces. The twin exists for the planner's Java tests and is compared to this
            // modulo whitespace by ParameterRewriterTests.
            var rewriter = ParameterRewriter.Parse(query.Sql);
            var plannerSql = rewriter.Render(rewriter.PrepareShape()).Sql;

            foreach (var level in Levels)
            {
                recorder.NextName = query.Name;
                var result = await recorder.PlanAsync(new PlanRequest
                {
                    Sql = plannerSql,
                    ContextId = fixture.Catalog.ContextId,
                    CatalogEpoch = fixture.Catalog.Epoch,
                    // What this query expects its parameters to be worth (D284). Empty for every
                    // query that says nothing, which is all but the hinted half of one pair.
                    ParameterHints = CorpusQueries.ResolvedHints(query),
                    Options = new Chalk.Client.PlannerOptions
                    {
                        Pushdown = level,
                        Conformance = query.Conformance,
                        Libraries = query.Libraries,
                    },
                }).ConfigureAwait(false);

                PlanValidator.Validate(result.Plan);
                recorded++;
                Console.WriteLine(
                    $"  {query.Name,-32} {level,-5} {query.Conformance,-8} "
                    + PlanDigest.Format(result.PlanDigest));
            }
        }

        return recorded;
    }

    private static int Verify(Options options)
    {
        var checkedPlans = 0;
        foreach (var milestone in new[] { "m1", "m2", "m3-joins", "m4-window", "m5-windows-ii", "m6-setop", "m6-udf" })
        {
            var result = Verify(options, milestone, ref checkedPlans);
            if (result != 0)
            {
                return result;
            }
        }

        Console.WriteLine($"verified {checkedPlans} recorded plans in {options.Corpus}/plans");
        return 0;
    }

    private static int Verify(Options options, string milestone, ref int checkedPlans)
    {
        var plansDir = new DirectoryInfo(Path.Combine(options.Corpus, "plans", milestone));
        if (!plansDir.Exists)
        {
            Console.Error.WriteLine($"{plansDir.FullName} does not exist; run 'record' first.");
            return 1;
        }

        foreach (var file in plansDir.GetFiles("*.binpb").OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            var plan = Plan.Parser.ParseFrom(File.ReadAllBytes(file.FullName));
            PlanValidator.Validate(plan);

            var digestFile = new FileInfo(Path.ChangeExtension(file.FullName, ".digest"));
            if (digestFile.Exists)
            {
                var recorded = PlanDigest.Parse(File.ReadAllText(digestFile.FullName));
                if (recorded != plan.PlanDigest)
                {
                    Console.Error.WriteLine(
                        $"{file.Name}: digest file says {PlanDigest.Format(recorded)} but the plan carries "
                        + PlanDigest.Format(plan.PlanDigest));
                    return 1;
                }
            }

            checkedPlans++;
        }

        return 0;
    }

    private static int Print(Options options)
    {
        var name = options.Name ?? throw new ArgumentException("print needs --name <query>");
        FileInfo? file = null;
        foreach (var milestone in new[] { "m1", "m2", "m3-joins", "m4-window", "m5-windows-ii", "m6-setop", "m6-udf" })
        {
            var plansDir = Path.Combine(options.Corpus, "plans", milestone);
            foreach (var candidate in new[] { name + ".binpb", name + ".full.binpb" })
            {
                var path = new FileInfo(Path.Combine(plansDir, candidate));
                if (path.Exists)
                {
                    file = path;
                    break;
                }
            }

            if (file is not null)
            {
                break;
            }
        }

        if (file is null)
        {
            Console.Error.WriteLine($"no recorded plan named '{name}' under {options.Corpus}/plans");
            return 1;
        }

        var plan = Plan.Parser.ParseFrom(File.ReadAllBytes(file.FullName));
        Console.Write(plan.ToPlanText());
        return 0;
    }

    /// <summary>
    /// Writes the fixture catalog so the planner's Java tests can register exactly what the client
    /// pushes, without running .NET (05-testing.md §4). Binary is what Java reads; the JSON twin is
    /// for the reviewer.
    /// </summary>
    private static void WriteSchemas(string corpus, CorpusFixture fixture)
    {
        var dir = Directory.CreateDirectory(Path.Combine(corpus, "schemas"));
        var message = fixture.CatalogMessage();
        File.WriteAllBytes(Path.Combine(dir.FullName, "corpus.binpb"), message.ToByteArray());
        File.WriteAllText(
            Path.Combine(dir.FullName, "corpus.json"),
            new JsonFormatter(JsonFormatter.Settings.Default.WithIndentation("  ")).Format(message) + "\n");
    }

    private sealed class Options
    {
        public required string Corpus { get; init; }

        public string? PlannerAddress { get; init; }

        public string? Name { get; init; }

        public static Options Parse(string[] args)
        {
            string? corpus = null;
            string? planner = null;
            string? name = null;
            for (var i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--corpus": corpus = Value(args, ++i, "--corpus"); break;
                    case "--planner": planner = Value(args, ++i, "--planner"); break;
                    case "--name": name = Value(args, ++i, "--name"); break;
                    default: throw new ArgumentException($"unknown option '{args[i]}'");
                }
            }

            return new Options
            {
                Corpus = corpus ?? throw new ArgumentException("--corpus <dir> is required"),
                PlannerAddress = planner,
                Name = name,
            };
        }

        private static string Value(string[] args, int index, string option) =>
            index < args.Length ? args[index] : throw new ArgumentException($"{option} needs a value");
    }
}
