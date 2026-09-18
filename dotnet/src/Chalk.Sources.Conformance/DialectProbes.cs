using System.Globalization;
using Chalk.Catalog;
using Chalk.Ir;

namespace Chalk.Sources.Conformance;

/// <summary>
/// §5 (b): the dialect probes. Each asks the source one question whose answer the profile claims to
/// know, and reports "declared X, observed Y".
/// </summary>
/// <remarks>
/// These are the questions that cannot be answered by reading a manual, because the answer depends
/// on how <em>this</em> database was built: a SQLite table declared <c>COLLATE NOCASE</c>, a
/// PostgreSQL cluster with a locale, a column whose dates went in as Julian day numbers. Rev 3's
/// deliberately-lying adapter is exactly the first of those, and the collation probe is what
/// catches it.
/// </remarks>
internal static class DialectProbes
{
    public static async Task<IReadOnlyList<ConformanceFinding>> RunAsync(
        ConformanceContext context, CancellationToken ct)
    {
        var findings = new List<ConformanceFinding>();
        if (context.Capabilities.QueryLanguage != QueryLanguage.Sql)
        {
            findings.Add(new ConformanceFinding
            {
                Subject = "dialect probes",
                Declared = context.Capabilities.QueryLanguage.ToString(),
                Observed = "the probes are SQL, so only a SQL source can answer them",
                Outcome = ConformanceOutcome.Skipped,
            });
            return findings;
        }

        findings.Add(await CollationAsync(context, ct).ConfigureAwait(false));
        findings.Add(await AccentAsync(context, ct).ConfigureAwait(false));
        findings.Add(await LikeCaseAsync(context, ct).ConfigureAwait(false));
        findings.Add(await NullOrderingAsync(context, ct).ConfigureAwait(false));
        findings.Add(await BetweenAsync(context, ct).ConfigureAwait(false));
        findings.Add(await EmptyStringAsync(context, ct).ConfigureAwait(false));
        findings.Add(await IntegerDivisionAsync(context, ct).ConfigureAwait(false));
        findings.Add(await ModulusAsync(context, ct).ConfigureAwait(false));
        findings.Add(await DecimalPrecisionAsync(context, ct).ConfigureAwait(false));
        findings.Add(await TimestampPrecisionAsync(context, ct).ConfigureAwait(false));
        findings.Add(await LikeEscapeAsync(context, ct).ConfigureAwait(false));

        // D168 (25-coverage-graft.md §5 E): two more, from findings the assessment of
        // ikvmnet/calcite-dotnet measured on dialects Chalk does not serve but could meet.
        findings.Add(await StringCastTruncationAsync(context, ct).ConfigureAwait(false));
        findings.Add(await ParameterRoundTripAsync(context, ct).ConfigureAwait(false));
        return findings;
    }

    /// <summary>
    /// The first finding rev 3's M4 risks name: does <c>'alpha' = 'ALPHA'</c>? Under Chalk's own
    /// code-point comparison it does not, and a source that says it does is a source no string
    /// predicate may be pushed into.
    /// </summary>
    private static async Task<ConformanceFinding> CollationAsync(
        ConformanceContext context, CancellationToken ct)
    {
        var text = context.Column("text_value");
        var sql = context.SelectIdsWhere($"{text} = 'alpha'");
        var (rows, failure) = await TryAsync(context, sql, ct).ConfigureAwait(false);
        if (failure is not null)
        {
            return Refused("string collation", sql, failure);
        }

        // Row 1 is 'alpha'; rows 2 and 3 are 'ALPHA' and 'Alpha'. A binary collation matches one.
        var matched = rows.Count;
        var observed = matched switch
        {
            1 => StringCollation.Binary,
            > 1 => StringCollation.CaseInsensitive,
            _ => StringCollation.Unspecified,
        };

        var declared = context.Profile.StringCollation;
        if (observed == StringCollation.Unspecified)
        {
            return new ConformanceFinding
            {
                Subject = "string collation",
                Declared = declared.ToString(),
                Observed = "no row matched 'alpha' at all",
                Outcome = ConformanceOutcome.Fail,
                Advice = "The kit's dataset is not in the table, or equality does not work.",
            };
        }

        return declared == StringCollation.Binary && observed != StringCollation.Binary
            ? new ConformanceFinding
            {
                Subject = "string collation",
                Declared = "StringCollation.Binary",
                Observed = $"'alpha' = 'ALPHA' matched {matched} rows, so the comparison ignores case",
                Outcome = ConformanceOutcome.Fail,
                Advice = "Declare StringCollation.CaseInsensitive. Chalk then keeps every string "
                    + "predicate, sort and DISTINCT local (D89), because a case-insensitive source "
                    + "would return different rows.",
            }
            : new ConformanceFinding
            {
                Subject = "string collation",
                Declared = declared.ToString(),
                Observed = observed == StringCollation.Binary
                    ? "'alpha' matched only itself, which is code-point comparison"
                    : $"'alpha' matched {matched} rows, which is not code-point comparison",
                Outcome = ConformanceOutcome.Pass,
            };
    }

