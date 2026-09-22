using System.Collections.Concurrent;
using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Client.Rpc;
using Chalk.Execution;
using Chalk.Ir;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Client;

/// <summary>
/// A planned and compiled statement. Thread-safe and reusable: bind different values to it as often
/// as you like.
/// </summary>
/// <remarks>
/// A statement with list-valued parameters has one compiled plan per <em>shape</em> — the tuple of
/// list lengths — because a plan containing n literals depends on n (D29). The one-element shape is
/// compiled at prepare time so element types and validation errors surface there; other shapes are
/// planned lazily on first use and cached.
/// </remarks>
public sealed class PreparedQuery
{
    private readonly ChalkEngine _engine;
    private readonly ParameterRewriter _rewriter;
    private readonly PrepareOptions _options;
    private readonly ConcurrentDictionary<string, ShapePlan> _shapes = new(StringComparer.Ordinal);
    private readonly RenderedStatement _prepared;

    internal PreparedQuery(
        ChalkEngine engine,
        string sql,
        ParameterRewriter rewriter,
        PrepareOptions options,
        PlanResult result,
        CompiledPlan compiled,
        IReadOnlyList<int> prepareShape,
        RenderedStatement rendered,
        RequestContext? context)
    {
        _engine = engine;
        Context = context;
        RequiredContext = RequiredNames(result.Plan, context);
        FoldedContext = FoldedNames(context);
        foreach (var name in FoldedContext)
        {
            _folded.Add(name);
        }

        _rewriter = rewriter;
        _options = options;
        // The tables this plan reads, collected once (D271 (d)): staleness is per table now, and
        // asking the plan again on every execution would put a tree walk on the execution path.
        _tablesRead = ChalkEngine.TablesRead(result.Plan);
        Sql = sql;
        Plan = result.Plan;
        // When the prepare asked for a redaction and the statement's parameters are named, the
        // names go back where the rewrite put a `?` — in the statement and in the plan text, whose
        // dynamic parameters read `?0`, `?1`, … (D287). Anything else is served as it came.
        var names = result.RedactedSql is null ? null : rewriter.SlotNames(rendered);
        PlanText =
            names is null || result.PlanText is null
                ? result.PlanText
                : ParameterNames.Substitute(result.PlanText, names, indexed: true);
        RedactedSql =
            names is null || result.RedactedSql is null
                ? result.RedactedSql
                : ParameterNames.Substitute(result.RedactedSql, names, indexed: false);
        PlanningStats = result.Stats;
        PlanningState = result.PlanningState;

        Columns = Names(compiled.OutputSchema);
        Extensions = result.Extensions;

        _compiled = compiled;
        OutputSchema = compiled.OutputSchema;
        ParameterTypes = compiled.ParameterTypes;

        _prepared = rendered;
        _prepareShape = prepareShape;
        _shapes[Key(prepareShape)] = new ShapePlan(compiled, rendered);
        AssignParameterTypes(rendered, compiled.ParameterTypes);
    }

    /// <summary>The SQL as the host wrote it, parameter styles and all.</summary>
    public string Sql { get; }

    /// <summary>
    /// The execution context this query was prepared with, or null when it was prepared without one
    /// (step 26, D152). A query prepared with a context carries it: executing it binds those same
    /// values, and an execute-time context is refused for it.
    /// </summary>
    public RequestContext? Context { get; }

    /// <summary>
    /// The context names this plan still needs bound at execution, which is empty under prepare-time
    /// binding because every one of them was folded into a literal
    /// (<c>docs/design/16-entitlements.md</c> §2).
    /// </summary>
    public IReadOnlyList<string> RequiredContext { get; }

    /// <summary>
    /// The context names whose values this plan <em>folded</em> — they are in the leaf, in the plan
    /// text and in the digest, and no execution and no narrowing may give them another value (D232,
    /// D233).
    /// </summary>
    public IReadOnlyList<string> FoldedContext { get; }

    /// <summary>One per output column, in order.</summary>
    public IReadOnlyList<PreparedColumn> Columns { get; }

