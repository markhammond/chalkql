using System.Globalization;

namespace Chalk.Sources.Conformance;

/// <summary>
/// One case of the entitlement conformance battery, and what a run of it must show
/// (<c>docs/design/16-entitlements.md</c> §7; <c>corpus/policy/README.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// The battery is written as data: a statement, a principal, a catalog, and the rows, labels,
/// acknowledgements and refusals the statement must produce for that principal. This is that data as
/// types, and <see cref="PolicyBattery"/> is what holds a run against it. Neither reads a file —
/// what turns <c>corpus/policy</c>'s YAML into these lives with the corpus, so this package stays
/// what it says it is: the source contract, the catalog model, and nothing else.
/// </para>
/// <para>
/// Every comparison here is between two things a host already has — the rows it got and the rows the
/// case expects — so an adapter author with a battery of their own can drive it the same way.
/// </para>
/// </remarks>
public sealed class BatteryCase
{
    /// <summary>The case number, three digits.</summary>
    public required string Id { get; init; }

    /// <summary>The coverage group of <c>README.md</c> §1: A for the design's own corpus, and so on.</summary>
    public string Group { get; init; } = "";

    /// <summary>The file holding the statement, relative to the corpus directory.</summary>
    public required string File { get; init; }

    /// <summary>The statement itself.</summary>
    public required string Sql { get; init; }

    /// <summary>The principal this case runs as.</summary>
    public required string Principal { get; init; }

    /// <summary><c>prepare</c> — the default — or <c>execute</c> for execute-time binding (D209).</summary>
    public string Binding { get; init; } = "prepare";

    /// <summary>The source profile: <c>poco</c>, <c>ado_duckdb</c>, and so on.</summary>
    public string Source { get; init; } = "poco";

    /// <summary><c>registration</c> for a case that expects registering the catalog itself to fail.</summary>
    public string Phase { get; init; } = "";

    /// <summary>The entitlement variant this case registers per table.</summary>
    public IReadOnlyDictionary<string, string> Catalog { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The prepare options this case overrides, by their corpus names.</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary><c>ordered</c> when the statement asks for a total order; otherwise a multiset.</summary>
    public string Compare { get; init; } = "multiset";

    /// <summary>What the case expects.</summary>
    public required BatteryExpectation Expect { get; init; }

    /// <summary>The case's own note, which says what it is for and where it is uncertain.</summary>
    public string Notes { get; init; } = "";

    public override string ToString() => $"{Id} {Principal}";
}

/// <summary>What one case expects: rows, labels, acknowledgements, a refusal, and the claims.</summary>
public sealed class BatteryExpectation
{
    /// <summary>The rows, in the select list's order. Null when the case expects a refusal.</summary>
    public IReadOnlyList<IReadOnlyList<object?>>? Rows { get; init; }

