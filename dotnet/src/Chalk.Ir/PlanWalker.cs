namespace Chalk.Ir;

/// <summary>
/// Pre-order enumeration of a plan's relations and expressions. Used by the validator, the printer,
/// the compiler's node registry and the corpus <c>-- expect:</c> assertions.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two walks, because there are two questions</b> (F92, owner 2026-09-17). A
/// <c>RemoteQuery</c> carries the plan it asked a source to run, and whether that subtree is part of
/// "the plan" depends on who is asking. <see cref="Rels(Plan)"/> is the <b>inclusive</b> walk — every
/// relation there is, the pushed subtrees included — and is what a question about the whole statement
/// wants: which tables it reads, what its digest covers, what a policy check must see.
/// <see cref="ExecutedRels(Plan)"/> stops at the boundary, because below it is the <em>source's</em>
/// work and not this client's: it is what parameter binding, the printer and any counting of the
/// operators this executor will run are about.
/// </para>
/// <para>
/// A pushed plan is deliberately not an <see cref="Inputs"/>: an input is a relation whose rows this
/// relation consumes here, and a pushed subtree is a description of work done elsewhere.
/// <see cref="Pushed"/> is where it lives, so a caller that needs it says so rather than reaching
/// into <c>RemoteQuery.PushedPlan</c> itself.
/// </para>
/// </remarks>
public static class PlanWalker
{
    /// <summary>
    /// Every relation in the plan, the pushed subtrees included, root first, inputs left to right.
    /// </summary>
    public static IEnumerable<Rel> Rels(Plan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Root is null ? [] : Rels(plan.Root);
    }

