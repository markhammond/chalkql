using Chalk.Ir;
using IrColumnStatistics = Chalk.Ir.ColumnStatistics;
using IrCostProfile = Chalk.Ir.CostProfile;
using IrFrequentValue = Chalk.Ir.FrequentValue;
using IrHistogram = Chalk.Ir.Histogram;
using IrHistogramBucket = Chalk.Ir.HistogramBucket;
using IrFunctionDescriptor = Chalk.Ir.FunctionDescriptor;
using IrIndex = Chalk.Ir.Index;
using IrParameter = Chalk.Ir.Parameter;
using IrDisclosure = Chalk.Ir.Disclosure;
using IrEnforcement = Chalk.Ir.Enforcement;
using IrTableEntitlement = Chalk.Ir.TableEntitlement;
using IrColumnEntitlement = Chalk.Ir.ColumnEntitlement;
using IrDisclosureRule = Chalk.Ir.DisclosureRule;

using Chalk.Entitlements;
// The descriptor vocabulary and the wire enums share three names; the model's are the ones this
// file means, and the wire ones are written out where it needs them.
using Disclosure = Chalk.Entitlements.Disclosure;
using DisclosureRule = Chalk.Entitlements.DisclosureRule;
using Enforcement = Chalk.Entitlements.Enforcement;

namespace Chalk.Catalog;

/// <summary>
/// Maps the idiomatic C# catalog model to and from the wire messages. Internal: the proto messages
/// are an implementation detail of the transport, not part of the host-facing API.
/// </summary>
internal static class CatalogProtoMapping
{
    public static Ir.CatalogContext ToProto(this CatalogContext catalog)
    {
        var message = new Ir.CatalogContext
        {
            ContextId = catalog.ContextId,
            Epoch = catalog.Epoch,
        };
        message.Schemas.AddRange(catalog.Schemas.Select(ToProto));
        if (catalog.JoinPolicy is { } policy)
        {
            message.JoinPolicy = ToProto(policy);
        }

        // Additive (D270 (c)): a catalog that declares no association adds no bytes and the
        // registration the sidecar receives is the one it received before.
        foreach (var association in catalog.Associations)
        {
            message.Associations.Add(new Ir.Association
            {
                Name = association.Name,
                FromSchema = association.FromSchema,
                FromTable = association.FromTable,
                FromColumn = association.FromColumn,
                ToSchema = association.ToSchema,
                ToTable = association.ToTable,
                ToColumn = association.ToColumn,
            });
        }

        return message;
    }

    /// <summary>The cross-source join policy (D104), as the planner reads it.</summary>
    public static Ir.CrossSourceJoinPolicy ToProto(CrossSourceJoinPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var message = new Ir.CrossSourceJoinPolicy
        {
            DefaultStrategy = (Ir.JoinStrategy)policy.DefaultStrategy,
            BroadcastMaxRows = policy.BroadcastMaxRows,
            LookupMaxCalls = policy.LookupMaxCalls,
            LocalJoinMaxRows = policy.LocalJoinMaxRows,
            UnknownRowCountAssumption = policy.UnknownRowCountAssumption,
        };
        foreach (var pair in policy.Pairs)
        {
            var rule = new Ir.SourcePairRule
            {
                LeftSource = pair.LeftSource,
                RightSource = pair.RightSource,
                Preferred = (Ir.JoinStrategy)pair.Preferred,
                BroadcastMaxRows = pair.BroadcastMaxRows,
            };
            rule.Allowed.AddRange(pair.Allowed.Select(a => (Ir.JoinStrategy)a));
            message.Pairs.Add(rule);
        }

        return message;
    }

