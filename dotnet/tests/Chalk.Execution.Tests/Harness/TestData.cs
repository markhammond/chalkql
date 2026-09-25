using Chalk.Catalog;

namespace Chalk.Execution.Tests.Harness;

/// <summary>
/// The small hand-built tables the kernel and operator tests run over. Deliberately tiny and explicit:
/// every NULL, NaN and boundary value is visible in the literal.
/// </summary>
internal static class TestData
{
    /// <summary>Nine rows covering NULLs on both sides of every kernel, plus a NaN.</summary>
    public static TestTable Numbers { get; } = new()
    {
        Name = "numbers",
        Columns =
        [
            ("i32", ChalkType.Int32(nullable: true)),
            ("i64", ChalkType.Int64(nullable: true)),
            ("f64", ChalkType.Float64(nullable: true)),
            ("f32", ChalkType.Float32(nullable: true)),
            ("dec", ChalkType.Decimal(18, 4, nullable: true)),
            ("s", ChalkType.String(nullable: true)),
            ("b", ChalkType.Bool(nullable: true)),
        ],
        Rows =
        [
            [1L, 10L, 1.5d, 1.5f, 1.5m, "alpha", true],
            [2L, 20L, -2.5d, -2.5f, -2.5m, "beta", false],
            [null, 30L, 3.5d, 3.5f, 3.5m, "gamma", null],
            [4L, null, double.NaN, float.NaN, 0.0m, null, true],
            [5L, 50L, null, null, null, "delta", false],
            [-6L, -60L, 0d, 0f, -0.0005m, "Delta", null],
            [7L, 70L, 1e308d, 1e38f, 12345.6789m, "épée", true],
            [0L, 0L, -0d, -0f, 0m, string.Empty, false],
            [9L, 90L, 2.5d, 2.5f, 2.5m, "zzz", true],
        ],
    };

    /// <summary>Two rows of every remaining kind, for the layouts the numeric table does not reach.</summary>
    public static TestTable Assorted { get; } = new()
    {
        Name = "assorted",
        Columns =
        [
            ("d", ChalkType.Date(nullable: true)),
            ("ts", ChalkType.Timestamp(6, nullable: true)),
            ("tz", ChalkType.TimestampTz(3, nullable: true)),
            ("t", ChalkType.Time(6, nullable: true)),
            ("bin", ChalkType.Binary(nullable: true)),
            ("uuid", ChalkType.Uuid(nullable: true)),
            ("iv", ChalkType.IntervalDay(nullable: true)),
        ],
        Rows =
        [
            // 2026-01-03T04:05:06.000007, and the same instant at millisecond precision.
            [20456L, 1767413106000007L, 1767413106000L, 14706000000L, new byte[] { 1, 2, 3 }, Guid16(1), 90L],
            [-1L, -1L, -1L, 0L, Array.Empty<byte>(), Guid16(2), -90L],
            [null, null, null, null, null, null, null],
        ],
    };

    /// <summary>Grouping fodder: three symbols, NULL keys, NULL measures, and a filterable flag.</summary>
    public static TestTable Trades { get; } = new()
    {
        Name = "trades",
        Columns =
        [
            ("symbol", ChalkType.String(nullable: true)),
            ("qty", ChalkType.Int64(nullable: true)),
            ("price", ChalkType.Float64(nullable: true)),
            ("live", ChalkType.Bool(nullable: false)),
        ],
        Rows =
        [
            ["AAA", 1L, 1.0d, true],
            ["BBB", 2L, 2.0d, false],
            ["AAA", 3L, null, true],
            [null, 4L, 4.0d, true],
            ["BBB", null, 5.0d, true],
            [null, 6L, 6.0d, false],
            ["AAA", 1L, 7.0d, true],
        ],
    };