    /// <summary>A locale collation makes <c>'café'</c> sort next to <c>'cafe'</c>; binary does not.</summary>
    private static async Task<ConformanceFinding> AccentAsync(
        ConformanceContext context, CancellationToken ct)
    {
        var text = context.Column("text_value");
        var sql = $"SELECT {context.Column("id")} FROM {context.TableName()} "
            + $"WHERE {text} IN ('cafe', 'café', 'cafz') ORDER BY {text}";
        var (rows, failure) = await TryAsync(context, sql, ct).ConfigureAwait(false);
        if (failure is not null)
        {
            return Refused("string ordering (accents)", sql, failure);
        }

        // By code point: 'cafe' (101) < 'cafz' (122) < 'café' (233). By most locales: cafe, café,
        // cafz. The ids are 7, 8, 6 respectively.
        var order = ConformanceValues.Ids(rows);
        var binary = order == "7,8,6";
        var declared = context.Profile.StringCollation;

        return declared == StringCollation.Binary && !binary
            ? new ConformanceFinding
            {
                Subject = "string ordering (accents)",
                Declared = "code-point order: cafe, cafz, café",
                Observed = $"ids {order}",
                Outcome = ConformanceOutcome.Fail,
                Advice = "The source orders strings by a locale, not by code point. Declare "
                    + "StringCollation.Locale so no ORDER BY on a string is pushed (D89).",
            }
            : new ConformanceFinding
            {
                Subject = "string ordering (accents)",
                Declared = declared.ToString(),
                Observed = binary ? "code-point order" : $"ids {order}, which is not code-point order",
                Outcome = ConformanceOutcome.Pass,
            };
    }

    /// <summary>A <c>LIKE</c> can ignore case where <c>=</c> does not; SQLite's does by default.</summary>
    private static async Task<ConformanceFinding> LikeCaseAsync(
        ConformanceContext context, CancellationToken ct)
    {
        var text = context.Column("text_value");
        var sql = context.SelectIdsWhere($"{text} LIKE 'alpha'");
        var (rows, failure) = await TryAsync(context, sql, ct).ConfigureAwait(false);
        if (failure is not null)
        {
            return Refused("LIKE case sensitivity", sql, failure);
        }

        var declaresLike = context.Capabilities.PushablePredicates.Contains(PredicateShape.Like)
            || context.Capabilities.PushablePredicates.Contains(PredicateShape.LikePrefix);
        var caseSensitive = rows.Count == 1;

        return declaresLike && !caseSensitive
            ? new ConformanceFinding
            {
                Subject = "LIKE case sensitivity",
                Declared = "a LIKE shape is pushable, so LIKE compares as Chalk does",
                Observed = $"LIKE 'alpha' matched {rows.Count} rows, so it ignores case",
                Outcome = ConformanceOutcome.Fail,
                Advice = "SQLite's LIKE is case-insensitive for ASCII by default. Remove the LIKE "
                    + "shapes from PushablePredicates, or set PRAGMA case_sensitive_like = ON.",
            }
            : new ConformanceFinding
            {
                Subject = "LIKE case sensitivity",
                Declared = declaresLike ? "a LIKE shape is pushable" : "no LIKE shape is pushable",
                Observed = caseSensitive
                    ? "LIKE 'alpha' matched only 'alpha'"
                    : $"LIKE 'alpha' matched {rows.Count} rows",
                Outcome = ConformanceOutcome.Pass,
            };
    }

