using System.Reflection;
using Chalk.Catalog;
using Chalk.Execution.Expressions;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Operators;

/// <summary>
/// A client-bodied table function (D78): the host's producer is called once per execution with the
/// argument values and its rows are written into batches.
/// </summary>
/// <remarks>
/// The producer is an ordinary .NET method returning an <c>IEnumerable&lt;TRow&gt;</c>, so it is
/// enumerated lazily and a table function that streams stays streaming. Row extraction is a typed
/// accessor per declared column, built once at plan compilation; a table function is not on the
/// zero-allocation path — the host's own enumerable decides that — but nothing here adds to it.
/// </remarks>
internal sealed class TableFunctionScanOperator : OperatorBase
{
    private readonly ITableRows _rows;
    private readonly ColumnCopier[] _copiers;
    private readonly ColumnarBatch _output;

    public TableFunctionScanOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> columnTypes,
        ITableRows rows)
        : base(context, schema, columnTypes, path)
    {
        _rows = rows;
        _copiers = [.. columnTypes.Select(t => new ColumnCopier(t))];
        _output = NewOutput();
    }

    protected override ValueTask DisposeCoreAsync() => default;

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        var batchSize = Context.Settings.BatchSize;
        var enumerator = _rows.Rows(Context).GetEnumerator();
        try
        {
            var more = enumerator.MoveNext();
            while (more)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var copier in _copiers)
                {
                    copier.Begin();
                }

                var count = 0;
                while (more && count < batchSize)
                {
                    _rows.Append(_copiers, enumerator.Current);
                    count++;
                    more = enumerator.MoveNext();
                }

                _output.Begin(count);
                for (var c = 0; c < _copiers.Length; c++)
                {
                    _output.Set(c, _copiers[c].FinishView());
                }

                // Not RowsScanned: those are rows read from a source, and a table function's rows
                // are made rather than read. RowsProduced counts them, as it counts every operator's.
                yield return _output;
            }
        }
        finally
        {
            enumerator.Dispose();
        }
    }
}

/// <summary>The type-erased half of a table function: rows in, columns out.</summary>
internal interface ITableRows
{
    /// <summary>The producer's rows for this execution, with the arguments bound.</summary>
    IEnumerable<object?> Rows(OperatorContext? context);

    /// <summary>Appends one row to the output columns.</summary>
    void Append(ColumnCopier[] copiers, object? row);

    /// <summary>One row as boxed values, which is what the reference executor works in (D13).</summary>
    object?[] Read(object? row);
}

/// <summary>
/// One registered producer, bound to a declaration. Built once at plan compilation, so a mismatched
/// row type or an unsupported column kind is refused there rather than mid-stream.
/// </summary>
internal sealed class TableRows<TRow> : ITableRows
{
    private readonly Delegate _producer;
    private readonly object?[] _arguments;
    private readonly RowColumn<TRow>[] _columns;

    public TableRows(
        Delegate producer, object?[] arguments, IReadOnlyList<ColumnDescriptor> columns, string what)
    {
        _producer = producer;
        _arguments = arguments;
        _columns = [.. columns.Select(c => RowColumn<TRow>.For(c, columns.Count, what))];
    }

    public IEnumerable<object?> Rows(OperatorContext? context)
    {
        var produced = _producer.DynamicInvoke(_arguments);
        if (produced is not IEnumerable<TRow> rows)
        {
            throw new UnsupportedFeatureException(
                $"a table function returning {produced?.GetType().Name ?? "null"}",
                $"A table function's producer returns IEnumerable<{typeof(TRow).Name}> "
                + "(docs/design/17-user-defined-functions.md §3).");
        }

        foreach (var row in rows)
        {
            yield return row;
        }
    }

    public void Append(ColumnCopier[] copiers, object? row)
    {
        var typed = (TRow)row!;
        for (var c = 0; c < _columns.Length; c++)
        {
            _columns[c].Append(copiers[c], typed);
        }
    }

    public object?[] Read(object? row)
    {
        var typed = (TRow)row!;
        var values = new object?[_columns.Length];
        for (var c = 0; c < values.Length; c++)
        {
            values[c] = _columns[c].Read(typed);
        }

        return values;
    }
}

/// <summary>One declared column, read off a row.</summary>
internal abstract class RowColumn<TRow>
{
    public abstract void Append(ColumnCopier copier, TRow row);

    /// <summary>
    /// The value in the reference executor's own vocabulary: every exact integer and temporal kind is
    /// a <c>long</c> there, so an INTEGER column reads back the same under both engines.
    /// </summary>
    public abstract object? Read(TRow row);

    /// <summary>
    /// Binds a declared column to a member of <typeparamref name="TRow"/> — or to the row itself,
    /// when the producer yields a bare value and the function returns one column.
    /// </summary>
    public static RowColumn<TRow> For(ColumnDescriptor column, int columnCount, string what)
    {
        var clr = LaneCodec.ClrTypeOf(column.Type)
            ?? throw new UnsupportedFeatureException(
                $"{what}: column '{column.Name}' of {LaneCodec.Describe(column.Type)}",
                "A v1 table function returns the scalar kinds a Tier 1 delegate can carry "
                + "(docs/design/17-user-defined-functions.md §3).");

        if (columnCount == 1 && Matches(typeof(TRow), clr))
        {
            return Build(column, clr, accessor: null, what);
        }

        var member = Member(column.Name)
            ?? throw new UnsupportedFeatureException(
                $"{what}: column '{column.Name}'",
                $"{typeof(TRow).Name} has no public property or field with that name; a table "
                + "function's row type names its columns the way a POCO table does.");
        return Build(column, clr, member, what);
    }

