using Chalk.Catalog;
using Chalk.Execution.Aggregation;
using Chalk.Execution.Expressions;
using Chalk.Execution.Joins;
using Chalk.Execution.Operators;
using Chalk.Execution.Vectors;
using Chalk.Execution.Windowing;
using Chalk.Ir;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Execution;

/// <summary>Builds one operator for an execution. Everything it needs was resolved at compilation.</summary>
internal delegate IBatchOperator OperatorFactory(OperatorContext context);

/// <summary>
/// Turns a validated <see cref="Plan"/> into a <see cref="CompiledPlan"/> (§6.8). Node kinds go
/// through a registry, so a later milestone adds a kind by adding one entry (work plan §5), and every
/// "unsupported" — a node kind, a kernel, a cast, an aggregate — surfaces here rather than mid-stream.
/// </summary>
internal static class PlanCompiler
{
    /// <summary>The node kinds M1 executes. A kind that is not here is unsupported, by construction.</summary>
    private static readonly IReadOnlyDictionary<Rel.KindOneofCase, Func<Compilation, Rel, string, OperatorFactory>>
        Registry = new Dictionary<Rel.KindOneofCase, Func<Compilation, Rel, string, OperatorFactory>>
        {
            [Rel.KindOneofCase.Read] = static (c, rel, path) => c.Read(rel, path),
            [Rel.KindOneofCase.IndexLookup] = static (c, rel, path) => c.IndexLookup(rel, path),
            [Rel.KindOneofCase.RemoteQuery] = static (c, rel, path) => c.RemoteQuery(rel, path),
            [Rel.KindOneofCase.VirtualTable] = static (c, rel, path) => c.Values(rel, path),
            [Rel.KindOneofCase.BoundTable] = static (c, rel, path) => c.ContextTable(rel, path),
            [Rel.KindOneofCase.Filter] = static (c, rel, path) => c.Filter(rel, path),
            [Rel.KindOneofCase.Project] = static (c, rel, path) => c.Project(rel, path),
            [Rel.KindOneofCase.Sort] = static (c, rel, path) => c.Sort(rel, path),
            [Rel.KindOneofCase.Fetch] = static (c, rel, path) => c.Fetch(rel, path),
            [Rel.KindOneofCase.TopN] = static (c, rel, path) => c.TopN(rel, path),
            [Rel.KindOneofCase.HashAggregate] = static (c, rel, path) =>
                c.Aggregate(rel, rel.HashAggregate.Aggregate, path),

            [Rel.KindOneofCase.HashJoin] = static (c, rel, path) => c.HashJoin(rel, path),
            [Rel.KindOneofCase.MergeJoin] = static (c, rel, path) => c.MergeJoin(rel, path),
            [Rel.KindOneofCase.NestedLoopJoin] = static (c, rel, path) => c.NestedLoopJoin(rel, path),
            [Rel.KindOneofCase.AsOfJoin] = static (c, rel, path) => c.AsOfJoin(rel, path),
            [Rel.KindOneofCase.Window] = static (c, rel, path) => c.Window(rel, path),
            [Rel.KindOneofCase.Hop] = static (c, rel, path) => c.Hop(rel, path),
            [Rel.KindOneofCase.Session] = static (c, rel, path) => c.Session(rel, path),
            [Rel.KindOneofCase.Unnest] = static (c, rel, path) => c.Unnest(rel, path),
            [Rel.KindOneofCase.SetOp] = static (c, rel, path) => c.SetOp(rel, path),
            [Rel.KindOneofCase.TableFunctionScan] = static (c, rel, path) =>
                c.TableFunctionScan(rel, path),

            // A logical Aggregate is always executable (02-ir.md §4); the executor picks hash itself.
            [Rel.KindOneofCase.Aggregate] = static (c, rel, path) => c.Aggregate(rel, rel.Aggregate, path),

            // So is a logical Join: an equi-condition becomes a hash join, anything else a nested
            // loop (12-joins.md §4).
            [Rel.KindOneofCase.Join] = static (c, rel, path) => c.LogicalJoin(rel, path),

            // M5, the federation nodes (D105). MaterialisedInput is not here: it is only ever built
            // by the AdaptiveJoin that owns the buffer it replays.
            [Rel.KindOneofCase.LookupJoin] = static (c, rel, path) => c.LookupJoin(rel, path),
            [Rel.KindOneofCase.AdaptiveJoin] = static (c, rel, path) => c.AdaptiveJoin(rel, path),
            [Rel.KindOneofCase.PartitionedScan] = static (c, rel, path) => c.PartitionedScan(rel, path),
            [Rel.KindOneofCase.MaterialisedInput] = static (c, rel, path) => c.MaterialisedInput(rel, path),
        };