    /// <summary>A wide, single-typed table for the batch-boundary and allocation tests.</summary>
    public static TestTable Series(int rows)
    {
        var data = new List<object?[]>(rows);
        for (var i = 0; i < rows; i++)
        {
            data.Add([(long)i, i % 7 == 0 ? null : (double)i, "row-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        return new TestTable
        {
            Name = "series",
            Columns =
            [
                ("n", ChalkType.Int64()),
                ("v", ChalkType.Float64(nullable: true)),
                ("label", ChalkType.String()),
            ],
            Rows = data,
        };
    }

    /// <summary>An empty table with the same shape as <see cref="Numbers"/>.</summary>
    public static TestTable Empty { get; } = new()
    {
        Name = "empty",
        Columns = Numbers.Columns,
        Rows = [],
    };

    public static TestSource Source(params TestTable[] tables) => new("mem", "main", tables);

    /// <summary>
    /// The window fixture, already in the order a window's input arrives in — (symbol ASC NULLS
    /// LAST, ts ASC NULLS LAST) — and carrying every shape §7 asks a window operator about: a
    /// partition with a tie on the order key, a single-row partition, a partition whose values are
    /// all NULL, a partition whose order keys are all NULL, and a NULL partition key.
    /// </summary>
    public static TestTable Windowed { get; } = new()
    {
        Name = "windowed",
        Columns =
        [
            ("symbol", ChalkType.String(nullable: true)),
            ("ts", ChalkType.Int64(nullable: true)),
            ("value", ChalkType.Int64(nullable: true)),
            ("price", ChalkType.Float64(nullable: true)),
        ],
        Rows =
        [
            ["AAA", 1L, 10L, 1.0d],
            ["AAA", 2L, null, 2.0d],
            ["AAA", 3L, 30L, null],
            ["AAA", 3L, 40L, 4.0d],
            ["BBB", 1L, 5L, 5.0d],
            ["CCC", 1L, null, null],
            ["CCC", 2L, null, null],
            ["DDD", null, 1L, 1.5d],
            ["DDD", null, 2L, 2.5d],
            [null, 1L, 7L, 7.0d],
            [null, 2L, 8L, 8.0d],
        ],
    };

    private static byte[] Guid16(byte seed)
    {
        var bytes = new byte[16];
        bytes[15] = seed;
        return bytes;
    }

    /// <summary>Rows with duplicate sort keys and NULLs, for the ordering operators.</summary>
    public static TestTable Sortable { get; } = new()
    {
        Name = "sortable",
        Columns = [("n", ChalkType.Int64(nullable: true)), ("g", ChalkType.String(nullable: true))],
        Rows =
        [
            [3L, "b"],
            [null, "b"],
            [-5L, "a"],
            [7L, "a"],
            [3L, "c"],
            [null, "a"],
        ],
    };

    /// <summary>Text and numbers that exercise the cast matrix, including the values that cannot convert.</summary>
    public static TestTable Casts { get; } = new()
    {
        Name = "casts",
        Columns =
        [
            ("f", ChalkType.Float64(nullable: true)),
            ("num", ChalkType.String(nullable: true)),
            ("flag", ChalkType.String(nullable: true)),
            ("date", ChalkType.String(nullable: true)),
            ("stamp", ChalkType.String(nullable: true)),
        ],
        Rows =
        [
            [0.25d, "  42  ", "TRUE", "2026-01-03", "2026-01-03T04:05:06"],
            [0.35d, "-7", "false", "1969-12-31", "1969-12-31T23:59:59"],
            [-0.25d, "1.5", "maybe", "not-a-date", "not-a-timestamp"],
            [-0.35d, "nope", null, "nope", "nope"],
            [null, null, null, null, null],
        ],
    };

    /// <summary>One instant either side of the epoch, plus a NULL row, in three temporal kinds.</summary>
    public static TestTable Instants { get; } = new()
    {
        Name = "instants",
        Columns =
        [
            ("d", ChalkType.Date(nullable: true)),
            ("ts", ChalkType.Timestamp(6, nullable: true)),
            ("tz", ChalkType.TimestampTz(3, nullable: true)),
        ],
        Rows =
        [
            [20456L, 1_767_413_106_000_007L, 1_767_413_106_000L],
            [-1L, -1L, -1L],
            [null, null, null],
        ],
    };

    /// <summary>Seven consecutive days from a Sunday, so EXTRACT(DOW) can be pinned end to end.</summary>
    public static TestTable Week { get; } = new()
    {
        Name = "week",
        Columns = [("d", ChalkType.Date())],

        // 2026-01-04 is a Sunday: day number 20457 counted from 1970-01-01.
        Rows = [[20457L], [20458L], [20459L], [20460L], [20461L], [20462L], [20463L]],
    };

    /// <summary>Strings that exercise code points, the empty string, a wildcard character and NULL.</summary>
    public static TestTable Strings { get; } = new()
    {
        Name = "strings",
        Columns = [("s", ChalkType.String(nullable: true))],
        Rows =
        [
            ["abcdef"],
            [string.Empty],
            ["\u00e9p\u00e9e"],
            ["\U0001F600x"],
            ["abc100%"],
            [null],
        ],
    };

    /// <summary>
    /// Labels that differ only in case, for the collation theories of §5 F: under Chalk's
    /// code-point comparison every upper-case letter sorts below every lower-case one, and under
    /// any case-folding collation they interleave instead.
    /// </summary>
    public static TestTable Collated { get; } = new()
    {
        Name = "collated",
        Columns = [("label", ChalkType.String(nullable: true))],
        Rows = [["A"], ["C"], ["D"], ["E"], ["a"], ["c"], ["e"], [null]],
    };

    /// <summary>The nine combinations of TRUE, FALSE and NULL, for Kleene logic.</summary>
    public static TestTable Logic { get; } = new()
    {
        Name = "logic",
        Columns = [("a", ChalkType.Bool(nullable: true)), ("b", ChalkType.Bool(nullable: true))],
        Rows =
        [
            [true, true], [true, false], [true, null],
            [false, true], [false, false], [false, null],
            [null, true], [null, false], [null, null],
        ],
    };

    /// <summary>The boundaries the checked kernels have to notice.</summary>
    public static TestTable Extremes { get; } = new()
    {
        Name = "extremes",
        Columns =
        [
            ("big", ChalkType.Int64(nullable: true)),
            ("small", ChalkType.Int64(nullable: true)),
        ],
        Rows =
        [
            [long.MaxValue, long.MinValue],
            [1L, -1L],
            [null, null],
        ],
    };

    /// <summary>The largest value a DECIMAL at scale 28 can hold — <c>decimal.MaxValue</c>,
    /// rescaled.</summary>
    public const decimal LargestAtScale28 = 7.9228162514264337593543950335m;

    /// <summary>The smallest positive one.</summary>
    public const decimal SmallestAtScale28 = 0.0000000000000000000000000001m;

    /// <summary>
    /// A tie at the 29th significant digit whose last kept digit is odd: away from zero rounds it up
    /// to ...568, as half-even would have.
    /// </summary>
    public const decimal TieUpAtScale28 = 0.1234567890123456789012345675m;

    /// <summary>
    /// And one whose last kept digit is even: away from zero rounds it up to ...569, where half-even
    /// would have gone down to ...568 — the pair is what tells the rules apart (D306).
    /// </summary>
    public const decimal TieDownAtScale28 = 0.1234567890123456789012345685m;

    /// <summary>10^20: past a 64-bit integer, nowhere near a 128-bit decimal.</summary>
    public const decimal TenToTheTwentieth = 100_000_000_000_000_000_000m;

    /// <summary>
    /// Midpoints on both sides of zero for the half-even rounding rule, and beside them the
    /// boundaries of the 128-bit decimal (§5 C, D168).
    /// </summary>
    /// <remarks>
    /// Three columns rather than three tables because a boundary is only interesting at a declared
    /// precision and scale, and these are the three that matter: the everyday one the midpoints
    /// live at, the widest whole number a <see cref="decimal"/> can carry, and the finest fraction.
    /// <c>decimal.MaxValue</c> and <c>MinValue</c> are 29 significant digits, which is one more
    /// than the 28 a DECIMAL's default precision allows and the reason <c>whole</c> declares 29.
    /// <para>
    /// <b>V56.</b> <c>fine</c> holds the largest value at scale 28 but <em>not</em> its negation,
    /// and that is deliberate. A DECIMAL(29,28) is one unit in the last place wider than a .NET
    /// <see cref="decimal"/> at each end: the executor computes
    /// -7.9228162514264337593543950335 - 1e-28 correctly and stores it, and it is the read back
    /// into a <see cref="decimal"/> that overflows. So the negative extreme is exercised in
    /// <c>whole</c>, where <c>decimal.MinValue</c> is inside both, and <c>fine</c>'s second row is
    /// the finest negative instead — which keeps every arithmetic theory over the column
    /// expressible in the language the assertions are written in.
    /// </para>
    /// </remarks>
    public static TestTable Rounding { get; } = new()
    {
        Name = "rounding",
        Columns =
        [
            ("d", ChalkType.Decimal(18, 3, nullable: true)),
            ("whole", ChalkType.Decimal(29, 0, nullable: true)),
            ("fine", ChalkType.Decimal(29, 28, nullable: true)),
        ],
        Rows =
        [
            [0.125m, decimal.MaxValue, LargestAtScale28],
            [0.135m, decimal.MinValue, -SmallestAtScale28],
            [-0.125m, TenToTheTwentieth, SmallestAtScale28],
            [-0.135m, -TenToTheTwentieth, TieUpAtScale28],
            [null, null, TieDownAtScale28],
        ],
    };

    /// <summary>A column that is NULL where a naive kernel would read a zero out of the gap.</summary>
    public static TestTable NullableDivisors { get; } = new()
    {
        Name = "divisors",
        Columns = [("d", ChalkType.Int64(nullable: true))],
        Rows = [[null], [5L], [null]],
    };

    /// <summary>Exactly one row, for the single-row case every kernel test owes.</summary>
    public static TestTable SingleRow { get; } = new()
    {
        Name = "single",
        Columns = [("n", ChalkType.Int64()), ("s", ChalkType.String())],
        Rows = [[7L, "only"]],
    };

    /// <summary>The batch sizes every kernel and operator test runs at (05-testing.md §1).</summary>
    public static Xunit.TheoryData<int> BatchSizeData
    {
        get
        {
            var data = new Xunit.TheoryData<int>();
            foreach (var size in Runner.BatchSizes)
            {
                data.Add(size);
            }

            return data;
        }
    }
}