    /// <summary>Where the source puts NULLs in an <c>ORDER BY</c> with no explicit clause.</summary>
    private static async Task<ConformanceFinding> NullOrderingAsync(
        ConformanceContext context, CancellationToken ct)
    {
        var text = context.Column("text_value");
        var sql = $"SELECT {context.Column("id")} FROM {context.TableName()} ORDER BY {text}";
        var (rows, failure) = await TryAsync(context, sql, ct).ConfigureAwait(false);
        if (failure is not null)
        {
            return Refused("default NULL placement", sql, failure);
        }

        // Ids 13 and 16 are the two rows whose text_value is NULL.
        var ids = rows.Select(r => ConformanceValues.Text(r[0])).ToList();
        var firstNull = ids.IndexOf("13");
        var nullsFirst = firstNull >= 0 && firstNull < 2;
        var observed = nullsFirst ? NullCollation.Low : NullCollation.High;
        var declared = context.Profile.DefaultNullCollation;

        // Ascending, LOW and FIRST both mean "NULLs first"; HIGH and LAST both mean "last".
        var declaredFirst = declared is NullCollation.Low or NullCollation.First;
        return declaredFirst == nullsFirst
            ? new ConformanceFinding
            {
                Subject = "default NULL placement",
                Declared = declared.ToString(),
                Observed = nullsFirst ? "NULLs first ascending" : "NULLs last ascending",
                Outcome = ConformanceOutcome.Pass,
            }
            : new ConformanceFinding
            {
                Subject = "default NULL placement",
                Declared = declared.ToString(),
                Observed = nullsFirst ? "NULLs first ascending" : "NULLs last ascending",
                Outcome = ConformanceOutcome.Fail,
                Advice = $"Declare NullCollation.{observed}. Chalk pushes a sort only when the "
                    + "source will put NULLs where the plan asks (D89), and it reads this field to "
                    + "decide.",
            };
    }

    /// <summary><c>BETWEEN</c> is inclusive at both ends in standard SQL. Some engines disagree.</summary>
    private static async Task<ConformanceFinding> BetweenAsync(
        ConformanceContext context, CancellationToken ct)
    {
        var number = context.Column("int_value");
        var sql = context.SelectIdsWhere($"{number} BETWEEN 1 AND 7");
        var (rows, failure) = await TryAsync(context, sql, ct).ConfigureAwait(false);
        if (failure is not null)
        {
            return Refused("BETWEEN inclusivity", sql, failure);
        }

        // What Chalk's own >= and <= would keep, computed from the rows the scan gave — including
        // the two endpoints, which is the whole question.
        var column = context.ColumnIndex("int_value");
        var expected = context.Rows.Count(r => r[column] is long v && v is >= 1 and <= 7);
        return rows.Count == expected
            ? new ConformanceFinding
            {
                Subject = "BETWEEN inclusivity",
                Declared = "inclusive at both ends, as SQL says",
                Observed = $"{rows.Count} rows for BETWEEN 1 AND 7, both ends included",
                Outcome = ConformanceOutcome.Pass,
            }
            : new ConformanceFinding
            {
                Subject = "BETWEEN inclusivity",
                Declared = $"inclusive at both ends, which keeps {expected} rows",
                Observed = $"{rows.Count} rows for BETWEEN 1 AND 7",
                Outcome = ConformanceOutcome.Fail,
                Advice = "Remove PredicateShape.Range: a BETWEEN Chalk expands to >= and <= would "
                    + "return different rows here.",
            };
    }

    /// <summary>Oracle treats the empty string as NULL. Nothing else does, and Chalk does not.</summary>
    private static async Task<ConformanceFinding> EmptyStringAsync(
        ConformanceContext context, CancellationToken ct)
    {
        var text = context.Column("text_value");
        var sql = context.SelectIdsWhere($"{text} = ''");
        var (rows, failure) = await TryAsync(context, sql, ct).ConfigureAwait(false);
        if (failure is not null)
        {
            return Refused("empty string versus NULL", sql, failure);
        }

        var distinct = rows.Count == 1;
        return distinct
            ? new ConformanceFinding
            {
                Subject = "empty string versus NULL",
                Declared = "distinct, as SQL says",
                Observed = "'' matched the one empty-string row and neither NULL",
                Outcome = ConformanceOutcome.Pass,
            }
            : new ConformanceFinding
            {
                Subject = "empty string versus NULL",
                Declared = "distinct",
                Observed = $"'' matched {rows.Count} rows",
                Outcome = ConformanceOutcome.Fail,
                Advice = "This source treats the empty string as NULL. Do not declare string "
                    + "predicates pushable: Chalk keeps them distinct and would disagree on every "
                    + "row that holds either.",
            };
    }

