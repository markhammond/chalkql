using System.Numerics;
using Chalk.Ir;
using Google.Protobuf;
using IrType = Chalk.Ir.Type;

namespace Chalk.TestKit;

/// <summary>
/// Terse constructors for hand-built IR. Tests that exercise the validator, the operators and the
/// kernels build plans with this instead of going through the planner
/// (<c>docs/design/05-testing.md</c> §1).
/// </summary>
public static class IrBuilder
{
    public static IrType Bool(bool nullable = false) => new() { Kind = TypeKind.Bool, Nullable = nullable };

    public static IrType I8(bool nullable = false) => new() { Kind = TypeKind.I8, Nullable = nullable };

    public static IrType I16(bool nullable = false) => new() { Kind = TypeKind.I16, Nullable = nullable };

    public static IrType I32(bool nullable = false) => new() { Kind = TypeKind.I32, Nullable = nullable };

    public static IrType I64(bool nullable = false) => new() { Kind = TypeKind.I64, Nullable = nullable };

    public static IrType Fp32(bool nullable = false) => new() { Kind = TypeKind.Fp32, Nullable = nullable };

    public static IrType Fp64(bool nullable = false) => new() { Kind = TypeKind.Fp64, Nullable = nullable };

    public static IrType Str(bool nullable = false) => new() { Kind = TypeKind.String, Nullable = nullable };

    public static IrType Binary(bool nullable = false) => new() { Kind = TypeKind.Binary, Nullable = nullable };

    public static IrType Date(bool nullable = false) => new() { Kind = TypeKind.Date, Nullable = nullable };

    public static IrType Time(uint precision = 6, bool nullable = false) =>
        new() { Kind = TypeKind.Time, Precision = precision, Nullable = nullable };

    public static IrType Timestamp(uint precision = 9, bool nullable = false) =>
        new() { Kind = TypeKind.Timestamp, Precision = precision, Nullable = nullable };

    public static IrType TimestampTz(uint precision = 9, bool nullable = false) =>
        new() { Kind = TypeKind.TimestampTz, Precision = precision, Nullable = nullable };

    public static IrType Dec(uint precision = 28, uint scale = 10, bool nullable = false) =>
        new() { Kind = TypeKind.Decimal, Precision = precision, Scale = scale, Nullable = nullable };

    public static IrType Uuid(bool nullable = false) => new() { Kind = TypeKind.Uuid, Nullable = nullable };

    public static IrType IntervalDay(bool nullable = false) =>
        new() { Kind = TypeKind.IntervalDay, Nullable = nullable };

    public static IrType IntervalYear(bool nullable = false) =>
        new() { Kind = TypeKind.IntervalYear, Nullable = nullable };

    public static Field F(string name, IrType type) => new() { Name = name, Type = type };

    public static RowType Row(params Field[] fields)
    {
        var row = new RowType();
        row.Fields.AddRange(fields);
        return row;
    }

    /// <summary>A row type from a sequence of fields, for the tests that build one by concatenation.</summary>
    public static RowType Row(IEnumerable<Field> fields)
    {
        var row = new RowType();
        row.Fields.AddRange(fields);
        return row;
    }

    public static Expr Ref(int index, IrType type) =>
        new() { Type = type, FieldRef = new FieldRef { Index = (uint)index } };

    /// <summary>A field reference to <paramref name="index"/> of <paramref name="row"/>, typed from it.</summary>
    public static Expr Ref(RowType row, int index) => Ref(index, row.Fields[index].Type);

    public static Expr Param(int index, IrType type) =>
        new() { Type = type, Param = new DynamicParam { Index = (uint)index } };

    public static Expr Lit(bool value) => new() { Type = Bool(), Literal = new Literal { BoolValue = value } };

    public static Expr Lit(int value) => new() { Type = I32(), Literal = new Literal { I32Value = value } };

    public static Expr Lit(long value) => new() { Type = I64(), Literal = new Literal { I64Value = value } };

    public static Expr Lit(double value) => new() { Type = Fp64(), Literal = new Literal { Fp64Value = value } };

    public static Expr Lit(float value) => new() { Type = Fp32(), Literal = new Literal { Fp32Value = value } };

    public static Expr Lit(string value) => new() { Type = Str(), Literal = new Literal { StringValue = value } };

    public static Expr LitBytes(params byte[] value) =>
        new() { Type = Binary(), Literal = new Literal { BinaryValue = ByteString.CopyFrom(value) } };

    public static Expr LitDate(int daysSinceEpoch) =>
        new() { Type = Date(), Literal = new Literal { DateValue = daysSinceEpoch } };

