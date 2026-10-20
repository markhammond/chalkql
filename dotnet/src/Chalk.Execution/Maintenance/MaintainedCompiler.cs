using Chalk.Catalog;
using Chalk.Execution.Expressions;
using Chalk.Execution.Operators;
using Chalk.Ir;
using Chalk.Sources;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Execution.Maintenance;

/// <summary>
/// Rewrites a statement's physical plan to run on changes (D343, <c>docs/design/68-incremental-maintenance.md</c>
/// §1–§2), or refuses it naming the first thing it cannot keep (§8).
/// </summary>
/// <remarks>
/// <para>
/// Every relation gains a last column, the weight. A filter, a projection and a <c>UNION ALL</c> keep
/// their own operators, the projection forwarding the weight; a sort is dropped. A node that keeps
/// state becomes a placeholder the compilation substitutes a maintained operator for: a read, a join,
/// an as-of join, an aggregate, a shared subtree, and a subtree that reads no table at all, which is
/// computed whole and compared.
/// </para>
/// <para>
/// The states are made here, once for the plan, and every operator tree built over it shares them.
/// </para>
/// <para>
/// A plan under an access policy is kept as any other (D361): its row filters and sanitisers are
/// filters and projections, and its paths joins. The compiled plan is then held to keep every filter
/// and projection over a governed read (<see cref="PolicyNodes"/>).
/// </para>
/// </remarks>
internal sealed class MaintainedCompiler
{
    private const string Placeholder = "$maintained#";

    private readonly Plan _plan;
    private readonly CatalogContext _catalog;
    private readonly IReadOnlyDictionary<string, ISourceRuntime> _sources;
    private readonly ExecutionSettings _settings;
    /// <summary>
    /// The parameters that move between updates: the clock's, and those a view may bind again (design 77
    /// §5). A node that reads one is kept as one that reads the clock is.
    /// </summary>
    private readonly HashSet<uint> _movingSlots;
    private readonly IPlanCompilation _compilation;
    private readonly Dictionary<string, OperatorFactory> _placeholders = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, Rel> _sharedPlans = [];
    private readonly Dictionary<uint, (SharedState State, Rel Rewritten)> _shared = [];
    private readonly Dictionary<Rel, ReadState> _reads = new(ReferenceEqualityComparer.Instance);

    /// <summary>Every tree the maintained plan compiles, which the policy's nodes are checked against.</summary>
    private readonly List<Rel> _compiled = [];

    /// <summary>The conditions of filters a maintained operator evaluates itself, rather than a compiled tree.</summary>
    private readonly List<Expr> _evaluated = [];

    /// <summary>The statement's last step: the first beneath its projections, where a LIMIT is kept (D360).</summary>
    private readonly Rel _end;

    public MaintainedCompiler(
        Plan plan,
        CatalogContext catalog,
        IReadOnlyDictionary<string, ISourceRuntime> sources,
        ExecutionSettings settings,
        IReadOnlyCollection<int> movingSlots)
    {
        _plan = plan;
        _catalog = catalog;
        _sources = sources;
        _settings = settings;
        _movingSlots = [.. movingSlots.Select(s => (uint)s)];
        _compilation = PlanCompiler.Begin(plan, catalog, sources, settings);
        _compilation.Substitute = (rel, _) =>
            rel.KindCase == Rel.KindOneofCase.BoundTable
            && rel.BoundTable.Name.StartsWith(Placeholder, StringComparison.Ordinal)
            && _placeholders.TryGetValue(rel.BoundTable.Name, out var factory)
                ? factory
                : null;
        foreach (var shared in plan.Shared)
        {
            _sharedPlans[shared.Id] = shared.Rel;
        }

        _end = plan.Root;
        while (_end.KindCase == Rel.KindOneofCase.Project)
        {
            _end = _end.Project.Input;
        }
    }

    /// <summary>Every state the plan's operators keep.</summary>
    public List<IMaintainedState> States { get; } = [];

    /// <summary>What replacements changed in the plan's tables, shared by every read of one in an update.</summary>
    public TableDiffs Diffs { get; } = new();

    /// <summary>
    /// The maintained plan's root: a factory for the operator whose batches are the result's changes,
    /// the output columns then the weight.
    /// </summary>
    public OperatorFactory Compile()
    {
        if (_settings.UseReferenceEngine)
        {
            throw new NotMaintainedException(
                "the engine runs the reference executor, and a maintained view runs on the vectorised one.")
            {
                Path = "root",
            };
        }

        var rewritten = Rewrite(_plan.Root, "root");
        var root = Node(rewritten, string.Empty);
        PolicyNodes.Check(_plan, Governed, _compiled, _evaluated);
        return root;
    }

    /// <summary>A tree compiled into the maintained plan: its operators, and a record of it for the policy's check.</summary>
    private OperatorFactory Node(Rel rel, string parentPath)
    {
        _compiled.Add(rel);
        return _compilation.Node(rel, parentPath);
    }

    /// <summary>
    /// The table a read or a lookup reads under an access policy, or as the policy's own read along a
    /// path; null for any other node.
    /// </summary>
    private string? Governed(Rel rel) => rel.KindCase switch
    {
        Rel.KindOneofCase.Read when rel.PolicyInjected || rel.Read.Correlation || Entitled(rel.Read.Table) =>
            rel.Read.Table.Table,
        Rel.KindOneofCase.IndexLookup when rel.PolicyInjected || Entitled(rel.IndexLookup.Table) =>
            rel.IndexLookup.Table.Table,
        _ => null,
    };

    private bool Entitled(TableRef table) => _catalog.FindTable(table.Schema, table.Table)?.Table.Entitlement is not null;

    private Rel Rewrite(Rel rel, string parent)
    {
        var path = $"{parent}/{rel.KindCase}";
        try
        {
            if (!ReadsTable(rel))
            {
                return Island(rel, path);
            }

            return rel.KindCase switch
            {
                Rel.KindOneofCase.Read => Read(rel, path),
                Rel.KindOneofCase.IndexLookup => Lookup(rel, path),
                Rel.KindOneofCase.Filter => Filter(rel, path),
                Rel.KindOneofCase.Project => Project(rel, path),
                Rel.KindOneofCase.Sort => Rewrite(rel.Sort.Input, path),
                Rel.KindOneofCase.TopN when ReferenceEquals(rel, _end) => Limit(rel, path, rel.TopN.Input, rel.TopN.Fields),
                Rel.KindOneofCase.Fetch when ReferenceEquals(rel, _end) && rel.Fetch.Order.Count > 0 =>
                    Limit(rel, path, rel.Fetch.Input, rel.Fetch.Order),
                Rel.KindOneofCase.SetOp => SetOp(rel, path),
                Rel.KindOneofCase.HashJoin => Join(
                    rel, path, rel.HashJoin.Left, rel.HashJoin.Right, rel.HashJoin.LeftKeys, rel.HashJoin.RightKeys,
                    rel.HashJoin.PostJoinFilter, rel.HashJoin.Type),
                Rel.KindOneofCase.MergeJoin => Join(
                    rel, path, rel.MergeJoin.Left, rel.MergeJoin.Right, rel.MergeJoin.LeftKeys, rel.MergeJoin.RightKeys,
                    rel.MergeJoin.PostJoinFilter, rel.MergeJoin.Type),
                Rel.KindOneofCase.NestedLoopJoin => Join(
                    rel, path, rel.NestedLoopJoin.Left, rel.NestedLoopJoin.Right, [], [],
                    rel.NestedLoopJoin.Condition, rel.NestedLoopJoin.Type),
                Rel.KindOneofCase.Join => Join(
                    rel, path, rel.Join.Left, rel.Join.Right, [], [], rel.Join.Condition, rel.Join.Type),
                Rel.KindOneofCase.AsOfJoin => AsOfJoin(rel, path),
                Rel.KindOneofCase.AsOfLookupJoin => AsOfSeek(rel, path),
                Rel.KindOneofCase.HashAggregate => Aggregate(rel, path, rel.HashAggregate.Aggregate),
                Rel.KindOneofCase.StreamAggregate => Aggregate(rel, path, rel.StreamAggregate.Aggregate),
                Rel.KindOneofCase.Aggregate => Aggregate(rel, path, rel.Aggregate),
                Rel.KindOneofCase.SharedRef => Shared(rel, path),
                Rel.KindOneofCase.Window => WindowCalls(rel, path),
                _ => throw new NotMaintainedException(Unmaintained(rel)),
            };
        }
        catch (NotMaintainedException refusal) when (refusal.Path is null)
        {
            throw new NotMaintainedException(refusal.Message) { Path = path };
        }
    }

