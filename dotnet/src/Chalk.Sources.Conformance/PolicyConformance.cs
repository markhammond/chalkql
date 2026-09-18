namespace Chalk.Sources.Conformance;

/// <summary>
/// The <b>policy conformance run</b> and its claims harness
/// (<c>docs/design/16-entitlements.md</c> §7, §9).
/// </summary>
/// <remarks>
/// <para>
/// A source is conformant under an entitlement when every statement returns the same rows and the
/// same masks as an independent evaluation of the descriptor over the rows — and when <b>the plan
/// matches what executed</b>. The second half is what this adds to a row comparison: the planner
/// reported a label per column and a verdict per read, and the values that came back either bear
/// those out or they do not. A row comparison alone cannot tell a correct plan from one that
/// disclosed the same rows for the wrong reason, and a label nothing checks is a comment.
/// </para>
/// <para>
/// It is expressed in values and names rather than in Chalk's own client types, so a third-party
/// adapter can drive it from whatever it already has: the rows it got, the rows an oracle disclosed,
/// the rows the table holds, the labels the report gave and the text the source was sent. Every
/// disagreement names the case, the principal and the claim.
/// </para>
/// </remarks>
public static class PolicyConformance
{
    /// <summary>Checks one (statement, principal) pair against everything the plan claimed.</summary>
    public static IReadOnlyList<ConformanceFinding> Check(PolicyCase claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        var findings = new List<ConformanceFinding>();
        var where = $"{claims.Name} as {claims.Principal}";

        LabelsEqualArrowMetadata(claims, where, findings);
        LabelsAgreeWithValues(claims, where, findings);
        VisibilityAgreesWithTheRowCount(claims, where, findings);
        PushedPredicatesAppearInTheRemoteText(claims, where, findings);
        return findings;
    }

    /// <summary>
    /// The label a caller reads off the prepared query and the one a typed consumer reads off the
    /// batch are the same label, position for position. They are written from one list, so a
    /// disagreement is a defect in the writing rather than in the policy — which is why it is worth
    /// one comparison rather than a code review.
    /// </summary>
    private static void LabelsEqualArrowMetadata(
        PolicyCase claims, string where, List<ConformanceFinding> findings)
    {
        foreach (var column in claims.Columns)
        {
            if (column.ArrowMetadata is null || Same(column.ArrowMetadata, column.Reported))
            {
                continue;
            }

            findings.Add(Fail(
                $"{where}: {column.Name} labels_equal_arrow_metadata",
                column.Reported,
                $"the batch's chalk.disclosure metadata says {column.ArrowMetadata}"));
        }
    }

    /// <summary>
    /// The label and the values, held to each other. <c>Full</c> means the value is the table's own;
    /// <c>Redacted</c> means every row is the stand-in; anything else means the values are the
    /// ones an independent evaluation of the descriptor disclosed, which is what the mask is.
    /// </summary>
    /// <remarks>
    /// Compared as multisets rather than row for row: the two paths are two plans and neither
    /// promises an order the statement did not ask for, so aligning them by position would be
    /// asserting something the contract does not say.
    /// </remarks>
    private static void LabelsAgreeWithValues(
        PolicyCase claims, string where, List<ConformanceFinding> findings)
    {
        for (var i = 0; i < claims.Columns.Count; i++)
        {
            var column = claims.Columns[i];
            var executed = Column(claims.Rows, i);

            if (column.Reported == "Redacted")
            {
                // Every row is the *same* stand-in, which is not the same as every row being NULL:
                // under PlaceholdersAsEmpty it is the type's empty value, and a column may declare a
                // stand-in of its own (D162). What makes a redacted column redacted is that it says
                // one thing whatever the row held.
                var distinct = executed
                    .Select(v => v?.ToString() ?? "<null>")
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (distinct.Length > 1)
                {
                    findings.Add(Fail(
                        $"{where}: {column.Name} labels_agree_with_values",
                        "Redacted, so every row is the same stand-in",
                        $"{distinct.Length} distinct values came back"));
                }

                continue;
            }

            if (claims.DisclosedRows is null || i >= claims.Columns.Count)
            {
                continue;
            }

            var oracle = Column(claims.DisclosedRows, i);
            if (!SameMultiset(executed, oracle))
            {
                findings.Add(Fail(
                    $"{where}: {column.Name} labels_agree_with_values",
                    $"{column.Reported}, so the values an independent disclosure produces",
                    Describe(executed, oracle)));
            }
        }
    }

    /// <summary>
    /// <c>visibility = NONE</c> is no rows at all, and <c>ALL</c> is every row the table holds —
    /// both computed from this principal's context and the statement alone, so both are claims about
    /// the result that the result can be held to.
    /// </summary>
    private static void VisibilityAgreesWithTheRowCount(
        PolicyCase claims, string where, List<ConformanceFinding> findings)
    {
        foreach (var table in claims.Tables)
        {
            // Of a statement that reads the table and nothing else: a global aggregate over no
            // visible rows is one row holding zero, which is a row and not a disclosure.
            if (table.Visibility == "None" && claims.Rows.Count > 0 && claims.SingleTableScan)
            {
                findings.Add(Fail(
                    $"{where}: {table.Table} visibility",
                    "None, so the statement can return no row",
                    $"{claims.Rows.Count} row(s) came back"));
            }

            if (table.Visibility == "All" && table.UnfilteredRowCount is { } all
                && claims.Rows.Count < all && claims.SingleTableScan)
            {
                findings.Add(Fail(
                    $"{where}: {table.Table} visibility",
                    $"All, so every one of the table's {all} rows",
                    $"{claims.Rows.Count} row(s) came back"));
            }
        }
    }

