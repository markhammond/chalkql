using Chalk.Entitlements;

namespace Chalk.TestKit;

/// <summary>
/// One named entitlement of the battery: the table it is for, and the descriptor itself
/// (<c>docs/design/16-entitlements.md</c> §1).
/// </summary>
public sealed record PolicyEntitlement(
    string Name,
    string Table,
    TableEntitlementDescriptor Descriptor);

/// <summary>
/// Where the battery's statements live: <c>corpus/policy/cases/</c>, one file per case
/// (<c>corpus/policy/README.md</c>).
/// </summary>
/// <remarks>
/// Everything else the battery is — the fixture, the entitlements, the principals, the expectations
/// and the judged disagreements — is code, in <see cref="PolicyFixture"/>,
/// <see cref="PolicyEntitlements"/>, <see cref="PolicyPrincipals"/> and <see cref="PolicyCases"/>.
/// The SQL stays a file because a statement is the one part of a case a reader wants to read on its
/// own, and because the corpus names each one.
/// </remarks>
public static class PolicyStatements
{
    /// <summary><c>corpus/policy</c> itself.</summary>
    public static DirectoryInfo Directory { get; } =
        new(Path.Combine(RepoLayout.Corpus.FullName, "policy"));

    private static readonly Dictionary<string, string> Read = new(StringComparer.Ordinal);

    /// <summary>The statement a case names, read once.</summary>
    public static string Of(PolicyBatteryCase battery)
    {
        ArgumentNullException.ThrowIfNull(battery);
        lock (Read)
        {
            if (!Read.TryGetValue(battery.File, out var sql))
            {
                var path = Path.Combine(Directory.FullName, battery.File);
                if (!File.Exists(path))
                {
                    throw new InvalidOperationException(
                        $"case {battery.Id} names {battery.File} and corpus/policy has no such file.");
                }

                sql = File.ReadAllText(path).Trim();
                Read[battery.File] = sql;
            }

            return sql;
        }
    }
}