    /// <summary>
    /// What the planner's extensions had to say about this statement, each a message addressed by
    /// its type URL (step 26c, D213). Empty when none had anything to say, which is the whole of a
    /// core client's experience of them; <see cref="Extension{T}"/> is how a package that knows one
    /// reads its own.
    /// </summary>
    public IReadOnlyList<Google.Protobuf.WellKnownTypes.Any> Extensions { get; }

    /// <summary>
    /// The message of type <typeparamref name="T"/> the response's extension slot carried, or null
    /// when it carried none.
    /// </summary>
    public T? Extension<T>()
        where T : class, Google.Protobuf.IMessage<T>, new()
    {
        var descriptor = new T().Descriptor;
        foreach (var extension in Extensions)
        {
            if (extension.Is(descriptor))
            {
                return extension.Unpack<T>();
            }
        }

        return null;
    }

    /// <summary>
    /// Lets one extension decorate the schema every batch of this query carries, and hear about each
    /// execution before it runs (step 26c, D213). The one seam the core keeps for a feature it does
    /// not itself know: today the entitlement wrapper puts <c>chalk.disclosure</c> on the fields and
    /// raises its audit event through it, and nothing in this package names either.
    /// </summary>
    internal void Decorate(
        Func<ArrowSchema, ArrowSchema>? decorateSchema, Action<PreparedQuery>? beforeExecute)
    {
        _decorateSchema = decorateSchema;
        _beforeExecute = beforeExecute;
        if (decorateSchema is null)
        {
            return;
        }

        var decorated = decorateSchema(_compiled.OutputSchema);
        if (ReferenceEquals(decorated, _compiled.OutputSchema))
        {
            return;
        }

        _compiled = _compiled.WithOutputSchema(decorated);
        OutputSchema = _compiled.OutputSchema;
        _shapes[Key(_prepareShape)] = new ShapePlan(_compiled, _prepared);
    }

    private Func<ArrowSchema, ArrowSchema>? _decorateSchema;
    private Action<PreparedQuery>? _beforeExecute;
    private CompiledPlan _compiled;
    private readonly IReadOnlyList<int> _prepareShape;

    /// <summary>The output columns, named. A column discloses nothing beyond its name to this package.</summary>
    private static IReadOnlyList<PreparedColumn> Names(ArrowSchema schema)
    {
        var fields = schema.FieldsList;
        var columns = new List<PreparedColumn>(fields.Count);
        for (var i = 0; i < fields.Count; i++)
        {
            columns.Add(new PreparedColumn { Name = fields[i].Name });
        }

        return columns;
    }

    /// <summary>The plan for the shape prepared at prepare time.</summary>
    public Plan Plan { get; }

    public ulong PlanDigest => Plan.PlanDigest;

    /// <summary>The Arrow schema every produced batch matches.</summary>
    public ArrowSchema OutputSchema { get; private set; }

    /// <summary>One per <c>?</c> in the rewritten SQL, as the planner inferred them.</summary>
    public IReadOnlyList<ChalkType> ParameterTypes { get; }

    /// <summary>The logical parameters after the D27 rewrite. A recurring <c>@name</c> appears once.</summary>
    public IReadOnlyList<ParameterDescriptor> Parameters => _rewriter.Parameters;

    public ParameterStyle ParameterStyle => _rewriter.Style;

    /// <summary>The planner's plan text, when it was asked for.</summary>
    /// <remarks>
    /// Rendered with this statement's literals redacted when <c>IncludeRedactedSql</c> was on for
    /// the prepare (D262), so a host that logs the plan text logs the same pseudonyms it logs in
    /// <see cref="RedactedSql"/>.
    /// </remarks>
    public string? PlanText { get; }

    /// <summary>
    /// This statement with every literal replaced by a keyed pseudonym, so the text can be logged
    /// (D262, <c>docs/design/37-redacted-sql.md</c>). Null unless
    /// <see cref="PrepareOptions.IncludeRedactedSql"/> — or the engine's
    /// <c>Redaction.IncludeRedactedSql</c> — asked for it, in which case nothing was computed to
    /// produce it.
    /// </summary>
    /// <remarks>
    /// It is the redaction of the statement <em>the planner saw</em>: <see cref="Sql"/> after the
    /// D27 parameter rewrite, so every parameter reads as <c>?</c> whichever style the host wrote.
    /// A pseudonym is not anonymisation — a low-entropy value is brute-forceable by anyone who knows
    /// the shape and holds the salt.
    /// </remarks>
    public string? RedactedSql { get; }

