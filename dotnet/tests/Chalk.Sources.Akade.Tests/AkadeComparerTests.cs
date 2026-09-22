using Akade.IndexedSet;
using Chalk.Catalog;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using IndexKind = Chalk.Ir.IndexKind;
using SortDirection = Chalk.Ir.SortDirection;

namespace Chalk.Sources.Akade.Tests;

/// <summary>
/// D281 — the comparer a host declares, the key types that declaration opens up, and the order the
/// package ships.
/// </summary>
public sealed class AkadeComparerTests
{
    private sealed record Row<TKey>(int Id, TKey Key);

    /// <summary>
    /// The definition of Chalk's order, as the engine itself applies it, against the comparer this
    /// package hands a host to build an Akade index with — value by value, both ways.
    /// </summary>
    /// <remarks>
    /// STRING is compared as a STRING column holds it. <see cref="SourceValueOrder"/> compares two
    /// CLR strings by UTF-16 code unit and a <see cref="Utf8String"/> by byte, and those two orders
    /// disagree across the surrogate range; the byte order is the one an uncollated STRING column
    /// sorts by, so it is the one the package's comparer produces.
    /// </remarks>
    [Fact]
    public void The_shipped_comparers_are_the_engines_own_order()
    {
        Agrees<byte>([0, 1, 200, byte.MaxValue]);
        Agrees<short>([short.MinValue, -1, 0, 7, short.MaxValue]);
        Agrees<int>([int.MinValue, -3, 0, 42, int.MaxValue]);
        Agrees<long>([long.MinValue, -3L, 0L, 42L, long.MaxValue]);
        Agrees<uint>([0u, 1u, uint.MaxValue]);
        Agrees<ulong>([0ul, 1ul, ulong.MaxValue]);
        Agrees<decimal>([decimal.MinValue, -1.5m, 0m, 1.5m, decimal.MaxValue]);
        Agrees<DateTime>([DateTime.MinValue, new DateTime(2026, 1, 1), DateTime.MaxValue]);
        Agrees<DateTimeOffset>(
        [
            DateTimeOffset.MinValue,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DateTimeOffset.MaxValue,
        ]);
        Agrees<DateOnly>([DateOnly.MinValue, new DateOnly(2026, 1, 1), DateOnly.MaxValue]);
        Agrees<TimeOnly>([TimeOnly.MinValue, new TimeOnly(12, 0), TimeOnly.MaxValue]);
        Agrees<TimeSpan>([TimeSpan.MinValue, TimeSpan.Zero, TimeSpan.FromDays(1), TimeSpan.MaxValue]);

        // NaN above every number, which is where Chalk puts it and where Comparer<T>.Default
        // does not.
        Agrees<float>([float.NegativeInfinity, -1f, 0f, 1f, float.PositiveInfinity, float.NaN]);
        Agrees<double>([double.NegativeInfinity, -1d, 0d, 1d, double.PositiveInfinity, double.NaN]);

        // Code points, including one above the basic plane and one just below the surrogate range.
        AgreesOnText(
        [
            "",
            "A",
            "Z",
            "a",
            "\u00E9",
            "\uD7FF",
            "\uE000",
            "\uFFFD",
            "\U00010000",
            "\U0010FFFF",
        ]);
    }

    private static void Agrees<TKey>(TKey[] values)
        where TKey : notnull
    {
        var ascending = ChalkComparers.For<TKey>();
        var descending = ChalkComparers.For<TKey>(descending: true);

        foreach (var left in values)
        {
            foreach (var right in values)
            {
                var expected = Math.Sign(SourceValueOrder.Compare(left, right, SortDirection.AscNullsLast));

                Assert.Equal(expected, Math.Sign(ascending.Compare(left, right)));
                Assert.Equal(-expected, Math.Sign(descending.Compare(left, right)));
            }
        }
    }

    private static void AgreesOnText(string[] values)
    {
        var ascending = ChalkComparers.For<string>();
        var descending = ChalkComparers.For<string>(descending: true);
        var utf8 = ChalkComparers.For<Utf8String>();

        foreach (var left in values)
        {
            foreach (var right in values)
            {
                var expected = Math.Sign(SourceValueOrder.Compare(
                    Utf8String.FromString(left),
                    Utf8String.FromString(right),
                    SortDirection.AscNullsLast));

                Assert.Equal(expected, Math.Sign(ascending.Compare(left, right)));
                Assert.Equal(-expected, Math.Sign(descending.Compare(left, right)));
                Assert.Equal(
                    expected,
                    Math.Sign(utf8.Compare(Utf8String.FromString(left), Utf8String.FromString(right))));
            }
        }
    }

