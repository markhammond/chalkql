using System.Data.Common;
using Chalk.Catalog;

namespace Chalk.Sources.Ado.Tests;

/// <summary>
/// §4's failure semantics, against a provider that can be made to misbehave on purpose: a fault
/// anywhere faults the whole query with the source named, the arena is returned, the stats record
/// what was fetched before the fault, and no connection is left open.
/// </summary>
/// <remarks>
/// These are the cases a real database will not produce on demand. A provider that faults on the
/// third row, one that blocks forever, and one that refuses the query outright are all one line of
/// configuration here and a flaky test against a real engine.
/// </remarks>
public sealed class AdoLifecycleTests
{
    private static readonly IReadOnlyList<ColumnDescriptor> Columns =
    [
        new() { Name = "id", Type = ChalkType.Int64() },
        new() { Name = "v", Type = ChalkType.Int64(nullable: true) },
    ];

    [Fact]
    public async Task A_completed_scan_disposes_its_connection()
    {
        var provider = new FakeProvider { Rows = Rows(8) };
        var source = Build(provider);

        using var arena = new ExecutionArena();
        var rows = await CountAsync(source, arena, TestContext.Current.CancellationToken);

        Assert.Equal(8, rows);
        Assert.Equal(1, provider.Opened);
        Assert.Equal(1, provider.Disposed);
        Assert.Equal(0, arena.OutstandingBytes);
    }

    /// <summary>
    /// The case the <c>await using</c> chain exists for: a consumer that stops reading part-way
    /// still closes the connection, because the enumerator's disposal runs on the way out.
    /// </summary>
    [Fact]
    public async Task An_abandoned_scan_disposes_its_connection()
    {
        var provider = new FakeProvider { Rows = Rows(1000) };
        var source = Build(provider);

        using var arena = new ExecutionArena();
        await foreach (var batch in source.ScanAsync(
            Scan(batchSize: 4), Context(arena), TestContext.Current.CancellationToken))
        {
            batch.Dispose();
            break;
        }

        Assert.Equal(1, provider.Opened);
        Assert.Equal(1, provider.Disposed);
        Assert.Equal(0, arena.OutstandingBytes);
    }

    /// <summary>A provider that refuses the query: named source, named subject, provider inside.</summary>
    [Fact]
    public async Task A_provider_exception_becomes_a_source_execution_exception()
    {
        var refusal = new InvalidOperationException("no such table");
        var provider = new FakeProvider { Rows = Rows(4), FailOnExecute = refusal };
        var source = Build(provider);

        using var arena = new ExecutionArena();
        var failure = await Assert.ThrowsAsync<SourceExecutionException>(
            () => CountAsync(source, arena, TestContext.Current.CancellationToken));

        Assert.Equal("fake", failure.SourceId);
        Assert.Contains("t", failure.Subject, StringComparison.Ordinal);
        Assert.Same(refusal, failure.InnerException);
        Assert.Equal(0, arena.OutstandingBytes);
        Assert.Equal(1, provider.Disposed);
    }

    /// <summary>
    /// A fault mid-stream faults the whole query — nothing partial is surfaced — and the stats say
    /// how many rows had already been counted, which is what a host reads to know how far it got.
    /// </summary>
    [Fact]
    public async Task A_fault_mid_stream_faults_the_query_and_keeps_the_counters()
    {
        var provider = new FakeProvider { Rows = Rows(100), FailAtRow = 7 };
        var source = Build(provider);

        using var arena = new ExecutionArena();
        var stats = new ExecutionStats();
        var context = new ScanContext { Stats = stats, Arena = arena };

        var failure = await Assert.ThrowsAsync<SourceExecutionException>(async () =>
        {
            await foreach (var batch in source.ScanAsync(
                Scan(batchSize: 2), context, TestContext.Current.CancellationToken))
            {
                batch.Dispose();
            }
        });

        Assert.Equal("fake", failure.SourceId);

        // Three whole batches of two came out before row seven; the partial fourth did not.
        Assert.Equal(6, stats.RowsScanned);
        Assert.Equal(0, arena.OutstandingBytes);
        Assert.Equal(1, provider.Disposed);
    }