    private static CrossSourceJoinPolicy FromProto(Ir.CrossSourceJoinPolicy? message)
    {
        if (message is null)
        {
            return CrossSourceJoinPolicy.Default;
        }

        return new CrossSourceJoinPolicy
        {
            DefaultStrategy = (JoinStrategy)message.DefaultStrategy,
            BroadcastMaxRows = message.BroadcastMaxRows,
            LookupMaxCalls = message.LookupMaxCalls,
            LocalJoinMaxRows = message.LocalJoinMaxRows,
            UnknownRowCountAssumption = message.UnknownRowCountAssumption,
            Pairs = message.Pairs
                .Select(p => new SourcePairRule
                {
                    LeftSource = p.LeftSource,
                    RightSource = p.RightSource,
                    Preferred = (JoinStrategy)p.Preferred,
                    BroadcastMaxRows = p.BroadcastMaxRows,
                    Allowed = p.Allowed.Select(a => (JoinStrategy)a).ToArray(),
                })
                .ToArray(),
        };
    }

    public static CatalogContext FromProto(Ir.CatalogContext message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new CatalogContext
        {
            ContextId = message.ContextId,
            Epoch = message.Epoch,
            Schemas = message.Schemas.Select(FromProto).ToArray(),
            JoinPolicy = FromProto(message.JoinPolicy),
            Associations = message.Associations
                .Select(a => new AssociationDescriptor
                {
                    Name = a.Name,
                    FromSchema = a.FromSchema,
                    FromTable = a.FromTable,
                    FromColumn = a.FromColumn,
                    ToSchema = a.ToSchema,
                    ToTable = a.ToTable,
                    ToColumn = a.ToColumn,
                })
                .ToArray(),
        };
    }

    private static Schema ToProto(SchemaDescriptor schema)
    {
        var message = new Schema
        {
            SourceId = schema.SourceId,
            Name = schema.Name,
            Kind = schema.Kind,
            Dialect = schema.Dialect ?? string.Empty,
            Capabilities = ToProto(schema.Capabilities),
            DialectProfile = ToProto(schema.DialectProfile),
            CostProfile = ToProto(schema.CostProfile),
            TrustSourceRowSecurity = schema.TrustSourceRowSecurity,
        };
        message.Tables.AddRange(schema.Tables.Select(ToProto));
        message.Functions.AddRange(schema.Functions.Select(ToProto));
        return message;
    }

    private static IrFunctionDescriptor ToProto(FunctionDescriptor function)
    {
        var message = new IrFunctionDescriptor
        {
            Name = function.Name,
            Kind = function.Kind,
            Volatility = function.Volatility,
            Strict = function.Strict,
            Leakproof = function.Leakproof,
            Cost = function.Cost,
            Rows = function.Rows,
            Window = function.Window,
            Ordered = function.Ordered,
            NullTreatment = function.NullTreatment,
        };
        if (function.ReturnType is { } returnType)
        {
            message.ReturnType = returnType.ToProto();
        }

        if (function.ReturnsTable.Count > 0)
        {
            var row = new RowType();
            foreach (var column in function.ReturnsTable)
            {
                row.Fields.Add(new Field { Name = column.Name, Type = column.Type.ToProto() });
            }

            message.ReturnsTable = row;
        }

        message.Parameters.AddRange(function.Parameters.Select(ToProto));
        message.Monotonicity.AddRange(function.Monotonicity);
        switch (function.Body)
        {
            case SqlFunctionBody sql:
                message.Sql = new SqlBody { Text = sql.Text };
                break;
            case ClientFunctionBody client:
                message.Client = new ClientBody { Registration = client.Registration ?? string.Empty };
                break;
            case NativeFunctionBody native:
                message.Native = new NativeBody { DialectName = native.DialectName ?? string.Empty };
                break;
            default:
                throw new CatalogValidationException(
                    $"functions ({function.Name})",
                    $"{function.Body.GetType().Name} is not one of the three implementation kinds.");
        }

        return message;
    }

    private static IrParameter ToProto(ParameterDescriptor parameter)
    {
        var message = new IrParameter
        {
            Name = parameter.Name,
            Type = parameter.Type.ToProto(),
            Optional = parameter.Optional,
        };
        if (parameter.Optional)
        {
            var literal = CatalogLiterals.ToProto(
                parameter.Default, parameter.Type, $"parameter {parameter.Name} default");
            message.DefaultValue = new Expr
            {
                Type = parameter.Type.ToProto(),
                Literal = literal ?? new Literal { IsNull = true },
            };
        }

        return message;
    }

