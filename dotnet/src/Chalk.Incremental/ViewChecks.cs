using CatalogContext = Chalk.Catalog.CatalogContext;
using FunctionDescriptor = Chalk.Catalog.FunctionDescriptor;
using Chalk.Client;
using Chalk.Ir;
using Chalk.Sources;
using Google.Protobuf;

namespace Chalk.Incremental;

/// <summary>
/// What a view refuses when it is made (D339, <c>docs/design/64-incremental-views.md</c> §6): the
/// statements whose rows would not be exactly what they return at each epoch, or would move without
/// an epoch. Each refusal names what it found and says what to do.
/// </summary>
internal static class ViewChecks
{
    public static void Check<TKey, TRow>(
        ChalkEngine engine, PreparedQuery prepared, int[] keyColumns, ViewOptions<TKey, TRow> options)
        where TKey : unmanaged
        where TRow : unmanaged
    {
        var plan = prepared.Plan;
        var exactness = options.Exactness;
        var requireExplicitImmutable = options.RequireExplicitImmutable;
        var databases = options.DatabaseReads;
        var reads = databases == ViewDatabaseReads.WhenRefreshed;
        Order(plan.Root, options.Key, keyColumns);

        // Without the opt-in, a database is refused by its source, whichever node reads it first.
        if (!reads)
        {
            ViewPlan.Walk(plan, message =>
            {
                switch (message)
                {
                    case TableRef table:
                        InEpochs(engine, table.SourceId, $"{table.Schema}.{table.Table}", databases);
                        break;
                    case RemoteQuery remote:
                        InEpochs(engine, remote.SourceId, null, databases);
                        break;
                }
            });
        }

        if (engine.ClockName is null && ViewPlan.ReadsCurrentTime(plan))
        {
            throw new StandingViewException(
                ChalkErrorCodes.ClockRequired,
                "The view reads the current time, and in a view that is the engine's clock, which this "
                + "engine does not have. Name one with ChalkEngineOptions.Clock, and move it with "
                + "AdvanceClock in a refresh.");
        }

        var functions = Functions(engine.Catalog);
        ViewPlan.Walk(plan, message =>
        {
            switch (message)
            {
                case Rel rel:
                    Accumulation(rel, exactness);
                    break;
                case ScalarCall { UserFunction.Length: > 0 } call:
                    Deterministic(functions, call.UserFunction, requireExplicitImmutable);
                    break;
                case Measure { UserFunction.Length: > 0 } measure:
                    Deterministic(functions, measure.UserFunction, requireExplicitImmutable);
                    break;
                case WindowCall { UserFunction.Length: > 0 } call:
                    Deterministic(functions, call.UserFunction, requireExplicitImmutable);
                    break;
                case TableFunctionScan scan:
                    Deterministic(functions, scan.Function, requireExplicitImmutable);
                    break;
                case TableRef table:
                    InEpochs(engine, table.SourceId, $"{table.Schema}.{table.Table}", databases);
                    break;
                case RemoteQuery remote:
                    InEpochs(engine, remote.SourceId, null, databases);
                    if (reads && Fed(remote))
                    {
                        throw FedRead(remote.SourceId, "a read bound with keys from the rest of the statement");
                    }

                    break;
                // A database read is kept between refreshes only when nothing else in the statement
                // binds it (D340); without the opt-in the source itself is refused, as above.
                case LookupJoin join when reads && Outside(engine, join.Lookup):
                    throw FedRead(SourceOf(join.Lookup), "a lookup join");
                case AdaptiveJoin join when reads && Outside(engine, join.Lookup?.Lookup):
                    throw FedRead(SourceOf(join.Lookup?.Lookup), "an adaptive join");
                case PartitionedScan { KeySet: not null } scan when reads && scan.Partitions.Any(p => Outside(engine, p)):
                    throw FedRead(SourceOf(scan.Partitions.First(p => Outside(engine, p))), "a scan pruned by keys at execution");
                case IndexLookup lookup when reads && engine.SourceById(lookup.Table.SourceId) is not IRefreshableSource:
                    throw FedRead(lookup.Table.SourceId, "an index lookup");
                case AsOfLookupJoin join when reads && engine.SourceById(join.Table.SourceId) is not IRefreshableSource:
                    throw FedRead(join.Table.SourceId, "an as-of lookup join");
            }
        });
    }

    /// <summary>Whether a remote read is bound with a key set: values taken from the rest of the statement.</summary>
    private static bool Fed(RemoteQuery remote)
    {
        var fed = false;
        ViewPlan.Walk(remote, message => fed |= message is KeySetParam or KeySetMatch);
        return fed;
    }

