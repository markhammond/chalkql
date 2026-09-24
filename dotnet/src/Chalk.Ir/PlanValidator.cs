using System.Globalization;
using Google.Protobuf;

namespace Chalk.Ir;

/// <summary>
/// Checks the structural invariants I-IR-1 … I-IR-23 (<c>docs/design/02-ir.md</c> §8, and the ADRs
/// that added the later ones: I-IR-16 in ADR 0021, I-IR-17 … I-IR-20 in ADR 0022, I-IR-21 … I-IR-23
/// in ADR 0077) on every plan the client receives. A failure is a contract bug in whoever produced the plan
/// (<see cref="InvalidPlanException"/>), or a version skew (<see cref="IrVersionMismatchException"/>) —
/// never a user error.
/// </summary>
public static class PlanValidator
{
    /// <summary>Validates the plan, throwing on the first violation.</summary>
    public static void Validate(Plan plan, PlanValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        new Validation(plan, options ?? PlanValidationOptions.Default).Run();
    }

    /// <summary>Function ids that take a symbolic <c>EnumArg</c> argument (I-IR-10, §5).</summary>
    private static readonly FunctionId[] EnumArgFunctions =
    [
        FunctionId.Extract,
        FunctionId.FloorTemporal,
        FunctionId.CeilTemporal,
        FunctionId.TimestampDiff,
        FunctionId.TimestampAdd,
        FunctionId.Trim,
    ];

    /// <summary>Calls whose value operands must share a <see cref="TypeKind"/> (I-IR-2, §5).</summary>
    private static readonly FunctionId[] HomogeneousFunctions =
    [
        FunctionId.Eq, FunctionId.Ne, FunctionId.Lt, FunctionId.Le, FunctionId.Gt, FunctionId.Ge,
        FunctionId.IsDistinctFrom, FunctionId.IsNotDistinctFrom,
        FunctionId.Add, FunctionId.Subtract, FunctionId.Multiply, FunctionId.Divide,
        FunctionId.Modulus, FunctionId.Power,
        FunctionId.Nullif, FunctionId.Coalesce,
    ];

    /// <summary>
    /// Arithmetic calls, whose declared result kind must be the kind their harmonised operands share
    /// (I-IR-2, §5). <c>POWER</c> is not one of them: it answers in FP64 whatever its operands are.
    /// </summary>
    private static readonly FunctionId[] ArithmeticFunctions =
    [
        FunctionId.Add, FunctionId.Subtract, FunctionId.Multiply, FunctionId.Divide,
        FunctionId.Modulus,
    ];

    private sealed class Validation(Plan plan, PlanValidationOptions options)
    {
        public void Run()
        {
            CheckVersionAndUnknownFields();

            if (plan.Root is null)
            {
                throw Invalid("I-IR-4", "plan", "the plan has no root relation");
            }

            if (plan.OutputType is null || plan.OutputType.Fields.Count == 0)
            {
                throw Invalid("I-IR-4", "plan.output_type", "output_type is missing or empty");
            }

            foreach (var (type, i) in plan.ParameterTypes.Select((t, i) => (t, i)))
            {
                CheckType(type, $"plan.parameter_types[{i}]");
                RefuseComposite(type, $"plan.parameter_types[{i}]", "a parameter's type");
            }

            var rootType = Rel(plan.Root, "root");
            NoRemoteQueryUnderANestedLoop(plan.Root, "root");

            if (!plan.OutputType.Equals(rootType))
            {
                throw Invalid(
                    "I-IR-4",
                    "plan.output_type",
                    $"output_type {IrTypes.Describe(plan.OutputType)} differs from the root's row type {IrTypes.Describe(rootType)}");
            }

            // I-IR-E, last: what the plan discloses, re-established from this client's own catalog
            // (16-entitlements.md §3.10, D201). Last because it walks column origins, which needs a
            // structurally valid plan to walk.
            EntitlementsInvariant.Check(plan, options);

            if (options.VerifyDigest)
            {
                var recomputed = PlanDigest.Compute(plan);
                if (recomputed != plan.PlanDigest)
                {
                    throw Invalid(
                        "I-IR-9",
                        "plan.plan_digest",
                        $"the plan carries digest {PlanDigest.Format(plan.PlanDigest)} but its canonical form hashes to "
                        + $"{PlanDigest.Format(recomputed)}; the planner and client disagree about the canonical encoding");
                }
            }
        }

        /// <summary>
        /// <c>I-IR-20</c>: no <c>RemoteQuery</c> on the inner side of a <c>NestedLoopJoin</c>.
        /// </summary>
        /// <remarks>
        /// The M4 assertion, made an invariant in M5 (20-m5-federation.md §3). A nested loop's inner
        /// side is the one a streaming implementation re-reads per outer row, so a remote query
        /// there is a plan whose cost is a lie however this executor happens to buffer it. The
        /// planner rewrites that shape to a LookupJoin where an equality gives it something to
        /// bind, and refuses where it cannot; this is the check that the rewrite happened.
        /// </remarks>
        private void NoRemoteQueryUnderANestedLoop(Chalk.Ir.Rel rel, string path)
        {
            var kindPath = $"{path}/{rel.KindCase}";
            if (rel.KindCase == Chalk.Ir.Rel.KindOneofCase.NestedLoopJoin
                && FetchesRemotely(rel.NestedLoopJoin.Right))
            {
                throw Invalid(
                    "I-IR-20",
                    $"{kindPath}.right",
                    "a RemoteQuery sits on a nested-loop join's inner side, which would fetch a "
                    + "source once per outer row. The planner rewrites this to a LookupJoin when the "
                    + "join has an equality between the two sides, and refuses it otherwise");
            }

            foreach (var input in PlanWalker.Inputs(rel))
            {
                NoRemoteQueryUnderANestedLoop(input, kindPath);
            }
        }

        /// <summary>Whether reading this subtree means asking a source for rows.</summary>
        private static bool FetchesRemotely(Chalk.Ir.Rel? rel)
        {
            if (rel is null)
            {
                return false;
            }

            if (rel.KindCase == Chalk.Ir.Rel.KindOneofCase.RemoteQuery)
            {
                return true;
            }

            foreach (var input in PlanWalker.Inputs(rel))
            {
                if (FetchesRemotely(input))
                {
                    return true;
                }
            }

            return false;
        }

        private void CheckVersionAndUnknownFields()
        {
            if (plan.IrVersion > IrVersion.Current)
            {
                throw new IrVersionMismatchException(
                    plan.IrVersion,
                    IrVersion.Current,
                    "The plan uses a node or field introduced after this client's IR version.");
            }

            if (plan.IrVersion < IrVersion.Minimum)
            {
                throw Invalid(
                    "I-IR-1",
                    "plan.ir_version",
                    $"ir_version is {plan.IrVersion}; this client reads {IrVersion.Minimum}..{IrVersion.Current}");
            }

            // An unknown field on a known message is also "newer IR" (02-ir.md §2 rule 2). Google.Protobuf
            // keeps unknown fields on the message and round-trips them, so re-parsing with them discarded
            // and comparing is the cheapest faithful detection.
            var stripped = Plan.Parser.WithDiscardUnknownFields(true).ParseFrom(plan.ToByteArray());
            if (!stripped.Equals(plan))
            {
                throw new IrVersionMismatchException(
                    plan.IrVersion,
                    IrVersion.Current,
                    "The plan carries fields this client does not know.");
            }
        }