    private static FunctionDescriptor FromProto(IrFunctionDescriptor message) => new()
    {
        Name = message.Name,
        Kind = message.Kind,
        Parameters = message.Parameters.Select(FromProto).ToArray(),
        ReturnType = message.ReturnType is null ? null : ChalkType.FromProto(message.ReturnType),
        ReturnsTable = message.ReturnsTable is null
            ? []
            : message.ReturnsTable.Fields
                .Select(f => new ColumnDescriptor { Name = f.Name, Type = ChalkType.FromProto(f.Type) })
                .ToArray(),
        Volatility = message.Volatility,
        Strict = message.Strict,
        Leakproof = message.Leakproof,
        Monotonicity = message.Monotonicity.ToArray(),
        Cost = message.Cost,
        Rows = message.Rows,
        Window = message.Window,
        Ordered = message.Ordered,
        NullTreatment = message.NullTreatment,
        Body = message.ImplementationCase switch
        {
            IrFunctionDescriptor.ImplementationOneofCase.Sql => new SqlFunctionBody { Text = message.Sql.Text },
            IrFunctionDescriptor.ImplementationOneofCase.Native => new NativeFunctionBody
            {
                DialectName = message.Native.DialectName.Length == 0 ? null : message.Native.DialectName,
            },
            _ => new ClientFunctionBody
            {
                Registration = message.Client?.Registration is { Length: > 0 } key ? key : null,
            },
        },
    };

    private static ParameterDescriptor FromProto(IrParameter message)
    {
        var type = ChalkType.FromProto(message.Type);
        return new ParameterDescriptor
        {
            Name = message.Name,
            Type = type,
            Optional = message.Optional,
            Default = message.DefaultValue is null
                ? null
                : CatalogLiterals.FromProto(message.DefaultValue.Literal, type),
        };
    }

    /// <summary>Null rather than an all-zero message, so "inherit" does not travel as a claim.</summary>
    private static IrCostProfile? ToProto(CostProfile profile) => profile.IsInherit
        ? null
        : new IrCostProfile
        {
            ScanRowCost = profile.ScanRowCost,
            LookupSeekCost = profile.LookupSeekCost,
            LookupRowCost = profile.LookupRowCost,
            RemoteCallCost = profile.RemoteCallCost,
            RemoteRowCost = profile.RemoteRowCost,
        };

    private static CostProfile FromProto(IrCostProfile? message) => message is null
        ? CostProfile.Inherit
        : new CostProfile
        {
            ScanRowCost = message.ScanRowCost,
            LookupSeekCost = message.LookupSeekCost,
            LookupRowCost = message.LookupRowCost,
            RemoteCallCost = message.RemoteCallCost,
            RemoteRowCost = message.RemoteRowCost,
        };

    private static SchemaDescriptor FromProto(Schema message) => new()
    {
        SourceId = message.SourceId,
        Name = message.Name,
        Kind = message.Kind,
        Dialect = message.Dialect.Length == 0 ? null : message.Dialect,
        Capabilities = message.Capabilities is null ? SourceCapabilities.None : FromProto(message.Capabilities),
        DialectProfile = message.DialectProfile is null
            ? DialectProfileDescriptor.None
            : FromProto(message.DialectProfile),
        CostProfile = FromProto(message.CostProfile),
        TrustSourceRowSecurity = message.TrustSourceRowSecurity,
        Tables = message.Tables.Select(FromProto).ToArray(),
        Functions = message.Functions.Select(FromProto).ToArray(),
    };

