using Chalk.Client;
using Chalk.Execution.Maintenance;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A maintained statement over a keyed in-process table reads only what a refresh of upserts and
/// deletes changed (D367): the table records it, and the statement takes the record as the table's
/// change. Before, it walked the table's two snapshots to find it, and read the table whole once a row
/// replaced in its first half threw the walk off.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class KeyedTableMaintenanceTests(SharedSidecar sidecar)
{
    private sealed record Note(int Id, int Team, string Body);

    private const string Sql = "SELECT id, team, body FROM notes WHERE team < 5";

    /// <summary>
    /// A note changed near the table's start is one row out and one in, and the table is not read
    /// whole; nor when a note is deleted beside another's change, or a note of a new key arrives. After
    /// each update the maintained result is what the statement returns.
    /// </summary>
    [Fact]
    public async Task A_row_changed_in_place_is_read_as_one_row_out_and_one_in()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var notes = Enumerable.Range(1, 20_000).Select(i => new Note(i, i % 7, $"note-{i}")).ToList();
        var source = new PocoSourceBuilder("mem")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("notes", (IReadOnlyList<Note>)notes, t => t.Keyed(x => x.Id))
            .Build();
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "keyed-maintenance",
            Sources = [source],
            Planner = sidecar.CreatePlanner(),
        });
        var prepared = await engine.PrepareAsync(Sql, ct: Ct);
        await using var maintained = await prepared.MaintainAsync([], -1, [0], Ct);
        var read = Assert.Single(maintained.States.OfType<ReadState>());
        var held = new Dictionary<int, object?[]>();
        Apply(await maintained.UpdateAsync([], default, engine.Version, whole: false, Ct), held);

        await engine.RefreshAsync(r => r.Upsert(source, "notes", (IReadOnlyList<Note>)[new Note(10, 3, "changed")]), Ct);
        var update = await maintained.UpdateAsync([], default, engine.Version, whole: false, Ct);
        Assert.False(read.Rewritten);
        Assert.Equal((1, 1), (update.Before.Length, update.Now.Length));
        Apply(update, held);
        Assert.Equal(await StatementAsync(engine), held);

        await engine.RefreshAsync(
            r => r.Delete(source, "notes", (IReadOnlyList<Note>)[notes[17]])
                .Upsert(source, "notes", (IReadOnlyList<Note>)[new Note(30, 2, "moved"), new Note(20_001, 1, "new")]),
            Ct);
        update = await maintained.UpdateAsync([], default, engine.Version, whole: false, Ct);
        Assert.False(read.Rewritten);
        Assert.Equal((2, 2), (update.Before.Length, update.Now.Length));
        Apply(update, held);
        Assert.Equal(await StatementAsync(engine), held);
    }

    private static void Apply(MaintainedUpdate update, Dictionary<int, object?[]> held)
    {
        if (update.Whole)
        {
            held.Clear();
        }

        foreach (var row in Rows(update.Before))
        {
            held.Remove((int)row[0]!);
        }

        foreach (var row in Rows(update.Now))
        {
            held[(int)row[0]!] = row;
        }
    }

    private static async Task<Dictionary<int, object?[]>> StatementAsync(ChalkEngine engine)
    {
        var rows = new Dictionary<int, object?[]>();
        await using var execution = await engine.ExecuteAsync(await engine.PrepareAsync(Sql, ct: Ct), ct: Ct);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                foreach (var row in Rows(batch))
                {
                    rows.Add((int)row[0]!, row);
                }
            }
        }

        return rows;
    }

    private static IEnumerable<object?[]> Rows(Apache.Arrow.RecordBatch batch)
    {
        for (var i = 0; i < batch.Length; i++)
        {
            yield return [.. Enumerable.Range(0, batch.ColumnCount).Select(c => Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(c), i))];
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