    /// <summary>
    /// The names the plan <em>still</em> needs at execution, in the order the planner listed them:
    /// what the report asks for, less whatever this prepared query already carries.
    /// </summary>
    /// <remarks>
    /// Under prepare-time binding — the primary mode — a list too large to fold becomes a
    /// <c>ContextTable</c> the executor materialises, and the planner names it here because
    /// execution has to bind it. The prepared query carries its context, so it does, and nothing is
    /// missing; what is left over is what a host forgot, and that is what
    /// <c>ContextRequiredException</c> is for.
    /// </remarks>
    private static IReadOnlyList<string> RequiredNames(Plan plan, RequestContext? context)
    {
        // A name bound as a shape holds no value at all, so everything the plan names for it is still
        // required (D209, D232); a name bound to values is carried by the prepared query, and what is
        // left over is what the host forgot.
        // The executed walk, because binding is the executor's (F92): a bound relation is one this
        // client materialises, and what a source was sent instead is a key set in the remote query's
        // own parameter list, which this walk reads.
        List<string>? names = null;
        foreach (var rel in PlanWalker.ExecutedRels(plan))
        {
            if (rel.KindCase != Rel.KindOneofCase.BoundTable)
            {
                continue;
            }

            Require(rel.BoundTable.Name, context, ref names);
        }

        foreach (var expr in PlanWalker.AllExprs(plan))
        {
            if (expr.KindCase != Expr.KindOneofCase.Param
                || expr.Param.BoundKey.Length == 0)
            {
                continue;
            }

            Require(expr.Param.BoundKey, context, ref names);
        }

        return names ?? (IReadOnlyList<string>)[];
    }

    /// <summary>
    /// The names this prepare bound to values, in ordinal order — the half that is in the plan
    /// (D232). Empty for a query prepared with no context and for one prepared with a bare shape.
    /// </summary>
    private static IReadOnlyList<string> FoldedNames(RequestContext? context)
    {
        if (context is null || context.IsEmpty || context.ShapeOnly)
        {
            return [];
        }

        var shapes = context.ShapeNames;
        var folded = new List<string>();
        foreach (var name in context.Scalars.Keys
            .Concat(context.Lists.Keys)
            .Concat(context.Relations.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal))
        {
            if (!shapes.Contains(name, StringComparer.Ordinal))
            {
                folded.Add(name);
            }
        }

        return folded;
    }

    private static void Require(string name, RequestContext? context, ref List<string>? names)
    {
        if (context is not null
            && !context.IsShape(name)
            && (context.Lists.ContainsKey(name)
                || context.Relations.ContainsKey(name)
                || context.Scalars.ContainsKey(name)))
        {
            return;
        }

        names ??= [];
        if (!names.Contains(name, StringComparer.Ordinal))
        {
            names.Add(name);
        }
    }

    public PlanningStats PlanningStats { get; }

    /// <summary>
    /// How the optimiser ended for this statement, and what it cost (D237,
    /// <c>docs/design/30-planning-options.md</c>).
    /// </summary>
    /// <remarks>
    /// Always present. A prepare that set no planning option gets
    /// <see cref="PlanningTerminationReason.Converged"/> with its elapsed times and no costs,
    /// because nothing was installed to sample one; a prepare that set one gets the evaluation count
    /// and the two costs as well, and <see cref="PlanningState.CostRatio"/> is what the search
    /// bought after its first complete answer.
    /// </remarks>
    public PlanningState PlanningState { get; }

    /// <summary>
    /// The plan this one was narrowed from, when the sidecar still held that plan's converted tree
    /// and started from it; null otherwise (§2.1, D233).
    /// </summary>
    /// <remarks>
    /// Read off the planner's own stage list, which says <c>narrowed from &lt;digest&gt;</c> in place
    /// of the parse, validation and conversion it skipped. It is a statement about how the plan was
    /// <em>built</em> and never about what it is: a narrowing that misses this gets the same plan and
    /// the same digest, and the corpus asserts exactly that.
    /// </remarks>
    public ulong? NarrowedFrom =>
        _narrowedFrom ??= ReadNarrowedFrom(PlanningStats);