    private static string Unmaintained(Rel rel) => rel.KindCase switch
    {
        Rel.KindOneofCase.Fetch or Rel.KindOneofCase.TopN =>
            "a LIMIT is maintained only as the statement's last step, ordered by its ORDER BY; here its rows "
            + "would depend on the input's order.",
        Rel.KindOneofCase.Hop or Rel.KindOneofCase.Session => "a hopping or session window is not maintained.",
        Rel.KindOneofCase.Unnest => "UNNEST over a table is not maintained.",
        Rel.KindOneofCase.RemoteQuery or Rel.KindOneofCase.LookupJoin or Rel.KindOneofCase.AdaptiveJoin
            or Rel.KindOneofCase.PartitionedScan or Rel.KindOneofCase.MaterialisedInput =>
            "a read of a source outside the engine's epochs is not maintained.",
        _ => $"the operator {rel.KindCase} is not maintained.",
    };

    /// <summary>A subtree that reads no table: computed whole, at the first update and when the clock moves.</summary>
    private Rel Island(Rel rel, string path)
    {
        var types = Types(rel.RowType);
        var body = Node(rel, path);
        var state = new IslandState(body, types, ReadsClock(rel), _settings.BatchSize);
        States.Add(state);
        var weighted = Weighted(rel.RowType);
        var schema = ArrowTypeMapping.ToArrowSchema(weighted);
        var weightedTypes = Types(weighted);
        return Substituted(weighted, context => new IslandChanges(context, path, schema, weightedTypes, state));
    }

    private Rel Read(Rel rel, string path)
    {
        var read = rel.Read;
        if (read.Filter is not null)
        {
            throw new NotMaintainedException("a read with a filter its source applies is not maintained.");
        }

        var state = ReadOf(read.Table, [.. read.Projection.Select(i => (int)i)], rel.RowType);
        _reads[rel] = state;
        var weighted = Weighted(rel.RowType);
        var schema = ArrowTypeMapping.ToArrowSchema(weighted);
        var types = Types(weighted);
        return Substituted(weighted, context => new ReadChanges(context, path, schema, types, state));
    }

