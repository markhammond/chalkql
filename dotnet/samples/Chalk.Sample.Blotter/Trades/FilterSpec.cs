using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Chalk.Sample.Blotter.Trades;

/// <summary>
/// What the grid shows: equality and <c>IN</c> filters, and a range of trade times. An empty list
/// filters nothing. Every value is bound as a parameter, never written into the SQL, so one shape of
/// filter is one prepared statement whatever values it holds.
/// </summary>
public sealed record FilterSpec
{
    public static FilterSpec None { get; } = new();

    public ImmutableArray<string> Clients { get; init; } = [];

    public ImmutableArray<string> Symbols { get; init; } = [];

    public ImmutableArray<string> Statuses { get; init; } = [];

    /// <summary><c>Buy</c>, <c>Sell</c>, or null for both.</summary>
    public string? Side { get; init; }

    /// <summary>Traded at or after, when set.</summary>
    public DateTime? From { get; init; }

    /// <summary>Traded before, when set.</summary>
    public DateTime? To { get; init; }

    /// <summary>Canonical, for sharing a live scope between pages that filter alike.</summary>
    public string Name
    {
        get
        {
            var name = new StringBuilder();
            Append(name, "client", Clients);
            Append(name, "symbol", Symbols);
            Append(name, "status", Statuses);
            if (Side is not null)
            {
                name.Append("side=").Append(Side).Append(';');
            }

            if (From is { } from)
            {
                name.Append("from=").Append(from.Ticks.ToString(CultureInfo.InvariantCulture)).Append(';');
            }

            if (To is { } to)
            {
                name.Append("to=").Append(to.Ticks.ToString(CultureInfo.InvariantCulture)).Append(';');
            }

            return name.Length == 0 ? "all" : name.ToString();
        }
    }

    /// <summary>The same filter with its lists sorted and its times truncated, so equal filters have one name.</summary>
    public FilterSpec Canonical() => this with
    {
        Clients = [.. Clients.Distinct().Order(StringComparer.Ordinal)],
        Symbols = [.. Symbols.Distinct().Order(StringComparer.Ordinal)],
        Statuses = [.. Statuses.Distinct().Order(StringComparer.Ordinal)],
        From = From is { } from ? TradeVersion.Micros(from) : null,
        To = To is { } to ? TradeVersion.Micros(to) : null,
    };

    /// <summary>
    /// The conditions, joined by <c>AND</c>, over columns named with <paramref name="prefix"/>; <c>TRUE</c>
    /// when there are none. Two prefixes share one set of parameters: the live scope tests a trade's
    /// version in the tail and its version in the snapshot against the same values.
    /// </summary>
    public string Where(string prefix = "")
    {
        var conditions = new List<string>();
        if (!Clients.IsEmpty)
        {
            conditions.Add($"{prefix}client IN @clients");
        }

        if (!Symbols.IsEmpty)
        {
            conditions.Add($"{prefix}symbol IN @symbols");
        }

        if (!Statuses.IsEmpty)
        {
            conditions.Add($"{prefix}status IN @statuses");
        }

        if (Side is not null)
        {
            conditions.Add($"{prefix}side = @side");
        }

        if (From is not null)
        {
            conditions.Add($"{prefix}trade_time >= @from");
        }

        if (To is not null)
        {
            conditions.Add($"{prefix}trade_time < @to");
        }

        return conditions.Count == 0 ? "TRUE" : string.Join(" AND ", conditions);
    }

    /// <summary>The values <see cref="Where"/> names.</summary>
    public Dictionary<string, object?> Parameters()
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!Clients.IsEmpty)
        {
            values["clients"] = Clients.ToArray();
        }

        if (!Symbols.IsEmpty)
        {
            values["symbols"] = Symbols.ToArray();
        }

        if (!Statuses.IsEmpty)
        {
            values["statuses"] = Statuses.ToArray();
        }

        if (Side is not null)
        {
            values["side"] = Side;
        }

        if (From is { } from)
        {
            values["from"] = from;
        }

        if (To is { } to)
        {
            values["to"] = to;
        }

        return values;
    }

    /// <summary>The same test in the host: the oracle the tests check both databases' answers against.</summary>
    public bool Matches(TradeVersion trade) =>
        (Clients.IsEmpty || Clients.Contains(trade.Client))
        && (Symbols.IsEmpty || Symbols.Contains(trade.Symbol))
        && (Statuses.IsEmpty || Statuses.Contains(trade.Status))
        && (Side is null || Side == trade.Side)
        && (From is not { } from || trade.TradeTime >= from)
        && (To is not { } to || trade.TradeTime < to);

    private static void Append(StringBuilder name, string field, ImmutableArray<string> values)
    {
        if (!values.IsEmpty)
        {
            name.Append(field).Append(" in ").AppendJoin('|', values).Append(';');
        }
    }
}
