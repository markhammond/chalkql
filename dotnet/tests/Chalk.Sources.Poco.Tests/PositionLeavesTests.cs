namespace Chalk.Sources.Poco.Tests;

/// <summary>
/// An index's order held in leaves (F202): positions inserted where their ordinals say, against a plain
/// list doing the same; an older version reading what it held while newer ones write past it; two
/// versions from one never writing over each other; rows at a run's end written in place the next
/// time; and leaves cut too fine packed again.
/// </summary>
public sealed class PositionLeavesTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public void Inserts_land_where_their_ordinals_say(int seed)
    {
        var random = new Random(seed);
        var reference = Enumerable.Range(0, 3_000).ToList();
        var leaves = PositionLeaves.FromSorted([.. reference]);
        var next = reference.Count;

        for (var round = 0; round < 200; round++)
        {
            var count = random.Next(1, 40);
            var ordinals = new int[count];
            for (var i = 0; i < count; i++)
            {
                // Clustered at a few places, as a key's latest rows are, and anywhere now and then.
                ordinals[i] = random.Next(4) == 0
                    ? random.Next(reference.Count + 1)
                    : Math.Min(reference.Count, (random.Next(5) * 701) % (reference.Count + 1));
            }

            Array.Sort(ordinals);
            var positions = Enumerable.Range(next, count).ToArray();
            next += count;

            leaves = leaves.Insert(ordinals, positions);
            reference = Inserted(reference, ordinals, positions);

            Assert.Equal(reference, leaves.ToArray());
            for (var probe = 0; probe < 20; probe++)
            {
                var ordinal = random.Next(reference.Count);
                Assert.Equal(reference[ordinal], leaves[ordinal]);
            }
        }
    }

    [Fact]
    public void An_older_version_reads_what_it_held()
    {
        var first = PositionLeaves.FromSorted([0, 1, 2, 3]);
        var second = first.Insert([4], [10]);
        var third = second.Insert([5], [11]);
        var fourth = third.Insert([6, 6], [12, 13]);

        Assert.Equal([0, 1, 2, 3], first.ToArray());
        Assert.Equal([0, 1, 2, 3, 10], second.ToArray());
        Assert.Equal([0, 1, 2, 3, 10, 11], third.ToArray());
        Assert.Equal([0, 1, 2, 3, 10, 11, 12, 13], fourth.ToArray());
    }

    /// <summary>
    /// Two appends to one version — one a refresh abandoned, the other the one it was retried as — each
    /// keep their own rows: the second finds the room claimed and copies the leaf.
    /// </summary>
    [Fact]
    public void Two_versions_from_one_do_not_write_over_each_other()
    {
        var withRoom = PositionLeaves.FromSorted([0, 1, 2]).Insert([3], [3]);

        var abandoned = withRoom.Insert([4], [100]);
        var retried = withRoom.Insert([4], [200]);

        Assert.Equal([0, 1, 2, 3, 100], abandoned.ToArray());
        Assert.Equal([0, 1, 2, 3, 200], retried.ToArray());
        Assert.Equal([0, 1, 2, 3], withRoom.ToArray());
    }

    /// <summary>
    /// Rows put inside a leaf cut it after them, so the next rows at the same run's end land at a leaf's
    /// end, in its room: the leaf's array is the one it was, written in place.
    /// </summary>
    [Fact]
    public void Rows_at_a_runs_end_are_written_in_place_the_next_time()
    {
        var leaves = PositionLeaves.FromSorted([.. Enumerable.Range(0, 2_000)]);

        leaves = leaves.Insert([700], [5_000]);
        var leaf = leaves.LeafOf(700);
        Assert.Equal(701, leaves.StartOf(leaf + 1));
        var array = leaves.ArrayOf(leaf);

        leaves = leaves.Insert([701], [5_001]);

        Assert.Same(array, leaves.ArrayOf(leaves.LeafOf(701)));
        Assert.Equal(5_001, leaves[701]);
        Assert.Equal(700, leaves[702]);
    }

    [Fact]
    public void Leaves_cut_too_fine_are_packed_again()
    {
        var random = new Random(3);
        var reference = Enumerable.Range(0, 20_000).ToList();
        var leaves = PositionLeaves.FromSorted([.. reference]);
        for (var round = 0; round < 2_000; round++)
        {
            var ordinal = random.Next(reference.Count + 1);
            leaves = leaves.Insert([ordinal], [reference.Count]);
            reference.Insert(ordinal, reference.Count);
        }

        Assert.Equal(reference, leaves.ToArray());
        Assert.True(
            !leaves.Fragmented,
            $"{leaves.LeafCount} leaves for {leaves.Count} positions");
    }

    [Fact]
    public void Rows_into_nothing_make_leaves_of_their_own()
    {
        var leaves = PositionLeaves.FromSorted([]).Insert([0, 0, 0], [2, 0, 1]);

        Assert.Equal([2, 0, 1], leaves.ToArray());
    }

    /// <summary>A plain list with each position inserted before the original element its ordinal names.</summary>
    private static List<int> Inserted(List<int> original, int[] ordinals, int[] positions)
    {
        var result = new List<int>(original.Count + positions.Length);
        var next = 0;
        for (var ordinal = 0; ordinal <= original.Count; ordinal++)
        {
            while (next < ordinals.Length && ordinals[next] == ordinal)
            {
                result.Add(positions[next++]);
            }

            if (ordinal < original.Count)
            {
                result.Add(original[ordinal]);
            }
        }

        return result;
    }
}