    /// <summary>
    /// An index lookup pinned to keys — a constant key, an IN list — read as its table is (design 68
    /// §19): the table's changes, kept to the rows whose key columns hold one of the lookup's keys, then
    /// its own residual. A lookup over a range of keys is refused.
    /// </summary>
    private Rel Lookup(Rel rel, string path)
    {
        var lookup = rel.IndexLookup;
        if (!lookup.Ranges.All(Pinned))
        {
            throw new NotMaintainedException(
                "an index lookup over a range of keys is not maintained; a lookup pinned to keys, as a constant "
                + "key or an IN list makes, is.");
        }

        var descriptor = _catalog.FindTable(lookup.Table.Schema, lookup.Table.Table)
            ?? throw new InvalidPlanException("I-IR-6", path, $"the catalog has no table {lookup.Table.Schema}.{lookup.Table.Table}");
        var index = descriptor.Table.Indexes.FirstOrDefault(i => string.Equals(i.Name, lookup.Index, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidPlanException("I-IR-6", path, $"table {lookup.Table.Schema}.{lookup.Table.Table} declares no index named '{lookup.Index}'");

        // The lookup's own columns, then the key columns it does not hand over, which the filter reads.
        var columns = lookup.Projection.Select(c => (int)c).ToList();
        var row = rel.RowType.Clone();
        foreach (var bound in lookup.Ranges.SelectMany(r => r.Lower.Select((value, k) => (value, k))))
        {
            RefuseClock(bound.value, "an index lookup's key");
            var column = index.Columns[bound.k];
            if (!columns.Contains(column))
            {
                columns.Add(column);
                var described = descriptor.Table.Columns[column];
                row.Fields.Add(new Field { Name = described.Name, Type = described.Type.ToProto() });
            }
        }

        var read = new Rel { RowType = row, Read = new Ir.Read { Table = lookup.Table } };
        read.Read.Projection.AddRange(columns.Select(c => (uint)c));
        var condition = lookup.Ranges.Count == 0
            ? new Expr { Type = ChalkType.Bool().ToProto(), Literal = new Literal { BoolValue = false } }
            : AnyOf([.. lookup.Ranges.Select(range => AllOf([.. range.Lower.Select((value, k) => KeyEquals(row, columns.IndexOf(index.Columns[k]), value))]))]);
        var kept = new Rel { RowType = row, Filter = new Ir.Filter { Input = read, Condition = condition } };
        if (columns.Count > lookup.Projection.Count)
        {
            var project = new Ir.Project { Input = kept };
            project.Exprs.AddRange(rel.RowType.Fields.Select((field, i) => new Expr { Type = field.Type, FieldRef = new FieldRef { Index = (uint)i } }));
            kept = new Rel { RowType = rel.RowType, Project = project };
        }

        if (lookup.Residual is { } residual)
        {
            kept = new Rel { RowType = rel.RowType, Filter = new Ir.Filter { Input = kept, Condition = residual } };
        }

        var rewritten = Rewrite(kept, path);
        _reads[rel] = _reads[read];
        return rewritten;
    }

    /// <summary>Whether a range is one key: equal bounds over the same columns, both inclusive, and no prefix.</summary>
    private static bool Pinned(IndexRange range) =>
        range is { LowerInclusive: true, UpperInclusive: true, Prefix: false }
        && range.Lower.Count > 0
        && range.Lower.Equals(range.Upper);

    /// <summary>A key column equal to a lookup's bound, the bound cast to the column's type if it has another.</summary>
    private static Expr KeyEquals(RowType row, int slot, Expr bound)
    {
        var type = row.Fields[slot].Type;
        var column = ChalkType.FromProto(type);
        var given = ChalkType.FromProto(bound.Type);
        var value = given.WithNullable(false) == column.WithNullable(false)
            ? bound
            : new Expr { Type = column.WithNullable(given.Nullable).ToProto(), Cast = new Ir.Cast { Input = bound, OnFailure = CastFailure.Error } };
        var equals = new Expr { Type = ChalkType.Bool(nullable: true).ToProto(), Call = new ScalarCall { Function = FunctionId.Eq } };
        equals.Call.Args.Add(new Expr { Type = type, FieldRef = new FieldRef { Index = (uint)slot } });
        equals.Call.Args.Add(value);
        return equals;
    }

    /// <summary>The conjunction of <paramref name="terms"/>, or the one term.</summary>
    private static Expr AllOf(Expr[] terms) => Joined(FunctionId.And, terms);

    /// <summary>The disjunction of <paramref name="terms"/>, or the one term.</summary>
    private static Expr AnyOf(Expr[] terms) => Joined(FunctionId.Or, terms);

    private static Expr Joined(FunctionId function, Expr[] terms)
    {
        if (terms.Length == 1)
        {
            return terms[0];
        }

        var joined = new Expr { Type = ChalkType.Bool(nullable: true).ToProto(), Call = new ScalarCall { Function = function } };
        joined.Call.Args.AddRange(terms);
        return joined;
    }

    /// <summary>The state of a read of <paramref name="table"/>'s <paramref name="projection"/>.</summary>
    private ReadState ReadOf(TableRef table, int[] projection, RowType row)
    {
        if (!_sources.TryGetValue(table.SourceId, out var source) || source is not ITrackedSource tracked)
        {
            throw new NotMaintainedException(
                $"the source '{table.SourceId}' cannot say what changed in its tables between two epochs. A "
                + "maintained view reads in-process tables refreshed in epochs.");
        }

        // A table under an access policy is read as any other: the policy's filters and projections over
        // the read are the statement's, kept by the rewrite and held to it by PolicyNodes (D361).
        var descriptor = _catalog.FindTable(table.Schema, table.Table)
            ?? throw new InvalidPlanException("I-IR-6", "root", $"the catalog has no table {table.Schema}.{table.Table}");
        var state = new ReadState(
            tracked,
            table.SourceId,
            descriptor.Table.Name,
            new ScanRequest
            {
                Table = descriptor.Table.Name,
                Projection = projection,
                OutputSchema = ArrowTypeMapping.ToArrowSchema(row),
                BatchSize = _settings.BatchSize,
            },
            Diffs);
        States.Add(state);
        return state;
    }

    private Rel Filter(Rel rel, string path)
    {
        if (ReadsClock(rel.Filter.Condition))
        {
            return Window(rel, path) ?? Clocked(rel, rel.Filter.Input, path, input => Filter(rel, input));
        }

        return Filter(rel, Rewrite(rel.Filter.Input, path));
    }

    /// <summary>
    /// A filter whose every read of the clock is a cut-off an input column is compared with: one that,
    /// when the clock moves, reads again only the rows between the old cut-offs and the new (design 68
    /// §6). Null for a filter that reads the clock any other way, which reads every row it holds.
    /// </summary>
    private Rel? Window(Rel rel, string path)
    {
        var input = rel.Filter.Input;
        var cuts = new List<(int Column, Expr Cut)>();
        if (!Cuts(rel.Filter.Condition, input.RowType, cuts) || cuts.Count == 0)
        {
            return null;
        }

        var state = new WindowState(Types(input.RowType), [.. cuts.Select(c => c.Column)], _settings.BatchSize);
        States.Add(state);
        var inputFactory = Node(Rewrite(input, path), path);
        var expressions = _compilation.ExpressionsAt(path);
        var condition = expressions.Compile(rel.Filter.Condition);
        IVectorExpr[] thresholds = [.. cuts.Select(c => expressions.Compile(c.Cut))];
        _evaluated.Add(rel.Filter.Condition);
        var weighted = Weighted(rel.RowType);
        var schema = ArrowTypeMapping.ToArrowSchema(weighted);
        var types = Types(weighted);
        return Substituted(weighted, context => new WindowChanges(
            context, path, schema, types, state, inputFactory(context), condition, thresholds));
    }

    /// <summary>
    /// Collects the comparisons of an input column with a cut-off that reads the clock, and says whether
    /// every read of the clock in <paramref name="expr"/> is the cut-off of one.
    /// </summary>
    private bool Cuts(Expr expr, RowType input, List<(int Column, Expr Cut)> cuts)
    {
        if (!ReadsClock(expr))
        {
            return true;
        }

        if (expr is { KindCase: Expr.KindOneofCase.Call, Call.Args.Count: 2 }
            && expr.Call.Function is FunctionId.Eq or FunctionId.Ne or FunctionId.Lt or FunctionId.Le
                or FunctionId.Gt or FunctionId.Ge)
        {
            if (Cut(expr.Call.Args[0], expr.Call.Args[1], input) is { } cut)
            {
                cuts.Add(cut);
                return true;
            }

            if (Cut(expr.Call.Args[1], expr.Call.Args[0], input) is { } reversed)
            {
                cuts.Add(reversed);
                return true;
            }
        }

        if (expr is { KindCase: Expr.KindOneofCase.Call, Call.Function: FunctionId.CurrentTimestamp or FunctionId.CurrentDate }
            || (expr.KindCase == Expr.KindOneofCase.Param && _movingSlots.Contains(expr.Param.Index)))
        {
            return false;
        }

        foreach (var child in Children(expr))
        {
            if (!Cuts(child, input, cuts))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <paramref name="column"/> compared with <paramref name="cut"/>: an input column of a whole-number
    /// type — a number, a date, a time or a timestamp — and an expression of that same type that reads
    /// no input column. Null for anything else.
    /// </summary>
    private static (int Column, Expr Cut)? Cut(Expr column, Expr cut, RowType input)
    {
        if (column.KindCase != Expr.KindOneofCase.FieldRef || column.FieldRef.Index >= input.Fields.Count)
        {
            return null;
        }

        var reads = false;
        foreach (var node in Descendants(cut))
        {
            reads |= node.KindCase == Expr.KindOneofCase.FieldRef;
        }

        var type = ChalkType.FromProto(input.Fields[(int)column.FieldRef.Index].Type).WithNullable(false);
        return reads
            || type.Kind is not (TypeKind.I8 or TypeKind.I16 or TypeKind.I32 or TypeKind.I64 or TypeKind.Date
                or TypeKind.Time or TypeKind.Timestamp or TypeKind.TimestampTz)
            || type != ChalkType.FromProto(cut.Type).WithNullable(false)
                ? null
                : ((int)column.FieldRef.Index, cut);
    }

    /// <summary>The expressions directly within <paramref name="expr"/>, however its fields hold them.</summary>
    private static IEnumerable<Expr> Children(Expr expr)
    {
        var pending = new Stack<Google.Protobuf.IMessage>();
        foreach (var field in Expr.Descriptor.Fields.InDeclarationOrder())
        {
            Push(pending, field.FieldType == Google.Protobuf.Reflection.FieldType.Message ? field.Accessor.GetValue(expr) : null);
        }

        while (pending.Count > 0)
        {
            var next = pending.Pop();
            if (next is Expr child)
            {
                yield return child;
                continue;
            }

            foreach (var field in next.Descriptor.Fields.InDeclarationOrder())
            {
                Push(pending, field.FieldType == Google.Protobuf.Reflection.FieldType.Message ? field.Accessor.GetValue(next) : null);
            }
        }

        static void Push(Stack<Google.Protobuf.IMessage> pending, object? value)
        {
            switch (value)
            {
                case Google.Protobuf.IMessage message:
                    pending.Push(message);
                    break;
                case System.Collections.IList list:
                    foreach (var item in list)
                    {
                        if (item is Google.Protobuf.IMessage element)
                        {
                            pending.Push(element);
                        }
                    }

                    break;
            }
        }
    }

    /// <summary><paramref name="expr"/> and every expression within it.</summary>
    private static IEnumerable<Expr> Descendants(Expr expr)
    {
        yield return expr;
        foreach (var child in Children(expr))
        {
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static Rel Filter(Rel rel, Rel input) => new()
    {
        RowType = Weighted(rel.RowType),
        Filter = new Ir.Filter { Input = input, Condition = rel.Filter.Condition },
    };

    private Rel Project(Rel rel, string path)
    {
        if (rel.Project.Exprs.Any(ReadsClock))
        {
            return Clocked(rel, rel.Project.Input, path, input => Project(rel, input));
        }

        return Project(rel, Rewrite(rel.Project.Input, path));
    }

    /// <summary>
    /// A filter or a projection that reads the clock over a table (design 68 §6): its own operator,
    /// weighted, run over a replay of what its state hands it.
    /// </summary>
    private Rel Clocked(Rel rel, Rel input, string path, Func<Rel, Rel> over)
    {
        var inputTypes = Types(input.RowType);
        var state = new ClockedState(inputTypes, Types(rel.RowType), _settings.BatchSize);
        States.Add(state);
        var inputFactory = Node(Rewrite(input, path), path);
        var replayRow = Weighted(input.RowType);
        var replaySchema = ArrowTypeMapping.ToArrowSchema(replayRow);
        var replayTypes = Types(replayRow);
        var replay = Substituted(replayRow, context => new ReplayChanges(context, path, replaySchema, replayTypes, state));
        var body = Node(over(replay), path);
        var weighted = Weighted(rel.RowType);
        var schema = ArrowTypeMapping.ToArrowSchema(weighted);
        var types = Types(weighted);
        return Substituted(weighted, context => new ClockedChanges(
            context, path, schema, types, state, inputFactory(context), body(context)));
    }

    /// <summary>
    /// A window rel's calls, maintained (design 77 §2, phase 1): each partition its input's changes touch
    /// is computed again, whole, by the batch window over its rows in order, and what differs from what it
    /// gave is given. Refused, naming the call, where one change could move every row of a partition (§1),
    /// or where a call's value depends on which of two tied rows comes first and the order can tie (§4).
    /// </summary>
    private Rel WindowCalls(Rel rel, string path)
    {
        var window = rel.Window;
        var input = window.Input;
        var names = rel.RowType.Fields;
        var inputWidth = input.RowType.Fields.Count;
        for (var i = 0; i < window.Calls.Count; i++)
        {
            var call = window.Calls[i];
            var name = inputWidth + i < names.Count ? names[inputWidth + i].Name : $"#{i}";
            if (Unbounded(window, call) is { } why)
            {
                throw new NotMaintainedException(
                    $"the window call '{name}' {why}, so one change to a partition would move every row of it.");
            }

            if (TieSensitive(window, call) && !OrderCannotTie(window, input))
            {
                throw new NotMaintainedException(
                    $"the window call '{name}' gives a value that depends on which of two rows that tie in its order "
                    + "comes first, and its partition and order keys cover no unique key the input declares. Declare "
                    + "one (UniqueKey on the table), order by columns that cover one, or use a RANGE frame.");
            }
        }

        var inputTypes = Types(input.RowType);
        var callTypes = Types(rel.RowType)[inputWidth..];
        var partitionOrder = new SortOrdering(
            [.. window.PartitionKeys.Select(k => new SortField
            {
                Expr = new Expr { Type = input.RowType.Fields[(int)k].Type, FieldRef = new FieldRef { Index = k } },
                Direction = SortDirection.AscNullsLast,
            })],
            input.RowType);
        var state = new WindowCallsState(
            inputTypes,
            callTypes,
            [.. window.PartitionKeys.Select(k => (int)k)],
            new SortOrdering(window.Order, input.RowType),
            partitionOrder,
            Reach(window, callTypes),
            ReadsClock(window),
            _settings.BatchSize);
        States.Add(state);
        var inputFactory = Node(Rewrite(input, path), path);

        // The batch window over the replay, which hands it each partition's rows in their order.
        var replayRow = Weighted(input.RowType);
        var replaySchema = ArrowTypeMapping.ToArrowSchema(replayRow);
        var replayTypes = Types(replayRow);
        var replay = Substituted(replayRow, context => new WindowReplay(context, path, replaySchema, replayTypes, state));
        var calls = window.Clone();
        calls.Input = replay;
        var callsRow = replayRow.Clone();
        for (var i = inputWidth; i < rel.RowType.Fields.Count; i++)
        {
            callsRow.Fields.Add(rel.RowType.Fields[i].Clone());
        }

        var body = Node(new Rel { RowType = callsRow, Window = calls }, path);
        var weighted = Weighted(rel.RowType);
        var schema = ArrowTypeMapping.ToArrowSchema(weighted);
        var types = Types(weighted);
        return Substituted(weighted, context => new WindowCallChanges(
            context, path, schema, types, state, inputFactory(context), body(context), inputWidth));
    }

    /// <summary>
    /// How far one change reaches in a window, in rows (design 77 §2): the rows after and before a changed
    /// row whose values may move, and the rows before and after a row its value reads, over every call;
    /// and how each call is resumed at a span that does not begin its partition. A RANGE or GROUPS offset,
    /// and an IGNORE NULLS navigation, reach without a count: unbounded.
    /// </summary>
    private static WindowReach Reach(Ir.Window window, ChalkType[] callTypes)
    {
        const int all = WindowReach.Unbounded;
        int ahead = 0, behind = 0, lookback = 0, lookahead = 0;
        var seeds = new WindowSeed[window.Calls.Count];
        var frame = window.Frame;
        var rowsFrame = frame?.Mode == FrameMode.Rows;
        var excludes = frame?.Exclusion is not (null or FrameExclusion.Unspecified or FrameExclusion.NoOthers);
        for (var i = 0; i < window.Calls.Count; i++)
        {
            var call = window.Calls[i];
            int a, b, back, forward;
            var seed = WindowSeed.None;
            if (call.FunctionCase == WindowCall.FunctionOneofCase.WindowFunction
                && call.WindowFunction is WindowFunctionId.RowNumber or WindowFunctionId.Rank or WindowFunctionId.DenseRank
                    or WindowFunctionId.Lag or WindowFunctionId.Lead)
            {
                switch (call.WindowFunction)
                {
                    case WindowFunctionId.Lag or WindowFunctionId.Lead:
                    {
                        var offset = call.Args.Count > 1 && Count(call.Args[1]) is { } n ? n : (call.Args.Count > 1 ? all : 1);
                        if (call.IgnoreNulls)
                        {
                            offset = all;
                        }

                        var lag = call.WindowFunction == WindowFunctionId.Lag;
                        (a, b, back, forward) = lag ? (offset, 0, offset, 0) : (0, offset, 0, offset);
                        break;
                    }

                    default:
                        (a, b, back, forward) = (all, 0, 0, 0);
                        seed = call.WindowFunction switch
                        {
                            WindowFunctionId.DenseRank => WindowSeed.Plus,
                            _ => WindowSeed.Rows,
                        };
                        break;
                }
            }
            else
            {
                // A frame's lower bound reaches back, its upper bound forward; a frame from the partition's
                // start reaches every later row.
                var lower = frame?.Lower;
                var upper = frame?.Upper;
                (a, back) = lower?.Kind switch
                {
                    FrameBoundKind.UnboundedPreceding => (all, all),
                    FrameBoundKind.Preceding when rowsFrame && Count(lower.Offset) is { } n => (n, n),
                    FrameBoundKind.Preceding => (all, all),
                    _ => (0, 0),
                };
                (b, forward) = upper?.Kind switch
                {
                    FrameBoundKind.Following when rowsFrame && Count(upper.Offset) is { } n => (n, n),
                    FrameBoundKind.Following => (all, all),
                    _ => (0, 0),
                };

                if (lower?.Kind == FrameBoundKind.UnboundedPreceding && !excludes && !call.Distinct
                    && call.UserFunction.Length == 0)
                {
                    seed = Resumable(call, upper?.Kind == FrameBoundKind.CurrentRow, callTypes[i]);
                    if (seed != WindowSeed.None)
                    {
                        back = 0;
                    }
                }

                if (excludes || frame?.Mode == FrameMode.Groups)
                {
                    (a, b, back, forward) = (all, all, all, all);
                }
            }

            seeds[i] = seed;
            ahead = Math.Max(ahead, a);
            behind = Math.Max(behind, b);
            lookback = Math.Max(lookback, back);
            lookahead = Math.Max(lookahead, forward);
        }

        return new WindowReach(ahead, behind, lookback, lookahead, seeds);
    }

    /// <summary>How a call over a frame from the partition's start resumes from the value before a span, if it can.</summary>
    private static WindowSeed Resumable(WindowCall call, bool endsAtCurrentRow, ChalkType type)
    {
        if (call.FunctionCase == WindowCall.FunctionOneofCase.WindowFunction)
        {
            return call.WindowFunction == WindowFunctionId.FirstValue
                ? call.IgnoreNulls ? WindowSeed.FirstIgnoringNulls : WindowSeed.First
                : WindowSeed.None;
        }

        // Only a frame that ends at its row holds just the rows before it, which the row before the span's
        // value is the aggregate of.
        if (!endsAtCurrentRow || call.FunctionCase != WindowCall.FunctionOneofCase.Aggregate)
        {
            return WindowSeed.None;
        }

        var kind = type.Kind;
        var summed = kind is TypeKind.I64 or TypeKind.I32 or TypeKind.Fp64 or TypeKind.Fp32;
        var ordered = summed || kind is TypeKind.I8 or TypeKind.I16 or TypeKind.Date or TypeKind.Timestamp or TypeKind.TimestampTz;
        return call.Aggregate switch
        {
            AggregateFunctionId.Sum or AggregateFunctionId.Sum0 or AggregateFunctionId.Count when summed => WindowSeed.Add,
            AggregateFunctionId.Min when ordered => WindowSeed.Least,
            AggregateFunctionId.Max when ordered => WindowSeed.Greatest,
            _ => WindowSeed.None,
        };
    }

    /// <summary>A literal count of rows, or null for a parameter or anything else.</summary>
    private static int? Count(Expr? expr) => expr?.KindCase == Expr.KindOneofCase.Literal
        ? expr.Literal.ValueCase switch
        {
            Literal.ValueOneofCase.I64Value => (int)Math.Min(expr.Literal.I64Value, int.MaxValue - 1),
            Literal.ValueOneofCase.I32Value => expr.Literal.I32Value,
            _ => null,
        }
        : null;

    /// <summary>
    /// Why one change could move every row of a call's partition, or null: a frame that reaches its end,
    /// or a function that reads the partition's size (§1).
    /// </summary>
    private static string? Unbounded(Ir.Window window, WindowCall call)
    {
        if (call.FunctionCase == WindowCall.FunctionOneofCase.WindowFunction)
        {
            switch (call.WindowFunction)
            {
                case WindowFunctionId.Ntile:
                    return "is an NTILE, which reads the partition's size";
                case WindowFunctionId.PercentRank:
                    return "is a PERCENT_RANK, which reads the partition's size";
                case WindowFunctionId.CumeDist:
                    return "is a CUME_DIST, which reads the partition's size";
                case WindowFunctionId.RowNumber or WindowFunctionId.Rank or WindowFunctionId.DenseRank
                    or WindowFunctionId.Lag or WindowFunctionId.Lead:
                    return null;
            }
        }

        return window.Frame?.Upper?.Kind == FrameBoundKind.UnboundedFollowing
            ? "has a frame that reaches the end of its partition"
            : null;
    }

    /// <summary>
    /// Whether a call's value depends on which of two rows that tie in the order comes first: a ROWS or
    /// GROUPS frame, or a function that counts or reads rows, where a RANGE frame, RANK and DENSE_RANK
    /// treat tied rows alike (§4).
    /// </summary>
    private static bool TieSensitive(Ir.Window window, WindowCall call)
    {
        if (call.FunctionCase == WindowCall.FunctionOneofCase.WindowFunction)
        {
            return call.WindowFunction is not (WindowFunctionId.Rank or WindowFunctionId.DenseRank);
        }

        return window.Frame?.Mode is not FrameMode.Range;
    }

    /// <summary>Whether the window's partition and order keys cover a unique key of its input.</summary>
    private bool OrderCannotTie(Ir.Window window, Rel input)
    {
        HashSet<int> keys = [.. window.PartitionKeys.Select(k => (int)k)];
        foreach (var field in window.Order)
        {
            if (field.Expr is { KindCase: Expr.KindOneofCase.FieldRef } reference)
            {
                keys.Add((int)reference.FieldRef.Index);
            }
        }

        return UniqueKeys(input).Any(unique => unique.IsSubsetOf(keys));
    }

    /// <summary>
    /// Sets of a relation's fields that no two of its rows share: a table's declared unique keys and unique
    /// indexes, carried through filters, projections, sorts and windows, and an aggregate's group keys.
    /// </summary>
    private List<HashSet<int>> UniqueKeys(Rel rel)
    {
        switch (rel.KindCase)
        {
            case Rel.KindOneofCase.Read:
                return TableKeys(rel.Read.Table, rel.Read.Projection);
            case Rel.KindOneofCase.IndexLookup:
                return TableKeys(rel.IndexLookup.Table, rel.IndexLookup.Projection);
            case Rel.KindOneofCase.Filter:
                return UniqueKeys(rel.Filter.Input);
            case Rel.KindOneofCase.Sort:
                return UniqueKeys(rel.Sort.Input);
            case Rel.KindOneofCase.Window:
                return UniqueKeys(rel.Window.Input);
            case Rel.KindOneofCase.SharedRef when _sharedPlans.TryGetValue(rel.SharedRef.Id, out var body):
                return UniqueKeys(body);
            case Rel.KindOneofCase.Project:
            {
                var mapped = new List<HashSet<int>>();
                foreach (var key in UniqueKeys(rel.Project.Input))
                {
                    var fields = new HashSet<int>();
                    foreach (var field in key)
                    {
                        var at = rel.Project.Exprs.ToList().FindIndex(
                            e => e is { KindCase: Expr.KindOneofCase.FieldRef } r && r.FieldRef.Index == field);
                        if (at < 0)
                        {
                            fields = null;
                            break;
                        }

                        fields.Add(at);
                    }

                    if (fields is not null)
                    {
                        mapped.Add(fields);
                    }
                }

                return mapped;
            }

            case Rel.KindOneofCase.HashAggregate or Rel.KindOneofCase.StreamAggregate or Rel.KindOneofCase.Aggregate:
            {
                var aggregate = rel.KindCase switch
                {
                    Rel.KindOneofCase.HashAggregate => rel.HashAggregate.Aggregate,
                    Rel.KindOneofCase.StreamAggregate => rel.StreamAggregate.Aggregate,
                    _ => rel.Aggregate,
                };

                return aggregate.Groupings.Count == 1
                    ? [[.. Enumerable.Range(0, aggregate.Groupings[0].Keys.Count)]]
                    : [];
            }

            default:
                return [];
        }
    }

    /// <summary>A table's unique keys and unique indexes, as the fields of a read that projects all their columns.</summary>
    private List<HashSet<int>> TableKeys(TableRef table, IList<uint> projection)
    {
        var keys = new List<HashSet<int>>();
        if (_catalog.FindTable(table.Schema, table.Table)?.Table is not { } descriptor)
        {
            return keys;
        }

        IEnumerable<IReadOnlyList<int>> declared = descriptor.UniqueKeys.Select(k => k.Columns)
            .Concat(descriptor.Indexes.Where(i => i.Unique).Select(i => i.Columns));
        foreach (var columns in declared)
        {
            var fields = new HashSet<int>();
            foreach (var column in columns)
            {
                var at = projection.IndexOf((uint)column);
                if (at < 0)
                {
                    fields = null;
                    break;
                }

                fields.Add(at);
            }

            if (fields is not null)
            {
                keys.Add(fields);
            }
        }

        return keys;
    }

    private static Rel Project(Rel rel, Rel input)
    {
        var project = new Ir.Project { Input = input };
        project.Exprs.AddRange(rel.Project.Exprs);
        project.Exprs.Add(new Expr
        {
            Type = Weights.Type.ToProto(),
            FieldRef = new FieldRef { Index = (uint)(input.RowType.Fields.Count - 1) },
        });
        return new Rel { RowType = Weighted(rel.RowType), Project = project };
    }

    private Rel SetOp(Rel rel, string path)
    {
        if (rel.SetOp.Kind != SetOpKind.UnionAll)
        {
            throw new NotMaintainedException($"{rel.SetOp.Kind} is not maintained; UNION ALL is.");
        }

        var union = new Ir.SetOp { Kind = SetOpKind.UnionAll };
        union.Inputs.AddRange(rel.SetOp.Inputs.Select(input => Rewrite(input, path)));
        return new Rel { RowType = Weighted(rel.RowType), SetOp = union };
    }

    private Rel Join(
        Rel rel,
        string path,
        Rel left,
        Rel right,
        IEnumerable<uint> leftKeys,
        IEnumerable<uint> rightKeys,
        Expr? condition,
        JoinType type)
    {
        if (type == JoinType.Right)
        {
            return Mirrored(rel, path, left, right, leftKeys, rightKeys, condition);
        }

        if (type is not (JoinType.Inner or JoinType.Left or JoinType.Semi or JoinType.Anti))
        {
            throw new NotMaintainedException(
                $"a {type.ToString().ToUpperInvariant()} join is not maintained; inner, left, right, semi and anti joins are.");
        }

        if (condition is not null)
        {
            RefuseClock(condition, "a join's condition");
        }

        // A nested-loop join the planner chose for a small side is still a join on keys where its
        // condition equates a column of each side: kept so, a change meets only its key's rows rather
        // than every row the other side holds.
        if (!leftKeys.Any() && condition is not null)
        {
            (leftKeys, rightKeys, condition) = EquiKeys(condition, left.RowType, right.RowType);
        }

        var leftTypes = Types(left.RowType);
        var rightTypes = Types(right.RowType);
        int[] leftColumns = [.. leftKeys.Select(k => (int)k)];
        int[] rightColumns = [.. rightKeys.Select(k => (int)k)];
        var mode = type switch
        {
            JoinType.Left => JoinMode.Left,
            JoinType.Semi => JoinMode.Semi,
            JoinType.Anti => JoinMode.Anti,
            _ => JoinMode.Inner,
        };
        if (condition is null && Seekable(left, leftColumns, leftTypes, rightColumns, rightTypes) is { } seek)
        {
            return SeekJoin(rel, path, left, right, leftColumns, rightColumns, mode, seek);
        }

        var state = new JoinState(
            leftTypes, rightTypes, Types(rel.RowType), leftColumns, rightColumns, mode, _settings.BatchSize);
        States.Add(state);
        var leftFactory = Node(Rewrite(left, path), path);
        var rightFactory = Node(Rewrite(right, path), path);
        var weighted = Weighted(rel.RowType);
        var schema = ArrowTypeMapping.ToArrowSchema(weighted);
        var types = Types(weighted);
        return Substituted(weighted, context => new JoinChanges(
            context,
            path,
            schema,
            types,
            state,
            leftFactory(context),
            rightFactory(context),
            condition is null ? null : _compilation.ExpressionsAt(path).Compile(condition)));
    }

    /// <summary>
    /// A right join, maintained as the left join it mirrors: its inputs swapped, their keys with them,
    /// its condition read across the swap, and a projection that puts the columns back in the order
    /// the plan has them. The planner writes a statement's left join as a right one where it would
    /// rather build on the other side, which a view under a policy meets once its tables grow.
    /// </summary>
    private Rel Mirrored(
        Rel rel,
        string path,
        Rel left,
        Rel right,
        IEnumerable<uint> leftKeys,
        IEnumerable<uint> rightKeys,
        Expr? condition)
    {
        var leftWidth = left.RowType.Fields.Count;
        var rightWidth = right.RowType.Fields.Count;
        var row = new RowType();
        row.Fields.AddRange(rel.RowType.Fields.Skip(leftWidth));
        row.Fields.AddRange(rel.RowType.Fields.Take(leftWidth));
        Expr? across = null;
        if (condition is not null)
        {
            across = condition.Clone();
            foreach (var node in Descendants(across))
            {
                if (node.KindCase == Expr.KindOneofCase.FieldRef)
                {
                    var index = (int)node.FieldRef.Index;
                    node.FieldRef.Index = (uint)(index < leftWidth ? rightWidth + index : index - leftWidth);
                }
            }
        }

        var mirror = new Rel { RowType = row, Join = new Ir.Join { Left = right, Right = left, Type = JoinType.Left } };
        var joined = Join(mirror, path, right, left, rightKeys, leftKeys, across, JoinType.Left);
        var back = new Ir.Project();
        back.Exprs.AddRange(rel.RowType.Fields.Select((field, i) => new Expr
        {
            Type = field.Type,
            FieldRef = new FieldRef { Index = (uint)(i < leftWidth ? rightWidth + i : i - leftWidth) },
        }));
        return Project(new Rel { RowType = rel.RowType, Project = back }, joined);
    }

    /// <summary>
    /// How a join's left side can be sought rather than held (design 74 §6): where it is a read of a
    /// table under an access policy through filters and projections that do not read the clock, and an
    /// index of the table begins with the columns the left keys hand over, the read, the index, and for
    /// each of the index's leading columns the key that holds its value. Null for any other join.
    /// </summary>
    private (Rel Read, IndexDescriptor Index, int[] Order)? Seekable(
        Rel left, int[] leftKeys, ChalkType[] leftTypes, int[] rightKeys, ChalkType[] rightTypes)
    {
        if (leftKeys.Length == 0
            || TableUnder(left) is not { KindCase: Rel.KindOneofCase.Read } read
            || read.Read.Filter is not null
            || Governed(read) is null
            || ReadsClock(left)
            || _catalog.FindTable(read.Read.Table.Schema, read.Read.Table.Table)?.Table is not { } table)
        {
            return null;
        }

        var columns = new int[leftKeys.Length];
        for (var k = 0; k < leftKeys.Length; k++)
        {
            // A key the index compares as the join does: one type on both sides, and not floating point,
            // whose -0.0 and 0.0 are one key to a join.
            var type = leftTypes[leftKeys[k]];
            if (TableColumn(left, leftKeys[k]) is not { } column
                || type.Kind is TypeKind.Fp32 or TypeKind.Fp64
                || type.WithNullable(false) != rightTypes[rightKeys[k]].WithNullable(false)
                || columns.AsSpan(0, k).Contains(column))
            {
                return null;
            }

            columns[k] = column;
        }

        foreach (var index in table.Indexes)
        {
            var fits = index.Kind switch
            {
                IndexKind.Ordered or IndexKind.Clustered => index.Columns.Count >= columns.Length,
                IndexKind.Hash => index.Columns.Count == columns.Length,
                _ => false,
            };
            if (fits && index.Columns.Take(columns.Length).All(columns.Contains))
            {
                return (read, index, [.. index.Columns.Take(columns.Length).Select(c => Array.IndexOf(columns, c))]);
            }
        }

        return null;
    }

    /// <summary>
    /// A join that seeks its left side through an index rather than holding it (design 74 §6): the right
    /// side and the left changes as any join reads them, and a lookup of the left side, compiled as the
    /// statement's own filters and projections over the rows of the keys the right changes name.
    /// </summary>
    private Rel SeekJoin(
        Rel rel,
        string path,
        Rel left,
        Rel right,
        int[] leftKeys,
        int[] rightKeys,
        JoinMode mode,
        (Rel Read, IndexDescriptor Index, int[] Order) seek)
    {
        var state = new JoinSeekState(
            Types(left.RowType), Types(right.RowType), Types(rel.RowType), leftKeys, rightKeys, seek.Order, mode, _settings.BatchSize);
        States.Add(state);
        var leftFactory = Node(Rewrite(left, path), path);
        var rightFactory = Node(Rewrite(right, path), path);

        var read = seek.Read;
        var table = _catalog.FindTable(read.Read.Table.Schema, read.Read.Table.Table)!.Value.Table;
        var source = _sources[read.Read.Table.SourceId];
        int[] projection = [.. read.Read.Projection.Select(c => (int)c)];
        var lookupPath = $"{path}/Seek";
        var readSchema = ArrowTypeMapping.ToArrowSchema(read.RowType);
        var readTypes = Types(read.RowType);
        var keyed = Substituted(read.RowType, context => new KeyLookup(
            context, $"{lookupPath}/Read", readSchema, readTypes, state, source, table.Name, seek.Index.Name, projection));
        var lookupFactory = Node(Replaced(left, read, keyed), lookupPath);

        var weighted = Weighted(rel.RowType);
        var schema = ArrowTypeMapping.ToArrowSchema(weighted);
        var types = Types(weighted);
        return Substituted(weighted, context => new JoinSeekChanges(
            context, path, schema, types, state, leftFactory(context), rightFactory(context), lookupFactory(context)));
    }

    /// <summary>
    /// A table read through filters and projections, with its read replaced: the same conditions and
    /// expressions, carried rather than copied, so the policy's check finds them.
    /// </summary>
    private static Rel Replaced(Rel rel, Rel read, Rel by)
    {
        if (ReferenceEquals(rel, read))
        {
            return by;
        }

        switch (rel.KindCase)
        {
            case Rel.KindOneofCase.Filter:
                return new Rel
                {
                    RowType = rel.RowType,
                    Filter = new Ir.Filter { Input = Replaced(rel.Filter.Input, read, by), Condition = rel.Filter.Condition },
                };
            case Rel.KindOneofCase.Project:
                var project = new Ir.Project { Input = Replaced(rel.Project.Input, read, by) };
                project.Exprs.AddRange(rel.Project.Exprs);
                return new Rel { RowType = rel.RowType, Project = project };
            default:
                throw new InvalidOperationException($"a sought join's left side holds a {rel.KindCase}, not a filter or a projection.");
        }
    }

    /// <summary>
    /// The conjuncts of a join's condition that equate a column of the left with a column of the right,
    /// of one type, as the join's keys, and the rest of the condition, or null where nothing is left.
    /// An equality is NULL for a NULL on either side, which is how a key meets nothing.
    /// </summary>
    private static (uint[] Left, uint[] Right, Expr? Remaining) EquiKeys(Expr condition, RowType left, RowType right)
    {
        var width = left.Fields.Count;
        var leftKeys = new List<uint>();
        var rightKeys = new List<uint>();
        var rest = new List<Expr>();
        var pending = new Stack<Expr>();
        pending.Push(condition);
        while (pending.Count > 0)
        {
            var conjunct = pending.Pop();
            if (conjunct is { KindCase: Expr.KindOneofCase.Call, Call.Function: FunctionId.And })
            {
                for (var i = conjunct.Call.Args.Count - 1; i >= 0; i--)
                {
                    pending.Push(conjunct.Call.Args[i]);
                }

                continue;
            }

            if (conjunct is { KindCase: Expr.KindOneofCase.Call, Call.Function: FunctionId.Eq, Call.Args.Count: 2 }
                && conjunct.Call.Args[0].KindCase == Expr.KindOneofCase.FieldRef
                && conjunct.Call.Args[1].KindCase == Expr.KindOneofCase.FieldRef)
            {
                var (a, b) = ((int)conjunct.Call.Args[0].FieldRef.Index, (int)conjunct.Call.Args[1].FieldRef.Index);
                if (a >= width && b < width)
                {
                    (a, b) = (b, a);
                }

                if (a < width && b >= width && b - width < right.Fields.Count
                    && ChalkType.FromProto(left.Fields[a].Type).WithNullable(false)
                        == ChalkType.FromProto(right.Fields[b - width].Type).WithNullable(false))
                {
                    leftKeys.Add((uint)a);
                    rightKeys.Add((uint)(b - width));
                    continue;
                }
            }

            rest.Add(conjunct);
        }

        return ([.. leftKeys], [.. rightKeys], rest.Count == 0 ? null : AllOf([.. rest]));
    }

    private Rel AsOfJoin(Rel rel, string path)
    {
        var join = rel.AsOfJoin;
        if (join.Type is not (JoinType.Inner or JoinType.Left))
        {
            throw new NotMaintainedException($"a {join.Type} as-of join is not maintained.");
        }

        var rightRead = TableUnder(join.Right)
            ?? throw new NotMaintainedException(
                "an as-of join whose right input is not a table read through filters and projections is not "
                + "maintained: its ties would break by an order the view cannot keep.");
        var leftTypes = Types(join.Left.RowType);
        var rightTypes = Types(join.Right.RowType);
        var leftFactory = Node(Rewrite(join.Left, path), path);
        var rightRewritten = Rewrite(join.Right, path);
        if (rightRead.KindCase == Rel.KindOneofCase.IndexLookup && !InTableOrder(join, rightRead.IndexLookup))
        {
            throw new NotMaintainedException(
                "an as-of join over an index lookup that may hand over rows of one key and time out of their order "
                + "in the table is not maintained: its ties would break by an order the view cannot keep. A lookup "
                + "of one key, read forwards through an ordered index the table builds, whose later columns are the "
                + "join's keys or its time, is maintained; or key the join on both sides.");
        }

        var rightFactory = Node(rightRewritten, path);

        // Its right rows are placed by their order in the table, so a replacement is read whole.
        _reads[rightRead].WholeOnRewrite = true;
        var state = new AsOfState(
            leftTypes,
            rightTypes,
            Types(rel.RowType),
            [.. join.LeftKeys.Select(k => (int)k)],
            [.. join.RightKeys.Select(k => (int)k)],
            (int)join.LeftTime,
            (int)join.RightTime,
            join.Match,
            join.Type == JoinType.Left,
            _reads[rightRead],
            _settings.BatchSize);
        States.Add(state);
        var weighted = Weighted(rel.RowType);
        var schema = ArrowTypeMapping.ToArrowSchema(weighted);
        var types = Types(weighted);
        return Substituted(weighted, context => new AsOfChanges(
            context, path, schema, types, state, leftFactory(context), rightFactory(context)));
    }

    /// <summary>
    /// An as-of seek into a table's index (design 68 §13): the statement's own seek, run over the left
    /// rows whose match may have moved, and a read of the keys of the rows appended to the table. None
    /// of the table's rows are kept.
    /// </summary>
    private Rel AsOfSeek(Rel rel, string path)
    {
        var seek = rel.AsOfLookupJoin;
        if (seek.Type is not (JoinType.Inner or JoinType.Left))
        {
            throw new NotMaintainedException($"a {seek.Type} as-of join is not maintained.");
        }

        // A seek's matches move only when rows are appended under its keys, or the table is rewritten;
        // a fixed value that read the clock would move them as the clock does.
        foreach (var value in seek.FixedValues)
        {
            RefuseClock(value, "an as-of seek's fixed value");
        }

        var descriptor = _catalog.FindTable(seek.Table.Schema, seek.Table.Table)
            ?? throw new InvalidPlanException("I-IR-6", path, $"the catalog has no table {seek.Table.Schema}.{seek.Table.Table}");
        var projection = seek.Projection.Select(i => (int)i).ToArray();
        var leftKeys = seek.LeftKeys.Select(k => (int)k).ToArray();
        var leftRow = seek.Left.RowType;
        var leftTypes = Types(leftRow);
        var leftWidth = leftTypes.Length;
        var output = Types(rel.RowType);

        // The table's key columns, in the order of the left's, and its time: what a row appended or taken
        // out is known by.
        var keyColumns = seek.RightKeys.Select(k => projection[(int)k]).ToArray();
        int[] read = [.. keyColumns, projection[(int)seek.RightTime]];
        var keyRow = new RowType();
        foreach (var column in read)
        {
            var described = descriptor.Table.Columns[column];
            keyRow.Fields.Add(new Field { Name = described.Name, Type = described.Type.ToProto() });
        }

        var keyTypes = Types(keyRow);
        for (var k = 0; k < keyColumns.Length; k++)
        {
            if (leftTypes[leftKeys[k]].WithNullable(false) != keyTypes[k].WithNullable(false))
            {
                throw new NotMaintainedException(
                    $"an as-of seek whose key '{keyRow.Fields[k].Name}' has one type in its table and another on "
                    + "its left is not maintained.");
            }
        }

        var keys = ReadOf(seek.Table, read, keyRow);
        var leftFactory = Node(Rewrite(seek.Left, path), path);
        var state = new SeekState(
            leftTypes,
            output[leftWidth..],
            output,
            keyTypes,
            leftKeys,
            (int)seek.LeftTime,
            (int)seek.RightTime,
            seek.Match,
            seek.Type == JoinType.Left,
            keys,
            _settings.BatchSize);
        States.Add(state);

        // The statement's own seek, as a left join, over the left rows the update matches: each as all the
        // seek reads of it, its keys and its time, and its place among them.
        var pendingRow = new RowType();
        for (var i = 0; i < state.Replayed.Length; i++)
        {
            // Named by place: a key may be the time too.
            var replayed = leftRow.Fields[state.Replayed[i]].Clone();
            replayed.Name = $"$replayed{i}";
            pendingRow.Fields.Add(replayed);
        }

        pendingRow.Fields.Add(new Field { Name = "$place", Type = SeekState.Place.ToProto() });
        var pendingPath = $"{path}/Pending";
        var pendingSchema = ArrowTypeMapping.ToArrowSchema(pendingRow);
        var pendingTypes = Types(pendingRow);
        var pending = Substituted(pendingRow, context => new PendingRows(context, pendingPath, pendingSchema, pendingTypes, state));
        var matcher = new Rel { RowType = pendingRow.Clone(), AsOfLookupJoin = seek.Clone() };
        matcher.AsOfLookupJoin.Left = pending;
        matcher.AsOfLookupJoin.Type = JoinType.Left;
        matcher.AsOfLookupJoin.LeftKeys.Clear();
        matcher.AsOfLookupJoin.LeftKeys.Add(Enumerable.Range(0, leftKeys.Length).Select(k => (uint)k));
        matcher.AsOfLookupJoin.LeftTime = (uint)leftKeys.Length;
        foreach (var field in rel.RowType.Fields.Skip(leftWidth))
        {
            var right = field.Clone();
            right.Type.Nullable = true;
            matcher.RowType.Fields.Add(right);
        }

        var seekFactory = Node(matcher, path);
        var keysPath = $"{path}/Read";
        var keysWeighted = Weighted(keyRow);
        var keysSchema = ArrowTypeMapping.ToArrowSchema(keysWeighted);
        var keysTypes = Types(keysWeighted);
        var weighted = Weighted(rel.RowType);
        var schema = ArrowTypeMapping.ToArrowSchema(weighted);
        var types = Types(weighted);
        return Substituted(weighted, context => new SeekChanges(
            context,
            path,
            schema,
            types,
            state,
            leftFactory(context),
            new ReadChanges(context, keysPath, keysSchema, keysTypes, keys, appendsOnly: true),
            seekFactory(context),
            state.LargestTime is null ? null : seekFactory(context)));
    }

    private Rel Aggregate(Rel rel, string path, Aggregate aggregate)
    {
        var state = new AggregateState(Types(aggregate.Input.RowType), Types(rel.RowType), aggregate, _settings.BatchSize);
        States.Add(state);
        var input = Node(Rewrite(aggregate.Input, path), path);

        // Over a table read through filters and projections that do not read the clock, a rewrite of
        // the table gives the aggregate the new rows only, and it builds its groups again from them
        // rather than taking every old row out one by one (design 68 §14).
        var rebuildsOn = TableUnder(aggregate.Input) is { } read && !ReadsClock(aggregate.Input)
            && _reads.TryGetValue(read, out var whole)
                ? whole
                : null;
        rebuildsOn?.NewRowsOnRewrite = true;
        var weighted = Weighted(rel.RowType);
        var schema = ArrowTypeMapping.ToArrowSchema(weighted);
        var types = Types(weighted);
        return Substituted(weighted, context => new AggregateChanges(context, path, schema, types, state, input(context), rebuildsOn));
    }

    /// <summary>
    /// The LIMIT the statement ends in (D360, design 73 §4): a maintained top-N step over its input's
    /// changes. Over a table read through filters and projections it holds the window and a margin;
    /// over anything else, every row its input gives.
    /// </summary>
    private Rel Limit(Rel rel, string path, Rel input, IReadOnlyList<SortField> order)
    {
        var (offset, count) = _compilation.Bounds(rel, path);
        var ordering = new SortOrdering(order, input.RowType);
        var beneath = input;
        while (beneath.KindCase == Rel.KindOneofCase.Sort)
        {
            beneath = beneath.Sort.Input;
        }

        var state = new TopNState(
            Types(input.RowType), ordering, offset, count, TableUnder(beneath) is not null, _settings.BatchSize);
        States.Add(state);
        var inputFactory = Node(Rewrite(input, path), path);
        var weighted = Weighted(rel.RowType);
        var schema = ArrowTypeMapping.ToArrowSchema(weighted);
        var types = Types(weighted);
        return Substituted(weighted, context => new TopNChanges(context, path, schema, types, state, inputFactory(context)));
    }

    private Rel Shared(Rel rel, string path)
    {
        var id = rel.SharedRef.Id;
        if (!_shared.TryGetValue(id, out var shared))
        {
            var body = _sharedPlans.TryGetValue(id, out var plan)
                ? plan
                : throw new InvalidPlanException("I-IR-4", path, $"the plan lists no shared plan {id}");
            var rewritten = Rewrite(body, $"shared#{id}");
            var state = new SharedState(Node(rewritten, $"shared#{id}"), Types(body.RowType), _settings.BatchSize);
            States.Add(state);
            shared = (state, rewritten);
            _shared[id] = shared;
        }

        var weighted = Weighted(rel.RowType);
        var schema = ArrowTypeMapping.ToArrowSchema(weighted);
        var types = Types(weighted);
        var held = shared.State;
        return Substituted(weighted, context => new SharedChanges(context, path, schema, types, held));
    }

    /// <summary>
    /// The read or the index lookup at the bottom of an input that is a table read through filters and
    /// projections, or null for any other input (design 68 §5, §19).
    /// </summary>
    private static Rel? TableUnder(Rel rel) => rel.KindCase switch
    {
        Rel.KindOneofCase.Read or Rel.KindOneofCase.IndexLookup => rel,
        Rel.KindOneofCase.Filter => TableUnder(rel.Filter.Input),
        Rel.KindOneofCase.Project => TableUnder(rel.Project.Input),
        _ => null,
    };

    /// <summary>
    /// Whether a lookup under an as-of join hands over the rows of one key and time in their order in the
    /// table, as a read does, so their ties break as the view keeps them (design 68 §19): one range, read
    /// forwards, through an ordered index the table builds — which keeps rows with equal keys in their
    /// order — whose columns after the pinned ones are the join's keys or its time.
    /// </summary>
    private bool InTableOrder(AsOfJoin join, IndexLookup lookup)
    {
        var table = _catalog.FindTable(lookup.Table.Schema, lookup.Table.Table)?.Table;
        var index = table?.Indexes.FirstOrDefault(i => string.Equals(i.Name, lookup.Index, StringComparison.OrdinalIgnoreCase));
        if (table is null
            || index is not { Kind: IndexKind.Ordered or IndexKind.Clustered }
            || lookup.Reverse
            || lookup.Ranges.Count != 1
            || !_sources.TryGetValue(lookup.Table.SourceId, out var source)
            || source is not ITrackedSource tracked
            || !tracked.OrdersTiesByPlace(table.Name, index.Name))
        {
            return false;
        }

        HashSet<int?> joined = [.. join.RightKeys.Select(k => TableColumn(join.Right, (int)k)), TableColumn(join.Right, (int)join.RightTime)];
        return index.Columns.Skip(lookup.Ranges[0].Lower.Count).All(c => joined.Contains(c));
    }

    /// <summary>
    /// The table column an output column of a table read through filters and projections hands over, or
    /// null for one it computes.
    /// </summary>
    private static int? TableColumn(Rel rel, int column) => rel.KindCase switch
    {
        Rel.KindOneofCase.Read => (int)rel.Read.Projection[column],
        Rel.KindOneofCase.IndexLookup => (int)rel.IndexLookup.Projection[column],
        Rel.KindOneofCase.Filter => TableColumn(rel.Filter.Input, column),
        Rel.KindOneofCase.Project => rel.Project.Exprs[column] is { KindCase: Expr.KindOneofCase.FieldRef } field
            ? TableColumn(rel.Project.Input, (int)field.FieldRef.Index)
            : null,
        _ => null,
    };

    /// <summary>A placeholder in the rewritten plan for a maintained operator.</summary>
    private Rel Substituted(RowType weighted, OperatorFactory factory)
    {
        var name = Placeholder + _placeholders.Count;
        _placeholders[name] = factory;
        return new Rel { RowType = weighted, BoundTable = new BoundTable { Name = name } };
    }

    /// <summary>Whether anything under <paramref name="rel"/> reads a table, shared subtrees included.</summary>
    private bool ReadsTable(Rel rel)
    {
        var reads = false;
        PlanTables.Walk(rel, _ => reads = true);
        if (reads)
        {
            return true;
        }

        var shared = new List<uint>();
        Walk(rel, r =>
        {
            if (r.KindCase == Rel.KindOneofCase.SharedRef)
            {
                shared.Add(r.SharedRef.Id);
            }
        });
        return shared.Any(id => _sharedPlans.TryGetValue(id, out var body) && ReadsTable(body));
    }

    /// <summary>Whether an expression or a subtree reads the clock: the current time, or the clock's parameter.</summary>
    private bool ReadsClock(Google.Protobuf.IMessage message)
    {
        if (PlanTables.ReadsCurrentTime(message))
        {
            return true;
        }

        var found = false;
        var pending = new Stack<Google.Protobuf.IMessage>();
        pending.Push(message);
        while (pending.Count > 0 && !found)
        {
            var next = pending.Pop();
            if (next is Expr { KindCase: Expr.KindOneofCase.Param } param && _movingSlots.Contains(param.Param.Index))
            {
                found = true;
            }

            if (next is Rel { KindCase: Rel.KindOneofCase.SharedRef } use
                && _sharedPlans.TryGetValue(use.SharedRef.Id, out var body))
            {
                pending.Push(body);
            }

            foreach (var field in next.Descriptor.Fields.InDeclarationOrder())
            {
                if (field.FieldType != Google.Protobuf.Reflection.FieldType.Message)
                {
                    continue;
                }

                switch (field.Accessor.GetValue(next))
                {
                    case Google.Protobuf.IMessage child:
                        pending.Push(child);
                        break;
                    case System.Collections.IList list:
                        foreach (var item in list)
                        {
                            if (item is Google.Protobuf.IMessage element)
                            {
                                pending.Push(element);
                            }
                        }

                        break;
                }
            }
        }

        return found;
    }

    private void RefuseClock(Expr expr, string what)
    {
        if (ReadsClock(expr))
        {
            throw new NotMaintainedException(
                $"{what} reads the clock, which is not maintained there: a maintained view reads the clock in "
                + "filters and projections.");
        }
    }

    private static void Walk(Rel rel, Action<Rel> visit)
    {
        var pending = new Stack<Google.Protobuf.IMessage>();
        pending.Push(rel);
        while (pending.Count > 0)
        {
            var next = pending.Pop();
            if (next is Rel node)
            {
                visit(node);
            }

            foreach (var field in next.Descriptor.Fields.InDeclarationOrder())
            {
                if (field.FieldType != Google.Protobuf.Reflection.FieldType.Message)
                {
                    continue;
                }

                switch (field.Accessor.GetValue(next))
                {
                    case Google.Protobuf.IMessage child:
                        pending.Push(child);
                        break;
                    case System.Collections.IList list:
                        foreach (var item in list)
                        {
                            if (item is Google.Protobuf.IMessage element)
                            {
                                pending.Push(element);
                            }
                        }

                        break;
                }
            }
        }
    }

    /// <summary>A row type with the weight after its fields.</summary>
    private static RowType Weighted(RowType row)
    {
        var weighted = row.Clone();
        weighted.Fields.Add(new Field { Name = Weights.Name, Type = Weights.Type.ToProto() });
        return weighted;
    }

    private static ChalkType[] Types(RowType row) => [.. row.Fields.Select(f => ChalkType.FromProto(f.Type))];
}