    private ulong? _narrowedFrom;

    private const string NarrowedFromStage = "narrowed from ";

    private static ulong? ReadNarrowedFrom(PlanningStats stats)
    {
        foreach (var stage in stats.Stages)
        {
            if (stage.StartsWith(NarrowedFromStage, StringComparison.Ordinal)
                && ulong.TryParse(
                    stage[NarrowedFromStage.Length..],
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var digest))
            {
                return digest;
            }
        }

        return null;
    }

    /// <summary>
    /// Plans this statement again with <paramref name="more"/>'s bindings folded into what this plan
    /// left open — <b>incremental narrowing</b> (§2.1, D233).
    /// </summary>
    /// <param name="more">
    /// What is known now that was not known at prepare: values for names this plan left open, and
    /// shapes for the ones that stay open. A name this plan <em>folded</em> is refused naming it —
    /// it is in the leaves and in the digest — and the host narrows from the plan that left it open.
    /// </param>
    /// <param name="ct">Cancels the planner round trip.</param>
    /// <remarks>
    /// The result is identical to <c>PrepareAsync(sql, union)</c>, plan for plan and digest for
    /// digest, because the request carries that very union; the base plan's digest travels beside it
    /// as a hint, so a sidecar that still holds this plan's converted tree can start from it rather
    /// than parse the statement again. Whether it did is <see cref="NarrowedFrom"/>, and the answer
    /// changes nothing about the plan.
    /// </remarks>
    public async ValueTask<PreparedQuery> NarrowAsync(
        RequestContext more, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(more);
        var union = Context is null ? more : Context.Narrow(more);
        var result = await _engine
            .PlanAsync(_prepared.Sql, _options, union, PlanDigest, ct)
            .ConfigureAwait(false);
        return new PreparedQuery(
            _engine,
            Sql,
            _rewriter,
            _options,
            result,
            await _engine.CompileAsync(result.Plan, _options, union, ct).ConfigureAwait(false),
            _prepareShape,
            _prepared,
            union);
    }

    /// <summary>The SQL the planner actually saw for the prepared shape (D27, V13).</summary>
    public string PlannerSql => _prepared.Sql;

    /// <summary>
    /// True once a table <em>this query reads</em> has changed shape since it was planned (D86,
    /// amended by D260 and made per table by D271 (d)) — after a refresh in which that table gained a
    /// column, lost an index or otherwise stopped being the table this plan was compiled against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Executing a stale query throws <see cref="Chalk.Sources.StalePlanException"/>. This is the
    /// question to ask first, because the answer is actionable: prepare the statement again. A host
    /// that keeps a pool of prepared statements across a schema change checks this rather than
    /// catching the exception on the first row of a result it has already started streaming.
    /// </para>
    /// <para>
    /// A refresh that only replaced rows leaves this false and the query runnable: the compiled plan
    /// holds column positions and source references, and a replacement changes neither. Statistics
    /// may have moved with the rows, so preparing again may well find a better plan — but nothing
    /// forces it (<c>docs/design/35-poco-refresh.md</c> §2).
    /// </para>
    /// <para>
    /// So does a shape change in a table this query does not read: since D271 (d) the comparison is
    /// per table, so a column added to <c>notes</c> strands the queries that read <c>notes</c> and
    /// leaves the ones that read <c>orders</c> exactly where they were.
    /// </para>
    /// </remarks>
    public bool IsStale => !_engine.IsCurrent(Plan, _tablesRead);

    /// <summary>The tables this plan reads, which is what its staleness is measured over.</summary>
    private readonly IReadOnlyList<TableKey>? _tablesRead;

    /// <summary>
    /// Binds values and returns an execution. Asynchronous only because a list length not seen before
    /// needs a planner round trip; the common path completes synchronously.
    /// </summary>
    internal ValueTask<QueryExecution> ExecuteAsync(
        object?[] values, ExecutionArena? arena, CancellationToken ct) =>
        ExecuteAsync(values, context: null, arena, ct);

    /// <summary>
    /// The same, on an arena rented from a host's pool rather than from the engine's (D258).
    /// </summary>
    internal ValueTask<QueryExecution> ExecuteAsync(
        object?[] values, RequestContext? context, ArenaPool pool, CancellationToken ct) =>
        ExecuteAsync(values, context, arena: null, ct, pool);

