using Chalk.Catalog;
using Chalk.Ir;
using SourceCapabilities = Chalk.Catalog.SourceCapabilities;

namespace Chalk.Sources.Ado;

/// <summary>
/// What an ADO.NET source over a given dialect can be asked to do (§3). A starting point, not a
/// promise: <see cref="For"/> describes what the <em>engine</em> supports, and whether a particular
/// database matches is what the conformance kit answers.
/// </summary>
/// <remarks>
/// Every claim here is one the engine's own documentation makes, and every one that depends on the
/// database rather than the engine is left to the profile: string comparison, null placement,
/// numeric precision. The pushdown gate then applies the drift rules of D89 on top, so a claim that
/// would change an answer is refused even where it was made honestly.
/// </remarks>
public static class AdoCapabilities
{
    /// <summary>
    /// A full descriptor for a dialect Chalk ships a profile for. Everything a mainstream SQL engine
    /// does: comparisons, ranges, <c>IN</c>, <c>IS NULL</c>, <c>LIKE</c>, conjunction and
    /// disjunction, projection, sorting, fetching, grouping, and every join shape.
    /// </summary>
    /// <param name="profile">The dialect. Its string collation gates the string shapes.</param>
    /// <param name="maxInList">The longest <c>IN</c> list to push. 1 000 is well under every engine's limit.</param>
    public static SourceCapabilities For(DialectProfileDescriptor profile, int maxInList = 1000)
    {
        ArgumentNullException.ThrowIfNull(profile);

        List<PredicateShape> shapes =
        [
            PredicateShape.Eq,
            PredicateShape.Range,
            PredicateShape.In,
            PredicateShape.IsNull,
            PredicateShape.Not,
            PredicateShape.And,
            PredicateShape.Or,
        ];

        // A LIKE the source evaluates under another collation matches different rows, and the
        // catalog validator refuses the pair outright — so the shape is only claimed where the
        // profile says the comparison is Chalk's own (D89).
        //
        // SQLite is the exception, and the conformance kit is what found it: its `=` really is
        // binary, but its `LIKE` folds ASCII case unless the connection has set
        // `PRAGMA case_sensitive_like = ON`. So the preset does not claim the LIKE shapes there; a
        // host that has set the pragma adds them back and runs the kit to confirm.
        if (profile.StringCollation == StringCollation.Binary
            && !profile.Dialect.Equals("sqlite", StringComparison.OrdinalIgnoreCase))
        {
            shapes.Add(PredicateShape.Like);
            shapes.Add(PredicateShape.LikePrefix);
        }

        return new SourceCapabilities
        {
            QueryLanguage = QueryLanguage.Sql,
            PushablePredicates = shapes,
            PushableFunctions = ScalarFunctions,
            PushableAggregates = Aggregates,
            SupportsProject = true,
            SupportsSort = true,
            SupportsLimit = true,
            SupportsOffset = true,
            SupportsDistinct = true,
            SupportsGroupBy = true,
            SupportsHaving = true,
            SupportsInnerJoin = true,
            SupportsOuterJoin = true,
            SupportsSemiAntiJoin = true,
            MaxInList = maxInList,
            SupportsParameters = true,

            // `x IN (VALUES (…), (…))` is a join against an inline relation, and every dialect this
            // source ships a profile for parses it: PostgreSQL and DuckDB natively, SQLite since
            // 3.8.3, where VALUES is a select statement in its own right. That is what makes
            // JOIN_STRATEGY_BROADCAST possible (M5).
            SupportsValuesJoin = maxInList > 0,

            // F50: `(a, b) IN ((?, ?), (?, ?))`. Declared for the two dialects it was measured on
            // — DuckDB 1.5.1 and PostgreSQL 16, both on 2026-09-11, both accepting it with `?`
            // placeholders bound positionally — and not for SQLite, which is left to the hash join
            // the composite case has always taken. SQLite's own row values would parse it (3.15
            // and later), but nothing here has measured that, and an undeclared capability costs a
            // plan shape rather than an answer.
            SupportsRowValueInList = maxInList > 0 && RowValuesMeasured(profile.Dialect),
        };
    }