    /// <summary>Whether a part of the plan reads a source outside epochs.</summary>
    private static bool Outside(ChalkEngine engine, Rel? rel)
    {
        var outside = false;
        if (rel is not null)
        {
            ViewPlan.Walk(rel, message => outside |= message switch
            {
                RemoteQuery remote => engine.SourceById(remote.SourceId) is not IRefreshableSource,
                TableRef table => engine.SourceById(table.SourceId) is not IRefreshableSource,
                _ => false,
            });
        }

        return outside;
    }

    private static string SourceOf(Rel? rel)
    {
        var source = string.Empty;
        if (rel is not null)
        {
            ViewPlan.Walk(rel, message =>
            {
                if (source.Length == 0)
                {
                    source = message switch
                    {
                        RemoteQuery remote => remote.SourceId,
                        TableRef table => table.SourceId,
                        _ => source,
                    };
                }
            });
        }

        return source;
    }

    /// <summary>
    /// Refuses a database read that takes values from the rest of the statement: a view keeps a read
    /// between refreshes, and this one would change whenever the statement's other inputs do (D340).
    /// </summary>
    private static StandingViewException FedRead(string sourceId, string what) => new(
        ChalkErrorCodes.UnsupportedInView,
        $"The view reads source '{sourceId}' through {what}, so what it reads from the database changes "
        + "whenever the rest of the statement does, and could not be kept between refreshes. Let the "
        + "join be local: drop the rule that asks otherwise for this pair of sources from the join policy "
        + "the statement is prepared with, ViewOptions.Prepare's or the prepared statement's own.");

    /// <summary>
    /// A keyed result has no order, so a statement that ends in an <c>ORDER BY</c> without a
    /// <c>LIMIT</c> is refused; and a <c>LIMIT</c> is admitted only where its order is total — the
    /// statement's last step, ordered by every key column — since otherwise the input's order would
    /// choose which tied rows it keeps.
    /// </summary>
    private static void Order(Rel root, IReadOnlyList<string> key, int[] keyColumns)
    {
        var last = root;
        while (last.KindCase == Rel.KindOneofCase.Project)
        {
            last = last.Project.Input;
        }

        if (last.KindCase == Rel.KindOneofCase.Sort)
        {
            throw new StandingViewException(
                ChalkErrorCodes.UnsupportedInView,
                "The view's statement ends in an ORDER BY with no LIMIT, and a view's rows have no "
                + "order: a reader is sent them by key. Drop the ORDER BY.");
        }

        ViewPlan.Walk(root, message =>
        {
            if (message is not Rel rel)
            {
                return;
            }

            switch (rel.KindCase)
            {
                case Rel.KindOneofCase.Fetch:
                    throw new StandingViewException(
                        ChalkErrorCodes.UnsupportedInView,
                        "The view's statement takes a LIMIT or OFFSET with no ORDER BY, so the input's "
                        + "order would choose its rows. Order by the view's key, or drop the LIMIT.");
                case Rel.KindOneofCase.TopN
                    when !ReferenceEquals(rel, root)
                        || !keyColumns.All(column => rel.TopN.Fields.Any(
                            field => field.Expr.FieldRef is { } reference && (int)reference.Index == column)):
                    throw new StandingViewException(
                        ChalkErrorCodes.UnsupportedInView,
                        $"The view's statement takes a LIMIT whose ORDER BY does not order by the view's "
                        + $"key ({string.Join(", ", key)}) as its last step, so ties would let the input's "
                        + "order choose its rows. Order by every key column too, or drop the LIMIT.");
            }
        });
    }

    /// <summary>
    /// Refuses a column computed by accumulating floating point — <c>SUM</c> or <c>AVG</c> over
    /// <c>REAL</c> or <c>DOUBLE</c>, as an aggregate or over a window, or a host aggregate over them —
    /// unless the view opts in: a sum moves with the order its rows are added in.
    /// </summary>
    private static void Accumulation(Rel rel, ViewExactness exactness)
    {
        if (exactness == ViewExactness.EqualUpToRounding)
        {
            return;
        }

        var aggregate = rel.KindCase switch
        {
            Rel.KindOneofCase.Aggregate => rel.Aggregate,
            Rel.KindOneofCase.HashAggregate => rel.HashAggregate.Aggregate,
            Rel.KindOneofCase.StreamAggregate => rel.StreamAggregate.Aggregate,
            _ => null,
        };

        if (aggregate is not null)
        {
            for (var i = 0; i < aggregate.Measures.Count; i++)
            {
                var measure = aggregate.Measures[i];
                if (Accumulates(measure.Function, measure.UserFunction, measure.Args))
                {
                    throw Inexact(rel, aggregate.Measures.Count - i, What(measure.Function, measure.UserFunction), measure.Args);
                }
            }
        }

        if (rel.KindCase == Rel.KindOneofCase.Window)
        {
            var calls = rel.Window.Calls;
            for (var i = 0; i < calls.Count; i++)
            {
                var call = calls[i];
                var function = call.FunctionCase == WindowCall.FunctionOneofCase.Aggregate
                    ? call.Aggregate
                    : AggregateFunctionId.Unspecified;
                if (Accumulates(function, call.UserFunction, call.Args))
                {
                    throw Inexact(rel, calls.Count - i, What(function, call.UserFunction), call.Args);
                }
            }
        }
    }

