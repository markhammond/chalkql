using Chalk.Execution.Aggregation;
using Chalk.Execution.Vectors;
using Chalk.Sources;

namespace Chalk.Execution.Windowing;

/// <summary>
/// The general frame engine of D59 (<c>14-windows-ii.md</c> §4): a segment tree over one partition,
/// whose leaves hold a row's aggregate state and whose internal nodes hold their combination, so any
/// frame — including one an <c>EXCLUDE</c> has cut a hole in — is O(log n) combinations rather than
/// O(frame) additions.
/// </summary>
/// <remarks>
/// <para>
/// It replaces step 18's recomputation for <c>EXCLUDE</c>, which was O(frame) per row. The fast paths
/// of step 18 — the sliding accumulators, the monotonic deque, the running extremum — stay, because
/// they are cheaper still when no row is excluded; <see cref="WindowFrames.ForceSegmentTree"/> makes
/// every frame take this path so a property test can prove the two agree.
/// </para>
/// <para>
/// The three exclusions are all "the frame minus one contiguous interval": <c>CURRENT ROW</c> cuts
/// out one row, <c>GROUP</c> the current row's peer group, and <c>TIES</c> the peer group with the
/// current row put back. So one query is at most three range queries, whatever the frame's width.
/// </para>
/// </remarks>
internal abstract class WindowSegmentTree
{
    /// <summary>Leaves live at <c>[Size, Size + rows)</c>; node 1 is the root.</summary>
    protected int Size { get; private set; }

    protected int Rows { get; private set; }

    /// <summary>The row a leaf stands for, or -1 when the leaf is past the end of the partition.</summary>
    protected int RowOf(int leaf) => leaf < Rows ? Start + leaf : -1;

    protected int Start { get; private set; }

    /// <summary>Builds the tree over the partition's rows <c>[start, end)</c>.</summary>
    public void Build(ExecutionArena arena, int start, int end)
    {
        Start = start;
        Rows = end - start;
        Size = 1;
        while (Size < Math.Max(Rows, 1))
        {
            Size <<= 1;
        }

        Allocate(arena, Size * 2);
        for (var leaf = 0; leaf < Size; leaf++)
        {
            SetLeaf(Size + leaf, RowOf(leaf));
        }

        for (var node = Size - 1; node >= 1; node--)
        {
            Merge(node, node * 2, (node * 2) + 1);
        }
    }

    /// <summary>Rents the node arrays, sized for <paramref name="nodes"/> nodes.</summary>
    protected abstract void Allocate(ExecutionArena arena, int nodes);

    public abstract void Release(ExecutionArena arena);

    /// <summary>Fills a leaf from one row, or with the identity when the row is -1.</summary>
    protected abstract void SetLeaf(int node, int row);

    /// <summary>Combines two children into their parent.</summary>
    protected abstract void Merge(int node, int left, int right);

    /// <summary>Folds one node's state into the query's running answer.</summary>
    protected abstract void Accumulate(int node);

    /// <summary>Folds the rows <c>[lo, hi]</c> — partition-relative absolute row numbers — in.</summary>
    public void Query(int lo, int hi)
    {
        if (hi < lo)
        {
            return;
        }

        var left = Math.Max(lo - Start, 0) + Size;
        var right = Math.Min(hi - Start, Rows - 1) + Size;
        if (right < left)
        {
            return;
        }

        while (left <= right)
        {
            if ((left & 1) == 1)
            {
                Accumulate(left++);
            }

            if ((right & 1) == 0)
            {
                Accumulate(right--);
            }

            if (left > right)
            {
                break;
            }

            left >>= 1;
            right >>= 1;
        }
    }

    /// <summary>
    /// The frame <c>[lo, hi]</c> with the interval <c>[cutLo, cutHi]</c> taken out — which is every
    /// exclusion SQL has, once the caller has put the current row back for <c>TIES</c>.
    /// </summary>
    public void QueryExcluding(int lo, int hi, int cutLo, int cutHi)
    {
        Query(lo, Math.Min(hi, cutLo - 1));
        Query(Math.Max(lo, cutHi + 1), hi);
    }
}

