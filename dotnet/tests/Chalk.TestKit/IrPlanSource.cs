using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Ir;
using Chalk.Sources;
using SourceCapabilities = Chalk.Catalog.SourceCapabilities;

namespace Chalk.TestKit;

/// <summary>
/// The other flavour of the pushed-subtree boundary (D84, corpus 14): a source whose query language
/// is Chalk's own IR. It reads <see cref="RemoteQueryRequest.PushedPlan"/> and never looks at
/// <see cref="RemoteQueryRequest.QueryText"/>, which for such a source is empty.
/// </summary>
/// <remarks>
/// <para>
/// It is backed by another source's scan — the POCO fixture — so the same rows are behind it as
/// behind every other copy in the corpus, and a disagreement means the pushed subtree was
/// interpreted wrongly rather than that the data differed.
/// </para>
/// <para>
/// The interpreter handles exactly what the descriptor declares and throws
/// <see cref="SourceContractException"/> on anything else, on purpose. That is the invariant of §1
/// made executable: if the planner ever pushes something this source did not claim, the test fails
/// loudly here instead of quietly returning the wrong rows. It is also why the interpreter is
/// written out by hand rather than delegating to Chalk's own executor — an adapter that could run
/// any subtree would have nothing to say about which subtrees it was sent.
/// </para>
/// </remarks>
public sealed class IrPlanSource : ISourceRuntime
{
    private readonly ISourceRuntime _inner;
    private readonly SchemaDescriptor _schema;

    /// <param name="sourceId">The id this source is registered under.</param>
    /// <param name="inner">The source whose scan supplies the rows.</param>
    /// <param name="tables">Which of the inner source's tables to expose.</param>
    public IrPlanSource(string sourceId, ISourceRuntime inner, params string[] tables)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(tables);

        SourceId = sourceId;
        _inner = inner;

