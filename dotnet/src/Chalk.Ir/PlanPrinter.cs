using System.Globalization;
using System.Numerics;
using System.Text;

namespace Chalk.Ir;

/// <summary>
/// Indented text rendering of a plan — one relation per line, expressions inline. For diagnostics
/// and test failure messages only; nothing parses it back (<c>docs/design/02-ir.md</c> §1).
/// </summary>
public static class PlanPrinter
{
    /// <summary>Renders the whole plan: header line, parameter types, then the relation tree.</summary>
    public static string Print(Plan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Plan ir_version={plan.IrVersion}")
          .Append(CultureInfo.InvariantCulture, $" digest={plan.PlanDigest:x16}")
          .Append(CultureInfo.InvariantCulture, $" context_id={plan.ContextId}")
          .Append(CultureInfo.InvariantCulture, $" catalog_epoch={plan.CatalogEpoch}");
        if (plan.ParameterTypes.Count > 0)
        {
            sb.Append(" params=[")
              .Append(string.Join(", ", plan.ParameterTypes.Select(IrTypes.Describe)))
              .Append(']');
        }

        sb.AppendLine();
        if (plan.Root is not null)
        {
            Print(sb, plan.Root, 1);
        }

        return sb.ToString();
    }

    /// <summary>Renders one relation and its inputs.</summary>
    public static string Print(Rel rel)
    {
        ArgumentNullException.ThrowIfNull(rel);
        var sb = new StringBuilder();
        Print(sb, rel, 0);
        return sb.ToString();
    }

    /// <summary>Renders one expression on a single line.</summary>
    public static string Print(Expr expr)
    {
        ArgumentNullException.ThrowIfNull(expr);
        var sb = new StringBuilder();
        Append(sb, expr);
        return sb.ToString();
    }

