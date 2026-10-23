using System.Globalization;

namespace Chalk.Sample.Blotter.Market;

/// <summary>The brokerage's reference data: who trades, what, and where.</summary>
public static class Reference
{
    public static readonly IReadOnlyList<string> Clients =
        [.. Enumerable.Range(1, 400).Select(i => string.Intern("C" + i.ToString("0000", CultureInfo.InvariantCulture)))];

    public static readonly IReadOnlyList<string> Symbols =
    [
        .. new[]
        {
            "AAPL", "MSFT", "NVDA", "AMZN", "GOOGL", "META", "TSLA", "AVGO", "JPM", "V", "MA", "UNH", "XOM", "LLY",
            "JNJ", "PG", "HD", "COST", "MRK", "ABBV", "CVX", "PEP", "KO", "ADBE", "CRM", "NFLX", "AMD", "INTC",
            "CSCO", "ORCL", "WMT", "BAC", "WFC", "GS", "MS", "C", "BLK", "SCHW", "AXP", "IBM", "QCOM", "TXN",
            "AMAT", "MU", "LRCX", "KLAC", "SNPS", "CDNS", "PANW", "NOW", "SHOP", "UBER", "ABNB", "PYPL", "SQ",
            "PLTR", "SNOW", "CRWD", "DDOG", "ZS", "NET", "MDB", "TEAM", "WDAY", "INTU", "ISRG", "TMO", "DHR",
            "ABT", "BMY", "AMGN", "GILD", "VRTX", "REGN", "MRNA", "PFE", "CAT", "DE", "HON", "GE", "BA", "LMT",
            "RTX", "NOC", "UPS", "FDX", "UNP", "CSX", "NSC", "DAL", "UAL", "AAL", "LUV", "MAR", "HLT", "BKNG",
            "EXPE", "SBUX", "MCD", "CMG", "NKE", "LULU", "TGT", "LOW", "TJX", "ROST", "DG", "DLTR", "F", "GM",
        }.Select(string.Intern),
    ];

    public static readonly IReadOnlyList<string> Venues =
        [.. new[] { "XNAS", "XNYS", "ARCX", "BATS", "EDGX", "IEXG", "MEMX", "DARK" }.Select(string.Intern)];

    public static readonly IReadOnlyList<string> Statuses =
        [.. new[] { "New", "PartiallyFilled", "Filled", "Cancelled" }.Select(string.Intern)];

    public static readonly IReadOnlyList<string> Sides = [string.Intern("Buy"), string.Intern("Sell")];
}