        var innerSchema = inner.DescribeSchema();
        _schema = new SchemaDescriptor
        {
            SourceId = sourceId,
            Name = sourceId,
            Kind = SourceKind.Remote,
            Dialect = "chalk-ir",
            Capabilities = Declared,
            Tables = [.. tables.Select(t => Expose(innerSchema.FindTable(t)
                ?? throw new ArgumentException($"the inner source has no table '{t}'", nameof(tables))))],
        };
    }

    /// <summary>
    /// One of the inner source's tables as this source exposes it. Foreign keys are dropped: this
    /// source shows a subset of the inner one's tables, and a key pointing at a table it does not
    /// expose is a descriptor the validator rightly refuses.
    /// </summary>
    private static TableDescriptor Expose(TableDescriptor table) => new()
    {
        Name = table.Name,
        Columns = table.Columns,
        RowCount = table.RowCount,
        RowCountKind = table.RowCountKind,
        CostProfile = table.CostProfile,
        UniqueKeys = table.UniqueKeys,
        Collations = table.Collations,
        Indexes = table.Indexes,
    };

    /// <summary>
    /// Filters, projection and limits, and nothing else — corpus 14's "with filters and limits
    /// declared". No dialect profile: an IR source has no SQL to spell, so the questions a profile
    /// answers do not arise. It evaluates Chalk's IR, so it has Chalk's semantics by construction.
    /// </summary>
    public static SourceCapabilities Declared { get; } = new()
    {
        QueryLanguage = QueryLanguage.Ir,
        PushablePredicates =
        [
            PredicateShape.Eq,
            PredicateShape.Range,
            PredicateShape.And,
        ],
        SupportsProject = true,
        SupportsLimit = true,
    };

    /// <inheritdoc />
    public string SourceId { get; }

    /// <summary>How many subtrees this source has been asked to run.</summary>
    public int QueriesRun { get; private set; }

    /// <inheritdoc />
    public SchemaDescriptor DescribeSchema() => _schema;

    /// <inheritdoc />
    public IAsyncEnumerable<RecordBatch> ScanAsync(
        ScanRequest request, ScanContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PushedFilter is not null)
        {
            throw new SourceContractException(
                SourceId,
                request.Table,
                "a filter was pushed into a scan; this source evaluates predicates through "
                + "ExecuteQueryAsync.");
        }

        return _inner.ScanAsync(request, context, ct);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RecordBatch> ExecuteQueryAsync(
        RemoteQueryRequest request,
        ScanContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        if (request.QueryText.Length != 0)
        {
            throw new SourceContractException(
                SourceId,
                "the pushed subtree",
                $"this source's query language is IR, but a query_text arrived: {request.QueryText}");
        }

        QueriesRun++;
        var rows = await EvaluateAsync(request.PushedPlan, request, context, ct).ConfigureAwait(false);

        // Handed back through the same builder the fixtures use, so the batches are ordinary Arrow
        // and the engine cannot tell this source from any other. No rows means no batches, which is
        // what every other source does.
        for (var offset = 0; offset < rows.Count; offset += request.BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var take = Math.Min(request.BatchSize, rows.Count - offset);
            yield return BatchBuilder.FromStorageRows(request.OutputSchema, rows.GetRange(offset, take));
        }
    }

    /// <summary>Runs one node of the pushed subtree, bottom up, into a list of rows.</summary>
    private async Task<List<object?[]>> EvaluateAsync(
        Rel rel, RemoteQueryRequest request, ScanContext context, CancellationToken ct) =>
        rel.KindCase switch
        {
            Rel.KindOneofCase.Read => await ReadAsync(rel.Read, context, ct).ConfigureAwait(false),
            Rel.KindOneofCase.Filter => Filter(
                await EvaluateAsync(rel.Filter.Input, request, context, ct).ConfigureAwait(false),
                rel.Filter.Condition,
                request),
            Rel.KindOneofCase.Project => Project(
                await EvaluateAsync(rel.Project.Input, request, context, ct).ConfigureAwait(false),
                rel.Project.Exprs,
                request),
            Rel.KindOneofCase.Fetch => Fetch(
                await EvaluateAsync(rel.Fetch.Input, request, context, ct).ConfigureAwait(false),
                rel.Fetch),
            _ => throw new SourceContractException(
                SourceId,
                "the pushed subtree",
                $"it contains a {rel.KindCase}, which this source never declared it can do. "
                + "Nothing undeclared may be pushed."),
        };

    private async Task<List<object?[]>> ReadAsync(Read read, ScanContext context, CancellationToken ct)
    {
        var table = _schema.FindTable(read.Table.Table)
            ?? throw new SourceContractException(
                SourceId, read.Table.Table, "there is no such table in this source.");

        if (read.Filter is not null)
        {
            throw new SourceContractException(
                SourceId,
                read.Table.Table,
                "the Read carries a filter; this source's predicates arrive as a Filter node.");
        }

        var projection = read.Projection.Select(i => (int)i).ToArray();
        var columns = projection.Select(i => table.Columns[i]).ToList();
        var scan = new ScanRequest
        {
            Table = table.Name,
            Projection = projection,
            OutputSchema = ArrowTypeMapping.ToArrowSchema(columns),
            BatchSize = 4096,
        };

        // Storage values throughout: the interpreter compares what the columns actually hold, so
        // nothing is lost converting to host shapes and back.
        var batches = new List<RecordBatch>();
        try
        {
            await foreach (var batch in _inner.ScanAsync(scan, context, ct).WithCancellation(ct))
            {
                batches.Add(batch);
            }

            return BatchReader.ToStorageRows(batches);
        }
        finally
        {
            foreach (var batch in batches)
            {
                batch.Dispose();
            }
        }
    }

    private List<object?[]> Filter(List<object?[]> rows, Expr condition, RemoteQueryRequest request) =>
        [.. rows.Where(row => Evaluate(condition, row, request) is true)];

    private List<object?[]> Project(
        List<object?[]> rows, IReadOnlyList<Expr> exprs, RemoteQueryRequest request) =>
        [.. rows.Select(row => exprs.Select(e => Evaluate(e, row, request)).ToArray())];

    private static List<object?[]> Fetch(List<object?[]> rows, Fetch fetch)
    {
        var skipped = rows.Skip((int)fetch.Offset);
        return [.. fetch.HasCount ? skipped.Take((int)fetch.Count) : skipped];
    }

    /// <summary>
    /// One expression over one row. Only the shapes the descriptor declares are handled; anything
    /// else is a contract failure, which is the point.
    /// </summary>
    private object? Evaluate(Expr expr, object?[] row, RemoteQueryRequest request) => expr.KindCase switch
    {
        Expr.KindOneofCase.FieldRef => row[(int)expr.FieldRef.Index],
        Expr.KindOneofCase.Literal => Value(expr),
        Expr.KindOneofCase.Param => request.Parameters[(int)expr.Param.Index],
        Expr.KindOneofCase.Call => Call(expr.Call, row, request),
        _ => throw new SourceContractException(
            SourceId,
            "the pushed subtree",
            $"it contains a {expr.KindCase} expression, which this source never declared."),
    };

    private object? Call(ScalarCall call, object?[] row, RemoteQueryRequest request)
    {
        // SQL three-valued logic, which is what Chalk's IR means: a comparison with a NULL operand
        // is NULL, and a Filter drops a row whose condition is not true.
        if (call.Function == FunctionId.And)
        {
            var result = (bool?)true;
            foreach (var arg in call.Args)
            {
                if (Evaluate(arg, row, request) is not bool value)
                {
                    result = null;
                    continue;
                }

                if (!value)
                {
                    return false;
                }
            }

            return result;
        }

        if (call.Args.Count != 2)
        {
            throw Undeclared(call.Function);
        }

        var left = Evaluate(call.Args[0], row, request);
        var right = Evaluate(call.Args[1], row, request);
        if (left is null || right is null)
        {
            return null;
        }

        var order = Compare(left, right);
        return call.Function switch
        {
            FunctionId.Eq => order == 0,
            FunctionId.Lt => order < 0,
            FunctionId.Le => order <= 0,
            FunctionId.Gt => order > 0,
            FunctionId.Ge => order >= 0,
            _ => throw Undeclared(call.Function),
        };
    }

    private SourceContractException Undeclared(FunctionId function) => new(
        SourceId,
        "the pushed subtree",
        $"it calls {function}, which this source never declared it can evaluate.");

    private static int Compare(object left, object right) => (left, right) switch
    {
        (long a, long b) => a.CompareTo(b),
        (double a, double b) => a.CompareTo(b),
        (float a, float b) => a.CompareTo(b),
        (decimal a, decimal b) => a.CompareTo(b),

        // Code-point comparison, which is Chalk's own: this source evaluates Chalk's IR, so it has
        // Chalk's string semantics by construction rather than by declaration.
        (string a, string b) => string.CompareOrdinal(a, b),
        (bool a, bool b) => a.CompareTo(b),
        _ => Convert.ToDecimal(left, System.Globalization.CultureInfo.InvariantCulture)
            .CompareTo(Convert.ToDecimal(right, System.Globalization.CultureInfo.InvariantCulture)),
    };

    private static object? Value(Expr expr)
    {
        var literal = expr.Literal;
        return literal.ValueCase switch
        {
            Literal.ValueOneofCase.IsNull => null,
            Literal.ValueOneofCase.BoolValue => literal.BoolValue,
            Literal.ValueOneofCase.I8Value => (long)literal.I8Value,
            Literal.ValueOneofCase.I16Value => (long)literal.I16Value,
            Literal.ValueOneofCase.I32Value => (long)literal.I32Value,
            Literal.ValueOneofCase.I64Value => literal.I64Value,
            Literal.ValueOneofCase.Fp32Value => literal.Fp32Value,
            Literal.ValueOneofCase.Fp64Value => literal.Fp64Value,
            Literal.ValueOneofCase.StringValue => literal.StringValue,

            // Storage form, to match what the scan produces: a DATE is its day number, and a
            // DECIMAL is its unscaled bytes read at the type's own scale.
            Literal.ValueOneofCase.DateValue => (long)literal.DateValue,
            Literal.ValueOneofCase.TimeValue => literal.TimeValue,
            Literal.ValueOneofCase.TimestampValue => literal.TimestampValue,
            Literal.ValueOneofCase.DecimalValue => BatchReader.Decimal(
                literal.DecimalValue.Unscaled.Span, (int)expr.Type.Scale),
            _ => throw new NotSupportedException(
                $"the IR test source has no reading for a {literal.ValueCase} literal"),
        };
    }
}