    /// <summary>
    /// Integer division of a negative: an integer at all, and truncating towards zero or flooring?
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type is half the question and this probe used to cast it away (F27, ADR 0027). It asked
    /// for <c>CAST(int_value / 3 AS DOUBLE PRECISION)</c> and passed any text beginning
    /// <c>-2</c>, so DuckDB's <c>-2.3333333333333335</c> — real division, which is neither
    /// truncation nor flooring — read as "truncates towards zero". It now asks for the expression
    /// uncast against a column declared the integer type Chalk gives an integer division, and the
    /// source's own scan contract is what names the type the provider produced.
    /// </para>
    /// <para>
    /// The operator is the one the planner would <em>generate</em> for this dialect rather than
    /// always <c>/</c>: DuckDB's <c>/</c> is real division whatever its operands are, so Chalk's
    /// DuckDB dialect writes <c>//</c>, and probing <c>/</c> there would measure a spelling Chalk
    /// never sends. The two must agree — this is the same fact as
    /// <c>SourceDialects.integerDivision</c> in the planner — and the pushdown corpus's golden SQL
    /// is what pins the planner's half of it.
    /// </para>
    /// </remarks>
    private static async Task<ConformanceFinding> IntegerDivisionAsync(
        ConformanceContext context, CancellationToken ct)
    {
        const string subject = "integer division of a negative";
        var number = context.Column("int_value");
        var divide = IntegerDivision(context.Profile.Dialect);
        var declared =
            $"truncates towards zero (-7 {divide} 3 = -2) in an integer type, as Chalk does";
        var sql = $"SELECT {number} {divide} 3 AS {context.Column("q")} "
            + $"FROM {context.TableName()} WHERE {number} = -7";

        // An integer column, because that is what Chalk declares for a division of two integers.
        // A source whose operator answers in a real cannot fill it, and says so.
        IReadOnlyList<ColumnDescriptor> integer =
            [new() { Name = "q", Type = ChalkType.Int64(nullable: true) }];
        var (rows, failure) = await TryAsync(context, sql, integer, ct).ConfigureAwait(false);
        if (failure is null && rows.Count > 0)
        {
            var observed = ConformanceValues.Text(rows[0][0]);
            return observed switch
            {
                "-2" => new ConformanceFinding
                {
                    Subject = subject,
                    Declared = declared,
                    Observed = $"-7 {divide} 3 = -2, in the integer type the column declares",
                    Outcome = ConformanceOutcome.Pass,
                },
                "-3" => new ConformanceFinding
                {
                    Subject = subject,
                    Declared = declared,
                    Observed = $"-7 {divide} 3 = -3, which is flooring",
                    Outcome = ConformanceOutcome.Fail,
                    Advice = "Remove FunctionId.Divide from PushableFunctions: this source floors "
                        + "where Chalk truncates, so a pushed division would give a different "
                        + "number.",
                },
                _ => new ConformanceFinding
                {
                    Subject = subject,
                    Declared = declared,
                    Observed = $"-7 {divide} 3 = {observed}, which is neither -2 nor -3",
                    Outcome = ConformanceOutcome.Fail,
                    Advice = "Remove FunctionId.Divide from PushableFunctions: a pushed division "
                        + "would give a different number.",
                },
            };
        }

        // It did not come back as an integer. Ask again as a real, so the finding can quote the
        // value as well as the type the provider reported for it.
        IReadOnlyList<ColumnDescriptor> real =
            [new() { Name = "q", Type = ChalkType.Float64(nullable: true) }];
        var (realRows, realFailure) = await TryAsync(context, sql, real, ct).ConfigureAwait(false);
        if (failure is SourceContractException mismatch && realFailure is null && realRows.Count > 0)
        {
            return new ConformanceFinding
            {
                Subject = subject,
                Declared = declared,
                Observed = $"-7 {divide} 3 = {ConformanceValues.Text(realRows[0][0])}, and not in "
                    + $"an integer type — {mismatch.Message}",
                Outcome = ConformanceOutcome.Fail,
                Advice = "This source's division is real division, so it neither truncates nor "
                    + "floors. Remove FunctionId.Divide from PushableFunctions, or give the "
                    + "planner's dialect an integer-division operator to write instead, the way "
                    + "Chalk's DuckDB dialect writes `//` (ADR 0027). A pushed division reaches "
                    + "the client as the wrong type, and inside a pushed predicate it matches the "
                    + "wrong rows.",
            };
        }

        return new ConformanceFinding
        {
            Subject = subject,
            Declared = declared,
            Observed = failure is null ? "no row came back" : failure.Message,
            Outcome = ConformanceOutcome.Skipped,
            Advice = $"The query was: {sql}",
        };
    }

    /// <summary>
    /// How the planner spells an integer division for this dialect (ADR 0027). The one fact the kit
    /// and <c>chalk.planner.plan.SourceDialects.integerDivision</c> hold in common; a dialect
    /// profile has no field for it, so they name the same dialect rather than reading one value.
    /// </summary>
    private static string IntegerDivision(string dialect) =>
        dialect.Equals("duckdb", StringComparison.OrdinalIgnoreCase) ? "//" : "/";

