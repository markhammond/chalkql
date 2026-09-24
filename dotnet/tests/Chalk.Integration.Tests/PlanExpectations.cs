using System.Globalization;
using Chalk.Ir;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// Parses and checks the <c>-- expect:</c> block at the head of each corpus query
/// (docs/design/05-testing.md §3). A diff on a plan digest tells you *that* a plan changed; these
/// assertions say what about it was supposed to be true in the first place.
/// </summary>
/// <remarks>
/// Every relation these lines are about is one <b>this client</b> executes, so they read the
/// executed walk (F92): <c>not(Filter)</c> over a pushdown query says the filter reached the source,
/// and the pushed plan is where it went — counting it here would make the expectation unsatisfiable
/// by the very plan it describes.
/// </remarks>
public static class PlanExpectations
{
    /// <summary>
    /// Applies every expectation that describes the plan. Lines that describe client-side behaviour
    /// — parameter style, list parameters — are checked by <c>ParameterTests</c> instead and are
    /// listed here so an unknown line still fails loudly.
    /// </summary>
    /// <param name="planText">
    /// The planner's own plan text, when the caller asked for it. Only the <c>plan_text</c>
    /// expectation reads it, and a query that writes one without a caller that supplies it fails
    /// saying so rather than passing vacuously.
    /// </param>
    public static void Check(CorpusQuery query, Plan plan, string? planText = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(plan);

        foreach (var expectation in query.Expectations)
        {
            Apply(query, plan, planText, expectation);
        }
    }

