using Chalk.Client;
using Chalk.Sources;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// What a host catches when a query fails while it runs (D337, F168). A ChalkQL exception reaches the
/// host as itself, so <c>catch (ArenaBudgetExceededException)</c> fires, its message led by the
/// operator it broke in. Only an exception that is not ChalkQL's is wrapped, as an
/// <see cref="ExecutionException"/> with the code <c>ExecutionFailed</c>.
/// </summary>
public sealed class ExecutionErrorTests(SharedSidecar sidecar) : IClassFixture<SharedSidecar>
{
    /// <summary>The arena breaks at the first batch, under a sort that must hold every row.</summary>
    [Fact]
    public async Task An_arena_budget_broken_at_the_first_batch_is_caught_as_itself()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(
            new ExecutionOptions { Arena = new ArenaOptions { MaxBytes = 4096 } });
        var prepared = await engine.PrepareAsync("SELECT id, text FROM notes ORDER BY text DESC");

        var error = await Assert.ThrowsAsync<ArenaBudgetExceededException>(
            () => DrainAsync(engine, prepared));

        Assert.Equal(ChalkErrorCodes.MemoryBudgetExceeded, error.Code);
        Assert.Equal(4096, error.MaxBytes);
        Assert.Matches(@"^Execution failed at root\S* \(plan [0-9a-f]{16}\): ", error.Message);
        Assert.EndsWith("[MemoryBudgetExceeded]", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The failure comes after the host has read batches: row 7's text is not valid UTF-16, so it has
    /// no UTF-8, and the scan refuses it as it stages that batch, two rows to a batch.
    /// </summary>
    [Fact]
    public async Task A_failure_mid_stream_is_caught_as_itself_after_the_batches_before_it()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(new ExecutionOptions { BatchSize = 2 });
        var prepared = await engine.PrepareAsync("SELECT id, text FROM bad_notes");

        var rows = 0;
        var error = await Assert.ThrowsAsync<InvalidUtf8Exception>(async () =>
        {
            await using var execution = await engine.ExecuteAsync(prepared, NoParameters);
            await foreach (var batch in execution.Batches)
            {
                rows += batch.Length;
                batch.Dispose();
            }
        });

        Assert.InRange(rows, 2, 6);
        Assert.Equal(ChalkErrorCodes.InvalidUtf8, error.Code);
        Assert.Matches(@"^Execution failed at root\S* \(plan [0-9a-f]{16}\): ", error.Message);
    }

    /// <summary>The reference engine raises the same exception, as itself.</summary>
    [Fact]
    public async Task The_reference_engine_lets_a_chalkql_exception_through_as_itself()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(
            new ExecutionOptions { Engine = ExecutionEngine.Reference });
        var prepared = await engine.PrepareAsync("SELECT id, text FROM bad_notes");

        var error = await Assert.ThrowsAsync<InvalidUtf8Exception>(() => DrainAsync(engine, prepared));

        Assert.Equal(ChalkErrorCodes.InvalidUtf8, error.Code);
    }

    /// <summary>A host's own function throws: that is not ChalkQL's, so it is wrapped, naming the operator.</summary>
    [Fact]
    public async Task A_hosts_own_exception_is_wrapped_as_an_execution_failure()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(new ExecutionOptions());
        var prepared = await engine.PrepareAsync("SELECT id, checked(id) AS c FROM notes");

        var error = await Assert.ThrowsAsync<ExecutionException>(() => DrainAsync(engine, prepared));

        Assert.Equal(ChalkErrorCodes.ExecutionFailed, error.Code);
        Assert.StartsWith("root", error.OperatorPath, StringComparison.Ordinal);
        var inner = Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal("the host refused row 7", inner.Message);
    }

    private static readonly IReadOnlyList<object?> NoParameters = [];

    private static readonly Note[] Notes =
    [
        .. Enumerable.Range(1, 10).Select(id => new Note(id, $"note {id} " + new string('x', 200))),
    ];

    private static readonly Note[] BadNotes =
    [
        .. Enumerable.Range(1, 10).Select(id => new Note(id, id == 7 ? "a\uD800b" : $"note {id}")),
    ];

    private async Task<ChalkEngine> CreateEngineAsync(ExecutionOptions execution) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "execution-errors",
            Sources =
            [
                new PocoSourceBuilder("mem")
                    .NamingPolicy(PocoNamingPolicy.SnakeCase)
                    .AddTable("notes", Notes)
                    .AddTable("bad_notes", BadNotes)
                    .AddFunction("checked", f => f.Scalar<int, int>("id").Strict().Client())
                    .Build(),
            ],
            Functions = registry => registry.AddScalar<int, int>(
                "checked",
                id => id == 7 ? throw new InvalidOperationException("the host refused row 7") : id),
            Planner = sidecar.CreatePlanner(),
            Execution = execution,
        });

    private static async Task DrainAsync(ChalkEngine engine, PreparedQuery prepared)
    {
        await using var execution = await engine.ExecuteAsync(prepared, NoParameters);
        await foreach (var batch in execution.Batches)
        {
            batch.Dispose();
        }
    }

    public sealed record Note(int Id, string Text);
}