    /// <summary>The sign of a modulus of a negative follows the dividend in SQL, the divisor in some engines.</summary>
    private static async Task<ConformanceFinding> ModulusAsync(
        ConformanceContext context, CancellationToken ct)
    {
        var number = context.Column("int_value");
        // Cast for the same reason as the division above: SQLite's mod() is one of its floating
        // point math functions and DuckDB's MOD returns an integer. The sign is the question.
        var sql = $"SELECT CAST(MOD({number}, 3) AS DOUBLE PRECISION) AS {context.Column("m")} "
            + $"FROM {context.TableName()} WHERE {number} = -7";
        IReadOnlyList<ColumnDescriptor> columns = [new() { Name = "m", Type = ChalkType.Float64(true) }];
        var (rows, failure) = await TryAsync(context, sql, columns, ct).ConfigureAwait(false);
        if (failure is not null || rows.Count == 0)
        {
            return new ConformanceFinding
            {
                Subject = "modulus of a negative",
                Declared = "the sign of the dividend (-7 mod 3 = -1), as Chalk does",
                Observed = failure is null ? "no row came back" : failure.Message,
                Outcome = ConformanceOutcome.Skipped,
                Advice = $"The query was: {sql}",
            };
        }

        var observed = ConformanceValues.Text(rows[0][0]);
        return observed is "-1" or "-1.0" or "-1.00000"
            ? new ConformanceFinding
            {
                Subject = "modulus of a negative",
                Declared = "the sign of the dividend, as Chalk does",
                Observed = "-7 mod 3 = -1",
                Outcome = ConformanceOutcome.Pass,
            }
            : new ConformanceFinding
            {
                Subject = "modulus of a negative",
                Declared = "the sign of the dividend (-7 mod 3 = -1)",
                Observed = $"-7 mod 3 = {observed}",
                Outcome = ConformanceOutcome.Fail,
                Advice = "Do not declare MOD pushable: this source's sign convention is not SQL's.",
            };
    }

    /// <summary>Whether a wide decimal survives the round trip, or is silently a double.</summary>
    private static async Task<ConformanceFinding> DecimalPrecisionAsync(
        ConformanceContext context, CancellationToken ct)
    {
        var value = context.Column("decimal_value");
        var sql = $"SELECT {value} AS {context.Column("v")} FROM {context.TableName()} "
            + $"WHERE {context.Column("id")} = 4";
        IReadOnlyList<ColumnDescriptor> columns =
            [new() { Name = "v", Type = ChalkType.Decimal(18, 6, nullable: true) }];
        var (rows, failure) = await TryAsync(context, sql, columns, ct).ConfigureAwait(false);
        if (failure is not null || rows.Count == 0)
        {
            return Refused("decimal precision", sql, failure ?? new InvalidOperationException("no row"));
        }

        var observed = ConformanceValues.Text(rows[0][0]);
        var exact = observed == "1234567890.123456";
        var declared = context.Profile.MaxNumericPrecision;

        return exact
            ? new ConformanceFinding
            {
                Subject = "decimal precision",
                Declared = declared == 0 ? "as Chalk's" : declared.ToString(CultureInfo.InvariantCulture),
                Observed = "1234567890.123456 survived the round trip exactly",
                Outcome = ConformanceOutcome.Pass,
            }
            : new ConformanceFinding
            {
                Subject = "decimal precision",
                Declared = declared == 0 ? "as Chalk's (38 digits)" : declared.ToString(CultureInfo.InvariantCulture),
                Observed = $"1234567890.123456 came back as {observed}",
                Outcome = declared is > 0 and < 16 ? ConformanceOutcome.Pass : ConformanceOutcome.Fail,
                Advice = declared is > 0 and < 16
                    ? "The profile already says this source's decimals are approximate, and the "
                        + "planner refuses to push a comparison it would truncate."
                    : "Lower DialectProfile.MaxNumericPrecision to what this source really keeps; "
                        + "Chalk then stops pushing comparisons that would truncate (D89).",
            };
    }

