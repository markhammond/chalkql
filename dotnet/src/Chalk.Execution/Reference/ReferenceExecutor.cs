using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Execution.Memory;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Execution.Reference;

/// <summary>
/// The independent, deliberately naive implementation of the same IR (D13, invariant I4). Rows are
/// <c>object?[]</c>, every operator materialises its whole input, and nothing here shares a kernel or
/// an operator with the vectorised engine — only <c>ArrowTypeMapping</c> and the parameter binder,
/// which is exactly what §8 permits.
/// </summary>
/// <remarks>
/// Its only job is to be obviously correct and to disagree loudly. A host reaches it through
/// <c>ExecutionOptions.Engine = Reference</c>, which is useful when a wrong result is suspected in
/// production.
/// </remarks>
internal sealed class ReferenceExecutor
{
    private readonly CompiledPlan _plan;
    private readonly CatalogContext _catalog;
    private readonly IReadOnlyDictionary<string, ISourceRuntime> _sources;
    private readonly ExecutionSettings _settings;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<object?>>> _relations;

    public ReferenceExecutor(
        CompiledPlan plan,
        CatalogContext catalog,
        IReadOnlyDictionary<string, ISourceRuntime> sources,
        ExecutionSettings settings,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<object?>>>? relations = null)
    {
        _plan = plan;
        _catalog = catalog;
        _sources = sources;
        _settings = settings;
        _relations = relations ?? Operators.OperatorContext.NoRelations;
    }

    public async IAsyncEnumerable<RecordBatch> ExecuteAsync(
        IReadOnlyList<ScalarValue> parameters,
        ExecutionStats stats,
        ExecutionArena arena,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var started = _settings.TimeProvider.GetTimestamp();
        var interpreter = new ReferenceInterpreter(
            [.. parameters.Select(ToReferenceValue)],
            _catalog,
            _settings.Functions,
            BoundSlots.Of(_plan.BoundScalars, _plan.ParameterTypes.Count))
        {
            Now = _settings.TimeProvider.GetUtcNow(),
        };

        List<object?[]> rows;
        try
        {
            rows = await EvaluateAsync(_plan.Plan.Root, "root", interpreter, stats, arena, ct)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or ChalkException))
        {
            throw new ExecutionException(_plan.Plan.PlanDigest, "root", exception.Message, exception);
        }