    /// <summary>
    /// The same, with the values a query prepared under execute-time binding still needs (D209).
    /// </summary>
    internal async ValueTask<QueryExecution> ExecuteAsync(
        object?[] values,
        RequestContext? context,
        ExecutionArena? arena,
        CancellationToken ct,
        ArenaPool? pool = null)
    {
        var bindings = Bindings(context);
        if (bindings is null && RequiredContext.Count > 0)
        {
            throw new ContextRequiredException(RequiredContext);
        }
        
        _beforeExecute?.Invoke(this);

        var shape = ParameterBinder.ShapeOf(Parameters, values);
        var key = Key(shape);

        if (!_shapes.TryGetValue(key, out var plan))
        {
            var rendered = _rewriter.Render(shape);
            var result =
                await _engine.PlanAsync(rendered.Sql, _options, Context, ct).ConfigureAwait(false);
            var compiled = await _engine.CompileAsync(result.Plan, _options, Context, ct).ConfigureAwait(false);
            if (_decorateSchema is { } decorate)
            {
                var decorated = decorate(compiled.OutputSchema);
                if (!ReferenceEquals(decorated, compiled.OutputSchema))
                {
                    compiled = compiled.WithOutputSchema(decorated);
                }
            }

            plan = _shapes.GetOrAdd(key, new ShapePlan(compiled, rendered));
        }

        if (_options.RequireCurrentCatalog)
        {
            _engine.RequireCurrentCatalog(plan.Compiled.Plan, _tablesRead);
        }

        var flat = ParameterBinder.Flatten(
            Parameters, values, plan.Rendered.Slots, plan.Compiled.ParameterTypes);

        return new QueryExecution(
            plan.Compiled,
            WithBoundScalars(flat, plan.Compiled.BoundScalars, bindings),
            BoundRelations(bindings),
            arena,
            pool ?? _engine.Arenas,
            ct,
            _engine.Log);
    }

    /// <summary>
    /// Which context this execution's values come from, checked before anything runs (D209).
    /// </summary>
    /// <remarks>
    /// A query prepared with values carries them and refuses a second set; a query prepared with a
    /// shape has none and needs one of exactly that shape. The shapes must be equal rather than
    /// merely sufficient, because the plan was built for the shape: a name it does not know is a name
    /// nothing reads, and a missing one is a parameter with no value.
    /// </remarks>
    private RequestContext? Bindings(RequestContext? supplied)
    {
        if (Context is null || Context.ShapeNames.Count == 0)
        {
            if (supplied is not null)
            {
                throw new ArgumentException(
                    "this query was prepared with its context values and carries them, so there is "
                    + "nothing to bind at execution. Prepare with context.Shape() — or with "
                    + "context.Shape(names) for the names that stay open — to bind the values here "
                    + "instead (docs/design/16-entitlements.md §2, §2.1, D209, D232).",
                    nameof(supplied));
            }

            return Context;
        }

        if (supplied is null)
        {
            // What the plan reads of the open half, or — where it reads none of it — the names the
            // prepare said would be bound here, so the message never comes back empty-handed.
            throw new ContextRequiredException(
                RequiredContext.Count > 0 ? RequiredContext : Context.ShapeNames);
        }

        if (supplied.ShapeNames.Count > 0)
        {
            throw new ArgumentException(
                "this query needs the context's values at execution and was handed "
                + string.Join(", ", supplied.ShapeNames)
                + " as shapes again. Bind the values "
                + "(docs/design/16-entitlements.md §2, §2.1, D209, D232).",
                nameof(supplied));
        }

        if (!string.Equals(supplied.ShapeSignature, Context.ShapeSignature, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "this query was planned for a different context shape: the names, kinds and types "
                + "bound at execution must be the ones it was prepared with, since the plan was "
                + "built for them. Prepare again for this shape "
                + "(docs/design/16-entitlements.md §2, D209).",
                nameof(supplied));
        }

        // Partial binding: what the plan folded is *in* the plan, so an execution that bound
        // other values for those names would be handed the prepared principal's rows under its own
        // name. The folded half must be the one it was planned for; the open half is this execution's,
        // which is the whole point of leaving it open.
        if (_folded.Count > 0
            && !string.Equals(
                supplied.ValueHash(_folded), _foldedHash ??= Context.ValueHash(_folded),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "this query folded " + string.Join(", ", _folded) + " into its plan at prepare, and "
                + "this execution binds other values for them. A folded value is in the leaf and in "
                + "the digest: executing with another one would answer for the principal the plan "
                + "was built for. Prepare again for these values, or narrow from the base. ",
                nameof(supplied));
        }

        return supplied;
    }

