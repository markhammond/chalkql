using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Chalk.Sample.AkadeIndexedSet;

namespace Chalk.Sources.Akade.Tests;

/// <summary>
/// Akade-level examples, deliberately split one-per-feature rather than keeping the README's examples
/// in one large sample. These prove the fixture itself before Chalk's adapter is involved.
/// </summary>
public sealed class AkadeReadmeExamplesTests
{
    [Fact]
    public void Unique_primary_index()
    {
        var set = AkadeReadmeExamples.BuildPurchases(AkadeReadmeExamples.Purchases);

        Assert.Equal(6, set[6].Id);
    }

    [Fact]
    public void Non_unique_index()
    {
        var set = AkadeReadmeExamples.BuildPurchases(AkadeReadmeExamples.Purchases);

        Assert.Equal(
            [6, 7],
            set.Where(x => x.ProductId, 4).Select(x => x.Id).Order().ToArray());
    }

    [Fact]
    public void Range_index()
    {
        var set = AkadeReadmeExamples.BuildPurchases(AkadeReadmeExamples.Purchases);

        Assert.Equal(
            [1, 2, 6],
            set.Range(x => x.Amount, 1, 3, inclusiveStart: true, inclusiveEnd: true)
                .Select(x => x.Id)
                .Order()
                .ToArray());
    }

    [Fact]
    public void Greater_than_or_equal_range_index()
    {
        var set = AkadeReadmeExamples.BuildPurchases(AkadeReadmeExamples.Purchases);

        Assert.Equal(
            [6, 7],
            set.GreaterThanOrEqual(x => x.UnitPrice, 10)
                .Select(x => x.Id)
                .Order()
                .ToArray());
    }

    [Fact]
    public void Computed_range_index()
    {
        var set =
            AkadeReadmeExamples.BuildPurchases(
                AkadeReadmeExamples.Purchases);

        var result =
            AkadeReadmeExamples.MaxTotal(set).Single();

        Assert.Equal(7, result.Id);
        Assert.Equal(
            80,
            AkadeReadmeExamples.PurchaseKeys.Total(result));
    }

    [Fact]
    public void Compound_index()
    {
        var set =
            AkadeReadmeExamples.BuildPurchases(
                AkadeReadmeExamples.Purchases);

        Assert.Equal(
            [7],
            AkadeReadmeExamples
                .ProductAndPrice(set, 4, 10)
                .Select(x => x.Id)
                .ToArray());
    }

    [Fact]
    public void Prefix_and_full_text_indexes()
    {
        var set = AkadeReadmeExamples.BuildStrings(
        [
            new("String", "System.String"),
            new("Int32", "System.Int32"),
            new("Interesting", "Something Interesting"),
        ]);

        Assert.Contains(set.StartsWith(x => x.Name, "Int"), x => x.Name == "Int32");
        Assert.Contains(set.Contains(x => x.Text, "String"), x => x.Name == "String");
        Assert.Contains(set.FuzzyStartsWith(x => x.Name, "Strang", 1), x => x.Name == "String");
    }

    [Fact]
    public void Multi_key_non_unique_index()
    {
        var set = AkadeReadmeExamples.BuildGraph(
        [
            new(1, [3, 4]),
            new(2, [3]),
            new(3, [1, 2, 3]),
            new(4, [1, 3]),
        ]);

        Assert.Equal(
            [3, 4],
            set.Where(x => x.ConnectsTo, contains: 1).Select(x => x.Id).Order().ToArray());
    }

    [Fact]
    public void Computed_and_compound_keys()
    {
        var set = AkadeReadmeExamples.BuildComputed([new(2, 10)]);

        Assert.Single(set.Where(x => (x.Start, x.End), (2, 10)));
        Assert.Single(set.Where(x => x.End - x.Start, 8));
        Assert.Single(set.Where(ComputedKey.SomeStaticMethod, 42));
    }

    [Fact]
    [Experimental("AkadeIndexedSetEXP0002")]
    public void Spatial_index()
    {
        var set = AkadeReadmeExamples.BuildSpatial(
        [
            new Vector2(100, 150),
            new Vector2(250, 200),
            new Vector2(400, 500),
            new Vector2(50, 75),
        ]);

        var result = set.Intersects(
                x => x,
                new Vector2(150, 150),
                new Vector2(300, 250))
            .ToArray();

        Assert.Contains(new Vector2(250, 200), result);
        Assert.DoesNotContain(new Vector2(400, 500), result);
    }

    [Fact]
    [Experimental("AkadeIndexedSetEXP0003")]
    public void Vector_index()
    {
        var set = AkadeReadmeExamples.BuildVectors(
        [
            new("Machine Learning Basics", new[] { 0.1f, 0.8f, 0.3f, 0.9f }),
            new("Deep Learning Guide", new[] { 0.2f, 0.9f, 0.4f, 0.8f }),
            new("Cooking Recipes", new[] { 0.9f, 0.1f, 0.8f, 0.2f }),
            new("Travel Guide", new[] { 0.3f, 0.2f, 0.9f, 0.1f }),
        ]);

        ReadOnlySpan<float> query =
            new[] { 0.15f, 0.85f, 0.35f, 0.85f };

        var result = set.ApproximateNearestNeighbors(
                x => x.Embedding.Span,
                query,
                k: 2)
            .ToArray();

        Assert.Equal(2, result.Length);
    }

    [Fact]
    public void Concurrent_indexed_set()
    {
        var set = AkadeReadmeExamples.BuildConcurrent(AkadeReadmeExamples.Purchases);

        Assert.Equal(2, set.Where(x => x.ProductId, 4).Count());
    }
}
