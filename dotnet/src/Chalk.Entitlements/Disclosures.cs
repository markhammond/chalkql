using Apache.Arrow;
using ArrowSchema = Apache.Arrow.Schema;

using Chalk.Client;

namespace Chalk.Entitlements;

/// <summary>
/// The planner's entitlement report, as the client's own types
/// (<c>docs/design/16-entitlements.md</c> §3.12).
/// </summary>
/// <remarks>
/// Nothing here is reachable for a plan over a catalog without entitlements: the report is absent,
/// every column reads <see cref="ReportedDisclosure.Full"/>, and the Arrow schema comes back
/// unchanged by reference, which keeps §0's "no bytes and no work when unused" true here too.
/// </remarks>
internal static class Disclosures
{
    /// <summary>The Arrow field metadata key a typed consumer reads (D161).</summary>
    internal const string MetadataKey = "chalk.disclosure";

    /// <summary>The message of type <typeparamref name="T"/> one of these extensions carries.</summary>
    internal static T? Find<T>(IReadOnlyList<Google.Protobuf.WellKnownTypes.Any> extensions)
        where T : class, Google.Protobuf.IMessage<T>, new()
    {
        var descriptor = new T().Descriptor;
        foreach (var extension in extensions)
        {
            if (extension.Is(descriptor))
            {
                return extension.Unpack<T>();
            }
        }

        return null;
    }

    /// <summary>
    /// The rewrite's report out of the prepared query's extension slot (step 26c, D212), as this
    /// package's own types. Empty when no extension carried one, which is every plan the pass did
    /// not run for.
    /// </summary>
    internal static EntitlementsReport Report(PreparedQuery prepared)
    {
        var reported = prepared.Extension<Rpc.EntitlementsReport>();
        if (reported is null)
        {
            return new EntitlementsReport
            {
                Columns = Columns(prepared.OutputSchema, null),
                Tables = [],
                DescriptorHashes = [],
            };
        }

        return new EntitlementsReport
        {
            Columns = Columns(prepared.OutputSchema, reported),
            Tables = Tables(reported),
            DescriptorHashes = [.. reported.DescriptorHashes],
            RequiredScalars = [.. reported.RequiredScalars],
            RequiredRelations = [.. reported.RequiredRelations],
            OmitDegradedToPlaceholder = reported.OmitDegradedToPlaceholder,
        };
    }

    /// <summary>The report's per-column labels in the IR's own vocabulary, for I-IR-E's last clause.</summary>
    internal static IReadOnlyList<Chalk.Ir.DisclosureOutcome>? Outcomes(PreparedQuery prepared) =>
        Outcomes(prepared.Extension<Rpc.EntitlementsReport>());

    /// <summary>The oracle, as this package's own types (D207).</summary>
    internal static EntitlementsExplanation Explanation(Rpc.EntitlementsExplain? explained)
    {
        var tables = new List<ExplainedTable>();
        foreach (var table in explained?.Tables ?? [])
        {
            var columns = new List<ExplainedColumn>(table.Columns.Count);
            foreach (var column in table.Columns)
            {
                columns.Add(new ExplainedColumn
                {
                    Column = column.Column,
                    Disclosure = column.Disclosure,
                    Mask = column.Mask,
                    Placeholder = column.Placeholder,
                    MinGroupSize = (int)column.MinGroupSize,
                });
            }

            var parents = new List<ExplainedParent>(table.Parents.Count);
            foreach (var parent in table.Parents)
            {
                parents.Add(new ExplainedParent
                {
                    Schema = parent.Schema,
                    Table = parent.Table,
                    Column = parent.Column,
                    ParentColumn = parent.ParentColumn,
                    Elided = parent.Elided,
                    KeyDisclosure = parent.KeyDisclosure,
                });
            }

            var paths = new List<ExplainedPath>(table.Paths.Count);
            foreach (var path in table.Paths)
            {
                var steps = new List<ExplainedStep>(path.Steps.Count);
                foreach (var step in path.Steps)
                {
                    steps.Add(new ExplainedStep
                    {
                        Schema = step.Schema,
                        Table = step.Table,
                        FromColumn = step.FromColumn,
                        ToColumn = step.ToColumn,
                        ToChild = step.ToChild,
                    });
                }

                paths.Add(new ExplainedPath
                {
                    Kind = path.Kind,
                    Steps = steps,
                    EndpointSchema = path.EndpointSchema,
                    EndpointTable = path.EndpointTable,
                    Elided = path.Elided,
                    Dropped = path.Dropped,
                    EndpointPredicate = path.EndpointPredicate,
                    KeyDisclosure = path.KeyDisclosure,
                });
            }

            tables.Add(new ExplainedTable
            {
                Schema = table.Schema,
                Table = table.Table,
                RowPredicate = table.RowPredicate,
                RowPredicatePushed = table.RowPredicatePushed,
                Residual = [.. table.Residual],
                Columns = columns,
                Parents = parents,
                Paths = paths,
            });
        }

        return new EntitlementsExplanation { Tables = tables };
    }

    /// <summary>One per output column, in order.</summary>
    internal static IReadOnlyList<EntitledColumn> Columns(
        ArrowSchema schema, Rpc.EntitlementsReport? report)
    {
        var fields = schema.FieldsList;
        var columns = new List<EntitledColumn>(fields.Count);
        for (var i = 0; i < fields.Count; i++)
        {
            var reported = report is not null && i < report.Columns.Count
                ? Map(report.Columns[i])
                : ReportedDisclosure.Full;
            columns.Add(new EntitledColumn { Name = fields[i].Name, Disclosure = reported });
        }

        return columns;
    }

