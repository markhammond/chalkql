using System.Diagnostics.CodeAnalysis;
using Akade.IndexedSet.Concurrency;

using System.Numerics;
using Akade.IndexedSet;

namespace Chalk.Sample.AkadeIndexedSet;

/// <summary>
/// Small data sets deliberately shaped after the examples in the Akade.IndexedSet README. Keeping
/// them in one place lets the console sample and tests exercise exactly the same index definitions.
/// </summary>
public static class AkadeReadmeExamples
{
    public static readonly Purchase[] Purchases =
    [
        new(Id: 1, ProductId: 1, Amount: 1, UnitPrice: 5),
        new(Id: 2, ProductId: 1, Amount: 2, UnitPrice: 5),
        new(Id: 6, ProductId: 4, Amount: 3, UnitPrice: 12),
        new(Id: 7, ProductId: 4, Amount: 8, UnitPrice: 10),
    ];
    
    public static Purchase[] PlanningPurchases()
    {
        const int fillerCount = 10_000;

        var rows = new List<Purchase>(
            AkadeReadmeExamples.Purchases.Length + fillerCount);

        rows.AddRange(AkadeReadmeExamples.Purchases);

        for (var i = 0; i < fillerCount; i++)
        {
            rows.Add(new Purchase(
                Id: 10_000 + i,
                ProductId: 100 + i,
                Amount: 100 + i,
                UnitPrice: 1));
        }

        return [.. rows];
    }

    /// <summary>
    /// Mirrors the README overview: primary/unique id, non-unique ProductId, two range indexes,
    /// a computed range key, and a compound equality key.
    /// </summary>
    public static IndexedSet<int, Purchase> BuildPurchases(
        IEnumerable<Purchase> rows) =>
        rows.ToIndexedSet(x => x.Id)
            .WithIndex(x => x.ProductId)
            .WithRangeIndex(x => x.Amount)
            .WithRangeIndex(x => x.UnitPrice)
            .WithRangeIndex(PurchaseKeys.Total)
            .WithIndex(PurchaseKeys.ProductAndUnitPrice)
            .Build();

    public static IEnumerable<Purchase> MaxTotal(
        IndexedSet<int, Purchase> set) =>
        set.MaxBy(PurchaseKeys.Total);

    public static IEnumerable<Purchase> ProductAndPrice(
        IndexedSet<int, Purchase> set,
        int productId,
        int unitPrice) =>
        set.Where(
            PurchaseKeys.ProductAndUnitPrice,
            (productId, unitPrice));
    
    public static class PurchaseKeys
    {
        // Static methods are intentionally used for the complex keys. Akade recommends that spelling
        // for complex indices, and Chalk can bind MethodInfo identity without parsing compiler IL.
        public static int Total(Purchase x) => x.Amount * x.UnitPrice;

        public static (int ProductId, int UnitPrice) ProductAndUnitPrice(Purchase x) => (x.ProductId, x.UnitPrice);
    }

    public static IndexedSet<SearchRow> BuildStrings(IEnumerable<SearchRow> rows) =>
        rows.ToIndexedSet()
            .WithPrefixIndex(x => x.Name)
            .WithFullTextIndex(x => x.Text)
            .Build();

    public static IndexedSet<int, GraphNode> BuildGraph(IEnumerable<GraphNode> rows) =>
        rows.ToIndexedSet(x => x.Id)
            .WithIndex(x => x.ConnectsTo)
            .Build();

    public static IndexedSet<RangeData> BuildComputed(IEnumerable<RangeData> rows) =>
        rows.ToIndexedSet()
            .WithIndex(x => (x.Start, x.End))
            .WithIndex(x => x.End - x.Start)
            .WithIndex(ComputedKey.SomeStaticMethod)
            .Build();

    public static IndexedSet<Vector2> BuildSpatial(IEnumerable<Vector2> rows) =>
        rows.ToIndexedSet()
            .WithSpatialIndex(x => x)
            .Build();

    [Experimental("AkadeIndexedSetEXP0003")]
    public static IndexedSet<Document> BuildVectors(IEnumerable<Document> rows) =>
        rows.ToIndexedSet()
            .WithVectorIndex(x => x.Embedding.Span)
            .Build();

    public static ConcurrentIndexedSet<Purchase> BuildConcurrent(IEnumerable<Purchase> rows) =>
        IndexedSetBuilder.Create(rows)
            .WithIndex(x => x.ProductId)
            .WithRangeIndex(x => x.Amount)
            .BuildConcurrent();
}

public sealed record Purchase(int Id, int ProductId, int Amount, int UnitPrice);

public sealed record SearchRow(string Name, string Text);

public sealed record GraphNode(int Id, IEnumerable<int> ConnectsTo);

public sealed record RangeData(int Start, int End);

public static class ComputedKey
{
    public static int SomeStaticMethod(RangeData x) => (x.End - x.Start) * 42 / 8;
}

public sealed record Document(string Title, ReadOnlyMemory<float> Embedding);