    private static Table ToProto(TableDescriptor table)
    {
        var message = new Table
        {
            Name = table.Name,
            RowCount = table.RowCount,
            RowCountKind = table.RowCountKind,
            CostProfile = ToProto(table.CostProfile),
            Entitlement = ToProto(table.Entitlement),
        };
        message.Columns.AddRange(table.Columns.Select(c => new Column
        {
            Name = c.Name,
            Type = c.Type.ToProto(),
            Statistics = ToProto(c.Statistics, c.Type, $"table '{table.Name}' column '{c.Name}'"),
        }));
        foreach (var key in table.UniqueKeys)
        {
            var unique = new UniqueKey();
            unique.Columns.AddRange(key.Columns.Select(c => (uint)c));
            message.UniqueKeys.Add(unique);
        }

        foreach (var collation in table.Collations)
        {
            var tableCollation = new TableCollation();
            tableCollation.Keys.AddRange(collation.Keys.Select(
                k => new Ir.KeyOrder { Column = (uint)k.Column, Direction = k.Direction }));
            message.Collations.Add(tableCollation);
        }

        foreach (var index in table.Indexes)
        {
            var indexMessage = new IrIndex
            {
                Name = index.Name,
                Kind = index.Kind,
                Unique = index.Unique,
                Reversal = index.Reversal,
            };
            indexMessage.Columns.AddRange(index.Columns.Select(c => (uint)c));
            indexMessage.Directions.AddRange(index.Directions);
            indexMessage.Covering.AddRange(index.Covering);
            message.Indexes.Add(indexMessage);
        }

        foreach (var foreignKey in table.ForeignKeys)
        {
            var keyMessage = new Ir.ForeignKey
            {
                Name = foreignKey.Name,
                ParentTable = foreignKey.ParentTable,
            };
            keyMessage.Columns.AddRange(foreignKey.Columns.Select(c => (uint)c));
            keyMessage.ParentColumns.AddRange(foreignKey.ParentColumns.Select(c => (uint)c));
            message.ForeignKeys.Add(keyMessage);
        }

        if (table.Partitioning is { } partitioning)
        {
            var partitionType = table.Columns[partitioning.PartitionColumn].Type;
            var message2 = new Ir.Partitioning
            {
                PartitionColumn = (uint)partitioning.PartitionColumn,
            };
            foreach (var partition in partitioning.Partitions)
            {
                var entry = new Ir.Partition
                {
                    SourceId = partition.Schema,
                    Table = partition.Table,
                    RowCount = partition.RowCount,
                };
                if (partition.HasValue)
                {
                    entry.Value = Value(partition.Value, partitionType, table.Name);
                }
                else
                {
                    entry.Range = new Ir.PartitionRange();
                    if (partition.LowerBound is not null)
                    {
                        entry.Range.Lower = Value(partition.LowerBound, partitionType, table.Name);
                    }

                    if (partition.UpperBound is not null)
                    {
                        entry.Range.Upper = Value(partition.UpperBound, partitionType, table.Name);
                    }
                }

                message2.Partitions.Add(entry);
            }

            message.Partitioning = message2;
        }

        return message;
    }

    /// <summary>A partition bound as a typed literal expression.</summary>
    private static Expr Value(object? value, ChalkType type, string table) => new()
    {
        Type = type.ToProto(),
        Literal = CatalogLiterals.ToProto(value, type, $"a partition of table '{table}'")
            ?? new Literal { IsNull = true },
    };

    /// <summary>Null when nothing is known, so an "unknown" statistic is an absent message.</summary>
    internal static IrColumnStatistics? ToProto(ColumnStatistics statistics, ChalkType type, string what)
    {
        if (statistics.Level == StatisticsLevel.Unknown
            && statistics.DistinctCount < 0
            && statistics.NullCount < 0
            && statistics.Min is null
            && statistics.Max is null
            && statistics.Histogram.Count == 0
            && statistics.FrequentValues.Count == 0)
        {
            return null;
        }

        var message = new IrColumnStatistics
        {
            Level = statistics.Level,
            DistinctCount = statistics.DistinctCount,
            NullCount = statistics.NullCount,
            Min = CatalogLiterals.ToProto(statistics.Min, type, what),
            Max = CatalogLiterals.ToProto(statistics.Max, type, what),
        };

        if (statistics.Histogram.Count > 0)
        {
            var histogram = new IrHistogram();
            foreach (var bucket in statistics.Histogram)
            {
                histogram.Buckets.Add(new IrHistogramBucket
                {
                    Upper = CatalogLiterals.ToProto(bucket.Upper, type, what),
                    Count = bucket.Count,
                    DistinctCount = bucket.DistinctCount,
                });
            }

            message.Histogram = histogram;
        }

        foreach (var frequent in statistics.FrequentValues)
        {
            message.FrequentValues.Add(new IrFrequentValue
            {
                Value = CatalogLiterals.ToProto(frequent.Value, type, what),
                Count = frequent.Count,
            });
        }

        return message;
    }