    /// <summary>
    /// The report's per-column labels in the IR's own vocabulary, for I-IR-E's last clause, or null
    /// when there is no report at all.
    /// </summary>
    /// <remarks>
    /// The two enumerations say the same things under two names — the report speaks to a
    /// caller, the read speaks about a column of a table — and this is the one place they meet.
    /// </remarks>
    internal static IReadOnlyList<Chalk.Ir.DisclosureOutcome>? Outcomes(Rpc.EntitlementsReport? report)
    {
        if (report is null)
        {
            return null;
        }

        var outcomes = new List<Chalk.Ir.DisclosureOutcome>(report.Columns.Count);
        foreach (var column in report.Columns)
        {
            outcomes.Add(column switch
            {
                Rpc.ReportedDisclosure.Masked => Chalk.Ir.DisclosureOutcome.Masked,
                Rpc.ReportedDisclosure.Redacted => Chalk.Ir.DisclosureOutcome.Redacted,
                Rpc.ReportedDisclosure.PerRow => Chalk.Ir.DisclosureOutcome.PerRow,
                Rpc.ReportedDisclosure.Aggregate => Chalk.Ir.DisclosureOutcome.Aggregate,
                Rpc.ReportedDisclosure.Tested => Chalk.Ir.DisclosureOutcome.Tested,
                _ => Chalk.Ir.DisclosureOutcome.Full,
            });
        }

        return outcomes;
    }

    /// <summary>One per entitled table the plan reads, in the order the rewrite met them.</summary>
    internal static IReadOnlyList<EntitledTableReport> Tables(Rpc.EntitlementsReport? report)
    {
        if (report is null || report.Tables.Count == 0)
        {
            return [];
        }

        var tables = new List<EntitledTableReport>(report.Tables.Count);
        foreach (var table in report.Tables)
        {
            tables.Add(new EntitledTableReport
            {
                Schema = table.Schema,
                Table = table.Table,
                DescriptorHash = table.DescriptorHash,
                RowPredicatePushed = table.RowPredicatePushed,
                Contradiction = table.Contradiction,
                ContradictionColumn = table.ContradictionColumn,
                Tested = [.. table.Tested.Select(t => new TestedColumnReport
                {
                    Column = t.Column,
                    Shapes = [.. t.Shapes],
                })],
                Visibility = table.Visibility switch
                {
                    Rpc.Visibility.None => TableVisibility.None,
                    Rpc.Visibility.All => TableVisibility.All,
                    _ => TableVisibility.Some,
                },
            });
        }

        return tables;
    }

    /// <summary>
    /// The schema with <c>chalk.disclosure</c> on every field that discloses anything but the value
    /// itself. The same instance back when nothing does, so an unentitled plan's batches carry the
    /// schema the compiler built and nothing is allocated.
    /// </summary>
    internal static ArrowSchema Decorate(ArrowSchema schema, IReadOnlyList<EntitledColumn> columns)
    {
        var interesting = false;
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Disclosure != ReportedDisclosure.Full)
            {
                interesting = true;
                break;
            }
        }

        if (!interesting)
        {
            return schema;
        }

        var fields = new List<Field>(schema.FieldsList.Count);
        for (var i = 0; i < schema.FieldsList.Count; i++)
        {
            var field = schema.FieldsList[i];
            var metadata = new Dictionary<string, string>(
                field.Metadata ?? new Dictionary<string, string>(0),
                StringComparer.Ordinal)
            {
                [MetadataKey] = Name(i < columns.Count ? columns[i].Disclosure : ReportedDisclosure.Full),
            };
            fields.Add(new Field(field.Name, field.DataType, field.IsNullable, metadata));
        }

        return new ArrowSchema(fields, schema.Metadata);
    }

    /// <summary>
    /// The name a consumer reads, upper-cased so it is stable across languages (D218 amended): the
    /// descriptor's own vocabulary spells a disclosure <c>MASKED</c>, and so does the wire enum once
    /// its prefix is dropped, so a consumer reads one word whichever side it reads it from.
    /// </summary>
    internal static string Name(ReportedDisclosure disclosure) => disclosure switch
    {
        ReportedDisclosure.Masked => "MASKED",
        ReportedDisclosure.Redacted => "REDACTED",
        ReportedDisclosure.PerRow => "PER_ROW",
        ReportedDisclosure.Aggregate => "AGGREGATE",
        ReportedDisclosure.Tested => "TESTED",
        _ => "FULL",
    };

    private static ReportedDisclosure Map(Rpc.ReportedDisclosure reported) => reported switch
    {
        Rpc.ReportedDisclosure.Masked => ReportedDisclosure.Masked,
        Rpc.ReportedDisclosure.Redacted => ReportedDisclosure.Redacted,
        Rpc.ReportedDisclosure.PerRow => ReportedDisclosure.PerRow,
        Rpc.ReportedDisclosure.Aggregate => ReportedDisclosure.Aggregate,
        Rpc.ReportedDisclosure.Tested => ReportedDisclosure.Tested,
        _ => ReportedDisclosure.Full,
    };
}