    /// <summary>
    /// Cancellation reaches the driver. The token alone does not: this asserts that
    /// <see cref="DbCommand.Cancel"/> was called, which is how a provider blocked inside
    /// <c>ReadAsync</c> is actually stopped.
    /// </summary>
    [Fact]
    public async Task Cancelling_mid_fetch_calls_cancel_on_the_command()
    {
        using var cancelling = new CancellationTokenSource();
        var reached = new TaskCompletionSource();
        var provider = new FakeProvider
        {
            Rows = Rows(1000),
            BeforeRow = async (row, ct) =>
            {
                if (row == 5)
                {
                    reached.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                }
            },
        };

        var source = Build(provider);
        using var arena = new ExecutionArena();

        var reading = Task.Run(async () =>
        {
            await foreach (var batch in source.ScanAsync(
                Scan(batchSize: 1), Context(arena), cancelling.Token))
            {
                batch.Dispose();
            }
        });

        await reached.Task;
        await cancelling.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);

        Assert.True(provider.Cancelled > 0, "the source never called DbCommand.Cancel");
        Assert.Equal(1, provider.Disposed);
        Assert.Equal(0, arena.OutstandingBytes);
    }

    /// <summary>
    /// A source that is too slow is a <see cref="SourceTimeoutException"/> naming the source and the
    /// budget, and not an <see cref="OperationCanceledException"/> — the caller did not change their
    /// mind, and the two want different handling.
    /// </summary>
    [Fact]
    public async Task A_source_that_exceeds_its_timeout_says_so()
    {
        var provider = new FakeProvider
        {
            Rows = Rows(1000),
            BeforeRow = async (row, ct) =>
            {
                if (row == 3)
                {
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                }
            },
        };

        var source = Build(provider, new SourceOptions { QueryTimeout = TimeSpan.FromMilliseconds(1) });
        using var arena = new ExecutionArena();

        var failure = await Assert.ThrowsAsync<SourceTimeoutException>(
            () => CountAsync(source, arena, TestContext.Current.CancellationToken));

        Assert.Equal("fake", failure.SourceId);
        Assert.Equal(TimeSpan.FromMilliseconds(1), failure.Timeout);
        Assert.Equal(1, provider.Disposed);
        Assert.Equal(0, arena.OutstandingBytes);
    }

    /// <summary>
    /// A pushed query whose request names no timeout runs under the builder's own
    /// <see cref="SourceOptions.QueryTimeout"/> (D86). The executor leaves the request's timeout
    /// empty for a source the host did not name, and this is where that empty lands.
    /// </summary>
    [Fact]
    public async Task A_pushed_query_without_a_request_timeout_runs_under_the_builders_own()
    {
        var provider = new FakeProvider
        {
            Rows = Rows(1000),
            BeforeRow = async (row, ct) =>
            {
                if (row == 3)
                {
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                }
            },
        };
        var source = Build(provider, new SourceOptions { QueryTimeout = TimeSpan.FromMilliseconds(1) });
        using var arena = new ExecutionArena();
        var failure = await Assert.ThrowsAsync<SourceTimeoutException>(
            () => CountPushedAsync(source, arena, timeout: null, TestContext.Current.CancellationToken));
        Assert.Equal(TimeSpan.FromMilliseconds(1), failure.Timeout);
        Assert.Equal(1, provider.Disposed);
        Assert.Equal(0, arena.OutstandingBytes);
    }

    /// <summary>And a request that does name one overrides the builder's, which is the host's say (D86).</summary>
    [Fact]
    public async Task A_request_timeout_overrides_the_builders_own()
    {
        var provider = new FakeProvider
        {
            Rows = Rows(1000),
            BeforeRow = async (row, ct) =>
            {
                if (row == 3)
                {
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                }
            },
        };
        var source = Build(provider, new SourceOptions { QueryTimeout = TimeSpan.FromMinutes(5) });
        using var arena = new ExecutionArena();
        var failure = await Assert.ThrowsAsync<SourceTimeoutException>(
            () => CountPushedAsync(
                source, arena, TimeSpan.FromMilliseconds(1), TestContext.Current.CancellationToken));
        Assert.Equal(TimeSpan.FromMilliseconds(1), failure.Timeout);
        Assert.Equal(1, provider.Disposed);
        Assert.Equal(0, arena.OutstandingBytes);
    }

    /// <summary>
    /// The caller's own cancellation still reads as cancellation even when a timeout is configured:
    /// the two are separate tokens and the source has to say which one fired.
    /// </summary>
    [Fact]
    public async Task A_cancelled_caller_is_not_reported_as_a_timeout()
    {
        using var cancelling = new CancellationTokenSource();
        var reached = new TaskCompletionSource();
        var provider = new FakeProvider
        {
            Rows = Rows(1000),
            BeforeRow = async (row, ct) =>
            {
                if (row == 3)
                {
                    reached.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                }
            },
        };

        var source = Build(provider, new SourceOptions { QueryTimeout = TimeSpan.FromMinutes(5) });
        using var arena = new ExecutionArena();

        var reading = Task.Run(() => CountAsync(source, arena, cancelling.Token));
        await reached.Task;
        await cancelling.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
    }

    /// <summary>
    /// D86: <c>RefreshAsync</c> re-introspects. A source whose tables were all registered by hand
    /// has nothing to re-read and keeps the descriptor it has.
    /// </summary>
    [Fact]
    public async Task A_hand_registered_source_refreshes_to_the_same_descriptor()
    {
        var provider = new FakeProvider { Rows = Rows(1) };
        var source = Build(provider);
        var before = source.DescribeSchema();

        await source.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Same(before, source.DescribeSchema());
        Assert.Equal(0, provider.Opened);
    }

    private static AdoSource Build(FakeProvider provider, SourceOptions? options = null)
    {
        var builder = new AdoSourceBuilder("fake", provider.Connect, "main")
            .Dialect(DialectProfiles.Ansi);
        if (options is not null)
        {
            builder.Options(options);
        }

        builder.AddTable("t", Columns);
        return builder.Build();
    }

    private static ScanRequest Scan(int batchSize) => new()
    {
        Table = "t",
        Projection = [0, 1],
        OutputSchema = ArrowTypeMapping.ToArrowSchema(Columns),
        BatchSize = batchSize,
    };

    private static ScanContext Context(ExecutionArena arena) =>
        new() { Stats = new ExecutionStats(), Arena = arena };

    private static async Task<int> CountAsync(AdoSource source, ExecutionArena arena, CancellationToken ct)
    {
        var rows = 0;
        await foreach (var batch in source.ScanAsync(Scan(batchSize: 4), Context(arena), ct))
        {
            rows += batch.Length;
            batch.Dispose();
        }

        return rows;
    }

    private static async Task<int> CountPushedAsync(
        AdoSource source, ExecutionArena arena, TimeSpan? timeout, CancellationToken ct)
    {
        var request = new RemoteQueryRequest
        {
            QueryText = "SELECT id, v FROM t",
            PushedPlan = new Chalk.Ir.Rel(),
            Parameters = [],
            ParameterTypes = [],
            OutputSchema = ArrowTypeMapping.ToArrowSchema(Columns),
            BatchSize = 4,
            Timeout = timeout,
        };
        var rows = 0;
        await foreach (var batch in source.ExecuteQueryAsync(request, Context(arena), ct))
        {
            rows += batch.Length;
            batch.Dispose();
        }

        return rows;
    }

    private static List<object?[]> Rows(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new object?[] { (long)i, (long)(i * 10) })];
}