    private static ColumnStatistics FromProto(IrColumnStatistics? message, ChalkType type)
    {
        if (message is null)
        {
            return ColumnStatistics.Unknown;
        }

        return new ColumnStatistics
        {
            Level = message.Level,
            DistinctCount = message.DistinctCount,
            NullCount = message.NullCount,
            Min = CatalogLiterals.FromProto(message.Min, type),
            Max = CatalogLiterals.FromProto(message.Max, type),
            Histogram = message.Histogram is null
                ? []
                : [.. message.Histogram.Buckets.Select(b => new HistogramBucket
                {
                    Upper = CatalogLiterals.FromProto(b.Upper, type),
                    Count = b.Count,
                    DistinctCount = b.DistinctCount,
                })],
            FrequentValues = [.. message.FrequentValues.Select(v => new FrequentValue
            {
                Value = CatalogLiterals.FromProto(v.Value, type),
                Count = v.Count,
            })],
        };
    }

    private static TableDescriptor FromProto(Table message) => new()
    {
        Name = message.Name,
        RowCount = message.RowCount,
        RowCountKind = message.RowCountKind,
        CostProfile = FromProto(message.CostProfile),
        Entitlement = FromProto(message.Entitlement),
        Columns = message.Columns
            .Select(c =>
            {
                var type = ChalkType.FromProto(c.Type);
                return new ColumnDescriptor
                {
                    Name = c.Name,
                    Type = type,
                    Statistics = FromProto(c.Statistics, type),
                };
            })
            .ToArray(),
        UniqueKeys = message.UniqueKeys
            .Select(k => new UniqueKeyDescriptor { Columns = k.Columns.Select(c => (int)c).ToArray() })
            .ToArray(),
        Collations = message.Collations
            .Select(c => new CollationDescriptor
            {
                Keys = c.Keys.Select(k => new KeyOrder((int)k.Column, k.Direction)).ToArray(),
            })
            .ToArray(),
        Indexes = message.Indexes
            .Select(i => new IndexDescriptor
            {
                Name = i.Name,
                Kind = i.Kind,
                Columns = i.Columns.Select(c => (int)c).ToArray(),
                Unique = i.Unique,
                Directions = i.Directions.ToArray(),
                Covering = i.Covering.ToArray(),
                Reversal = i.Reversal,
            })
            .ToArray(),
        Partitioning = message.Partitioning is null
            ? null
            : new PartitioningDescriptor
            {
                PartitionColumn = (int)message.Partitioning.PartitionColumn,
                Partitions = message.Partitioning.Partitions
                    .Select(p => new PartitionDescriptor
                    {
                        Schema = p.SourceId,
                        Table = p.Table,
                        RowCount = p.RowCount,
                        HasValue = p.MatchCase == Ir.Partition.MatchOneofCase.Value,
                        Value = p.MatchCase == Ir.Partition.MatchOneofCase.Value
                            ? CatalogLiterals.FromProto(
                                p.Value.Literal,
                                ChalkType.FromProto(
                                    message.Columns[(int)message.Partitioning.PartitionColumn].Type))
                            : null,
                        LowerBound = p.MatchCase == Ir.Partition.MatchOneofCase.Range && p.Range.Lower is not null
                            ? CatalogLiterals.FromProto(
                                p.Range.Lower.Literal,
                                ChalkType.FromProto(
                                    message.Columns[(int)message.Partitioning.PartitionColumn].Type))
                            : null,
                        UpperBound = p.MatchCase == Ir.Partition.MatchOneofCase.Range && p.Range.Upper is not null
                            ? CatalogLiterals.FromProto(
                                p.Range.Upper.Literal,
                                ChalkType.FromProto(
                                    message.Columns[(int)message.Partitioning.PartitionColumn].Type))
                            : null,
                    })
                    .ToArray(),
            },
        ForeignKeys = message.ForeignKeys
            .Select(k => new ForeignKeyDescriptor
            {
                Name = k.Name,
                Columns = k.Columns.Select(c => (int)c).ToArray(),
                ParentTable = k.ParentTable,
                ParentColumns = k.ParentColumns.Select(c => (int)c).ToArray(),
            })
            .ToArray(),
    };

