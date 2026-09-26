using Akade.IndexedSet;

namespace Chalk.Sources.Akade.Tests;

/// <summary>An Akade set declares its zone as any source does (D311).</summary>
public sealed class ZoneTests
{
    private sealed record Named(int Id, string Name);

    [Fact]
    public void The_declared_zone_is_the_descriptors_and_none_is_the_default()
    {
        var set = new[] { new Named(1, "a") }.ToIndexedSet().Build();

        Assert.Equal("eu", AkadeSource.From("names", set).Zone("eu").Build().DescribeSchema().Zone);
        Assert.Equal(string.Empty, AkadeSource.From("names", set).Build().DescribeSchema().Zone);
    }
}