    private readonly HashSet<string> _folded = new(StringComparer.Ordinal);
    private string? _foldedHash;

    /// <summary>
    /// The statement's own values, then this plan's bound scalars in slot order — one array, which is
    /// what makes a context scalar an ordinary parameter to everything below (D209).
    /// </summary>
    private static object?[] WithBoundScalars(
        object?[] values, IReadOnlyList<BoundScalar> bound, RequestContext? context)
    {
        if (bound.Count == 0)
        {
            return values;
        }

        var all = new object?[values.Length + bound.Count];
        System.Array.Copy(values, all, values.Length);
        List<string>? missing = null;
        for (var i = 0; i < bound.Count; i++)
        {
            if (context is not null && context.Scalars.TryGetValue(bound[i].Name, out var value))
            {
                all[values.Length + i] = value;
                continue;
            }

            missing ??= [];
            missing.Add(bound[i].Name);
        }

        return missing is null ? all : throw new ContextRequiredException(missing);
    }

    /// <summary>
    /// The rows of every relation this query's context bound, by name — what a <c>ContextTable</c>
    /// materialises from at execution (16-entitlements.md §2, §4).
    /// </summary>
    /// <remarks>
    /// Lists and relations are one namespace here on purpose: a list the planner would not fold
    /// becomes a <c>ContextTable</c> exactly as a relation does, and by the time a plan names one the
    /// distinction has done its work. Built once per prepared query, and not at all for a statement
    /// that binds nothing.
    /// </remarks>
    private IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<object?>>>? BoundRelations(
        RequestContext? context)
    {
        if (context is null || (context.Lists.Count == 0 && context.Relations.Count == 0))
        {
            return null;
        }

        // Cached only for a query that carries its own context: under execute-time binding the rows
        // are this execution's, and two principals share the plan and nothing else.
        var cacheable = ReferenceEquals(context, Context);
        if (cacheable && _boundRelations is not null)
        {
            return _boundRelations;
        }

        var bound = new Dictionary<string, IReadOnlyList<IReadOnlyList<object?>>>(
            context.Lists.Count + context.Relations.Count, StringComparer.Ordinal);
        foreach (var (name, relation) in context.Lists)
        {
            bound[name] = relation.Rows;
        }

        foreach (var (name, relation) in context.Relations)
        {
            bound[name] = relation.Rows;
        }

        return cacheable ? _boundRelations = bound : bound;
    }

    private IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<object?>>>? _boundRelations;

    /// <summary>
    /// The compiled plan for the no-parameter shape, for the benchmark's pipeline-only measurement
    /// (<c>15-zero-allocation-execution.md</c> §5). Not a host-facing path: it reaches the engine's
    /// own batches, which are reused slots rather than anything a caller may keep.
    /// </summary>
    internal async ValueTask<CompiledPlan> CompiledAsync(CancellationToken ct)
    {
        if (Parameters.Count != 0)
        {
            throw new ArgumentException(
                $"'{Sql}' has {Parameters.Count} parameter(s); the compiled plan is only reachable "
                + "for a statement with none, because a shape has to be bound to choose one.");
        }

        await using var execution = await ExecuteAsync([], arena: null, ct).ConfigureAwait(false);
        return _shapes[Key(ParameterBinder.ShapeOf(Parameters, []))].Compiled;
    }

    /// <summary>
    /// Fills in each logical parameter's type from the placeholder it renders to — the element type
    /// when the parameter takes a list, because the placeholder sits inside the expanded IN list.
    /// </summary>
    private void AssignParameterTypes(RenderedStatement rendered, IReadOnlyList<ChalkType> types)
    {
        for (var i = 0; i < rendered.Slots.Count && i < types.Count; i++)
        {
            var parameter = Parameters[rendered.Slots[i].ParameterIndex];
            if (parameter.Type == default)
            {
                parameter.Type = types[i];
            }
        }
    }