    private static Ir.SourceCapabilities ToProto(SourceCapabilities capabilities)
    {
        var message = new Ir.SourceCapabilities
        {
            QueryLanguage = capabilities.QueryLanguage,
            SupportsProject = capabilities.SupportsProject,
            SupportsSort = capabilities.SupportsSort,
            SupportsLimit = capabilities.SupportsLimit,
            SupportsOffset = capabilities.SupportsOffset,
            SupportsDistinct = capabilities.SupportsDistinct,
            SupportsGroupBy = capabilities.SupportsGroupBy,
            SupportsHaving = capabilities.SupportsHaving,
            SupportsInnerJoin = capabilities.SupportsInnerJoin,
            SupportsOuterJoin = capabilities.SupportsOuterJoin,
            SupportsSemiAntiJoin = capabilities.SupportsSemiAntiJoin,
            MaxPushdownRows = capabilities.MaxPushdownRows,
            MaxInList = capabilities.MaxInList,
            SupportsParameters = capabilities.SupportsParameters,
            SupportsValuesJoin = capabilities.SupportsValuesJoin,
            SupportsMaskPushdown = capabilities.SupportsMaskPushdown,
            SupportsRowValueInList = capabilities.SupportsRowValueInList,

            // D273: the field carries explicit presence and absent means true, so what this client
            // declares is always written — a descriptor that means false must say so, and one that
            // means true says so too rather than relying on the reader's default.
            SupportsCase = capabilities.SupportsCase,
        };
        message.PushablePredicates.AddRange(capabilities.PushablePredicates);
        message.PushableFunctions.AddRange(capabilities.PushableFunctions);
        message.PushableAggregates.AddRange(capabilities.PushableAggregates);
        message.NativeFunctions.AddRange(capabilities.NativeFunctions);
        message.UnsupportedFunctions.AddRange(capabilities.UnsupportedFunctions);
        return message;
    }

    private static SourceCapabilities FromProto(Ir.SourceCapabilities message) => new()
    {
        QueryLanguage = message.QueryLanguage,
        SupportsProject = message.SupportsProject,
        SupportsSort = message.SupportsSort,
        SupportsLimit = message.SupportsLimit,
        SupportsOffset = message.SupportsOffset,
        SupportsDistinct = message.SupportsDistinct,
        SupportsGroupBy = message.SupportsGroupBy,
        SupportsHaving = message.SupportsHaving,
        SupportsInnerJoin = message.SupportsInnerJoin,
        SupportsOuterJoin = message.SupportsOuterJoin,
        SupportsSemiAntiJoin = message.SupportsSemiAntiJoin,
        MaxPushdownRows = message.MaxPushdownRows,
        MaxInList = message.MaxInList,
        SupportsParameters = message.SupportsParameters,
        SupportsValuesJoin = message.SupportsValuesJoin,
        SupportsMaskPushdown = message.SupportsMaskPushdown,
        SupportsRowValueInList = message.SupportsRowValueInList,

        // D273: absent is true, which is what makes a catalog recorded before the field existed
        // push a CASE from now on.
        SupportsCase = !message.HasSupportsCase || message.SupportsCase,
        PushablePredicates = message.PushablePredicates.ToArray(),
        PushableFunctions = message.PushableFunctions.ToArray(),
        PushableAggregates = message.PushableAggregates.ToArray(),
        NativeFunctions = message.NativeFunctions.ToArray(),
        UnsupportedFunctions = message.UnsupportedFunctions.ToArray(),
    };