        /// <summary>Validates the relation and returns its output row type.</summary>
        private RowType Rel(Rel rel, string path)
        {
            if (rel.KindCase == Chalk.Ir.Rel.KindOneofCase.None)
            {
                throw new IrVersionMismatchException(
                    plan.IrVersion,
                    IrVersion.Current,
                    $"The relation at {path} has a node kind this client does not know.");
            }

            if (rel.RowType is null || rel.RowType.Fields.Count == 0)
            {
                throw Invalid("I-IR-4", path, $"{rel.KindCase} has no row_type");
            }

            foreach (var (field, i) in rel.RowType.Fields.Select((f, i) => (f, i)))
            {
                CheckType(field.Type, $"{path}.row_type[{i}] ({field.Name})");
            }

            if (rel.EstRowCount < 0 || double.IsNaN(rel.EstRowCount))
            {
                throw Invalid(
                    "I-IR-4",
                    path,
                    $"est_row_count is {rel.EstRowCount.ToString(CultureInfo.InvariantCulture)}; it must be >= 0");
            }

            var kindPath = $"{path}/{rel.KindCase}";
            var output = rel.RowType;

            switch (rel.KindCase)
            {
                case Chalk.Ir.Rel.KindOneofCase.Read:
                    Read(rel, kindPath);
                    RefuseCompositeColumns(output, kindPath, "a table column");
                    break;

                case Chalk.Ir.Rel.KindOneofCase.VirtualTable:
                    VirtualTable(rel, kindPath);

                    // A VALUES row is literals, and no literal is a composite value. An empty one
                    // holds none: it is what the planner leaves where it proved a relation empty
                    // — a predicate that folds to FALSE, a principal who may see no row — and its
                    // row type is still the statement's, a composite column included (ADR 0077).
                    if (rel.VirtualTable.Rows.Count > 0)
                    {
                        RefuseCompositeColumns(output, kindPath, "a VALUES column");
                    }

                    break;

                // A relation the host bound by name (step 26, 16-entitlements.md §2, §4). Its rows
                // are deliberately not in the plan — that is the point of it — so all there is to
                // check is that the plan says which binding, and what shape it must have.
                case Chalk.Ir.Rel.KindOneofCase.BoundTable:
                    if (rel.BoundTable.Name.Length == 0)
                    {
                        throw Invalid("I-IR-1", kindPath, "ContextTable names no binding");
                    }

                    if (output.Fields.Count == 0)
                    {
                        throw Invalid(
                            "I-IR-4",
                            kindPath,
                            $"ContextTable '{rel.BoundTable.Name}' declares no columns; the "
                            + "executor binds the host's rows against this row type");
                    }

                    RefuseCompositeColumns(output, kindPath, "a bound table's column");
                    break;

                case Chalk.Ir.Rel.KindOneofCase.Filter:
                {
                    var input = Rel(rel.Filter.Input, kindPath);
                    Expression(rel.Filter.Condition, input, $"{kindPath}.condition");
                    RequireKind(rel.Filter.Condition, TypeKind.Bool, $"{kindPath}.condition");
                    RequireSameRow(output, input, kindPath, "Filter output equals its input");
                    break;
                }

                case Chalk.Ir.Rel.KindOneofCase.Project:
                {
                    var input = Rel(rel.Project.Input, kindPath);
                    if (rel.Project.Exprs.Count != output.Fields.Count)
                    {
                        throw Invalid(
                            "I-IR-4",
                            kindPath,
                            $"Project has {rel.Project.Exprs.Count} expressions but a row type of {output.Fields.Count} fields");
                    }

                    for (var i = 0; i < rel.Project.Exprs.Count; i++)
                    {
                        var e = rel.Project.Exprs[i];
                        Expression(e, input, $"{kindPath}.exprs[{i}]");
                        RequireSameType(output.Fields[i].Type, e.Type, $"{kindPath}.exprs[{i}]", "the projected field type");
                    }

                    break;
                }

                case Chalk.Ir.Rel.KindOneofCase.Aggregate:
                    AggregateNode(rel.Aggregate, output, kindPath);
                    break;

                case Chalk.Ir.Rel.KindOneofCase.HashAggregate:
                    AggregateNode(rel.HashAggregate.Aggregate, output, kindPath);
                    break;

                case Chalk.Ir.Rel.KindOneofCase.StreamAggregate:
                    AggregateNode(rel.StreamAggregate.Aggregate, output, kindPath);
                    break;

                case Chalk.Ir.Rel.KindOneofCase.Sort:
                {
                    var input = Rel(rel.Sort.Input, kindPath);
                    if (rel.Sort.Fields.Count == 0)
                    {
                        throw Invalid("I-IR-5", kindPath, "Sort has no sort fields");
                    }

                    SortFields(rel.Sort.Fields, input, $"{kindPath}.fields");
                    RequireSameRow(output, input, kindPath, "Sort output equals its input");
                    break;
                }

                case Chalk.Ir.Rel.KindOneofCase.TopN:
                {
                    var input = Rel(rel.TopN.Input, kindPath);
                    if (rel.TopN.Fields.Count == 0)
                    {
                        throw Invalid("I-IR-5", kindPath, "TopN has no sort fields");
                    }

                    SortFields(rel.TopN.Fields, input, $"{kindPath}.fields");
                    if (rel.TopN.Count < 0)
                    {
                        throw Invalid("I-IR-5", kindPath, $"TopN.count is {rel.TopN.Count}; it must be >= 0");
                    }

                    if (rel.TopN.Offset < 0)
                    {
                        throw Invalid("I-IR-5", kindPath, $"TopN.offset is {rel.TopN.Offset}; it must be >= 0");
                    }

                    OneBound(rel.TopN.CountParam is not null, rel.TopN.Count != 0, kindPath, "TopN", "count");
                    OneBound(rel.TopN.OffsetParam is not null, rel.TopN.Offset != 0, kindPath, "TopN", "offset");
                    RequireSameRow(output, input, kindPath, "TopN output equals its input");
                    break;
                }

                case Chalk.Ir.Rel.KindOneofCase.Fetch:
                {
                    var input = Rel(rel.Fetch.Input, kindPath);
                    if (rel.Fetch.Offset < 0)
                    {
                        throw Invalid("I-IR-5", kindPath, $"Fetch.offset is {rel.Fetch.Offset}; it must be >= 0");
                    }

                    if (rel.Fetch.HasCount && rel.Fetch.Count < 0)
                    {
                        throw Invalid("I-IR-5", kindPath, $"Fetch.count is {rel.Fetch.Count}; it must be >= 0 when set");
                    }

                    OneBound(rel.Fetch.CountParam is not null, rel.Fetch.HasCount, kindPath, "Fetch", "count");
                    OneBound(rel.Fetch.OffsetParam is not null, rel.Fetch.Offset != 0, kindPath, "Fetch", "offset");
                    RequireSameRow(output, input, kindPath, "Fetch output equals its input");
                    break;
                }

                case Chalk.Ir.Rel.KindOneofCase.Join:
                {
                    var left = Rel(rel.Join.Left, $"{kindPath}.left");
                    var right = Rel(rel.Join.Right, $"{kindPath}.right");
                    var joined = Concat(left, right);
                    if (rel.Join.Condition is not null)
                    {
                        Expression(rel.Join.Condition, joined, $"{kindPath}.condition");
                        RequireKind(rel.Join.Condition, TypeKind.Bool, $"{kindPath}.condition");
                    }

                    break;
                }

                case Chalk.Ir.Rel.KindOneofCase.HashJoin:
                    EquiJoin(
                        Rel(rel.HashJoin.Left, $"{kindPath}.left"),
                        Rel(rel.HashJoin.Right, $"{kindPath}.right"),
                        rel.HashJoin.LeftKeys,
                        rel.HashJoin.RightKeys,
                        rel.HashJoin.PostJoinFilter,
                        kindPath);
                    break;

                case Chalk.Ir.Rel.KindOneofCase.MergeJoin:
                {
                    EquiJoin(
                        Rel(rel.MergeJoin.Left, $"{kindPath}.left"),
                        Rel(rel.MergeJoin.Right, $"{kindPath}.right"),
                        rel.MergeJoin.LeftKeys,
                        rel.MergeJoin.RightKeys,
                        rel.MergeJoin.PostJoinFilter,
                        kindPath);

                    // A merge join streams two sorted runs, so the claim that they are sorted has to
                    // be in the plan and not only in the planner's head (12-joins.md §2).
                    RequireSortedOn(rel.MergeJoin.Left, rel.MergeJoin.LeftKeys, $"{kindPath}.left");
                    RequireSortedOn(rel.MergeJoin.Right, rel.MergeJoin.RightKeys, $"{kindPath}.right");
                    break;
                }

                case Chalk.Ir.Rel.KindOneofCase.NestedLoopJoin:
                {
                    var left = Rel(rel.NestedLoopJoin.Left, $"{kindPath}.left");
                    var right = Rel(rel.NestedLoopJoin.Right, $"{kindPath}.right");
                    if (rel.NestedLoopJoin.Condition is not null)
                    {
                        Expression(rel.NestedLoopJoin.Condition, Concat(left, right), $"{kindPath}.condition");
                        RequireKind(rel.NestedLoopJoin.Condition, TypeKind.Bool, $"{kindPath}.condition");
                    }

                    break;
                }

                case Chalk.Ir.Rel.KindOneofCase.AsOfJoin:
                {
                    var left = Rel(rel.AsOfJoin.Left, $"{kindPath}.left");
                    var right = Rel(rel.AsOfJoin.Right, $"{kindPath}.right");
                    EquiJoin(
                        left,
                        right,
                        rel.AsOfJoin.LeftKeys,
                        rel.AsOfJoin.RightKeys,
                        residual: null,
                        kindPath);

                    for (var i = 0; i < rel.AsOfJoin.LeftKeys.Count; i++)
                    {
                        RequireSameKind(
                            left.Fields[(int)rel.AsOfJoin.LeftKeys[i]].Type,
                            right.Fields[(int)rel.AsOfJoin.RightKeys[i]].Type,
                            $"{kindPath}.left_keys[{i}]",
                            "the right key it is compared with");
                    }

                    RequireIndex(rel.AsOfJoin.LeftTime, left, "I-IR-3", $"{kindPath}.left_time");
                    RequireIndex(rel.AsOfJoin.RightTime, right, "I-IR-3", $"{kindPath}.right_time");

                    // The time columns are compared as an ordering, which neither a list nor a
                    // composite has (I-IR-12, I-IR-23; found by the executor's backstop, D297).
                    RefuseNonScalar(
                        left.Fields[(int)rel.AsOfJoin.LeftTime].Type,
                        $"{kindPath}.left_time",
                        "an as-of join's time column");
                    RefuseNonScalar(
                        right.Fields[(int)rel.AsOfJoin.RightTime].Type,
                        $"{kindPath}.right_time",
                        "an as-of join's time column");
                    RequireSameKind(
                        left.Fields[(int)rel.AsOfJoin.LeftTime].Type,
                        right.Fields[(int)rel.AsOfJoin.RightTime].Type,
                        $"{kindPath}.left_time",
                        "the right time column it is compared with");

                    if (rel.AsOfJoin.Match == AsOfMatch.Unspecified)
                    {
                        throw Invalid("I-IR-5", kindPath, "AsOfJoin.match is unspecified");
                    }

                    if (rel.AsOfJoin.Type is not (JoinType.Inner or JoinType.Left))
                    {
                        throw Invalid(
                            "I-IR-5",
                            kindPath,
                            $"AsOfJoin.type is {rel.AsOfJoin.Type}; only INNER and LEFT exist");
                    }

                    break;
                }

                case Chalk.Ir.Rel.KindOneofCase.SetOp:
                {
                    if (rel.SetOp.Inputs.Count < 2)
                    {
                        throw Invalid("I-IR-4", kindPath, $"SetOp has {rel.SetOp.Inputs.Count} inputs; it needs at least 2");
                    }

                    for (var i = 0; i < rel.SetOp.Inputs.Count; i++)
                    {
                        var input = Rel(rel.SetOp.Inputs[i], $"{kindPath}.inputs[{i}]");
                        RequireSetOpRow(output, input, $"{kindPath}.inputs[{i}]");
                    }

                    // Every form but UNION ALL compares whole rows, which makes each column a
                    // DISTINCT key: neither a list nor a composite has an equality to compare them by
                    // (I-IR-12, I-IR-23).
                    if (rel.SetOp.Kind != SetOpKind.UnionAll)
                    {
                        for (var i = 0; i < output.Fields.Count; i++)
                        {
                            RefuseNonScalar(
                                output.Fields[i].Type,
                                $"{kindPath}.row_type[{i}] ({output.Fields[i].Name})",
                                $"compared by {SetOpName(rel.SetOp.Kind)}");
                        }
                    }

                    break;
                }

                case Chalk.Ir.Rel.KindOneofCase.Window:
                    WindowNode(rel.Window, output, kindPath);
                    break;

                case Chalk.Ir.Rel.KindOneofCase.Hop:
                {
                    var input = Rel(rel.Hop.Input, kindPath);
                    RequireIndex(rel.Hop.TimeColumn, input, "I-IR-3", $"{kindPath}.time_column");
                    WindowBounds(
                        input,
                        output,
                        rel.Hop.TimeColumn,
                        kindPath,
                        ("slide", rel.Hop.Slide),
                        ("size", rel.Hop.Size));
                    break;
                }

                case Chalk.Ir.Rel.KindOneofCase.Session:
                {
                    var input = Rel(rel.Session.Input, kindPath);
                    for (var i = 0; i < rel.Session.PartitionKeys.Count; i++)
                    {
                        RequireIndex(
                            rel.Session.PartitionKeys[i],
                            input,
                            "I-IR-3",
                            $"{kindPath}.partition_keys[{i}]");
                        RefuseNonScalar(
                            input.Fields[(int)rel.Session.PartitionKeys[i]].Type,
                            $"{kindPath}.partition_keys[{i}]",
                            "partitioned by");
                    }

                    RequireIndex(rel.Session.TimeColumn, input, "I-IR-3", $"{kindPath}.time_column");
                    WindowBounds(
                        input, output, rel.Session.TimeColumn, kindPath, ("gap", rel.Session.Gap));
                    break;
                }

                case Chalk.Ir.Rel.KindOneofCase.Unnest:
                    UnnestNode(rel.Unnest, output, kindPath);
                    break;

                case Chalk.Ir.Rel.KindOneofCase.TableFunctionScan:
                    TableFunctionScanNode(rel.TableFunctionScan, output, kindPath);
                    RefuseCompositeColumns(output, kindPath, "a table function's column");
                    break;

                case Chalk.Ir.Rel.KindOneofCase.IndexLookup:
                    IndexLookup(rel, kindPath);
                    RefuseCompositeColumns(output, kindPath, "a table column");
                    break;

                case Chalk.Ir.Rel.KindOneofCase.RemoteQuery:
                    // One of the two flavours must be there (D84). A SQL source carries both, so
                    // the query text can be reversed to the algebra it came from; an IR source
                    // carries only the subtree, which is what it interprets.
                    if (rel.RemoteQuery.QueryText.Length == 0 && rel.RemoteQuery.PushedPlan is null)
                    {
                        throw Invalid(
                            "I-IR-4",
                            kindPath,
                            "RemoteQuery has neither query_text nor pushed_plan, so there is "
                            + "nothing for the source to run");
                    }

                    if (rel.RemoteQuery.SourceId.Length == 0)
                    {
                        throw Invalid("I-IR-4", kindPath, "RemoteQuery.source_id is empty");
                    }

                    RenderedBounds(rel.RemoteQuery, kindPath);
                    RefuseCompositeColumns(output, kindPath, "a column a source returns");
                    break;

                case Chalk.Ir.Rel.KindOneofCase.LookupJoin:
                    LookupJoin(rel.LookupJoin, rel.RowType, kindPath);
                    break;

                case Chalk.Ir.Rel.KindOneofCase.AdaptiveJoin:
                    AdaptiveJoin(rel, kindPath);
                    break;

                case Chalk.Ir.Rel.KindOneofCase.MaterialisedInput:
                    // A leaf whose row type is the materialisation's. Whether the slot exists is a
                    // whole-plan question: I-IR-18, checked once the enclosing AdaptiveJoin is known.
                    break;

                case Chalk.Ir.Rel.KindOneofCase.PartitionedScan:
                    PartitionedScan(rel, kindPath);
                    break;

                case Chalk.Ir.Rel.KindOneofCase.None:
                default:
                    throw Invalid("I-IR-1", kindPath, $"unhandled relation kind {rel.KindCase}");
            }

            // Collations address the node's own output row (02-ir.md §4).
            for (var c = 0; c < rel.Collations.Count; c++)
            {
                var collation = rel.Collations[c];
                if (collation.Fields.Count == 0)
                {
                    throw Invalid("I-IR-5", $"{kindPath}.collations[{c}]", "a collation has no fields");
                }

                SortFields(collation.Fields, output, $"{kindPath}.collations[{c}]");
            }

            return output;
        }

