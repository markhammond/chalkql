using System.Globalization;
using Chalk.Client;

namespace Chalk.TestKit;

/// <summary>
/// Starts a planner sidecar for the integration tests (D21, docs/design/05-testing.md §9). Nothing
/// beyond the shadow jar is required, and not even a port: this is a thin wrapper over
/// <see cref="PlannerProcess"/>, so
/// the launcher a host would use is the launcher every integration test exercises
/// (docs/design/09-unix-socket-transport.md §4).
///
/// <list type="bullet">
/// <item><c>CHALK_PLANNER_ADDRESS</c> — use an already-running sidecar (the developer loop).</item>
/// <item><c>CHALK_PLANNER_TRANSPORT</c> — <c>unix</c> (the default on macOS and Linux) or <c>tcp</c>.</item>
/// <item><c>CHALK_PLANNER_JAR</c>, else <c>planner/build/libs/chalk-planner-*-all.jar</c>.</item>
/// <item><c>java</c> from <c>JAVA_HOME</c>, else the platform's JDK locations, else <c>PATH</c>.</item>
/// </list>
///
/// When neither a JDK nor a jar is available, <see cref="SkipReason"/> explains why rather than the
/// tests failing with a connection error.
/// </summary>
public sealed class SidecarFixture : IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly PlannerProcess? _process;

    private SidecarFixture(string address, PlannerProcess? process, string? skipReason)
    {
        Address = address;
        _process = process;
        SkipReason = skipReason;
    }

    /// <summary>
    /// The base address of the running sidecar, e.g. <c>http://127.0.0.1:53359</c> or
    /// <c>unix:///tmp/chalk-4711-a1b2c3.sock</c>.
    /// </summary>
    public string Address { get; }

    /// <summary>Non-null when no sidecar could be started, with the reason to report.</summary>
    public string? SkipReason { get; }

    /// <summary>True when tests that need a sidecar can run.</summary>
    public bool IsAvailable => SkipReason is null;

    /// <summary>Starts a sidecar, or produces an unavailable fixture with a reason.</summary>
    public static async Task<SidecarFixture> StartAsync(CancellationToken ct = default)
    {
        var configured = Environment.GetEnvironmentVariable("CHALK_PLANNER_ADDRESS");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return new SidecarFixture(configured, process: null, skipReason: null);
        }

        var jar = ResolveJar();
        if (jar is null)
        {
            return Unavailable(
                "no planner jar: set CHALK_PLANNER_JAR, or build one with "
                + "'planner/gradlew -p planner shadowJar'");
        }

        PlannerProcess process;
        try
        {
            process = await PlannerProcess.StartAsync(
                new PlannerProcessOptions
                {
                    JarPath = jar,
                    Transport = Transport(),
                    StartTimeout = StartTimeout,
                    // One worker (D243): exercises the slicing policy under contention
                    // deterministically, which is what the whole integration suite runs under.
                    Workers = 1,
                },
                ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException e)
        {
            return Unavailable(e.Message);
        }

        var fixture = new SidecarFixture(process.Address.ToString(), process, skipReason: null);
        await fixture.WaitUntilServingAsync(ct).ConfigureAwait(false);
        return fixture;
    }

    /// <summary>A planner talking to this sidecar. The caller disposes it.</summary>
    public IQueryPlanner CreatePlanner() =>
        new GrpcQueryPlanner(new GrpcPlannerOptions { Address = new Uri(Address), Deadline = TestTimeouts.PlannerDeadline });

    /// <summary>Whatever the sidecar wrote to stderr, for a failure message.</summary>
    public IReadOnlyList<string> Diagnostics() => _process?.Diagnostics() ?? [];

    public ValueTask DisposeAsync() => _process?.DisposeAsync() ?? ValueTask.CompletedTask;

    /// <summary>
    /// <c>unix</c> on macOS and Linux, <c>tcp</c> elsewhere, and <c>CHALK_PLANNER_TRANSPORT</c> wins.
    /// CI runs the whole integration suite over both.
    /// </summary>
    private static PlannerTransport Transport() =>
        (Environment.GetEnvironmentVariable("CHALK_PLANNER_TRANSPORT") ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "tcp" => PlannerTransport.Tcp,
            "unix" => PlannerTransport.UnixSocket,
            "" => PlannerTransport.Default,
            var other => throw new ArgumentException(
                $"CHALK_PLANNER_TRANSPORT must be 'tcp' or 'unix'; got '{other}'"),
        };

    private static SidecarFixture Unavailable(string reason) =>
        new("unavailable", process: null, skipReason: reason);

    /// <summary>
    /// Polls <c>GetInfo</c> until it answers. A successful RPC is a stronger readiness signal than the
    /// health service — it proves the service itself is wired up, not only that the port is bound —
    /// and it needs no extra package.
    /// </summary>
    private async Task WaitUntilServingAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + StartTimeout;
        Exception? last = null;
        await using var planner = CreatePlanner();
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await planner.GetInfoAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (PlannerUnavailableException e)
            {
                last = e;
                await Task.Delay(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);
            }
        }

        throw new TimeoutException(
            $"the planner at {Address} did not answer GetInfo within {StartTimeout}."
            + Environment.NewLine + string.Join(Environment.NewLine, Diagnostics()),
            last);
    }

    private static string? ResolveJar() => RepoLayout.ResolvedPlannerJar?.FullName;

    /// <summary>Used in skip messages so the reason is visible in the test output.</summary>
    public override string ToString() =>
        IsAvailable
            ? string.Create(CultureInfo.InvariantCulture, $"sidecar at {Address}")
            : $"sidecar unavailable: {SkipReason}";
}
