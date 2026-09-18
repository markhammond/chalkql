using Chalk.Ir;

namespace Chalk.Catalog;

/// <summary>
/// The dialect profiles Chalk ships as data (D82, D85). Each is a starting point an adapter copies
/// and overrides: <c>DialectProfiles.Sqlite with { StringCollation = StringCollation.Binary }</c> is
/// not available on a class with <c>init</c> properties, so the presets expose
/// <see cref="With"/> — or just write a whole descriptor.
/// </summary>
/// <remarks>
/// <para>
/// A preset is a claim about a <em>typical</em> installation of that engine, and every claim here is
/// one the conformance kit checks. Two are deliberately conservative rather than typical:
/// </para>
/// <list type="bullet">
/// <item>
/// SQLite's default text collation is <c>BINARY</c>, but a column declared
/// <c>COLLATE NOCASE</c> — or a database built with a custom collation — is not, and nothing in the
/// schema tells the driver which. So <see cref="Sqlite"/> claims
/// <see cref="StringCollation.Binary"/> because that is the default the vast majority of tables
/// have, and the kit's collation probe is what makes the claim honest for a given database: rev 3's
/// lying adapter is exactly this preset over a <c>NOCASE</c> table.
/// </item>
/// <item>
/// None of the presets claims <see cref="DialectProfileDescriptor.ImplicitCoercionMatches"/>, so
/// every generated cast is explicit. That costs some SQL width and removes a whole class of silent
/// disagreement.
/// </item>
/// </list>
/// </remarks>
public static class DialectProfiles
{
    /// <summary>
    /// Standard SQL with nothing engine-specific: double-quoted identifiers, binary collation,
    /// explicit null placement. What an unknown SQL source should start from.
    /// </summary>
    public static DialectProfileDescriptor Ansi { get; } = new()
    {
        Dialect = "ansi",
        Quoting = IdentifierQuoting.DoubleQuote,
        QuotedCasing = IdentifierCasing.Unchanged,
        UnquotedCasing = IdentifierCasing.Unchanged,
        CaseSensitiveIdentifiers = true,
        Conformance = SqlConformance.Default,
        MaxNumericPrecision = 38,
        MaxTimestampPrecision = 9,
        HasBoolean = true,
        DefaultNullCollation = NullCollation.High,
        SupportsNullOrderingClause = true,
        StringCollation = StringCollation.Binary,
        ParameterPlaceholder = ParameterPlaceholder.Question,
    };

    /// <summary>
    /// SQLite. Dynamic typing, so the declared column type is what Chalk trusts and the reader
    /// checks each value against it. <c>LIMIT … OFFSET …</c> rather than <c>FETCH</c>; no
    /// <c>NULLS FIRST/LAST</c> before 3.30, and Chalk does not assume a version, so the clause is
    /// declared unsupported and the default placement (NULLs first ascending, i.e. LOW) is what a
    /// pushed sort has to match. DECIMAL is REAL underneath, so the precision is the double's.
    /// </summary>
    public static DialectProfileDescriptor Sqlite { get; } = new()
    {
        Dialect = "sqlite",
        Quoting = IdentifierQuoting.DoubleQuote,
        QuotedCasing = IdentifierCasing.Unchanged,
        UnquotedCasing = IdentifierCasing.Unchanged,
        CaseSensitiveIdentifiers = false,
        Conformance = SqlConformance.Lenient,
        MaxNumericPrecision = 15,
        MaxTimestampPrecision = 3,
        HasBoolean = false,
        DefaultNullCollation = NullCollation.Low,
        SupportsNullOrderingClause = false,
        StringCollation = StringCollation.Binary,
        ParameterPlaceholder = ParameterPlaceholder.NamedDollar,
        DynamicallyTyped = true,
    };

    /// <summary>
    /// DuckDB. PostgreSQL-shaped SQL with a full type system: exact DECIMAL to 38 digits,
    /// microsecond timestamps, a real BOOLEAN, and <c>NULLS FIRST/LAST</c> in <c>ORDER BY</c>.
    /// </summary>
    public static DialectProfileDescriptor DuckDb { get; } = new()
    {
        Dialect = "duckdb",
        Quoting = IdentifierQuoting.DoubleQuote,
        QuotedCasing = IdentifierCasing.Unchanged,
        UnquotedCasing = IdentifierCasing.Unchanged,
        CaseSensitiveIdentifiers = false,
        Conformance = SqlConformance.Lenient,
        MaxNumericPrecision = 38,
        MaxTimestampPrecision = 6,
        HasBoolean = true,
        DefaultNullCollation = NullCollation.Last,
        SupportsNullOrderingClause = true,
        StringCollation = StringCollation.Binary,
        ParameterPlaceholder = ParameterPlaceholder.NamedDollar,
    };

