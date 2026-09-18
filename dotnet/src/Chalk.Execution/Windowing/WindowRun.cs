using Chalk.Execution.Vectors;
using Chalk.Ir;

namespace Chalk.Execution.Windowing;

/// <summary>
/// The per-execution state every call evaluator reads: the buffered input, the peer groups and the
/// frame bounds the operator computed once for the partition it is working on.
/// </summary>
/// <remarks>
/// The frame is computed once per partition and shared by every call, because it is a property of
/// the window and not of the function — which is also why a plan with five calls over one frame
/// costs one frame pass and five accumulations rather than five of each.
/// </remarks>
internal sealed class WindowRun
{
    public required WindowColumn[] Columns { get; init; }

    public required int RowCount { get; init; }

    /// <summary>First row of each row's peer group.</summary>
    public required int[] PeerStart { get; init; }

    /// <summary>One past the last row of each row's peer group.</summary>
    public required int[] PeerEnd { get; init; }

    /// <summary>First row of each row's frame.</summary>
    public required int[] FrameLo { get; init; }

    /// <summary>Last row of each row's frame; the frame is empty when it is below <see cref="FrameLo"/>.</summary>
    public required int[] FrameHi { get; init; }

    public required FrameExclusion Exclusion { get; init; }

    public required IReadOnlyList<ScalarValue> Parameters { get; init; }

    /// <summary>True when no row is ever excluded, which is what lets the accumulators slide.</summary>
    public bool NoExclusion => Exclusion == FrameExclusion.NoOthers;

    /// <summary>
    /// Whether row <paramref name="candidate"/> is excluded from row <paramref name="current"/>'s
    /// frame. SQL's four exclusions, all defined against the current row's peer group.
    /// </summary>
    public bool Excluded(int current, int candidate) => Exclusion switch
    {
        FrameExclusion.CurrentRow => candidate == current,
        FrameExclusion.Group => candidate >= PeerStart[current] && candidate < PeerEnd[current],
        FrameExclusion.Ties =>
            candidate != current
            && candidate >= PeerStart[current]
            && candidate < PeerEnd[current],
        _ => false,
    };
}