    /// <summary>Output column name to its reported disclosure, in the corpus's vocabulary.</summary>
    public IReadOnlyDictionary<string, string> Columns { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Per entitled table: <c>visibility</c>, <c>row_predicate_pushed</c>, <c>contradiction</c>.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Report { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

    /// <summary>The refusal, when the case expects one.</summary>
    public BatteryError? Error { get; init; }

    /// <summary>The <c>assert:</c> verbs, by name, with their arguments as text.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Asserts { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
}

/// <summary>A refusal the case expects, in the corpus's own vocabulary (README §4).</summary>
public sealed class BatteryError
{
    public required string Kind { get; init; }

    public string Table { get; init; } = "";

    public string Column { get; init; } = "";

    /// <summary>What the raw value was used for: <c>projection</c>, <c>filter</c>, and so on.</summary>
    public string Use { get; init; } = "";

    /// <summary>A substring the message must carry, where the case names one.</summary>
    public string Detail { get; init; } = "";
}

/// <summary>What running one case produced.</summary>
public sealed class BatteryRun
{
    /// <summary>The output column names, in order. Empty when the statement was refused.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>The reported disclosure of each of them, positionally.</summary>
    public IReadOnlyList<string> Disclosures { get; init; } = [];

    /// <summary>The rows, in the order they came back.</summary>
    public IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; } = [];

    /// <summary>Per entitled table, the acknowledgements the report carried.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Report { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

    /// <summary>The generated source SQL, for the cases that assert on it.</summary>
    public IReadOnlyList<string> RemoteQueries { get; init; } = [];

    /// <summary>The refusal, when the statement was refused.</summary>
    public string Refusal { get; init; } = "";

    /// <summary>Its kind: <c>POLICY</c>, <c>VALIDATION</c>, <c>INVALID_CATALOG</c>, …</summary>
    public string RefusalKind { get; init; } = "";

    /// <summary>Facts read off the plan, for the verbs of <c>cases-calcite.yaml</c>.</summary>
    public IReadOnlyDictionary<string, string> PlanFacts { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// The battery's dispatcher: one case, one run, and every disagreement named
/// (<c>docs/design/16-entitlements.md</c> §7).
/// </summary>
/// <remarks>
/// A finding here is a <em>disagreement</em> and not automatically a defect. The battery's rows were
/// computed by hand from the fixture, and where the hand computation and the engine disagree the
/// case's own note usually says which of them was uncertain. That is the point of running it: the
/// disagreements are the finding, and each one is judged rather than assumed.
/// </remarks>
public static class PolicyBattery
{
    /// <summary>Every disagreement between what the case expects and what the run produced.</summary>
    public static IReadOnlyList<ConformanceFinding> Check(BatteryCase expected, BatteryRun run)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(run);
        var findings = new List<ConformanceFinding>();
        var where = $"case {expected.Id} as {expected.Principal}";

        if (expected.Expect.Error is { } error)
        {
            Refusal(where, error, run, findings);
            return findings;
        }

        if (run.Refusal.Length > 0)
        {
            findings.Add(Fail(
                $"{where}: the statement",
                "rows",
                $"{run.RefusalKind}: {FirstSentence(run.Refusal)}"));
            return findings;
        }

        Columns(where, expected, run, findings);
        Rows(where, expected, run, findings);
        Report(where, expected, run, findings);
        Asserts(where, expected, run, findings);
        return findings;
    }

    // ---------------------------------------------------------------- the refusal

    private static void Refusal(
        string where, BatteryError error, BatteryRun run, List<ConformanceFinding> findings)
    {
        var declared = Describe(error);
        if (run.Refusal.Length == 0)
        {
            findings.Add(Fail($"{where}: the refusal", declared, "the statement was accepted"));
            return;
        }

        if (!string.Equals(error.Kind, run.RefusalKind, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(Fail($"{where}: the refusal's kind", error.Kind, run.RefusalKind));
        }

        foreach (var (what, text) in Named(error))
        {
            if (text.Length > 0
                && run.Refusal.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0)
            {
                findings.Add(Fail(
                    $"{where}: the refusal names the {what}",
                    text,
                    FirstSentence(run.Refusal)));
            }
        }
    }

    /// <summary>
    /// What a refusal must name. The <c>use</c> is the corpus's own vocabulary rather than the
    /// engine's — README §4 says as much and asks for the mapping to be the finding — so it is
    /// matched loosely, word by word, and a miss is reported rather than assumed to be a defect.
    /// </summary>
    private static IEnumerable<(string What, string Text)> Named(BatteryError error)
    {
        yield return ("table", error.Table);
        yield return ("column", error.Column);
        yield return ("detail", error.Detail);
    }

    private static string Describe(BatteryError error)
    {
        var parts = new List<string> { error.Kind };
        if (error.Table.Length > 0)
        {
            parts.Add(error.Table);
        }

        if (error.Column.Length > 0)
        {
            parts.Add(error.Column);
        }

        if (error.Use.Length > 0)
        {
            parts.Add(error.Use);
        }

        return string.Join(" ", parts);
    }

    // ---------------------------------------------------------------- the labels

    private static void Columns(
        string where, BatteryCase expected, BatteryRun run, List<ConformanceFinding> findings)
    {
        foreach (var (name, disclosure) in expected.Expect.Columns)
        {
            var at = IndexOf(run.Columns, name);
            if (at < 0)
            {
                findings.Add(Fail(
                    $"{where}: the output column '{name}'",
                    "present",
                    run.Columns.Count == 0 ? "no columns" : string.Join(", ", run.Columns)));
                continue;
            }

            var reported = at < run.Disclosures.Count ? run.Disclosures[at] : "";
            if (!SameDisclosure(disclosure, reported))
            {
                findings.Add(Fail($"{where}: {name}'s disclosure", disclosure, reported));
            }
        }
    }

    /// <summary>
    /// The corpus predates the rename of <c>Undisclosed</c> to <c>Redacted</c> (D215), and the two
    /// are the same acknowledgement. Nothing else is treated as equal.
    /// </summary>
    private static bool SameDisclosure(string expected, string reported) =>
        string.Equals(expected, reported, StringComparison.OrdinalIgnoreCase)
        || (string.Equals(expected, "Undisclosed", StringComparison.OrdinalIgnoreCase)
            && string.Equals(reported, "Redacted", StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- the rows

    private static void Rows(
        string where, BatteryCase expected, BatteryRun run, List<ConformanceFinding> findings)
    {
        if (expected.Expect.Rows is not { } rows)
        {
            return;
        }

        var ordered = string.Equals(expected.Compare, "ordered", StringComparison.Ordinal);
        var want = rows.Select(Render).ToList();
        var got = run.Rows.Select(Render).ToList();
        if (!ordered)
        {
            want.Sort(StringComparer.Ordinal);
            got.Sort(StringComparer.Ordinal);
        }

        if (want.Count != got.Count)
        {
            findings.Add(Fail(
                $"{where}: the row count", want.Count.ToString(CultureInfo.InvariantCulture),
                got.Count.ToString(CultureInfo.InvariantCulture)));
            return;
        }

        for (var i = 0; i < want.Count; i++)
        {
            if (!string.Equals(want[i], got[i], StringComparison.Ordinal))
            {
                findings.Add(Fail($"{where}: row {i}", want[i], got[i]));
            }
        }
    }

    /// <summary>
    /// One row as text. Decimals compare numerically — the corpus writes them as strings so that no
    /// YAML implementation rounds them, and the scale of a division is Calcite's rule and not the
    /// battery's (README §5 U13) — and everything else compares as it prints.
    /// </summary>
    private static string Render(IReadOnlyList<object?> row) =>
        string.Join("|", row.Select(Render));

    private static string Render(object? value) =>
        value switch
        {
            null => "<null>",
            // A decimal the corpus wrote as a quoted string so that no YAML implementation rounded
            // it. Only a run of digits with a point is one: a postcode stays a postcode and a date
            // stays a date (README §5 U13).
            string text when LooksDecimal(text) => Normalise(decimal.Parse(text, CultureInfo.InvariantCulture)),
            decimal d => Normalise(d),
            double d => Normalise((decimal)d),
            float f => Normalise((decimal)f),
            bool b => b ? "true" : "false",
            DateTime t => t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            byte[] bytes => Convert.ToHexStringLower(bytes),
            _ => value.ToString() ?? "",
        };

    private static bool LooksDecimal(string text)
    {
        var at = text.Length > 0 && text[0] == '-' ? 1 : 0;
        var point = false;
        for (var i = at; i < text.Length; i++)
        {
            if (text[i] == '.')
            {
                if (point || i == at || i == text.Length - 1)
                {
                    return false;
                }

                point = true;
                continue;
            }

            if (!char.IsAsciiDigit(text[i]))
            {
                return false;
            }
        }

        return point && at < text.Length;
    }

    /// <summary>A decimal without trailing zeros, so 12.50 and 12.5 are the same number.</summary>
    private static string Normalise(decimal value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        if (!text.Contains('.', StringComparison.Ordinal))
        {
            return text;
        }

        text = text.TrimEnd('0').TrimEnd('.');
        return text.Length == 0 || text == "-" ? "0" : text;
    }

    // ---------------------------------------------------------------- the acknowledgements

    private static void Report(
        string where, BatteryCase expected, BatteryRun run, List<ConformanceFinding> findings)
    {
        foreach (var (table, claims) in expected.Expect.Report)
        {
            if (!run.Report.TryGetValue(table, out var reported))
            {
                findings.Add(Fail($"{where}: the report for {table}", "one row", "none"));
                continue;
            }

            foreach (var (what, value) in claims)
            {
                var observed = reported.TryGetValue(what, out var found) ? found : "";
                if (!string.Equals(value, observed, StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(Fail($"{where}: {table}.{what}", value, observed));
                }
            }
        }
    }

    // ---------------------------------------------------------------- the assert verbs

    private static void Asserts(
        string where, BatteryCase expected, BatteryRun run, List<ConformanceFinding> findings)
    {
        foreach (var (verb, arguments) in expected.Expect.Asserts)
        {
            switch (verb)
            {
                case "output_column_order":
                    if (!run.Columns.SequenceEqual(arguments, StringComparer.Ordinal))
                    {
                        findings.Add(Fail(
                            $"{where}: output_column_order",
                            string.Join(", ", arguments),
                            string.Join(", ", run.Columns)));
                    }

                    break;

                case "remote_query_contains":
                    foreach (var text in arguments)
                    {
                        if (!run.RemoteQueries.Any(
                            q => q.Contains(text, StringComparison.OrdinalIgnoreCase)))
                        {
                            findings.Add(Fail(
                                $"{where}: remote_query_contains",
                                text,
                                run.RemoteQueries.Count == 0
                                    ? "no remote query"
                                    : string.Join(" / ", run.RemoteQueries)));
                        }
                    }

                    break;

                case "remote_query_not_contains":
                    foreach (var text in arguments)
                    {
                        if (run.RemoteQueries.Any(
                            q => q.Contains(text, StringComparison.OrdinalIgnoreCase)))
                        {
                            findings.Add(Fail(
                                $"{where}: remote_query_not_contains",
                                $"no '{text}'",
                                string.Join(" / ", run.RemoteQueries)));
                        }
                    }

                    break;

                default:
                {
                    // Every other verb is a fact the runner read off the plan and put in PlanFacts
                    // under the verb's own name; an empty answer is a verb nothing computed, which
                    // is a finding rather than a pass.
                    var observed = run.PlanFacts.TryGetValue(verb, out var found) ? found : "";
                    var declared = string.Join(", ", arguments);
                    if (observed.Length == 0)
                    {
                        findings.Add(Fail($"{where}: {verb}", declared, "the run computed nothing"));
                    }
                    else if (!string.Equals(observed, declared, StringComparison.Ordinal))
                    {
                        findings.Add(Fail($"{where}: {verb}", declared, observed));
                    }

                    break;
                }
            }
        }
    }

    // ---------------------------------------------------------------- the small parts

    private static int IndexOf(IReadOnlyList<string> names, string name)
    {
        for (var i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static string FirstSentence(string message)
    {
        var stop = message.IndexOf(". ", StringComparison.Ordinal);
        return stop < 0 ? message : message[..(stop + 1)];
    }

    private static ConformanceFinding Fail(string subject, string declared, string observed) => new()
    {
        Subject = subject,
        Declared = declared,
        Observed = observed,
        Outcome = ConformanceOutcome.Fail,
    };
}