    /// <summary>
    /// A pushed row predicate is a claim about the text the source was sent: the tenancy the fold
    /// resolved is in it. Where it is not, the source was asked for more rows than the principal can
    /// see and the filter that mattered ran here.
    /// </summary>
    private static void PushedPredicatesAppearInTheRemoteText(
        PolicyCase claims, string where, List<ConformanceFinding> findings)
    {
        foreach (var table in claims.Tables)
        {
            if (!table.RowPredicatePushed || table.RemoteQueryText is null)
            {
                continue;
            }

            foreach (var literal in table.ScopeLiterals)
            {
                if (!table.RemoteQueryText.Contains(literal, StringComparison.Ordinal))
                {
                    findings.Add(Fail(
                        $"{where}: {table.Table} row_predicate_pushed",
                        $"the scope literal {literal} in the source's own query",
                        table.RemoteQueryText));
                }
            }
        }
    }

    /// <summary>
    /// The two names for one label: the report says <c>PerRow</c> and the Arrow metadata
    /// <c>per_row</c>, which is the convention each side's readers expect. What is compared is the
    /// label, not its spelling.
    /// </summary>
    private static bool Same(string metadata, string reported) =>
        string.Equals(
            metadata.Replace("_", "", StringComparison.Ordinal),
            reported.Replace("_", "", StringComparison.Ordinal),
            StringComparison.OrdinalIgnoreCase);

    private static ConformanceFinding Fail(string subject, string declared, string observed) =>
        new()
        {
            Subject = subject,
            Outcome = ConformanceOutcome.Fail,
            Declared = declared,
            Observed = observed,
        };

    private static object?[] Column(IReadOnlyList<IReadOnlyList<object?>> rows, int index)
    {
        var values = new object?[rows.Count];
        for (var r = 0; r < rows.Count; r++)
        {
            values[r] = index < rows[r].Count ? rows[r][index] : null;
        }

        return values;
    }

    private static bool SameMultiset(object?[] left, object?[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var value in left)
        {
            var key = Key(value);
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        foreach (var value in right)
        {
            var key = Key(value);
            if (!counts.TryGetValue(key, out var n) || n == 0)
            {
                return false;
            }

            counts[key] = n - 1;
        }

        return true;
    }

    private static string Key(object? value) =>
        value is null ? "<null>" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";

    private static string Describe(object?[] executed, object?[] oracle) =>
        $"executed [{string.Join(", ", executed.Take(4).Select(Key))}"
        + (executed.Length > 4 ? ", …]" : "]")
        + $" against [{string.Join(", ", oracle.Take(4).Select(Key))}"
        + (oracle.Length > 4 ? ", …]" : "]");
}

/// <summary>One (statement, principal) pair, and everything the plan claimed about it (§7).</summary>
public sealed class PolicyCase
{
    /// <summary>The case's own name — a corpus query, a battery case id.</summary>
    public required string Name { get; init; }

    /// <summary>The principal it ran as.</summary>
    public required string Principal { get; init; }

    /// <summary>One per output column, in order.</summary>
    public required IReadOnlyList<PolicyColumn> Columns { get; init; }

    /// <summary>The rows the engine returned.</summary>
    public required IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; }

    /// <summary>
    /// The rows an independent evaluation of the descriptor disclosed, or null where none is
    /// available — a statement whose shape carries a group-size guard has no oracle, because
    /// suppression is a property of the statement rather than of a row.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<object?>>? DisclosedRows { get; init; }

    /// <summary>One per entitled table the plan reads.</summary>
    public required IReadOnlyList<PolicyTable> Tables { get; init; }

    /// <summary>
    /// Whether the statement is a plain scan of one table, so its row count may be compared with the
    /// table's. A join or an aggregate makes that comparison meaningless.
    /// </summary>
    public bool SingleTableScan { get; init; }
}

/// <summary>One output column, as the plan labelled it.</summary>
public sealed class PolicyColumn
{
    public required string Name { get; init; }

    /// <summary>The label the prepared query reported.</summary>
    public required string Reported { get; init; }

    /// <summary>The label the batch's <c>chalk.disclosure</c> metadata carried, or null for none.</summary>
    public string? ArrowMetadata { get; init; }
}

/// <summary>One entitled table, as the plan reported it.</summary>
public sealed class PolicyTable
{
    public required string Table { get; init; }

    /// <summary><c>None</c>, <c>Some</c> or <c>All</c>.</summary>
    public required string Visibility { get; init; }

    public required bool RowPredicatePushed { get; init; }

    /// <summary>The text the source was sent, where the plan pushed anything.</summary>
    public string? RemoteQueryText { get; init; }

    /// <summary>The literals this principal's scope folded to, which a pushed predicate must carry.</summary>
    public IReadOnlyList<string> ScopeLiterals { get; init; } = [];

    /// <summary>How many rows the table holds at all, for the <c>All</c> claim.</summary>
    public int? UnfilteredRowCount { get; init; }
}