    /// <summary>
    /// PostgreSQL. NULLs sort high by default; identifiers fold to lower case unquoted; DECIMAL is
    /// exact and TIMESTAMP is microseconds. The string collation is deliberately
    /// <see cref="StringCollation.Locale"/>: a PostgreSQL database's default collation comes from
    /// the cluster's locale, and only <c>C</c> / <c>POSIX</c> is binary. A host whose database is
    /// <c>C</c>-collated overrides the field and the kit's probe confirms it.
    /// </summary>
    public static DialectProfileDescriptor PostgreSql { get; } = new()
    {
        Dialect = "postgresql",
        Quoting = IdentifierQuoting.DoubleQuote,
        QuotedCasing = IdentifierCasing.Unchanged,
        UnquotedCasing = IdentifierCasing.ToLower,
        CaseSensitiveIdentifiers = true,
        Conformance = SqlConformance.Lenient,
        MaxNumericPrecision = 38,
        MaxTimestampPrecision = 6,
        HasBoolean = true,
        DefaultNullCollation = NullCollation.High,
        SupportsNullOrderingClause = true,
        StringCollation = StringCollation.Locale,
        ParameterPlaceholder = ParameterPlaceholder.NamedAt,
    };

    /// <summary>The preset a name selects, case-insensitively. <see cref="Ansi"/> for anything else.</summary>
    public static DialectProfileDescriptor ByName(string? name) => name?.ToLowerInvariant() switch
    {
        "sqlite" => Sqlite,
        "duckdb" => DuckDb,
        "postgresql" or "postgres" => PostgreSql,
        _ => Ansi,
    };

    /// <summary>
    /// A copy of <paramref name="profile"/> with the given overrides — the <c>with</c> expression an
    /// <c>init</c>-property class does not get. Every parameter left null keeps the preset's value.
    /// </summary>
    public static DialectProfileDescriptor With(
        this DialectProfileDescriptor profile,
        StringCollation? stringCollation = null,
        bool? supportsNullOrderingClause = null,
        NullCollation? defaultNullCollation = null,
        SqlConformance? conformance = null,
        IReadOnlyList<SqlLibrary>? libraries = null,
        uint? maxNumericPrecision = null,
        uint? maxTimestampPrecision = null,
        bool? hasBoolean = null,
        string? timeZone = null,
        bool? approximateDecimal = null,
        bool? approximateDistinctCount = null,
        bool? approximateTopN = null,
        bool? implicitCoercionMatches = null,
        ParameterPlaceholder? parameterPlaceholder = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new DialectProfileDescriptor
        {
            Dialect = profile.Dialect,
            Quoting = profile.Quoting,
            QuotedCasing = profile.QuotedCasing,
            UnquotedCasing = profile.UnquotedCasing,
            CaseSensitiveIdentifiers = profile.CaseSensitiveIdentifiers,
            Conformance = conformance ?? profile.Conformance,
            Libraries = libraries ?? profile.Libraries,
            MaxNumericPrecision = maxNumericPrecision ?? profile.MaxNumericPrecision,
            MaxTimestampPrecision = maxTimestampPrecision ?? profile.MaxTimestampPrecision,
            HasBoolean = hasBoolean ?? profile.HasBoolean,
            TimeZone = timeZone ?? profile.TimeZone,
            DefaultNullCollation = defaultNullCollation ?? profile.DefaultNullCollation,
            SupportsNullOrderingClause =
                supportsNullOrderingClause ?? profile.SupportsNullOrderingClause,
            StringCollation = stringCollation ?? profile.StringCollation,
            ApproximateDecimal = approximateDecimal ?? profile.ApproximateDecimal,
            ApproximateDistinctCount = approximateDistinctCount ?? profile.ApproximateDistinctCount,
            ApproximateTopN = approximateTopN ?? profile.ApproximateTopN,
            ImplicitCoercionMatches = implicitCoercionMatches ?? profile.ImplicitCoercionMatches,
            ParameterPlaceholder = parameterPlaceholder ?? profile.ParameterPlaceholder,
        };
    }
}