    private static bool Matches(Type actual, Type required) =>
        actual == required || Nullable.GetUnderlyingType(actual) == required;

    private static MemberInfo? Member(string name)
    {
        static string Key(string value) => value.Replace("_", string.Empty, StringComparison.Ordinal);
        foreach (var property in typeof(TRow).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (string.Equals(Key(property.Name), Key(name), StringComparison.OrdinalIgnoreCase))
            {
                return property;
            }
        }

        foreach (var field in typeof(TRow).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (string.Equals(Key(field.Name), Key(name), StringComparison.OrdinalIgnoreCase))
            {
                return field;
            }
        }

        return null;
    }

    private static RowColumn<TRow> Build(
        ColumnDescriptor column, Type clr, MemberInfo? accessor, string what)
    {
        var actual = accessor switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            _ => typeof(TRow),
        };

        if (!Matches(actual, clr))
        {
            throw new UnsupportedFeatureException(
                $"{what}: column '{column.Name}'",
                $"it is declared {LaneCodec.Describe(column.Type)} and the row's member is "
                + $"{actual.Name}.");
        }

        return new ReflectedRowColumn<TRow>(column.Type, accessor);
    }
}

/// <summary>
/// Reads one member and writes one lane. Reflection is used to <em>read</em> the value, which costs
/// a boxing per row; a table function's rows come from a host enumerable that has already allocated
/// them, so this is not on any gate the engine keeps.
/// </summary>
internal sealed class ReflectedRowColumn<TRow> : RowColumn<TRow>
{
    private readonly ChalkType _type;
    private readonly MemberInfo? _accessor;
    private readonly int _width;
    private readonly bool _variable;

    public ReflectedRowColumn(ChalkType type, MemberInfo? accessor)
    {
        _type = type;
        _accessor = accessor;
        var kind = ColumnKinds.Of(type);
        _width = ColumnKinds.Width(kind);
        _variable = ColumnKinds.IsVariableLength(kind);
    }

    public override object? Read(TRow row) => Normalise(Member(row));

    private object? Member(TRow row) => _accessor switch
    {
        PropertyInfo property => property.GetValue(row),
        FieldInfo field => field.GetValue(row),
        _ => row,
    };

    /// <summary>The reference executor boxes every exact and temporal kind as a <c>long</c>.</summary>
    private static object? Normalise(object? value) => value switch
    {
        sbyte v => (long)v,
        short v => (long)v,
        int v => (long)v,
        _ => value,
    };

    public override void Append(ColumnCopier copier, TRow row)
    {
        var value = Member(row);

        if (value is null)
        {
            if (_variable)
            {
                copier.AppendConstant(ScalarValue.Null(_type), 1);
            }
            else
            {
                Span<byte> empty = stackalloc byte[_width];
                empty.Clear();
                copier.AppendRaw(empty, valid: false);
            }

            return;
        }

        if (_variable)
        {
            copier.AppendConstant(
                new ScalarValue { Type = _type, Text = (string)value }, 1);
            return;
        }

        Span<byte> lane = stackalloc byte[_width];
        lane.Clear();
        WriteLane(lane, value);
        copier.AppendRaw(lane, valid: true);
    }

    private static void WriteLane(Span<byte> lane, object value)
    {
        switch (value)
        {
            case bool b:
                lane[0] = (byte)(b ? 1 : 0);
                break;
            case sbyte v:
                System.Runtime.InteropServices.MemoryMarshal.Write(lane, in v);
                break;
            case short v:
                System.Runtime.InteropServices.MemoryMarshal.Write(lane, in v);
                break;
            case int v:
                System.Runtime.InteropServices.MemoryMarshal.Write(lane, in v);
                break;
            case long v:
                System.Runtime.InteropServices.MemoryMarshal.Write(lane, in v);
                break;
            case float v:
                System.Runtime.InteropServices.MemoryMarshal.Write(lane, in v);
                break;
            case double v:
                System.Runtime.InteropServices.MemoryMarshal.Write(lane, in v);
                break;
            default:
                throw new UnsupportedFeatureException(
                    $"a table function column of CLR type {value.GetType().Name}",
                    "docs/design/17-user-defined-functions.md §3 lists the types a v1 table function "
                    + "returns.");
        }
    }
}

/// <summary>Builds the row reader for a registered producer, with the row type known statically.</summary>
internal sealed class TableRowsFactory : IHostTableVisitor<ITableRows>
{
    private readonly object?[] _arguments;
    private readonly IReadOnlyList<ColumnDescriptor> _columns;
    private readonly string _what;

    public TableRowsFactory(object?[] arguments, IReadOnlyList<ColumnDescriptor> columns, string what)
    {
        _arguments = arguments;
        _columns = columns;
        _what = what;
    }

    public ITableRows Visit<TRow>(Delegate producer) =>
        new TableRows<TRow>(producer, _arguments, _columns, _what);
}
