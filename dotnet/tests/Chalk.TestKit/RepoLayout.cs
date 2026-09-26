using System.Reflection;

namespace Chalk.TestKit;

/// <summary>
/// Finds the repository root from a test assembly so tests can read <c>corpus/</c> and the planner's
/// digest fixtures without a working-directory convention.
/// </summary>
public static class RepoLayout
{
    private static readonly Lazy<DirectoryInfo> RootLazy = new(Find);

    /// <summary>The repository root — the directory holding <c>proto/</c>, <c>planner/</c> and <c>dotnet/</c>.</summary>
    public static DirectoryInfo Root => RootLazy.Value;

    /// <summary>The shared query/plan corpus.</summary>
    public static DirectoryInfo Corpus => new(Path.Combine(Root.FullName, "corpus"));

    /// <summary>
    /// Where a fixture puts the files it has to write: databases, data directories, log files. Under
    /// the machine's temp root and never under the working tree (D133 §0c) — a fixture that writes a
    /// database beside the sources leaves a blob a reviewer has to notice, and one of them reached a
    /// commit before it was found. <c>git status --porcelain</c> after a full run is the check.
    /// </summary>
    public static DirectoryInfo TestTemp
    {
        get
        {
            var root = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "chalk-tests"));
            root.Create();
            return root;
        }
    }

    /// <summary>
    /// A path under <see cref="TestTemp"/> for a file or directory this run owns. The caller deletes
    /// it; nothing here outlives the machine's own temp cleaning if it does not.
    /// </summary>
    public static string TestTempPath(string name) => Path.Combine(TestTemp.FullName, name);

    /// <summary>
    /// Whether <paramref name="path"/> is inside <see cref="TestTemp"/>. Fixtures assert this about
    /// the paths they are about to write, so the invariant is checked where it is established rather
    /// than only by the repository being clean afterwards.
    /// </summary>
    public static bool IsUnderTestTemp(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var root = Path.TrimEndingDirectorySeparator(TestTemp.FullName);
        var full = Path.GetFullPath(path);
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>Corpus queries for milestone 1.</summary>
    public static DirectoryInfo Queries => new(Path.Combine(Corpus.FullName, "queries", "m1"));

    /// <summary>Corpus queries expected to fail planning.</summary>
    public static DirectoryInfo ErrorQueries => new(Path.Combine(Corpus.FullName, "queries", "m1-errors"));

    /// <summary>Corpus queries for milestone 2 — the index corpus (D40).</summary>
    public static DirectoryInfo QueriesM2 => new(Path.Combine(Corpus.FullName, "queries", "m2"));

    /// <summary>Recorded plans, digests and their JSON twins, for milestone 1.</summary>
    public static DirectoryInfo Plans => new(Path.Combine(Corpus.FullName, "plans", "m1"));

    /// <summary>Recorded plans for one milestone's corpus.</summary>
    public static DirectoryInfo PlansFor(string milestone) =>
        new(Path.Combine(Corpus.FullName, "plans", milestone));

    /// <summary>Frozen plans from released IR versions, replayed by <c>Chalk.Ir.Tests</c>.</summary>
    public static DirectoryInfo IrCompat => new(Path.Combine(Corpus.FullName, "ir-compat"));

    /// <summary>Small plans written by the planner's Java tests; the C# digest must reproduce them.</summary>
    public static DirectoryInfo DigestFixtures =>
        new(Path.Combine(Root.FullName, "planner", "src", "test", "resources", "digest-fixtures"));

    /// <summary>The planner shadow jar, if it has been built.</summary>
    public static FileInfo? PlannerJar
    {
        get
        {
            var libs = new DirectoryInfo(Path.Combine(Root.FullName, "planner", "build", "libs"));
            return libs.Exists
                ? libs.GetFiles("chalk-planner-*-all.jar").OrderBy(f => f.Name, StringComparer.Ordinal).LastOrDefault()
                : null;
        }
    }

    /// <summary>
    /// The jar the suites run: <c>CHALK_PLANNER_JAR</c> when it names a file — a relative value searched
    /// for upward from the working directory and from this assembly's directory, by the client's own
    /// rule (F106) — else <see cref="PlannerJar"/>.
    /// </summary>
    public static FileInfo? ResolvedPlannerJar
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("CHALK_PLANNER_JAR");
            if (string.IsNullOrWhiteSpace(configured))
            {
                return PlannerJar;
            }

            // The client's own rule, so a relative value means the same thing to a fixture as to a
            // sample or a test that starts the sidecar itself: searched for from the working directory
            // upward, then from this assembly's directory upward.
            var climbFrom = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            return Client.PlannerProcess.TryResolveNamedJar(configured, climbFrom, out var resolved, out _)
                ? new FileInfo(resolved)
                : PlannerJar;
        }
    }

    private static DirectoryInfo Find()
    {
        var start = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Directory.GetCurrentDirectory();
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "proto", "chalk", "v1"))
                && Directory.Exists(Path.Combine(dir.FullName, "planner"))
                && Directory.Exists(Path.Combine(dir.FullName, "dotnet")))
            {
                return dir;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not find the Chalk repository root above '{start}'. Tests locate corpus/ and the planner "
            + "jar relative to it; run them from a working tree rather than a copied output directory.");
    }
}
