using Chalk.Catalog;
using Chalk.Execution.Aggregation;
using Chalk.Execution.Numeric;
using Chalk.Execution.Vectors;
using Chalk.Sources;

namespace Chalk.Execution.Windowing;

/// <summary>
/// One buffered input column, read by row. The window operator materialises its whole input before
/// it computes anything (§4), so every read here is against the operator's own buffered column
/// rather than a batch that is about to be recycled.
/// </summary>
/// <remarks>
/// The lane readers go through <see cref="LaneAccess"/>, which is also what the hash aggregate uses:
/// floating point is normalised the same way (every NaN is one value, -0.0 is 0.0), so a partition
/// key or an order key groups identically wherever it is compared.
/// </remarks>
internal sealed class WindowColumn
{
    private Vector _vector;

    public WindowColumn(in ColumnView view, int rows, ChalkType type)
    {
        _vector = Vector.FromView(view, rows);
        View = view;
        Type = type;
        Kind = ColumnKinds.Of(type);
        Width = ColumnKinds.Width(Kind);
    }

    /// <summary>
    /// Points this accessor at another view of the same type. The streaming path's retained rows move
    /// whenever its store grows or is compacted — once a batch, never once a row — and re-pointing is
    /// what keeps that off the per-row allocation budget (D258.4).
    /// </summary>
    public void Retarget(in ColumnView view, int rows)
    {
        _vector = Vector.FromView(view, rows);
        View = view;
    }

    /// <summary>The buffered column, valid for the length of the operator's run.</summary>
    public ColumnView View { get; private set; }

    public ChalkType Type { get; }

    public ColumnKind Kind { get; }

    public int Width { get; }

    public bool IsValid(int row) => View.IsValid(row);

    /// <summary>The row's bytes, in <paramref name="scratch"/> or as a view into the column.</summary>
    public ReadOnlySpan<byte> Lane(int row, Span<byte> scratch) =>
        LaneAccess.Read(_vector, row, Kind, Width, scratch);

    /// <summary>The row as a 64-bit integer — every integral and temporal layout but DATE's width.</summary>
    public long Integer(int row) => Kind switch
    {
        ColumnKind.Int8 => (sbyte)View.RawLanes(1)[row],
        ColumnKind.Int16 => View.Lanes<short>()[row],
        ColumnKind.Int32 => View.Lanes<int>()[row],
        ColumnKind.Boolean => View.BoolAt(row) ? 1L : 0L,
        _ => View.Lanes<long>()[row],
    };

    public double Double(int row) => Kind switch
    {
        ColumnKind.Float => View.Lanes<float>()[row],
        ColumnKind.Double => View.Lanes<double>()[row],
        ColumnKind.Decimal128 => (double)Decimal(row),
        _ => Integer(row),
    };

    public decimal Decimal(int row) => Kind switch
    {
        ColumnKind.Decimal128 => Decimals.Read(View.RawLanes(16).Slice(row * 16, 16), Type.Scale),
        ColumnKind.Float => (decimal)View.Lanes<float>()[row],
        ColumnKind.Double => (decimal)View.Lanes<double>()[row],
        _ => Integer(row),
    };
}

/// <summary>Comparisons over a set of buffered columns, for partition runs and peer groups.</summary>
internal sealed class WindowRowComparer
{
    private readonly WindowColumn[] _columns;
    private readonly byte[] _left = new byte[16];
    private readonly byte[] _right = new byte[16];

    public WindowRowComparer(WindowColumn[] columns) => _columns = columns;

    public int ColumnCount => _columns.Length;

    /// <summary>
    /// Whether two rows agree on every column, with NULLs equal to each other — the grouping rule,
    /// which is what both a partition key ("NULLs partition together") and a peer group want.
    /// </summary>
    public bool SameGroup(int a, int b)
    {
        Span<byte> left = _left;
        Span<byte> right = _right;
        foreach (var column in _columns)
        {
            var leftValid = column.IsValid(a);
            var rightValid = column.IsValid(b);
            if (leftValid != rightValid)
            {
                return false;
            }

            if (!leftValid)
            {
                continue;
            }

            if (LaneComparer.Compare(column.Kind, column.Lane(a, left), column.Lane(b, right)) != 0)
            {
                return false;
            }
        }

        return true;
    }
}
