using Chalk.Catalog;
using Chalk.Execution.Numeric;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution.Windowing;

/// <summary>
/// Turns one window's frame specification into each row's <c>[lo, hi]</c>, partition by partition.
///
/// <para>
/// The offsets are bound once per execution — they are literals or parameters, never expressions — and
/// a <c>RANGE</c> frame's order key is materialised once into the lane its offset is added in: 64-bit
/// integers for the integral and temporal kinds (where an interval offset is converted into the key's
/// own units first), double for the approximate ones, decimal for DECIMAL.
/// </para>
/// </summary>
internal sealed class WindowFrameBuilder
{
    private readonly WindowSpec _spec;
    private readonly WindowRun _run;
    private readonly bool _hasOffsets;
    private readonly ColumnKind _lane;

    private readonly long _lowerRows;
    private readonly long _upperRows;
    private readonly long _lowerLong;
    private readonly long _upperLong;
    private readonly double _lowerDouble;
    private readonly double _upperDouble;
    private readonly decimal _lowerDecimal;
    private readonly decimal _upperDecimal;

    private long[] _longKeys = [];
    private double[] _doubleKeys = [];
    private decimal[] _decimalKeys = [];

    public WindowFrameBuilder(WindowSpec spec, WindowRun run, ExecutionArena arena, int rows)
    {
        _spec = spec;
        _run = run;
        _hasOffsets = spec.Lower.HasOffset || spec.Upper.HasOffset;

        if (spec.Mode == FrameMode.Rows)
        {
            _lowerRows = RowOffset(spec.Lower, run.Parameters);
            _upperRows = RowOffset(spec.Upper, run.Parameters);
            return;
        }

        if (!_hasOffsets)
        {
            return;
        }

        var key = spec.RangeKey;
        var keyType = spec.InputTypes[key.Column];
        _lane = WindowFrames.RangeLane(keyType);
        var column = run.Columns[key.Column];

        switch (_lane)
        {
            case ColumnKind.Int64:
                _longKeys = arena.Rent<long>(rows);
                for (var i = 0; i < rows; i++)
                {
                    _longKeys[i] = column.IsValid(i) ? column.Integer(i) : 0;
                }

                _lowerLong = LongOffset(spec.Lower, run.Parameters, keyType);
                _upperLong = LongOffset(spec.Upper, run.Parameters, keyType);
                return;

            case ColumnKind.Double:
                _doubleKeys = arena.Rent<double>(rows);
                for (var i = 0; i < rows; i++)
                {
                    _doubleKeys[i] = column.IsValid(i) ? column.Double(i) : 0;
                }

                _lowerDouble = DoubleOffset(spec.Lower, run.Parameters);
                _upperDouble = DoubleOffset(spec.Upper, run.Parameters);
                return;

            default:
                _decimalKeys = arena.Rent<decimal>(rows);
                for (var i = 0; i < rows; i++)
                {
                    _decimalKeys[i] = column.IsValid(i) ? column.Decimal(i) : 0m;
                }

                _lowerDecimal = DecimalOffset(spec.Lower, run.Parameters);
                _upperDecimal = DecimalOffset(spec.Upper, run.Parameters);
                return;
        }
    }

