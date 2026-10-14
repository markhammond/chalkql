using System.Collections.Concurrent;
using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Client.Rpc;
using Chalk.Execution;
using Chalk.Ir;
using Chalk.Sources;
using Microsoft.Extensions.Logging;
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
    private readonly RenderedStatement _prepared;

    /// <summary>
    /// Everything that belongs to the plan this statement runs, replaced whole when a plan made in the
    /// background swaps in (design 69 §4): an execution reads it once, so it runs one plan throughout.
    /// </summary>
    private PlanState _state;

    /// <summary>
    /// This statement's hints, kept so a narrowing plans under the same ones (D284, ADR 0074). Not
    /// exposed: <see cref="HintedParameters"/> is all a caller gets back.
    /// </summary>
    private readonly IReadOnlyList<ParameterValueHint> _hints;

    internal PreparedQuery(
        ChalkEngine engine,
        string sql,
        ParameterRewriter rewriter,
        PrepareOptions options,
        PlanResult result,
        CompiledPlan compiled,
        IReadOnlyList<int> prepareShape,
        RenderedStatement rendered,
        RequestContext? context,
        IReadOnlyList<ParameterValueHint> hints)
    {
        // The ordinals and nothing else (D284). A hint informs an estimate; it is not part of this
        // query and no execution is held to it, so there is nothing else worth keeping — and keeping
        // the values would put them somewhere a caller could read them back out.
        HintedParameters = [.. hints.Select(h => h.Ordinal)];
        _hints = hints;
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
        Sql = sql;
        LogicalDigest = result.LogicalDigest;
        // When the prepare asked for a redaction and the statement's parameters are named, the
        // names go back where the rewrite put a `?` — in the statement and in the redacted text
        // (D287). Anything else is served as it came.
        var names = result.RedactedSql is null ? null : rewriter.SlotNames(rendered);
        RedactedSql =
            names is null || result.RedactedSql is null
                ? result.RedactedSql
                : ParameterNames.Substitute(result.RedactedSql, names, indexed: false);

        Columns = Names(compiled.OutputSchema);
        Extensions = result.Extensions;

        OutputSchema = compiled.OutputSchema;
        ParameterTypes = compiled.ParameterTypes;

        _prepared = rendered;
        _prepareShape = prepareShape;
        _state = PlanState.Of(engine, result, compiled, Key(prepareShape), rendered, names);
        AssignParameterTypes(rendered, compiled.ParameterTypes);
        engine.Track(this);
    }

    /// <summary>The SQL as the host wrote it, parameter styles and all.</summary>
    public string Sql { get; }

    /// <summary>The engine this statement was prepared on (D341: a view made from it must be that engine's).</summary>
    internal ChalkEngine Engine => _engine;

    /// <summary>
    /// The options this statement was prepared with: what a view made from it prepares its text with
    /// again after a shape change, and the cap on local joins it was planned under (D341).
    /// </summary>
    internal PrepareOptions Options => _options;

    /// <summary>
    /// Which of this statement's placeholders were planned against a value the caller expected
    /// (D284), in order. The ordinals only: nothing of a hint's value is retained.
    /// </summary>
    public IReadOnlyList<int> HintedParameters { get; }

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
    /// Lets one extension decorate the schema every batch of this query carries, hear about each
    /// execution before it runs (step 26c, D213), and check each plan the query takes after its
    /// prepare. The one seam the core keeps for a feature it does not itself know: today the
    /// entitlement wrapper puts <c>chalk.disclosure</c> on the fields, raises its audit event and
    /// checks plans against its report through it, and nothing in this package names any of them.
    /// </summary>
    /// <param name="decorateSchema">What every batch's schema becomes.</param>
    /// <param name="beforeExecute">
    /// Heard before each execution, with the context that execution runs with: the one it binds, for
    /// a query prepared with shapes, or the one it was prepared with, for a query that folded its
    /// values (F166). Never the prepare's shape, which holds no values and belongs to no request.
    /// </param>
    /// <param name="checkPlan">
    /// Shown each plan the query takes after its prepare, before it is used: one for another length of
    /// a list parameter, at its first execution, and one a background run made (design 69 §4). It
    /// throws to refuse one: the execution is then refused, or the background plan is not taken. A
    /// query decorated without one is never planned again in the background.
    /// </param>
    internal void Decorate(
        Func<ArrowSchema, ArrowSchema>? decorateSchema,
        Action<PreparedQuery, RequestContext?>? beforeExecute,
        Action<PlanResult>? checkPlan = null)
    {
        _decorateSchema = decorateSchema;
        _beforeExecute = beforeExecute;
        _checkPlan = checkPlan;
        if (decorateSchema is null)
        {
            return;
        }

        var state = Volatile.Read(ref _state);
        var decorated = decorateSchema(state.Compiled.OutputSchema);
        if (ReferenceEquals(decorated, state.Compiled.OutputSchema))
        {
            return;
        }

        var compiled = state.Compiled.WithOutputSchema(decorated);
        OutputSchema = compiled.OutputSchema;
        Volatile.Write(ref _state, state.With(compiled, Key(_prepareShape), _prepared));
    }

    private Func<ArrowSchema, ArrowSchema>? _decorateSchema;
    private Action<PreparedQuery, RequestContext?>? _beforeExecute;
    private Action<PlanResult>? _checkPlan;
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
    /// <remarks>
    /// The one a refresh last swapped in, under deferred planning (design 69 §4); otherwise the one
    /// it was prepared with.
    /// </remarks>
    public Plan Plan => Volatile.Read(ref _state).Plan;

    public ulong PlanDigest => Plan.PlanDigest;

    /// <summary>
    /// The statement's identity, which statistics do not move (D342): a digest of its logical plan,
    /// before the planner chose anything by cost.
    /// </summary>
    /// <remarks>
    /// The same for a statement however it is spaced or its keywords cased; different for other
    /// values, aliases, windows, tables or policy, as <see cref="PlanDigest"/> is. Unlike
    /// <see cref="PlanDigest"/>, the identity of the plan that runs, it does not move when new
    /// statistics change the plan's operators, so it is what to name shared work by. Zero when the
    /// plan came from a planner that computes none, such as <see cref="RecordedPlanner"/>.
    /// </remarks>
    public ulong LogicalDigest { get; }

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
    public string? PlanText => Volatile.Read(ref _state).PlanText;

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
            if (rel.KindCase == Rel.KindOneofCase.BoundTable)
            {
                Require(rel.BoundTable.Name, context, ref names);
                continue;
            }

            // A membership over a context list reads the list by name, as a bound relation does, and
            // is answered here from what the execution binds (F161).
            foreach (var expr in PlanWalker.OwnExprs(rel).SelectMany(PlanWalker.Exprs))
            {
                if (expr.KindCase == Expr.KindOneofCase.ContextMembership)
                {
                    Require(expr.ContextMembership.List, context, ref names);
                }
            }
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

    public PlanningStats PlanningStats => Volatile.Read(ref _state).Stats;

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
    public PlanningState PlanningState => Volatile.Read(ref _state).PlanningState;

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
    public ulong? NarrowedFrom => ReadNarrowedFrom(PlanningStats);

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

        // The names this narrowing binds are checked as a prepare's are; the rest were, when this
        // query was prepared, and are not converted twice (D317, D323).
        var union = Context is null
            ? more.Bound(_engine.Binding)
            : Context.Narrow(more).Bound(_engine.Binding, more.Names().ToHashSet(StringComparer.Ordinal));
        var result = await _engine
            .PlanAsync(_prepared.Sql, _options, union, PlanDigest, _hints, ct)
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
            union,
            _hints);
    }

    /// <summary>The SQL the planner actually saw for the prepared shape (D27, V13).</summary>
    public string PlannerSql => _prepared.Sql;

    /// <summary>
    /// True once a table <em>this query reads</em> has changed shape since it was planned (D86,
    /// amended by D260 and made per table by D271 (d)) — after a refresh in which that table gained a
    /// column, lost an index or otherwise stopped being the table this plan was compiled against — or
    /// its row count has drifted tenfold, either way (D342).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Executing a query whose table changed shape throws <see cref="Chalk.Sources.StalePlanException"/>.
    /// This is the question to ask first, because the answer is actionable: prepare the statement
    /// again. A host that keeps a pool of prepared statements across a schema change checks this rather
    /// than catching the exception on the first row of a result it has already started streaming.
    /// </para>
    /// <para>
    /// A refresh that only replaced rows leaves the query runnable: the compiled plan holds column
    /// positions and source references, and a replacement changes neither
    /// (<c>docs/design/35-poco-refresh.md</c> §2). But a plan made on other numbers can be a poor one
    /// — one made on empty tables scans and loops where one made on loaded tables seeks and hashes —
    /// so this is also true once a table the query reads has drifted tenfold, either way, since it was
    /// planned (D342). A table whose row count is unknown is not judged. The query still runs, and runs
    /// correctly; preparing it again finds the plan for the numbers there are now. Under
    /// <c>DeferPlanning()</c> a background run plans it again in place instead, and
    /// <see cref="ChangedShape"/> tells the two halves apart.
    /// </para>
    /// <para>
    /// So does a shape change in a table this query does not read: since D271 (d) the comparison is
    /// per table, so a column added to <c>notes</c> strands the queries that read <c>notes</c> and
    /// leaves the ones that read <c>orders</c> exactly where they were.
    /// </para>
    /// </remarks>
    public bool IsStale
    {
        get
        {
            var state = Volatile.Read(ref _state);
            return !_engine.IsCurrent(state.Plan, state.TablesRead) || Drifted(state, _engine.Catalog, Drift);
        }
    }

    /// <summary>How far a table's row count may move, either way, before the plan made on it is stale.</summary>
    private const double Drift = 10;

    /// <summary>
    /// How far a table's row count may move, either way, before a background run plans the statement
    /// again (design 69 §4): sooner than <see cref="Drift"/> calls it stale.
    /// </summary>
    internal const double PlanAgainDrift = 2;

    /// <summary>
    /// Whether a table the plan reads has drifted by <paramref name="factor"/>, either way, between the
    /// numbers it was made on and <paramref name="live"/> (D342, F196). A table whose count is unknown,
    /// then or now, is not judged. One comparison per table, and no planner round trip.
    /// </summary>
    private static bool Drifted(PlanState state, Chalk.Catalog.CatalogContext live, double factor)
    {
        if (state.PlannedRows.Length == 0)
        {
            return false;
        }

        foreach (var (table, planned) in state.PlannedRows)
        {
            if (RowsOf(live, table) is not { } now)
            {
                continue;
            }

            // Counted from one, so that an empty table is a table of one row: nothing is divided by
            // zero, and a table that grew from empty to a few rows has not drifted.
            var ratio = (Math.Max(now, 1) + 0d) / Math.Max(planned, 1);
            if (ratio >= factor || ratio <= 1 / factor)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a background run may plan this statement again (design 69 §4): not when it was
    /// prepared with a planner extension that checked the plan it was handed and does not check the
    /// plans it takes later, nor when its plan carries no logical digest to prove the new one the same
    /// statement.
    /// </summary>
    internal bool MayPlanAgain =>
        LogicalDigest != 0
        && (_checkPlan is not null
            || (_options.Extensions.Count == 0 && _decorateSchema is null && _beforeExecute is null))
        && !_notTheSame
        && _engine.IsCurrent(Volatile.Read(ref _state).Plan, Volatile.Read(ref _state).TablesRead);

    /// <summary>
    /// A background run planned this statement again and got another statement back, which it will
    /// not swap in; it is not asked again. A shape change makes the statement stale instead, and the
    /// host prepares it again, as it always has.
    /// </summary>
    private bool _notTheSame;

    /// <summary>
    /// Whether a table this statement's plan reads has moved by <see cref="PlanAgainDrift"/> between
    /// the numbers the plan was made on and <paramref name="catalog"/>'s.
    /// </summary>
    internal bool DriftedFrom(Chalk.Catalog.CatalogContext catalog) =>
        Drifted(Volatile.Read(ref _state), catalog, PlanAgainDrift);

    /// <summary>
    /// The same, judged from the numbers <paramref name="waiting"/> was made on: a plan a background
    /// run made for this statement, which no refresh has swapped in yet.
    /// </summary>
    internal static bool DriftedFrom(PlanState waiting, Chalk.Catalog.CatalogContext catalog) =>
        Drifted(waiting, catalog, PlanAgainDrift);

    /// <summary>
    /// This statement planned and compiled again, on the numbers the planner holds now (design 69
    /// §4), ready to swap in, and what the planning came to: no plan when the planner came to the one
    /// the statement runs, held back one that saves less than its view's margin (design 70 §3), or
    /// sent another statement — another logical plan, or other parameters or context to bind. Every
    /// plan of a statement answers the same rows, so the one that comes back differs only in what it
    /// costs.
    /// </summary>
    internal async ValueTask<PlanAgain> PlanAgainAsync(CancellationToken ct)
    {
        var current = Volatile.Read(ref _state);
        var margin = Choice?.ImprovementMargin;
        var result = await _engine
            .PlanAgainAsync(
                _prepared.Sql,
                _options,
                Context,
                _hints,
                current.Plan.PlanDigest,
                pricePlans: margin is not null,
                cheaperBy: margin,
                ct)
            .ConfigureAwait(false);
        if (result.Unchanged || result.Held || result.Plan.PlanDigest == current.Plan.PlanDigest)
        {
            // The planner came to the plan this statement runs, on the numbers it holds now, or to one
            // that does not save the margin a view set on it (design 70 §3): nothing to compile or
            // swap in, so a view kept from the plan keeps on, and drift is judged from these numbers
            // from now on.
            if (result.Held)
            {
                _engine.PlanningLog.LogDebug(
                    "the planner held back a plan of {Digest:x16} that costs {Next} against {Held} for the plan it runs",
                    LogicalDigest,
                    result.PlanCost,
                    result.KnownPlanCost);
            }

            current.Rebase(PlannedRows(result.PlannedOn ?? _engine.Catalog, current.TablesRead));
            return result.Held
                ? new PlanAgain(null, PlanAgainOutcome.Held, 0, result.PlanCost, result.KnownPlanCost)
                : new PlanAgain(null, PlanAgainOutcome.Unchanged, current.Plan.PlanDigest, result.PlanCost, result.KnownPlanCost);
        }

        if (result.LogicalDigest != LogicalDigest
            || !RequiredNames(result.Plan, Context).SequenceEqual(RequiredContext, StringComparer.Ordinal))
        {
            _notTheSame = true;
            return new PlanAgain(null, PlanAgainOutcome.NotTheSame, result.Plan.PlanDigest, result.PlanCost, result.KnownPlanCost);
        }

        // The extension's check, as at prepare; a refusal keeps the plan this statement has, and a
        // later run, on other numbers, may bring one it takes.
        _checkPlan?.Invoke(result);

        var compiled = await _engine.CompileAsync(result.Plan, _options, Context, ct).ConfigureAwait(false);
        if (!compiled.ParameterTypes.SequenceEqual(current.Compiled.ParameterTypes))
        {
            _notTheSame = true;
            return new PlanAgain(null, PlanAgainOutcome.NotTheSame, result.Plan.PlanDigest, result.PlanCost, result.KnownPlanCost);
        }

        if (_decorateSchema is { } decorate)
        {
            var decorated = decorate(compiled.OutputSchema);
            if (!ReferenceEquals(decorated, compiled.OutputSchema))
            {
                compiled = compiled.WithOutputSchema(decorated);
            }
        }

        // Under a margin, what the planner priced the plan it sent at, beside the plan it replaces: or
        // that it no longer kept that plan, so no margin applied.
        if (margin is not null)
        {
            if (result.KnownPlanCost is { } held)
            {
                _engine.PlanningLog.LogDebug(
                    "the planner sent a plan of {Digest:x16} that costs {Next} against {Held} for the plan it runs",
                    LogicalDigest,
                    result.PlanCost,
                    held);
            }
            else
            {
                _engine.PlanningLog.LogDebug(
                    "the planner sent a plan of {Digest:x16} that costs {Next}; it no longer keeps the plan it runs, so no margin applied",
                    LogicalDigest,
                    result.PlanCost);
            }
        }

        var names = result.RedactedSql is null ? null : _rewriter.SlotNames(_prepared);
        var next = PlanState.Of(_engine, result, compiled, Key(_prepareShape), _prepared, names);
        return new PlanAgain(next, PlanAgainOutcome.New, result.Plan.PlanDigest, result.PlanCost, result.KnownPlanCost);
    }

    /// <summary>
    /// What planning a statement again came to: the new plan, when there is one; the planner's word;
    /// the digest of the plan it came to, zero when none came back; and both plans' costs where a
    /// margin had them priced.
    /// </summary>
    internal readonly record struct PlanAgain(
        PlanState? Next, PlanAgainOutcome Outcome, ulong NewPlan, double? NewCost, double? RunningCost);

    /// <summary>What planning a statement again came to (design 69 §4, design 70 §3).</summary>
    internal enum PlanAgainOutcome
    {
        /// <summary>Another plan, ready to swap in.</summary>
        New,

        /// <summary>The plan the statement runs: drift is judged from the new numbers from now on.</summary>
        Unchanged,

        /// <summary>Another plan that does not save its view's margin, which the planner held back.</summary>
        Held,

        /// <summary>Another statement, neither swapped in nor planned again.</summary>
        NotTheSame,
    }

    /// <summary>
    /// What a standing view asks of the plans its statement takes after the view is made (design 70).
    /// Null — every statement but a view's whose host set it — requests a plan whenever the numbers
    /// drift, takes every plan, and asks for no prices.
    /// </summary>
    internal PlanChoice? Choice { get; set; }

    /// <summary>A view's say on its statement's plans (design 70).</summary>
    /// <param name="RefreshQueryPlan">
    /// The host's veto on requesting a new plan for drift (§1): asked before one is requested, and
    /// false leaves the statement on its plan for now. Null requests one whenever the numbers drift.
    /// </param>
    /// <param name="ImprovementMargin">
    /// The fraction of the held plan's cost a new plan must save, both on the new plan's numbers (§3):
    /// the planner holds back one that saves less, and sends none. Null takes every plan.
    /// </param>
    internal sealed record PlanChoice(Func<bool>? RefreshQueryPlan, double? ImprovementMargin);

    /// <summary>
    /// Asks the planner to keep the plan this statement runs, so that the next background run can price
    /// it on newer numbers (design 70 §3): what a view with a margin does when it puts its say on the
    /// statement. A plan that comes back other than the one held, on numbers that moved since it was
    /// made, is not taken here: the next background run plans it again, as it would have, and since the
    /// plan held was not kept, no margin holds that run's plan back.
    /// </summary>
    internal async ValueTask KeepPlanAsync(CancellationToken ct)
    {
        var current = Volatile.Read(ref _state);
        await _engine
            .PlanAgainAsync(
                _prepared.Sql, _options, Context, _hints, current.Plan.PlanDigest, pricePlans: true, cheaperBy: null, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a new plan may be requested for this statement now, for drift (design 70 §1): its view's
    /// host says, and a host's callback that throws says not now, logged. True when no host says.
    /// </summary>
    internal bool MayRefreshPlan()
    {
        if (Choice?.RefreshQueryPlan is not { } refresh)
        {
            return true;
        }

        try
        {
            return refresh();
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            _engine.PlanningLog.LogWarning(
                failure,
                "a view's RefreshQueryPlan threw for statement {Digest:x16}; no plan is requested for it now",
                LogicalDigest);
            return false;
        }
    }

    /// <summary>
    /// Whether a table this statement reads has changed shape since it was planned: the half of
    /// <see cref="IsStale"/> after which the statement no longer runs, throwing
    /// <see cref="Chalk.Sources.StalePlanException"/>, and must be prepared again.
    /// </summary>
    /// <remarks>
    /// The other half, a tenfold drift, leaves the statement running correctly on a plan made for other
    /// numbers. Under <c>DeferPlanning()</c> a background run plans such a statement again in place, so
    /// a host prepares it again only when this is true. Without deferred planning nothing plans it
    /// again, and a host prepares it again on <see cref="IsStale"/>, as before.
    /// </remarks>
    public bool ChangedShape
    {
        get
        {
            var state = Volatile.Read(ref _state);
            return !_engine.IsCurrent(state.Plan, state.TablesRead);
        }
    }

    /// <summary>
    /// Runs <paramref name="next"/> from now on, when it is still current: a refresh may have changed
    /// a table it reads since it was planned, and then neither plan runs. Called under the refresh
    /// lock, at the refresh that takes a background run's work in. An execution already running keeps
    /// the plan it read when it began.
    /// </summary>
    internal bool Adopt(PlanState next)
    {
        if (!_engine.IsCurrent(next.Plan, next.TablesRead))
        {
            return false;
        }

        // The plan this statement runs, planned again on newer numbers: nothing to swap in, so a view
        // kept from it keeps on, and drift is judged from the newer numbers.
        var current = Volatile.Read(ref _state);
        if (current.Plan.PlanDigest == next.Plan.PlanDigest)
        {
            current.Rebase(next.PlannedRows);
            return false;
        }

        Volatile.Write(ref _state, next);
        return true;
    }

    /// <summary>The known row counts of <paramref name="tables"/> in <paramref name="catalog"/>.</summary>
    private static (TableKey Table, long Rows)[] PlannedRows(
        Chalk.Catalog.CatalogContext catalog, IReadOnlyList<TableKey>? tables)
    {
        if (tables is null || tables.Count == 0)
        {
            return [];
        }

        var found = new List<(TableKey Table, long Rows)>(tables.Count);
        foreach (var table in tables)
        {
            if (RowsOf(catalog, table) is { } rows)
            {
                found.Add((table, rows));
            }
        }

        return [.. found];
    }

    /// <summary>
    /// A table's row count in a catalog, or null when the catalog does not know it or no longer
    /// declares the table. Found as the engine finds a table it checks for shape.
    /// </summary>
    private static long? RowsOf(Chalk.Catalog.CatalogContext catalog, TableKey table)
    {
        // Indexed rather than enumerated, so that the drift check adds no allocation to IsStale.
        var schemas = catalog.Schemas;
        for (var i = 0; i < schemas.Count; i++)
        {
            var schema = schemas[i];
            if (!string.Equals(schema.SourceId, table.SourceId, StringComparison.Ordinal)
                || !string.Equals(schema.Name, table.Schema, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tables = schema.Tables;
            for (var j = 0; j < tables.Count; j++)
            {
                if (string.Equals(tables[j].Name, table.Table, StringComparison.OrdinalIgnoreCase))
                {
                    return tables[j].RowCount >= 0 ? tables[j].RowCount : null;
                }
            }
        }

        return null;
    }

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
        ArenaPool? pool = null,
        int clockParameter = -1)
    {
        // An execution's own context is checked as a prepare's is, once, here (D317, D323).
        var bindings = Bindings(context?.Bound(_engine.Binding));
        if (bindings is null && RequiredContext.Count > 0)
        {
            throw new ContextRequiredException(RequiredContext);
        }
        
        _beforeExecute?.Invoke(this, bindings);

        // One read: this execution runs the plan it finds here, whatever swaps in while it runs.
        var state = Volatile.Read(ref _state);
        var plan = await ShapePlanAsync(state, values, ct).ConfigureAwait(false);

        if (_options.RequireCurrentCatalog)
        {
            _engine.RequireCurrentCatalog(plan.Compiled.Plan, state.TablesRead);
        }

        // On an engine that names a clock, a query that reads the time reads the clock, which must be
        // set; the parameter named after it, left unbound, is the clock too (D341).
        if (_engine.ClockUnset && (plan.Compiled.ReadsCurrentTime || clockParameter >= 0))
        {
            throw new ClockRequiredException(_engine.ClockName!);
        }

        var flat = ParameterBinder.Flatten(
            Parameters, values, plan.Rendered.Slots, plan.Compiled.ParameterTypes, _engine.Binding);

        var execution = new QueryExecution(
            plan.Compiled,
            WithBoundScalars(flat, plan.Compiled.BoundScalars, bindings),
            BoundRelations(bindings),
            arena,
            pool ?? _engine.Arenas,
            ct,
            _engine.Log);
        if (clockParameter >= 0)
        {
            execution.UseClockSlots(ClockSlots(plan.Rendered.Slots, clockParameter));
        }

        return execution;
    }

    /// <summary>
    /// The plan for the shape of <paramref name="values"/>: a list parameter's length, say, which the
    /// statement is planned for once and then keeps.
    /// </summary>
    private async ValueTask<ShapePlan> ShapePlanAsync(PlanState state, object?[] values, CancellationToken ct)
    {
        var shape = ParameterBinder.ShapeOf(Parameters, values);
        var key = Key(shape);
        if (state.Shapes.TryGetValue(key, out var plan))
        {
            return plan;
        }

        var rendered = _rewriter.Render(shape);
        var result = await _engine.PlanAsync(rendered.Sql, _options, Context, ct).ConfigureAwait(false);

        // A plan the prepare never saw is checked as the prepare's was, and refuses the execution
        // when it fails.
        _checkPlan?.Invoke(result);
        var compiled = await _engine.CompileAsync(result.Plan, _options, Context, ct).ConfigureAwait(false);
        if (_decorateSchema is { } decorate)
        {
            var decorated = decorate(compiled.OutputSchema);
            if (!ReferenceEquals(decorated, compiled.OutputSchema))
            {
                compiled = compiled.WithOutputSchema(decorated);
            }
        }

        return state.Shapes.GetOrAdd(key, new ShapePlan(compiled, rendered));
    }

    /// <summary>
    /// This statement kept by incremental maintenance for a standing view (D343,
    /// <c>docs/design/68-incremental-maintenance.md</c>), its parameters of the shape of
    /// <paramref name="values"/>, the parameter at <paramref name="clockParameter"/> (or none, -1) the
    /// engine's clock, and its result keyed by the output columns <paramref name="key"/>. A statement
    /// that cannot be maintained is refused with <see cref="NotMaintainableException"/>, naming why.
    /// </summary>
    internal async ValueTask<MaintainedQuery> MaintainAsync(
        object?[] values, int clockParameter, int[] key, CancellationToken ct)
    {
        if (RequiredContext.Count > 0)
        {
            throw new NotMaintainableException(
                "the statement reads a request's context, which a maintained view does not bind.", "root");
        }

        var state = Volatile.Read(ref _state);
        var plan = await ShapePlanAsync(state, values, ct).ConfigureAwait(false);
        var clockSlots = clockParameter >= 0 ? ClockSlots(plan.Rendered.Slots, clockParameter) : [];
        Chalk.Execution.Maintenance.MaintainedPlan maintained;
        try
        {
            maintained = Chalk.Execution.Maintenance.MaintainedPlan.Create(plan.Compiled, clockSlots);
        }
        catch (Chalk.Execution.Maintenance.NotMaintainedException refusal)
        {
            throw new NotMaintainableException(refusal.Message, refusal.Path ?? "root");
        }

        maintained.Key = key;
        return new MaintainedQuery(this, maintained, plan.Rendered.Slots, plan.Compiled.ParameterTypes, state);
    }

    /// <summary>
    /// The plans this statement runs, as an object that is another once plans the background made
    /// swap in (design 69 §4): what a maintained query compares the plans it was made from with.
    /// </summary>
    internal object Plans => Volatile.Read(ref _state);

    /// <summary>The values a maintained update binds, flattened as an execution's are.</summary>
    internal object?[] Flatten(object?[] values, IReadOnlyList<PlaceholderSlot> slots, IReadOnlyList<ChalkType> types) =>
        ParameterBinder.Flatten(Parameters, values, slots, types, _engine.Binding);

    /// <summary>The placeholders the parameter at <paramref name="parameter"/> fills: one for each time the text names it.</summary>
    private static int[] ClockSlots(IReadOnlyList<PlaceholderSlot> slots, int parameter)
    {
        var found = new List<int>();
        for (var i = 0; i < slots.Count; i++)
        {
            if (slots[i].ParameterIndex == parameter)
            {
                found.Add(i);
            }
        }

        return [.. found];
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
                    + "instead.",
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
                + " as shapes again. Bind the values.",
                nameof(supplied));
        }

        if (!string.Equals(supplied.ShapeSignature, Context.ShapeSignature, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "this query was planned for a different context shape: the names, kinds and types "
                + "bound at execution must be the ones it was prepared with, since the plan was "
                + "built for them. Prepare again for this shape.",
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
    /// what makes a context scalar an ordinary parameter to everything below (D209). Each is held to
    /// the exact rule against the type the plan declares for it, named as the host named it (D317);
    /// the context's converters ran when it was handed over.
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
                all[values.Length + i] = ValueBinding.None.Bind(
                    value, ChalkType.FromProto(bound[i].Type), $"Context scalar '{bound[i].Name}'");
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
        return Volatile.Read(ref _state).Shapes[Key(ParameterBinder.ShapeOf(Parameters, []))].Compiled;
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

    internal sealed record ShapePlan(CompiledPlan Compiled, RenderedStatement Rendered);

    /// <summary>
    /// One plan of this statement and what belongs to it: the plan, its text and its planning stats,
    /// the compiled plan of every binding shape planned under it, the tables it reads, and the row
    /// counts it was made on (D342).
    /// </summary>
    internal sealed class PlanState
    {
        private PlanState(
            Plan plan,
            string? planText,
            PlanningStats stats,
            PlanningState planningState,
            CompiledPlan compiled,
            ConcurrentDictionary<string, ShapePlan> shapes,
            IReadOnlyList<TableKey>? tablesRead,
            (TableKey Table, long Rows)[] plannedRows)
        {
            Plan = plan;
            PlanText = planText;
            Stats = stats;
            PlanningState = planningState;
            Compiled = compiled;
            Shapes = shapes;
            TablesRead = tablesRead;
            _plannedRows = plannedRows;
        }

        public Plan Plan { get; }

        public string? PlanText { get; }

        public PlanningStats Stats { get; }

        public PlanningState PlanningState { get; }

        /// <summary>The compiled plan of the shape prepared at prepare time.</summary>
        public CompiledPlan Compiled { get; }

        /// <summary>Every binding shape planned under this plan, by its key.</summary>
        public ConcurrentDictionary<string, ShapePlan> Shapes { get; }

        /// <summary>
        /// The tables this plan reads, collected once (D271 (d)): staleness is per table, and asking
        /// the plan again on every execution would put a tree walk on the execution path.
        /// </summary>
        public IReadOnlyList<TableKey>? TablesRead { get; }

        /// <summary>
        /// The row count of each table this plan reads, as the numbers it was made on gave it, for the
        /// tables whose count was known (D342): what drift is judged against.
        /// </summary>
        public (TableKey Table, long Rows)[] PlannedRows => Volatile.Read(ref _plannedRows);

        private (TableKey Table, long Rows)[] _plannedRows;

        /// <summary>
        /// Judges drift from <paramref name="rows"/> from now on: the planner came to this same plan on
        /// them, so the plan is as current as one made on them would be.
        /// </summary>
        public void Rebase((TableKey Table, long Rows)[] rows) => Volatile.Write(ref _plannedRows, rows);

        /// <summary>
        /// The state of a plan the engine returned: <paramref name="names"/>, when the prepare asked
        /// for a redaction and named its parameters, go back where the rewrite put a <c>?</c> in the
        /// plan text, whose dynamic parameters read <c>?0</c>, <c>?1</c>, … (D287).
        /// </summary>
        public static PlanState Of(
            ChalkEngine engine,
            PlanResult result,
            CompiledPlan compiled,
            string key,
            RenderedStatement rendered,
            IReadOnlyList<string>? names)
        {
            var tablesRead = ChalkEngine.TablesRead(result.Plan);
            var shapes = new ConcurrentDictionary<string, ShapePlan>(StringComparer.Ordinal)
            {
                [key] = new ShapePlan(compiled, rendered),
            };
            return new PlanState(
                result.Plan,
                names is null || result.PlanText is null
                    ? result.PlanText
                    : ParameterNames.Substitute(result.PlanText, names, indexed: true),
                result.Stats,
                result.PlanningState,
                compiled,
                shapes,
                tablesRead,
                PlannedRows(result.PlannedOn ?? engine.Catalog, tablesRead));
        }

        /// <summary>The same plan with the prepare shape's compiled plan replaced, as a decoration does.</summary>
        public PlanState With(CompiledPlan compiled, string key, RenderedStatement rendered)
        {
            var shapes = new ConcurrentDictionary<string, ShapePlan>(Shapes, StringComparer.Ordinal)
            {
                [key] = new ShapePlan(compiled, rendered),
            };
            return new PlanState(Plan, PlanText, Stats, PlanningState, compiled, shapes, TablesRead, PlannedRows);
        }
    }
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
    private CompiledPlan _compiled;
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
        // D312: a value used as a LIKE pattern and malformed under its escape is refused here, when
        // the execution is asked for — before any source is touched, and the same whether the LIKE
        // would have run in the engine or in a source.
        compiled.LikeParameters.Validate(parameters);
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
    /// Has this execution read <paramref name="clock"/> as the current time, instead of the engine's,
    /// when called before it runs: a standing view's epoch's clock (D339).
    /// </summary>
    internal void UseClock(TimeProvider clock) => _compiled = _compiled.WithClock(clock);

    /// <summary>
    /// Has this execution write the engine's clock into <paramref name="slots"/> when it pins its
    /// tables: the parameter named as the clock, which the host left unbound (D341).
    /// </summary>
    internal void UseClockSlots(int[] slots) => _compiled = _compiled.WithClockSlots(slots);

    /// <summary>
    /// Has this execution keep its reads of sources outside epochs in <paramref name="reads"/>, and
    /// replay what it kept, when called before it runs: a standing view over a database (D340).
    /// </summary>
    internal void UseReads(Chalk.Sources.IReadKeeper reads) => _compiled = _compiled.WithReads(reads);

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

            // One structured event per execution (04-client.md §11), at Trace: dozens a second under
            // load, it would bury the planning a host reads at Debug, and the result carries its
            // ExecutionStats for a host that wants them. The counters come first because they are
            // the ones that stay comparable when the hardware changes.
            Microsoft.Extensions.Logging.LoggerExtensions.LogTrace(
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