        private void Read(Rel rel, string path)
        {
            if (rel.Read.Table is null || rel.Read.Table.Table.Length == 0)
            {
                throw Invalid("I-IR-6", path, "Read has no table reference");
            }

            if (rel.Read.Projection.Count == 0)
            {
                throw Invalid("I-IR-6", path, "Read.projection is empty; a Read always outputs at least one column");
            }

            if (rel.Read.Projection.Count != rel.RowType.Fields.Count)
            {
                throw Invalid(
                    "I-IR-6",
                    path,
                    $"Read.projection has {rel.Read.Projection.Count} entries but the row type has {rel.RowType.Fields.Count} fields");
            }

            if (rel.Read.Filter is not null && !options.AllowReadFilter)
            {
                throw Invalid(
                    "I-IR-6",
                    path,
                    "Read.filter is set, but the source did not declare a matching pushdown capability "
                    + "(an M1-M3 planner never sets it)");
            }
        }

        /// <summary>
        /// <c>I-IR-4</c>: every <c>rendered_bounds</c> position names a parameter entry, once.
        /// </summary>
        /// <remarks>
        /// A rendered bound is the one placeholder the provider is never asked to bind: the
        /// executor reads its value when the execution starts and writes the number into the query
        /// text in its place. So a position out of range, or one naming an entry that is not a
        /// <c>DynamicParam</c>, is a plan whose text and parameter list cannot be lined up at all —
        /// and a position named twice says to render one placeholder for two different reasons.
        /// All three are refused here rather than at the first row.
        /// </remarks>
        private void RenderedBounds(Chalk.Ir.RemoteQuery query, string path)
        {
            var last = -1;
            foreach (var position in query.RenderedBounds)
            {
                if (position >= query.Parameters.Count)
                {
                    throw Invalid(
                        "I-IR-4",
                        path,
                        $"RemoteQuery.rendered_bounds names placeholder {position} and the query has "
                        + $"{query.Parameters.Count}");
                }

                if (query.Parameters[(int)position].KindCase != Expr.KindOneofCase.Param)
                {
                    throw Invalid(
                        "I-IR-4",
                        path,
                        $"RemoteQuery.rendered_bounds names placeholder {position}, which is a "
                        + $"{query.Parameters[(int)position].KindCase}; a rendered bound is the "
                        + "DynamicParam the executor reads the count from");
                }

                if ((int)position <= last)
                {
                    throw Invalid(
                        "I-IR-4",
                        path,
                        $"RemoteQuery.rendered_bounds is {string.Join(", ", query.RenderedBounds)}; "
                        + "the positions are distinct and ascending");
                }

                last = (int)position;
            }
        }

        /// <summary>
        /// The shape D37 fixes: bounds over a prefix of the key, all but the last column an
        /// equality, and the projection matching the row type. A range whose bounds disagree
        /// anywhere but in their last element would name a set of rows no index can produce.
        /// </summary>
        private void IndexLookup(Rel rel, string path)
        {
            var lookup = rel.IndexLookup;
            if (lookup.Table is null || lookup.Table.Table.Length == 0)
            {
                throw Invalid("I-IR-6", path, "IndexLookup has no table reference");
            }

            if (lookup.Index.Length == 0)
            {
                throw Invalid("I-IR-6", path, "IndexLookup.index names no index");
            }

            if (lookup.Projection.Count == 0)
            {
                throw Invalid("I-IR-6", path, "IndexLookup.projection is empty");
            }

            if (lookup.Projection.Count != rel.RowType.Fields.Count)
            {
                throw Invalid(
                    "I-IR-6",
                    path,
                    $"IndexLookup.projection has {lookup.Projection.Count} entries but the row type has "
                    + $"{rel.RowType.Fields.Count} fields");
            }

            for (var r = 0; r < lookup.Ranges.Count; r++)
            {
                var range = lookup.Ranges[r];
                var rangePath = $"{path}.ranges[{r}]";
                if (range.Lower.Count == 0 && range.Upper.Count == 0)
                {
                    continue; // the whole index, in key order
                }

                if (range.Prefix)
                {
                    // A prefix range is the equality prefix and then a LIKE pattern, open above: the
                    // bound it finally becomes depends on the index's kind and is resolved when the
                    // bounds are bound (D282).
                    if (range.Upper.Count != 0)
                    {
                        throw Invalid(
                            "I-IR-6",
                            rangePath,
                            $"the range is a prefix but bounds {range.Upper.Count} key column(s) above; "
                            + "a prefix names its own upper bound and the range is open there");
                    }

                    foreach (var bound in range.Lower)
                    {
                        Expression(bound, rel.RowType, rangePath);
                    }

                    RequireKind(range.Lower[^1], TypeKind.String, rangePath);
                    continue;
                }

                if (Math.Abs(range.Lower.Count - range.Upper.Count) > 1)
                {
                    throw Invalid(
                        "I-IR-6",
                        rangePath,
                        $"the bounds cover {range.Lower.Count} and {range.Upper.Count} key columns; "
                        + "only the last bounded column may be open on one side");
                }

                var shared = Math.Min(range.Lower.Count, range.Upper.Count);
                var equalities = Math.Max(range.Lower.Count, range.Upper.Count) - 1;
                for (var i = 0; i < shared && i < equalities; i++)
                {
                    if (!range.Lower[i].Equals(range.Upper[i]))
                    {
                        throw Invalid(
                            "I-IR-6",
                            rangePath,
                            $"key column {i} is not the last bounded one, so both bounds must be the same "
                            + "expression; an index cannot answer a range on a column another range is open on");
                    }
                }

                foreach (var bound in range.Lower.Concat(range.Upper))
                {
                    Expression(bound, rel.RowType, rangePath);
                }
            }

            if (lookup.Residual is not null)
            {
                Expression(lookup.Residual, rel.RowType, $"{path}.residual");
                RequireKind(lookup.Residual, TypeKind.Bool, $"{path}.residual");
            }
        }

        private void VirtualTable(Rel rel, string path)
        {
            var fields = rel.RowType.Fields;
            for (var r = 0; r < rel.VirtualTable.Rows.Count; r++)
            {
                var row = rel.VirtualTable.Rows[r];
                if (row.Values.Count != fields.Count)
                {
                    throw Invalid(
                        "I-IR-4",
                        $"{path}.rows[{r}]",
                        $"row has {row.Values.Count} values but the row type has {fields.Count} fields");
                }

                for (var i = 0; i < row.Values.Count; i++)
                {
                    var value = row.Values[i];
                    var valuePath = $"{path}.rows[{r}][{i}]";
                    if (value.KindCase != Expr.KindOneofCase.Literal)
                    {
                        throw Invalid("I-IR-4", valuePath, $"VirtualTable values are literals; found {value.KindCase}");
                    }

                    Expression(value, EmptyRow, valuePath);
                    RequireSameType(fields[i].Type, value.Type, valuePath, "the VirtualTable field type");
                }
            }
        }

        private void AggregateNode(Aggregate aggregate, RowType output, string path)
        {
            var input = Rel(aggregate.Input, path);

            if (aggregate.Groupings.Count != 1)
            {
                throw Invalid(
                    "I-IR-7",
                    path,
                    $"the aggregate has {aggregate.Groupings.Count} groupings; v1 requires exactly one "
                    + "(grouping sets are reserved)");
            }

            var keys = aggregate.Groupings[0].Keys;
            for (var i = 0; i < keys.Count; i++)
            {
                RequireIndex(keys[i], input, "I-IR-3", $"{path}.groupings[0].keys[{i}]");
                RefuseNonScalar(
                    input.Fields[(int)keys[i]].Type,
                    $"{path}.groupings[0].keys[{i}]",
                    "grouped by");
            }

            if (output.Fields.Count != keys.Count + aggregate.Measures.Count)
            {
                throw Invalid(
                    "I-IR-7",
                    path,
                    $"the aggregate's row type has {output.Fields.Count} fields but {keys.Count} keys "
                    + $"and {aggregate.Measures.Count} measures");
            }

            for (var i = 0; i < keys.Count; i++)
            {
                RequireSameType(
                    output.Fields[i].Type,
                    input.Fields[(int)keys[i]].Type,
                    $"{path}.row_type[{i}]",
                    $"the type of grouping key ${keys[i]}");
            }

            for (var m = 0; m < aggregate.Measures.Count; m++)
            {
                var measure = aggregate.Measures[m];
                var measurePath = $"{path}.measures[{m}]";
                CheckType(measure.Type, $"{measurePath}.type");
                for (var a = 0; a < measure.Args.Count; a++)
                {
                    Expression(measure.Args[a], input, $"{measurePath}.args[{a}]");
                    RefuseComposite(
                        measure.Args[a].Type,
                        $"{measurePath}.args[{a}]",
                        measure.UserFunction.Length > 0
                            ? "a function's argument"
                            : "a built-in aggregate's argument");
                }

                if (measure.UserFunction.Length == 0)
                {
                    RefuseComposite(measure.Type, $"{measurePath}.type", "a built-in aggregate's result");
                }

                if (measure.Filter is not null)
                {
                    Expression(measure.Filter, input, $"{measurePath}.filter");
                    RequireKind(measure.Filter, TypeKind.Bool, $"{measurePath}.filter");
                }

                if (measure.UserFunction.Length > 0
                    && measure.Function != AggregateFunctionId.Unspecified)
                {
                    throw Invalid(
                        "I-IR-16",
                        measurePath,
                        $"the measure names both the built-in {measure.Function} and the user "
                        + $"aggregate '{measure.UserFunction}'");
                }

                OrderBy(measure.OrderBy, measure.Function, input, $"{measurePath}.order_by");
                if (measure.Distinct && measure.Function == AggregateFunctionId.Count
                    && measure.Args.Count == 0)
                {
                    throw Invalid("I-IR-7", measurePath, "COUNT(DISTINCT) needs an argument");
                }

                RequireSameType(
                    output.Fields[keys.Count + m].Type,
                    measure.Type,
                    $"{path}.row_type[{keys.Count + m}]",
                    "the measure's result type");
            }
        }

        /// <summary>
        /// The <c>WITHIN GROUP (ORDER BY …)</c> ordering of a holistic aggregate (D57): field
        /// references into the aggregate's input row, and only on an aggregate that has one.
        /// </summary>
        private void OrderBy(
            IReadOnlyList<SortField> orderBy, AggregateFunctionId function, RowType input, string path)
        {
            if (orderBy.Count == 0)
            {
                return;
            }

            if (!Ordered.Contains(function))
            {
                throw Invalid(
                    "I-IR-13",
                    path,
                    $"{function} carries an order_by; only {string.Join(", ", Ordered)} take one");
            }

            SortFields(orderBy, input, path);
        }

