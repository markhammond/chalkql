using Chalk.Sources.Poco;
using Chalk.Sample.AkadeIndexedSet;

namespace Chalk.Sources.Akade.Tests;

/// <summary>
/// Source contract tests that do not require the Calcite sidecar.
/// </summary>
public sealed class IndexedSetSourceTests
{
    [Fact]
    public void Factory_infers_record_type_and_exposes_one_typed_table()
    {
        var set = AkadeReadmeExamples.BuildPurchases(AkadeReadmeExamples.Purchases);

        var source = AkadeSource
            .From("purchases", set)
            .TableName("purchases")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .Build();

        ITableTarget<Purchase> table = source.Table;

        Assert.Same(source, table.Runtime);
        Assert.Equal("purchases", table.Table);
        Assert.Equal("purchases", source.SourceId);
        Assert.Single(source.DescribeSchema().Tables);
        Assert.Equal(4, source.DescribeSchema().Tables[0].RowCount);
    }

    [Fact]
    public async Task Table_refresh_recomputes_metadata_after_live_mutation_of_same_set()
    {
        var set = AkadeReadmeExamples.BuildPurchases(AkadeReadmeExamples.Purchases);

        var source = AkadeSource
            .From("purchases", set)
            .TableName("purchases")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .Build();

        Assert.Equal(4, source.DescribeSchema().Tables[0].RowCount);

        set.Add(new Purchase(8, 4, 1, 11));

        // Live mutation deliberately does not mutate the published catalog snapshot.
        Assert.Equal(4, source.DescribeSchema().Tables[0].RowCount);

        await source.RefreshTableAsync("purchases", CancellationToken.None);

        Assert.Equal(5, source.DescribeSchema().Tables[0].RowCount);
    }

    [Fact]
    public async Task Table_refresh_does_not_re_read_registration()
    {
        var current =
            AkadeReadmeExamples.BuildPurchases(
                AkadeReadmeExamples.Purchases);

        var source = AkadeSource
            .From("purchases", () => current)
            .TableName("purchases")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .Build();

        current =
            AkadeReadmeExamples.BuildPurchases(
                AkadeReadmeExamples.Purchases.Append(
                    new Purchase(8, 4, 1, 11)));

        await source.RefreshTableAsync("purchases");

        Assert.Equal(
            4,
            source.DescribeSchema().Tables[0].RowCount);
    }
    
    [Fact]
    public async Task Source_refresh_re_reads_registration()
    {
        var current = AkadeReadmeExamples.BuildPurchases(AkadeReadmeExamples.Purchases);

        var source = AkadeSource
            .From("purchases", () => current)
            .TableName("purchases")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .Build();

        current = AkadeReadmeExamples.BuildPurchases(
            AkadeReadmeExamples.Purchases.Append(new Purchase(8, 4, 1, 11)));
        
        await source.RefreshAsync();
        
        Assert.Equal(5, source.DescribeSchema().Tables[0].RowCount);
    }

    [Fact]
    public void Replace_and_append_are_rejected_without_editor()
    {
        var set = AkadeReadmeExamples.BuildPurchases(AkadeReadmeExamples.Purchases);

        var source = AkadeSource
            .From("purchases", set)
            .TableName("purchases")
            .Build();

        var entry = new SourceRefreshEntry
        {
            Table = "purchases",
            Kind = SourceRefreshKind.Append,
            RowType = typeof(Purchase),
            Rows = new[] { new Purchase(8, 4, 1, 11) },
        };

        var exception = Assert.Throws<SourceContractException>(
            () => source.ValidateRefresh([entry]));

        Assert.Contains("RebuildWith", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Append_with_editor_builds_and_publishes_fresh_successor()
    {
        var set = AkadeReadmeExamples.BuildPurchases(AkadeReadmeExamples.Purchases);

        var source = AkadeSource
            .From("purchases", set)
            .TableName("purchases")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .RebuildWith(AkadeReadmeExamples.BuildPurchases)
            .Build();

        var entry = new SourceRefreshEntry
        {
            Table = "purchases",
            Kind = SourceRefreshKind.Append,
            RowType = typeof(Purchase),
            Rows = new[] { new Purchase(8, 4, 1, 11) },
            Statistics = StatisticsRefresh.Defer,
        };

        var commit = await source.PrepareRefreshAsync([entry], CancellationToken.None);

        // Preparation is invisible.
        Assert.Equal(4, source.DescribeSchema().Tables[0].RowCount);

        commit.Commit();

        // Row count is always fresh even when value-distribution statistics are deferred.
        Assert.Equal(5, source.DescribeSchema().Tables[0].RowCount);
    }

    [Fact]
    public void Concurrent_factory_returns_concurrent_source()
    {
        var set = AkadeReadmeExamples.BuildConcurrent(AkadeReadmeExamples.Purchases);

        var source = AkadeSource
            .From("purchases", set)
            .TableName("purchases")
            .Build();

        Assert.IsType<ConcurrentIndexedSetSource<Purchase>>(source);
        Assert.Equal(4, source.DescribeSchema().Tables[0].RowCount);
    }
}
