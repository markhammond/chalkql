namespace Chalk.Sample.Blotter.Domain;

/// <summary>
/// The brokerage's reference data, after the Tapline PivotTable sample's: clients trading linear
/// perpetuals against the house, in US dollars, each through its channels.
/// </summary>
public static class Reference
{
    /// <summary>The clients by how much they trade, most first, and the region covering each.</summary>
    public static readonly IReadOnlyList<(string Ref, string Name, string Region)> Clients =
    [
        ("CEDAR", "Cedar Trust", "Americas"), ("FERN", "Fern Partners", "EMEA"), ("ACORN", "Acorn Fund", "APAC"),
        ("JUNIPER", "Juniper LP", "APAC"), ("BIRCH", "Birch Capital", "EMEA"), ("HAZEL", "Hazel Fund", "APAC"),
        ("DAMSON", "Damson LP", "Americas"), ("LAUREL", "Laurel & Co", "EMEA"), ("ELDER", "Elder & Co", "Americas"),
        ("IVY", "Ivy Partners", "APAC"), ("GORSE", "Gorse Capital", "Americas"), ("KELP", "Kelp Trust", "APAC"),
    ];

    public static readonly IReadOnlyList<string> Regions = [.. new[] { "Americas", "EMEA", "APAC" }.Select(string.Intern)];

    public static readonly IReadOnlyList<string> Channels =
        [.. new[] { "FIX", "API", "GUI", "Mobile", "Voice" }.Select(string.Intern)];

    /// <summary>A client's channels by its rank from 1: the busiest on FIX and API, the middle on API and GUI, the rest on GUI, mobile and voice.</summary>
    public static (string Name, double Weight)[] ChannelsOf(int rank) => rank switch
    {
        <= 2 => [("FIX", 0.6), ("API", 0.3), ("Voice", 0.1)],
        <= 6 => [("API", 0.5), ("FIX", 0.2), ("GUI", 0.3)],
        _ => [("GUI", 0.5), ("Mobile", 0.3), ("Voice", 0.2)],
    };

    /// <summary>The markup a client pays on its channel, in basis points.</summary>
    public static double MarkupBps(string channel) =>
        channel switch { "FIX" => 6, "API" => 8, "GUI" => 20, "Mobile" => 25, _ => 35 };

    /// <summary>The assets, how popular each is, a lot of each in units, and a price to start from.</summary>
    public static readonly IReadOnlyList<(string Asset, double Weight, double Lot, double Price)> Assets =
    [
        ("ADA", 0.05, 2_000, 0.45), ("AVAX", 0.05, 20, 25), ("BTC", 0.30, 0.05, 65_000), ("DOGE", 0.06, 10_000, 0.12),
        ("ETH", 0.24, 0.5, 3_200), ("LINK", 0.05, 50, 14), ("SOL", 0.15, 10, 150), ("XRP", 0.10, 2_000, 0.55),
    ];

    /// <summary>The house's perpetual on each asset, in US dollars, which its clients trade.</summary>
    public static readonly IReadOnlyList<string> Instruments = [.. Assets.Select(a => string.Intern($"{a.Asset}/USD Perp"))];

    /// <summary>The asset an instrument is on, by its place in <see cref="Assets"/>.</summary>
    public static int AssetOf(string instrument) => InstrumentIndex[instrument];

    private static readonly Dictionary<string, int> InstrumentIndex =
        Instruments.Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i, StringComparer.Ordinal);

    public static readonly IReadOnlyList<string> Statuses =
        [.. new[] { "New", "Partial", "Filled", "Cancelled" }.Select(string.Intern)];

    public static readonly IReadOnlyList<string> Sides = [string.Intern("Buy"), string.Intern("Sell")];
}
