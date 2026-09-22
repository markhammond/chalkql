using Akade.IndexedSet;
using Chalk.Catalog;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using IndexKind = Chalk.Ir.IndexKind;

namespace Chalk.Sources.Akade.Tests;

/// <summary>
/// D282 — Akade's trie as a Chalk PREFIX index: taken for one at discovery rather than at query
/// time, answering prefixes and refusing everything else.
/// </summary>
public sealed class AkadePrefixIndexTests
{
    private sealed record Term(string Name, string Text);

    private static Term[] Rows() =>
    [
        new("String", "System.String"),
        new("Int32", "System.Int32"),
        new("Int64", "System.Int64"),
        new("Int", "the prefix of the two above"),
        new("Interesting", "Something Interesting"),
        new("Stream", "System.IO.Stream"),
        new("Decimal", "System.Decimal"),
    ];

    private static IndexedSet<Term> Set() =>
        Rows().ToIndexedSet()
            .WithPrefixIndex(x => x.Name)
            .WithFullTextIndex(x => x.Text)
            .Build();

    [Fact]
    public void A_prefix_index_is_disclosed_as_one_and_a_full_text_index_is_not()
    {
        var indexes = AkadeSource
            .From("terms", Set())
            .TableName("terms")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .Build()
            .DescribeSchema()
            .Tables
            .Single()
            .Indexes;

        var index = Assert.Single(indexes);
        Assert.Equal("x => x.Name", index.Name);
        Assert.Equal(IndexKind.Prefix, index.Kind);
        Assert.Equal([0], index.Columns);
        Assert.Empty(index.Directions);
        Assert.False(index.Unique);
    }

    private static AkadePrefixIndex<Term> Index(IndexedSet<Term> set) =>
        new(
            new IndexDescriptor
            {
                Name = "ix_terms_name",
                Kind = IndexKind.Prefix,
                Columns = [0],
            },
            set,
            x => x.Name,
            "x => x.Name",
            "terms",
            "terms");

    private static IndexKeyRange Prefix(string prefix) => new()
    {
        Lower = [prefix],
        LowerInclusive = true,
        Upper = [],
        UpperInclusive = false,
        Prefix = prefix,
    };

    [Fact]
    public void A_prefix_index_agrees_with_a_full_scan()
    {
        var rows = Rows();
        var report = PocoIndexConformance.Verify(Index(Set()), rows, [x => x.Name]);

        // Every prefix of every name, the empty prefix, and prefixes nothing matches.
        Assert.True(report.Ranges >= 40, report.ToString());
    }

    [Fact]
    public void A_prefix_that_is_also_a_whole_key_matches_both()
    {
        var names = Index(Set()).Lookup(Prefix("Int")).Select(x => x.Name).Order().ToArray();

        Assert.Equal(["Int", "Int32", "Int64", "Interesting"], names);
    }

    [Fact]
    public void The_empty_prefix_is_every_row()
    {
        Assert.Equal(Rows().Length, Index(Set()).Lookup(Prefix(string.Empty)).Count());
    }

    [Fact]
    public void A_prefix_nothing_starts_with_matches_nothing()
    {
        Assert.Empty(Index(Set()).Lookup(Prefix("Zz")));
    }

    [Fact]
    public void Anything_that_is_not_a_prefix_is_refused_by_name()
    {
        var failure = Assert.Throws<SourceContractException>(
            () => Index(Set()).Lookup(IndexKeyRange.Equality("Int")).ToList());

        Assert.Equal("terms", failure.SourceId);
        Assert.Contains("ix_terms_name", failure.Message, StringComparison.Ordinal);
        Assert.Contains("answers prefix lookups only", failure.Message, StringComparison.Ordinal);
    }
}
