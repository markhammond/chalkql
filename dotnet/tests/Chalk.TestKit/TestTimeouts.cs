using System.Diagnostics;
using Chalk.Sources;

namespace Chalk.TestKit;

/// <summary>
/// The timeouts the fixtures hand to sources and to the planner: the shipped defaults when the
/// tests run on their own, and none at all under a debugger — a pause at a breakpoint longer than
/// a source's thirty-second query timeout cancels the query underneath the test, and the interrupt
/// comes back as a source failure that has nothing to do with what was being debugged.
/// </summary>
public static class TestTimeouts
{
    /// <summary>
    /// Whether the fixtures run without timeouts: under a debugger, or with
    /// <c>CHALK_TEST_TIMEOUTS=none</c> in the environment, which reproduces the debugger's setting
    /// from the command line.
    /// </summary>
    public static bool None { get; } =
        Debugger.IsAttached
        || string.Equals(Environment.GetEnvironmentVariable("CHALK_TEST_TIMEOUTS"), "none", StringComparison.OrdinalIgnoreCase);

    /// <summary>The per-query timeout a fixture's ADO source runs under.</summary>
    public static TimeSpan Query { get; } =
        None ? Timeout.InfiniteTimeSpan : SourceOptions.Default.QueryTimeout;

    /// <summary>The deadline on one planner call.</summary>
    public static TimeSpan PlannerDeadline { get; } =
        None ? TimeSpan.FromHours(1) : TimeSpan.FromSeconds(30);

    /// <summary>The source options every fixture builder applies.</summary>
    public static SourceOptions SourceOptions { get; } = new() { QueryTimeout = Query };
}