        /// <summary>The aggregates whose answer depends on an order (D57).</summary>
        private static readonly AggregateFunctionId[] Ordered =
        [
            AggregateFunctionId.PercentileCont,
            AggregateFunctionId.PercentileDisc,
            AggregateFunctionId.Listagg,
            AggregateFunctionId.StringAgg,
            AggregateFunctionId.ArrayAgg,
        ];

        /// <summary>
        /// The window shape D48 fixes (<c>13-window-functions.md</c> §2): frames are always
        /// explicit, a <c>RANGE</c> frame with an offset has exactly one order key, offsets are
        /// constants or parameters, and the output row is the input's fields followed by one column
        /// per call.
        /// </summary>
        private void WindowNode(Window window, RowType output, string path)
        {
            var input = Rel(window.Input, path);

            for (var i = 0; i < window.PartitionKeys.Count; i++)
            {
                RequireIndex(window.PartitionKeys[i], input, "I-IR-3", $"{path}.partition_keys[{i}]");
                RefuseNonScalar(
                    input.Fields[(int)window.PartitionKeys[i]].Type,
                    $"{path}.partition_keys[{i}]",
                    "partitioned by");
            }

            SortFields(window.Order, input, $"{path}.order");

            if (window.Calls.Count == 0)
            {
                throw Invalid("I-IR-11", path, "a Window has no calls");
            }

            if (window.Frame is null)
            {
                throw Invalid(
                    "I-IR-11",
                    path,
                    "a Window has no frame; the planner resolves SQL's defaults and emits the bounds "
                    + "it resolved, never 'default'");
            }

            Frame(window, input, $"{path}.frame");

            if (output.Fields.Count != input.Fields.Count + window.Calls.Count)
            {
                throw Invalid(
                    "I-IR-11",
                    path,
                    $"the window's row type has {output.Fields.Count} fields but its input has "
                    + $"{input.Fields.Count} and it makes {window.Calls.Count} call(s)");
            }

            for (var i = 0; i < input.Fields.Count; i++)
            {
                RequireSameType(
                    output.Fields[i].Type,
                    input.Fields[i].Type,
                    $"{path}.row_type[{i}]",
                    "the input field it carries through");
            }

            for (var c = 0; c < window.Calls.Count; c++)
            {
                var call = window.Calls[c];
                var callPath = $"{path}.calls[{c}]";
                if (call.UserFunction.Length > 0
                    && call.FunctionCase != WindowCall.FunctionOneofCase.None)
                {
                    throw Invalid(
                        "I-IR-16",
                        callPath,
                        $"the window call names both a built-in and the user aggregate "
                        + $"'{call.UserFunction}'");
                }

                if (call.UserFunction.Length == 0
                    && call.FunctionCase == WindowCall.FunctionOneofCase.None)
                {
                    throw new IrVersionMismatchException(
                        plan.IrVersion,
                        IrVersion.Current,
                        $"The window call at {callPath} names a function this client does not know.");
                }

                if (call.FunctionCase == WindowCall.FunctionOneofCase.Aggregate
                    && call.Aggregate == AggregateFunctionId.Unspecified)
                {
                    throw new IrVersionMismatchException(
                        plan.IrVersion,
                        IrVersion.Current,
                        $"The window call at {callPath} names an aggregate this client does not know.");
                }

                if (call.FunctionCase == WindowCall.FunctionOneofCase.WindowFunction
                    && call.WindowFunction == WindowFunctionId.Unspecified)
                {
                    throw new IrVersionMismatchException(
                        plan.IrVersion,
                        IrVersion.Current,
                        $"The window call at {callPath} names a function this client does not know.");
                }

                CheckType(call.Type, $"{callPath}.type");
                for (var a = 0; a < call.Args.Count; a++)
                {
                    Expression(call.Args[a], input, $"{callPath}.args[{a}]");
                    RefuseComposite(
                        call.Args[a].Type,
                        $"{callPath}.args[{a}]",
                        call.UserFunction.Length > 0
                            ? "a function's argument"
                            : "a built-in window function's argument");
                }

                if (call.UserFunction.Length == 0)
                {
                    RefuseComposite(call.Type, $"{callPath}.type", "a built-in window function's result");
                }

                if (call.IgnoreNulls
                    && call.FunctionCase != WindowCall.FunctionOneofCase.WindowFunction)
                {
                    throw Invalid(
                        "I-IR-11",
                        callPath,
                        "ignore_nulls is only defined for the navigation functions");
                }

                if (call.Distinct && call.FunctionCase != WindowCall.FunctionOneofCase.Aggregate)
                {
                    throw Invalid("I-IR-11", callPath, "distinct is only defined for an aggregate call");
                }

                if (call.OrderBy.Count > 0
                    && call.FunctionCase != WindowCall.FunctionOneofCase.Aggregate)
                {
                    throw Invalid("I-IR-11", callPath, "order_by is only defined for an aggregate call");
                }

                OrderBy(
                    call.OrderBy,
                    call.FunctionCase == WindowCall.FunctionOneofCase.Aggregate
                        ? call.Aggregate
                        : AggregateFunctionId.Unspecified,
                    input,
                    $"{callPath}.order_by");

                RequireSameType(
                    output.Fields[input.Fields.Count + c].Type,
                    call.Type,
                    $"{path}.row_type[{input.Fields.Count + c}]",
                    "the call's result type");
            }
        }

        /// <summary>
        /// The shape <c>Hop</c> and <c>Session</c> share (D55): a temporal time column, positive
        /// interval constants, and an output row that is the input's fields plus
        /// <c>window_start</c> and <c>window_end</c> of the time column's type.
        /// </summary>
        private void WindowBounds(
            RowType input,
            RowType output,
            uint timeColumn,
            string path,
            params (string Name, Expr? Value)[] intervals)
        {
            var time = input.Fields[(int)timeColumn].Type!;
            if (!IrTypes.IsTemporal(time.Kind) || time.Kind == TypeKind.Time)
            {
                throw Invalid(
                    "I-IR-14",
                    $"{path}.time_column",
                    $"the time column is {IrTypes.Describe(time)}; a window needs DATE, TIMESTAMP or "
                    + "TIMESTAMP_TZ");
            }

            foreach (var (name, value) in intervals)
            {
                var argPath = $"{path}.{name}";
                if (value is null)
                {
                    throw Invalid("I-IR-14", argPath, $"{name} is missing");
                }

                Expression(value, input, argPath);
                RequireKind(value, TypeKind.IntervalDay, argPath);
                if (value.KindCase is not (Expr.KindOneofCase.Literal or Expr.KindOneofCase.Param))
                {
                    throw Invalid(
                        "I-IR-14",
                        argPath,
                        $"{name} is a {value.KindCase}; it must be a constant or a parameter");
                }

                if (value.KindCase == Expr.KindOneofCase.Literal
                    && value.Literal.ValueCase == Literal.ValueOneofCase.IntervalDayValue
                    && value.Literal.IntervalDayValue <= 0)
                {
                    throw Invalid(
                        "I-IR-14",
                        argPath,
                        $"{name} is {value.Literal.IntervalDayValue} microseconds; it must be positive");
                }
            }

            if (output.Fields.Count != input.Fields.Count + 2)
            {
                throw Invalid(
                    "I-IR-14",
                    path,
                    $"the row type has {output.Fields.Count} fields but its input has "
                    + $"{input.Fields.Count} and a window adds window_start and window_end");
            }

            for (var i = 0; i < input.Fields.Count; i++)
            {
                RequireSameType(
                    output.Fields[i].Type,
                    input.Fields[i].Type,
                    $"{path}.row_type[{i}]",
                    "the input field it carries through");
            }

            for (var i = input.Fields.Count; i < output.Fields.Count; i++)
            {
                RequireSameKind(
                    output.Fields[i].Type,
                    time,
                    $"{path}.row_type[{i}]",
                    "the time column's type");
            }
        }

        /// <summary>
        /// <c>TableFunctionScan</c> (D78): a leaf. Its arguments are over no input row at all, so
        /// they may hold no field reference — which is exactly what makes it a leaf rather than a
        /// projection with a strange name.
        /// </summary>
        private void TableFunctionScanNode(TableFunctionScan scan, RowType output, string path)
        {
            if (scan.Function.Length == 0)
            {
                throw Invalid("I-IR-16", path, "the table function scan names no function");
            }

            if (output.Fields.Count == 0)
            {
                throw Invalid("I-IR-2", path, "a table function scan produces at least one column");
            }

            var empty = new RowType();
            for (var i = 0; i < scan.Args.Count; i++)
            {
                Expression(scan.Args[i], empty, $"{path}.args[{i}]");
            }
        }

        /// <summary>
        /// <c>Unnest</c> (D66): a LIST column in, the input's fields plus the element — made
        /// nullable when <c>keep_empty</c> pads — plus an optional I32 ordinality out.
        /// </summary>
        private void UnnestNode(Unnest unnest, RowType output, string path)
        {
            var input = Rel(unnest.Input, path);
            RequireIndex(unnest.ListColumn, input, "I-IR-3", $"{path}.list_column");

            var list = input.Fields[(int)unnest.ListColumn].Type!;
            if (list.Kind != TypeKind.List)
            {
                throw Invalid(
                    "I-IR-15",
                    $"{path}.list_column",
                    $"the unnested column is {IrTypes.Describe(list)}; Unnest takes a LIST");
            }

            var extra = unnest.WithOrdinality ? 2 : 1;
            if (output.Fields.Count != input.Fields.Count + extra)
            {
                throw Invalid(
                    "I-IR-15",
                    path,
                    $"the row type has {output.Fields.Count} fields but its input has "
                    + $"{input.Fields.Count} and Unnest adds {extra}");
            }

            for (var i = 0; i < input.Fields.Count; i++)
            {
                RequireSameType(
                    output.Fields[i].Type,
                    input.Fields[i].Type,
                    $"{path}.row_type[{i}]",
                    "the input field it carries through");
            }

            // The element keeps the list's element type, made nullable when keep_empty pads an empty
            // or NULL list with one.
            var element = list.Element.Clone();
            element.Nullable |= unnest.KeepEmpty;
            RequireSameType(
                output.Fields[input.Fields.Count].Type,
                element,
                $"{path}.row_type[{input.Fields.Count}]",
                "the list's element type" + (unnest.KeepEmpty ? ", made nullable" : string.Empty));

            if (unnest.WithOrdinality)
            {
                var ordinality = output.Fields[input.Fields.Count + 1].Type!;
                if (ordinality.Kind != TypeKind.I32 || ordinality.Nullable != unnest.KeepEmpty)
                {
                    throw Invalid(
                        "I-IR-15",
                        $"{path}.row_type[{input.Fields.Count + 1}]",
                        $"the ordinality column is {IrTypes.Describe(ordinality)}; with keep_empty "
                        + $"{unnest.KeepEmpty.ToString(CultureInfo.InvariantCulture)} it is I32"
                        + (unnest.KeepEmpty ? "?" : string.Empty));
                }
            }
        }

