using System.Diagnostics.CodeAnalysis;
using Chalk.Client;
using Chalk.Sources;
using Chalk.Sources.Poco;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.TestKit;

/// <summary>
/// A symbol, keyed by a <c>Utf8String</c> (D145, <c>docs/design/24-zero-gc.md</c> §3 and §9).
/// </summary>
/// <remarks>
/// Three text-ish members on purpose: a non-nullable <c>Utf8String</c> key, a nullable one, and a
/// <c>ReadOnlyMemory&lt;byte&gt;</c> beside them that must still map to BINARY — the marker type is
/// what says "this is text", and the fixture is what proves the two do not run together.
/// </remarks>
public sealed record Utf8SymbolRow(
    Utf8String Symbol, Utf8String? Label, ReadOnlyMemory<byte> Payload, long Rank);

/// <summary>A row whose <c>symbol</c> is a foreign key into <see cref="Utf8SymbolRow"/>.</summary>
public sealed record Utf8QuoteRow(Utf8String Symbol, long Seq, double Price);

/// <summary>
/// The <c>utf8_symbols</c> fixture and its child table, in a source of their own (§9).
/// </summary>
/// <remarks>
/// <para>
/// A separate source and a separate catalog, deliberately: this step is below the plan, and adding a
/// table to the corpus catalog would move every recorded plan's schema. Nothing here is used by an
/// existing test.
/// </para>
/// <para>
/// The values are chosen to exercise what byte semantics actually decide: non-ASCII text whose UTF-8
/// order differs from a culture order, uppercase and lowercase neighbours (ordinal puts every
/// uppercase letter before every lowercase one), and strings on both sides of the twelve bytes
/// DuckDB inlines into a <c>duckdb_string_t</c>.
/// </para>
/// </remarks>
public sealed class Utf8Fixture
{
    public const string SourceId = "u8";

    public const string ContextId = "utf8";

    public const long Epoch = 1;

    /// <summary>
    /// The bases the symbol names are built from. Short ones stay inside DuckDB's twelve inlined
    /// bytes; the long ones and the non-ASCII ones do not.
    /// </summary>
    public static IReadOnlyList<string> Bases { get; } =
    [
        "ADA", "BTC", "ETH", "SOL", "XRP", "DOGE",
        "ÉTOILE", "日経", "München", "naïve",
        "A_VERY_LONG_SYMBOL_NAME", "another_long_symbol_name",
        "Zebra", "apple", "Apple", "zebra",
    ];

    private Utf8Fixture(
        PocoSource source, IReadOnlyList<Utf8SymbolRow> symbols, IReadOnlyList<Utf8QuoteRow> quotes)
    {
        Source = source;
        Symbols = symbols;
        Quotes = quotes;
        Catalog = new CatalogContext
        {
            ContextId = ContextId,
            Epoch = Epoch,
            Schemas = [source.DescribeSchema()],
        };
    }

    /// <summary>The shared fixture. Built once; nothing mutates it.</summary>
    public static Utf8Fixture Shared { get; } = Create();

    public PocoSource Source { get; }

    public CatalogContext Catalog { get; }

    public IReadOnlyList<Utf8SymbolRow> Symbols { get; }

    public IReadOnlyList<Utf8QuoteRow> Quotes { get; }

    public IReadOnlyList<ISourceRuntime> Sources => [Source];

    /// <summary>
    /// Builds the fixture. Verification (D17) runs over bytes, so a generator that broke the
    /// declared byte order would fail here.
    /// </summary>
    public static Utf8Fixture Create(int quotes = 20_000)
    {
        var symbols = SymbolRows();
        var quoteRows = QuoteRows(symbols, quotes);

        var source = Declare(new PocoSourceBuilder(SourceId, SourceId))
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("utf8_symbols", symbols, t => t
                .OrderedBy(s => s.Symbol)
                .UniqueKey(s => s.Symbol)
                // A permutation index whose key is not the declared collation, so a lookup over it
                // sorts and searches in byte order rather than reading the rows in place.
                .Index(s => s.Rank))
            .AddTable("utf8_quotes", quoteRows, t => t
                .OrderedBy(q => q.Symbol)
                .ThenBy(q => q.Seq)
                .UniqueKey(q => q.Symbol, q => q.Seq)
                .ForeignKey(q => q.Symbol).References<Utf8SymbolRow>(s => s.Symbol, verify: true))
            .Build();

        return new Utf8Fixture(source, symbols, quoteRows);
    }

