using System.Globalization;
using System.Text;
using Chalk.Client;
using Chalk.Sources;
using Xunit;

namespace Chalk.TestKit;

/// <summary>
/// D252's one detector (<c>docs/design/32-adversarial-entitlements.md</c> §2): everything a
/// principal receives, scanned for a canary the oracle does not disclose to them.
/// </summary>
/// <remarks>
/// <para>
/// A principal's <b>allowed</b> tokens are the canaries in the values <see cref="TenancyOracle"/>
/// discloses to them, and every other canary in the fixture is forbidden — which makes the detector
/// the oracle read a second way, over channels an oracle comparison cannot reach. "Receive" is
/// broad on purpose: the result rows, the plan text, the disclosure report, a refusal or an
/// exception message, the SQL the engine sent to a source, and the statistics record a host may log.
/// </para>
/// <para>
/// The numeric canary is checked by value rather than by output position, and the reason is worth
/// stating: an aggregate over rows whose <c>amount</c> the principal may not see is NULL by
/// construction — the leaf emits a placeholder for them — and an aggregate over the rows they may
/// see is made of values that are theirs already. So a forbidden amount in a cell is a raw value
/// that escaped, whatever the statement did, and the amounts are spaced so that no mean of two
/// allowed ones lands on a forbidden third (<see cref="TenancyCanaries.Amounts"/>).
/// </para>
/// <para>
/// It fails through <c>Assert.Fail</c> naming the principal, the token, where it appeared and the
/// statement, because "what leaked, to whom, and through which channel" is the whole of the report
/// a run of the adversarial battery has to make.
/// </para>
/// </remarks>
public sealed class LeakDetector
{
    private readonly string _principal;
    private readonly IReadOnlySet<string> _forbiddenTokens;
    private readonly IReadOnlyList<long> _forbiddenAmounts;

    private LeakDetector(
        string principal, IReadOnlySet<string> tokens, IReadOnlyList<long> amounts)
    {
        _principal = principal;
        _forbiddenTokens = tokens;
        _forbiddenAmounts = amounts;
    }

    /// <summary>Every canary the fixture's own rows carry: the universe a principal's is taken from.</summary>
    public static IReadOnlySet<string> EveryToken { get; } = ReadEveryToken();

    /// <summary>What this principal may not receive, by any channel.</summary>
    public static LeakDetector For(
        string principal, RequestContext context, bool creatorSeesFull = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(principal);
        ArgumentNullException.ThrowIfNull(context);

        var disclosed = TenancyOracle.DiscloseValues(context, creatorSeesFull);
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in disclosed.Strings)
        {
            foreach (var token in TenancyCanaries.Extract(value))
            {
                allowed.Add(token);
            }
        }

