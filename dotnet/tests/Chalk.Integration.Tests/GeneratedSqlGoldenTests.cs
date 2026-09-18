using System.Text;
using Chalk.Client;
using Chalk.Ir;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The generated SQL, recorded per dialect as golden text (§2). This is the review signal for a
/// converter change: a rule or a dialect that starts emitting different SQL shows up as a diff a
/// person can read, rather than as a passing test over a query nobody looked at.
/// </summary>
/// <remarks>
/// One file per query, holding every <c>RemoteQuery</c> the plan carries, in plan order, at every
/// pushdown level that produces one. The file also records the parameters each query refers to, so
/// a change in how a placeholder is numbered is visible too.
/// <para>
/// Regenerate with <c>CHALK_WRITE_FIXTURES=1</c> and review the diff.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class GeneratedSqlGoldenTests(SharedSidecar sidecar)
{
    private static readonly PushdownLevel[] Levels =
        [PushdownLevel.Full, PushdownLevel.FiltersOnly, PushdownLevel.ProjectionOnly];

    private static DirectoryInfo Goldens =>
        new(Path.Combine(RepoLayout.Corpus.FullName, "plans", "m7-pushdown"));

    public static TheoryData<string> Queries()
    {
        var data = new TheoryData<string>();
        foreach (var query in CorpusQueries.LoadM7())
        {
            data.Add(query.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task The_generated_sql_matches_the_golden(string name)
    {
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var query = CorpusQueries.LoadM7().Single(q => q.Name == name);
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = RemoteFixture.Shared.Sources,
            Planner = sidecar.CreatePlanner(),
        });

        var text = new StringBuilder();
        foreach (var level in Levels)
        {
            var prepared = await engine.PrepareAsync(query.Sql, query.PrepareOptions(level));
            text.Append("-- ").Append(level).Append('\n');
            var found = 0;
            foreach (var rel in PlanWalker.Rels(prepared.Plan.Root))
            {
                if (rel.KindCase != Rel.KindOneofCase.RemoteQuery)
                {
                    continue;
                }

                found++;
                var remote = rel.RemoteQuery;
                text.Append("source=").Append(remote.SourceId)
                    .Append(" dialect=").Append(remote.Dialect)
                    .Append(" parameters=").Append(remote.Parameters.Count)
                    .Append('\n');
                text.Append(remote.QueryText).Append('\n');

                // D84: both flavours travel for a SQL source, so a golden that showed only the text
                // could not tell whether the algebra had been lost.
                Assert.True(
                    remote.PushedPlan is not null,
                    $"{name}: the RemoteQuery for '{remote.SourceId}' carries no pushed plan");
            }

            if (found == 0)
            {
                text.Append("(nothing pushed)\n");
            }

            text.Append('\n');
        }

        var file = new FileInfo(Path.Combine(Goldens.FullName, name + ".sql"));
        if (Environment.GetEnvironmentVariable("CHALK_WRITE_FIXTURES") == "1")
        {
            Goldens.Create();
            await File.WriteAllTextAsync(file.FullName, text.ToString());
            return;
        }

        Assert.True(
            file.Exists,
            $"{file.FullName} does not exist. Regenerate with CHALK_WRITE_FIXTURES=1 and review it.");
        Assert.Equal(
            (await File.ReadAllTextAsync(file.FullName)).ReplaceLineEndings("\n"),
            text.ToString().ReplaceLineEndings("\n"));
    }
}