    /// <summary>Whether sub-millisecond timestamps survive, or the source stores milliseconds.</summary>
    private static async Task<ConformanceFinding> TimestampPrecisionAsync(
        ConformanceContext context, CancellationToken ct)
    {
        var value = context.Column("ts_value");
        var sql = $"SELECT {value} AS {context.Column("v")} FROM {context.TableName()} "
            + $"WHERE {context.Column("id")} = 3";
        IReadOnlyList<ColumnDescriptor> columns =
            [new() { Name = "v", Type = ChalkType.Timestamp(9, nullable: true) }];
        var (rows, failure) = await TryAsync(context, sql, columns, ct).ConfigureAwait(false);
        if (failure is not null || rows.Count == 0)
        {
            return Refused("timestamp precision", sql, failure ?? new InvalidOperationException("no row"));
        }

        // Row 3's timestamp is one tick — 100 ns — past midnight. A source that stores milliseconds,
        // microseconds or seconds truncates it to midnight.
        var observed = ConformanceValues.Text(rows[0][0]);
        var kept = observed.Contains("00:00:00.0000001", StringComparison.Ordinal);
        var declared = context.Profile.MaxTimestampPrecision;
        var claimsNanoseconds = declared is 0 or >= 7;

        return kept || !claimsNanoseconds
            ? new ConformanceFinding
            {
                Subject = "timestamp precision",
                Declared = declared == 0 ? "as Chalk's (nanoseconds)" : declared.ToString(CultureInfo.InvariantCulture),
                Observed = kept ? "100 ns survived the round trip" : $"came back as {observed}",
                Outcome = ConformanceOutcome.Pass,
            }
            : new ConformanceFinding
            {
                Subject = "timestamp precision",
                Declared = declared == 0 ? "as Chalk's (nanoseconds)" : declared.ToString(CultureInfo.InvariantCulture),
                Observed = $"100 ns past midnight came back as {observed}",
                Outcome = ConformanceOutcome.Fail,
                Advice = "Lower DialectProfile.MaxTimestampPrecision to what this source really "
                    + "keeps; Chalk then stops pushing a comparison it would truncate (D89).",
            };
    }

    /// <summary>Whether <c>ESCAPE</c> in a <c>LIKE</c> is honoured, so a literal <c>%</c> can be matched.</summary>
    private static async Task<ConformanceFinding> LikeEscapeAsync(
        ConformanceContext context, CancellationToken ct)
    {
        var declaresLike = context.Capabilities.PushablePredicates.Contains(PredicateShape.Like);
        var text = context.Column("text_value");
        var sql = context.SelectIdsWhere($"{text} LIKE '100!%' ESCAPE '!'");
        var (rows, failure) = await TryAsync(context, sql, ct).ConfigureAwait(false);
        if (failure is not null)
        {
            return new ConformanceFinding
            {
                Subject = "LIKE escape handling",
                Declared = declaresLike ? "PredicateShape.Like is pushable" : "no LIKE shape declared",
                Observed = $"the source refused ESCAPE: {failure.Message}",
                Outcome = declaresLike ? ConformanceOutcome.Fail : ConformanceOutcome.Skipped,
                Advice = $"The query was: {sql}",
            };
        }

        // Row 9's text is '100%'. Escaped, the pattern matches exactly it.
        var honoured = rows.Count == 1 && ConformanceValues.Ids(rows) == "9";
        return honoured || !declaresLike
            ? new ConformanceFinding
            {
                Subject = "LIKE escape handling",
                Declared = declaresLike ? "PredicateShape.Like is pushable" : "no LIKE shape declared",
                Observed = honoured
                    ? "'100!%' ESCAPE '!' matched only the literal '100%'"
                    : $"matched ids {ConformanceValues.Ids(rows)}",
                Outcome = ConformanceOutcome.Pass,
            }
            : new ConformanceFinding
            {
                Subject = "LIKE escape handling",
                Declared = "PredicateShape.Like is pushable",
                Observed = $"'100!%' ESCAPE '!' matched ids {ConformanceValues.Ids(rows)}",
                Outcome = ConformanceOutcome.Fail,
                Advice = "The source does not honour ESCAPE, so a pattern containing a literal "
                    + "wildcard would match different rows. Remove PredicateShape.Like.",
            };
    }