    /// <summary>The symbols, in the byte order the table declares.</summary>
    public static IReadOnlyList<Utf8SymbolRow> SymbolRows()
    {
        var rows = new List<Utf8SymbolRow>(Bases.Count);
        for (var i = 0; i < Bases.Count; i++)
        {
            var name = Bases[i] + "USDT";

            // Every third label is NULL, so the nullable member is genuinely nullable. Written out
            // because a null literal beside a Utf8String binds to the implicit byte[] conversion
            // and would give the empty value rather than a NULL (V43).
            Utf8String? label = default;
            if (i % 3 != 2)
            {
                label = Utf8String.FromString(Bases[i]);
            }

            rows.Add(new Utf8SymbolRow(
                Utf8String.FromString(name),
                label,
                new ReadOnlyMemory<byte>([(byte)i, 0xFF, (byte)(i * 7 % 256)]),
                i));
        }

        // Byte order, which is what the table declares and what verification checks.
        rows.Sort(static (a, b) => a.Symbol.CompareTo(b.Symbol));
        return rows;
    }

    private static IReadOnlyList<Utf8QuoteRow> QuoteRows(
        IReadOnlyList<Utf8SymbolRow> symbols, int rows)
    {
        var perSymbol = Math.Max(1, rows / symbols.Count);
        var quotes = new List<Utf8QuoteRow>(perSymbol * symbols.Count);
        foreach (var symbol in symbols)
        {
            for (var seq = 0; seq < perSymbol; seq++)
            {
                // Deterministic and generator-free: the price is a function of the key, so two runs
                // of the fixture are the same rows.
                quotes.Add(new Utf8QuoteRow(
                    symbol.Symbol, seq, ((symbol.Rank * 31) + (seq % 97)) / 8.0));
            }
        }

        return quotes;
    }

    /// <summary>
    /// The two Tier 1 fixture functions of §9, declared in the catalog's own terms — STRING in,
    /// STRING or I64 out. The delegate's spelling is its own business (D146), and these are written
    /// in <see cref="Utf8String"/>.
    /// </summary>
    public static PocoSourceBuilder Declare(PocoSourceBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .AddFunction("upper_ascii", f => f
                .Scalar<string, string>("s")
                .Strict()
                .Client())
            .AddFunction("byte_length", f => f
                .Scalar<string, long>("s")
                .Strict()
                .Client());
    }

    /// <summary>
    /// The implementations. Neither allocates: <c>upper_ascii</c> writes into a per-thread scratch
    /// buffer and lends it, which the lifetime rule of <see cref="Utf8String"/> allows because the
    /// engine copies the bytes into the result column before the next call.
    /// </summary>
    [Experimental("CHALK001")]
    public static void Register(IFunctionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddScalar<Utf8String, Utf8String>("upper_ascii", UpperAscii);
        registry.AddScalar<Utf8String, long>("byte_length", static value => value.Length);
    }

    /// <summary>
    /// ASCII upper-casing over bytes: every other byte is left alone, so a multi-byte sequence
    /// passes through unchanged and the result is UTF-8 whenever the input was. Deliberately
    /// <em>not</em> a culture operation — that is what <see cref="Utf8String"/>'s doc comment says
    /// belongs on the existing string kernels.
    /// </summary>
    public static Utf8String UpperAscii(Utf8String value)
    {
        var source = value.AsSpan();
        var scratch = _scratch;
        if (scratch is null || scratch.Length < source.Length)
        {
            // Grown once to the widest value the scan meets and then reused, which is what keeps the
            // delegate off the per-row allocation budget.
            scratch = _scratch = new byte[Math.Max(64, source.Length)];
        }

        var target = scratch.AsSpan(0, source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            var b = source[i];
            target[i] = b is >= (byte)'a' and <= (byte)'z' ? (byte)(b - 32) : b;
        }

        return Utf8String.FromBytes(scratch.AsMemory(0, source.Length));
    }

    [ThreadStatic]
    private static byte[]? _scratch;
}
