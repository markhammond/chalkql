namespace Chalk.Sources.Poco.Tests;

/// <summary>
/// The shared ramp every source that honours a row goal sizes its batches with (D276,
/// <c>docs/design/46-row-goals.md</c> §5).
/// </summary>
public sealed class BatchRampTests
{
    [Fact]
    public void Without_a_goal_every_batch_is_a_full_one()
    {
        Assert.Equal(4096, BatchRamp.Next(0, 4096, rowGoal: null));
        Assert.Equal(4096, BatchRamp.Next(4096, 4096, rowGoal: null));
    }

    [Fact]
    public void The_first_batch_of_a_goaled_scan_is_the_goal()
    {
        Assert.Equal(1, BatchRamp.Next(0, 4096, rowGoal: 1));
        Assert.Equal(100, BatchRamp.Next(0, 4096, rowGoal: 100));
    }

    [Fact]
    public void A_goal_larger_than_the_batch_size_is_the_batch_size()
    {
        Assert.Equal(4096, BatchRamp.Next(0, 4096, rowGoal: 4096));
        Assert.Equal(4096, BatchRamp.Next(0, 4096, rowGoal: 1_000_000_000_000L));
    }

    [Fact]
    public void Each_batch_after_the_first_doubles_up_to_the_batch_size()
    {
        var sizes = new List<int>();
        var previous = 0;
        for (var i = 0; i < 16; i++)
        {
            previous = BatchRamp.Next(previous, 64, rowGoal: 1);
            sizes.Add(previous);
        }

        Assert.Equal([1, 2, 4, 8, 16, 32, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64], sizes);
    }

    /// <summary>
    /// A goal of zero or less is what a plan says when no limit reaches the leaf, and it is read as
    /// "nothing said" rather than as "no rows".
    /// </summary>
    [Fact]
    public void A_goal_of_zero_or_less_is_no_goal_at_all()
    {
        Assert.Equal(4096, BatchRamp.Next(0, 4096, rowGoal: 0));
        Assert.Equal(4096, BatchRamp.Next(0, 4096, rowGoal: -7));
    }

    /// <summary>A batch is never empty, whatever the caller asks for.</summary>
    [Fact]
    public void A_batch_is_never_empty()
    {
        Assert.Equal(1, BatchRamp.Next(0, 1, rowGoal: 1));
        Assert.Equal(1, BatchRamp.Next(1, 1, rowGoal: 1));
        Assert.Equal(1, BatchRamp.Next(0, 0, rowGoal: null));
        Assert.Equal(1, BatchRamp.Next(0, -3, rowGoal: 5));
    }

    /// <summary>
    /// The doubling saturates rather than overflowing, which a batch size near the top of the range
    /// is the only way to reach.
    /// </summary>
    [Fact]
    public void The_doubling_saturates_instead_of_overflowing()
    {
        var next = BatchRamp.Next(int.MaxValue / 2, int.MaxValue, rowGoal: 1);
        Assert.InRange(next, 1, int.MaxValue);
        Assert.Equal(int.MaxValue, BatchRamp.Next(next, int.MaxValue, rowGoal: 1));
    }
}