        /// <summary>One frame: explicit bounds, offsets only where an offset belongs.</summary>
        private void Frame(Window window, RowType input, string path)
        {
            var frame = window.Frame;
            if (frame.Mode == FrameMode.Unspecified)
            {
                throw new IrVersionMismatchException(
                    plan.IrVersion,
                    IrVersion.Current,
                    $"The frame at {path} has a mode this client does not know.");
            }

            if (frame.Exclusion == FrameExclusion.Unspecified)
            {
                throw Invalid(
                    "I-IR-11",
                    path,
                    "frame exclusion is unspecified; EXCLUDE NO OTHERS is written down, not implied");
            }

            var lower = Bound(frame.Lower, window, input, $"{path}.lower", upper: false);
            var upper = Bound(frame.Upper, window, input, $"{path}.upper", upper: true);

            // A frame whose lower bound follows its upper one names no rows at all for any row.
            if (lower == FrameBoundKind.Following && upper == FrameBoundKind.CurrentRow)
            {
                throw Invalid(
                    "I-IR-11", path, "a FOLLOWING lower bound cannot pair with a CURRENT ROW upper one");
            }
        }

        private FrameBoundKind Bound(
            FrameBound? bound, Window window, RowType input, string path, bool upper)
        {
            if (bound is null)
            {
                throw Invalid("I-IR-11", path, "a frame bound is missing; bounds are always explicit");
            }

            switch (bound.Kind)
            {
                case FrameBoundKind.Unspecified:
                    throw new IrVersionMismatchException(
                        plan.IrVersion,
                        IrVersion.Current,
                        $"The frame bound at {path} has a kind this client does not know.");
                case FrameBoundKind.UnboundedFollowing when !upper:
                    throw Invalid("I-IR-11", path, "UNBOUNDED FOLLOWING is not a lower bound");
                case FrameBoundKind.UnboundedPreceding when upper:
                    throw Invalid("I-IR-11", path, "UNBOUNDED PRECEDING is not an upper bound");
                default:
                    break;
            }

            var offsetKinds = bound.Kind is FrameBoundKind.Preceding or FrameBoundKind.Following;
            if (!offsetKinds)
            {
                if (bound.Offset is not null)
                {
                    throw Invalid("I-IR-11", path, $"{bound.Kind} carries an offset");
                }

                return bound.Kind;
            }

            if (bound.Offset is null)
            {
                throw Invalid("I-IR-11", path, $"{bound.Kind} has no offset");
            }

            if (bound.Offset.KindCase is not (Expr.KindOneofCase.Literal or Expr.KindOneofCase.Param))
            {
                throw Invalid(
                    "I-IR-11",
                    path,
                    $"a frame offset is a literal or a dynamic parameter; found {bound.Offset.KindCase}");
            }

            Expression(bound.Offset, input, $"{path}.offset");

            if (window.Frame.Mode is FrameMode.Rows or FrameMode.Groups)
            {
                RequireKind(bound.Offset, TypeKind.I64, $"{path}.offset");
            }
            else if (window.Order.Count != 1)
            {
                throw Invalid(
                    "I-IR-11",
                    path,
                    $"a RANGE frame with an offset needs exactly one order key; the window has "
                    + $"{window.Order.Count}");
            }

            return bound.Kind;
        }

        private void EquiJoin(
            RowType left,
            RowType right,
            IReadOnlyList<uint> leftKeys,
            IReadOnlyList<uint> rightKeys,
            Expr? residual,
            string path)
        {
            if (leftKeys.Count != rightKeys.Count)
            {
                throw Invalid(
                    "I-IR-3",
                    path,
                    $"{leftKeys.Count} left keys against {rightKeys.Count} right keys");
            }

            for (var i = 0; i < leftKeys.Count; i++)
            {
                RequireIndex(leftKeys[i], left, "I-IR-3", $"{path}.left_keys[{i}]");
                RequireIndex(rightKeys[i], right, "I-IR-3", $"{path}.right_keys[{i}]");
                RefuseNonScalar(left.Fields[(int)leftKeys[i]].Type, $"{path}.left_keys[{i}]", "joined on");
                RefuseNonScalar(right.Fields[(int)rightKeys[i]].Type, $"{path}.right_keys[{i}]", "joined on");
            }

            if (residual is not null)
            {
                Expression(residual, Concat(left, right), $"{path}.post_join_filter");
                RequireKind(residual, TypeKind.Bool, $"{path}.post_join_filter");
            }
        }

        /// <summary>
        /// A lookup join (M5, D105). Structurally an equi-join whose right input is a query carrying
        /// exactly one key set; <c>I-IR-17</c> is that shape.
        /// </summary>
        private RowType LookupJoin(Chalk.Ir.LookupJoin join, RowType output, string path)
        {
            var driving = Rel(join.Driving, $"{path}.driving");
            var lookup = Rel(join.Lookup, $"{path}.lookup");

            if (join.Type is not (JoinType.Inner or JoinType.Left or JoinType.Semi or JoinType.Anti))
            {
                throw Invalid(
                    "I-IR-17",
                    path,
                    $"a LookupJoin is INNER, LEFT, SEMI or ANTI; found {join.Type}. RIGHT and FULL "
                    + "need every row of the lookup side, which is the one thing a lookup does not fetch");
            }

            EquiJoin(driving, lookup, join.DrivingKeys, join.LookupKeys, join.PostJoinFilter, path);

            if (join.MaxKeysPerCall <= 0)
            {
                throw Invalid(
                    "I-IR-17",
                    path,
                    $"max_keys_per_call is {join.MaxKeysPerCall}; a call must carry at least one key");
            }

            var slots = KeySetSlots(join.Lookup);
            if (slots.Count != 1)
            {
                throw Invalid(
                    "I-IR-17",
                    $"{path}.lookup",
                    "a LookupJoin's lookup subtree binds exactly one key set; found "
                    + (slots.Count == 0 ? "none" : $"{slots.Count} slots"));
            }

            // One key pair for a KeySetParam, and as many as a KeySetMatch has columns for a
            // composite key set (F50). The two have to agree: a call binds rows of exactly the
            // width the predicate matches, and the executor reads the driving values by position.
            var keyColumns = KeySetColumns(join.Lookup);
            if (join.DrivingKeys.Count != keyColumns)
            {
                throw Invalid(
                    "I-IR-17",
                    path,
                    $"a LookupJoin has {join.DrivingKeys.Count} key pairs and its lookup subtree's "
                    + $"key set matches {keyColumns} column"
                    + (keyColumns == 1 ? string.Empty : "s")
                    + "; they are the same key, so there are as many pairs as columns");
            }

            // Types, not names: a join's output row disambiguates a duplicated column name and the
            // concatenation does not, which is a difference in spelling rather than in shape.
            var expected = join.Type is JoinType.Semi or JoinType.Anti
                ? driving
                : Concat(driving, lookup);
            RequireSetOpRow(output, expected, path);
            return output;
        }

        /// <summary>
        /// An adaptive join (D97): the small side, and two branches over a MaterialisedInput that
        /// replays it. Both branches must produce this node's row type, because either may run.
        /// </summary>
        private void AdaptiveJoin(Chalk.Ir.Rel rel, string path)
        {
            var adaptive = rel.AdaptiveJoin;
            if (adaptive.Lookup is null || adaptive.Local is null || adaptive.Small is null)
            {
                throw Invalid("I-IR-18", path, "an AdaptiveJoin needs a small side and both branches");
            }

            var small = Rel(adaptive.Small, $"{path}.small");
            RequireIndex(adaptive.Key, small, "I-IR-18", $"{path}.key");
            RefuseNonScalar(small.Fields[(int)adaptive.Key].Type, $"{path}.key", "joined on");
            if (adaptive.MaxKeys <= 0)
            {
                throw Invalid(
                    "I-IR-18",
                    path,
                    $"max_keys is {adaptive.MaxKeys}; the threshold a distinct-key count is compared "
                    + "against must be positive");
            }

            // Both branches replay the same materialisation, and both must say so.
            RequireReplay(adaptive.Lookup.Driving, adaptive.Slot, small, $"{path}.lookup.driving");
            var lookupRow = LookupJoin(adaptive.Lookup, rel.RowType, $"{path}.lookup");
            var localRow = Rel(adaptive.Local, $"{path}.local");
            RequireSameRow(localRow, lookupRow, path, "the lookup branch's row");
            RequireSameRow(rel.RowType, lookupRow, path, "the branches' row");
        }

        /// <summary>A branch's replay input is a MaterialisedInput of the right slot (I-IR-18).</summary>
        private void RequireReplay(Chalk.Ir.Rel? replay, uint slot, RowType small, string path)
        {
            if (replay is null || replay.KindCase != Chalk.Ir.Rel.KindOneofCase.MaterialisedInput)
            {
                throw Invalid(
                    "I-IR-18",
                    path,
                    "an AdaptiveJoin's branches read the small side through a MaterialisedInput; found "
                    + (replay?.KindCase.ToString() ?? "nothing"));
            }

            if (replay.MaterialisedInput.Slot != slot)
            {
                throw Invalid(
                    "I-IR-18",
                    path,
                    $"the branch replays slot {replay.MaterialisedInput.Slot} and the join materialises {slot}");
            }

            RequireSameRow(replay.RowType, small, path, "the small side's row");
        }

        /// <summary>A partitioned scan (D106): every branch has this node's row type.</summary>
        private void PartitionedScan(Chalk.Ir.Rel rel, string path)
        {
            var scan = rel.PartitionedScan;
            if (scan.Partitions.Count == 0)
            {
                throw Invalid("I-IR-19", path, "a PartitionedScan has no partitions");
            }

            if (scan.Matches.Count != 0 && scan.Matches.Count != scan.Partitions.Count)
            {
                throw Invalid(
                    "I-IR-19",
                    path,
                    $"{scan.Matches.Count} partition matches against {scan.Partitions.Count} partitions; "
                    + "they are parallel lists");
            }

            for (var i = 0; i < scan.Partitions.Count; i++)
            {
                var branch = Rel(scan.Partitions[i], $"{path}.partitions[{i}]");
                RequireSetOpRow(rel.RowType, branch, $"{path}.partitions[{i}]");
            }

            for (var i = 0; i < scan.Matches.Count; i++)
            {
                var value = scan.Matches[i].Value;
                if (value is not null && value.KindCase != Expr.KindOneofCase.Literal)
                {
                    throw Invalid(
                        "I-IR-19",
                        $"{path}.matches[{i}]",
                        $"a partition's value is a literal; found {value.KindCase}");
                }
            }

            if (rel.Collations.Count > 0)
            {
                throw Invalid(
                    "I-IR-19",
                    path,
                    "a PartitionedScan claims no ordering: its branches are read in no particular "
                    + "order and their rows are interleaved");
            }
        }

