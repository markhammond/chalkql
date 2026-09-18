using System.Globalization;
using System.Text;

namespace Chalk.Sources.Conformance;

/// <summary>What one check found. The kit's whole output is a list of these (D88).</summary>
public sealed class ConformanceFinding
{
    /// <summary>What was checked, e.g. <c>string collation</c> or <c>PredicateShape.Like</c>.</summary>
    public required string Subject { get; init; }

    /// <summary>What the descriptor or the dialect profile claims.</summary>
    public required string Declared { get; init; }

    /// <summary>What the source actually did.</summary>
    public required string Observed { get; init; }

    /// <summary>Whether the two agree.</summary>
    public required ConformanceOutcome Outcome { get; init; }

    /// <summary>What a host should do about it, when there is anything to do.</summary>
    public string Advice { get; init; } = string.Empty;

    /// <summary>The declared-versus-observed line the design asks for, in those words.</summary>
    public override string ToString()
    {
        var line = new StringBuilder(Outcome switch
        {
            ConformanceOutcome.Pass => "PASS ",
            ConformanceOutcome.Fail => "FAIL ",
            _ => "SKIP ",
        });
        line.Append(Subject).Append(": declared ").Append(Declared).Append(", observed ").Append(Observed);
        if (Advice.Length > 0)
        {
            line.Append(". ").Append(Advice);
        }

        return line.ToString();
    }
}

/// <summary>How a check turned out.</summary>
public enum ConformanceOutcome
{
    /// <summary>The source did what its descriptor says it does.</summary>
    Pass = 0,

    /// <summary>It did not. The descriptor claims more than the source delivers.</summary>
    Fail = 1,

    /// <summary>
    /// The check could not run — the source declares nothing to check, or the data the probe needs
    /// is not there. Distinct from a pass: nothing was proven.
    /// </summary>
    Skipped = 2,
}

/// <summary>
/// Everything one run found. A run <see cref="Passed"/> when nothing failed; a skipped check is not
/// a failure, and it is not a pass either, which is why <see cref="Skipped"/> is worth reading.
/// </summary>
public sealed class ConformanceReport
{
    public required string SourceId { get; init; }

    /// <summary>
    /// What the run was pointed at, from <see cref="ConformanceOptions.Server"/>: a server version,
    /// a build, a file name. Empty when the caller said nothing.
    /// </summary>
    public string Server { get; init; } = string.Empty;

    public required IReadOnlyList<ConformanceFinding> Findings { get; init; }

    /// <summary>Whether every check that ran agreed with the descriptor.</summary>
    public bool Passed => Findings.All(f => f.Outcome != ConformanceOutcome.Fail);

    public int Failures => Findings.Count(f => f.Outcome == ConformanceOutcome.Fail);

    public int Skipped => Findings.Count(f => f.Outcome == ConformanceOutcome.Skipped);

    /// <summary>
    /// The report as text, for checking in next to the adapter it describes. Deliberately stable:
    /// the order is the order the checks ran, which is fixed, and nothing in it is a timing.
    /// </summary>
    public override string ToString()
    {
        var text = new StringBuilder();
        text.Append("Chalk source conformance: ").Append(SourceId).Append('\n');
        if (Server.Length > 0)
        {
            text.Append("server: ").Append(Server).Append('\n');
        }

        text.Append(Passed ? "PASSED" : "FAILED")
            .Append(" — ")
            .Append(Findings.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" checks, ")
            .Append(Failures.ToString(CultureInfo.InvariantCulture))
            .Append(" failed, ")
            .Append(Skipped.ToString(CultureInfo.InvariantCulture))
            .Append(" skipped\n\n");
        foreach (var finding in Findings)
        {
            text.Append(finding).Append('\n');
        }

        return text.ToString();
    }
}

/// <summary>
/// Raised by <see cref="SourceConformance.Verify"/> when a run failed: the exception carries the
/// whole report, so a test that calls it gets the findings in its failure message rather than a
/// bare boolean.
/// </summary>
public sealed class ConformanceException : ChalkException
{
    public ConformanceException(ConformanceReport report)
        : base($"Source '{report.SourceId}' does not conform to its own descriptor:\n{report}") =>
        Report = report;

    public ConformanceReport Report { get; }
}