        var allowedAmounts = disclosed.Amounts.ToHashSet();
        return new LeakDetector(
            principal,
            EveryToken.Where(t => !allowed.Contains(t)).ToHashSet(StringComparer.Ordinal),
            [.. TenancyCanaries.Amounts.Where(a => !allowedAmounts.Contains(a))]);
    }

    /// <summary>
    /// The one assertion. Everything the run handed the principal goes through here, and anything a
    /// caller leaves unset is simply nothing to scan.
    /// </summary>
    public void Inspect(LeakScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);

        for (var i = 0; i < scan.Rows.Count; i++)
        {
            var cells = scan.Rows[i].Split('|');
            for (var c = 0; c < cells.Length; c++)
            {
                var column = c < scan.Columns.Count ? scan.Columns[c] : $"column {c}";
                Scan(scan, $"row {i + 1}, {column}", cells[c]);
            }
        }

        Scan(scan, "the plan text", scan.PlanText);
        Scan(scan, "the disclosure report", scan.Report);
        Scan(scan, "the refusal message", scan.Refusal);
        Scan(scan, "the statistics record", scan.Statistics);
        for (var i = 0; i < scan.RemoteSql.Count; i++)
        {
            Scan(scan, $"the SQL sent to a source ({i + 1})", scan.RemoteSql[i]);
        }
    }

    /// <summary>The statistics record as text, which is what a host would log of one execution.</summary>
    public static string Statistics(ExecutionStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        var text = new StringBuilder(stats.ToString());
        foreach (var source in stats.SourceFetches.Keys.Order(StringComparer.Ordinal))
        {
            text.Append(' ').Append(source);
        }

        foreach (var path in stats.SourcePaths)
        {
            text.Append(' ').Append(path.Key).Append('=').Append(path.Value);
        }

        return text.ToString();
    }

    private void Scan(LeakScan scan, string where, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (var token in TenancyCanaries.Extract(text))
        {
            if (_forbiddenTokens.Contains(token))
            {
                Assert.Fail(
                    $"{scan.Statement}: {_principal} received the canary {token} in {where}, which "
                    + "the oracle does not disclose to them. The value is: "
                    + Quote(text));
            }
        }

        foreach (var amount in _forbiddenAmounts)
        {
            if (Holds(text, amount))
            {
                Assert.Fail(
                    $"{scan.Statement}: {_principal} received the canary amount {amount} in {where}, "
                    + "which the oracle does not disclose to them. The value is: "
                    + Quote(text));
            }
        }
    }

    /// <summary>Whether the text holds this number as a number rather than inside a longer one.</summary>
    private static bool Holds(string text, long amount)
    {
        var digits = amount.ToString(CultureInfo.InvariantCulture);
        var at = text.IndexOf(digits, StringComparison.Ordinal);
        while (at >= 0)
        {
            var before = at == 0 || !char.IsAsciiDigit(text[at - 1]);
            var end = at + digits.Length;
            var after = end == text.Length || !char.IsAsciiDigit(text[end]);
            if (before && after)
            {
                return true;
            }

            at = text.IndexOf(digits, at + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static string Quote(string text) =>
        text.Length <= 400 ? text : text[..400] + "…";

    private static IReadOnlySet<string> ReadEveryToken()
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        void Take(string? value)
        {
            foreach (var token in TenancyCanaries.Extract(value))
            {
                tokens.Add(token);
            }
        }

        foreach (var member in TenancyFixture.Members)
        {
            Take(member.FirstName);
            Take(member.LastName);
            Take(member.NationalId);
            Take(member.Postcode);
        }

        foreach (var order in TenancyFixture.Orders)
        {
            Take(order.Note);
        }

        foreach (var note in TenancyFixture.Notes)
        {
            Take(note.Body);
        }

        foreach (var message in TenancyFixture.Messages)
        {
            Take(message.Content);
        }

        foreach (var attachment in TenancyFixture.Attachments)
        {
            Take(attachment.Name);
        }

        foreach (var invite in TenancyFixture.Invites)
        {
            Take(invite.Target);
        }

        return tokens;
    }
}

/// <summary>Everything one run of one statement handed one principal (D252).</summary>
public sealed class LeakScan
{
    /// <summary>What the failure names: the statement, and which configuration ran it.</summary>
    public required string Statement { get; init; }

    /// <summary>The result rows, each rendered as its cells joined by <c>'|'</c>.</summary>
    public IReadOnlyList<string> Rows { get; init; } = [];

    /// <summary>The output column names, so a failure names the column and not only the row.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary><c>PrepareOptions.IncludePlanText</c>'s text, where the run asked for it.</summary>
    public string? PlanText { get; init; }

    /// <summary>The disclosure report, as the corpus renders it.</summary>
    public string? Report { get; init; }

    /// <summary>A refusal or an exception message, where the run got one instead of rows.</summary>
    public string? Refusal { get; init; }

    /// <summary>The query text of every <c>RemoteQuery</c> the plan carries.</summary>
    public IReadOnlyList<string> RemoteSql { get; init; } = [];

    /// <summary>The statistics record, as <see cref="LeakDetector.Statistics"/> renders it.</summary>
    public string? Statistics { get; init; }
}
