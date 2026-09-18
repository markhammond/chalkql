using System.Text;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Ir;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// Where the sanitiser is evaluated when the entitled tables are in a database: above the source
/// boundary, or inside the query the source is sent (<c>docs/design/16-entitlements.md</c> §3.2,
/// §3.8; F104).
/// </summary>
/// <remarks>
/// <para>
/// The sanitiser of a protected column is a <c>CASE</c> the pass builds from the descriptor's rules
/// (§3.2), and until this measurement nothing said where it ran. A <c>CASE</c> was never pushed to
/// a SQL source and a host could not enable it — <c>IfThen</c> is a top-level expression kind
/// rather than a <c>FunctionId</c>, so it could not be named in <c>PushableFunctions</c> — which
/// meant an entitled leaf over a real database always kept a projection above the boundary,
/// whatever the source could actually evaluate.
/// </para>
/// <para>
/// What is recorded per statement and binding mode, summed over every principal: how many
/// <c>RemoteQuery</c> rels the plan carries, how many <c>IfThen</c> expressions stand in the
/// executed tree <em>above</em> them, how many stand <em>inside</em> a pushed plan, and how many of
/// the SQL texts a source is sent spell a <c>CASE</c>. The two walks are ADR 0062 §6's:
/// <see cref="PlanWalker.ExecutedRels(Plan)"/> is what the client runs and
/// <see cref="PlanWalker.Rels(Plan)"/> is that plus everything inside a pushed subtree.
/// </para>
/// <para>
/// A summary rather than the texts: thirty-six statements, two binding modes and fifteen principals
/// are a thousand plans, and what a reviewer needs to see move is the count on each side of the
/// boundary. The texts themselves are in the goldens the pushdown corpus already records and, for
/// the plans this run moved, in ADR 0065.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class SanitiserBoundaryTests(SharedSidecar sidecar)
{
    /// <summary>Where the measurement is recorded, one line per statement and binding mode.</summary>
    private static FileInfo Golden =>
        new(Path.Combine(RepoLayout.Corpus.FullName, "plans", "m7-tenancy-case", "boundary.txt"));

    /// <summary>
    /// The tables <see cref="TenancyAdoFixture"/> does not hold, as
    /// <see cref="TenancyRemoteBindingTests"/> lists them: a statement naming one is not a statement
    /// this fixture can plan at all.
    /// </summary>
    private static readonly string[] Absent = ["invites", "notes", "attachments", "symbols"];

    /// <summary>
    /// The whole tenancy family over a database, both binding modes, every principal — measured on
    /// each side of the source boundary.
    /// </summary>
    [Fact]
    public async Task The_sanitiser_is_measured_on_each_side_of_the_boundary()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = TenancyAdoFixture.Shared.Sources,
            Planner = sidecar.CreatePlanner(),
            Functions = TenancyAdoFixture.RegisterFunctions,
        });

        var text = new StringBuilder();
        text.Append("-- F104: where the sanitiser is evaluated, summed over every principal.\n");
        text.Append("-- remote: RemoteQuery rels. above: IfThen in the executed tree. ");
        text.Append("pushed: IfThen inside a pushed plan.\n");
        text.Append("-- sql-case: remote query texts spelling a CASE. refused: principals the ");
        text.Append("policy refused.\n\n");

        foreach (var query in CorpusQueries.LoadM7Tenancy())
        {
            if (Absent.Any(t => query.Sql.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (var partial in new[] { false, true })
            {
                text.Append(await MeasureAsync(engine, query, partial));
            }
        }

        if (Environment.GetEnvironmentVariable("CHALK_WRITE_FIXTURES") == "1")
        {
            Golden.Directory!.Create();
            await File.WriteAllTextAsync(Golden.FullName, text.ToString());
            return;
        }

        Assert.True(
            Golden.Exists,
            $"{Golden.FullName} does not exist. Regenerate with CHALK_WRITE_FIXTURES=1 and read it.");
        Assert.Equal(
            (await File.ReadAllTextAsync(Golden.FullName)).ReplaceLineEndings("\n"),
            text.ToString().ReplaceLineEndings("\n"));
    }

    /// <summary>One statement under one binding mode, over every principal.</summary>
    private static async Task<string> MeasureAsync(
        ChalkEngine engine, CorpusQuery query, bool partial)
    {
        int remote = 0, above = 0, pushed = 0, spelled = 0, refused = 0, failed = 0;
        foreach (var (_, context) in TenancyFixture.Principals)
        {
            var bound = partial ? TenancyFixture.PartiallyBound(context) : context.Shape();
            EntitledQuery prepared;
            try
            {
                prepared = await engine
                    .WithEntitlements()
                    .PrepareAsync(query.Sql, bound, query.PrepareOptions(PushdownLevel.Full));
            }
            catch (EntitlementException)
            {
                refused++;
                continue;
            }
            catch (Exception error)
                when (TenancyCorpusTests.KnownLeaks.ContainsKey(query.Name)
                    && TenancyCorpusTests.IsRegistered(error))
            {
                // A registered defect (D251) plans no further; the measurement says so and moves on.
                failed++;
                continue;
            }

            var plan = prepared.Query.Plan;
            foreach (var rel in PlanWalker.ExecutedRels(plan))
            {
                above += Conditionals(rel);
                if (rel.KindCase != Rel.KindOneofCase.RemoteQuery)
                {
                    continue;
                }

                remote++;
                if (Spells(rel.RemoteQuery.QueryText))
                {
                    spelled++;
                }

                foreach (var inside in PlanWalker.Pushed(rel).SelectMany(PlanWalker.Rels))
                {
                    pushed += Conditionals(inside);
                }
            }
        }

        var where = partial ? "folded      " : "execute-time";
        return $"{query.Name,-42} {where} remote={remote,-4} above={above,-4} pushed={pushed,-4} "
            + $"sql-case={spelled,-4} refused={refused,-3} failed={failed}\n";
    }

    /// <summary>Every <c>IfThen</c> in one rel's own expressions, however deeply nested.</summary>
    private static int Conditionals(Rel rel) =>
        PlanWalker.OwnExprs(rel)
            .SelectMany(PlanWalker.Exprs)
            .Count(e => e.KindCase == Expr.KindOneofCase.IfThen);

    /// <summary>
    /// Whether a generated statement spells a <c>CASE</c>. The word is looked for with its
    /// following space so that a column or an alias holding it as a substring is not a match; the
    /// converter writes the keyword upper-case whatever the dialect.
    /// </summary>
    private static bool Spells(string sql) =>
        sql.Contains("CASE WHEN", StringComparison.Ordinal);
}