    private static Ir.DialectProfile ToProto(DialectProfileDescriptor profile)
    {
        var message = new Ir.DialectProfile
        {
            Dialect = profile.Dialect,
            Quoting = profile.Quoting,
            QuotedCasing = profile.QuotedCasing,
            UnquotedCasing = profile.UnquotedCasing,
            CaseSensitiveIdentifiers = profile.CaseSensitiveIdentifiers,
            Conformance = profile.Conformance,
            MaxNumericPrecision = profile.MaxNumericPrecision,
            MaxTimestampPrecision = profile.MaxTimestampPrecision,
            HasBoolean = profile.HasBoolean,
            TimeZone = profile.TimeZone,
            DefaultNullCollation = profile.DefaultNullCollation,
            SupportsNullOrderingClause = profile.SupportsNullOrderingClause,
            StringCollation = profile.StringCollation,
            ApproximateDecimal = profile.ApproximateDecimal,
            ApproximateDistinctCount = profile.ApproximateDistinctCount,
            ApproximateTopN = profile.ApproximateTopN,
            ImplicitCoercionMatches = profile.ImplicitCoercionMatches,
            ParameterPlaceholder = profile.ParameterPlaceholder,
        };
        message.Libraries.AddRange(profile.Libraries);
        return message;
    }

    private static DialectProfileDescriptor FromProto(Ir.DialectProfile message) => new()
    {
        Dialect = message.Dialect,
        Quoting = message.Quoting,
        QuotedCasing = message.QuotedCasing,
        UnquotedCasing = message.UnquotedCasing,
        CaseSensitiveIdentifiers = message.CaseSensitiveIdentifiers,
        Conformance = message.Conformance,
        Libraries = message.Libraries.ToArray(),
        MaxNumericPrecision = message.MaxNumericPrecision,
        MaxTimestampPrecision = message.MaxTimestampPrecision,
        HasBoolean = message.HasBoolean,
        TimeZone = message.TimeZone,
        DefaultNullCollation = message.DefaultNullCollation,
        SupportsNullOrderingClause = message.SupportsNullOrderingClause,
        StringCollation = message.StringCollation,
        ApproximateDecimal = message.ApproximateDecimal,
        ApproximateDistinctCount = message.ApproximateDistinctCount,
        ApproximateTopN = message.ApproximateTopN,
        ImplicitCoercionMatches = message.ImplicitCoercionMatches,
        ParameterPlaceholder = message.ParameterPlaceholder,
    };