    /// <summary>
    /// Whether a cast to an unbounded string type keeps the whole value (D168). A bare
    /// <c>VARCHAR</c> in a <c>CAST</c> is thirty characters in T-SQL, so a long value comes back
    /// truncated <em>with no error at all</em> — a class of defect no other probe here would see,
    /// because every other one compares short strings. <c>DialectProfile</c> has
    /// <c>max_numeric_precision</c> and <c>max_timestamp_precision</c> and no string equivalent, so
    /// what this reports is whether one is needed.
    /// </summary>
    private static async Task<ConformanceFinding> StringCastTruncationAsync(
        ConformanceContext context, CancellationToken ct)
    {
        // Eighty characters: past T-SQL's thirty and past any other default worth knowing about,
        // and still short enough for a source with a real limit to be told apart from one with none.
        const string Value =
            "0123456789012345678901234567890123456789012345678901234567890123456789012345678901";

        var sql =
            $"SELECT CAST('{Value}' AS VARCHAR) AS {context.Column("v")} FROM {context.TableName()} "
            + $"WHERE {context.Column("id")} = 1";
        IReadOnlyList<ColumnDescriptor> columns =
            [new() { Name = "v", Type = ChalkType.String(nullable: true) }];
        var (rows, failure) = await TryAsync(context, sql, columns, ct).ConfigureAwait(false);
        if (failure is not null || rows.Count == 0)
        {
            return Refused(
                "string cast truncation", sql, failure ?? new InvalidOperationException("no row"));
        }

        var observed = ConformanceValues.Text(rows[0][0]);
        return observed == Value
            ? new ConformanceFinding
            {
                Subject = "string cast truncation",
                Declared = "an unbounded string cast keeps the value, as Chalk's does",
                Observed = $"all {Value.Length} characters survived CAST(... AS VARCHAR)",
                Outcome = ConformanceOutcome.Pass,
            }
            : new ConformanceFinding
            {
                Subject = "string cast truncation",
                Declared = "an unbounded string cast keeps the value, as Chalk's does",
                Observed =
                    $"{Value.Length} characters went in and {observed.Length} came back",
                Outcome = ConformanceOutcome.Fail,
                Advice = "This source's bare VARCHAR in a CAST has a length of its own, so a pushed "
                    + "cast can silently drop characters. Do not declare CAST pushable for it until "
                    + "the adapter spells the length out.",
            };
    }

    /// <summary>
    /// Whether a bound value comes back as itself (D168): a timestamp with fractional seconds and a
    /// decimal at full scale, each bound as a parameter and compared with the row it was read from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from the read-side precision probes above, and a different defect: a driver that
    /// binds a time through a coarser type makes <c>01:02:03.500</c> compare equal to
    /// <c>01:02:03</c> <em>and unequal to itself</em>, which no amount of reading care would catch.
    /// It bears on <c>supports_parameters</c>, so the finding names that.
    /// </para>
    /// <para>
    /// The value bound is the one the source's own scan produced, not the one the dataset put in: a
    /// source that stores milliseconds has already truncated the latter, and asking it to find a
    /// value it never held would be a probe about storage. And a mismatch the profile already
    /// declares — a source whose numeric precision says its decimals are approximate — is reported
    /// rather than failed, exactly as the decimal probe reports one.
    /// </para>
    /// </remarks>
    private static async Task<ConformanceFinding> ParameterRoundTripAsync(
        ConformanceContext context, CancellationToken ct)
    {
        if (!context.Capabilities.SupportsParameters)
        {
            return new ConformanceFinding
            {
                Subject = "parameter round trip",
                Declared = "the source takes no parameters",
                Observed = "not asked",
                Outcome = ConformanceOutcome.Skipped,
            };
        }

        var first = context.Rows.FirstOrDefault(r => Id(r) == 1);
        var fourth = context.Rows.FirstOrDefault(r => Id(r) == 4);
        if (first is null || fourth is null)
        {
            return new ConformanceFinding
            {
                Subject = "parameter round trip",
                Declared = "the source takes parameters",
                Observed = "the kit's rows 1 and 4 are not both there to bind",
                Outcome = ConformanceOutcome.Skipped,
            };
        }

        // Zone-less, because Chalk's TIMESTAMP is (02-ir.md §3) and the kit's reader hands the
        // value over as UTC. A driver that refuses to write a UTC-kinded DateTime into a
        // `timestamp without time zone` is right to, and the probe is about precision rather than
        // about which mistake it is making.
        var timestamp = first[5] is DateTime moment
            ? DateTime.SpecifyKind(moment, DateTimeKind.Unspecified)
            : first[5];
        var (timestampMatched, timestampFailure) = await BoundMatchesAsync(
            context, "ts_value", timestamp, ChalkType.Timestamp(9, nullable: true), 1, ct)
            .ConfigureAwait(false);
        var (decimalMatched, decimalFailure) = await BoundMatchesAsync(
            context, "decimal_value", fourth[4], ChalkType.Decimal(18, 6, nullable: true), 4, ct)
            .ConfigureAwait(false);

        if (timestampFailure is not null || decimalFailure is not null)
        {
            return Refused(
                "parameter round trip",
                "SELECT id FROM <table> WHERE <column> = ?",
                (timestampFailure ?? decimalFailure)!);
        }

        // A precision the profile already declares as coarse explains a mismatch; a profile that
        // claims Chalk's own precision does not.
        var timestampDeclared = context.Profile.MaxTimestampPrecision;
        var decimalDeclared = context.Profile.MaxNumericPrecision;
        var timestampExplained = timestampDeclared is > 0 and < 7;
        var decimalExplained = decimalDeclared is > 0 and < 16;


        var problems = new List<string>();
        if (!timestampMatched)
        {
            problems.Add(
                $"a timestamp with fractional seconds did not match the row it came from (declared "
                + $"precision {Declared(timestampDeclared, "nanoseconds")})");
        }

        if (!decimalMatched)
        {
            problems.Add(
                $"a decimal at scale 6 did not match the row it came from (declared precision "
                + $"{Declared(decimalDeclared, "38 digits")})");
        }

        if (problems.Count == 0)
        {
            return new ConformanceFinding
            {
                Subject = "parameter round trip",
                Declared = "the source takes parameters",
                Observed = "a timestamp with fractional seconds and a decimal at scale 6 each "
                    + "matched the row they came from",
                Outcome = ConformanceOutcome.Pass,
            };
        }

        var explained = (timestampMatched || timestampExplained)
            && (decimalMatched || decimalExplained);
        return new ConformanceFinding
        {
            Subject = "parameter round trip",
            Declared = "the source takes parameters",
            Observed = string.Join("; ", problems),
            Outcome = explained ? ConformanceOutcome.Pass : ConformanceOutcome.Fail,
            Advice = explained
                ? "The profile already says this source's values are coarser than Chalk's, and the "
                    + "planner refuses to push a comparison it would truncate."
                : "This source's driver binds one of these through a coarser type, so a value can "
                    + "compare unequal to itself. Clear supports_parameters, or lower "
                    + "max_timestamp_precision / max_numeric_precision to what binding really keeps.",
        };
    }