    /// <summary>
    /// Every relation in the subtree, the pushed subtrees included, root first, inputs left to
    /// right; a <c>RemoteQuery</c>'s pushed plan follows its inputs, of which it has none.
    /// </summary>
    public static IEnumerable<Rel> Rels(Rel root)
    {
        ArgumentNullException.ThrowIfNull(root);
        yield return root;
        foreach (var input in Inputs(root))
        {
            foreach (var descendant in Rels(input))
            {
                yield return descendant;
            }
        }

        foreach (var pushed in Pushed(root))
        {
            foreach (var descendant in Rels(pushed))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// Every relation <em>this client</em> executes: the plan as run, which stops at each
    /// <c>RemoteQuery</c> because what is below one is the source's work.
    /// </summary>
    public static IEnumerable<Rel> ExecutedRels(Plan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Root is null ? [] : ExecutedRels(plan.Root);
    }

    /// <summary>The same, from one relation down.</summary>
    public static IEnumerable<Rel> ExecutedRels(Rel root)
    {
        ArgumentNullException.ThrowIfNull(root);
        yield return root;
        foreach (var input in Inputs(root))
        {
            foreach (var descendant in ExecutedRels(input))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// The plan a <c>RemoteQuery</c> asked its source to run, when it carries one — empty for every
    /// other relation, and for a remote query whose text is all there is.
    /// </summary>
    public static IEnumerable<Rel> Pushed(Rel rel)
    {
        ArgumentNullException.ThrowIfNull(rel);
        if (rel.KindCase == Rel.KindOneofCase.RemoteQuery && rel.RemoteQuery.PushedPlan is { } pushed)
        {
            yield return pushed;
        }
    }

    /// <summary>The relation's direct inputs, in field order (left before right for binary nodes).</summary>
    public static IEnumerable<Rel> Inputs(Rel rel)
    {
        ArgumentNullException.ThrowIfNull(rel);
        switch (rel.KindCase)
        {
            case Rel.KindOneofCase.Filter:
                yield return rel.Filter.Input;
                break;
            case Rel.KindOneofCase.Project:
                yield return rel.Project.Input;
                break;
            case Rel.KindOneofCase.Aggregate:
                yield return rel.Aggregate.Input;
                break;
            case Rel.KindOneofCase.Sort:
                yield return rel.Sort.Input;
                break;
            case Rel.KindOneofCase.Fetch:
                yield return rel.Fetch.Input;
                break;
            case Rel.KindOneofCase.Join:
                yield return rel.Join.Left;
                yield return rel.Join.Right;
                break;
            case Rel.KindOneofCase.SetOp:
                foreach (var input in rel.SetOp.Inputs)
                {
                    yield return input;
                }

                break;
            case Rel.KindOneofCase.Window:
                yield return rel.Window.Input;
                break;
            case Rel.KindOneofCase.HashJoin:
                yield return rel.HashJoin.Left;
                yield return rel.HashJoin.Right;
                break;
            case Rel.KindOneofCase.MergeJoin:
                yield return rel.MergeJoin.Left;
                yield return rel.MergeJoin.Right;
                break;
            case Rel.KindOneofCase.NestedLoopJoin:
                yield return rel.NestedLoopJoin.Left;
                yield return rel.NestedLoopJoin.Right;
                break;
            case Rel.KindOneofCase.AsOfJoin:
                yield return rel.AsOfJoin.Left;
                yield return rel.AsOfJoin.Right;
                break;
            case Rel.KindOneofCase.HashAggregate:
                yield return rel.HashAggregate.Aggregate.Input;
                break;
            case Rel.KindOneofCase.StreamAggregate:
                yield return rel.StreamAggregate.Aggregate.Input;
                break;
            case Rel.KindOneofCase.TopN:
                yield return rel.TopN.Input;
                break;
            case Rel.KindOneofCase.Hop:
                yield return rel.Hop.Input;
                break;
            case Rel.KindOneofCase.Session:
                yield return rel.Session.Input;
                break;
            case Rel.KindOneofCase.Unnest:
                yield return rel.Unnest.Input;
                break;
            case Rel.KindOneofCase.LookupJoin:
                yield return rel.LookupJoin.Driving;
                yield return rel.LookupJoin.Lookup;
                break;
            case Rel.KindOneofCase.AdaptiveJoin:

                // Both branches, and the small side they replay. The digest covers both (D97), the
                // validator has to type-check both, and the printer shows both — so every one of
                // them travels through here rather than through three hand-written recursions.
                yield return rel.AdaptiveJoin.Small;
                yield return rel.AdaptiveJoin.Lookup.Driving;
                yield return rel.AdaptiveJoin.Lookup.Lookup;
                yield return rel.AdaptiveJoin.Local;
                break;
            case Rel.KindOneofCase.PartitionedScan:
                foreach (var partition in rel.PartitionedScan.Partitions)
                {
                    yield return partition;
                }

                break;
            case Rel.KindOneofCase.Read:
            case Rel.KindOneofCase.VirtualTable:
            case Rel.KindOneofCase.IndexLookup:
            case Rel.KindOneofCase.RemoteQuery:
            case Rel.KindOneofCase.TableFunctionScan:
            case Rel.KindOneofCase.MaterialisedInput:
            case Rel.KindOneofCase.None:
            default:
                break;
        }
    }

    /// <summary>
    /// The expressions this relation owns, in declaration order — not those of its inputs, and not
    /// the sub-expressions (use <see cref="Exprs(Expr)"/> for those).
    /// </summary>
    public static IEnumerable<Expr> OwnExprs(Rel rel)
    {
        ArgumentNullException.ThrowIfNull(rel);
        foreach (var collation in rel.Collations)
        {
            foreach (var field in collation.Fields)
            {
                if (field.Expr is not null)
                {
                    yield return field.Expr;
                }
            }
        }

        switch (rel.KindCase)
        {
            case Rel.KindOneofCase.Read:
                if (rel.Read.Filter is not null)
                {
                    yield return rel.Read.Filter;
                }

                break;
            case Rel.KindOneofCase.Filter:
                yield return rel.Filter.Condition;
                break;
            case Rel.KindOneofCase.Project:
                foreach (var e in rel.Project.Exprs)
                {
                    yield return e;
                }

                break;
            case Rel.KindOneofCase.Aggregate:
                foreach (var e in AggregateExprs(rel.Aggregate))
                {
                    yield return e;
                }

                break;
            case Rel.KindOneofCase.HashAggregate:
                foreach (var e in AggregateExprs(rel.HashAggregate.Aggregate))
                {
                    yield return e;
                }

                break;
            case Rel.KindOneofCase.StreamAggregate:
                foreach (var e in AggregateExprs(rel.StreamAggregate.Aggregate))
                {
                    yield return e;
                }

                break;
            case Rel.KindOneofCase.Sort:
                foreach (var f in rel.Sort.Fields)
                {
                    yield return f.Expr;
                }

                break;
            case Rel.KindOneofCase.TopN:
                foreach (var f in rel.TopN.Fields)
                {
                    yield return f.Expr;
                }

                break;
            case Rel.KindOneofCase.Join:
                if (rel.Join.Condition is not null)
                {
                    yield return rel.Join.Condition;
                }

                break;
            case Rel.KindOneofCase.HashJoin:
                if (rel.HashJoin.PostJoinFilter is not null)
                {
                    yield return rel.HashJoin.PostJoinFilter;
                }

                break;
            case Rel.KindOneofCase.MergeJoin:
                if (rel.MergeJoin.PostJoinFilter is not null)
                {
                    yield return rel.MergeJoin.PostJoinFilter;
                }

                break;
            case Rel.KindOneofCase.NestedLoopJoin:
                if (rel.NestedLoopJoin.Condition is not null)
                {
                    yield return rel.NestedLoopJoin.Condition;
                }

                break;
            case Rel.KindOneofCase.AsOfJoin:
                // Keys and time columns are field indexes, not expressions.
                break;
            case Rel.KindOneofCase.Window:
                foreach (var field in rel.Window.Order)
                {
                    yield return field.Expr;
                }

                if (rel.Window.Frame is { } frame)
                {
                    if (frame.Lower?.Offset is { } lower)
                    {
                        yield return lower;
                    }

                    if (frame.Upper?.Offset is { } upper)
                    {
                        yield return upper;
                    }
                }

                foreach (var call in rel.Window.Calls)
                {
                    foreach (var arg in call.Args)
                    {
                        yield return arg;
                    }

                    foreach (var order in call.OrderBy)
                    {
                        yield return order.Expr;
                    }
                }

                break;
            case Rel.KindOneofCase.VirtualTable:
                foreach (var row in rel.VirtualTable.Rows)
                {
                    foreach (var v in row.Values)
                    {
                        yield return v;
                    }
                }

                break;
            case Rel.KindOneofCase.IndexLookup:
                foreach (var range in rel.IndexLookup.Ranges)
                {
                    foreach (var e in range.Lower)
                    {
                        yield return e;
                    }

                    foreach (var e in range.Upper)
                    {
                        yield return e;
                    }
                }

                if (rel.IndexLookup.Residual is not null)
                {
                    yield return rel.IndexLookup.Residual;
                }

                break;
            case Rel.KindOneofCase.RemoteQuery:
                foreach (var p in rel.RemoteQuery.Parameters)
                {
                    yield return p;
                }

                break;
            case Rel.KindOneofCase.Hop:
                yield return rel.Hop.Slide;
                yield return rel.Hop.Size;
                break;
            case Rel.KindOneofCase.Session:
                yield return rel.Session.Gap;
                break;
            case Rel.KindOneofCase.TableFunctionScan:
                foreach (var argument in rel.TableFunctionScan.Args)
                {
                    yield return argument;
                }

                break;
            case Rel.KindOneofCase.LookupJoin:
                if (rel.LookupJoin.PostJoinFilter is not null)
                {
                    yield return rel.LookupJoin.PostJoinFilter;
                }

                break;
            case Rel.KindOneofCase.AdaptiveJoin:
                if (rel.AdaptiveJoin.Lookup.PostJoinFilter is not null)
                {
                    yield return rel.AdaptiveJoin.Lookup.PostJoinFilter;
                }

                break;
            case Rel.KindOneofCase.PartitionedScan:
                foreach (var match in rel.PartitionedScan.Matches)
                {
                    if (match.Value is not null)
                    {
                        yield return match.Value;
                    }
                }

                if (rel.PartitionedScan.KeySet is not null)
                {
                    yield return rel.PartitionedScan.KeySet;
                }

                break;
            case Rel.KindOneofCase.Fetch:
            case Rel.KindOneofCase.SetOp:
            case Rel.KindOneofCase.Unnest:
            case Rel.KindOneofCase.MaterialisedInput:
            case Rel.KindOneofCase.None:
            default:
                break;
        }
    }

    /// <summary>Every expression in the tree, root first, arguments left to right.</summary>
    public static IEnumerable<Expr> Exprs(Expr root)
    {
        ArgumentNullException.ThrowIfNull(root);
        yield return root;
        foreach (var child in Children(root))
        {
            foreach (var descendant in Exprs(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>The expression's direct arguments, in evaluation order.</summary>
    public static IEnumerable<Expr> Children(Expr expr)
    {
        ArgumentNullException.ThrowIfNull(expr);
        switch (expr.KindCase)
        {
            case Expr.KindOneofCase.Call:
                foreach (var a in expr.Call.Args)
                {
                    yield return a;
                }

                break;
            case Expr.KindOneofCase.Cast:
                yield return expr.Cast.Input;
                break;
            case Expr.KindOneofCase.IfThen:
                foreach (var clause in expr.IfThen.Clauses)
                {
                    yield return clause.Condition;
                    yield return clause.Result;
                }

                if (expr.IfThen.ElseBranch is not null)
                {
                    yield return expr.IfThen.ElseBranch;
                }

                break;
            case Expr.KindOneofCase.InList:
                yield return expr.InList.Value;
                foreach (var o in expr.InList.Options)
                {
                    yield return o;
                }

                break;
            case Expr.KindOneofCase.KeySetMatch:
                foreach (var column in expr.KeySetMatch.Columns)
                {
                    yield return column;
                }

                break;
            case Expr.KindOneofCase.FieldRef:
            case Expr.KindOneofCase.Literal:
            case Expr.KindOneofCase.Param:
            case Expr.KindOneofCase.EnumArg:
            case Expr.KindOneofCase.KeySet:
            case Expr.KindOneofCase.None:
            default:
                break;
        }
    }

    /// <summary>
    /// Every expression <em>this client</em> evaluates — the executed walk's, not the inclusive
    /// one's.
    /// </summary>
    /// <remarks>
    /// This is the expression walk parameter binding is about, and parameter binding is the
    /// executor's: <see cref="BoundScalars"/> numbers the bound scalars by the order a walk first
    /// meets them and the compiler fills the slots by the same order, so what counts is what this
    /// client will evaluate. A pushed plan's parameters are the <c>RemoteQuery</c>'s own
    /// <c>parameters</c> list, which is this walk's, and the subtree below it is the source's
    /// business.
    /// </remarks>
    public static IEnumerable<Expr> AllExprs(Plan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return ExecutedRels(plan).SelectMany(OwnExprs).SelectMany(Exprs);
    }

    /// <summary>
    /// True when the plan <em>as executed</em> contains at least one relation of this kind.
    /// </summary>
    /// <remarks>
    /// The executed walk, because this pair is the corpus's <c>-- expect: has(…)</c> and
    /// <c>not(…)</c> vocabulary and that vocabulary is about what this client runs: a pushdown
    /// query's <c>not(Filter)</c> says the filter reached the source, and the pushed plan is where
    /// it went. A question about the whole statement asks <see cref="Rels(Plan)"/> directly.
    /// </remarks>
    public static bool Has(Plan plan, Rel.KindOneofCase kind) =>
        ExecutedRels(plan).Any(r => r.KindCase == kind);

    /// <summary>How many relations of this kind the plan as executed contains.</summary>
    public static int Count(Plan plan, Rel.KindOneofCase kind) =>
        ExecutedRels(plan).Count(r => r.KindCase == kind);

    private static IEnumerable<Expr> AggregateExprs(Aggregate aggregate)
    {
        foreach (var measure in aggregate.Measures)
        {
            foreach (var arg in measure.Args)
            {
                yield return arg;
            }

            if (measure.Filter is not null)
            {
                yield return measure.Filter;
            }

            foreach (var field in measure.OrderBy)
            {
                yield return field.Expr;
            }
        }
    }
}
