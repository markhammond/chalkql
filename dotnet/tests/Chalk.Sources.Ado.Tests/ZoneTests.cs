using Chalk.Catalog;

namespace Chalk.Sources.Ado.Tests;

/// <summary>A source declares its zone, and the descriptor carries it; nothing else changes (D311).</summary>
public sealed class ZoneTests
{
    [Fact]
    public void A_source_declares_no_zone_until_the_host_says_so()
    {
        Assert.Equal(string.Empty, Builder().Build().DescribeSchema().Zone);
    }

    [Fact]
    public void The_declared_zone_is_the_descriptors()
    {
        Assert.Equal("eu", Builder().Zone("eu").Build().DescribeSchema().Zone);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_zone_is_refused_at_the_builder(string zone)
    {
        Assert.Throws<ArgumentException>(() => Builder().Zone(zone));
    }

    private static AdoSourceBuilder Builder()
    {
        var provider = new FakeProvider { Rows = [] };
        return new AdoSourceBuilder("fake", provider.Connect, "main")
            .Dialect(DialectProfiles.Ansi)
            .AddTable("t", [new() { Name = "id", Type = ChalkType.Int64() }]);
    }
}
