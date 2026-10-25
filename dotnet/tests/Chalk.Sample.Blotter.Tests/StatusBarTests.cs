using Chalk.Sample.Blotter.Components;

namespace Chalk.Sample.Blotter.Tests;

/// <summary>The top bar's figures: two significant figures, shown again only when they move by 5% or more.</summary>
public sealed class StatusBarTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(0.004, "0")]
    [InlineData(0.0123, "0.012")]
    [InlineData(0.42, "0.42")]
    [InlineData(1.349, "1.3")]
    [InlineData(2.66, "2.7")]
    [InlineData(9.96, "10")]
    [InlineData(15.3, "15")]
    [InlineData(127, "130")]
    public void Figures_are_shown_to_two_significant_figures(double value, string shown) => Assert.Equal(shown, StatusBar.Two(value));

    [Theory]
    [InlineData(2.6, 2.6, false)]
    [InlineData(2.6, 2.7, false)] // reads differently, but under 5%
    [InlineData(2.6, 2.75, true)]
    [InlineData(15, 15.4, false)] // 5% or less, and reads the same
    [InlineData(15, 16, true)]
    [InlineData(0, 0.5, true)]
    [InlineData(0.5, 0, true)]
    [InlineData(0.42, 0.43, false)]
    public void A_figure_shows_again_once_it_moves_by_five_percent(double shown, double now, bool moved) =>
        Assert.Equal(moved, StatusBar.Moved(shown, now));
}