    private static bool Accumulates(AggregateFunctionId function, string userFunction, IList<Expr> args) =>
        (function is AggregateFunctionId.Sum or AggregateFunctionId.Sum0 or AggregateFunctionId.Avg
            || userFunction.Length > 0)
        && args.Any(arg => arg.Type?.Kind is TypeKind.Fp32 or TypeKind.Fp64);

    private static string What(AggregateFunctionId function, string userFunction) =>
        userFunction.Length > 0
            ? $"the host aggregate {userFunction}"
            : function switch
            {
                AggregateFunctionId.Sum0 => "a SUM",
                AggregateFunctionId.Avg => "an AVG",
                _ => "a SUM",
            };

    private static StandingViewException Inexact(Rel rel, int fromEnd, string what, IList<Expr> args)
    {
        var fields = rel.RowType.Fields;
        var name = fromEnd <= fields.Count ? fields[^fromEnd].Name : string.Empty;
        var column = name.Length > 0 && !name.StartsWith('$') ? $"'{name}', " : string.Empty;
        var type = args.First(arg => arg.Type?.Kind is TypeKind.Fp32 or TypeKind.Fp64).Type.Kind == TypeKind.Fp32
            ? "REAL"
            : "DOUBLE";
        return new StandingViewException(
            ChalkErrorCodes.InexactAccumulation,
            $"The view's column {column}computed by {what} over a {type} value, accumulates in floating "
            + "point, so it could differ from what the statement returns by rounding. Cast the value to "
            + "DECIMAL to keep the view exact, or opt in with Exactness = ViewExactness.EqualUpToRounding.");
    }

    /// <summary>
    /// Refuses a host function whose result can change while its arguments do not: one declared
    /// <c>STABLE</c> or <c>VOLATILE</c>, and, when the view asks for an explicit declaration, one
    /// whose registration said nothing.
    /// </summary>
    private static void Deterministic(
        Dictionary<string, FunctionDescriptor> functions, string name, bool requireExplicitImmutable)
    {
        if (!functions.TryGetValue(name, out var function))
        {
            return;
        }

        switch (function.Volatility)
        {
            case Volatility.Stable or Volatility.Volatile:
                throw new StandingViewException(
                    ChalkErrorCodes.NondeterministicFunction,
                    $"The view calls {name}, a host function declared "
                    + $"{(function.Volatility == Volatility.Stable ? "STABLE" : "VOLATILE")}: its result can "
                    + "change while its arguments do not, so the view could not be what its statement "
                    + "returns at each epoch. Declare it Immutable() if its result depends only on its "
                    + "arguments, or move what it reads into a table refreshed in epochs.");
            case Volatility.Unspecified when requireExplicitImmutable:
                throw new StandingViewException(
                    ChalkErrorCodes.NondeterministicFunction,
                    $"The view calls {name}, a host function whose registration does not say Immutable(), "
                    + "and the view admits only one that does (RequireExplicitImmutable). Declare "
                    + "Immutable() on its registration if its result depends only on its arguments, or "
                    + "move what it reads into a table refreshed in epochs.");
        }
    }

    /// <summary>
    /// Refuses a source whose rows change outside this engine's refreshes, such as a database, unless
    /// the view reads databases when they are refreshed (D340).
    /// </summary>
    /// <remarks>
    /// A source another engine also refreshes changes when that engine says, which no refusal here can
    /// know when the view is made; a view over one sees such a change at its next update.
    /// </remarks>
    private static void InEpochs(ChalkEngine engine, string sourceId, string? table, ViewDatabaseReads databases)
    {
        var source = engine.SourceById(sourceId);
        var what = table is null ? $"source '{sourceId}'" : $"{table}, from source '{sourceId}'";
        if (source is not IRefreshableSource && databases == ViewDatabaseReads.None)
        {
            throw new StandingViewException(
                ChalkErrorCodes.SourceOutsideEpochs,
                $"The view reads {what}, whose rows change outside this engine's refreshes, so the view "
                + "could not read it as of an epoch. Load what the view needs into an in-process table "
                + "and refresh it in epochs, or set DatabaseReads = ViewDatabaseReads.WhenRefreshed to "
                + "read it again each time a refresh names it.");
        }
    }

    /// <summary>The catalog's functions by their <c>schema.name</c>, as a plan names one.</summary>
    private static Dictionary<string, FunctionDescriptor> Functions(CatalogContext catalog)
    {
        var functions = new Dictionary<string, FunctionDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var schema in catalog.Schemas)
        {
            foreach (var function in schema.Functions)
            {
                functions[$"{schema.Name}.{function.Name}"] = function;
            }
        }

        return functions;
    }
}