    public static Expr LitTimestamp(long units, uint precision = 9) =>
        new() { Type = Timestamp(precision), Literal = new Literal { TimestampValue = units } };

    public static Expr LitDecimal(decimal value, uint precision = 28, uint scale = 10)
    {
        var scaled = value;
        for (var i = 0; i < scale; i++)
        {
            scaled *= 10m;
        }

        var unscaled = new BigInteger(decimal.Truncate(scaled));
        Span<byte> bytes = stackalloc byte[16];
        if (!unscaled.TryWriteBytes(bytes, out _, isUnsigned: false, isBigEndian: false))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "does not fit in 128 bits at this scale");
        }

        if (unscaled.Sign < 0)
        {
            for (var i = unscaled.GetByteCount(isUnsigned: false); i < 16; i++)
            {
                bytes[i] = 0xFF;
            }
        }

        return new Expr
        {
            Type = Dec(precision, scale),
            Literal = new Literal { DecimalValue = new DecimalValue { Unscaled = ByteString.CopyFrom(bytes) } },
        };
    }

    public static Expr LitUuid(Guid value) => new()
    {
        Type = Uuid(),
        Literal = new Literal { UuidValue = ByteString.CopyFrom(value.ToByteArray(bigEndian: true)) },
    };

    public static Expr Null(IrType type)
    {
        var nullable = type.Clone();
        nullable.Nullable = true;
        return new Expr { Type = nullable, Literal = new Literal { IsNull = true } };
    }

    public static Expr EnumArg(string value) => new() { EnumArg = new EnumArg { Value = value } };

    public static Expr Call(FunctionId function, IrType type, params Expr[] args)
    {
        var call = new ScalarCall { Function = function };
        call.Args.AddRange(args);
        return new Expr { Type = type, Call = call };
    }

    public static Expr Cast(Expr input, IrType target, CastFailure onFailure = CastFailure.Error) =>
        new() { Type = target, Cast = new Cast { Input = input, OnFailure = onFailure } };

    public static Expr Case(IrType type, Expr elseBranch, params (Expr Condition, Expr Result)[] clauses)
    {
        var ifThen = new IfThen { ElseBranch = elseBranch };
        foreach (var (condition, result) in clauses)
        {
            ifThen.Clauses.Add(new IfClause { Condition = condition, Result = result });
        }

        return new Expr { Type = type, IfThen = ifThen };
    }

    public static Expr In(Expr value, IrType type, params Expr[] options)
    {
        var inList = new InList { Value = value };
        inList.Options.AddRange(options);
        return new Expr { Type = type, InList = inList };
    }

    public static Rel Read(
        string table,
        RowType rowType,
        IEnumerable<int>? projection = null,
        double rows = 0,
        string sourceId = "mem",
        string schema = "main",
        params Collation[] collations)
    {
        var read = new Read
        {
            Table = new TableRef { SourceId = sourceId, Schema = schema, Table = table },
        };
        read.Projection.AddRange((projection ?? Enumerable.Range(0, rowType.Fields.Count)).Select(i => (uint)i));
        var rel = new Rel { RowType = rowType, EstRowCount = rows, Read = read };
        rel.Collations.AddRange(collations);
        return rel;
    }

    /// <summary>An index lookup on a declared index (M2). Ranges are built with <see cref="Range"/>.</summary>
    public static Rel IndexLookup(
        string table,
        string index,
        RowType rowType,
        IEnumerable<IndexRange> ranges,
        IEnumerable<int>? projection = null,
        double rows = 0,
        string sourceId = "mem",
        string schema = "main",
        params Collation[] collations)
    {
        var lookup = new IndexLookup
        {
            Table = new TableRef { SourceId = sourceId, Schema = schema, Table = table },
            Index = index,
        };
        lookup.Projection.AddRange(
            (projection ?? Enumerable.Range(0, rowType.Fields.Count)).Select(i => (uint)i));
        lookup.Ranges.AddRange(ranges);

        var rel = new Rel { RowType = rowType, EstRowCount = rows, IndexLookup = lookup };
        rel.Collations.AddRange(collations);
        return rel;
    }

    /// <summary>One index range. Empty bounds mean unbounded on that side.</summary>
    public static IndexRange Range(
        IEnumerable<Expr>? lower = null,
        IEnumerable<Expr>? upper = null,
        bool lowerInclusive = true,
        bool upperInclusive = true,
        bool prefix = false)
    {
        var range = new IndexRange
        {
            LowerInclusive = lowerInclusive,
            UpperInclusive = upperInclusive,
            Prefix = prefix,
        };
        range.Lower.AddRange(lower ?? []);
        range.Upper.AddRange(upper ?? []);
        return range;
    }

    public static Rel Filter(Rel input, Expr condition, double rows = 0) => new()
    {
        RowType = input.RowType,
        EstRowCount = rows,
        Filter = new Filter { Input = input, Condition = condition },
        Collations = { input.Collations },
    };

    public static Rel Project(Rel input, IEnumerable<(string Name, Expr Expr)> exprs, double rows = 0)
    {
        var list = exprs.ToList();
        var project = new Project { Input = input };
        project.Exprs.AddRange(list.Select(e => e.Expr));
        return new Rel
        {
            RowType = Row(list.Select(e => F(e.Name, e.Expr.Type)).ToArray()),
            EstRowCount = rows,
            Project = project,
        };
    }

    /// <summary>The row a join produces: left fields then right fields, each side null-padded as
    /// its join type requires (Calcite's <c>Join.deriveRowType</c>).</summary>
    public static RowType JoinedRow(Rel left, Rel right, JoinType type)
    {
        var row = new RowType();
        var padsLeft = type is JoinType.Right or JoinType.Full;
        var padsRight = type is JoinType.Left or JoinType.Full;
        foreach (var field in left.RowType.Fields)
        {
            row.Fields.Add(padsLeft ? Nullable(field) : field.Clone());
        }

        if (type is JoinType.Semi or JoinType.Anti)
        {
            return row;
        }

        foreach (var field in right.RowType.Fields)
        {
            row.Fields.Add(padsRight ? Nullable(field) : field.Clone());
        }

        return row;
    }

    private static Field Nullable(Field field)
    {
        var copy = field.Clone();
        copy.Type = copy.Type.Clone();
        copy.Type.Nullable = true;
        return copy;
    }

    public static Rel HashJoin(
        Rel left,
        Rel right,
        IEnumerable<int> leftKeys,
        IEnumerable<int> rightKeys,
        JoinType type = JoinType.Inner,
        Expr? residual = null)
    {
        var join = new HashJoin { Left = left, Right = right, Type = type };
        join.LeftKeys.AddRange(leftKeys.Select(k => (uint)k));
        join.RightKeys.AddRange(rightKeys.Select(k => (uint)k));
        if (residual is not null)
        {
            join.PostJoinFilter = residual;
        }

        var rel = new Rel { RowType = JoinedRow(left, right, type), HashJoin = join };
        rel.Collations.AddRange(left.Collations);
        return rel;
    }

    public static Rel MergeJoin(
        Rel left,
        Rel right,
        IEnumerable<int> leftKeys,
        IEnumerable<int> rightKeys,
        JoinType type = JoinType.Inner,
        Expr? residual = null)
    {
        var join = new MergeJoin { Left = left, Right = right, Type = type };
        join.LeftKeys.AddRange(leftKeys.Select(k => (uint)k));
        join.RightKeys.AddRange(rightKeys.Select(k => (uint)k));
        if (residual is not null)
        {
            join.PostJoinFilter = residual;
        }

        var rel = new Rel { RowType = JoinedRow(left, right, type), MergeJoin = join };
        rel.Collations.AddRange(left.Collations);
        return rel;
    }

    public static Rel NestedLoopJoin(
        Rel left, Rel right, Expr? condition = null, JoinType type = JoinType.Inner)
    {
        var join = new NestedLoopJoin { Left = left, Right = right, Type = type };
        if (condition is not null)
        {
            join.Condition = condition;
        }

        var rel = new Rel { RowType = JoinedRow(left, right, type), NestedLoopJoin = join };
        rel.Collations.AddRange(left.Collations);
        return rel;
    }

    public static Rel Join(Rel left, Rel right, Expr? condition = null, JoinType type = JoinType.Inner)
    {
        var join = new Join { Left = left, Right = right, Type = type };
        if (condition is not null)
        {
            join.Condition = condition;
        }

        return new Rel { RowType = JoinedRow(left, right, type), Join = join };
    }

    public static Rel AsOfJoin(
        Rel left,
        Rel right,
        IEnumerable<int> leftKeys,
        IEnumerable<int> rightKeys,
        int leftTime,
        int rightTime,
        AsOfMatch match,
        JoinType type = JoinType.Inner)
    {
        var join = new AsOfJoin
        {
            Left = left,
            Right = right,
            LeftTime = (uint)leftTime,
            RightTime = (uint)rightTime,
            Match = match,
            Type = type,
        };
        join.LeftKeys.AddRange(leftKeys.Select(k => (uint)k));
        join.RightKeys.AddRange(rightKeys.Select(k => (uint)k));
        var rel = new Rel { RowType = JoinedRow(left, right, type), AsOfJoin = join };
        rel.Collations.AddRange(left.Collations);
        return rel;
    }

    /// <summary>Adds an ordering claim to a rel — what a merge join reads back.</summary>
    public static Rel Collated(Rel rel, params (int Field, SortDirection Direction)[] fields)
    {
        var collation = new Collation();
        foreach (var (field, direction) in fields)
        {
            collation.Fields.Add(new SortField
            {
                Expr = Ref(rel.RowType, field),
                Direction = direction,
            });
        }

        rel.Collations.Add(collation);
        return rel;
    }

    /// <summary>A <c>RemoteQuery</c> leaf, for the tests that hand one to a fake source (M5).</summary>
    /// <param name="renderedBounds">
    /// The placeholder positions that are a pushed <c>LIMIT</c> or <c>OFFSET</c> the executor writes
    /// a number into rather than binding. Empty for every query without one.
    /// </param>
    /// <param name="pushedPlan">
    /// The algebra the text was generated from. Defaults to a bare leaf of the same row type; a
    /// test whose bound is an <c>OFFSET</c> passes one that says so, because that is where the
    /// executor reads the clause name for a refusal.
    /// </param>
    public static Rel RemoteQuery(
        string sourceId,
        string queryText,
        RowType row,
        double rows = 0,
        IEnumerable<Expr>? parameters = null,
        string dialect = "fake",
        IEnumerable<uint>? renderedBounds = null,
        Rel? pushedPlan = null)
    {
        var query = new RemoteQuery
        {
            SourceId = sourceId,
            Dialect = dialect,
            QueryText = queryText,
            PushedPlan = pushedPlan ?? new Rel { RowType = row },
        };
        if (parameters is not null)
        {
            query.Parameters.AddRange(parameters);
        }

        if (renderedBounds is not null)
        {
            query.RenderedBounds.AddRange(renderedBounds);
        }

        return new Rel { RowType = row, EstRowCount = rows, RemoteQuery = query };
    }

    /// <summary>A key set: the one option of the IN list a lookup query binds (M5, D105).</summary>
    public static Expr KeySet(IrType type, uint slot = 0) =>
        new() { Type = type, KeySet = new KeySetParam { Slot = slot } };

    /// <summary>A <c>LookupJoin</c> over a driving subtree and a key-set-bearing remote query.</summary>
    public static Rel LookupJoin(
        Rel driving,
        Rel lookup,
        int drivingKey,
        int lookupKey,
        JoinType type = JoinType.Inner,
        int maxKeysPerCall = 2,
        bool keySetRows = false,
        double rows = 0)
    {
        var join = new LookupJoin
        {
            Driving = driving,
            Lookup = lookup,
            Type = type,
            MaxKeysPerCall = maxKeysPerCall,
            KeySetRows = keySetRows,
        };
        join.DrivingKeys.Add((uint)drivingKey);
        join.LookupKeys.Add((uint)lookupKey);
        return new Rel
        {
            RowType = JoinedRow(driving, lookup, type),
            EstRowCount = rows,
            LookupJoin = join,
        };
    }

    /// <summary>A <c>MaterialisedInput</c> replaying the row type of <paramref name="small"/>.</summary>
    public static Rel MaterialisedInput(Rel small, uint slot = 0) => new()
    {
        RowType = small.RowType,
        EstRowCount = small.EstRowCount,
        MaterialisedInput = new MaterialisedInput { Slot = slot },
    };

    /// <summary>An <c>AdaptiveJoin</c> over a small side, a lookup branch and a local branch (D97).</summary>
    public static Rel AdaptiveJoin(
        Rel small,
        Rel lookupJoin,
        Rel localJoin,
        int key,
        int maxKeys,
        uint slot = 0)
    {
        var adaptive = new AdaptiveJoin
        {
            Small = small,
            Lookup = lookupJoin.LookupJoin,
            Local = localJoin,
            Key = (uint)key,
            MaxKeys = maxKeys,
            Slot = slot,
        };
        return new Rel { RowType = lookupJoin.RowType, AdaptiveJoin = adaptive };
    }

    /// <summary>A <c>PartitionedScan</c> over branches with the values they hold (D106).</summary>
    public static Rel PartitionedScan(
        IEnumerable<(Rel Branch, Expr? Value)> partitions, RowType? row = null)
    {
        var scan = new PartitionedScan();
        RowType? first = null;
        foreach (var (branch, value) in partitions)
        {
            first ??= branch.RowType;
            scan.Partitions.Add(branch);
            var match = new PartitionMatch();
            if (value is not null)
            {
                match.Value = value;
            }

            scan.Matches.Add(match);
        }

        return new Rel
        {
            RowType = row ?? first ?? new RowType(),
            PartitionedScan = scan,
        };
    }

    public static Rel Sort(Rel input, params SortField[] fields)
    {
        var sort = new Sort { Input = input };
        sort.Fields.AddRange(fields);
        var collation = new Collation();
        collation.Fields.AddRange(fields);
        var rel = new Rel { RowType = input.RowType, EstRowCount = input.EstRowCount, Sort = sort };
        rel.Collations.Add(collation);
        return rel;
    }

    public static Rel Fetch(Rel input, long offset, long? count) => new()
    {
        RowType = input.RowType,
        EstRowCount = input.EstRowCount,
        Fetch = count is null
            ? new Fetch { Input = input, Offset = offset }
            : new Fetch { Input = input, Offset = offset, Count = count.Value },
        Collations = { input.Collations },
    };

    /// <summary>
    /// A <c>Fetch</c> whose bounds are parameters rather than numbers (D285). A null ordinal leaves
    /// that bound as the literal beside it, so one call covers every mixture.
    /// </summary>
    public static Rel FetchParam(
        Rel input, int? offsetParam, int? countParam, long offset = 0, long? count = null)
    {
        var fetch = new Fetch { Input = input };
        if (offsetParam is { } skip)
        {
            fetch.OffsetParam = new DynamicParam { Index = (uint)skip };
        }
        else
        {
            fetch.Offset = offset;
        }

        if (countParam is { } bound)
        {
            fetch.CountParam = new DynamicParam { Index = (uint)bound };
        }
        else if (count is not null)
        {
            fetch.Count = count.Value;
        }

        return new Rel
        {
            RowType = input.RowType,
            EstRowCount = input.EstRowCount,
            Fetch = fetch,
            Collations = { input.Collations },
        };
    }

    /// <summary>The same for a <c>TopN</c>.</summary>
    public static Rel TopNParam(
        Rel input, int? offsetParam, int? countParam, long offset, long count, params SortField[] fields)
    {
        var topN = new TopN { Input = input };
        if (offsetParam is { } skip)
        {
            topN.OffsetParam = new DynamicParam { Index = (uint)skip };
        }
        else
        {
            topN.Offset = offset;
        }

        if (countParam is { } bound)
        {
            topN.CountParam = new DynamicParam { Index = (uint)bound };
        }
        else
        {
            topN.Count = count;
        }

        topN.Fields.AddRange(fields);
        var collation = new Collation();
        collation.Fields.AddRange(fields);
        var rel = new Rel { RowType = input.RowType, EstRowCount = count, TopN = topN };
        rel.Collations.Add(collation);
        return rel;
    }

    public static Rel TopN(Rel input, long offset, long count, params SortField[] fields)
    {
        var topN = new TopN { Input = input, Offset = offset, Count = count };
        topN.Fields.AddRange(fields);
        var collation = new Collation();
        collation.Fields.AddRange(fields);
        var rel = new Rel { RowType = input.RowType, EstRowCount = count, TopN = topN };
        rel.Collations.Add(collation);
        return rel;
    }

    public static Rel HashAggregate(
        Rel input,
        IEnumerable<int> keys,
        IEnumerable<(string Name, Measure Measure)> measures,
        double rows = 0)
    {
        var keyList = keys.ToList();
        var measureList = measures.ToList();
        var aggregate = new Aggregate { Input = input };
        var grouping = new Grouping();
        grouping.Keys.AddRange(keyList.Select(k => (uint)k));
        aggregate.Groupings.Add(grouping);
        aggregate.Measures.AddRange(measureList.Select(m => m.Measure));

        var fields = keyList
            .Select(k => input.RowType.Fields[k])
            .Concat(measureList.Select(m => F(m.Name, m.Measure.Type)))
            .ToArray();

        return new Rel
        {
            RowType = Row(fields),
            EstRowCount = rows,
            HashAggregate = new HashAggregate { Aggregate = aggregate },
        };
    }

    /// <summary>
    /// A window node (D48). The frame is written out in full, as the IR requires; the output row is
    /// the input's fields followed by one column per call.
    /// </summary>
    public static Rel Window(
        Rel input,
        IEnumerable<int> partitionKeys,
        IEnumerable<SortField> order,
        WindowFrame frame,
        IEnumerable<(string Name, WindowCall Call)> calls)
    {
        var orderList = order.ToList();
        var callList = calls.ToList();
        var window = new Window { Input = input, Frame = frame };
        window.PartitionKeys.AddRange(partitionKeys.Select(k => (uint)k));
        window.Order.AddRange(orderList);
        window.Calls.AddRange(callList.Select(c => c.Call));

        var fields = input.RowType.Fields
            .Concat(callList.Select(c => F(c.Name, c.Call.Type)))
            .ToArray();

        var rel = new Rel { RowType = Row(fields), EstRowCount = input.EstRowCount, Window = window };
        var collation = new Collation();
        foreach (var key in window.PartitionKeys)
        {
            collation.Fields.Add(new SortField
            {
                Expr = Ref((int)key, input.RowType.Fields[(int)key].Type),
                Direction = SortDirection.AscNullsLast,
            });
        }

        foreach (var field in orderList)
        {
            collation.Fields.Add(field);
        }

        if (collation.Fields.Count > 0)
        {
            rel.Collations.Add(collation);
        }

        return rel;
    }

    /// <summary>A frame, with both bounds and the exclusion spelled out.</summary>
    public static WindowFrame Frame(
        FrameMode mode,
        FrameBoundKind lower,
        FrameBoundKind upper,
        Expr? lowerOffset = null,
        Expr? upperOffset = null,
        FrameExclusion exclusion = FrameExclusion.NoOthers)
    {
        var low = new FrameBound { Kind = lower };
        if (lowerOffset is not null)
        {
            low.Offset = lowerOffset;
        }

        var high = new FrameBound { Kind = upper };
        if (upperOffset is not null)
        {
            high.Offset = upperOffset;
        }

        return new WindowFrame { Mode = mode, Lower = low, Upper = high, Exclusion = exclusion };
    }

    /// <summary>The default frame of an ordered window: everything up to and including the peers.</summary>
    public static WindowFrame RunningFrame() => Frame(
        FrameMode.Range, FrameBoundKind.UnboundedPreceding, FrameBoundKind.CurrentRow);

    /// <summary>The whole partition, which is what an unordered window gets.</summary>
    public static WindowFrame WholePartitionFrame() => Frame(
        FrameMode.Range, FrameBoundKind.UnboundedPreceding, FrameBoundKind.UnboundedFollowing);

    /// <summary>A <c>ROWS BETWEEN n PRECEDING AND m FOLLOWING</c> frame.</summary>
    public static WindowFrame RowsFrame(
        long preceding, long following, FrameExclusion exclusion = FrameExclusion.NoOthers) => Frame(
        FrameMode.Rows,
        FrameBoundKind.Preceding,
        FrameBoundKind.Following,
        Lit(preceding),
        Lit(following),
        exclusion);

    /// <summary>One window call over an aggregate function.</summary>
    public static WindowCall WinAgg(AggregateFunctionId function, IrType type, params Expr[] args)
    {
        var call = new WindowCall { Aggregate = function, Type = type };
        call.Args.AddRange(args);
        return call;
    }

    /// <summary>The same, with <c>DISTINCT</c> (D56).</summary>
    public static WindowCall WinAggDistinct(
        AggregateFunctionId function, IrType type, params Expr[] args)
    {
        var call = WinAgg(function, type, args);
        call.Distinct = true;
        return call;
    }

    /// <summary>A LIST type of <paramref name="element"/>, one level deep (D58).</summary>
    public static IrType List(IrType element, bool nullable = true) =>
        new() { Kind = TypeKind.List, Nullable = nullable, Element = element };

    /// <summary>
    /// A STRUCT type of <paramref name="fields"/>, in order, one level deep (D291). Each field keeps
    /// its own nullability; <paramref name="nullable"/> is the struct's own.
    /// </summary>
    public static IrType Struct(bool nullable, params Field[] fields)
    {
        var type = new IrType { Kind = TypeKind.Struct, Nullable = nullable };
        type.Fields.AddRange(fields);
        return type;
    }

    /// <summary>The same, non-nullable as a whole.</summary>
    public static IrType Struct(params Field[] fields) => Struct(nullable: false, fields);

    /// <summary>
    /// Field <paramref name="index"/> of a STRUCT-typed <paramref name="input"/> (D291), typed as
    /// I-IR-22 says: the field's own type, made nullable when the struct is.
    /// </summary>
    public static Expr FieldAccess(Expr input, int index)
    {
        ArgumentNullException.ThrowIfNull(input);
        var type = input.Type.Fields[index].Type.Clone();
        type.Nullable |= input.Type.Nullable;
        return new Expr
        {
            Type = type,
            FieldAccess = new FieldAccess { Input = input, Index = (uint)index },
        };
    }

    /// <summary>A call to a client-bodied user function, named <c>schema.name</c> (D78).</summary>
    public static Expr UserCall(string qualifiedName, IrType type, params Expr[] args)
    {
        var call = new ScalarCall { UserFunction = qualifiedName };
        call.Args.AddRange(args);
        return new Expr { Type = type, Call = call };
    }

    /// <summary>A measure over a client-bodied user aggregate, named <c>schema.name</c> (D80).</summary>
    public static Measure UserAgg(string qualifiedName, IrType type, params Expr[] args)
    {
        var measure = new Measure { UserFunction = qualifiedName, Type = type };
        measure.Args.AddRange(args);
        return measure;
    }

    /// <summary>A LIST literal (D58).</summary>
    public static Expr LitList(IrType element, params Expr[] elements)
    {
        var value = new ListValue();
        foreach (var e in elements)
        {
            value.Elements.Add(e.Literal);
        }

        return new Expr { Type = List(element), Literal = new Literal { ListValue = value } };
    }

    /// <summary>A hopping window (D55). The output is the input's fields plus the two bounds.</summary>
    public static Rel Hop(Rel input, int timeColumn, Expr slide, Expr size)
    {
        var bounds = input.RowType.Fields[timeColumn].Type;
        var fields = input.RowType.Fields
            .Concat([F("window_start", bounds), F("window_end", bounds)])
            .ToArray();
        var rel = new Rel
        {
            RowType = Row(fields),
            EstRowCount = input.EstRowCount,
            Hop = new Hop
            {
                Input = input,
                TimeColumn = (uint)timeColumn,
                Slide = slide,
                Size = size,
            },
        };

        foreach (var collation in input.Collations)
        {
            rel.Collations.Add(collation.Clone());
        }

        return rel;
    }

    /// <summary>A session window (D55). Its bounds are nullable: a NULL time belongs to no session.</summary>
    public static Rel Session(Rel input, IEnumerable<int> partitionKeys, int timeColumn, Expr gap)
    {
        var bounds = input.RowType.Fields[timeColumn].Type.Clone();
        bounds.Nullable = true;
        var fields = input.RowType.Fields
            .Concat([F("window_start", bounds), F("window_end", bounds)])
            .ToArray();
        var session = new Session { Input = input, TimeColumn = (uint)timeColumn, Gap = gap };
        var keys = partitionKeys.ToList();
        session.PartitionKeys.AddRange(keys.Select(k => (uint)k));

        var rel = new Rel
        {
            RowType = Row(fields),
            EstRowCount = input.EstRowCount,
            Session = session,
        };

        var collation = new Collation();
        foreach (var key in keys)
        {
            collation.Fields.Add(Asc(key, input.RowType.Fields[key].Type));
        }

        if (!keys.Contains(timeColumn))
        {
            collation.Fields.Add(Asc(timeColumn, input.RowType.Fields[timeColumn].Type));
        }

        rel.Collations.Add(collation);
        return rel;
    }

    /// <summary>Flattening a LIST column (D66).</summary>
    /// <summary>An n-ary set operation over inputs that already share a row type (D69).</summary>
    public static Rel SetOp(SetOpKind kind, params Rel[] inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var setOp = new SetOp { Kind = kind };
        setOp.Inputs.AddRange(inputs);
        return new Rel
        {
            RowType = inputs[0].RowType,
            EstRowCount = inputs.Sum(i => i.EstRowCount),
            SetOp = setOp,
        };
    }

    public static Rel Unnest(
        Rel input, int listColumn, bool withOrdinality = false, bool keepEmpty = false)
    {
        var element = input.RowType.Fields[listColumn].Type.Element.Clone();
        element.Nullable |= keepEmpty;
        var fields = input.RowType.Fields.Concat([F("element", element)]).ToList();
        if (withOrdinality)
        {
            fields.Add(F("ordinality", new IrType { Kind = TypeKind.I32, Nullable = keepEmpty }));
        }

        var rel = new Rel
        {
            RowType = Row(fields),
            EstRowCount = input.EstRowCount,
            Unnest = new Unnest
            {
                Input = input,
                ListColumn = (uint)listColumn,
                WithOrdinality = withOrdinality,
                KeepEmpty = keepEmpty,
            },
        };

        foreach (var collation in input.Collations)
        {
            rel.Collations.Add(collation.Clone());
        }

        return rel;
    }

    /// <summary>One window call over a ranking or navigation function.</summary>
    public static WindowCall WinFn(
        WindowFunctionId function, IrType type, bool ignoreNulls = false, params Expr[] args)
    {
        var call = new WindowCall { WindowFunction = function, Type = type, IgnoreNulls = ignoreNulls };
        call.Args.AddRange(args);
        return call;
    }

    public static Measure Agg(
        AggregateFunctionId function,
        IrType type,
        Expr? arg = null,
        bool distinct = false,
        Expr? filter = null,
        IEnumerable<Expr>? extraArgs = null,
        SortField? orderBy = null)
    {
        var measure = new Measure { Function = function, Type = type, Distinct = distinct };
        if (arg is not null)
        {
            measure.Args.Add(arg);
        }

        if (extraArgs is not null)
        {
            measure.Args.AddRange(extraArgs);
        }

        if (orderBy is not null)
        {
            measure.OrderBy.Add(orderBy);
        }

        if (filter is not null)
        {
            measure.Filter = filter;
        }

        return measure;
    }

    public static Rel Values(RowType rowType, params Expr[][] rows)
    {
        var table = new VirtualTable();
        foreach (var row in rows)
        {
            var virtualRow = new VirtualRow();
            virtualRow.Values.AddRange(row);
            table.Rows.Add(virtualRow);
        }

        return new Rel { RowType = rowType, EstRowCount = rows.Length, VirtualTable = table };
    }

    public static SortField Asc(int index, IrType type, bool nullsFirst = false) => new()
    {
        Expr = Ref(index, type),
        Direction = nullsFirst ? SortDirection.AscNullsFirst : SortDirection.AscNullsLast,
    };

    public static SortField Desc(int index, IrType type, bool nullsFirst = true) => new()
    {
        Expr = Ref(index, type),
        Direction = nullsFirst ? SortDirection.DescNullsFirst : SortDirection.DescNullsLast,
    };

    public static Collation Collation(params SortField[] fields)
    {
        var collation = new Collation();
        collation.Fields.AddRange(fields);
        return collation;
    }

    /// <summary>Wraps a root relation in a plan and stamps the correct digest.</summary>
    public static Plan Plan(
        Rel root,
        string contextId = "test",
        long catalogEpoch = 1,
        params IrType[] parameterTypes)
    {
        var plan = new Plan
        {
            IrVersion = IrVersion.Current,
            ContextId = contextId,
            CatalogEpoch = catalogEpoch,
            OutputType = root.RowType,
            Root = root,
        };
        plan.ParameterTypes.AddRange(parameterTypes);
        plan.PlanDigest = PlanDigest.Compute(plan);
        return plan;
    }

    public static Expr LitI8(sbyte value) => new() { Type = I8(), Literal = new Literal { I8Value = value } };

    public static Expr LitI16(short value) => new() { Type = I16(), Literal = new Literal { I16Value = value } };

    public static Expr LitTime(long microsecondsSinceMidnight, uint precision = 6) =>
        new() { Type = Time(precision), Literal = new Literal { TimeValue = microsecondsSinceMidnight } };

    public static Expr LitTimestampTz(long units, uint precision = 9) => new()
    {
        Type = TimestampTz(precision),
        Literal = new Literal { TimestampTzValue = units },
    };

    public static Expr LitIntervalDay(long microseconds) =>
        new() { Type = IntervalDay(), Literal = new Literal { IntervalDayValue = microseconds } };

    public static Expr LitIntervalYear(int months) =>
        new() { Type = IntervalYear(), Literal = new Literal { IntervalYearValue = months } };

    /// <summary>The same expression with a nullable result type, for a VirtualTable's typed columns.</summary>
    public static Expr Nullable(Expr expr)
    {
        ArgumentNullException.ThrowIfNull(expr);
        var copy = expr.Clone();
        copy.Type.Nullable = true;
        return copy;
    }

    /// <summary>A logical <c>Aggregate</c> — the node a recorded or third-party plan may carry (§4).</summary>
    public static Rel Aggregate(
        Rel input,
        IEnumerable<int> keys,
        IEnumerable<(string Name, Measure Measure)> measures,
        double rows = 0)
    {
        var hash = HashAggregate(input, keys, measures, rows);
        return new Rel
        {
            RowType = hash.RowType,
            EstRowCount = rows,
            Aggregate = hash.HashAggregate.Aggregate,
        };
    }
}