    private static void Apply(CorpusQuery query, Plan plan, string? planText, string expectation)
    {
        var (verb, argument) = Split(expectation);
        switch (verb)
        {
            case "has":
                Assert.True(
                    PlanWalker.Has(plan, Kind(argument)),
                    Because(query, plan, $"expected a {argument} node"));
                break;

            case "not":
                Assert.False(
                    PlanWalker.Has(plan, Kind(argument)),
                    Because(query, plan, $"expected no {argument} node"));
                break;

            case "count":
            {
                var (kind, expected) = Equation(argument);
                if (string.Equals(kind, "DynamicParam", StringComparison.Ordinal))
                {
                    // An expression, not a node: count occurrences of `?` in the plan.
                    Assert.Equal(
                        expected,
                        PlanWalker.AllExprs(plan).Count(e => e.KindCase == Expr.KindOneofCase.Param));
                    break;
                }

                if (string.Equals(kind, "Cast", StringComparison.Ordinal))
                {
                    Assert.Equal(
                        expected,
                        PlanWalker.AllExprs(plan).Count(e => e.KindCase == Expr.KindOneofCase.Cast));
                    break;
                }

                // D291: a field of a struct is an expression too, and how many of them a plan has
                // is what says the struct was taken apart rather than flattened into its fields.
                if (string.Equals(kind, "FieldAccess", StringComparison.Ordinal))
                {
                    Assert.Equal(
                        expected,
                        PlanWalker.AllExprs(plan).Count(e => e.KindCase == Expr.KindOneofCase.FieldAccess));
                    break;
                }

                Assert.Equal(expected, PlanWalker.Count(plan, Kind(kind)));
                break;
            }

            case "index":
            {
                var lookup = PlanWalker.ExecutedRels(plan)
                    .FirstOrDefault(r => r.KindCase == Rel.KindOneofCase.IndexLookup);
                Assert.NotNull(lookup);
                Assert.Equal(argument, lookup.IndexLookup.Index);
                break;
            }

            // D257: what the planner's own plan text says. The IR carries an index by name, so the
            // kind a lookup was planned over — and, for a clustered one, whether its projection was
            // covered — is visible in the plan text and nowhere else.
            case "plan_text":
            {
                Assert.True(
                    planText is not null,
                    $"{query.Name}: '{expectation}' needs the planner's plan text; plan this query "
                    + "with IncludePlanText.");
                Assert.Contains(argument, planText, StringComparison.Ordinal);
                break;
            }

            // The other half of the same claim: what the plan text must *not* say. One pair of
            // corpus queries is the same statement planned with and without parameter value hints,
            // and what separates them is a row goal one of them has and the other does not.
            case "not_plan_text":
            {
                Assert.True(
                    planText is not null,
                    $"{query.Name}: '{expectation}' needs the planner's plan text; plan this query "
                    + "with IncludePlanText.");
                Assert.DoesNotContain(argument, planText, StringComparison.Ordinal);
                break;
            }

            case "ranges":
            {
                var lookup = PlanWalker.ExecutedRels(plan)
                    .FirstOrDefault(r => r.KindCase == Rel.KindOneofCase.IndexLookup);
                Assert.NotNull(lookup);
                Assert.Equal(
                    int.Parse(argument, CultureInfo.InvariantCulture),
                    lookup.IndexLookup.Ranges.Count);
                break;
            }

            case "read_projection":
            {
                // The leaf's projection, whether the leaf is a scan or an index lookup: pruning is
                // what the expectation is about, and M2 gave the same query a different leaf.
                var leaf = PlanWalker.ExecutedRels(plan).FirstOrDefault(
                    r => r.KindCase is Rel.KindOneofCase.Read or Rel.KindOneofCase.IndexLookup);
                Assert.NotNull(leaf);
                Assert.Equal(
                    int.Parse(argument, CultureInfo.InvariantCulture),
                    leaf.KindCase == Rel.KindOneofCase.Read
                        ? leaf.Read.Projection.Count
                        : leaf.IndexLookup.Projection.Count);
                break;
            }

            case "join_type":
            {
                var parts = argument.Split(':', 2);
                var (kind, wanted) = (parts[0], parts[1]);
                var join = PlanWalker.ExecutedRels(plan).FirstOrDefault(r => r.KindCase == Kind(kind));
                Assert.NotNull(join);
                Assert.Equal(wanted, JoinTypeOf(join).ToString().ToUpperInvariant());
                break;
            }

            case "asof_match":
            {
                var join = PlanWalker.ExecutedRels(plan)
                    .FirstOrDefault(r => r.KindCase == Rel.KindOneofCase.AsOfJoin);
                Assert.NotNull(join);
                Assert.Equal(argument, join.AsOfJoin.Match.ToString().ToUpperInvariant());
                break;
            }

            case "root_collation":
                CheckRootCollation(query, plan, argument);
                break;

            case "param_types":
                Assert.Equal(
                    argument.Trim('[', ']')
                        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                    plan.ParameterTypes.Select(IrTypes.Describe).ToArray());
                break;

            case "group_keys":
            {
                var aggregate = PlanWalker.ExecutedRels(plan)
                    .FirstOrDefault(r => r.KindCase == Rel.KindOneofCase.HashAggregate);
                Assert.NotNull(aggregate);
                Assert.Equal(
                    int.Parse(argument, CultureInfo.InvariantCulture),
                    aggregate.HashAggregate.Aggregate.Groupings[0].Keys.Count);
                break;
            }

            case "measures":
                CheckMeasures(query, plan, argument);
                break;

            case "has_function":
                Assert.True(
                    Functions(plan).Contains(Function(argument)),
                    Because(query, plan, $"expected a call to {argument}"));
                break;

            case "not_function":
                Assert.DoesNotContain(Function(argument), Functions(plan));
                break;

            case "has_if_then":
                Assert.Contains(
                    PlanWalker.AllExprs(plan),
                    e => e.KindCase == Expr.KindOneofCase.IfThen);
                break;

            // Step 22. A user function is named rather than numbered, so `has_function` cannot say
            // anything about one; `not_if_then` is what says a STRICT body's NULL guard folded away.
            case "not_if_then":
                Assert.DoesNotContain(
                    PlanWalker.AllExprs(plan),
                    e => e.KindCase == Expr.KindOneofCase.IfThen);
                break;

            case "has_user_function":
                Assert.Contains(argument, UserFunctions(plan));
                break;

            case "not_user_function":
                Assert.Empty(UserFunctions(plan));
                break;

            // D291: the plan takes a struct apart somewhere, whatever the count.
            case "has_field_access":
                Assert.Contains(
                    PlanWalker.AllExprs(plan),
                    e => e.KindCase == Expr.KindOneofCase.FieldAccess);
                break;

            case "has_in_list":
                Assert.Contains(
                    PlanWalker.AllExprs(plan),
                    e => e.KindCase == Expr.KindOneofCase.InList);
                break;

            case "in_list_options":
                CheckInListOptions(query, plan, argument);
                break;

            // Client-side claims: ParameterTests owns these, and CorpusDifferentialTests owns
            // rows_scanned, which is a fact about an execution rather than about a plan. Named here
            // so an unknown verb still fails.
            case "param_style":
            case "parameters":
            case "accepts_list":
            case "rows_scanned":

            // D276: what a row goal buys, stated as a bound on the rows the source read. Like
            // `rows_scanned`, it is a fact about an execution, so CorpusDifferentialTests owns it.
            case "rows_scanned_at_most":
                break;

            default:
                Assert.Fail($"{query.Name}: unknown expectation '{expectation}'");
                break;
        }
    }

