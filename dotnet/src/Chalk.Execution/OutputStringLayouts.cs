using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution;

/// <summary>
/// Which Arrow layout each STRING column of a plan's output row is declared in (D244).
/// </summary>
/// <remarks>
/// <para>
/// A pure function of the compiled plan and the sources' declared native layouts, so the same
/// prepared query declares the same schema every time it is prepared. Nothing here changes what the
/// materialiser is <em>able</em> to emit: whatever arrives, the declared layout is what comes out.
/// This only chooses declarations so that the conversion is rare.
/// </para>
/// <para>
/// Under <see cref="StringLayouts.Any"/> a column is declared <see cref="StringLayouts.Utf8"/> when
/// it reaches the output untouched from a leaf whose source declares classic strings, and
/// <see cref="StringLayouts.Utf8View"/> otherwise. "Untouched" is the narrow reading: a
/// <c>Project</c> passes a bare <c>FieldRef</c> through and computes everything else, a <c>Fetch</c>
/// narrows a batch without rebuilding a column, and a <c>PartitionedScan</c> passes through when
/// every partition agrees. Every other node re-materialises the column — a join, a sort, a TopN, an
/// aggregate, a set operator, a window, an UNNEST, a table function, a literal table — and so does a
/// <c>Filter</c>, because whether it compacts or forwards a selection is a per-batch decision
/// (<c>SelectionCompactionThreshold</c>) and a declaration has to be the same on every batch.
/// </para>
/// </remarks>
internal static class OutputStringLayouts
{
    /// <summary>
    /// One layout per output column, in order. Columns that hold no STRING get a layout too — it is
    /// simply never read.
    /// </summary>
    public static IReadOnlyList<StringLayouts> Resolve(
        Plan plan,
        StringLayouts accepted,
        IReadOnlyDictionary<string, ISourceRuntime> sources)
    {
        var count = plan.OutputType.Fields.Count;
        var layouts = new StringLayouts[count];

        if (accepted != StringLayouts.Any)
        {
            // One acceptable layout: every column declares it, and there is nothing to resolve.
            Array.Fill(layouts, accepted);
            return layouts;
        }

        for (var i = 0; i < count; i++)
        {
            layouts[i] = Resolve(plan.Root, i, sources);
        }

        return layouts;
    }

    /// <summary>
    /// The layout column <paramref name="column"/> of <paramref name="rel"/>'s output row reaches
    /// the host in, following the reference through the nodes that pass one.
    /// </summary>
    private static StringLayouts Resolve(
        Rel rel,
        int column,
        IReadOnlyDictionary<string, ISourceRuntime> sources)
    {
        while (true)
        {
            switch (rel.KindCase)
            {
                case Rel.KindOneofCase.Read:
                    return Native(rel.Read.Table.SourceId, sources);

                case Rel.KindOneofCase.IndexLookup:
                    return Native(rel.IndexLookup.Table.SourceId, sources);

                case Rel.KindOneofCase.RemoteQuery:
                    return Native(rel.RemoteQuery.SourceId, sources);

                case Rel.KindOneofCase.Project:
                {
                    var project = rel.Project;
                    if (column >= project.Exprs.Count
                        || project.Exprs[column].KindCase != Expr.KindOneofCase.FieldRef)
                    {
                        // A computed column is built by an expression's own scratch, which is views.
                        return StringLayouts.Utf8View;
                    }

                    column = (int)project.Exprs[column].FieldRef.Index;
                    rel = project.Input;
                    continue;
                }

                case Rel.KindOneofCase.Fetch:
                    rel = rel.Fetch.Input;
                    continue;

                case Rel.KindOneofCase.PartitionedScan:
                {
                    // Every partition has this node's row type, and the host sees whichever of them
                    // produced the batch: a declaration they do not all agree on is not one.
                    var partitions = rel.PartitionedScan.Partitions;
                    if (partitions.Count == 0)
                    {
                        return StringLayouts.Utf8View;
                    }

                    var first = Resolve(partitions[0], column, sources);
                    for (var i = 1; i < partitions.Count; i++)
                    {
                        if (Resolve(partitions[i], column, sources) != first)
                        {
                            return StringLayouts.Utf8View;
                        }
                    }

                    return first;
                }

                default:
                    return StringLayouts.Utf8View;
            }
        }
    }

    /// <summary>
    /// What a source says its strings are in, and views for a source the compiler cannot find — the
    /// compiler raises that as its own error a moment later, and a layout is not the place to say so.
    /// </summary>
    private static StringLayouts Native(
        string sourceId,
        IReadOnlyDictionary<string, ISourceRuntime> sources) =>
        sources.TryGetValue(sourceId, out var source)
        && source.NativeStringLayout == StringLayouts.Utf8
            ? StringLayouts.Utf8
            : StringLayouts.Utf8View;
}
