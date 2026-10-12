using Chalk.Catalog;
using Chalk.Client;

namespace Chalk.Incremental;

/// <summary>
/// How a view prepares its statement (D340, <c>docs/design/65-views-over-databases.md</c> §5): the
/// host's options, with the view's cap on local joins in the join policy and, for a view that reads
/// databases, every cross-source join local, so that no database read takes values from the rest of
/// the statement and each can be kept between refreshes.
/// </summary>
internal static class ViewPrepare
{
    /// <summary>
    /// A rule for every pair of sources: join locally. The view adds it after the host's own rules,
    /// so it decides every pair those leave, ahead of the catalog's rules.
    /// </summary>
    private static readonly SourcePairRule Local = new()
    {
        Allowed = [JoinStrategy.Local],
        Preferred = JoinStrategy.Local,
    };

    public static PrepareOptions For<TKey, TRow>(ViewOptions<TKey, TRow> options)
        where TKey : unmanaged
        where TRow : unmanaged
    {
        var host = options.Prepare ?? new PrepareOptions();
        var policy = host.JoinPolicy;
        if (policy is { LocalJoinMaxRows: > 0 } && policy.LocalJoinMaxRows != options.LocalJoinMaxRows)
        {
            throw new ArgumentException(
                $"the view's LocalJoinMaxRows is {options.LocalJoinMaxRows}, and its Prepare.JoinPolicy sets "
                + $"another, {policy.LocalJoinMaxRows}. State the cap once, in ViewOptions.LocalJoinMaxRows.",
                nameof(options));
        }

        List<SourcePairRule> pairs = [.. policy?.Pairs ?? []];
        if (options.DatabaseReads == ViewDatabaseReads.WhenRefreshed)
        {
            pairs.Add(Local);
        }

        return host with
        {
            JoinPolicy = new CrossSourceJoinPolicy
            {
                DefaultStrategy = policy?.DefaultStrategy ?? JoinStrategy.Unspecified,
                BroadcastMaxRows = policy?.BroadcastMaxRows ?? 0,
                LookupMaxCalls = policy?.LookupMaxCalls ?? 0,
                LocalJoinMaxRows = options.LocalJoinMaxRows,
                UnknownRowCountAssumption = policy?.UnknownRowCountAssumption ?? 0,
                Pairs = pairs,
            },
        };
    }
}