        try
        {
            foreach (var batch in ReferenceValues.ToBatches(
                rows,
                _plan.OutputSchema,
                _plan.OutputTypes,
                _settings.BatchSize,
                ManagedMemoryAllocator.Instance))
            {
                ct.ThrowIfCancellationRequested();
                stats.AddRowsProduced(batch.Length);
                stats.AddBatchesProduced(1);
                yield return batch;
            }
        }
        finally
        {
            stats.Elapsed = _settings.TimeProvider.GetElapsedTime(started);
        }
    }

    /// <summary>The parameter binder is shared with the engine (§8); its scalars are unpacked here.</summary>
    private static object? ToReferenceValue(ScalarValue scalar)
    {
        if (scalar.IsNull)
        {
            return null;
        }

        return scalar.Type.Kind switch
        {
            TypeKind.Bool => scalar.Integer != 0,
            TypeKind.Fp32 => scalar.Single,
            TypeKind.Fp64 => scalar.Double,
            TypeKind.String => scalar.Text ?? string.Empty,
            TypeKind.Binary or TypeKind.Uuid => scalar.Bytes ?? [],
            TypeKind.Decimal => ReferenceValues.ReadDecimal(scalar.ReadBytes(), scalar.Type.Scale),
            _ => scalar.Integer,
        };
    }

    private async ValueTask<List<object?[]>> EvaluateAsync(
        Rel rel,
        string parentPath,
        ReferenceInterpreter interpreter,
        ExecutionStats stats,
        ExecutionArena arena,
        CancellationToken ct)
    {
        var path = $"{parentPath}/{rel.KindCase}";
        switch (rel.KindCase)
        {
            case Rel.KindOneofCase.Read:
                return await ReadAsync(rel, path, stats, arena, ct).ConfigureAwait(false);

            case Rel.KindOneofCase.IndexLookup:
                return await IndexLookupAsync(rel, path, interpreter, stats, arena, ct)
                    .ConfigureAwait(false);

            case Rel.KindOneofCase.RemoteQuery:
                // The oracle must not depend on any source's query engine (D13, D39): it runs a
                // PUSHDOWN_LEVEL_NONE plan, where a remote table is a plain Read through the
                // source's own scan path and every predicate is evaluated here. A RemoteQuery in an
                // oracle plan means the level was wrong, not that a node is missing.
                throw new UnsupportedFeatureException(
                    "RemoteQuery in the reference executor",
                    "The reference executor is the I4 oracle and runs plans made at "
                    + "PushdownLevel.None, where nothing is pushed into a source. Plan with "
                    + "ExecutionOptions.Engine = Reference and PushdownLevel.None.");

            case Rel.KindOneofCase.VirtualTable:
            {
                var rows = new List<object?[]>(rel.VirtualTable.Rows.Count);
                foreach (var row in rel.VirtualTable.Rows)
                {
                    rows.Add([.. row.Values.Select(v => interpreter.Evaluate(v, []))]);
                }

                return rows;
            }

            case Rel.KindOneofCase.BoundTable:
            {
                // The relation the host bound under this name (§4). The oracle binds it the way it
                // binds a parameter — the one thing §8 lets the two engines share — and then reads
                // it row by row like any other literal table.
                var name = rel.BoundTable.Name;
                if (!_relations.TryGetValue(name, out var supplied))
                {
                    throw new ExecutionException(
                        _plan.Plan.PlanDigest,
                        path,
                        $"the plan reads the context relation '{name}' and the execution bound "
                        + "none.");
                }

                var types = rel.RowType.Fields.Select(f => ChalkType.FromProto(f.Type)).ToList();
                var rows = new List<object?[]>(supplied.Count);
                foreach (var row in supplied)
                {
                    rows.Add(
                        [.. ParameterBinder.Bind(row, types).Select(ToReferenceValue)]);
                }

                return rows;
            }

            case Rel.KindOneofCase.Filter:
            {
                var input = await EvaluateAsync(rel.Filter.Input, path, interpreter, stats, arena, ct)
                    .ConfigureAwait(false);
                var kept = new List<object?[]>();
                foreach (var row in input)
                {
                    if (interpreter.IsTrue(rel.Filter.Condition, row))
                    {
                        kept.Add(row);
                    }
                }

                return kept;
            }

            case Rel.KindOneofCase.Project:
            {
                var input = await EvaluateAsync(rel.Project.Input, path, interpreter, stats, arena, ct)
                    .ConfigureAwait(false);
                var projected = new List<object?[]>(input.Count);
                foreach (var row in input)
                {
                    var values = new object?[rel.Project.Exprs.Count];
                    for (var i = 0; i < values.Length; i++)
                    {
                        values[i] = interpreter.Evaluate(rel.Project.Exprs[i], row);
                    }

                    projected.Add(values);
                }

                return projected;
            }

            case Rel.KindOneofCase.Sort:
            {
                var input = await EvaluateAsync(rel.Sort.Input, path, interpreter, stats, arena, ct)
                    .ConfigureAwait(false);
                input.Sort(new ReferenceRowComparer(rel.Sort.Fields));
                return input;
            }

            case Rel.KindOneofCase.Fetch:
            {
                var input = await EvaluateAsync(rel.Fetch.Input, path, interpreter, stats, arena, ct)
                    .ConfigureAwait(false);
                var skipped = input.Skip((int)Math.Min(rel.Fetch.Offset, int.MaxValue));
                return [.. rel.Fetch.HasCount ? skipped.Take((int)Math.Min(rel.Fetch.Count, int.MaxValue)) : skipped];
            }

            case Rel.KindOneofCase.TopN:
            {
                var input = await EvaluateAsync(rel.TopN.Input, path, interpreter, stats, arena, ct)
                    .ConfigureAwait(false);
                input.Sort(new ReferenceRowComparer(rel.TopN.Fields));
                return [.. input
                    .Skip((int)Math.Min(rel.TopN.Offset, int.MaxValue))
                    .Take((int)Math.Min(rel.TopN.Count, int.MaxValue))];
            }

            case Rel.KindOneofCase.Aggregate:
            case Rel.KindOneofCase.HashAggregate:
            {
                var aggregate = rel.KindCase == Rel.KindOneofCase.Aggregate
                    ? rel.Aggregate
                    : rel.HashAggregate.Aggregate;
                var input = await EvaluateAsync(aggregate.Input, path, interpreter, stats, arena, ct)
                    .ConfigureAwait(false);
                return ReferenceAggregation.Run(aggregate, input, interpreter);
            }

            case Rel.KindOneofCase.Join:
                return await JoinAsync(
                        rel.Join.Left,
                        rel.Join.Right,
                        rel.Join.Type,
                        (row, _) => rel.Join.Condition is null || interpreter.IsTrue(rel.Join.Condition, row),
                        path,
                        interpreter,
                        stats,
                        arena,
                        ct)
                    .ConfigureAwait(false);

            case Rel.KindOneofCase.HashJoin:
                return await EquiJoinAsync(
                        rel.HashJoin.Left,
                        rel.HashJoin.Right,
                        rel.HashJoin.Type,
                        rel.HashJoin.LeftKeys,
                        rel.HashJoin.RightKeys,
                        rel.HashJoin.PostJoinFilter,
                        path,
                        interpreter,
                        stats,
                        arena,
                        ct)
                    .ConfigureAwait(false);

            case Rel.KindOneofCase.MergeJoin:
                return await EquiJoinAsync(
                        rel.MergeJoin.Left,
                        rel.MergeJoin.Right,
                        rel.MergeJoin.Type,
                        rel.MergeJoin.LeftKeys,
                        rel.MergeJoin.RightKeys,
                        rel.MergeJoin.PostJoinFilter,
                        path,
                        interpreter,
                        stats,
                        arena,
                        ct)
                    .ConfigureAwait(false);

            case Rel.KindOneofCase.NestedLoopJoin:
                return await JoinAsync(
                        rel.NestedLoopJoin.Left,
                        rel.NestedLoopJoin.Right,
                        rel.NestedLoopJoin.Type,
                        (row, _) => rel.NestedLoopJoin.Condition is null
                            || interpreter.IsTrue(rel.NestedLoopJoin.Condition, row),
                        path,
                        interpreter,
                        stats,
                        arena,
                        ct)
                    .ConfigureAwait(false);

            case Rel.KindOneofCase.AsOfJoin:
                return await AsOfJoinAsync(rel.AsOfJoin, path, interpreter, stats, arena, ct)
                    .ConfigureAwait(false);

            case Rel.KindOneofCase.Window:
            {
                var input = await EvaluateAsync(rel.Window.Input, path, interpreter, stats, arena, ct)
                    .ConfigureAwait(false);
                return ReferenceWindow.Run(rel.Window, input, interpreter);
            }

            case Rel.KindOneofCase.Hop:
            {
                var input = await EvaluateAsync(rel.Hop.Input, path, interpreter, stats, arena, ct)
                    .ConfigureAwait(false);
                return ReferenceExpansions.Hop(rel.Hop, input, interpreter);
            }

            case Rel.KindOneofCase.Session:
            {
                var input = await EvaluateAsync(rel.Session.Input, path, interpreter, stats, arena, ct)
                    .ConfigureAwait(false);
                return ReferenceExpansions.Session(rel.Session, input, interpreter);
            }

            case Rel.KindOneofCase.Unnest:
            {
                var input = await EvaluateAsync(rel.Unnest.Input, path, interpreter, stats, arena, ct)
                    .ConfigureAwait(false);
                return ReferenceExpansions.Unnest(rel.Unnest, input);
            }

            case Rel.KindOneofCase.TableFunctionScan:
            {
                // D78: the host's producer, called with the same arguments and read through the same
                // accessors the vectorised operator uses.
                var scan = rel.TableFunctionScan;
                var arguments = new object?[scan.Args.Count];
                for (var i = 0; i < arguments.Length; i++)
                {
                    arguments[i] = interpreter.Evaluate(scan.Args[i], []);
                }

                var reader = interpreter.UserTable(scan.Function, arguments);
                var produced = new List<object?[]>();
                foreach (var row in reader.Rows(null))
                {
                    produced.Add(reader.Read(row));
                }

                return produced;
            }

            case Rel.KindOneofCase.SetOp:
            {
                var inputs = new List<List<object?[]>>(rel.SetOp.Inputs.Count);
                foreach (var input in rel.SetOp.Inputs)
                {
                    inputs.Add(await EvaluateAsync(input, path, interpreter, stats, arena, ct)
                        .ConfigureAwait(false));
                }

                return ReferenceExpansions.SetOp(rel.SetOp.Kind, inputs);
            }

            case Rel.KindOneofCase.PartitionedScan:
            {
                // Not a pushdown artefact: a partitioned table has this shape at every level (D106),
                // so the oracle reads one too — sequentially, in partition order, which is the whole
                // difference from the vectorised operator. The union claims no ordering, so any
                // order is correct and a deterministic one is what a differential wants.
                var partitions = new List<object?[]>();
                foreach (var partition in rel.PartitionedScan.Partitions)
                {
                    partitions.AddRange(
                        await EvaluateAsync(partition, path, interpreter, stats, arena, ct)
                            .ConfigureAwait(false));
                }

                return partitions;
            }

            case Rel.KindOneofCase.LookupJoin:
            case Rel.KindOneofCase.AdaptiveJoin:
            case Rel.KindOneofCase.MaterialisedInput:
                // The federation strategies, refused by name (D109). The reference executor is rev
                // 3's federation oracle: it pulls every remote table through its own scan path and
                // joins locally, which is what makes it an independent answer. A plan made at
                // PushdownLevel.None has no strategy in it at all — the joins are ordinary ones over
                // ordinary Reads — so one of these here means the level was wrong.
                throw new UnsupportedFeatureException(
                    rel.KindCase + " in the reference executor",
                    "The reference executor is the federation oracle and runs plans made at "
                    + "PushdownLevel.None, where no cross-source strategy is chosen at all. Plan "
                    + "with ExecutionOptions.Engine = Reference and PushdownLevel.None.");

            default:
                throw new UnsupportedFeatureException(
                    rel.KindCase.ToString(),
                    "The reference executor implements the M1 node set (docs/design/06-m1-workplan.md §4).");
        }
    }

    /// <summary>
    /// Every join but ASOF, as nested loops over both inputs with the whole condition evaluated on
    /// each candidate row (D45). Obviously correct, quadratic, and sharing nothing with the
    /// vectorised operators — which is the entire point of an oracle.
    /// </summary>
    private async ValueTask<List<object?[]>> JoinAsync(
        Rel leftRel,
        Rel rightRel,
        JoinType type,
        Func<object?[], int, bool> matches,
        string path,
        ReferenceInterpreter interpreter,
        ExecutionStats stats,
        ExecutionArena arena,
        CancellationToken ct)
    {
        var left = await EvaluateAsync(leftRel, path, interpreter, stats, arena, ct).ConfigureAwait(false);
        var right = await EvaluateAsync(rightRel, path, interpreter, stats, arena, ct).ConfigureAwait(false);
        var leftWidth = leftRel.RowType.Fields.Count;
        var rightWidth = rightRel.RowType.Fields.Count;
        var projectsRight = type is not (JoinType.Semi or JoinType.Anti);

        var rows = new List<object?[]>();
        var matchedRight = new bool[right.Count];
        foreach (var leftRow in left)
        {
            ct.ThrowIfCancellationRequested();
            var matchedLeft = false;
            for (var r = 0; r < right.Count; r++)
            {
                var joined = Concat(leftRow, right[r], leftWidth, rightWidth);
                if (!matches(joined, r))
                {
                    continue;
                }

                matchedLeft = true;
                matchedRight[r] = true;
                if (type is JoinType.Semi or JoinType.Anti)
                {
                    break;
                }

                rows.Add(joined);
            }

            switch (type)
            {
                case JoinType.Semi when matchedLeft:
                case JoinType.Anti when !matchedLeft:
                    rows.Add([.. leftRow]);
                    break;
                case JoinType.Left or JoinType.Full when !matchedLeft:
                    rows.Add(Concat(leftRow, null, leftWidth, rightWidth));
                    break;
                default:
                    break;
            }
        }

        if (projectsRight && type is JoinType.Right or JoinType.Full)
        {
            for (var r = 0; r < right.Count; r++)
            {
                if (!matchedRight[r])
                {
                    rows.Add(Concat(null, right[r], leftWidth, rightWidth));
                }
            }
        }

        return rows;
    }

    /// <summary>An equi-join, as the same nested loops with the keys as the condition.</summary>
    private ValueTask<List<object?[]>> EquiJoinAsync(
        Rel leftRel,
        Rel rightRel,
        JoinType type,
        IReadOnlyList<uint> leftKeys,
        IReadOnlyList<uint> rightKeys,
        Expr? residual,
        string path,
        ReferenceInterpreter interpreter,
        ExecutionStats stats,
        ExecutionArena arena,
        CancellationToken ct)
    {
        var leftWidth = leftRel.RowType.Fields.Count;
        return JoinAsync(
            leftRel,
            rightRel,
            type,
            (joined, _) =>
            {
                for (var k = 0; k < leftKeys.Count; k++)
                {
                    var a = joined[(int)leftKeys[k]];
                    var b = joined[leftWidth + (int)rightKeys[k]];

                    // A NULL key never matches, which is why this is not GroupEquals.
                    if (a is null || b is null || ReferenceValues.Compare(a, b) != 0)
                    {
                        return false;
                    }
                }

                return residual is null || interpreter.IsTrue(residual, joined);
            },
            path,
            interpreter,
            stats,
            arena,
            ct);
    }

    /// <summary>
    /// <c>ASOF</c>, as a direct scan: for every left row, look at every right row and keep the
    /// closest match. No index, no sort, no binary search — the only thing it shares with the
    /// operator is the semantics of D41, which is what makes disagreement meaningful.
    /// </summary>
    private async ValueTask<List<object?[]>> AsOfJoinAsync(
        AsOfJoin join,
        string path,
        ReferenceInterpreter interpreter,
        ExecutionStats stats,
        ExecutionArena arena,
        CancellationToken ct)
    {
        var left = await EvaluateAsync(join.Left, path, interpreter, stats, arena, ct).ConfigureAwait(false);
        var right = await EvaluateAsync(join.Right, path, interpreter, stats, arena, ct).ConfigureAwait(false);
        var leftWidth = join.Left.RowType.Fields.Count;
        var rightWidth = join.Right.RowType.Fields.Count;
        var backwards = join.Match is AsOfMatch.Ge or AsOfMatch.Gt;
        var strict = join.Match is AsOfMatch.Gt or AsOfMatch.Lt;

        var rows = new List<object?[]>(left.Count);
        foreach (var leftRow in left)
        {
            ct.ThrowIfCancellationRequested();
            var leftTime = leftRow[(int)join.LeftTime];
            object?[]? best = null;
            object? bestTime = null;

            if (leftTime is not null)
            {
                for (var r = 0; r < right.Count; r++)
                {
                    var rightRow = right[r];
                    var rightTime = rightRow[(int)join.RightTime];
                    if (rightTime is null || !KeysEqual(join, leftRow, rightRow))
                    {
                        continue;
                    }

                    var order = ReferenceValues.Compare(leftTime, rightTime);
                    var qualifies = backwards
                        ? (strict ? order > 0 : order >= 0)
                        : (strict ? order < 0 : order <= 0);
                    if (!qualifies)
                    {
                        continue;
                    }

                    // Closest wins; on a tie the first right row wins (D41), so a later row with the
                    // same time is never taken.
                    if (bestTime is null
                        || (backwards
                            ? ReferenceValues.Compare(rightTime, bestTime) > 0
                            : ReferenceValues.Compare(rightTime, bestTime) < 0))
                    {
                        best = rightRow;
                        bestTime = rightTime;
                    }
                }
            }

            if (best is not null)
            {
                rows.Add(Concat(leftRow, best, leftWidth, rightWidth));
            }
            else if (join.Type == JoinType.Left)
            {
                rows.Add(Concat(leftRow, null, leftWidth, rightWidth));
            }
        }

        return rows;
    }

    private static bool KeysEqual(AsOfJoin join, object?[] leftRow, object?[] rightRow)
    {
        for (var k = 0; k < join.LeftKeys.Count; k++)
        {
            var a = leftRow[(int)join.LeftKeys[k]];
            var b = rightRow[(int)join.RightKeys[k]];
            if (a is null || b is null || ReferenceValues.Compare(a, b) != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The joined row: left fields then right fields, with NULLs for the absent side.</summary>
    private static object?[] Concat(object?[]? left, object?[]? right, int leftWidth, int rightWidth)
    {
        var row = new object?[leftWidth + rightWidth];
        if (left is not null)
        {
            System.Array.Copy(left, row, leftWidth);
        }

        if (right is not null)
        {
            System.Array.Copy(right, 0, row, leftWidth, rightWidth);
        }

        return row;
    }

    /// <summary>
    /// An index lookup, run as a full scan with the ranges turned back into predicates (D39).
    ///
    /// <para>
    /// Deliberately so. The oracle's job is <em>results</em>, so it must not depend on any index
    /// implementation, least of all a host-supplied one: a lookup that returns a row the predicate
    /// would not select, or misses one it would, fails the differential test whatever the index did.
    /// What this does not check is covered elsewhere — the ordering guarantee by the ORDER BY-based
    /// comparison rules, <c>RowsScanned</c> by the instrumentation tests, and performance by nothing,
    /// on purpose. It is slower on index plans, which is irrelevant to tests.
    /// </para>
    /// </summary>
    private async ValueTask<List<object?[]>> IndexLookupAsync(
        Rel rel,
        string path,
        ReferenceInterpreter interpreter,
        ExecutionStats stats,
        ExecutionArena arena,
        CancellationToken ct)
    {
        var lookup = rel.IndexLookup;
        var table = lookup.Table;
        if (!_sources.TryGetValue(table.SourceId, out var source))
        {
            throw new UnsupportedFeatureException(
                $"source '{table.SourceId}'", "The engine holds no source with that id.");
        }

        var descriptor = _catalog.FindTable(table.Schema, table.Table)
            ?? throw new InvalidPlanException(
                "I-IR-6", path, $"the catalog has no table {table.Schema}.{table.Table}");
        var index = descriptor.Table.Indexes.FirstOrDefault(
            i => string.Equals(i.Name, lookup.Index, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidPlanException(
                "I-IR-6", path, $"table {table.Schema}.{table.Table} declares no index '{lookup.Index}'");

        // The key columns as positions in this node's own row, which is what the ranges bound.
        var projection = lookup.Projection.Select(i => (int)i).ToList();
        var keyFields = index.Columns.Select(c => projection.IndexOf(c)).ToList();

        var schema = ArrowTypeMapping.ToArrowSchema(rel.RowType);
        var types = rel.RowType.Fields.Select(f => ChalkType.FromProto(f.Type)).ToArray();
        var request = new ScanRequest
        {
            Table = descriptor.Table.Name,
            Projection = projection,
            OutputSchema = schema,
            BatchSize = Math.Max(1, _settings.BatchSize),
        };

        var scanned = new List<object?[]>();
        await foreach (var batch in source
            .ScanAsync(request, new ScanContext { Stats = stats, Arena = arena }, ct)
            .WithCancellation(ct))
        {
            try
            {
                ReferenceValues.ReadBatch(batch, types, scanned);
            }
            finally
            {
                batch.Dispose();
            }
        }

        var kept = new List<object?[]>();
        foreach (var row in scanned)
        {
            ct.ThrowIfCancellationRequested();
            if (Matches(row, keyFields, lookup.Ranges, interpreter))
            {
                kept.Add(row);
            }
        }

        if (lookup.Residual is not null)
        {
            kept = [.. kept.Where(row => interpreter.IsTrue(lookup.Residual, row))];
        }

        // "Rows come out in the index's key order" is part of what the IR node promises (02-ir.md
        // §4), and a scan-and-filter delivers the table's order instead. Sorting here keeps the
        // oracle's *results* — the rows and their order — the ones the plan claims, while still
        // depending on no index implementation at all: the key is read from the row.
        // D257: a clustered index is an ordered one with a copy of its columns beside it, so it makes
        // the same promise about the order its rows come out in.
        if (index.Kind is IndexKind.Ordered or IndexKind.Clustered && keyFields.All(f => f >= 0))
        {
            var order = index.Key()
                .Select((key, position) => new SortField
                {
                    Expr = new Expr
                    {
                        Type = rel.RowType.Fields[keyFields[position]].Type,
                        FieldRef = new FieldRef { Index = (uint)keyFields[position] },
                    },
                    Direction = key.Direction,
                })
                .ToList();
            kept.Sort(new ReferenceRowComparer(order));
        }

        return kept;
    }

    /// <summary>True when the row falls inside any range: the union the ranges stand for.</summary>
    private static bool Matches(
        object?[] row,
        IReadOnlyList<int> keyFields,
        IReadOnlyList<IndexRange> ranges,
        ReferenceInterpreter interpreter)
    {
        foreach (var range in ranges)
        {
            if (InRange(row, keyFields, range, interpreter))
            {
                return true;
            }
        }

        return false;
    }

    private static bool InRange(
        object?[] row, IReadOnlyList<int> keyFields, IndexRange range, ReferenceInterpreter interpreter)
    {
        var bounded = Math.Max(range.Lower.Count, range.Upper.Count);
        for (var i = 0; i < bounded; i++)
        {
            if (i >= keyFields.Count || keyFields[i] < 0 || row[keyFields[i]] is null)
            {
                // A NULL in a bounded column never matches: the range stands for a comparison, and a
                // comparison with NULL is unknown.
                return false;
            }
        }

        if (!Side(row, keyFields, range.Lower, range.LowerInclusive, below: true, interpreter))
        {
            return false;
        }

        return Side(row, keyFields, range.Upper, range.UpperInclusive, below: false, interpreter);
    }

    /// <summary>One side of a range, compared column by column until the first difference.</summary>
    private static bool Side(
        object?[] row,
        IReadOnlyList<int> keyFields,
        IReadOnlyList<Expr> bound,
        bool inclusive,
        bool below,
        ReferenceInterpreter interpreter)
    {
        if (bound.Count == 0)
        {
            return true; // unbounded on this side
        }

        for (var i = 0; i < bound.Count; i++)
        {
            var value = interpreter.Evaluate(bound[i], row);
            if (value is null)
            {
                return false; // a NULL bound matches nothing
            }

            var comparison = ReferenceValues.Compare(row[keyFields[i]]!, value);
            if (comparison != 0)
            {
                return below ? comparison > 0 : comparison < 0;
            }
        }

        return inclusive;
    }

    private async ValueTask<List<object?[]>> ReadAsync(
        Rel rel,
        string path,
        ExecutionStats stats,
        ExecutionArena arena,
        CancellationToken ct)
    {
        var table = rel.Read.Table;
        if (!_sources.TryGetValue(table.SourceId, out var source))
        {
            throw new UnsupportedFeatureException(
                $"source '{table.SourceId}'", "The engine holds no source with that id.");
        }

        var descriptor = _catalog.FindTable(table.Schema, table.Table)
            ?? throw new InvalidPlanException(
                "I-IR-6", path, $"the catalog has no table {table.Schema}.{table.Table}");

        var schema = ArrowTypeMapping.ToArrowSchema(rel.RowType);
        var types = rel.RowType.Fields.Select(f => ChalkType.FromProto(f.Type)).ToArray();
        var request = new ScanRequest
        {
            Table = descriptor.Table.Name,
            Projection = [.. rel.Read.Projection.Select(i => (int)i)],
            OutputSchema = schema,
            BatchSize = Math.Max(1, _settings.BatchSize),
        };

        var rows = new List<object?[]>();
        await foreach (var batch in source
            .ScanAsync(request, new ScanContext { Stats = stats, Arena = arena }, ct)
            .WithCancellation(ct))
        {
            try
            {
                ReferenceValues.ReadBatch(batch, types, rows);
            }
            finally
            {
                batch.Dispose();
            }
        }

        return rows;
    }
}

/// <summary>Row ordering for the reference <c>Sort</c> and <c>TopN</c>: a plain <c>List.Sort</c> comparer.</summary>
internal sealed class ReferenceRowComparer : IComparer<object?[]>
{
    private readonly IReadOnlyList<SortField> _fields;

    public ReferenceRowComparer(IReadOnlyList<SortField> fields) => _fields = fields;

    public int Compare(object?[]? left, object?[]? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null ? 0 : left is null ? -1 : 1;
        }

        foreach (var field in _fields)
        {
            var column = (int)field.Expr.FieldRef.Index;
            var a = left[column];
            var b = right[column];
            var nullsFirst = field.Direction
                is SortDirection.AscNullsFirst or SortDirection.DescNullsFirst;
            var descending = field.Direction
                is SortDirection.DescNullsFirst or SortDirection.DescNullsLast;

            if (a is null || b is null)
            {
                if (a is null && b is null)
                {
                    continue;
                }

                return (a is null) == nullsFirst ? -1 : 1;
            }

            var comparison = ReferenceValues.Compare(a, b);
            if (comparison != 0)
            {
                return descending ? -comparison : comparison;
            }
        }

        return 0;
    }
}
