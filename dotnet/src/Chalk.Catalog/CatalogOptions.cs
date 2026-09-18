using Chalk.Catalog;

namespace Chalk.Entitlements;

/// <summary>
/// Catalog-wide entitlement settings a host may change, none of which costs anything left alone
/// (<c>docs/design/16-entitlements.md</c> §6).
/// </summary>
/// <remarks>
/// In the <c>Chalk.Entitlements</c> namespace and the <c>Chalk.Catalog</c> assembly, as the
/// descriptor types are (step 26c, V89): both of its members are about entitlements — what
/// registration requires, and what a placeholder holds — so a host that never writes a policy never
/// meets the type, and a host that does finds it beside the descriptors it belongs with.
/// </remarks>
public sealed class CatalogOptions
{
    /// <summary>Every default: nothing required, and no group-size floor (D211).</summary>
    public static CatalogOptions Default { get; } = new();

    /// <summary>
    /// Registration fails for any table that neither carries an entitlement nor is marked
    /// <see cref="TableDescriptor.IsPublic"/> (D154) — for a host that wants forgetting to be
    /// impossible. Off by default, which is what keeps the layer free when unused.
    /// </summary>
    public bool RequireEntitlements { get; init; }

    /// <summary>
    /// What a placeholder holds when a prepare does not say (D162). The host's default; a client may
    /// still choose per prepare.
    /// </summary>
    public PlaceholderPolicy PlaceholderPolicy { get; init; } = PlaceholderPolicy.PlaceholdersAsNull;
}

/// <summary>
/// What a placeholder holds for an undisclosed column (D162). A matching rule's own placeholder wins
/// over the column's, and the column's over either of these (D224).
/// </summary>
public enum PlaceholderPolicy
{
    Unspecified = 0,

    /// <summary>
    /// The default: a typed NULL, the output type widened to nullable even where the base column is
    /// NOT NULL — a value that looks like data is not an acknowledgement.
    /// </summary>
    PlaceholdersAsNull = 1,

    /// <summary>
    /// The planner's own empty value where it defines one, and a typed NULL where it does not
    /// (F47). "You know what you are doing."
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value is the planner's rather than Chalk's, because "empty" is a policy choice and not a
    /// property of a type, and a zero table of Chalk's own would be a second opinion nobody asked
    /// for. The table it comes to, as informed by the user's own review of it:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>''</c> for STRING and empty bytes for BINARY;</description></item>
    /// <item><description><c>false</c> for BOOL;</description></item>
    /// <item><description>0 for every integer width, for FP32 and FP64, and for DECIMAL;</description></item>
    /// <item><description>the epoch for DATE, TIME and TIMESTAMP — but <b>year one</b>
    /// (<c>0001-01-01</c>) for TIMESTAMP_TZ, which is the planner's own value and not the
    /// epoch;</description></item>
    /// <item><description><b>a typed NULL</b>, with the output widened to nullable exactly as under
    /// <see cref="PlaceholdersAsNull"/>, for UUID, LIST and the two interval types, for which the
    /// planner defines no empty value at all. Never an error.</description></item>
    /// </list>
    /// <para>
    /// Where there is a value the column's declared nullability is kept, which is the point of the
    /// policy. A host that needs a non-null stand-in for one of the last four names it on the
    /// column, or on the rule that withholds it.
    /// </para>
    /// </remarks>
    PlaceholdersAsEmpty = 2,
}
