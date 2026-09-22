using Apache.Arrow;
using Chalk.Client;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;
using Array = System.Array;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Integration.Tests;

/// <summary>
/// Shared plumbing for the differential tests: bind the parameter values a corpus query needs, run
/// it, and work out what the comparison may assume about ordering and rows scanned.
/// </summary>
internal static class DifferentialRunner
{
    /// <summary>
    /// The values the parameterised corpus queries are executed with. The queries themselves carry
    /// only their SQL, so the bindings live here where both the differential and the LINQ-oracle
    /// tests can reach them.
    /// </summary>
    public static IReadOnlyDictionary<string, object?>? BindingsFor(string queryName) => queryName switch
    {
        "19_params" => new Dictionary<string, object?>
        {
            ["p0"] = Utf8String.FromAsciiString("BTCUSDT"),
            ["p1"] = new DateTime(2026, 1, 3),
        },
        "31_params_named" => new Dictionary<string, object?>
        {
            ["symbol"] = Utf8String.FromAsciiString("BTCUSDT"),
            ["from"] = new DateTime(2026, 1, 3),
            ["to"] = new DateTime(2026, 1, 4),
        },
        "32_params_ordinal_reuse" => new Dictionary<string, object?>
        {
            ["p0"] = new DateTime(2026, 1, 3),
            ["p1"] = "ETHUSDT",
            ["p2"] = new DateTime(2026, 1, 4),
        },
        "33_params_list_in" => new Dictionary<string, object?>
        {
            ["symbols"] = new[] { Utf8String.FromAsciiString("BTCUSDT"), Utf8String.FromAsciiString("ETHUSDT") },
            ["before"] = new DateTime(2026, 1, 2),
        },
        "34_params_list_empty_in" => new Dictionary<string, object?>
        {
            ["symbols"] = Array.Empty<Utf8String>(),
        },
        "35_params_list_empty_not_in" => new Dictionary<string, object?>
        {
            ["symbols"] = Array.Empty<Utf8String>(),
        },

        // The parameterised bounds (D285). The values are ordinary counts: what is being proved is
        // that a bound the planner never saw is read when the execution starts and honoured exactly,
        // and that the oracle and the engine agree about which rows that leaves.
        "47_limit_param" => new Dictionary<string, object?>
        {
            ["p0"] = 5,
        },
        "48_limit_offset_params" => new Dictionary<string, object?>
        {
            ["p0"] = 5,
            ["p1"] = 3,
        },
        "49_topn_param" => new Dictionary<string, object?>
        {
            ["p0"] = 5,
        },

        // The federated bounds (D288). A page small enough that the source truncating and the
        // engine truncating are visibly different amounts of work, and large enough that the
        // answer is more than one row.
        "17_parameterised_limit_to_a_source" => new Dictionary<string, object?>
        {
            ["p0"] = 6,
        },
        "18_parameterised_limit_over_the_branches" => new Dictionary<string, object?>
        {
            ["p0"] = 6,
        },

        // The M4 pushdown corpus (§5). A date inside the generated lineitem's range, so the query
        // returns rows at every level.
        "12_parameters" => new Dictionary<string, object?>
        {
            ["from"] = new DateTime(1995, 1, 1),
        },

        // The `edge` family (D165, step 25 §0b): a bound parameter outside Latin-1, which the
        // planner's UTF-8 character set is what makes plannable at all.
        "g_02_non_latin_1_literal_and_parameter" => new Dictionary<string, object?>
        {
            ["tag"] = Utf8String.FromString("ムーン"),
        },

        // The M2 corpus (D40). Every value is chosen so the query returns rows: the symbols exist,
        // the timestamps fall inside the 14 days the generator produces, and the order key is one
        // the generator emitted.
        "01_eq_unique_prefix" => new Dictionary<string, object?>
        {
            ["p0"] = Utf8String.FromString("BTCUSDT"),
        },
        "02_range_on_collation" or "02b_range_on_collation_ordered" => new Dictionary<string, object?>
        {
            ["p0"] = new DateTime(2026, 1, 3),
            ["p1"] = new DateTime(2026, 1, 4),
        },
        "03_eq_prefix_and_range" or "03b_eq_prefix_and_range_ordered" => new Dictionary<string, object?>
        {
            ["p0"] = Utf8String.FromString("ETHUSDT"),
            ["p1"] = new DateTime(2026, 1, 12),
        },
        "04_unindexed_column" => new Dictionary<string, object?>
        {
            ["p0"] = 1000.0,
        },
        "05_in_list_and_range" => new Dictionary<string, object?>
        {
            ["p0"] = new DateTime(2026, 1, 2),
        },
        "06_residual_filter" => new Dictionary<string, object?>
        {
            ["p0"] = Utf8String.FromString("SOLUSDT"),
            ["p1"] = 5000L,
        },
        "09_non_unique_index_over_structs" => new Dictionary<string, object?>
        {
            ["p0"] = 7L,
        },
        "11_unique_point_lookup" => new Dictionary<string, object?>
        {
            ["p0"] = Utf8String.FromString("XRPUSDT"),
            ["p1"] = new DateTime(2026, 1, 3, 12, 34, 0),
        },

        // D276. The row goal only buys anything when the first rows the leaf is read for are rows
        // the query wants, so both bindings are deliberately unselective: a bound below every
        // symbol the generator emits, and a volume below every generated one but the smallest.
        // What is asserted is how far the source read, so a binding that matched nothing would make
        // the bound hold for the wrong reason.
        "13_limit_over_ordered_lookup" => new Dictionary<string, object?>
        {
            ["p0"] = Utf8String.FromString("AAA"),
        },
        "14_limit_over_filtered_scan" => new Dictionary<string, object?>
        {
            ["p0"] = 1000L,
        },

        // The hinted pair of design 49 §6. The same values for both halves, because what is being
        // proved is that the hints chose a different plan and not a different answer.
        "15_parameterised_bound_unhinted" => new Dictionary<string, object?>
        {
            ["p0"] = Utf8String.FromString("AAA"),
            ["p1"] = 1,
        },
        "16_parameterised_bound_hinted" => new Dictionary<string, object?>
        {
            ["p0"] = Utf8String.FromString("AAA"),
            ["p1"] = 1,
        },
        _ => null,
    };