    /// <summary>
    /// The cache key for one binding shape. A statement with no list parameter has one shape for
    /// ever, so it gets a constant rather than a string built per execution (§5's fixed-cost gate).
    /// </summary>
    private static string Key(IReadOnlyList<int> shape) =>
        shape.Count == 0 ? string.Empty : string.Join(',', shape);

    private sealed record ShapePlan(CompiledPlan Compiled, RenderedStatement Rendered);
}

/// <summary>One output column of a prepared statement.</summary>
public sealed class PreparedColumn
{
    /// <summary>The column's name, as the statement asked for it.</summary>
    public required string Name { get; init; }
}

/// <summary>
/// One run of a prepared statement. Single-use: enumerate <see cref="Batches"/> once, and dispose
/// each batch when you are done with it.
/// </summary>
public sealed class QueryExecution : IAsyncDisposable
{
    private readonly CompiledPlan _compiled;
    private readonly object?[] _parameters;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<object?>>>? _relations;
    private readonly ExecutionArena? _hostArena;
    private readonly ArenaPool _pool;
    private readonly CancellationToken _ct;
    private readonly Microsoft.Extensions.Logging.ILogger _log;
    private int _enumerated;

    internal QueryExecution(
        CompiledPlan compiled,
        object?[] parameters,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<object?>>>? relations,
        ExecutionArena? hostArena,
        ArenaPool pool,
        CancellationToken ct,
        Microsoft.Extensions.Logging.ILogger log)
    {
        _compiled = compiled;
        _parameters = parameters;
        _relations = relations;
        _hostArena = hostArena;
        _pool = pool;
        _ct = ct;
        _log = log;
        Schema = compiled.OutputSchema;
    }

    /// <summary>The schema every batch matches — names, types, nullability and order.</summary>
    public ArrowSchema Schema { get; }

    /// <summary>
    /// The plan this execution actually runs. Usually the prepared statement's own, but a statement
    /// with a list parameter is planned per list length (D29), so it is the executed plan — not the
    /// prepared one — whose collations and node kinds describe these rows.
    /// </summary>
    public Plan Plan => _compiled.Plan;

    /// <summary>Live counters; final once enumeration completes or faults.</summary>
    public ExecutionStats Stats { get; } = new();

    /// <summary>
    /// The result. Nothing runs until this is enumerated, and it may be enumerated only once; the
    /// batches are the caller's to dispose.
    /// </summary>
    public IAsyncEnumerable<RecordBatch> Batches => Enumerate();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async IAsyncEnumerable<RecordBatch> Enumerate()
    {
        if (Interlocked.Exchange(ref _enumerated, 1) != 0)
        {
            throw new InvalidOperationException(
                "a QueryExecution is single-use; prepare once and execute again for another run");
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        // A host-supplied arena is the host's to dispose; one rented from a pool — the engine's or
        // the host's — goes back warm, and one taken beyond the pool's capacity is released here
        // (08-execution-arena.md §2.1, D258).
        var (arena, transient) = _hostArena is { } supplied ? (supplied, false) : _pool.Rent();
        if (_hostArena is null)
        {
            Stats.RecordArenaPool(_pool.Name);
        }
        try
        {
            await foreach (var batch in _compiled
                .ExecuteAsync(_parameters, Stats, _relations, arena, _ct)
                .ConfigureAwait(false))
            {
                yield return batch;
            }
        }
        finally
        {
            if (_hostArena is null)
            {
                _pool.Return(arena, transient);
            }

            Stats.Elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);

            // One structured event per execution (04-client.md §11). The counters come first
            // because they are the ones that stay comparable when the hardware changes.
            Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(
                _log,
                "executed {Digest:x16}: {RowsProduced} rows produced, {RowsScanned} scanned, "
                + "{BatchesProduced} batches, {PeakPooledBytes} peak pooled bytes, {ElapsedMs}ms",
                _compiled.Plan.PlanDigest,
                Stats.RowsProduced,
                Stats.RowsScanned,
                Stats.BatchesProduced,
                Stats.PeakPooledBytes,
                Stats.Elapsed.TotalMilliseconds);
        }
    }
}