    private static string Declared(uint precision, string chalks) =>
        precision == 0 ? "as Chalk's (" + chalks + ")" : precision.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Binds one value against the column it came from and says whether the row came back. One
    /// query per value: a source that infers a parameter's type from its context has an easier
    /// time with one than with two of different types in one predicate.
    /// </summary>
    private static async Task<(bool Matched, Exception? Failure)> BoundMatchesAsync(
        ConformanceContext context,
        string column,
        object? value,
        ChalkType type,
        long expectedId,
        CancellationToken ct)
    {
        var sql =
            $"SELECT {context.Column("id")} FROM {context.TableName()} "
            + $"WHERE {context.Column(column)} = ?";
        var (rows, failure) = await TryAsync(
            context, sql, context.IdOnly, [value], [type], ct).ConfigureAwait(false);
        return failure is not null ? (false, failure) : (rows.Any(r => Id(r) == expectedId), null);
    }

    /// <summary>The <c>id</c> of a scanned row, whatever integral shape the source produced it in.</summary>
    private static long Id(object?[] row) =>
        Convert.ToInt64(row[0], CultureInfo.InvariantCulture);

    private static Task<(IReadOnlyList<object?[]> Rows, Exception? Failure)> TryAsync(
        ConformanceContext context, string sql, CancellationToken ct) =>
        TryAsync(context, sql, context.IdOnly, [], [], ct);

    private static Task<(IReadOnlyList<object?[]> Rows, Exception? Failure)> TryAsync(
        ConformanceContext context,
        string sql,
        IReadOnlyList<ColumnDescriptor> columns,
        CancellationToken ct) =>
        TryAsync(context, sql, columns, [], [], ct);

    private static async Task<(IReadOnlyList<object?[]> Rows, Exception? Failure)> TryAsync(
        ConformanceContext context,
        string sql,
        IReadOnlyList<ColumnDescriptor> columns,
        IReadOnlyList<object?> parameters,
        IReadOnlyList<ChalkType> parameterTypes,
        CancellationToken ct)
    {
        try
        {
            return (
                await context.QueryAsync(sql, columns, parameters, parameterTypes, ct)
                    .ConfigureAwait(false),
                null);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return ([], failure);
        }
    }

    private static ConformanceFinding Refused(string subject, string sql, Exception failure) => new()
    {
        Subject = subject,
        Declared = "the profile makes a claim about this",
        Observed = $"the source refused the probe: {failure.Message}",
        Outcome = ConformanceOutcome.Skipped,
        Advice = $"The query was: {sql}",
    };
}
