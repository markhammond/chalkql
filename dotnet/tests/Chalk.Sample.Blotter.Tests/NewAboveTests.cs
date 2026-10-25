using Chalk.Sample.Blotter.Components;

namespace Chalk.Sample.Blotter.Tests;

/// <summary>The count of what arrived above, as its badge shows it.</summary>
public sealed class NewAboveTests
{
    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "1")]
    [InlineData(999, "999")]
    [InlineData(1_000, "999+")]
    [InlineData(2_500_000, "999+")]
    public void The_count_stops_at_999_plus(long above, string? shown) => Assert.Equal(shown, NewAbove.Count(above));
}
