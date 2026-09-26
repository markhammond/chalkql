using Chalk.Sources.Poco;

namespace Chalk.Sources.Poco.Tests;

/// <summary>An in-process source declares its zone too: its rows belong somewhere (D311).</summary>
public sealed class PocoZoneTests
{
    private sealed record Row(int Id);

    [Fact]
    public void The_declared_zone_is_the_descriptors_and_none_is_the_default()
    {
        var zoned = new PocoSourceBuilder("mem").Zone("eu").AddTable("rows", new[] { new Row(1) }).Build();
        var plain = new PocoSourceBuilder("mem").AddTable("rows", new[] { new Row(1) }).Build();

        Assert.Equal("eu", zoned.DescribeSchema().Zone);
        Assert.Equal(string.Empty, plain.DescribeSchema().Zone);
        Assert.Throws<ArgumentException>(() => new PocoSourceBuilder("mem").Zone(" "));
    }
}