    private static void Print(StringBuilder sb, Rel rel, int depth)
    {
        sb.Append(' ', depth * 2).Append(Describe(rel));
        if (rel.PolicyInjected)
        {
            // The entitlement rewrite put this node here rather than the query (step 26,
            // 16-entitlements.md §3.12). It is an ordinary node otherwise; the word is for a reader.
            sb.Append(" entitled");
        }

        sb.Append(CultureInfo.InvariantCulture, $" rows={rel.EstRowCount.ToString("0.###", CultureInfo.InvariantCulture)}");
        sb.Append(CultureInfo.InvariantCulture, $" out={IrTypes.Describe(rel.RowType)}");
        if (rel.Collations.Count > 0)
        {
            sb.Append(" collations=[")
              .Append(string.Join(
                  ", ",
                  rel.Collations.Select(c => "(" + string.Join(", ", c.Fields.Select(Describe)) + ")")))
              .Append(']');
        }

        sb.AppendLine();
        foreach (var input in PlanWalker.Inputs(rel))
        {
            Print(sb, input, depth + 1);
        }
    }

    private static string Describe(Rel rel) => rel.KindCase switch
    {
        Rel.KindOneofCase.Read => $"Read {Describe(rel.Read.Table)} projection=[{string.Join(",", rel.Read.Projection)}]"
            + (rel.Read.Filter is null ? string.Empty : $" filter={Print(rel.Read.Filter)}")
            // The entitlement rewrite's per-column outcomes, in the table's own column ordinals
            // (step 26, 16-entitlements.md §3.10). Empty for an unentitled read, so no recorded plan
            // of a catalog without entitlements gains a word; FULL is elided, so what a reader sees
            // is what the leaf did not disclose plainly.
            + Disclosures(rel.Read)
            // And the descriptor it was compiled under (D231), which is in the digest: two plans
            // that differ only by a changed policy differ here too, and a reader can see which.
            + (rel.Read.DescriptorHash.Length == 0
                ? string.Empty
                : $" descriptor={rel.Read.DescriptorHash}"),
        Rel.KindOneofCase.Filter => $"Filter {Print(rel.Filter.Condition)}",
        Rel.KindOneofCase.Project => $"Project [{string.Join(", ", rel.Project.Exprs.Select(Print))}]",
        Rel.KindOneofCase.Aggregate => "Aggregate " + Describe(rel.Aggregate),
        Rel.KindOneofCase.HashAggregate => "HashAggregate " + Describe(rel.HashAggregate.Aggregate),
        Rel.KindOneofCase.StreamAggregate => "StreamAggregate " + Describe(rel.StreamAggregate.Aggregate),
        Rel.KindOneofCase.Sort => $"Sort [{string.Join(", ", rel.Sort.Fields.Select(Describe))}]",
        Rel.KindOneofCase.Fetch => "Fetch offset=" + rel.Fetch.Offset.ToString(CultureInfo.InvariantCulture)
            + (rel.Fetch.HasCount ? " count=" + rel.Fetch.Count.ToString(CultureInfo.InvariantCulture) : " count=all"),
        Rel.KindOneofCase.TopN => $"TopN [{string.Join(", ", rel.TopN.Fields.Select(Describe))}]"
            + string.Create(CultureInfo.InvariantCulture, $" offset={rel.TopN.Offset} count={rel.TopN.Count}"),
        Rel.KindOneofCase.Join => $"Join {rel.Join.Type} on "
            + (rel.Join.Condition is null ? "true" : Print(rel.Join.Condition)),
        Rel.KindOneofCase.HashJoin => $"HashJoin {rel.HashJoin.Type} "
            + $"left_keys=[{string.Join(",", rel.HashJoin.LeftKeys)}] right_keys=[{string.Join(",", rel.HashJoin.RightKeys)}]"
            + (rel.HashJoin.PostJoinFilter is null ? string.Empty : $" residual={Print(rel.HashJoin.PostJoinFilter)}"),
        Rel.KindOneofCase.MergeJoin => $"MergeJoin {rel.MergeJoin.Type} "
            + $"left_keys=[{string.Join(",", rel.MergeJoin.LeftKeys)}] right_keys=[{string.Join(",", rel.MergeJoin.RightKeys)}]"
            + (rel.MergeJoin.PostJoinFilter is null ? string.Empty : $" residual={Print(rel.MergeJoin.PostJoinFilter)}"),
        Rel.KindOneofCase.NestedLoopJoin => $"NestedLoopJoin {rel.NestedLoopJoin.Type} on "
            + (rel.NestedLoopJoin.Condition is null ? "true" : Print(rel.NestedLoopJoin.Condition)),
        Rel.KindOneofCase.AsOfJoin => $"AsOfJoin {rel.AsOfJoin.Type} "
            + $"left_keys=[{string.Join(",", rel.AsOfJoin.LeftKeys)}] right_keys=[{string.Join(",", rel.AsOfJoin.RightKeys)}]"
            + string.Create(
                CultureInfo.InvariantCulture,
                $" match=${rel.AsOfJoin.LeftTime} {Describe(rel.AsOfJoin.Match)} ${rel.AsOfJoin.RightTime}"),
        Rel.KindOneofCase.SetOp => $"SetOp {rel.SetOp.Kind} inputs={rel.SetOp.Inputs.Count}",
        Rel.KindOneofCase.Hop => $"Hop time={Ref(rel.Hop.TimeColumn)} "
            + $"slide={Print(rel.Hop.Slide)} size={Print(rel.Hop.Size)}",
        Rel.KindOneofCase.Session => $"Session partition=[{string.Join(",", rel.Session.PartitionKeys)}] "
            + $"time={Ref(rel.Session.TimeColumn)} gap={Print(rel.Session.Gap)}",
        Rel.KindOneofCase.Unnest => $"Unnest list={Ref(rel.Unnest.ListColumn)}"
            + (rel.Unnest.WithOrdinality ? " with_ordinality" : string.Empty)
            + (rel.Unnest.KeepEmpty ? " keep_empty" : string.Empty),
        Rel.KindOneofCase.TableFunctionScan =>
            $"TableFunctionScan {rel.TableFunctionScan.Function}("
            + string.Join(", ", rel.TableFunctionScan.Args.Select(Print)) + ")",
        Rel.KindOneofCase.Window => Describe(rel.Window),
        // A relation the host bound by name (step 26, 16-entitlements.md §2). Its name and its row
        // type are all the plan carries; the rows are the host's and stay out of every artefact.
        Rel.KindOneofCase.BoundTable => $"BoundTable {rel.BoundTable.Name}",
        Rel.KindOneofCase.VirtualTable => $"VirtualTable rows={rel.VirtualTable.Rows.Count}"
            + (rel.VirtualTable.Rows.Count == 0
                ? string.Empty
                : " " + string.Join(
                    ", ",
                    rel.VirtualTable.Rows.Select(r => "(" + string.Join(", ", r.Values.Select(Print)) + ")"))),
        Rel.KindOneofCase.IndexLookup => $"IndexLookup {Describe(rel.IndexLookup.Table)} index={rel.IndexLookup.Index} "
            + $"ranges={rel.IndexLookup.Ranges.Count} projection=[{string.Join(",", rel.IndexLookup.Projection)}]"
            + (rel.IndexLookup.Residual is null ? string.Empty : $" residual={Print(rel.IndexLookup.Residual)}"),
        Rel.KindOneofCase.RemoteQuery => $"RemoteQuery source={rel.RemoteQuery.SourceId} dialect={rel.RemoteQuery.Dialect} "
            + $"sql={Quote(rel.RemoteQuery.QueryText)}"
            + (rel.RemoteQuery.PushedPlan is null ? string.Empty : " pushed_plan=yes"),
        Rel.KindOneofCase.LookupJoin => $"LookupJoin {rel.LookupJoin.Type} "
            + $"driving_keys=[{string.Join(",", rel.LookupJoin.DrivingKeys)}] "
            + $"lookup_keys=[{string.Join(",", rel.LookupJoin.LookupKeys)}] "
            + string.Create(CultureInfo.InvariantCulture, $"max_keys_per_call={rel.LookupJoin.MaxKeysPerCall}")
            + (rel.LookupJoin.KeySetRows ? " key_set=rows" : " key_set=in")
            + (rel.LookupJoin.PostJoinFilter is null ? string.Empty : $" residual={Print(rel.LookupJoin.PostJoinFilter)}"),
        Rel.KindOneofCase.AdaptiveJoin => $"AdaptiveJoin {rel.AdaptiveJoin.Lookup.Type} "
            + string.Create(
                CultureInfo.InvariantCulture,
                $"key=${rel.AdaptiveJoin.Key} max_keys={rel.AdaptiveJoin.MaxKeys} slot={rel.AdaptiveJoin.Slot}"),
        Rel.KindOneofCase.MaterialisedInput => string.Create(
            CultureInfo.InvariantCulture, $"MaterialisedInput slot={rel.MaterialisedInput.Slot}"),
        Rel.KindOneofCase.PartitionedScan => $"PartitionedScan partitions={rel.PartitionedScan.Partitions.Count} "
            + "values=[" + string.Join(
                ", ",
                rel.PartitionedScan.Matches.Select(m => m.Value is null ? "range" : Print(m.Value)))
            + "]",
        Rel.KindOneofCase.None => "<no kind set>",
        _ => $"<unknown kind {(int)rel.KindCase}>",
    };

    /// <summary>The match comparison, read as <c>left_time op right_time</c>.</summary>
    private static string Describe(AsOfMatch match) => match switch
    {
        AsOfMatch.Lt => "<",
        AsOfMatch.Le => "<=",
        AsOfMatch.Gt => ">",
        AsOfMatch.Ge => ">=",
        _ => "?",
    };

    private static string Describe(Window window)
    {
        var sb = new StringBuilder("Window partition=[")
            .Append(string.Join(",", window.PartitionKeys.Select(k => "$" + k.ToString(CultureInfo.InvariantCulture))))
            .Append("] order=[")
            .Append(string.Join(", ", window.Order.Select(Describe)))
            .Append("] frame=")
            .Append(Describe(window.Frame))
            .Append(" calls=[")
            .Append(string.Join(", ", window.Calls.Select(Describe)))
            .Append(']');
        return sb.ToString();
    }

    private static string Describe(WindowFrame? frame)
    {
        if (frame is null)
        {
            return "<none>";
        }

        var mode = frame.Mode switch
        {
            FrameMode.Rows => "ROWS",
            FrameMode.Range => "RANGE",
            FrameMode.Groups => "GROUPS",
            _ => "UNSPECIFIED",
        };
        var exclusion = frame.Exclusion switch
        {
            FrameExclusion.CurrentRow => " EXCLUDE CURRENT ROW",
            FrameExclusion.Group => " EXCLUDE GROUP",
            FrameExclusion.Ties => " EXCLUDE TIES",
            _ => string.Empty,
        };
        return $"{mode} BETWEEN {Describe(frame.Lower)} AND {Describe(frame.Upper)}{exclusion}";
    }

    private static string Describe(FrameBound? bound) => bound is null
        ? "<none>"
        : bound.Kind switch
        {
            FrameBoundKind.UnboundedPreceding => "UNBOUNDED PRECEDING",
            FrameBoundKind.Preceding => Offset(bound) + " PRECEDING",
            FrameBoundKind.CurrentRow => "CURRENT ROW",
            FrameBoundKind.Following => Offset(bound) + " FOLLOWING",
            FrameBoundKind.UnboundedFollowing => "UNBOUNDED FOLLOWING",
            _ => "UNSPECIFIED",
        };

    private static string Offset(FrameBound bound) =>
        bound.Offset is null ? "<none>" : Print(bound.Offset);

    private static string Describe(WindowCall call)
    {
        var name = call.UserFunction.Length > 0
            ? call.UserFunction
            : call.FunctionCase == WindowCall.FunctionOneofCase.Aggregate
                ? call.Aggregate.ToString().ToUpperInvariant()
                : call.WindowFunction.ToString().ToUpperInvariant();
        var sb = new StringBuilder(name).Append('(');
        if (call.Distinct)
        {
            sb.Append("DISTINCT ");
        }

        sb.Append(string.Join(", ", call.Args.Select(Print))).Append(')');
        if (call.OrderBy.Count > 0)
        {
            sb.Append(" WITHIN GROUP (ORDER BY ")
                .Append(string.Join(", ", call.OrderBy.Select(Describe)))
                .Append(')');
        }

        if (call.IgnoreNulls)
        {
            sb.Append(" IGNORE NULLS");
        }

        return sb.Append("->").Append(IrTypes.Describe(call.Type)).ToString();
    }

    private static string Describe(Aggregate aggregate)
    {
        var groupings = string.Join(
            " ", aggregate.Groupings.Select(g => "keys=[" + string.Join(",", g.Keys) + "]"));
        var measures = string.Join(", ", aggregate.Measures.Select(Describe));
        return $"{groupings} measures=[{measures}]";
    }

    private static string Describe(Measure measure)
    {
        var name = measure.UserFunction.Length > 0
            ? measure.UserFunction
            : measure.Function.ToString().ToUpperInvariant();
        var sb = new StringBuilder(name);
        sb.Append('(');
        if (measure.Distinct)
        {
            sb.Append("DISTINCT ");
        }

        sb.Append(string.Join(", ", measure.Args.Select(Print))).Append(')');
        if (measure.OrderBy.Count > 0)
        {
            sb.Append(" WITHIN GROUP (ORDER BY ")
                .Append(string.Join(", ", measure.OrderBy.Select(Describe)))
                .Append(')');
        }

        if (measure.Filter is not null)
        {
            sb.Append(" FILTER ").Append(Print(measure.Filter));
        }

        sb.Append("->").Append(IrTypes.Describe(measure.Type));
        return sb.ToString();
    }

    private static string Describe(TableRef? table) =>
        table is null ? "<none>" : $"{table.SourceId}.{table.Schema}.{table.Table}";

    /// <summary>
    /// The entitled read's outcomes, <c>FULL</c> elided: a reader wants the columns the leaf did not
    /// hand over plainly, and an entitled table is mostly plain columns. An unentitled read carries
    /// no entry at all and prints nothing.
    /// </summary>
    private static string Disclosures(Read read)
    {
        var interesting = read.Disclosures
            .Where(d => d.Outcome != DisclosureOutcome.Full)
            .Select(d => $"{d.Column}:{Describe(d.Outcome)}")
            .ToList();
        return interesting.Count == 0 ? string.Empty : $" disclosures=[{string.Join(", ", interesting)}]";
    }

    /// <summary>
    /// The disclosure words plan text spells, upper-cased as every other string form of a disclosure
    /// is (D218 amended): the descriptor's own vocabulary and the wire enum's, once its prefix is
    /// dropped.
    /// </summary>
    private static string Describe(DisclosureOutcome outcome) => outcome switch
    {
        DisclosureOutcome.Full => "FULL",
        DisclosureOutcome.Masked => "MASKED",
        DisclosureOutcome.Aggregate => "AGGREGATE",
        DisclosureOutcome.Redacted => "REDACTED",
        DisclosureOutcome.PerRow => "PER_ROW",
        DisclosureOutcome.Tested => "TESTED",
        _ => "UNSPECIFIED",
    };

    private static string Describe(SortField field) =>
        (field.Expr is null ? "<none>" : Print(field.Expr)) + " " + Describe(field.Direction);

    private static string Describe(SortDirection direction) => direction switch
    {
        SortDirection.AscNullsFirst => "ASC NULLS FIRST",
        SortDirection.AscNullsLast => "ASC NULLS LAST",
        SortDirection.DescNullsFirst => "DESC NULLS FIRST",
        SortDirection.DescNullsLast => "DESC NULLS LAST",
        _ => "UNSPECIFIED",
    };

    private static void Append(StringBuilder sb, Expr expr)
    {
        switch (expr.KindCase)
        {
            case Expr.KindOneofCase.FieldRef:
                sb.Append('$').Append(expr.FieldRef.Index.ToString(CultureInfo.InvariantCulture));
                break;
            case Expr.KindOneofCase.Literal:
                sb.Append(Describe(expr.Literal, expr.Type));
                break;
            case Expr.KindOneofCase.Param:
                // A named bound value reads as the policy wrote it — `?@ctx.user` — rather than as
                // an index it does not have (16-entitlements.md §2, D209).
                sb.Append('?').Append(
                    expr.Param.BoundKey.Length > 0
                        ? "@ctx." + expr.Param.BoundKey
                        : expr.Param.Index.ToString(CultureInfo.InvariantCulture));
                break;
            case Expr.KindOneofCase.EnumArg:
                sb.Append('#').Append(expr.EnumArg.Value);
                break;
            case Expr.KindOneofCase.KeySet:
                sb.Append("<key set ")
                    .Append(expr.KeySet.Slot.ToString(CultureInfo.InvariantCulture))
                    .Append('>');
                break;
            case Expr.KindOneofCase.KeySetMatch:
                // `(a, b) IN <key set 0>` — the composite twin of an InList over a KeySetParam
                // (F50), printed the way the generated SQL spells it.
                sb.Append('(');
                for (var i = 0; i < expr.KeySetMatch.Columns.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    Append(sb, expr.KeySetMatch.Columns[i]);
                }

                sb.Append(") IN <key set ")
                    .Append(expr.KeySetMatch.KeySet.Slot.ToString(CultureInfo.InvariantCulture))
                    .Append('>');
                break;
            case Expr.KindOneofCase.Cast:
                sb.Append("CAST(");
                Append(sb, expr.Cast.Input);
                sb.Append(" AS ").Append(IrTypes.Describe(expr.Type));
                if (expr.Cast.OnFailure == CastFailure.Null)
                {
                    sb.Append(" ON FAILURE NULL");
                }

                sb.Append(')');
                break;
            case Expr.KindOneofCase.Call:
                sb.Append(
                    expr.Call.UserFunction.Length > 0
                        ? expr.Call.UserFunction
                        : Name(expr.Call.Function)).Append('(');
                for (var i = 0; i < expr.Call.Args.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    Append(sb, expr.Call.Args[i]);
                }

                sb.Append(')');
                break;
            case Expr.KindOneofCase.IfThen:
                sb.Append("CASE");
                foreach (var clause in expr.IfThen.Clauses)
                {
                    sb.Append(" WHEN ");
                    Append(sb, clause.Condition);
                    sb.Append(" THEN ");
                    Append(sb, clause.Result);
                }

                sb.Append(" ELSE ");
                if (expr.IfThen.ElseBranch is null)
                {
                    sb.Append("<none>");
                }
                else
                {
                    Append(sb, expr.IfThen.ElseBranch);
                }

                sb.Append(" END");
                break;
            case Expr.KindOneofCase.InList:
                Append(sb, expr.InList.Value);
                sb.Append(" IN (");
                for (var i = 0; i < expr.InList.Options.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    Append(sb, expr.InList.Options[i]);
                }

                sb.Append(')');
                break;
            case Expr.KindOneofCase.None:
            default:
                sb.Append("<no kind set>");
                break;
        }
    }

    private static string Name(FunctionId function) => function.ToString().ToUpperInvariant();

    /// <summary>A field index as the plan text writes one: <c>$3</c>.</summary>
    private static string Ref(uint index) => "$" + index.ToString(CultureInfo.InvariantCulture);

    private static string Describe(Literal literal, Type? type) => literal.ValueCase switch
    {
        Literal.ValueOneofCase.IsNull => "NULL:" + IrTypes.Describe(type),
        Literal.ValueOneofCase.BoolValue => literal.BoolValue ? "true" : "false",
        Literal.ValueOneofCase.I8Value => literal.I8Value.ToString(CultureInfo.InvariantCulture),
        Literal.ValueOneofCase.I16Value => literal.I16Value.ToString(CultureInfo.InvariantCulture),
        Literal.ValueOneofCase.I32Value => literal.I32Value.ToString(CultureInfo.InvariantCulture),
        Literal.ValueOneofCase.I64Value => literal.I64Value.ToString(CultureInfo.InvariantCulture),
        Literal.ValueOneofCase.Fp32Value => literal.Fp32Value.ToString("R", CultureInfo.InvariantCulture),
        Literal.ValueOneofCase.Fp64Value => literal.Fp64Value.ToString("R", CultureInfo.InvariantCulture),
        Literal.ValueOneofCase.StringValue => Quote(literal.StringValue),
        Literal.ValueOneofCase.BinaryValue => "x'" + Convert.ToHexStringLower(literal.BinaryValue.Span) + "'",
        Literal.ValueOneofCase.DateValue => "DATE " + literal.DateValue.ToString(CultureInfo.InvariantCulture),
        Literal.ValueOneofCase.TimeValue => "TIME " + literal.TimeValue.ToString(CultureInfo.InvariantCulture),
        Literal.ValueOneofCase.TimestampValue => "TIMESTAMP " + literal.TimestampValue.ToString(CultureInfo.InvariantCulture),
        Literal.ValueOneofCase.TimestampTzValue => "TIMESTAMP_TZ " + literal.TimestampTzValue.ToString(CultureInfo.InvariantCulture),
        Literal.ValueOneofCase.DecimalValue => DescribeDecimal(literal.DecimalValue, type),
        Literal.ValueOneofCase.UuidValue => "UUID " + Convert.ToHexStringLower(literal.UuidValue.Span),
        Literal.ValueOneofCase.IntervalDayValue => "INTERVAL " + literal.IntervalDayValue.ToString(CultureInfo.InvariantCulture) + "us",
        Literal.ValueOneofCase.IntervalYearValue => "INTERVAL " + literal.IntervalYearValue.ToString(CultureInfo.InvariantCulture) + "mo",
        Literal.ValueOneofCase.ListValue => "["
            + string.Join(
                ", ", literal.ListValue.Elements.Select(e => Describe(e, type?.Element)))
            + "]",
        _ => "<no value set>",
    };

    private static string DescribeDecimal(DecimalValue value, Type? type)
    {
        var scale = (int)(type?.Scale ?? 0);
        var unscaled = value.Unscaled.Length == 16
            ? new BigInteger(value.Unscaled.Span, isUnsigned: false, isBigEndian: false)
            : BigInteger.Zero;
        var text = BigInteger.Abs(unscaled).ToString(CultureInfo.InvariantCulture);
        var sign = unscaled.Sign < 0 ? "-" : string.Empty;
        if (scale == 0)
        {
            return sign + text;
        }

        text = text.PadLeft(scale + 1, '0');
        return sign + text[..^scale] + "." + text[^scale..];
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
