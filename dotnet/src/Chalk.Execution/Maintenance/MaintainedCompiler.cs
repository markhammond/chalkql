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
/// </remarks>
internal sealed class MaintainedCompiler
{
    private const string Placeholder = "$maintained#";

    private readonly Plan _plan;
    private readonly CatalogContext _catalog;
    private readonly IReadOnlyDictionary<string, ISourceRuntime> _sources;
    private readonly ExecutionSettings _settings;
    private readonly HashSet<uint> _clockSlots;
    private readonly IPlanCompilation _compilation;
    private readonly Dictionary<string, OperatorFactory> _placeholders = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, Rel> _sharedPlans = [];
    private readonly Dictionary<uint, (SharedState State, Rel Rewritten)> _shared = [];
    private readonly Dictionary<Rel, ReadState> _reads = new(ReferenceEqualityComparer.Instance);

    /// <summary>The statement's last step: the first beneath its projections, where a LIMIT is kept (D360).</summary>
    private readonly Rel _end;

    public MaintainedCompiler(
        Plan plan,
        CatalogContext catalog,
        IReadOnlyDictionary<string, ISourceRuntime> sources,
        ExecutionSettings settings,
        IReadOnlyCollection<int> clockSlots)
    {
        _plan = plan;
        _catalog = catalog;
        _sources = sources;
        _settings = settings;
        _clockSlots = [.. clockSlots.Select(s => (uint)s)];
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
        return _compilation.Node(rewritten, string.Empty);
    }

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
        Rel.KindOneofCase.Window => "a window function over a table is not maintained.",
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
        var body = _compilation.Node(rel, path);
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

        var descriptor = _catalog.FindTable(table.Schema, table.Table)
            ?? throw new InvalidPlanException("I-IR-6", "root", $"the catalog has no table {table.Schema}.{table.Table}");
        if (descriptor.Table.Entitlement is not null)
        {
            throw new NotMaintainedException($"the table '{table.Table}' carries an access policy, which a maintained view does not read under.");
        }

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
            return Clocked(rel, rel.Filter.Input, path, input => Filter(rel, input));
        }

        return Filter(rel, Rewrite(rel.Filter.Input, path));
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
        var inputFactory = _compilation.Node(Rewrite(input, path), path);
        var replayRow = Weighted(input.RowType);
        var replaySchema = ArrowTypeMapping.ToArrowSchema(replayRow);
        var replayTypes = Types(replayRow);
        var replay = Substituted(replayRow, context => new ReplayChanges(context, path, replaySchema, replayTypes, state));
        var body = _compilation.Node(over(replay), path);
        var weighted = Weighted(rel.RowType);
        var schema = ArrowTypeMapping.ToArrowSchema(weighted);
        var types = Types(weighted);
        return Substituted(weighted, context => new ClockedChanges(
            context, path, schema, types, state, inputFactory(context), body(context)));
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
        if (type is not (JoinType.Inner or JoinType.Left or JoinType.Semi or JoinType.Anti))
        {
            throw new NotMaintainedException(
                $"a {type.ToString().ToUpperInvariant()} join is not maintained; inner, left, semi and anti joins are.");
        }

        if (condition is not null)
        {
            RefuseClock(condition, "a join's condition");
        }

        var leftTypes = Types(left.RowType);
        var rightTypes = Types(right.RowType);
        var state = new JoinState(
            leftTypes,
            rightTypes,
            Types(rel.RowType),
            [.. leftKeys.Select(k => (int)k)],
            [.. rightKeys.Select(k => (int)k)],
            type switch
            {
                JoinType.Left => JoinMode.Left,
                JoinType.Semi => JoinMode.Semi,
                JoinType.Anti => JoinMode.Anti,
                _ => JoinMode.Inner,
            },
            _settings.BatchSize);
        States.Add(state);
        var leftFactory = _compilation.Node(Rewrite(left, path), path);
        var rightFactory = _compilation.Node(Rewrite(right, path), path);
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
        var leftFactory = _compilation.Node(Rewrite(join.Left, path), path);
        var rightRewritten = Rewrite(join.Right, path);
        if (rightRead.KindCase == Rel.KindOneofCase.IndexLookup && !InTableOrder(join, rightRead.IndexLookup))
        {
            throw new NotMaintainedException(
                "an as-of join over an index lookup that may hand over rows of one key and time out of their order "
                + "in the table is not maintained: its ties would break by an order the view cannot keep. A lookup "
                + "of one key, read forwards through an ordered index the table builds, whose later columns are the "
                + "join's keys or its time, is maintained; or key the join on both sides.");
        }

        var rightFactory = _compilation.Node(rightRewritten, path);

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
        var leftFactory = _compilation.Node(Rewrite(seek.Left, path), path);
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

        var seekFactory = _compilation.Node(matcher, path);
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
        var input = _compilation.Node(Rewrite(aggregate.Input, path), path);

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
        var inputFactory = _compilation.Node(Rewrite(input, path), path);
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
            var state = new SharedState(_compilation.Node(rewritten, $"shared#{id}"), Types(body.RowType), _settings.BatchSize);
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
            if (next is Expr { KindCase: Expr.KindOneofCase.Param } param && _clockSlots.Contains(param.Param.Index))
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
