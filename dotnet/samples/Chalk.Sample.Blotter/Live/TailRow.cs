namespace Chalk.Sample.Blotter.Live;

/// <summary>
/// One trade in the live tail: its latest version from the feed, beside what the current snapshot
/// holds for it. The tail keeps only trades whose latest version the snapshot does not have.
/// </summary>
/// <remarks>
/// The snapshot's columns are here so that the live scopes can test a filter against both versions
/// in one statement: a trade whose snapshot version passes the filter has a row in the snapshot's
/// pages that the tail now overrides, wherever its new version lands, in the filter or out of it.
/// <see cref="BaseVersion"/> is zero for a trade the snapshot does not hold at all.
/// </remarks>
public sealed record TailRow
{
    public required long TradeId { get; init; }

    public required int Version { get; init; }

    public required DateTime TradeTime { get; init; }

    public required string Client { get; init; }

    public required string Symbol { get; init; }

    public required string Side { get; init; }

    public required string Status { get; init; }

    public required int BaseVersion { get; init; }

    public required DateTime BaseTradeTime { get; init; }

    public required string BaseClient { get; init; }

    public required string BaseSymbol { get; init; }

    public required string BaseSide { get; init; }

    public required string BaseStatus { get; init; }

    /// <summary>A rough size in memory, for the budget, counted twice: once here, once in ChalkQL's copy.</summary>
    public const int Bytes = 2 * 112;

    public static TailRow Of(TradeVersion live, TradeVersion? snapshot) => new()
    {
        TradeId = live.TradeId,
        Version = live.Version,
        TradeTime = live.TradeTime,
        Client = live.Client,
        Symbol = live.Symbol,
        Side = live.Side,
        Status = live.Status,
        BaseVersion = snapshot?.Version ?? 0,
        BaseTradeTime = snapshot?.TradeTime ?? live.TradeTime,
        BaseClient = snapshot?.Client ?? live.Client,
        BaseSymbol = snapshot?.Symbol ?? live.Symbol,
        BaseSide = snapshot?.Side ?? live.Side,
        BaseStatus = snapshot?.Status ?? live.Status,
    };
}