/// <summary>
/// The numeric tree: a count and a sum per node, which is what <c>COUNT</c>, <c>SUM</c>,
/// <c>SUM0</c> and <c>AVG</c> all read. Only the sum array the result kind needs is rented.
/// </summary>
internal sealed class WindowSumTree : WindowSegmentTree
{
    private readonly ColumnKind _kind;
    private readonly bool _starCount;

    /// <summary>
    /// Whether the caller reads <see cref="Count"/> and nothing else — a <c>COUNT(x)</c>, whose
    /// result kind is BIGINT and which would otherwise leave a checked sum of the argument in every
    /// leaf that no caller ever reads (ADR 0044). Unlike <see cref="_starCount"/> the argument's
    /// validity still decides whether the row is counted.
    /// </summary>
    private readonly bool _countsOnly;

    private WindowColumn? _column;

    private long[] _counts = [];
    private long[] _integers = [];
    private double[] _doubles = [];
    private float[] _singles = [];
    private decimal[] _decimals = [];

    public WindowSumTree(ColumnKind resultKind, bool starCount, bool countsOnly = false)
    {
        _kind = resultKind;
        _starCount = starCount;
        _countsOnly = countsOnly;
    }

    /// <summary>The running answer of the last <see cref="WindowSegmentTree.Query"/> sequence.</summary>
    public long Count { get; private set; }

    public long Integer { get; private set; }

    public double Double { get; private set; }

    public float Single { get; private set; }

    public decimal Decimal { get; private set; }

    public void Reset()
    {
        Count = 0;
        Integer = 0;
        Double = 0;
        Single = 0;
        Decimal = 0;
    }

    public void Attach(WindowColumn? column) => _column = column;

    protected override void Allocate(ExecutionArena arena, int nodes)
    {
        Release(arena);
        _counts = arena.Rent<long>(nodes);
        System.Array.Clear(_counts, 0, nodes);
        switch (_kind)
        {
            case ColumnKind.Float:
                _singles = arena.Rent<float>(nodes);
                System.Array.Clear(_singles, 0, nodes);
                break;
            case ColumnKind.Double:
                _doubles = arena.Rent<double>(nodes);
                System.Array.Clear(_doubles, 0, nodes);
                break;
            case ColumnKind.Decimal128:
                _decimals = arena.Rent<decimal>(nodes);
                System.Array.Clear(_decimals, 0, nodes);
                break;
            default:
                _integers = arena.Rent<long>(nodes);
                System.Array.Clear(_integers, 0, nodes);
                break;
        }
    }

    public override void Release(ExecutionArena arena)
    {
        arena.Return(_counts);
        arena.Return(_integers);
        arena.Return(_doubles);
        arena.Return(_singles);
        arena.Return(_decimals);
        _counts = [];
        _integers = [];
        _doubles = [];
        _singles = [];
        _decimals = [];
    }

    protected override void SetLeaf(int node, int row)
    {
        _counts[node] = 0;
        Write(node, 0, 0, 0, 0);
        if (row < 0)
        {
            return;
        }

        if (_starCount)
        {
            _counts[node] = 1;
            return;
        }

        var column = _column!;
        if (!column.IsValid(row))
        {
            return;
        }

        _counts[node] = 1;
        if (_countsOnly)
        {
            // The leaf's value is never read, and reading it would truncate the argument into a long.
            return;
        }

        switch (_kind)
        {
            case ColumnKind.Float:
                _singles[node] = (float)column.Double(row);
                break;
            case ColumnKind.Double:
                _doubles[node] = column.Double(row);
                break;
            case ColumnKind.Decimal128:
                _decimals[node] = column.Decimal(row);
                break;
            default:
                _integers[node] = Integers(column, row);
                break;
        }
    }