    /// <summary>A key type Chalk cannot order is refused by name rather than ordered badly.</summary>
    [Fact]
    public void A_type_chalk_does_not_order_is_refused_by_name()
    {
        var failure = Assert.Throws<NotSupportedException>(() => ChalkComparers.For<Guid>());

        Assert.Contains("Guid", failure.Message, StringComparison.Ordinal);
        Assert.Contains("has no order", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every admitted key type, through the conformance kit, both ways: the rows a range matches are
    /// the rows a full scan keeps, and they arrive in the index's declared key order.
    /// </summary>
    [Fact]
    public void Every_admitted_key_type_conforms_in_both_directions()
    {
        foreach (var descending in new[] { false, true })
        {
            Conforms<byte>([3, 1, 2, 9, 4, 7, 5, 8], descending);
            Conforms<short>([-3, 1, 0, 9, -4, 7, 5, 8], descending);
            Conforms<int>([300, 100, 200, 900, 400, 700, 500, 800], descending);
            Conforms<long>([300L, 100L, 200L, 900L, 400L, 700L, 500L, 800L], descending);
            Conforms<uint>([3u, 1u, 2u, 9u, 4u, 7u, 5u, 8u], descending);
            Conforms<ulong>([3ul, 1ul, 2ul, 9ul, 4ul, 7ul, 5ul, 8ul], descending);
            Conforms<decimal>([3.5m, 1.25m, 2m, 9m, -4m, 7m, 5m, 8m], descending);
            Conforms<DateTime>(
                [.. new[] { 3, 1, 2, 9, 4, 7, 5, 8 }.Select(d => new DateTime(2026, 1, d))],
                descending);
            Conforms<DateTimeOffset>(
                [.. new[] { 3, 1, 2, 9, 4, 7, 5, 8 }
                    .Select(d => new DateTimeOffset(2026, 1, d, 0, 0, 0, TimeSpan.Zero))],
                descending);
            Conforms<DateOnly>(
                [.. new[] { 3, 1, 2, 9, 4, 7, 5, 8 }.Select(d => new DateOnly(2026, 1, d))],
                descending);
            Conforms<TimeOnly>(
                [.. new[] { 3, 1, 2, 9, 4, 7, 5, 8 }.Select(h => new TimeOnly(h, 0))],
                descending);
            Conforms<TimeSpan>(
                [.. new[] { 3, 1, 2, 9, 4, 7, 5, 8 }.Select(TimeSpan.FromHours)],
                descending);
            // NaN included: Chalk puts it above every number, so it is the extreme one end of the
            // index sits at, and the ranges that reach it are the ones Akade's own one-sided shapes
            // used to get wrong.
            Conforms<float>([3f, 1f, 2f, 9f, float.NaN, 7f, 5f, float.NegativeInfinity], descending);
            Conforms<double>([3d, 1d, 2d, 9d, double.NaN, 7d, 5d, double.NegativeInfinity], descending);
            Conforms<string>(["cc", "aa", "bb", "zz", "dd", "ss", "ee", "yy"], descending);
            Conforms<Utf8String>(
                [.. new[] { "cc", "aa", "bb", "zz", "dd", "ss", "ee", "yy" }.Select(Utf8String.FromString)],
                descending);
        }
    }

    private static void Conforms<TKey>(TKey[] keys, bool descending)
        where TKey : notnull
    {
        var rows = keys.Select((key, i) => new Row<TKey>(i, key)).ToArray();
        var comparer = ChalkComparers.For<TKey>(descending);
        var set = rows.ToIndexedSet().WithRangeIndex(Key, comparer).Build();

        var index = new AkadeScalarIndex<Row<TKey>, TKey>(
            new IndexDescriptor
            {
                Name = $"ix_{typeof(TKey).Name}_{(descending ? "desc" : "asc")}",
                Kind = IndexKind.Ordered,
                Columns = [1],
                Directions = [descending ? SortDirection.DescNullsFirst : SortDirection.AscNullsLast],
            },
            set,
            Key,
            "Key",
            "akade",
            "rows",
            comparer);

        var report = PocoIndexConformance.Verify(index, rows, [row => row.Key]);
        Assert.True(report.Ranges >= 20, report.ToString());
    }

    /// <summary>
    /// Static, so Akade files every index built with it under the same name and the declaration
    /// below matches it by method identity as well as by text.
    /// </summary>
    private static TKey Key<TKey>(Row<TKey> row) => row.Key;

    private sealed record Named(int Id, string Name);

    private static IndexedSetSourceBuilder<Named> Names(IComparer<string> comparer) =>
        AkadeSource
            .From(
                "names",
                new[] { new Named(1, "b"), new Named(2, "a"), new Named(3, "c") }
                    .ToIndexedSet()
                    .WithRangeIndex(x => x.Name, comparer)
                    .Build())
            .TableName("names")
            .NamingPolicy(PocoNamingPolicy.SnakeCase);

    /// <summary>
    /// The text the compiler records for the accessor is the text Akade files the index under, so a
    /// lambda written once at the index and once at the declaration is matched although the two are
    /// different methods.
    /// </summary>
    [Fact]
    public void A_declared_comparer_makes_an_ordered_string_index_an_access_path()
    {
        var undeclared = Names(ChalkComparers.For<string>())
            .Build()
            .DescribeSchema()
            .Tables
            .Single()
            .Indexes;

        Assert.Empty(undeclared);

        var declared = Names(ChalkComparers.For<string>())
            .Comparer(x => x.Name, ChalkComparers.For<string>())
            .Build()
            .DescribeSchema()
            .Tables
            .Single()
            .Indexes;

        var index = Assert.Single(declared);
        Assert.Equal("x => x.Name", index.Name);
        Assert.Equal(IndexKind.Ordered, index.Kind);
        Assert.Equal([SortDirection.AscNullsLast], index.Directions);
    }

    [Fact]
    public void A_declared_descending_comparer_registers_a_descending_index()
    {
        var declared = Names(ChalkComparers.For<string>(descending: true))
            .Comparer(x => x.Name, ChalkComparers.For<string>(descending: true))
            .Build()
            .DescribeSchema()
            .Tables
            .Single()
            .Indexes;

        var index = Assert.Single(declared);
        Assert.Equal([SortDirection.DescNullsFirst], index.Directions);
    }

    [Fact]
    public void An_unknown_comparer_is_refused_by_name_with_the_accepted_ones_listed()
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => Names(StringComparer.OrdinalIgnoreCase)
                .Comparer(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Build());

        Assert.Contains("x => x.Name", failure.Message, StringComparison.Ordinal);
        Assert.Contains("not an order Chalk recognises", failure.Message, StringComparison.Ordinal);
        Assert.Contains("ChalkComparers.For<String>()", failure.Message, StringComparison.Ordinal);
        Assert.Contains("StringComparer.Ordinal", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>StringComparer.Ordinal</c> is accepted — it is code-unit order, which is code-point order
    /// everywhere except across the surrogate range. A row from that range makes the index arrive
    /// out of Chalk's order, and the guard says so at that row rather than answering wrongly.
    /// </summary>
    [Fact]
    public void Ordinal_string_order_is_accepted_and_the_guard_catches_the_surrogate_range()
    {
        var accepted = Names(StringComparer.Ordinal)
            .Comparer(x => x.Name, StringComparer.Ordinal)
            .Build()
            .DescribeSchema()
            .Tables
            .Single()
            .Indexes;

        Assert.Equal("x => x.Name", Assert.Single(accepted).Name);

        // U+E000 is below U+10000 by code point and above it by code unit, because the latter is
        // stored as a surrogate pair at U+D800..U+DFFF.
        var rows = new[] { new Named(1, "\uE000"), new Named(2, "\U00010000") };
        var set = rows.ToIndexedSet().WithRangeIndex(x => x.Name, StringComparer.Ordinal).Build();

        var index = new AkadeScalarIndex<Named, string>(
            new IndexDescriptor
            {
                Name = "ix_names_name",
                Kind = IndexKind.Ordered,
                Columns = [1],
                Directions = [SortDirection.AscNullsLast],
            },
            set,
            x => x.Name,
            "x => x.Name",
            "akade",
            "names",
            ChalkComparers.For<string>());

        var failure = Assert.Throws<SourceContractException>(
            () => index.Lookup(IndexKeyRange.All).ToList());

        Assert.Equal("akade", failure.SourceId);
        Assert.Equal("names", failure.Table);
        Assert.Contains("x => x.Name", failure.Message, StringComparison.Ordinal);
        Assert.Contains("ix_names_name", failure.Message, StringComparison.Ordinal);
    }

    private sealed record Real(int Id, double Value);

    /// <summary>
    /// A real key without a declaration is not an access path: the CLR's order puts NaN below every
    /// number and Chalk puts it above, so an index built with the default comparer would hand back a
    /// range the planner deleted a sort for.
    /// </summary>
    [Fact]
    public void A_real_key_needs_a_declaration()
    {
        var rows = new[] { new Real(1, 1d), new Real(2, 2d), new Real(3, 0d) };

        var undeclared = AkadeSource
            .From("reals", rows.ToIndexedSet().WithRangeIndex(x => x.Value, ChalkComparers.For<double>()).Build())
            .TableName("reals")
            .Build()
            .DescribeSchema()
            .Tables
            .Single()
            .Indexes;

        Assert.Empty(undeclared);

        var declared = AkadeSource
            .From("reals", rows.ToIndexedSet().WithRangeIndex(x => x.Value, ChalkComparers.For<double>()).Build())
            .TableName("reals")
            .Comparer(x => x.Value, ChalkComparers.For<double>())
            .Build()
            .DescribeSchema()
            .Tables
            .Single()
            .Indexes;

        Assert.Equal(IndexKind.Ordered, Assert.Single(declared).Kind);
    }

    /// <summary>
    /// Measured against Akade 1.5.0: <c>GreaterThan[OrEqual]</c> and <c>LessThan[OrEqual]</c> do not
    /// honour the comparer the index was built with — they answer an index whose order differs from
    /// the CLR's default with the wrong rows, usually none at all. <c>Range</c> and the index's own
    /// extremes do honour it, so every bounded shape the adapter serves is a <c>Range</c> between
    /// them. This pins the difference rather than describing it.
    /// </summary>
    [Fact]
    public void One_sided_akade_shapes_do_not_honour_a_custom_comparer_and_the_adapter_uses_none()
    {
        var rows = new[] { new Real(1, 1d), new Real(2, double.NaN), new Real(3, 0d) };
        var set = rows.ToIndexedSet()
            .WithRangeIndex(x => x.Value, ChalkComparers.For<double>())
            .Build();

        // What Akade answers directly, with a NaN among the keys: nothing.
        Assert.Empty(set.GreaterThanOrEqual(x => x.Value, 0d));

        // What the adapter answers, as a Range between the index's own extremes: every row.
        var index = new AkadeScalarIndex<Real, double>(
            new IndexDescriptor
            {
                Name = "ix_reals_value",
                Kind = IndexKind.Ordered,
                Columns = [1],
                Directions = [SortDirection.AscNullsLast],
            },
            set,
            x => x.Value,
            "x => x.Value",
            "akade",
            "reals",
            ChalkComparers.For<double>());

        var matched = index
            .Lookup(new IndexKeyRange { Lower = [0d], LowerInclusive = true, Upper = [] })
            .Select(r => r.Value)
            .ToArray();

        Assert.Equal([0d, 1d, double.NaN], matched);
    }

    private sealed record Stamped(int Id, DateTime Ts);

    /// <summary>
    /// The temporal types need no declaration: the CLR's order for them already is Chalk's, which is
    /// what makes them the widening this step is mostly made of.
    /// </summary>
    [Fact]
    public void A_temporal_key_is_an_access_path_on_the_strength_of_its_type()
    {
        var rows = new[]
        {
            new Stamped(1, new DateTime(2026, 1, 2)),
            new Stamped(2, new DateTime(2026, 1, 1)),
        };

        var indexes = AkadeSource
            .From("stamps", rows.ToIndexedSet().WithRangeIndex(x => x.Ts).Build())
            .TableName("stamps")
            .Build()
            .DescribeSchema()
            .Tables
            .Single()
            .Indexes;

        Assert.Equal(IndexKind.Ordered, Assert.Single(indexes).Kind);
    }
}