    /// <summary>
    /// The entitlement, or null when the table carries none — which is the whole of "no bytes when
    /// unused": an absent message, exactly as an all-unknown <see cref="ColumnStatistics"/> is.
    /// </summary>
    private static IrTableEntitlement? ToProto(TableEntitlementDescriptor? entitlement)
    {
        if (entitlement is null)
        {
            return null;
        }

        var message = new IrTableEntitlement
        {
            RowPredicate = entitlement.RowPredicate,
            DescriptorHash = entitlement.DescriptorHash,
            Enforcement = (IrEnforcement)entitlement.Enforcement,
            DefaultDisclosure = (IrDisclosure)entitlement.DefaultDisclosure,
            PushMasks = entitlement.PushMasks,
        };
        foreach (var parent in entitlement.Through)
        {
            message.Through.Add(new Chalk.Ir.ParentVisibility
            {
                Column = (uint)parent.Column,
                ParentSchema = parent.ParentSchema,
                ParentTable = parent.ParentTable,
                ParentColumn = (uint)parent.ParentColumn,
            });
        }

        foreach (var path in entitlement.Inherited)
        {
            var pathMessage = new Chalk.Ir.InheritedVisibility
            {
                Kind = path.Kind,
                EndpointPredicate = path.EndpointPredicate,
                PathPredicate = path.PathPredicate,
                EndpointSchema = path.EndpointSchema,
                EndpointTable = path.EndpointTable,
            };
            foreach (var step in path.Steps)
            {
                pathMessage.Steps.Add(new Chalk.Ir.VisibilityStep
                {
                    Schema = step.Schema,
                    Table = step.Table,
                    FromColumn = (uint)step.FromColumn,
                    ToColumn = (uint)step.ToColumn,
                    Direction = (Chalk.Ir.StepDirection)step.Direction,
                });
            }

            message.Inherited.Add(pathMessage);
        }

        foreach (var column in entitlement.Columns)
        {
            var columnMessage = new IrColumnEntitlement
            {
                Column = (uint)column.Column,
                Mask = column.Mask,
                Placeholder = column.Placeholder,
                MinGroupSize = (uint)column.MinGroupSize,
                Otherwise = (IrDisclosure)column.Otherwise,
                Statistical = column.Statistical,
            };
            columnMessage.AggregateOnlyFunctions.AddRange(column.AggregateOnlyFunctions);
            foreach (var rule in column.Rules)
            {
                var ruleMessage = new IrDisclosureRule
                {
                    When = rule.When,
                    Then = (IrDisclosure)rule.Then,
                    Mask = rule.Mask,
                    Placeholder = rule.Placeholder,
                };
                ruleMessage.Tests.AddRange(rule.Tests.Select(t => (Chalk.Ir.TestShape)t));
                columnMessage.Rules.Add(ruleMessage);
            }

            message.Columns.Add(columnMessage);
        }

        return message;
    }

    private static TableEntitlementDescriptor? FromProto(IrTableEntitlement? message) =>
        message is null
            ? null
            : new TableEntitlementDescriptor
            {
                RowPredicate = message.RowPredicate,
                Enforcement = (Enforcement)message.Enforcement,
                DefaultDisclosure = (Disclosure)message.DefaultDisclosure,
                PushMasks = message.PushMasks,
                Through = message.Through
                    .Select(p => new ParentVisibilityDescriptor
                    {
                        Column = (int)p.Column,
                        ParentSchema = p.ParentSchema,
                        ParentTable = p.ParentTable,
                        ParentColumn = (int)p.ParentColumn,
                    })
                    .ToArray(),
                Inherited = message.Inherited
                    .Select(p => new InheritedVisibilityDescriptor
                    {
                        Kind = p.Kind,
                        EndpointPredicate = p.EndpointPredicate,
                        PathPredicate = p.PathPredicate,
                        EndpointSchema = p.EndpointSchema,
                        EndpointTable = p.EndpointTable,
                        Steps = p.Steps
                            .Select(s => new VisibilityStepDescriptor
                            {
                                Schema = s.Schema,
                                Table = s.Table,
                                FromColumn = (int)s.FromColumn,
                                ToColumn = (int)s.ToColumn,
                                Direction = (Chalk.Entitlements.StepDirection)s.Direction,
                            })
                            .ToArray(),
                    })
                    .ToArray(),
                Columns = message.Columns
                    .Select(c => new ColumnEntitlementDescriptor
                    {
                        Column = (int)c.Column,
                        Mask = c.Mask,
                        Placeholder = c.Placeholder,
                        MinGroupSize = (int)c.MinGroupSize,
                        AggregateOnlyFunctions = c.AggregateOnlyFunctions.ToArray(),
                        Otherwise = (Disclosure)c.Otherwise,
                        Statistical = c.Statistical,
                        Rules = c.Rules
                            .Select(r => new DisclosureRule
                            {
                                When = r.When,
                                Then = (Disclosure)r.Then,
                                Mask = r.Mask,
                                Placeholder = r.Placeholder,
                                Tests = r.Tests.Select(t => (Chalk.Entitlements.TestShape)t).ToArray(),
                            })
                            .ToArray(),
                    })
                    .ToArray(),
            };
}
