using Akade.IndexedSet;
using Chalk.Sources.Poco;
using IndexKind = Chalk.Ir.IndexKind;

namespace Chalk.Sources.Akade.Tests;

public sealed class AkadeIndexRegistrationTests
{
    [Fact]
    public void Direct_scalar_indexes_are_advertised_and_expression_indexes_are_hidden()
    {
        var set = Purchases(
        [
            new(1, 1, 1, 5),
            new(2, 1, 2, 5),
            new(6, 4, 3, 12),
            new(7, 4, 8, 10),
        ]);

        var source = AkadeSource
            .From("purchases", set)
            .TableName("purchases")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .Build();

        var indexes = source.DescribeSchema().Tables.Single().Indexes;
        var byName = indexes.ToDictionary(index => index.Name, StringComparer.Ordinal);

        Assert.Equal(4, indexes.Count);

        Assert.Equal(IndexKind.Hash, byName["x => x.Id"].Kind);
        Assert.True(byName["x => x.Id"].Unique);

        Assert.Equal(IndexKind.Hash, byName["x => x.ProductId"].Kind);
        Assert.False(byName["x => x.ProductId"].Unique);

        Assert.Equal(IndexKind.Ordered, byName["x => x.Amount"].Kind);
        Assert.Equal(IndexKind.Ordered, byName["x => x.UnitPrice"].Kind);

        Assert.DoesNotContain(
            indexes,
            index => index.Name.Contains(nameof(PurchaseKeys.Total), StringComparison.Ordinal));
        Assert.DoesNotContain(
            indexes,
            index => index.Name.Contains(nameof(PurchaseKeys.ProductAndUnitPrice), StringComparison.Ordinal));
    }

    private static IndexedSet<int, Purchase> Purchases(IEnumerable<Purchase> rows) =>
        rows.ToIndexedSet(x => x.Id)
            .WithIndex(x => x.ProductId)
            .WithRangeIndex(x => x.Amount)
            .WithRangeIndex(x => x.UnitPrice)
            .WithRangeIndex(PurchaseKeys.Total)
            .WithIndex(PurchaseKeys.ProductAndUnitPrice)
            .Build();

    private sealed record Purchase(int Id, int ProductId, int Amount, int UnitPrice);

    private static class PurchaseKeys
    {
        public static int Total(Purchase x) => x.Amount * x.UnitPrice;

        public static (int ProductId, int UnitPrice) ProductAndUnitPrice(Purchase x) =>
            (x.ProductId, x.UnitPrice);
    }
}