    /// <summary>
    /// Runs a prepared query with whatever bindings its corpus entry needs, optionally on an arena
    /// the caller owns (08-execution-arena.md §2.1) rather than one out of the engine's pool.
    /// </summary>
    public static async Task<(IReadOnlyList<RecordBatch> Batches, ExecutionStats Stats)> RunAsync(
        ChalkEngine engine, PreparedQuery prepared, CorpusQuery query, ExecutionArena? arena = null)
    {
        var (batches, stats, _) = await RunWithPlanAsync(engine, prepared, query, arena);
        return (batches, stats);
    }

    /// <summary>
    /// The same, plus the plan that actually ran. They differ for a statement with a list parameter,
    /// which is planned per list length (D29) — and from M2 that can mean a different node kind, not
    /// just a different number of placeholders.
    /// </summary>
    public static async Task<(IReadOnlyList<RecordBatch> Batches, ExecutionStats Stats, Plan Plan)>
        RunWithPlanAsync(
            ChalkEngine engine, PreparedQuery prepared, CorpusQuery query, ExecutionArena? arena = null)
    {
        var bindings = BindingsFor(query.Name);
        var execution = bindings is null
            ? await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>?)null, arena)
            : prepared.ParameterStyle == ParameterStyle.Named
                ? await engine.ExecuteAsync(prepared, ToNamed(prepared, bindings), arena)
                : await engine.ExecuteAsync(prepared, ToPositional(prepared, bindings), arena);

        await using (execution)
        {
            var batches = new List<RecordBatch>();
            await foreach (var batch in execution.Batches)
            {
                batches.Add(batch);
            }

            return (batches, execution.Stats, execution.Plan);
        }
    }

    /// <summary>
    /// What the two engines' results are allowed to differ by (05-testing.md §5). A plan that
    /// promises a total order compares row by row; anything else compares as a multiset, and the
    /// actual output separately has to honour every ORDER BY key the plan claims. Floating-point
    /// columns produced by an aggregation compare within a tolerance, because summation order is not
    /// part of the contract; every other column, floating point included, compares exactly.
    /// </summary>
    public static ResultComparisonOptions ComparisonFor(Plan plan, CatalogContext catalog)
    {
        var origins = PlanOrigins.Trace(plan.Root);
        var collations = plan.Root.Collations;

        // A node may claim several orderings (a one-row result satisfies every one of them). The
        // longest is the most informative to assert; totality only needs one of them to hold.
        var assertion = collations
            .OrderByDescending(c => c.Fields.Count)
            .FirstOrDefault();

        var orderKeys = assertion is null
            ? []
            : assertion.Fields.Select(f => new OrderKeyExpectation(
                (int)f.Expr.FieldRef.Index,
                f.Direction is SortDirection.DescNullsFirst or SortDirection.DescNullsLast,
                f.Direction is SortDirection.AscNullsFirst or SortDirection.DescNullsFirst)).ToArray();

        var total = collations.Any(c => IsTotalOrder(c, plan, origins, catalog));

        var tolerant = new List<int>();
        for (var i = 0; i < plan.OutputType.Fields.Count; i++)
        {
            var kind = plan.OutputType.Fields[i].Type.Kind;
            if (origins.Columns[i].Kind == ColumnOriginKind.Measure
                && kind is TypeKind.Fp32 or TypeKind.Fp64)
            {
                tolerant.Add(i);
            }
        }

        return new ResultComparisonOptions
        {
            CompareAsMultiset = !total,
            OrderKeys = orderKeys,
            AggregatedFloatColumns = tolerant,

            // A window's floating sum slides: a row that leaves the frame is subtracted rather than
            // forgotten, so the error is bounded by the number of slides since the last rebuild
            // (4 096 rows, 13-window-functions.md §4) and not by the frame's width. The reference
            // executor re-adds every frame from scratch, so the two legitimately differ by more than
            // an aggregate's four ULPs. Sixty-four is the number the DuckDB oracle already uses.
            UlpTolerance = PlanWalker.Has(plan, Rel.KindOneofCase.Window) ? 64 : 4,
        };
    }

    /// <summary>
    /// Whether <paramref name="collation"/> orders the result completely: it covers every output
    /// column, or it covers a set of columns no two rows can agree on. Answering "no" costs only a
    /// weaker comparison, so every case this cannot prove is a "no".
    /// </summary>
    private static bool IsTotalOrder(
        Collation collation, Plan plan, PlanOrigins origins, CatalogContext catalog)
    {
        var covered = collation.Fields
            .Select(f => (int)f.Expr.FieldRef.Index)
            .ToHashSet();
        if (covered.Count == 0)
        {
            return false;
        }

        if (covered.Count == plan.OutputType.Fields.Count)
        {
            return true;
        }

        var coveredOrigins = covered.Select(i => origins.Columns[i]).ToHashSet();
        if (origins.GroupKeys is { } keys)
        {
            // GROUP BY makes its keys unique; without an aggregate a base table's own key does.
            return keys.Count > 0 && keys.All(coveredOrigins.Contains);
        }

        foreach (var schema in catalog.Schemas)
        {
            foreach (var table in schema.Tables)
            {
                var name = $"{schema.Name}.{table.Name}";
                foreach (var key in table.UniqueKeys)
                {
                    if (key.Columns.Count > 0
                        && key.Columns.All(c => coveredOrigins.Contains(ColumnOrigin.Base(name, c))))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// How many rows a plan reads when every leaf is a full scan: every row of every table it
    /// touches. Exact for a plan with no <c>IndexLookup</c> and no <c>Fetch</c>; an upper bound
    /// otherwise, which is what M2 turned it into.
    /// </summary>
    public static long ExpectedRowsScanned(Plan plan, CorpusFixture fixture)
    {
        long total = 0;
        foreach (var rel in PlanWalker.Rels(plan))
        {
            var reference = rel.KindCase switch
            {
                Rel.KindOneofCase.Read => rel.Read.Table,
                Rel.KindOneofCase.IndexLookup => rel.IndexLookup.Table,
                _ => null,
            };
            if (reference is null)
            {
                continue;
            }

            var table = fixture.Catalog.FindTable(reference.Schema, reference.Table);
            total += table?.Table.RowCount ?? 0;
        }

        return total;
    }

    public static void Dispose(IEnumerable<RecordBatch> batches)
    {
        foreach (var batch in batches)
        {
            batch.Dispose();
        }
    }

    private static Dictionary<string, object?> ToNamed(
        PreparedQuery prepared, IReadOnlyDictionary<string, object?> bindings)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in prepared.Parameters)
        {
            values[parameter.Name!] = bindings[parameter.Name!];
        }

        return values;
    }

    private static object?[] ToPositional(
        PreparedQuery prepared, IReadOnlyDictionary<string, object?> bindings) =>
        [.. Enumerable.Range(0, prepared.Parameters.Count).Select(i => bindings["p" + i])];
}
