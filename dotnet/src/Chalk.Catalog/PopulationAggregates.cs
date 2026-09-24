namespace Chalk.Entitlements;

/// <summary>
/// The aggregates a column may permit over an <see cref="Disclosure.AggregateOnly"/> value (D190,
/// <c>docs/design/16-entitlements.md</c> §1).
/// </summary>
/// <remarks>
/// <para>
/// The set is closed because the question is not "is this a function" but "does this report a
/// population or an individual". <c>MIN</c>, <c>MAX</c> and <c>ANY_VALUE</c> report one row's value
/// however many rows are in the group; so do the positional aggregates (<c>FIRST_VALUE</c>,
/// <c>LAST_VALUE</c>, <c>NTH_VALUE</c>, <c>MODE</c>), the holistic ones (<c>PERCENTILE_CONT</c> and
/// friends) and every string aggregate (<c>LISTAGG</c>, <c>STRING_AGG</c>, <c>ARRAY_AGG</c>), which
/// carry the values themselves. A user-defined aggregate is refused because nothing here can know
/// what it does — unless its host declared it <c>Population()</c>, promising that its result reports
/// the group and never one row's value, which <see cref="IsPermitted(string, Chalk.Catalog.CatalogContext)"/> asks
/// of the catalog (D295).
/// </para>
/// <para>
/// The names are the SQL ones, matched case-insensitively after trimming. <c>COUNT</c> covers
/// <c>COUNT(DISTINCT …)</c>, which is the same operator with a flag, and <c>SUM0</c> is the form
/// <c>AGGREGATE_REDUCE_FUNCTIONS</c> rewrites <c>AVG</c> into — permitting it is what keeps a
/// correct plan from being refused after the rewrite.
/// </para>
/// </remarks>
public static class PopulationAggregates
{
    private static readonly HashSet<string> Permitted = new(StringComparer.Ordinal)
    {
        "COUNT",
        "SUM",
        "SUM0",
        "AVG",
        "STDDEV",
        "STDDEV_POP",
        "STDDEV_SAMP",
        "VARIANCE",
        "VAR_POP",
        "VAR_SAMP",
        "COVAR_POP",
        "COVAR_SAMP",
        "CORR",
        "REGR_COUNT",
        "REGR_AVGX",
        "REGR_AVGY",
        "REGR_INTERCEPT",
        "REGR_R2",
        "REGR_SLOPE",
        "REGR_SXX",
        "REGR_SXY",
        "REGR_SYY",
        "BOOL_AND",
        "BOOL_OR",
        "EVERY",
        "SOME",
        "APPROX_COUNT_DISTINCT",
    };

    /// <summary>The permitted names, for a message that has to say what they are.</summary>
    public static string Listing { get; } = string.Join(", ", Permitted.Order(StringComparer.Ordinal));

    /// <summary>
    /// Whether <paramref name="function"/> names a population aggregate. Case-insensitive; leading
    /// and trailing space is ignored.
    /// </summary>
    public static bool IsPermitted(string function) =>
        function is not null && Permitted.Contains(function.Trim().ToUpperInvariant());

    /// <summary>
    /// Whether <paramref name="function"/> may be named in an allow-list of <paramref name="catalog"/>
    /// (D295): a population aggregate of the fixed set, or an aggregate the catalog declares
    /// <c>Population()</c>. An allow-list names an aggregate without its schema, so every function
    /// of that name in every schema must be such an aggregate, and at least one must exist: a name
    /// another schema declares without the promise is never permitted by one that makes it.
    /// </summary>
    public static bool IsPermitted(string function, Chalk.Catalog.CatalogContext catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (IsPermitted(function))
        {
            return true;
        }

        if (function is null)
        {
            return false;
        }

        var name = function.Trim();
        var declared = false;
        foreach (var schema in catalog.Schemas)
        {
            foreach (var candidate in schema.Functions)
            {
                if (!string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (candidate.Kind != Chalk.Ir.FunctionKind.Aggregate || !candidate.Population)
                {
                    return false;
                }

                declared = true;
            }
        }

        return declared;
    }
}