    public static CompiledPlan Compile(
        Plan plan,
        CatalogContext catalog,
        IReadOnlyDictionary<string, ISourceRuntime> sourcesBySourceId,
        ExecutionSettings settings,
        IReadOnlyDictionary<string, string>? redactedQueryText = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(sourcesBySourceId);
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.BatchSize < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings), settings.BatchSize, "BatchSize must be at least one row.");
        }

        PlanValidator.Validate(plan);

        // The context scalars this plan reads at execution, and the slots they take after the
        // statement's own parameters (16-entitlements.md §2, D209). Empty under prepare-time binding.
        var boundScalars = Chalk.Ir.BoundScalars.Of(plan);
        var compilation = new Compilation(
            catalog,
            sourcesBySourceId,
            settings.Functions,
            BoundSlots.Of(boundScalars, plan.ParameterTypes.Count),
            settings.ForceBufferedWindows,
            redactedQueryText);
        var root = compilation.Node(plan.Root, "root");

        // D244: the output row's STRING columns are declared here, once, and every batch carries an
        // array of exactly the declared type.
        var stringLayouts = OutputStringLayouts.Resolve(
            plan, settings.OutputStrings, sourcesBySourceId);

        var compiled = new CompiledPlan(
            plan,
            ArrowTypeMapping.ToArrowSchema(plan.OutputType, stringLayouts),
            [.. plan.ParameterTypes.Select(ChalkType.FromProto)],
            boundScalars,
            [.. plan.OutputType.Fields.Select(f => ChalkType.FromProto(f.Type))],
            root,
            catalog,
            sourcesBySourceId,
            settings);

        // Build the tree once and discard, so that a missing kernel, an unbindable source or an
        // unsupported aggregate throws here. Every unsupported surfaces at PrepareAsync, never mid-stream.
        // An operator holds nothing that needs releasing until it has been enumerated.
        _ = root(compiled.NewContext());
        return compiled;
    }

    /// <summary>Compilation state: the catalog, the sources and the one expression compiler.</summary>
    private sealed class Compilation
    {
        private readonly CatalogContext _catalog;
        private readonly IReadOnlyDictionary<string, ISourceRuntime> _sources;
        private readonly HostFunctionSet _functions;

        /// <summary>Where this plan's named bound scalars sit among the execution's values (D209).</summary>
        private readonly IReadOnlyDictionary<string, int> _boundSlots;

        /// <summary>Compile every window onto the buffered operator, whatever its frame (D258.4).</summary>
        private readonly bool _bufferedWindows;

        /// <summary>
        /// Each pushed query's text with its literals as pseudonyms, by the text itself (D262), or
        /// null for a prepare that asked for no redaction — which is every prepare by default. It is
        /// what a <c>SourceExecutionException</c> quotes when the engine's redaction is on, and it
        /// was computed at prepare, because a failure path must not make a call.
        /// </summary>
        private readonly IReadOnlyDictionary<string, string>? _redactedQueryText;

        public Compilation(
            CatalogContext catalog,
            IReadOnlyDictionary<string, ISourceRuntime> sources,
            HostFunctionSet functions,
            IReadOnlyDictionary<string, int> boundSlots,
            bool bufferedWindows,
            IReadOnlyDictionary<string, string>? redactedQueryText = null)
        {
            _catalog = catalog;
            _sources = sources;
            _functions = functions;
            _boundSlots = boundSlots;
            _bufferedWindows = bufferedWindows;
            _redactedQueryText = redactedQueryText;
        }

        /// <summary>The redaction recorded for this pushed query, or null when there is none.</summary>
        private string? Redacted(string queryText) =>
            _redactedQueryText is not null
            && queryText.Length > 0
            && _redactedQueryText.TryGetValue(queryText, out var redacted)
                ? redacted
                : null;

        /// <summary>
        /// The expression compiler for this plan. It carries the catalog and the host's
        /// registrations, because a client-bodied call names a function rather than an id (D78).
        /// </summary>
        private ExpressionCompiler Expressions() => new(_catalog, _functions, _boundSlots);

        /// <summary>
        /// The buffer a <c>MaterialisedInput</c> in the subtree being compiled replays (D97). Set
        /// while an adaptive join compiles its branches and null everywhere else, which is what makes
        /// a stray MaterialisedInput a plan error rather than a null reference.
        /// </summary>
        private Materialisation? _materialisation;

        public OperatorFactory Node(Rel rel, string parentPath)
        {
            var path = $"{parentPath}/{rel.KindCase}";
            if (!Registry.TryGetValue(rel.KindCase, out var factory))
            {
                throw new UnsupportedFeatureException(
                    rel.KindCase.ToString(),
                    $"The M1 executor runs {string.Join(", ", Registry.Keys.OrderBy(k => k.ToString(), StringComparer.Ordinal))}; "
                    + "see docs/design/06-m1-workplan.md §4 for what is deliberately out.");
            }

            return factory(this, rel, path);
        }

        public OperatorFactory Read(Rel rel, string path)
        {
            var read = rel.Read;
            var table = read.Table;

            if (!_sources.TryGetValue(table.SourceId, out var source))
            {
                throw new UnsupportedFeatureException(
                    $"source '{table.SourceId}'",
                    $"The engine holds no source with that id. Known: {string.Join(", ", _sources.Keys)}.");
            }

            var descriptor = _catalog.FindTable(table.Schema, table.Table)
                ?? throw new InvalidPlanException(
                    "I-IR-6", path, $"the catalog has no table {table.Schema}.{table.Table}");

            var projection = read.Projection.Select(i => (int)i).ToArray();
            foreach (var column in projection)
            {
                if (column < 0 || column >= descriptor.Table.Columns.Count)
                {
                    throw new InvalidPlanException(
                        "I-IR-6",
                        path,
                        $"projection column {column} is out of range for a table of "
                        + $"{descriptor.Table.Columns.Count} columns");
                }
            }

            var schema = ArrowTypeMapping.ToArrowSchema(rel.RowType);
            var columnTypes = Types(rel.RowType);
            // `Read.filter` is the source's own predicate (M4). It travels as it is: a source that
            // never declared a matching shape refuses it with SourceContractException rather than
            // ignoring it, which would return too many rows.
            var pushedFilter = read.Filter;
            return context => new ScanOperator(
                context,
                path,
                source,
                new ScanRequest
                {
                    Table = descriptor.Table.Name,
                    Projection = projection,
                    OutputSchema = schema,
                    BatchSize = context.Settings.BatchSize,
                    PushedFilter = pushedFilter,
                    // `Read.row_goal` is a hint the source may size its first batch to (D276).
                    // Zero on the wire means "nothing above said", which is null here.
                    RowGoal = read.RowGoal > 0 ? read.RowGoal : null,
                },
                schema,
                columnTypes);
        }

        /// <summary>
        /// A subtree the source runs (D83, D84). The plan carries the generated SQL, the pushed
        /// subtree as IR and the parameters the query refers to; which of the two the adapter reads
        /// is its query language's business, and both are handed over.
        /// </summary>
        public OperatorFactory RemoteQuery(Rel rel, string path)
        {
            var query = rel.RemoteQuery;
            if (!_sources.TryGetValue(query.SourceId, out var source))
            {
                throw new UnsupportedFeatureException(
                    $"source '{query.SourceId}'",
                    $"The engine holds no source with that id. Known: {string.Join(", ", _sources.Keys)}.");
            }

            // The plan's parameter expressions are DynamicParams, in placeholder order; what the
            // operator needs is their indexes into this execution's bound values.
            var indexes = new int[query.Parameters.Count];
            for (var i = 0; i < indexes.Length; i++)
            {
                var parameter = query.Parameters[i];
                if (parameter.KindCase != Expr.KindOneofCase.Param)
                {
                    throw new InvalidPlanException(
                        "I-IR-4",
                        path,
                        $"RemoteQuery.parameters[{i}] is a {parameter.KindCase}; every entry must be a "
                        + "DynamicParam naming one of the statement's parameters");
                }

                indexes[i] = BoundSlots.Of(parameter.Param, _boundSlots);
            }

            var schema = ArrowTypeMapping.ToArrowSchema(rel.RowType);
            var rendered = RenderedBounds(query, indexes, path);
            return context => new RemoteQueryOperator(
                context,
                path,
                source,
                query,
                indexes,
                rendered,
                context.Settings.SourceTimeout(query.SourceId),
                schema,
                Types(rel.RowType),
                Redacted(query.QueryText));
        }

        /// <summary>
        /// The placeholders this executor writes a number into rather than binding (D288): where
        /// each one is in the text, the slot it reads, and whether it is the <c>LIMIT</c> or the
        /// <c>OFFSET</c> — which the pushed plan says, because that is the algebra the text was
        /// generated from.
        /// </summary>
        private IReadOnlyList<RemoteQueryOperator.RenderedBound> RenderedBounds(
            Chalk.Ir.RemoteQuery query, int[] indexes, string path)
        {
            if (query.RenderedBounds.Count == 0)
            {
                return [];
            }

            var bounds = new RemoteQueryOperator.RenderedBound[query.RenderedBounds.Count];
            for (var i = 0; i < bounds.Length; i++)
            {
                var position = (int)query.RenderedBounds[i];
                if (position >= indexes.Length)
                {
                    throw new InvalidPlanException(
                        "I-IR-4",
                        path,
                        $"RemoteQuery.rendered_bounds names placeholder {position} and the query "
                        + $"has {indexes.Length}");
                }

                var param = query.Parameters[position].Param;
                var clause = ClauseOf(query.PushedPlan, param);
                bounds[i] = new RemoteQueryOperator.RenderedBound(
                    position,
                    RowBound.Parameter(indexes[position], Named(param)),
                    clause);
            }

            return bounds;
        }

        /// <summary>
        /// Which clause a pushed bound is, read off the pushed plan: <c>OFFSET</c> when a
        /// <c>Fetch</c> under this query reads it as one, and <c>LIMIT</c> otherwise. Only the
        /// wording of a refusal turns on it, so a plan that says nothing gets the common answer.
        /// </summary>
        private static string ClauseOf(Chalk.Ir.Rel? rel, DynamicParam param)
        {
            if (rel is null)
            {
                return "LIMIT";
            }

            if (rel.KindCase == Rel.KindOneofCase.Fetch
                && rel.Fetch.OffsetParam is { } offset
                && Same(offset, param))
            {
                return "OFFSET";
            }

            foreach (var input in PlanWalker.Inputs(rel))
            {
                if (ClauseOf(input, param) == "OFFSET")
                {
                    return "OFFSET";
                }
            }

            return "LIMIT";
        }

        private static bool Same(DynamicParam left, DynamicParam right) =>
            left.BoundKey.Length == 0 && right.BoundKey.Length == 0
                ? left.Index == right.Index
                : string.Equals(left.BoundKey, right.BoundKey, StringComparison.Ordinal);

        /// <summary>
        /// A cross-source lookup join (M5, D105): the driving side streams, the lookup side is a
        /// remote query with one key set in it, and every call binds that set (§4).
        /// </summary>
        public OperatorFactory LookupJoin(Rel rel, string path) =>
            LookupJoin(rel.LookupJoin, rel, path, replay: null);

        /// <summary>
        /// The same, optionally reading its driving side from an adaptive join's materialisation
        /// rather than from the plan's own subtree.
        /// </summary>
        private OperatorFactory LookupJoin(
            Chalk.Ir.LookupJoin join, Rel rel, string path, Materialisation? replay)
        {
            if (join.PostJoinFilter is not null)
            {
                throw new UnsupportedFeatureException(
                    "a lookup join with a residual predicate",
                    "A LookupJoin carries only its equality in this milestone; the planner does not "
                    + "emit one with a post_join_filter, and a plan that has one was made by "
                    + "something else (ADR 0022).");
            }

            var lookup = join.Lookup;
            if (lookup.KindCase != Rel.KindOneofCase.RemoteQuery)
            {
                throw new UnsupportedFeatureException(
                    $"a lookup join whose lookup side is a {lookup.KindCase}",
                    "A LookupJoin's lookup side is the query a source runs with the keys bound into "
                    + "it, so it is always a RemoteQuery.");
            }

            var query = lookup.RemoteQuery;
            if (!_sources.TryGetValue(query.SourceId, out var source))
            {
                throw new UnsupportedFeatureException(
                    $"source '{query.SourceId}'",
                    $"The engine holds no source with that id. Known: {string.Join(", ", _sources.Keys)}.");
            }

            // The parameter that marks where the keys go: a bare KeySetParam for a single key
            // column, and the KeySetMatch itself for a composite one, whose columns say how wide a
            // bound key row is (F50).
            var ordinal = -1;
            for (var i = 0; i < query.Parameters.Count; i++)
            {
                if (query.Parameters[i].KindCase is Expr.KindOneofCase.KeySet
                    or Expr.KindOneofCase.KeySetMatch)
                {
                    ordinal = i;
                    break;
                }
            }

            if (ordinal < 0)
            {
                throw new InvalidPlanException(
                    "I-IR-17",
                    path,
                    "a LookupJoin's lookup query binds no key set: none of its parameters is a "
                    + "KeySetParam, so there is nowhere for the driving side's keys to go");
            }

            var drivingTypes = Types(join.Driving.RowType);
            var lookupTypes = Types(lookup.RowType);
            var drivingFactory = replay is null
                ? Node(join.Driving, path)
                : Replay(join.Driving, replay, path + "/driving");

            var schema = ArrowTypeMapping.ToArrowSchema(rel.RowType);
            var lookupSchema = ArrowTypeMapping.ToArrowSchema(lookup.RowType);
            var outputTypes = Types(rel.RowType);

            var drivingKeys = join.DrivingKeys.Select(k => (int)k).ToArray();
            var lookupKeys = join.LookupKeys.Select(k => (int)k).ToArray();

            return context => new Joins.LookupJoinOperator(
                context,
                path,
                schema,
                outputTypes,
                drivingTypes,
                lookupTypes,
                drivingFactory(context),
                source,
                query,
                ordinal,
                join.KeySetRows,
                join.Type,
                join.MaxKeysPerCall,
                drivingKeys,
                lookupKeys,
                context.Settings.SourceTimeout(query.SourceId),
                lookupSchema,
                residual: null,
                Redacted(query.QueryText));
        }

        /// <summary>
        /// An adaptive join (D97): the small side read once, its distinct keys counted, and the
        /// branch chosen from the count rather than from an estimate (§4).
        /// </summary>
        public OperatorFactory AdaptiveJoin(Rel rel, string path)
        {
            var adaptive = rel.AdaptiveJoin;
            var smallTypes = Types(adaptive.Small.RowType);
            var materialisation = new Materialisation(smallTypes);
            var small = Node(adaptive.Small, path + "/small");

            var lookup = LookupJoin(adaptive.Lookup, rel, path + "/lookup", materialisation);
            var local = NodeWith(adaptive.Local, path + "/local", materialisation);

            var schema = ArrowTypeMapping.ToArrowSchema(rel.RowType);
            return context => new Joins.AdaptiveJoinOperator(
                context,
                path,
                schema,
                Types(rel.RowType),
                small(context),
                materialisation,
                lookup(context),
                local(context),
                (int)adaptive.Key,
                smallTypes[(int)adaptive.Key],
                adaptive.MaxKeys,
                smallTypes,
                adaptive.Small.EstRowCount);
        }

        /// <summary>
        /// A partitioned table's branches, fanned out (D106, D107). Every branch is compiled as an
        /// ordinary subtree, so one partition may be a pushed remote query and the next a local scan.
        /// </summary>
        public OperatorFactory PartitionedScan(Rel rel, string path)
        {
            var scan = rel.PartitionedScan;
            var partitions = new List<OperatorFactory>(scan.Partitions.Count);
            for (var i = 0; i < scan.Partitions.Count; i++)
            {
                partitions.Add(Node(scan.Partitions[i], $"{path}[{i}]"));
            }

            var values = new List<ScalarValue?>(scan.Matches.Count);
            foreach (var match in scan.Matches)
            {
                values.Add(match.Value is null ? null : Literals.ToScalar(match.Value));
            }

            var schema = ArrowTypeMapping.ToArrowSchema(rel.RowType);
            var types = Types(rel.RowType);
            var factories = partitions
                .Select(p => new Func<OperatorContext, IBatchOperator>(c => p(c)))
                .ToArray();
            return context => new PartitionedScanOperator(context, path, schema, types, factories, values);
        }

        /// <summary>
        /// A subtree in which every <c>MaterialisedInput</c> replays <paramref name="materialisation"/>.
        /// </summary>
        private OperatorFactory NodeWith(Rel rel, string parentPath, Materialisation materialisation)
        {
            var previous = _materialisation;
            _materialisation = materialisation;
            try
            {
                return Node(rel, parentPath);
            }
            finally
            {
                _materialisation = previous;
            }
        }

        /// <summary>
        /// A <c>MaterialisedInput</c> reached through the ordinary registry: legal only inside an
        /// adaptive join's branch, which is the only place a buffer to replay exists.
        /// </summary>
        public OperatorFactory MaterialisedInput(Rel rel, string path)
        {
            if (_materialisation is not { } materialisation)
            {
                throw new InvalidPlanException(
                    "I-IR-18",
                    path,
                    "a MaterialisedInput outside an AdaptiveJoin: there is no materialisation for it "
                    + "to replay");
            }

            return Replay(rel, materialisation, path);
        }

        /// <summary>The leaf that replays an adaptive join's buffer.</summary>
        private OperatorFactory Replay(Rel rel, Materialisation materialisation, string path)
        {
            if (rel.KindCase != Rel.KindOneofCase.MaterialisedInput)
            {
                throw new InvalidPlanException(
                    "I-IR-18",
                    path,
                    "an AdaptiveJoin's branches read the small side through a MaterialisedInput; "
                    + $"found {rel.KindCase}");
            }

            var schema = ArrowTypeMapping.ToArrowSchema(rel.RowType);
            var types = Types(rel.RowType);
            return context => new MaterialisedInputOperator(context, path, schema, types, materialisation);
        }

        /// <summary>
        /// A lookup on a declared index (M2, §5). The ranges are resolved to constants and parameter
        /// slots here, so an execution binds them by reading a list rather than by evaluating
        /// anything; a bound that is neither a literal nor a parameter is a planner bug.
        /// </summary>
        public OperatorFactory IndexLookup(Rel rel, string path)
        {
            var lookup = rel.IndexLookup;
            var table = lookup.Table;

            if (lookup.Residual is not null)
            {
                throw new UnsupportedFeatureException(
                    "IndexLookup.residual",
                    "An M2 planner emits Filter(IndexLookup) rather than fusing the residual "
                    + "(docs/design/11-m2-index-support.md §3).");
            }

            if (!_sources.TryGetValue(table.SourceId, out var source))
            {
                throw new UnsupportedFeatureException(
                    $"source '{table.SourceId}'",
                    $"The engine holds no source with that id. Known: {string.Join(", ", _sources.Keys)}.");
            }

            var descriptor = _catalog.FindTable(table.Schema, table.Table)
                ?? throw new InvalidPlanException(
                    "I-IR-6", path, $"the catalog has no table {table.Schema}.{table.Table}");

            var index = descriptor.Table.Indexes.FirstOrDefault(
                i => string.Equals(i.Name, lookup.Index, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidPlanException(
                    "I-IR-6",
                    path,
                    $"table {table.Schema}.{table.Table} declares no index named '{lookup.Index}'");

            var projection = lookup.Projection.Select(i => (int)i).ToArray();
            foreach (var column in projection)
            {
                if (column < 0 || column >= descriptor.Table.Columns.Count)
                {
                    throw new InvalidPlanException(
                        "I-IR-6",
                        path,
                        $"projection column {column} is out of range for a table of "
                        + $"{descriptor.Table.Columns.Count} columns");
                }
            }

            var ranges = new List<IndexLookupOperator.RangePlan>(lookup.Ranges.Count);
            foreach (var range in lookup.Ranges)
            {
                if (range.Lower.Count > index.Columns.Count || range.Upper.Count > index.Columns.Count)
                {
                    throw new InvalidPlanException(
                        "I-IR-6",
                        path,
                        $"a range bounds more key columns than index '{index.Name}' has "
                        + $"({index.Columns.Count})");
                }

                if (range.Prefix && index.Kind is not (IndexKind.Ordered or IndexKind.Clustered or IndexKind.Prefix))
                {
                    throw new InvalidPlanException(
                        "I-IR-6",
                        path,
                        $"the range is a LIKE prefix but index '{index.Name}' is {index.Kind}; only "
                        + "an ordered, clustered or prefix index answers one");
                }

                ranges.Add(new IndexLookupOperator.RangePlan
                {
                    Lower = [.. range.Lower.Select(b => Bound(b, path))],
                    LowerInclusive = range.LowerInclusive,
                    Upper = [.. range.Upper.Select(b => Bound(b, path))],
                    UpperInclusive = range.UpperInclusive,
                    // D282: how a prefix is resolved depends on the index's kind, and the kind is
                    // known here rather than on the wire. A prefix index is sent the prefix; every
                    // other kind is sent the plain half-open range it stands for.
                    Prefix = range.Prefix,
                    PrefixIndex = index.Kind == IndexKind.Prefix,
                    IndexName = index.Name,
                    Table = descriptor.Table.Name,
                    SourceId = table.SourceId,
                });
            }

            var schema = ArrowTypeMapping.ToArrowSchema(rel.RowType);
            var columnTypes = Types(rel.RowType);
            // `IndexLookup.row_goal` is a hint the source may size its first batch to (D276). Zero
            // on the wire means "nothing above said", which is null here.
            var rowGoal = lookup.RowGoal > 0 ? lookup.RowGoal : (long?)null;

            // D283: a reversed lookup is a requirement, not a hint — the plan has no sort above it —
            // so a plan that asks for one from an index that never declared it is refused here
            // rather than answered forwards.
            if (lookup.Reverse)
            {
                foreach (var range in lookup.Ranges)
                {
                    if (!index.CanReverse(range.Upper.Count == 0))
                    {
                        throw new InvalidPlanException(
                            "I-IR-6",
                            path,
                            $"the lookup reads index '{index.Name}' backwards, and that index "
                            + $"declares reversal {index.Reversal}");
                    }
                }
            }

            return context => new IndexLookupOperator(
                context, path, source, descriptor.Table.Name, index.Name, ranges, projection, schema,
                columnTypes, rowGoal, lookup.Reverse);
        }

        /// <summary>A range bound: a literal the planner wrote down, or a parameter slot.</summary>
        private IndexLookupOperator.BoundPlan Bound(Expr bound, string path) => bound.KindCase switch
        {
            Expr.KindOneofCase.Literal =>
                IndexLookupOperator.BoundPlan.Constant(Literals.ToScalar(bound).ToClr()),
            Expr.KindOneofCase.Param =>
                IndexLookupOperator.BoundPlan.Parameter(BoundSlots.Of(bound.Param, _boundSlots)),
            _ => throw new InvalidPlanException(
                "I-IR-6",
                path,
                $"an IndexLookup range bound is a {bound.KindCase}; only a literal or a parameter "
                + "can bound an index key (docs/design/11-m2-index-support.md §3)"),
        };

        /// <summary>
        /// The four set operations of D69 (<c>15-zero-allocation-execution.md</c> §6):
        /// <c>UNION ALL</c> is pure pass-through, and the other three share one hash table over
        /// whole rows.
        /// </summary>
        public OperatorFactory SetOp(Rel rel, string path)
        {
            var inputs = rel.SetOp.Inputs.Select(input => Node(input, path)).ToArray();
            var schema = ArrowTypeMapping.ToArrowSchema(rel.RowType);
            var types = Types(rel.RowType);
            var kind = rel.SetOp.Kind;
            if (kind == SetOpKind.Unspecified)
            {
                throw new InvalidPlanException(
                    "I-IR-4", path, "the SetOp kind is unspecified");
            }

            // Every form but UNION ALL compares whole rows, and a v1 LIST has no equality (D58). The
            // planner refuses this too; the check is repeated here because the IR is the trust
            // boundary and a plan need not have come from Chalk's own planner.
            if (kind != SetOpKind.UnionAll)
            {
                for (var i = 0; i < types.Length; i++)
                {
                    if (types[i].Kind == TypeKind.List)
                    {
                        throw new UnsupportedFeatureException(
                            $"{kind} over the LIST column '{rel.RowType.Fields[i].Name}'",
                            "v1 lists have no ordering or equality, so a set operation that compares "
                            + "rows cannot have one in its row (docs/design/14-windows-ii.md §5). "
                            + "UNION ALL, which compares nothing, is allowed.");
                    }

                    // D291: the same of a composite value, which UNION ALL carries.
                    if (types[i].Kind == TypeKind.Composite)
                    {
                        throw new UnsupportedFeatureException(
                            $"{kind} over the COMPOSITE column '{rel.RowType.Fields[i].Name}'",
                            "a composite value has no equality, so a set operation that compares rows cannot "
                            + "have one in its row. UNION ALL, which compares nothing, carries one "
                            + "(docs/design/51-structured-function-results.md §1).");
                    }
                }
            }

            return context =>
            {
                var built = new IBatchOperator[inputs.Length];
                for (var i = 0; i < built.Length; i++)
                {
                    built[i] = inputs[i](context);
                }

                return kind == SetOpKind.UnionAll
                    ? new UnionAllOperator(context, path, schema, types, built)
                    : new HashSetOperator(
                        context, path, schema, types, built, kind, rel.EstRowCount);
            };
        }

        public OperatorFactory Values(Rel rel, string path)
        {
            var schema = ArrowTypeMapping.ToArrowSchema(rel.RowType);
            var types = Types(rel.RowType);
            var rows = rel.VirtualTable.Rows
                .Select(row => row.Values.Select(Literals.ToScalar).ToArray())
                .ToArray();
            return context => new ValuesOperator(context, path, schema, types, rows);
        }

        /// <summary>
        /// A relation the host bound by name, materialised at execution start (§4). Nothing is read
        /// here: a list too large to fold is deliberately not in the plan, so the rows arrive with
        /// the execution and not with the compilation.
        /// </summary>
        public OperatorFactory ContextTable(Rel rel, string path)
        {
            var schema = ArrowTypeMapping.ToArrowSchema(rel.RowType);
            var types = Types(rel.RowType);
            var name = rel.BoundTable.Name;
            return context => new ContextTableOperator(context, path, schema, types, name);
        }

        public OperatorFactory Filter(Rel rel, string path)
        {
            var input = Node(rel.Filter.Input, path);
            var schema = ArrowTypeMapping.ToArrowSchema(rel.RowType);
            var types = Types(rel.RowType);
            var condition = rel.Filter.Condition;
            return context => new FilterOperator(
                context, path, schema, types, input(context), Expressions().Compile(condition));
        }

        public OperatorFactory Project(Rel rel, string path)
        {
            var input = Node(rel.Project.Input, path);
            var schema = ArrowTypeMapping.ToArrowSchema(rel.RowType);
            var exprs = rel.Project.Exprs.ToArray();
            var types = Types(rel.RowType);
            return context =>
            {
                var compiler = Expressions();
                return new ProjectOperator(
                    context, path, schema, types, input(context), [.. exprs.Select(compiler.Compile)]);
            };
        }

        public OperatorFactory Sort(Rel rel, string path)
        {
            var input = Node(rel.Sort.Input, path);
            var schema = ArrowTypeMapping.ToArrowSchema(rel.RowType);
            var types = Types(rel.RowType);
            var ordering = new SortOrdering(rel.Sort.Fields, rel.Sort.Input.RowType);

            // What a blocking sort concatenates is its input, so its input's estimate is the one its
            // copiers start at (D258.3).
            var estimatedRows = rel.Sort.Input.EstRowCount;
            return context => new SortOperator(
                context, path, schema, types, input(context), ordering, estimatedRows);
        }

        public OperatorFactory Fetch(Rel rel, string path)
        {
            var input = Node(rel.Fetch.Input, path);
            var schema = ArrowTypeMapping.ToArrowSchema(rel.RowType);
            var offset = Bound(
                rel.Fetch.OffsetParam,
                rel.Fetch.OffsetParam is not null,
                rel.Fetch.Offset,
                rel.Fetch.Offset != 0,
                "OFFSET",
                path);
            // Unset count and unset count_param together mean "no limit", which is the one shape
            // that has no bound at all.
            var count = rel.Fetch.CountParam is not null || rel.Fetch.HasCount
                ? Bound(
                    rel.Fetch.CountParam,
                    rel.Fetch.CountParam is not null,
                    rel.Fetch.Count,
                    rel.Fetch.HasCount,
                    "LIMIT",
                    path)
                : (RowBound?)null;
            var types = Types(rel.RowType);
            return context => new FetchOperator(
                context, path, schema, types, input(context), offset, count);
        }

        public OperatorFactory TopN(Rel rel, string path)
        {
            var input = Node(rel.TopN.Input, path);
            var schema = ArrowTypeMapping.ToArrowSchema(rel.RowType);
            var types = Types(rel.RowType);
            var ordering = new SortOrdering(rel.TopN.Fields, rel.TopN.Input.RowType);
            var offset = Bound(
                rel.TopN.OffsetParam,
                rel.TopN.OffsetParam is not null,
                rel.TopN.Offset,
                rel.TopN.Offset != 0,
                "OFFSET",
                path);
            var count = Bound(
                rel.TopN.CountParam,
                rel.TopN.CountParam is not null,
                rel.TopN.Count,
                rel.TopN.Count != 0,
                "LIMIT",
                path);
            return context => new TopNOperator(
                context, path, schema, types, input(context), ordering, offset, count);
        }

        /// <summary>
        /// A <c>LIMIT</c> or <c>OFFSET</c> bound: the parameter when the plan carries one, else the
        /// number it wrote down (D285). The two are alternatives, so a plan that sets both is
        /// refused rather than read as one of them.
        /// </summary>
        private RowBound Bound(
            DynamicParam? param, bool hasParam, long literal, bool hasLiteral, string clause, string path)
        {
            if (!hasParam)
            {
                return RowBound.Constant(literal);
            }

            if (hasLiteral)
            {
                throw new InvalidPlanException(
                    "I-IR-5",
                    path,
                    $"a {clause} bound carries both a parameter and the literal {literal}; the two "
                    + "are alternatives and only one of them can be the bound");
            }

            return RowBound.Parameter(BoundSlots.Of(param!, _boundSlots), Named(param!));
        }

        /// <summary>A parameter as the plan names it, for a refusal: <c>?1</c>, or its bound key.</summary>
        private static string Named(DynamicParam param) =>
            param.BoundKey.Length == 0
                ? "?" + param.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : param.BoundKey;

        public OperatorFactory HashJoin(Rel rel, string path)
        {
            var join = rel.HashJoin;
            var shape = new JoinShape(this, rel, join.Left, join.Right, join.Type, path);
            var leftKeys = join.LeftKeys.Select(k => (int)k).ToArray();
            var rightKeys = join.RightKeys.Select(k => (int)k).ToArray();
            var residual = join.PostJoinFilter;
            return context => new HashJoinOperator(
                context,
                path,
                shape.Schema,
                shape.OutputTypes,
                shape.LeftTypes,
                shape.RightTypes,
                shape.Left(context),
                shape.Right(context),
                join.Type,
                residual is null ? null : Expressions().Compile(residual),
                leftKeys,
                rightKeys,
                shape.RightRows);
        }

        public OperatorFactory MergeJoin(Rel rel, string path)
        {
            var join = rel.MergeJoin;
            var shape = new JoinShape(this, rel, join.Left, join.Right, join.Type, path);
            var leftKeys = join.LeftKeys.Select(k => (int)k).ToArray();
            var rightKeys = join.RightKeys.Select(k => (int)k).ToArray();

            // The validator has already checked that both inputs claim an ordering over the keys; the
            // directions come from the left input's, which is the one the join's own output follows.
            var order = new JoinKeyOrder(MergeDirections(join.Left, leftKeys, path));
            var residual = join.PostJoinFilter;
            return context => new MergeJoinOperator(
                context,
                path,
                shape.Schema,
                shape.OutputTypes,
                shape.LeftTypes,
                shape.RightTypes,
                shape.Left(context),
                shape.Right(context),
                join.Type,
                residual is null ? null : Expressions().Compile(residual),
                leftKeys,
                rightKeys,
                order,
                shape.RightRows);
        }

        public OperatorFactory NestedLoopJoin(Rel rel, string path)
        {
            var join = rel.NestedLoopJoin;
            var shape = new JoinShape(this, rel, join.Left, join.Right, join.Type, path);
            var condition = join.Condition;
            return context => new NestedLoopJoinOperator(
                context,
                path,
                shape.Schema,
                shape.OutputTypes,
                shape.LeftTypes,
                shape.RightTypes,
                shape.Left(context),
                shape.Right(context),
                join.Type,
                condition is null ? null : Expressions().Compile(condition),
                shape.RightRows);
        }

        public OperatorFactory AsOfJoin(Rel rel, string path)
        {
            var join = rel.AsOfJoin;
            var shape = new JoinShape(this, rel, join.Left, join.Right, join.Type, path);
            var leftKeys = join.LeftKeys.Select(k => (int)k).ToArray();
            var rightKeys = join.RightKeys.Select(k => (int)k).ToArray();
            var preSorted = ClaimsKeyThenTime(join.Right, rightKeys, (int)join.RightTime);
            return context => new AsOfJoinOperator(
                context,
                path,
                shape.Schema,
                shape.OutputTypes,
                shape.LeftTypes,
                shape.RightTypes,
                shape.Left(context),
                shape.Right(context),
                join.Type,
                leftKeys,
                rightKeys,
                (int)join.LeftTime,
                (int)join.RightTime,
                join.Match,
                preSorted,
                shape.RightRows);
        }

        /// <summary>
        /// A logical <c>Join</c> is always executable (02-ir.md §4): with an equality between the two
        /// sides it runs as a hash join, and otherwise as a nested loop.
        /// </summary>
        public OperatorFactory LogicalJoin(Rel rel, string path)
        {
            var join = rel.Join;
            var leftWidth = join.Left.RowType.Fields.Count;
            var leftKeys = new List<int>();
            var rightKeys = new List<int>();
            var residual = JoinConditions.Split(
                join.Condition, leftWidth, join.Right.RowType.Fields.Count, leftKeys, rightKeys);

            if (leftKeys.Count == 0)
            {
                return NestedLoopJoin(
                    new Rel
                    {
                        RowType = rel.RowType,
                        NestedLoopJoin = new NestedLoopJoin
                        {
                            Left = join.Left,
                            Right = join.Right,
                            Condition = join.Condition,
                            Type = join.Type,
                        },
                    },
                    path);
            }

            var hash = new HashJoin { Left = join.Left, Right = join.Right, Type = join.Type };
            hash.LeftKeys.AddRange(leftKeys.Select(k => (uint)k));
            hash.RightKeys.AddRange(rightKeys.Select(k => (uint)k));
            if (residual is not null)
            {
                hash.PostJoinFilter = residual;
            }

            return HashJoin(new Rel { RowType = rel.RowType, HashJoin = hash }, path);
        }

        /// <summary>The directions of the collation a merge-join input claims over its keys.</summary>
        private static IReadOnlyList<SortDirection> MergeDirections(Rel input, int[] keys, string path)
        {
            foreach (var collation in input.Collations)
            {
                if (collation.Fields.Count < keys.Length)
                {
                    continue;
                }

                var directions = new SortDirection[keys.Length];
                var matched = true;
                for (var i = 0; i < keys.Length && matched; i++)
                {
                    var field = collation.Fields[i];
                    matched = field.Expr is { KindCase: Expr.KindOneofCase.FieldRef }
                        && field.Expr.FieldRef.Index == (uint)keys[i];
                    if (matched)
                    {
                        directions[i] = field.Direction;
                    }
                }

                if (matched)
                {
                    return directions;
                }
            }

            throw new InvalidPlanException(
                "I-IR-5",
                path,
                "a MergeJoin input must claim an ordering whose leading fields are the join keys in "
                + "the join's own key order");
        }

        /// <summary>
        /// Whether the right input already delivers its rows grouped by key and ordered by time, in
        /// which case the ASOF operator can skip sorting each partition (12-joins.md §4).
        /// </summary>
        private static bool ClaimsKeyThenTime(Rel input, int[] keys, int time)
        {
            foreach (var collation in input.Collations)
            {
                if (collation.Fields.Count < keys.Length + 1)
                {
                    continue;
                }

                var prefix = new HashSet<uint>();
                var ascending = true;
                for (var i = 0; i < keys.Length; i++)
                {
                    var field = collation.Fields[i];
                    if (field.Expr is not { KindCase: Expr.KindOneofCase.FieldRef })
                    {
                        ascending = false;
                        break;
                    }

                    prefix.Add(field.Expr.FieldRef.Index);
                }

                var last = collation.Fields[keys.Length];
                if (ascending
                    && prefix.Count == keys.Length
                    && keys.All(k => prefix.Contains((uint)k))
                    && last.Expr is { KindCase: Expr.KindOneofCase.FieldRef }
                    && last.Expr.FieldRef.Index == (uint)time
                    && last.Direction is SortDirection.AscNullsFirst or SortDirection.AscNullsLast)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// One window node (D48). The frame and the calls are resolved here so that an unsupported
        /// frame, an unsupported function or a call the operator cannot read is an exception from
        /// PrepareAsync; the evaluators themselves are built per execution because they own state.
        /// </summary>
        public OperatorFactory Window(Rel rel, string path)
        {
            var window = rel.Window;
            var input = Node(window.Input, path);
            var schema = ArrowTypeMapping.ToArrowSchema(rel.RowType);
            var outputTypes = Types(rel.RowType);
            var inputTypes = Types(window.Input.RowType);
            var spec = WindowCompiler.Spec(window, inputTypes, path);
            _ = WindowCompiler.Evaluators(window, inputTypes, path, _catalog, _functions);

            var estimatedRows = window.Input.EstRowCount;

            // D258.4: a frame that ends at or before the current row, over calls that can all be
            // answered from the rows still in hand, takes the streaming operator. The choice is made
            // once, here, because it is a property of the plan and not of the data.
            var streaming = _bufferedWindows
                ? null
                : WindowCompiler.Streaming(window, spec, inputTypes, path);
            if (streaming is not null)
            {
                return context => new StreamingWindowOperator(
                    context,
                    path,
                    schema,
                    outputTypes,
                    inputTypes,
                    input(context),
                    spec,
                    WindowCompiler.Streaming(window, spec, inputTypes, path)!);
            }

            return context => new WindowOperator(
                context,
                path,
                schema,
                outputTypes,
                inputTypes,
                input(context),
                spec,
                WindowCompiler.Evaluators(window, inputTypes, path, _catalog, _functions),
                estimatedRows);
        }

        public OperatorFactory Hop(Rel rel, string path)
        {
            var hop = rel.Hop;
            var input = Node(hop.Input, path);
            var schema = ArrowTypeMapping.ToArrowSchema(rel.RowType);
            var outputTypes = Types(rel.RowType);
            var timeType = ChalkType.FromProto(hop.Input.RowType.Fields[(int)hop.TimeColumn].Type);
            var column = (int)hop.TimeColumn;
            var slide = Constant(hop.Slide, "HOP's slide", path);
            var size = Constant(hop.Size, "HOP's size", path);
            return context => new HopOperator(
                context,
                path,
                schema,
                outputTypes,
                input(context),
                column,
                timeType,
                slide(context),
                size(context));
        }

        public OperatorFactory Session(Rel rel, string path)
        {
            var session = rel.Session;
            var input = Node(session.Input, path);
            var schema = ArrowTypeMapping.ToArrowSchema(rel.RowType);
            var outputTypes = Types(rel.RowType);
            var inputTypes = Types(session.Input.RowType);
            var keys = session.PartitionKeys.Select(k => (int)k).ToArray();
            var column = (int)session.TimeColumn;
            var gap = Constant(session.Gap, "SESSION's gap", path);
            return context => new SessionOperator(
                context,
                path,
                schema,
                outputTypes,
                inputTypes,
                input(context),
                keys,
                column,
                gap(context));
        }

        public OperatorFactory Unnest(Rel rel, string path)
        {
            var unnest = rel.Unnest;
            var input = Node(unnest.Input, path);
            var schema = ArrowTypeMapping.ToArrowSchema(rel.RowType);
            var outputTypes = Types(rel.RowType);
            var column = (int)unnest.ListColumn;
            var ordinality = unnest.WithOrdinality;
            var keepEmpty = unnest.KeepEmpty;
            return context => new UnnestOperator(
                context, path, schema, outputTypes, input(context), column, ordinality, keepEmpty);
        }

        /// <summary>
        /// A window function's interval operand: a literal resolved now, or a parameter read when the
        /// execution starts. Either way it is one value for the whole run.
        /// </summary>
        private Func<OperatorContext, ScalarValue> Constant(Expr expr, string what, string path)
        {
            switch (expr?.KindCase)
            {
                case Expr.KindOneofCase.Literal:
                {
                    var value = Literals.ToScalar(expr);
                    return _ => value;
                }

                case Expr.KindOneofCase.Param:
                {
                    var index = BoundSlots.Of(expr.Param, _boundSlots);
                    return context => context.Parameters[index];
                }

                default:
                    throw new UnsupportedFeatureException(
                        $"{what} at {path}",
                        "A window's slide, size and gap are constants or parameters (I-IR-14).");
            }
        }

        public OperatorFactory Aggregate(Rel rel, Aggregate aggregate, string path)
        {
            var input = Node(aggregate.Input, path);
            var schema = ArrowTypeMapping.ToArrowSchema(rel.RowType);
            var types = Types(rel.RowType);
            var inputRow = aggregate.Input.RowType;
            var keys = aggregate.Groupings[0].Keys.Select(i => (int)i).ToArray();

            foreach (var measure in aggregate.Measures)
            {
                if (measure.UserFunction.Length > 0)
                {
                    continue;
                }

                var maximum = Aggregates.MaximumArguments(measure.Function);
                if (measure.Args.Count > maximum)
                {
                    throw new UnsupportedFeatureException(
                        $"{measure.Function} with {measure.Args.Count} arguments",
                        $"It takes at most {maximum} (docs/design/02-ir.md §6, "
                        + "docs/design/14-windows-ii.md §3).");
                }

                if (measure.OrderBy.Count > 1)
                {
                    throw new UnsupportedFeatureException(
                        $"{measure.Function} ordered by {measure.OrderBy.Count} keys",
                        "The executor orders a holistic aggregate by one key "
                        + "(docs/design/14-windows-ii.md §3).");
                }
            }

            // A13: one grouping only, and mixed DISTINCT/non-DISTINCT would need grouping sets.
            var measures = aggregate.Measures.ToArray();
            var declaredDistinct = DeclaredDistinct(aggregate, keys);
            return context =>
            {
                var compiler = Expressions();
                var keyExprs = keys
                    .Select(k => compiler.Compile(IrBuilders.FieldRef(k, inputRow.Fields[k].Type)))
                    .ToArray();

                var plans = new MeasurePlan[measures.Length];
                for (var m = 0; m < measures.Length; m++)
                {
                    var measure = measures[m];
                    var ordered = measure.OrderBy.Count > 0 ? measure.OrderBy[0] : null;
                    var descending = ordered is not null && Aggregates.IsDescending(ordered.Direction);

                    // A percentile's value is its WITHIN GROUP expression, not its argument: Calcite
                    // puts the fraction in the argument list (D57).
                    var valueExpr = measure.UserFunction.Length == 0
                        && Aggregates.IsPercentile(measure.Function)
                        ? ordered?.Expr
                            ?? throw new InvalidPlanException(
                                "I-IR-13",
                                path,
                                $"{measure.Function} has no WITHIN GROUP ordering to take a percentile of")
                        : measure.Args.Count > 0 ? measure.Args[0] : null;

                    var argument = valueExpr is null ? null : compiler.Compile(valueExpr);
                    var argumentType = argument?.Type;

                    // The key is only read when it is a different column from the value; a percentile
                    // orders by the value itself, and so does an aggregate with no ORDER BY.
                    var keyExpr = measure.UserFunction.Length == 0
                        && Aggregates.IsPercentile(measure.Function) ? null : ordered?.Expr;
                    var key = keyExpr is null ? null : compiler.Compile(keyExpr);

                    plans[m] = new MeasurePlan
                    {
                        Accumulator = measure.UserFunction.Length > 0
                            ? UserFunctionBinding.AggregateAccumulator(
                                _catalog, _functions, measure.UserFunction, ChalkType.FromProto(measure.Type))
                            : MeasureAccumulator.Create(
                                measure,
                                ChalkType.FromProto(measure.Type),
                                argumentType,
                                key?.Type,
                                descending),
                        Argument = argument,
                        Filter = measure.Filter is null ? null : compiler.Compile(measure.Filter),
                        Distinct = measure.Distinct,
                        ArgumentKind = argumentType is { } t ? ColumnKinds.Of(t) : ColumnKind.Int64,
                        ArgumentWidth = argumentType is { } w ? ColumnKinds.Width(ColumnKinds.Of(w)) : 8,
                        OrderKey = key,
                        OrderKeyKind = key is { } k ? ColumnKinds.Of(k.Type) : ColumnKind.Int64,
                        OrderKeyWidth = key is { } kw ? ColumnKinds.Width(ColumnKinds.Of(kw.Type)) : 8,
                    };
                }

                // An aggregate's own estimate is a count of groups, which is what every piece of
                // its per-group state scales with (D258.3).
                return new HashAggregateOperator(
                    context, path, schema, types, input(context), keyExprs, plans, rel.EstRowCount, declaredDistinct);
            };
        }

        /// <summary>
        /// The distinct count a source declares for a single grouping key, which is what lets the
        /// hash aggregate build a perfect hash over a small key set (D255). -1 — no statistic — is
        /// the usual answer and costs nothing.
        /// </summary>
        /// <remarks>
        /// Only a grouping that reads a table directly is resolved: through a filter the count is an
        /// upper bound rather than the size of the key set, and through a projection the key need
        /// not be a column at all. Both simply get no statistic. The count is a hint either way —
        /// <c>ColumnStatistics</c> has no way to say that one is exact, and a POCO table's is exact
        /// only where an index covers the column — and the operator treats it as one: a key the
        /// declared set turns out not to contain is a miss that its general table answers.
        /// </remarks>
        private long DeclaredDistinct(Aggregate aggregate, int[] keys)
        {
            if (keys.Length != 1 || aggregate.Input?.KindCase != Rel.KindOneofCase.Read)
            {
                return -1;
            }

            var read = aggregate.Input.Read;
            if (_catalog.FindTable(read.Table.Schema, read.Table.Table) is not { } descriptor)
            {
                return -1;
            }

            var field = keys[0];
            var column = read.Projection.Count > 0
                ? field >= 0 && field < read.Projection.Count ? (int)read.Projection[field] : -1
                : field;

            return column >= 0 && column < descriptor.Table.Columns.Count
                ? descriptor.Table.Columns[column].Statistics.DistinctCount
                : -1;
        }

        /// <summary>
        /// A client-bodied table function (D78): a leaf whose rows the host produces. Its arguments
        /// are constants or bound parameters over no input row, which is what makes it a leaf.
        /// </summary>
        public OperatorFactory TableFunctionScan(Rel rel, string path)
        {
            var scan = rel.TableFunctionScan;
            var schema = ArrowTypeMapping.ToArrowSchema(rel.RowType);
            var types = Types(rel.RowType);

            if (_catalog is null)
            {
                throw new UnsupportedFeatureException(
                    $"table function {scan.Function}",
                    "This plan was compiled without a catalog, so a declared function cannot be "
                    + "resolved.");
            }

            var descriptor = UserFunctionBinding.Resolve(_catalog, scan.Function);
            var host = UserFunctionBinding.Find(_functions, descriptor);
            UserFunctionBinding.Check(descriptor, host, $"table function {scan.Function}");

            var arguments = new Func<OperatorContext, ScalarValue>[scan.Args.Count];
            for (var i = 0; i < arguments.Length; i++)
            {
                arguments[i] = Constant(
                    scan.Args[i], $"argument {i} of {scan.Function}", $"{path}.args[{i}]");
            }

            // Bound once at compilation, so a wrong row type or an unsupported column kind is an
            // error from PrepareAsync (§6.8).
            _ = ((HostTable)host!).Accept(
                new TableRowsFactory(
                    new object?[arguments.Length], descriptor.ReturnsTable, $"table function {scan.Function}"));

            return context =>
            {
                var values = new object?[arguments.Length];
                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = arguments[i](context).ToClr();
                }

                var rows = ((HostTable)host).Accept(
                    new TableRowsFactory(
                        values, descriptor.ReturnsTable, $"table function {scan.Function}"));
                return new TableFunctionScanOperator(context, path, schema, types, rows);
            };
        }

        private static ChalkType[] Types(RowType row) =>
            [.. row.Fields.Select(f => ChalkType.FromProto(f.Type))];

        /// <summary>
        /// The shape every join operator needs: the output schema and types, the two inputs' types,
        /// and the two child factories. SEMI and ANTI output the left row only, which is the one
        /// place the join type changes the shape rather than the semantics.
        /// </summary>
        private sealed class JoinShape
        {
            public JoinShape(Compilation compilation, Rel rel, Rel left, Rel right, JoinType type, string path)
            {
                Schema = ArrowTypeMapping.ToArrowSchema(rel.RowType);
                OutputTypes = Types(rel.RowType);
                LeftTypes = Types(left.RowType);
                RightTypes = Types(right.RowType);
                _left = compilation.Node(left, path);
                _right = compilation.Node(right, path);

                // The right input is the side a pair join buffers whole, so its estimate is what the
                // build side's copiers start at (D258.3).
                RightRows = right.EstRowCount;

                var expected = type is JoinType.Semi or JoinType.Anti
                    ? LeftTypes.Length
                    : LeftTypes.Length + RightTypes.Length;
                if (OutputTypes.Length != expected)
                {
                    throw new InvalidPlanException(
                        "I-IR-4",
                        path,
                        $"a {type} join over rows of {LeftTypes.Length} and {RightTypes.Length} fields "
                        + $"has an output row of {OutputTypes.Length}; it must be {expected}");
                }
            }

            private readonly OperatorFactory _left;
            private readonly OperatorFactory _right;

            public ArrowSchema Schema { get; }

            public ChalkType[] OutputTypes { get; }

            public ChalkType[] LeftTypes { get; }

            public ChalkType[] RightTypes { get; }

            /// <summary>The plan's estimate of the right input's rows, or zero where it has none.</summary>
            public double RightRows { get; }

            public IBatchOperator Left(OperatorContext context) => _left(context);

            public IBatchOperator Right(OperatorContext context) => _right(context);
        }
    }
}

/// <summary>The one IR node the compiler has to synthesise: an aggregate's grouping key reference.</summary>
internal static class IrBuilders
{
    public static Expr FieldRef(int index, Ir.Type type) =>
        new() { Type = type, FieldRef = new FieldRef { Index = (uint)index } };
}
