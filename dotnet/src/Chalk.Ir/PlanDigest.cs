using System.Buffers.Binary;
using System.Security.Cryptography;
using Google.Protobuf;

namespace Chalk.Ir;

/// <summary>
/// The plan digest: the golden-plan test key and the telemetry correlation id
/// (<c>docs/design/02-ir.md</c> §7).
/// <code>
/// canonical(plan) = plan with plan_digest := 0, context_id := "", catalog_epoch := 0,
///                   and every Rel.est_row_count := 0 (recursively), serialised with
///                   deterministic protobuf encoding
/// plan_digest     = little-endian uint64 of the first 8 bytes of SHA-256(canonical(plan))
/// </code>
/// Estimates are excluded on purpose: a statistics refresh must not churn golden files, but a rule
/// or cost change that alters plan <em>shape</em> must. The planner computes the same value; the
/// two implementations are tested against shared fixtures.
/// </summary>
public static class PlanDigest
{
    /// <summary>
    /// Zeroes every estimate, including the ones inside a <c>RemoteQuery</c>'s pushed plan (D84).
    /// The pushed subtree is part of the plan's shape, so it must be canonicalised like the rest —
    /// otherwise a statistics refresh would churn every digest that has a remote query in it.
    /// </summary>
    private static void StripEstimates(Rel root)
    {
        // The inclusive walk, which is what "part of the plan's shape" means here (F92).
        foreach (var rel in PlanWalker.Rels(root))
        {
            rel.EstRowCount = 0;
        }
    }

    /// <summary>Computes the digest of the plan's canonical form.</summary>
    public static ulong Compute(Plan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var bytes = Canonicalise(plan).ToByteArray();
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return BinaryPrimitives.ReadUInt64LittleEndian(hash);
    }

    /// <summary>
    /// The canonical form the digest is taken over. Exposed so the corpus tool can show exactly what
    /// was hashed when two implementations disagree.
    /// </summary>
    public static Plan Canonicalise(Plan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var copy = plan.Clone();
        copy.PlanDigest = 0;
        copy.ContextId = string.Empty;
        copy.CatalogEpoch = 0;
        if (copy.Root is not null)
        {
            StripEstimates(copy.Root);
        }

        return copy;
    }

    /// <summary>The 16-hex-character form written to <c>corpus/plans/m1/*.digest</c>.</summary>
    public static string Format(ulong digest) => digest.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Parses the 16-hex-character form. Whitespace around it is ignored.</summary>
    public static ulong Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ulong.Parse(
            text.Trim(),
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