        /// <summary>
        /// The key-set slots a subtree binds, pushed plans included. Slots rather than occurrences,
        /// because one key set legitimately appears twice — once in the pushed predicate that reads
        /// it and once in the RemoteQuery's parameter list that names where it goes — and both are
        /// the same set.
        /// </summary>
        private static HashSet<uint> KeySetSlots(Chalk.Ir.Rel rel)
        {
            var slots = new HashSet<uint>();
            Collect(rel, slots);
            return slots;

            // The inclusive walk: a RemoteQuery's pushed plan is where the predicate that binds the
            // key set actually lives (F92).
            static void Collect(Chalk.Ir.Rel root, HashSet<uint> into)
            {
                foreach (var node in PlanWalker.Rels(root))
                {
                    foreach (var own in PlanWalker.OwnExprs(node))
                    {
                        foreach (var expr in PlanWalker.Exprs(own))
                        {
                            if (expr.KindCase == Expr.KindOneofCase.KeySet)
                            {
                                into.Add(expr.KeySet.Slot);
                            }
                            else if (expr.KindCase == Expr.KindOneofCase.KeySetMatch)
                            {
                                into.Add(expr.KeySetMatch.KeySet.Slot);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// How many columns this subtree's key set matches — one for a <c>KeySetParam</c>, the
        /// <c>KeySetMatch</c>'s column count for a composite one (F50), and one when there is no
        /// key set at all, which the slot check reports instead.
        /// </summary>
        private static int KeySetColumns(Chalk.Ir.Rel rel)
        {
            foreach (var node in PlanWalker.ExecutedRels(rel))
            {
                // The pushed plan first, and explicitly: it holds the KeySetMatch with the real
                // column count where the RemoteQuery's own parameter list holds only the slot, so
                // asking in the inclusive walk's pre-order would answer 1 for a composite key (F50).
                foreach (var pushed in PlanWalker.Pushed(node))
                {
                    var nested = KeySetColumns(pushed);
                    if (nested > 0)
                    {
                        return nested;
                    }
                }

                foreach (var own in PlanWalker.OwnExprs(node))
                {
                    foreach (var expr in PlanWalker.Exprs(own))
                    {
                        if (expr.KindCase == Expr.KindOneofCase.KeySetMatch)
                        {
                            return expr.KeySetMatch.Columns.Count;
                        }

                        if (expr.KindCase == Expr.KindOneofCase.KeySet)
                        {
                            return 1;
                        }
                    }
                }
            }

            return 1;
        }

        private void SortFields(IReadOnlyList<SortField> fields, RowType input, string path)
        {
            for (var i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                var fieldPath = $"{path}[{i}]";
                if (field.Expr is null || field.Expr.KindCase != Expr.KindOneofCase.FieldRef)
                {
                    throw Invalid(
                        "I-IR-5",
                        fieldPath,
                        $"a sort field expression is always a FieldRef; found {field.Expr?.KindCase.ToString() ?? "nothing"}");
                }

                if (field.Direction == SortDirection.Unspecified)
                {
                    throw Invalid("I-IR-5", fieldPath, "the sort direction is unspecified");
                }

                Expression(field.Expr, input, fieldPath);
                RefuseNonScalar(field.Expr.Type, fieldPath, "sorted on");
            }
        }

        /// <summary>Validates an expression tree against the row it reads from.</summary>
        private void Expression(Expr expr, RowType input, string path)
        {
            if (expr.KindCase == Expr.KindOneofCase.None)
            {
                throw new IrVersionMismatchException(
                    plan.IrVersion,
                    IrVersion.Current,
                    $"The expression at {path} has a kind this client does not know.");
            }

            if (expr.KindCase == Expr.KindOneofCase.EnumArg)
            {
                // I-IR-10: an EnumArg is only legal directly under one of the functions that take one,
                // which is checked by the caller. Reaching here means it was somewhere else.
                throw Invalid(
                    "I-IR-10",
                    path,
                    $"an EnumArg may only be a direct argument of {string.Join(", ", EnumArgFunctions)}");
            }

            if (expr.Type is null)
            {
                throw Invalid("I-IR-4", path, $"the {expr.KindCase} expression has no type");
            }

            CheckType(expr.Type, $"{path}.type");

            switch (expr.KindCase)
            {
                case Expr.KindOneofCase.FieldRef:
                    RequireIndex(expr.FieldRef.Index, input, "I-IR-3", path);
                    RequireSameType(
                        input.Fields[(int)expr.FieldRef.Index].Type,
                        expr.Type,
                        path,
                        $"the input field ${expr.FieldRef.Index} type");
                    break;

                case Expr.KindOneofCase.Literal:
                    // A composite value is produced by a function and by nothing else: there is no composite
                    // literal, not even a typed NULL one (I-IR-23).
                    RefuseComposite(expr.Type, path, "a literal");
                    LiteralValue(expr, path);
                    break;

                case Expr.KindOneofCase.Param:
                {
                    RefuseComposite(expr.Type, path, "a parameter's type");

                    // A named bound value is not one of the statement's parameters and has no index
                    // into their types: its type is its own, and execution binds it by name from the
                    // request's context (16-entitlements.md §2, D209).
                    if (expr.Param.BoundKey.Length != 0)
                    {
                        break;
                    }

                    var index = (int)expr.Param.Index;
                    if (index >= plan.ParameterTypes.Count)
                    {
                        throw Invalid(
                            "I-IR-3",
                            path,
                            $"DynamicParam index {index} but the plan declares {plan.ParameterTypes.Count} parameter types");
                    }

                    RequireSameType(plan.ParameterTypes[index], expr.Type, path, $"plan.parameter_types[{index}]");
                    break;
                }

                case Expr.KindOneofCase.Cast:
                    Expression(expr.Cast.Input, input, $"{path}.input");
                    RefuseComposite(expr.Cast.Input.Type, $"{path}.input", "cast");
                    RefuseComposite(expr.Type, path, "a cast's target");
                    break;

                case Expr.KindOneofCase.IfThen:
                {
                    if (expr.IfThen.Clauses.Count == 0)
                    {
                        throw Invalid("I-IR-4", path, "CASE has no WHEN clauses");
                    }

                    if (expr.IfThen.ElseBranch is null)
                    {
                        throw Invalid(
                            "I-IR-4",
                            path,
                            "CASE has no ELSE; the planner emits a typed NULL literal when SQL omits it");
                    }

                    RefuseComposite(expr.Type, path, "a CASE result");

                    for (var i = 0; i < expr.IfThen.Clauses.Count; i++)
                    {
                        var clause = expr.IfThen.Clauses[i];
                        Expression(clause.Condition, input, $"{path}.clauses[{i}].condition");
                        RequireKind(clause.Condition, TypeKind.Bool, $"{path}.clauses[{i}].condition");
                        Expression(clause.Result, input, $"{path}.clauses[{i}].result");
                        RequireSameKind(expr.Type, clause.Result.Type, $"{path}.clauses[{i}].result", "the CASE result type");
                    }

                    Expression(expr.IfThen.ElseBranch, input, $"{path}.else");
                    RequireSameKind(expr.Type, expr.IfThen.ElseBranch.Type, $"{path}.else", "the CASE result type");
                    break;
                }

                case Expr.KindOneofCase.InList:
                {
                    Expression(expr.InList.Value, input, $"{path}.value");
                    RefuseComposite(expr.InList.Value.Type, $"{path}.value", "an IN value");
                    if (expr.InList.Options.Count == 0)
                    {
                        throw Invalid("I-IR-4", path, "IN has no options");
                    }

                    for (var i = 0; i < expr.InList.Options.Count; i++)
                    {
                        var option = expr.InList.Options[i];
                        var optionPath = $"{path}.options[{i}]";
                        if (option.KindCase is not (Expr.KindOneofCase.Literal
                            or Expr.KindOneofCase.Param or Expr.KindOneofCase.KeySet))
                        {
                            throw Invalid(
                                "I-IR-4",
                                optionPath,
                                $"IN options are literals, dynamic parameters or a key set; found {option.KindCase}. "
                                + "Non-constant IN lists are rewritten by the planner to OR");
                        }

                        // I-IR-17: a key set is the *whole* list, because it stands for every key
                        // one lookup call carries. A list that mixed one with literals would have
                        // no expansion the executor could write.
                        if (option.KindCase == Expr.KindOneofCase.KeySet && expr.InList.Options.Count != 1)
                        {
                            throw Invalid(
                                "I-IR-17",
                                optionPath,
                                "a KeySetParam is an IN list's only option: it stands for the whole "
                                + $"set of keys a lookup call binds, and this list has {expr.InList.Options.Count}");
                        }

                        Expression(option, input, optionPath);
                        RequireSameKind(expr.InList.Value.Type, option.Type, optionPath, "the IN value's type");
                    }

                    break;
                }

                case Expr.KindOneofCase.Call:
                    Call(expr, input, path);
                    break;

                case Expr.KindOneofCase.KeySet:
                    // Bound by the executor, never by a caller, and only inside a LookupJoin's
                    // lookup subtree — which the whole-plan pass checks, because here there is no
                    // way to know where the expression sits.
                    if (expr.Type is null)
                    {
                        throw Invalid("I-IR-2", path, "a KeySetParam has no type; it is the key column's");
                    }

                    break;

                case Expr.KindOneofCase.KeySetMatch:
                    KeySetMatch(expr, input, path);
                    break;

                case Expr.KindOneofCase.FieldAccess:
                    FieldAccess(expr, input, path);
                    break;

                case Expr.KindOneofCase.EnumArg:
                case Expr.KindOneofCase.None:
                default:
                    throw Invalid("I-IR-1", path, $"unhandled expression kind {expr.KindCase}");
            }
        }

        /// <summary>
        /// <c>I-IR-22</c> (D291): one field of a COMPOSITE-typed input, by position, typed as the field is —
        /// made nullable when the composite is, because a NULL composite reads as NULL in every field
        /// whatever the field declares.
        /// </summary>
        private void FieldAccess(Expr expr, RowType input, string path)
        {
            var access = expr.FieldAccess;
            if (access.Input is null)
            {
                throw Invalid("I-IR-22", path, "a FieldAccess has no input");
            }

            Expression(access.Input, input, $"{path}.input");
            var composite = access.Input.Type!;
            if (composite.Kind != TypeKind.Composite)
            {
                throw Invalid(
                    "I-IR-22",
                    $"{path}.input",
                    $"the input is {IrTypes.Describe(composite)}; a FieldAccess reads a field of a COMPOSITE");
            }

            if (access.Index >= (uint)composite.Fields.Count)
            {
                throw Invalid(
                    "I-IR-22",
                    path,
                    $"field index {access.Index} is out of range for a COMPOSITE of "
                    + $"{composite.Fields.Count} field{(composite.Fields.Count == 1 ? string.Empty : "s")}");
            }

            var field = composite.Fields[(int)access.Index];
            var expected = field.Type.Clone();
            expected.Nullable |= composite.Nullable;
            if (!expected.Equals(expr.Type))
            {
                throw Invalid(
                    "I-IR-22",
                    path,
                    $"the access is typed {IrTypes.Describe(expr.Type)}, and field '{field.Name}' of "
                    + $"{IrTypes.Describe(composite)} reads as {IrTypes.Describe(expected)}: the field's "
                    + "own type, made nullable when the composite is");
            }
        }

        /// <summary>
        /// A composite key set (F50): the tuple of columns is one of the key rows a lookup call
        /// binds. Two or more columns, because one column is the <c>InList</c> spelling, and BOOL,
        /// because it is a predicate.
        /// </summary>
        private void KeySetMatch(Expr expr, RowType input, string path)
        {
            var match = expr.KeySetMatch;
            if (match.Columns.Count < 2)
            {
                throw Invalid(
                    "I-IR-17",
                    path,
                    $"a KeySetMatch matches two or more columns; found {match.Columns.Count}. A "
                    + "key set over one column is an InList whose only option is a KeySetParam");
            }

            if (expr.Type is null || expr.Type.Kind != TypeKind.Bool)
            {
                throw Invalid(
                    "I-IR-2",
                    path,
                    "a KeySetMatch is a predicate, so its type is BOOL");
            }

            for (var i = 0; i < match.Columns.Count; i++)
            {
                Expression(match.Columns[i], input, $"{path}.columns[{i}]");
            }
        }

        private void Call(Expr expr, RowType input, string path)
        {
            var call = expr.Call;
            if (call.UserFunction.Length > 0)
            {
                // Exactly one of the two names the callee (step 22): a stable id for a built-in, a
                // schema-qualified name for a function the catalog declares.
                if (call.Function != FunctionId.Unspecified)
                {
                    throw Invalid(
                        "I-IR-16",
                        path,
                        $"the call names both the built-in {call.Function} and the user function "
                        + $"'{call.UserFunction}'");
                }

                for (var i = 0; i < call.Args.Count; i++)
                {
                    Expression(call.Args[i], input, $"{path}.args[{i}]");

                    // A composite value is never a parameter type (D294), so no declared function takes one.
                    RefuseComposite(call.Args[i].Type, $"{path}.args[{i}]", "a function's argument");
                }

                return;
            }

            if (call.Function == FunctionId.Unspecified)
            {
                throw new IrVersionMismatchException(
                    plan.IrVersion,
                    IrVersion.Current,
                    $"The call at {path} names a function this client does not know.");
            }

            var takesEnum = Array.IndexOf(EnumArgFunctions, call.Function) >= 0;
            for (var i = 0; i < call.Args.Count; i++)
            {
                var arg = call.Args[i];
                var argPath = $"{path}.args[{i}]";
                if (arg.KindCase == Expr.KindOneofCase.EnumArg)
                {
                    if (!takesEnum)
                    {
                        throw Invalid(
                            "I-IR-10",
                            argPath,
                            $"{call.Function} does not take an EnumArg argument");
                    }

                    if (arg.EnumArg.Value.Length == 0)
                    {
                        throw Invalid("I-IR-10", argPath, "the EnumArg has an empty value");
                    }

                    continue;
                }

                Expression(arg, input, argPath);
                if (arg.Type?.Kind == TypeKind.Composite && !TakesComposite(call.Function))
                {
                    RefuseComposite(arg.Type, argPath, CompositeRole(call.Function));
                }
            }

            if (call.Function is FunctionId.And or FunctionId.Or)
            {
                if (call.Args.Count < 2)
                {
                    throw Invalid("I-IR-4", path, $"{call.Function} takes at least 2 arguments; found {call.Args.Count}");
                }

                for (var i = 0; i < call.Args.Count; i++)
                {
                    RequireKind(call.Args[i], TypeKind.Bool, $"{path}.args[{i}]");
                }
            }
            else if (call.Function == FunctionId.Not)
            {
                if (call.Args.Count != 1)
                {
                    throw Invalid("I-IR-4", path, $"NOT takes 1 argument; found {call.Args.Count}");
                }

                RequireKind(call.Args[0], TypeKind.Bool, $"{path}.args[0]");
            }

            RefuseComposite(expr.Type, path, $"the result of {call.Function}");
            CheckHomogeneity(call, path, expr.Type);
        }

        /// <summary>A set operation as SQL spells it, for a refusal.</summary>
        private static string SetOpName(SetOpKind kind) => kind switch
        {
            SetOpKind.UnionDistinct => "UNION",
            SetOpKind.IntersectAll => "INTERSECT ALL",
            SetOpKind.IntersectDistinct => "INTERSECT",
            SetOpKind.ExceptAll => "EXCEPT ALL",
            SetOpKind.ExceptDistinct => "EXCEPT",
            _ => kind.ToString(),
        };

        /// <summary>
        /// The built-ins a COMPOSITE may be an argument of: the two null tests, which read a value's
        /// validity and nothing else (D291). Every other built-in compares, computes or converts.
        /// </summary>
        private static bool TakesComposite(FunctionId function) =>
            function is FunctionId.IsNull or FunctionId.IsNotNull;

        /// <summary>What a COMPOSITE argument of <paramref name="function"/> would be, for the refusal.</summary>
        private static string CompositeRole(FunctionId function) => function switch
        {
            FunctionId.Eq or FunctionId.Ne or FunctionId.Lt or FunctionId.Le or FunctionId.Gt
                or FunctionId.Ge or FunctionId.IsDistinctFrom or FunctionId.IsNotDistinctFrom
                or FunctionId.Nullif => "compared",
            FunctionId.Add or FunctionId.Subtract or FunctionId.Multiply or FunctionId.Divide
                or FunctionId.Modulus or FunctionId.Negate or FunctionId.Abs or FunctionId.Power
                => "an arithmetic operand",
            _ => $"an argument of {function}",
        };

        /// <summary>
        /// I-IR-2: value operands of comparisons, arithmetic, NULLIF and COALESCE share a TypeKind
        /// (and, for DECIMAL, a scale; for temporal kinds, a precision). Temporal arithmetic against an
        /// interval is exempt: <c>(TIMESTAMP, INTERVAL_DAY) -&gt; TIMESTAMP</c> is heterogeneous by
        /// definition (02-ir.md §6). For arithmetic the rule reaches the call's own result as well;
        /// see <see cref="CheckArithmeticResult"/>.
        /// </summary>
        private void CheckHomogeneity(ScalarCall call, string path, Type? declared)
        {
            if (Array.IndexOf(HomogeneousFunctions, call.Function) < 0 || call.Args.Count < 2)
            {
                return;
            }

            var kinds = call.Args.Select(a => a.Type?.Kind ?? TypeKind.Unspecified).ToArray();
            if (kinds.Any(IrTypes.IsInterval) && kinds.Any(k => IrTypes.IsTemporal(k) || IrTypes.IsInterval(k)))
            {
                return;
            }

            var first = call.Args[0].Type!;
            for (var i = 1; i < call.Args.Count; i++)
            {
                RequireSameKind(first, call.Args[i].Type, $"{path}.args[{i}]", $"args[0]'s type {IrTypes.Describe(first)}");
            }

            CheckArithmeticResult(call, path, declared, first.Kind);
        }

        /// <summary>
        /// I-IR-2, the result half: an arithmetic call is declared at the kind its harmonised operands
        /// share. The executor closes each kernel over one type at plan compilation and reads that type
        /// off the call, so a call declared narrower than its operands reads wide lanes as narrow ones —
        /// which is exactly how an INTEGER-typed <c>MOD</c> over two BIGINT operands reached execution
        /// and divided by a zero high half (ADR 0024, V52). Operand homogeneity alone accepted it; this
        /// is what rejects it at receipt.
        /// </summary>
        /// <remarks>
        /// A result that is itself an interval is exempt for the same reason an interval operand is:
        /// a datetime difference is heterogeneous by definition. DECIMAL scale and precision are not
        /// checked here — a product's scale is legitimately the sum of its operands' — only the kind.
        /// </remarks>
        private void CheckArithmeticResult(ScalarCall call, string path, Type? declared, TypeKind operands)
        {
            if (Array.IndexOf(ArithmeticFunctions, call.Function) < 0
                || declared is null
                || IrTypes.IsInterval(declared.Kind)
                || declared.Kind == operands)
            {
                return;
            }

            throw Invalid(
                "I-IR-2",
                path,
                $"{call.Function} is declared {IrTypes.Describe(declared)} over operands of kind {operands}; "
                + "an arithmetic call's result kind is its harmonised operands' kind, and the executor "
                + "takes the kernel's type from the result");
        }

        private void LiteralValue(Expr expr, string path)
        {
            var literal = expr.Literal;
            var kind = expr.Type!.Kind;
            if (literal.ValueCase == Literal.ValueOneofCase.IsNull)
            {
                if (!literal.IsNull)
                {
                    throw Invalid("I-IR-8", path, "is_null is set but false; a typed NULL sets it to true");
                }

                if (!expr.Type.Nullable)
                {
                    throw Invalid("I-IR-8", path, $"a NULL literal has non-nullable type {IrTypes.Describe(expr.Type)}");
                }

                return;
            }

            var expected = kind switch
            {
                TypeKind.Bool => Literal.ValueOneofCase.BoolValue,
                TypeKind.I8 => Literal.ValueOneofCase.I8Value,
                TypeKind.I16 => Literal.ValueOneofCase.I16Value,
                TypeKind.I32 => Literal.ValueOneofCase.I32Value,
                TypeKind.I64 => Literal.ValueOneofCase.I64Value,
                TypeKind.Fp32 => Literal.ValueOneofCase.Fp32Value,
                TypeKind.Fp64 => Literal.ValueOneofCase.Fp64Value,
                TypeKind.String => Literal.ValueOneofCase.StringValue,
                TypeKind.Binary => Literal.ValueOneofCase.BinaryValue,
                TypeKind.Date => Literal.ValueOneofCase.DateValue,
                TypeKind.Time => Literal.ValueOneofCase.TimeValue,
                TypeKind.Timestamp => Literal.ValueOneofCase.TimestampValue,
                TypeKind.TimestampTz => Literal.ValueOneofCase.TimestampTzValue,
                TypeKind.Decimal => Literal.ValueOneofCase.DecimalValue,
                TypeKind.Uuid => Literal.ValueOneofCase.UuidValue,
                TypeKind.IntervalDay => Literal.ValueOneofCase.IntervalDayValue,
                TypeKind.IntervalYear => Literal.ValueOneofCase.IntervalYearValue,
                TypeKind.List => Literal.ValueOneofCase.ListValue,
                _ => Literal.ValueOneofCase.None,
            };

            if (literal.ValueCase != expected)
            {
                throw Invalid(
                    "I-IR-8",
                    path,
                    $"a literal of type {IrTypes.Describe(expr.Type)} carries {literal.ValueCase}, expected {expected}");
            }

            switch (literal.ValueCase)
            {
                case Literal.ValueOneofCase.DecimalValue when literal.DecimalValue.Unscaled.Length != 16:
                    throw Invalid(
                        "I-IR-8",
                        path,
                        $"DecimalValue.unscaled is {literal.DecimalValue.Unscaled.Length} bytes; it must be exactly 16");
                case Literal.ValueOneofCase.UuidValue when literal.UuidValue.Length != 16:
                    throw Invalid(
                        "I-IR-8",
                        path,
                        $"uuid_value is {literal.UuidValue.Length} bytes; it must be exactly 16");
                case Literal.ValueOneofCase.I8Value when literal.I8Value is < sbyte.MinValue or > sbyte.MaxValue:
                    throw Invalid("I-IR-8", path, $"i8_value {literal.I8Value} is out of range for I8");
                case Literal.ValueOneofCase.I16Value when literal.I16Value is < short.MinValue or > short.MaxValue:
                    throw Invalid("I-IR-8", path, $"i16_value {literal.I16Value} is out of range for I16");
                case Literal.ValueOneofCase.ListValue:
                {
                    // Each element is a literal of the list's element type, checked by the same rules
                    // (I-IR-12). The recursion is one level, because the element is never a LIST.
                    var element = expr.Type.Element!;
                    for (var i = 0; i < literal.ListValue.Elements.Count; i++)
                    {
                        LiteralValue(
                            new Expr { Type = element, Literal = literal.ListValue.Elements[i] },
                            $"{path}.list_value.elements[{i}]");
                    }

                    break;
                }

                default:
                    break;
            }
        }

        private void CheckType(Type? type, string path)
        {
            if (type is null)
            {
                throw Invalid("I-IR-4", path, "the type is missing");
            }

            if (type.Kind == TypeKind.Unspecified)
            {
                throw new IrVersionMismatchException(
                    plan.IrVersion,
                    IrVersion.Current,
                    $"The type at {path} has a kind this client does not know.");
            }

            // I-IR-12 (D58): a LIST carries an element, one level deep, and nothing else carries one.
            if (type.Kind == TypeKind.List)
            {
                if (type.Element is null)
                {
                    throw Invalid("I-IR-12", path, "a LIST has no element type");
                }

                if (type.Element.Kind == TypeKind.List)
                {
                    throw Invalid(
                        "I-IR-12",
                        path,
                        "a LIST's element is itself a LIST; v1 lists are exactly one level deep");
                }

                if (type.Element.Kind == TypeKind.Composite)
                {
                    throw Invalid(
                        "I-IR-12",
                        path,
                        "a LIST's element is a COMPOSITE; a list holds scalars and a composite is never "
                        + "nested in another");
                }

                CheckType(type.Element, $"{path}.element");
            }
            else if (type.Element is not null)
            {
                throw Invalid(
                    "I-IR-12",
                    path,
                    $"{type.Kind} carries an element type; only a LIST has one");
            }

            // I-IR-21 (D291): a COMPOSITE carries its fields, one level deep, and nothing else carries any.
            if (type.Kind == TypeKind.Composite)
            {
                CompositeFields(type, path);
            }
            else if (type.Fields.Count > 0)
            {
                throw Invalid(
                    "I-IR-21",
                    path,
                    $"{type.Kind} carries {type.Fields.Count} field(s); only a COMPOSITE has fields");
            }

            if (type.Kind == TypeKind.Decimal)
            {
                if (type.Precision == 0 || type.Precision > 38)
                {
                    throw Invalid(
                        "I-IR-4",
                        path,
                        $"DECIMAL precision is {type.Precision}; it must be 1..38");
                }

                if (type.Scale > type.Precision)
                {
                    throw Invalid(
                        "I-IR-4",
                        path,
                        $"DECIMAL scale {type.Scale} exceeds precision {type.Precision}");
                }
            }
            else if (IrTypes.HasPrecision(type.Kind))
            {
                var max = type.Kind == TypeKind.Time ? 6u : 9u;
                if (type.Precision > max)
                {
                    throw Invalid(
                        "I-IR-4",
                        path,
                        $"{type.Kind} precision is {type.Precision}; the maximum is {max}");
                }

                if (type.Scale != 0)
                {
                    throw Invalid("I-IR-4", path, $"{type.Kind} carries scale {type.Scale}; scale is DECIMAL-only");
                }
            }
            else
            {
                if (type.Precision != 0 || type.Scale != 0)
                {
                    throw Invalid(
                        "I-IR-4",
                        path,
                        $"{type.Kind} carries precision {type.Precision} / scale {type.Scale}; both must be zero");
                }
            }
        }

        /// <summary>
        /// <c>I-IR-21</c> (D291): a COMPOSITE has at least one field; every field has a name, no two names
        /// are equal ignoring case — SQL resolves a field that way — and every field is a scalar, so a
        /// composite is exactly one level deep.
        /// </summary>
        private void CompositeFields(Type type, string path)
        {
            if (type.Fields.Count == 0)
            {
                throw Invalid("I-IR-21", path, "a COMPOSITE has no fields; it has at least one");
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < type.Fields.Count; i++)
            {
                var field = type.Fields[i];
                var fieldPath = $"{path}.fields[{i}]";
                if (string.IsNullOrEmpty(field.Name))
                {
                    throw Invalid("I-IR-21", fieldPath, "a COMPOSITE field has no name");
                }

                fieldPath = $"{fieldPath} ({field.Name})";
                if (!names.Add(field.Name))
                {
                    throw Invalid(
                        "I-IR-21",
                        fieldPath,
                        $"field name '{field.Name}' is used twice ignoring case; a field is resolved "
                        + "by name ignoring case, so two such names would be one field");
                }

                if (field.Type is null)
                {
                    throw Invalid("I-IR-21", fieldPath, "the field has no type");
                }

                if (IrTypes.IsNonScalar(field.Type.Kind))
                {
                    throw Invalid(
                        "I-IR-21",
                        fieldPath,
                        $"the field is a {field.Type.Kind.ToString().ToUpperInvariant()}; a COMPOSITE is "
                        + "one level deep and its fields are scalars");
                }

                CheckType(field.Type, fieldPath);
            }
        }

        /// <summary>
        /// The non-scalar rule: a <c>LIST</c> can be produced, carried, projected and indexed into, and a
        /// <c>COMPOSITE</c> produced by a user function, carried and taken apart by field access — but
        /// neither is ever compared, grouped, sorted, partitioned or joined on. I-IR-12 says so for a
        /// list (D58), in the words it always has, and I-IR-23 for a composite (D291). The places where
        /// a type is used that way say so here rather than each rediscovering it.
        /// </summary>
        private void RefuseNonScalar(Type? type, string path, string what)
        {
            if (type?.Kind == TypeKind.List)
            {
                throw Invalid(
                    "I-IR-12",
                    path,
                    $"a LIST cannot be {what}; v1 lists are produced, projected and indexed into only");
            }

            RefuseComposite(type, path, what);
        }

        /// <summary>
        /// <c>I-IR-23</c> (D291): the places a COMPOSITE may never be — a key, an operand, a literal, a
        /// parameter, a cast, a CASE result, a built-in's argument or result, a table's column.
        /// </summary>
        private void RefuseComposite(Type? type, string path, string what)
        {
            if (type?.Kind == TypeKind.Composite)
            {
                throw Invalid(
                    "I-IR-23",
                    path,
                    $"a COMPOSITE cannot be {what}; a composite value is produced by a user function and is only "
                    + "carried, or taken apart by field access");
            }
        }

        /// <summary>
        /// <c>I-IR-23</c> for a leaf's row: a composite value exists only between the function that produced it
        /// and the row that takes it apart or carries it out, so no table, source or bound relation
        /// ever holds one.
        /// </summary>
        private void RefuseCompositeColumns(RowType row, string path, string what)
        {
            for (var i = 0; i < row.Fields.Count; i++)
            {
                RefuseComposite(row.Fields[i].Type, $"{path}.row_type[{i}] ({row.Fields[i].Name})", what);
            }
        }

        private static readonly RowType EmptyRow = new();

        /// <summary>
        /// I-IR-5 extension (12-joins.md §2): a <c>MergeJoin</c> input must itself claim an ordering
        /// whose leading fields are exactly the keys the join relies on, in some order. The join
        /// streams two sorted runs; if an input is not sorted on its keys the result is silently
        /// wrong, so the claim is checked structurally rather than trusted.
        /// </summary>
        private void RequireSortedOn(Rel input, IReadOnlyList<uint> keys, string path)
        {
            if (keys.Count == 0)
            {
                throw Invalid("I-IR-5", path, "a MergeJoin has no equality keys");
            }

            foreach (var collation in input.Collations)
            {
                if (collation.Fields.Count < keys.Count)
                {
                    continue;
                }

                var prefix = new HashSet<uint>();
                for (var i = 0; i < keys.Count; i++)
                {
                    var expr = collation.Fields[i].Expr;
                    if (expr is null || expr.KindCase != Expr.KindOneofCase.FieldRef)
                    {
                        break;
                    }

                    prefix.Add(expr.FieldRef.Index);
                }

                if (prefix.Count == keys.Count && keys.All(prefix.Contains))
                {
                    return;
                }
            }

            throw Invalid(
                "I-IR-5",
                path,
                $"a MergeJoin input must claim an ordering whose first {keys.Count} field(s) are its "
                + $"join keys [{string.Join(",", keys)}]; it claims "
                + (input.Collations.Count == 0 ? "none" : $"{input.Collations.Count} other ordering(s)"));
        }

        private static RowType Concat(RowType left, RowType right)
        {
            var joined = new RowType();
            joined.Fields.AddRange(left.Fields);
            joined.Fields.AddRange(right.Fields);
            return joined;
        }

        private void RequireIndex(uint index, RowType row, string invariant, string path)
        {
            if (index >= (uint)row.Fields.Count)
            {
                throw Invalid(
                    invariant,
                    path,
                    $"index {index} is out of range for an input row of {row.Fields.Count} fields");
            }
        }

        private void RequireKind(Expr expr, TypeKind kind, string path)
        {
            if (expr.Type is null || expr.Type.Kind != kind)
            {
                throw Invalid(
                    "I-IR-2",
                    path,
                    $"expected {kind} but found {IrTypes.Describe(expr.Type)}");
            }
        }

        private void RequireSameType(Type? expected, Type? actual, string path, string what)
        {
            if (!Equals(expected, actual))
            {
                throw Invalid(
                    "I-IR-4",
                    path,
                    $"{IrTypes.Describe(actual)} does not match {what} ({IrTypes.Describe(expected)})");
            }
        }

        private void RequireSameKind(Type? expected, Type? actual, string path, string what)
        {
            if (expected is null || actual is null
                || expected.Kind != actual.Kind
                || (expected.Kind == TypeKind.Decimal && expected.Scale != actual.Scale)
                || (IrTypes.IsTemporal(expected.Kind) && expected.Precision != actual.Precision))
            {
                throw Invalid(
                    "I-IR-2",
                    path,
                    $"{IrTypes.Describe(actual)} is not operand-compatible with {what} ({IrTypes.Describe(expected)}); "
                    + "the planner inserts a Cast to make operands homogeneous");
            }
        }

        /// <summary>
        /// A set operation's inputs have the output's row type up to nullability: Calcite makes the
        /// output the least restrictive of the branches and inserts the casts that need one, but it
        /// does not cast a non-nullable column to a nullable one — the value is the same either way
        /// (<c>15-zero-allocation-execution.md</c> §6, "the validator checks kinds and nullability
        /// union"). Everything else about the types must match exactly.
        /// </summary>
        private void RequireSetOpRow(RowType output, RowType input, string path)
        {
            if (output.Fields.Count != input.Fields.Count)
            {
                throw Invalid(
                    "I-IR-4",
                    path,
                    $"every SetOp input has the output row type, but the output row "
                    + $"{IrTypes.Describe(output)} differs from the input row {IrTypes.Describe(input)}");
            }

            for (var i = 0; i < output.Fields.Count; i++)
            {
                var expected = output.Fields[i].Type;
                var actual = input.Fields[i].Type;
                if (!WidensTo(actual, expected))
                {
                    throw Invalid(
                        "I-IR-4",
                        path,
                        $"every SetOp input has the output row type up to nullability, but field {i} "
                        + $"is {IrTypes.Describe(actual)} against an output field of "
                        + $"{IrTypes.Describe(expected)}");
                }
            }
        }

        /// <summary>
        /// Whether <paramref name="actual"/> is <paramref name="expected"/> with nothing but
        /// nullability narrower: the same type, nullable wherever the input is and perhaps where it is
        /// not. A COMPOSITE's fields widen the same way, position by position as a row's columns do
        /// (D291, ADR 0077): Calcite makes a record nullable by copying it with every field nullable,
        /// so a union's least restrictive composite has nullable fields over branches whose fields are
        /// not, and an outer join does the same to its nullable side.
        /// </summary>
        private static bool WidensTo(Type actual, Type expected)
        {
            if (actual.Nullable && !expected.Nullable)
            {
                return false;
            }

            if (actual.Kind == TypeKind.Composite && expected.Kind == TypeKind.Composite)
            {
                if (actual.Fields.Count != expected.Fields.Count)
                {
                    return false;
                }

                for (var f = 0; f < actual.Fields.Count; f++)
                {
                    if (!WidensTo(actual.Fields[f].Type, expected.Fields[f].Type))
                    {
                        return false;
                    }
                }

                return true;
            }

            var widened = actual.Clone();
            widened.Nullable = expected.Nullable;
            return expected.Equals(widened);
        }

        private void RequireSameRow(RowType output, RowType input, string path, string what)
        {
            if (!output.Equals(input))
            {
                throw Invalid(
                    "I-IR-4",
                    path,
                    $"{what}, but the output row {IrTypes.Describe(output)} differs from the input row {IrTypes.Describe(input)}");
            }
        }

        /// <summary>
        /// A bound is written one way or the other and never both: the literal the planner knew, or
        /// the parameter the executor reads when the execution starts (D285). A node carrying both
        /// is terminal — there is no rule that says which one wins — so it is refused here rather
        /// than resolved by an executor's private preference.
        /// </summary>
        private static void OneBound(bool hasParam, bool hasLiteral, string path, string node, string field)
        {
            if (hasParam && hasLiteral)
            {
                throw Invalid(
                    "I-IR-5",
                    path,
                    $"{node}.{field} and {node}.{field}_param are both set; a bound is the literal "
                    + "or the parameter and never both");
            }
        }

        private static InvalidPlanException Invalid(string invariant, string path, string detail) =>
            new(invariant, path, detail);
    }
}