    protected override void Merge(int node, int left, int right)
    {
        _counts[node] = _counts[left] + _counts[right];
        switch (_kind)
        {
            case ColumnKind.Float:
                _singles[node] = _singles[left] + _singles[right];
                break;
            case ColumnKind.Double:
                _doubles[node] = _doubles[left] + _doubles[right];
                break;
            case ColumnKind.Decimal128:
                _decimals[node] = _decimals[left] + _decimals[right];
                break;
            default:
                _integers[node] = checked(_integers[left] + _integers[right]);
                break;
        }
    }

    protected override void Accumulate(int node)
    {
        Count += _counts[node];
        switch (_kind)
        {
            case ColumnKind.Float:
                Single += _singles[node];
                break;
            case ColumnKind.Double:
                Double += _doubles[node];
                break;
            case ColumnKind.Decimal128:
                Decimal += _decimals[node];
                break;
            default:
                Integer = checked(Integer + _integers[node]);
                break;
        }
    }

    private void Write(int node, long integer, double dbl, float single, decimal dec)
    {
        switch (_kind)
        {
            case ColumnKind.Float:
                _singles[node] = single;
                break;
            case ColumnKind.Double:
                _doubles[node] = dbl;
                break;
            case ColumnKind.Decimal128:
                _decimals[node] = dec;
                break;
            default:
                _integers[node] = integer;
                break;
        }
    }

    /// <summary>The argument as an integer, matching what the sliding accumulator reads.</summary>
    private static long Integers(WindowColumn column, int row) => column.Kind switch
    {
        ColumnKind.Float or ColumnKind.Double => checked((long)Math.Truncate(column.Double(row))),
        ColumnKind.Decimal128 => checked((long)decimal.Truncate(column.Decimal(row))),
        _ => column.Integer(row),
    };
}

/// <summary>
/// The extremum tree: one row index per node — the row holding that range's minimum or maximum, or
/// -1 when the range has no value. Storing an index rather than a value means every comparable type
/// works and nothing is copied, which is the same trick <c>WindowRowEvaluator</c> uses.
/// </summary>
internal sealed class WindowExtremumTree : WindowSegmentTree
{
    private readonly bool _max;
    private WindowColumn? _column;
    private int[] _rows = [];
    private byte[] _leftScratch = new byte[16];
    private byte[] _rightScratch = new byte[16];

    public WindowExtremumTree(bool max) => _max = max;

    /// <summary>The row holding the answer of the last query sequence, or -1 when there is none.</summary>
    public int Winner { get; private set; } = -1;

    public void Reset() => Winner = -1;

    public void Attach(WindowColumn column) => _column = column;

    protected override void Allocate(ExecutionArena arena, int nodes)
    {
        Release(arena);
        _rows = arena.Rent<int>(nodes);
    }

    public override void Release(ExecutionArena arena)
    {
        arena.Return(_rows);
        _rows = [];
    }

    protected override void SetLeaf(int node, int row) =>
        _rows[node] = row >= 0 && _column!.IsValid(row) ? row : -1;

    protected override void Merge(int node, int left, int right) =>
        _rows[node] = Better(_rows[left], _rows[right]);

    protected override void Accumulate(int node) => Winner = Better(Winner, _rows[node]);

    /// <summary>The better of two rows under this tree's comparison; -1 means "no value".</summary>
    private int Better(int left, int right)
    {
        if (left < 0)
        {
            return right;
        }

        if (right < 0)
        {
            return left;
        }

        var column = _column!;
        var comparison = LaneComparer.Compare(
            column.Kind, column.Lane(left, _leftScratch), column.Lane(right, _rightScratch));
        if (comparison == 0)
        {
            return Math.Min(left, right);
        }

        return (_max ? comparison > 0 : comparison < 0) ? left : right;
    }
}