    /// <summary>Computes <c>[lo, hi]</c> for every row of the partition <c>[start, end)</c>.</summary>
    public void Build(int start, int end)
    {
        if (_spec.Mode == FrameMode.Rows)
        {
            WindowFrames.Rows(
                start, end, _spec.Lower.Kind, _lowerRows, _spec.Upper.Kind, _upperRows,
                _run.FrameLo, _run.FrameHi);
            return;
        }

        if (!_hasOffsets)
        {
            WindowFrames.RangePeers(
                start, end, _spec.Lower.Kind, _spec.Upper.Kind,
                _run.PeerStart, _run.PeerEnd, _run.FrameLo, _run.FrameHi);
            return;
        }

        var column = _run.Columns[_spec.RangeKey.Column];
        var nonNullStart = start;
        while (nonNullStart < end && !column.IsValid(nonNullStart))
        {
            nonNullStart++;
        }

        var nonNullEnd = end;
        while (nonNullEnd > nonNullStart && !column.IsValid(nonNullEnd - 1))
        {
            nonNullEnd--;
        }

        var descending = _spec.RangeKey.Descending;
        switch (_lane)
        {
            case ColumnKind.Int64:
                WindowFrames.RangeOffsets<long>(
                    _longKeys, start, end, nonNullStart, nonNullEnd, descending,
                    _spec.Lower.Kind, _lowerLong, _spec.Upper.Kind, _upperLong,
                    _run.PeerStart, _run.PeerEnd, _run.FrameLo, _run.FrameHi);
                return;
            case ColumnKind.Double:
                WindowFrames.RangeOffsets<double>(
                    _doubleKeys, start, end, nonNullStart, nonNullEnd, descending,
                    _spec.Lower.Kind, _lowerDouble, _spec.Upper.Kind, _upperDouble,
                    _run.PeerStart, _run.PeerEnd, _run.FrameLo, _run.FrameHi);
                return;
            default:
                WindowFrames.RangeOffsets<decimal>(
                    _decimalKeys, start, end, nonNullStart, nonNullEnd, descending,
                    _spec.Lower.Kind, _lowerDecimal, _spec.Upper.Kind, _upperDecimal,
                    _run.PeerStart, _run.PeerEnd, _run.FrameLo, _run.FrameHi);
                return;
        }
    }

    public void Release(ExecutionArena arena)
    {
        arena.Return(_longKeys);
        arena.Return(_doubleKeys);
        arena.Return(_decimalKeys);
        _longKeys = [];
        _doubleKeys = [];
        _decimalKeys = [];
    }

    internal static ScalarValue? Offset(WindowBound bound, IReadOnlyList<ScalarValue> parameters)
    {
        if (!bound.HasOffset)
        {
            return null;
        }

        var value = bound.Offset!.Bind(parameters);
        if (value.IsNull)
        {
            throw new InvalidOperationException("a window frame offset must not be NULL.");
        }

        return value;
    }

    internal static long RowOffset(WindowBound bound, IReadOnlyList<ScalarValue> parameters)
    {
        var value = Offset(bound, parameters);
        if (value is null)
        {
            return 0;
        }

        return value.Integer >= 0
            ? value.Integer
            : throw new InvalidOperationException(
                $"a ROWS frame offset must not be negative; it is {value.Integer}.");
    }

    internal static long LongOffset(
        WindowBound bound, IReadOnlyList<ScalarValue> parameters, ChalkType key)
    {
        var value = Offset(bound, parameters);
        if (value is null)
        {
            return 0;
        }

        var raw = value.Type.Kind == TypeKind.IntervalDay
            ? WindowFrames.IntervalToKeyUnits(value.Integer, key)
            : value.Integer;
        return raw >= 0
            ? raw
            : throw new InvalidOperationException(
                $"a RANGE frame offset must not be negative; it is {raw}.");
    }

    internal static double DoubleOffset(WindowBound bound, IReadOnlyList<ScalarValue> parameters)
    {
        var value = Offset(bound, parameters);
        if (value is null)
        {
            return 0;
        }

        var raw = value.Type.Kind switch
        {
            TypeKind.Fp32 => value.Single,
            TypeKind.Fp64 => value.Double,
            TypeKind.Decimal => (double)Decimals.Read(value.ReadBytes(), value.Type.Scale),
            _ => value.Integer,
        };
        return raw >= 0
            ? raw
            : throw new InvalidOperationException(
                $"a RANGE frame offset must not be negative; it is {raw}.");
    }

    internal static decimal DecimalOffset(WindowBound bound, IReadOnlyList<ScalarValue> parameters)
    {
        var value = Offset(bound, parameters);
        if (value is null)
        {
            return 0m;
        }

        var raw = value.Type.Kind switch
        {
            TypeKind.Decimal => Decimals.Read(value.ReadBytes(), value.Type.Scale),
            TypeKind.Fp32 => (decimal)value.Single,
            TypeKind.Fp64 => (decimal)value.Double,
            _ => value.Integer,
        };
        return raw >= 0
            ? raw
            : throw new InvalidOperationException(
                $"a RANGE frame offset must not be negative; it is {raw}.");
    }
}