    /// <summary>Every user function a plan names: in a call, in a measure, over a window, as a leaf.</summary>
    private static IReadOnlyCollection<string> UserFunctions(Plan plan)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var expr in PlanWalker.AllExprs(plan))
        {
            if (expr.KindCase == Expr.KindOneofCase.Call && expr.Call.UserFunction.Length > 0)
            {
                names.Add(expr.Call.UserFunction);
            }
        }

        foreach (var rel in PlanWalker.ExecutedRels(plan))
        {
            if (rel.KindCase == Rel.KindOneofCase.TableFunctionScan)
            {
                names.Add(rel.TableFunctionScan.Function);
            }

            var aggregate = rel.KindCase switch
            {
                Rel.KindOneofCase.Aggregate => rel.Aggregate,
                Rel.KindOneofCase.HashAggregate => rel.HashAggregate.Aggregate,
                Rel.KindOneofCase.StreamAggregate => rel.StreamAggregate.Aggregate,
                _ => null,
            };
            foreach (var measure in aggregate?.Measures ?? [])
            {
                if (measure.UserFunction.Length > 0)
                {
                    names.Add(measure.UserFunction);
                }
            }

            if (rel.KindCase == Rel.KindOneofCase.Window)
            {
                foreach (var call in rel.Window.Calls)
                {
                    if (call.UserFunction.Length > 0)
                    {
                        names.Add(call.UserFunction);
                    }
                }
            }
        }

        return names;
    }

    private static void CheckRootCollation(CorpusQuery query, Plan plan, string argument)
    {
        var expected = argument.Trim('[', ']')
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        Assert.NotEmpty(plan.Root.Collations);
        var actual = plan.Root.Collations[0].Fields
            .Select(f => $"{plan.Root.RowType.Fields[(int)f.Expr.FieldRef.Index].Name} {Direction(f.Direction)}")
            .ToArray();

        Assert.Equal(expected, actual);
    }

    private static void CheckMeasures(CorpusQuery query, Plan plan, string argument)
    {
        var expected = argument.Trim('[', ']')
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        var aggregate = PlanWalker.ExecutedRels(plan)
            .FirstOrDefault(r => r.KindCase == Rel.KindOneofCase.HashAggregate);
        Assert.NotNull(aggregate);

        var actual = aggregate.HashAggregate.Aggregate.Measures
            .Select(m => m.Function.ToString().ToUpperInvariant())
            .ToArray();
        Assert.Equal(expected, actual);
    }

    private static void CheckInListOptions(CorpusQuery query, Plan plan, string argument)
    {
        var expected = argument.Trim('[', ']')
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim('\''))
            .ToArray();

        var inList = PlanWalker.AllExprs(plan).FirstOrDefault(e => e.KindCase == Expr.KindOneofCase.InList);
        Assert.NotNull(inList);

        var actual = inList.InList.Options.Select(o => o.Literal.StringValue).ToArray();
        Assert.Equal(expected, actual);

        // V6: without shouldConvertRaggedUnionTypesToVarying the shorter literal would arrive
        // space-padded to the longer one's CHAR width and stop matching.
        Assert.All(actual, s => Assert.Equal(s.TrimEnd(), s));
    }

    /// <summary>
    /// Splits <c>verb(argument)</c>, <c>verb=argument</c> and <c>verb(argument)=n</c>. The last form
    /// is why this is not a one-liner: <c>count(Read)=1</c> has both a parenthesis and an equals, and
    /// <c>param_types=[TIMESTAMP(9)?]</c> has them the other way round.
    /// </summary>
    private static (string Verb, string Argument) Split(string expectation)
    {
        var open = expectation.IndexOf('(', StringComparison.Ordinal);
        var equals = expectation.IndexOf('=', StringComparison.Ordinal);

        if (open > 0 && (equals < 0 || open < equals))
        {
            var close = expectation.IndexOf(')', open);
            var verb = expectation[..open];
            var inner = close > open ? expectation[(open + 1)..close] : expectation[(open + 1)..];
            var tail = close >= 0 && close + 1 < expectation.Length
                ? expectation[(close + 1)..].Trim()
                : string.Empty;
            return tail.StartsWith('=')
                ? (verb, inner + "=" + tail[1..].Trim())
                : (verb, inner);
        }

        return equals > 0
            ? (expectation[..equals].Trim(), expectation[(equals + 1)..].Trim())
            : (expectation, string.Empty);
    }

    private static (string Kind, int Expected) Equation(string argument)
    {
        var parts = argument.Split('=', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2
            ? (parts[0], int.Parse(parts[1], CultureInfo.InvariantCulture))
            : throw new FormatException($"expected 'Kind)=n', got '{argument}'");
    }

    private static JoinType JoinTypeOf(Rel rel) => rel.KindCase switch
    {
        Rel.KindOneofCase.Join => rel.Join.Type,
        Rel.KindOneofCase.HashJoin => rel.HashJoin.Type,
        Rel.KindOneofCase.MergeJoin => rel.MergeJoin.Type,
        Rel.KindOneofCase.NestedLoopJoin => rel.NestedLoopJoin.Type,
        Rel.KindOneofCase.AsOfJoin => rel.AsOfJoin.Type,
        _ => JoinType.Unspecified,
    };

    private static Rel.KindOneofCase Kind(string name) => name switch
    {
        "Read" => Rel.KindOneofCase.Read,
        "Filter" => Rel.KindOneofCase.Filter,
        "Project" => Rel.KindOneofCase.Project,
        "Aggregate" => Rel.KindOneofCase.Aggregate,
        "HashAggregate" => Rel.KindOneofCase.HashAggregate,
        "Sort" => Rel.KindOneofCase.Sort,
        "Fetch" => Rel.KindOneofCase.Fetch,
        "TopN" => Rel.KindOneofCase.TopN,
        "VirtualTable" => Rel.KindOneofCase.VirtualTable,
        "IndexLookup" => Rel.KindOneofCase.IndexLookup,

        // M4 (D83). PlanWalker treats a RemoteQuery as a leaf, so `not(Filter)` over a plan whose
        // filter was pushed is true — which is the claim those corpus queries are making.
        "RemoteQuery" => Rel.KindOneofCase.RemoteQuery,
        "Join" => Rel.KindOneofCase.Join,
        "HashJoin" => Rel.KindOneofCase.HashJoin,
        "MergeJoin" => Rel.KindOneofCase.MergeJoin,
        "NestedLoopJoin" => Rel.KindOneofCase.NestedLoopJoin,
        "AsOfJoin" => Rel.KindOneofCase.AsOfJoin,
        "Window" => Rel.KindOneofCase.Window,
        "Hop" => Rel.KindOneofCase.Hop,
        "Session" => Rel.KindOneofCase.Session,
        "Unnest" => Rel.KindOneofCase.Unnest,
        "SetOp" => Rel.KindOneofCase.SetOp,

        // The IR has no Correlate node at all (D67): `not(Correlate)` is therefore always true, and
        // the corpus writes it to say that this is the claim being made rather than an accident.
        "TableFunctionScan" => Rel.KindOneofCase.TableFunctionScan,

        // M5, the federation nodes (D105).
        "LookupJoin" => Rel.KindOneofCase.LookupJoin,
        "AdaptiveJoin" => Rel.KindOneofCase.AdaptiveJoin,
        "MaterialisedInput" => Rel.KindOneofCase.MaterialisedInput,
        "PartitionedScan" => Rel.KindOneofCase.PartitionedScan,
        "Correlate" => Rel.KindOneofCase.None,
        _ => throw new FormatException($"unknown node kind '{name}'"),
    };

    /// <summary>
    /// The corpus names functions as the proto does (<c>FLOOR_TEMPORAL</c>); the generated C# enum
    /// drops the underscores (<c>FloorTemporal</c>), so compare with them removed.
    /// </summary>
    private static FunctionId Function(string name)
    {
        var wanted = name.Replace("_", string.Empty, StringComparison.Ordinal);
        foreach (var id in Enum.GetValues<FunctionId>())
        {
            if (string.Equals(id.ToString(), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
        }

        throw new FormatException($"unknown function '{name}'");
    }

    private static IReadOnlySet<FunctionId> Functions(Plan plan) =>
        PlanWalker.AllExprs(plan)
            .Where(e => e.KindCase == Expr.KindOneofCase.Call)
            .Select(e => e.Call.Function)
            .ToHashSet();

    private static string Direction(SortDirection direction) => direction switch
    {
        SortDirection.AscNullsFirst => "asc_nulls_first",
        SortDirection.AscNullsLast => "asc_nulls_last",
        SortDirection.DescNullsFirst => "desc_nulls_first",
        SortDirection.DescNullsLast => "desc_nulls_last",
        _ => "unspecified",
    };

    private static string Because(CorpusQuery query, Plan plan, string what) =>
        $"{query.Name}: {what}.{Environment.NewLine}{PlanExtensions.ToPlanText(plan)}";
}