    /// <summary>
    /// The dialects whose row-constructor <c>IN</c> list Chalk has measured (F50, ADR 0027). A
    /// preset claims only what was run against it.
    /// </summary>
    private static bool RowValuesMeasured(string dialect) =>
        dialect.Equals("duckdb", StringComparison.OrdinalIgnoreCase)
        || dialect.Equals("postgresql", StringComparison.OrdinalIgnoreCase)
        || dialect.Equals("postgres", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The same, restricted to predicates and projection — a source Chalk should fetch from but not
    /// compute in. What a host reaches for when a database is shared with something latency-sensitive.
    /// </summary>
    public static SourceCapabilities FiltersOnly(DialectProfileDescriptor profile, int maxInList = 1000)
    {
        var full = For(profile, maxInList);
        return new SourceCapabilities
        {
            QueryLanguage = QueryLanguage.Sql,
            PushablePredicates = full.PushablePredicates,
            PushableFunctions = full.PushableFunctions,
            SupportsProject = true,
            MaxInList = maxInList,
            SupportsParameters = true,

            // `x IN (VALUES (…), (…))` is a join against an inline relation, and every dialect this
            // source ships a profile for parses it: PostgreSQL and DuckDB natively, SQLite since
            // 3.8.3, where VALUES is a select statement in its own right. That is what makes
            // JOIN_STRATEGY_BROADCAST possible (M5).
            SupportsValuesJoin = maxInList > 0,
            SupportsRowValueInList = maxInList > 0 && RowValuesMeasured(profile.Dialect),
        };
    }

    /// <summary>
    /// The scalar functions the in-box source claims. Deliberately the arithmetic, comparison and
    /// string operations every SQL engine spells the same way — anything whose spelling or
    /// semantics varies (date arithmetic, string padding, regular expressions) is left out, because
    /// a function that means something slightly different in the source is exactly the silent wrong
    /// answer D89 exists to prevent.
    /// </summary>
    public static IReadOnlyList<FunctionId> ScalarFunctions { get; } =
    [
        FunctionId.Add,
        FunctionId.Subtract,
        FunctionId.Multiply,

        // F27 (ADR 0027): DIVIDE is declared here for all three dialects, and for DuckDB only
        // because the planner writes `//` there. DuckDB's `/` is real division whatever its
        // operands are -- `5 / 2` is 2.5 and `-7 / 3` is -2.3333333333333335, both DOUBLE, measured
        // on 1.5.1 -- so a pushed integer division came back a DOUBLE against a declared integer,
        // and inside a pushed predicate it was evaluated as real division, which nothing would have
        // caught. `//` truncates towards zero and answers in the operands' own type, which is what
        // SQLite, PostgreSQL and Chalk all do with `/`. The dialect and the kit's integer-division
        // probe together are the evidence, and neither alone would be -- the probe passed the
        // DOUBLE for a year because it cast the result before looking at it.
        FunctionId.Divide,

        // D168: MOD and `||` are declared on the kit's own evidence rather than on a reading of
        // three manuals. The `modulus of a negative` probe says all three dialects take the sign of
        // the dividend, as Chalk does, and the `empty string versus NULL` probe says an empty string
        // is a value -- which between them are what a pushed MOD and a pushed concatenation depend
        // on. A dialect that answered otherwise would fail the kit before a query reached it, which
        // is the model D89 asks for.
        //
        // The sign is not the whole of MOD, though, and this list is what found the rest (V53, ADR
        // 0024). That probe casts to DOUBLE on purpose, so it is silent about the *type* a source
        // answers in: SQLite's own mod() is a floating-point math function that returns REAL and
        // loses a wide dividend through a double. What makes MOD pushable there is the planner's
        // SQLite dialect writing `a % b`, which is exact and integral; the probe and the dialect
        // together are the evidence, and neither alone would be.
        FunctionId.Modulus,
        FunctionId.Concat,
        FunctionId.Negate,
        FunctionId.Abs,
        FunctionId.Upper,
        FunctionId.Lower,
        FunctionId.CharLength,
        FunctionId.Trim,
        FunctionId.Coalesce,
        FunctionId.Nullif,
    ];

    /// <summary>
    /// The aggregates the in-box source claims. <c>COUNT</c>, <c>MIN</c> and <c>MAX</c> are exact
    /// everywhere; <c>SUM</c> and <c>SUM0</c> are exact for integers and follow the profile's
    /// precision for decimals, which the gate checks. <c>AVG</c> is not here: the planner reduces it
    /// to <c>SUM0 / COUNT</c> before a rule ever sees it (A12), and a source's own <c>AVG</c> may
    /// round differently.
    /// </summary>
    public static IReadOnlyList<AggregateFunctionId> Aggregates { get; } =
    [
        AggregateFunctionId.Count,
        AggregateFunctionId.Sum,
        AggregateFunctionId.Sum0,
        AggregateFunctionId.Min,
        AggregateFunctionId.Max,
    ];
}
