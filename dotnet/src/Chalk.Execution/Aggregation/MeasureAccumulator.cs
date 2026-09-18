using System.Runtime.InteropServices;
using Chalk.Catalog;
using Chalk.Execution.Expressions;
using Chalk.Execution.Numeric;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution.Aggregation;

/// <summary>
/// One measure's state, indexed by dense group id (§6.6). Everything arrives as a raw lane, so the
/// accumulator never has to know which Arrow array the value came from.
/// </summary>
/// <remarks>
/// The state scales with the number of groups, so it is rented from the execution's arena between
/// <c>Begin</c> and <c>Release</c> rather than kept by the operator (ADR 0012). A run therefore always
/// starts from nothing, which is also why there is no separate reset step to get wrong.
/// </remarks>
internal abstract class MeasureAccumulator : ArenaScratch
{
    protected MeasureAccumulator(ChalkType resultType)
    {
        ResultType = resultType;
        ResultKind = ColumnKinds.Of(resultType);
        ResultWidth = ColumnKinds.Width(ResultKind);
    }

    protected ChalkType ResultType { get; }

    protected ColumnKind ResultKind { get; }

    protected int ResultWidth { get; }

    /// <summary>Grows the state to hold at least <paramref name="groups"/> groups.</summary>
    public abstract void EnsureCapacity(int groups);

    /// <summary>Folds one row's argument in. <paramref name="valid"/> is false for a NULL argument.</summary>
    public abstract void Add(int group, ReadOnlySpan<byte> lane, bool valid);

    /// <summary>
    /// Folds a whole batch in one typed loop, scattering into the group state by row group (D255).
    /// False means this accumulator has no kernel for the argument's layout and the operator's
    /// per-row fold runs instead, so a measure never loses a feature by not being covered here.
    /// </summary>
    /// <remarks>
    /// The operator only offers a batch to a measure that is neither <c>DISTINCT</c> nor ordered; a
    /// kernel therefore only has to be <see cref="Add"/> over a batch, and has to be exactly that.
    /// </remarks>
    /// <param name="batch">The rows, their groups, the filter mask and the argument's validity.</param>
    /// <param name="argument">The argument column, or the default vector for <c>COUNT(*)</c>.</param>
    /// <param name="hasArgument">Whether the measure has an argument at all.</param>
    public virtual bool TryAddBatch(in GroupedBatch batch, in Vector argument, bool hasArgument) =>
        false;

    /// <summary>
    /// Whether this aggregate's answer depends on an order, and the operator therefore has to read
    /// the {@code WITHIN GROUP (ORDER BY …)} key per row (D57). False for everything else, which is
    /// why <see cref="Add"/> is the interface the hot path uses.
    /// </summary>
    public virtual bool NeedsOrderKey => false;

    /// <summary>Folds one row in, with the key it is ordered by. Only the ordered holistics take one.</summary>
    public virtual void AddOrdered(
        int group, ReadOnlySpan<byte> lane, bool valid, ReadOnlySpan<byte> key, bool keyValid) =>
        Add(group, lane, valid);

    /// <summary>Writes the group's result, which for an empty group is what §4 says it is.</summary>
    public abstract void Emit(ColumnCopier copier, int group);

    /// <summary>Builds the accumulator a measure asks for, or refuses the ones the engine has not got.</summary>
    /// <param name="measure">The IR measure.</param>
    /// <param name="resultType">The measure's result type.</param>
    /// <param name="argumentType">
    /// The type of the value being aggregated: the measure's first argument, except for a percentile,
    /// whose value is its {@code WITHIN GROUP} expression rather than its argument (D57).
    /// </param>
    /// <param name="keyType">The order key's type, or null when the aggregate has no ordering.</param>
    /// <param name="descending">Whether that ordering is descending.</param>
    public static MeasureAccumulator Create(
        Measure measure,
        ChalkType resultType,
        ChalkType? argumentType,
        ChalkType? keyType = null,
        bool descending = false)
    {
        switch (measure.Function)
        {
            case AggregateFunctionId.PercentileCont:
            case AggregateFunctionId.PercentileDisc:
                return new PercentileAccumulator(
                    resultType,
                    argumentType ?? resultType,
                    Fraction(measure),
                    measure.Function == AggregateFunctionId.PercentileCont,
                    descending);
            case AggregateFunctionId.Listagg:
            case AggregateFunctionId.StringAgg:
                return new ListAggAccumulator(
                    resultType, argumentType ?? resultType, keyType, Separator(measure), descending);
            case AggregateFunctionId.ArrayAgg:
                return new ArrayAggAccumulator(
                    resultType,
                    argumentType ?? resultType.Element ?? resultType,
                    keyType,
                    descending);
            case AggregateFunctionId.Mode:
                return new ModeAccumulator(resultType, argumentType ?? resultType);
            case AggregateFunctionId.Count:
                return new CountAccumulator(resultType, measure.Args.Count > 0);
            case AggregateFunctionId.Sum:
            case AggregateFunctionId.Sum0:
                return new SumAccumulator(
                    resultType,
                    argumentType ?? resultType,
                    zeroWhenEmpty: measure.Function == AggregateFunctionId.Sum0);
            case AggregateFunctionId.Min:
            case AggregateFunctionId.Max:
                return new MinMaxAccumulator(resultType, measure.Function == AggregateFunctionId.Max);
            case AggregateFunctionId.Avg:
                throw new UnsupportedFeatureException(
                    "AVG",
                    "The reference planner reduces AVG to SUM0/COUNT and M1 executors reject it (A12).");
            default:
                throw new UnsupportedFeatureException(
                    measure.Function.ToString().ToUpperInvariant(),
                    "The engine implements COUNT, SUM, SUM0, MIN, MAX and the holistic family "
                    + "(docs/design/02-ir.md §6, docs/design/14-windows-ii.md §3).");
        }
    }

    /// <summary>A percentile's fraction: a constant in [0, 1], which Calcite has already checked.</summary>
    private static double Fraction(Measure measure)
    {
        var literal = measure.Args.Count > 0 ? measure.Args[0] : null;
        if (literal is not { KindCase: Expr.KindOneofCase.Literal })
        {
            throw new UnsupportedFeatureException(
                measure.Function.ToString().ToUpperInvariant() + " with a non-constant fraction",
                "The fraction is a literal between 0 and 1 (docs/design/14-windows-ii.md §3).");
        }

        var value = Expressions.Literals.ToScalar(literal);
        var fraction = value.Type.Kind switch
        {
            TypeKind.Fp32 => value.Single,
            TypeKind.Fp64 => value.Double,
            TypeKind.Decimal => (double)Numeric.Decimals.Read(value.ReadBytes(), value.Type.Scale),
            _ => value.Integer,
        };

        if (fraction is < 0 or > 1)
        {
            throw new UnsupportedFeatureException(
                measure.Function.ToString().ToUpperInvariant() + $" of {fraction}",
                "The fraction must be between 0 and 1 (docs/design/14-windows-ii.md §3).");
        }

        return fraction;
    }

    /// <summary>LISTAGG's separator, which the planner always emits as a literal second argument.</summary>
    private static string Separator(Measure measure)
    {
        if (measure.Args.Count < 2)
        {
            return string.Empty;
        }

        var literal = measure.Args[1];
        if (literal is not { KindCase: Expr.KindOneofCase.Literal })
        {
            throw new UnsupportedFeatureException(
                "LISTAGG with a non-constant separator",
                "The separator is a literal (docs/design/14-windows-ii.md §3).");
        }

        return Expressions.Literals.ToScalar(literal).Text ?? string.Empty;
    }
}

/// <summary>COUNT: never NULL, zero over an empty group.</summary>
internal sealed class CountAccumulator : MeasureAccumulator
{
    private readonly bool _hasArgument;
    private long[] _counts = [];

    public CountAccumulator(ChalkType resultType, bool hasArgument)
        : base(resultType) => _hasArgument = hasArgument;

    public override void EnsureCapacity(int groups)
    {
        if (_counts.Length < groups)
        {
            _counts = Grow(_counts, groups);
        }
    }

    protected override void ReleaseCore() => Give(ref _counts);

    public override void Add(int group, ReadOnlySpan<byte> lane, bool valid)
    {
        if (!_hasArgument || valid)
        {
            _counts[group]++;
        }
    }

    /// <summary>
    /// The counting kernel (D255). <c>COUNT(*)</c> never reads the argument column at all;
    /// <c>COUNT(col)</c> reads only its validity, which the batch already carries.
    /// </summary>
    public override bool TryAddBatch(in GroupedBatch batch, in Vector argument, bool hasArgument)
    {
        if (hasArgument != _hasArgument || (hasArgument && argument.IsScalar))
        {
            return false;
        }

        AccumulateKernels.Count(batch, _counts);
        return true;
    }

    public override void Emit(ColumnCopier copier, int group)
    {
        Span<byte> lane = stackalloc byte[ResultWidth];
        NumberLanes.WriteInteger(ResultKind, ResultType, _counts[group], lane);
        copier.AppendRaw(lane, valid: true);
    }
}

/// <summary>
/// SUM and SUM0. Exact kinds accumulate checked, so an overflow raises rather than wraps; floating
/// point is summed sequentially in input order with no compensation (A6), which is exactly what the
/// reference executor does so the two can be compared within 4 ULPs.
/// </summary>
internal sealed class SumAccumulator : MeasureAccumulator
{
    private readonly ColumnKind _argumentKind;
    private readonly int _argumentScale;
    private readonly bool _zeroWhenEmpty;
    private long[] _integers = [];
    private double[] _doubles = [];
    private float[] _singles = [];
    private decimal[] _decimals = [];
    private bool[] _seen = [];

    public SumAccumulator(ChalkType resultType, ChalkType argumentType, bool zeroWhenEmpty)
        : base(resultType)
    {
        _argumentKind = ColumnKinds.Of(argumentType);
        _argumentScale = argumentType.Scale;
        _zeroWhenEmpty = zeroWhenEmpty;
        if (!IrTypes.IsNumeric(resultType.Kind))
        {
            throw new UnsupportedFeatureException(
                $"SUM over {resultType}",
                "SUM is defined for the numeric kinds (docs/design/02-ir.md §6).");
        }
    }

    public override void EnsureCapacity(int groups)
    {
        if (_seen.Length >= groups)
        {
            return;
        }

        _seen = Grow(_seen, groups);
        var size = _seen.Length;
        switch (ResultKind)
        {
            case ColumnKind.Float:
                _singles = Grow(_singles, size);
                break;
            case ColumnKind.Double:
                _doubles = Grow(_doubles, size);
                break;
            case ColumnKind.Decimal128:
                _decimals = Grow(_decimals, size);
                break;
            default:
                _integers = Grow(_integers, size);
                break;
        }
    }

    protected override void ReleaseCore()
    {
        Give(ref _seen);
        Give(ref _integers);
        Give(ref _doubles);
        Give(ref _singles);
        Give(ref _decimals);
    }

    public override void Add(int group, ReadOnlySpan<byte> lane, bool valid)
    {
        if (!valid)
        {
            return;
        }

        _seen[group] = true;
        switch (ResultKind)
        {
            case ColumnKind.Float:
                _singles[group] += (float)ReadDouble(lane);
                break;
            case ColumnKind.Double:
                _doubles[group] += ReadDouble(lane);
                break;
            case ColumnKind.Decimal128:
                _decimals[group] += ReadDecimal(lane);
                break;
            default:
                _integers[group] = checked(_integers[group] + ReadInteger(lane));
                break;
        }
    }

    /// <summary>
    /// The summing kernels (D255): an exact integer state from an <c>I32</c> or <c>I64</c> lane, a
    /// double state from those or from <c>FP64</c>, and a decimal state from a DECIMAL lane of the
    /// same scale. An <c>FP32</c> state, and any argument that needs a checked narrowing cast on the
    /// way in, stay on the generic path.
    /// </summary>
    public override bool TryAddBatch(in GroupedBatch batch, in Vector argument, bool hasArgument)
    {
        if (!hasArgument || argument.IsScalar)
        {
            return false;
        }

        var view = argument.View;
        switch (ResultKind)
        {
            case ColumnKind.Double:
                switch (_argumentKind)
                {
                    case ColumnKind.Double:
                        AccumulateKernels.SumDouble(batch, view.Lanes<double>(), _doubles, _seen);
                        return true;
                    case ColumnKind.Int64:
                        AccumulateKernels.SumDoubleFromInt64(batch, view.Lanes<long>(), _doubles, _seen);
                        return true;
                    case ColumnKind.Int32:
                        AccumulateKernels.SumDoubleFromInt32(batch, view.Lanes<int>(), _doubles, _seen);
                        return true;
                    default:
                        return false;
                }

            case ColumnKind.Decimal128:
                if (_argumentKind != ColumnKind.Decimal128)
                {
                    return false;
                }

                AccumulateKernels.SumDecimal(
                    batch, view.RawLanes(16), _decimals, _seen, _argumentScale);
                return true;

            case ColumnKind.Float:
                return false;

            default:
                switch (_argumentKind)
                {
                    case ColumnKind.Int64:
                        AccumulateKernels.SumInt64(batch, view.Lanes<long>(), _integers, _seen);
                        return true;
                    case ColumnKind.Int32:
                        AccumulateKernels.SumInt32(batch, view.Lanes<int>(), _integers, _seen);
                        return true;
                    default:
                        return false;
                }
        }
    }

    public override void Emit(ColumnCopier copier, int group)
    {
        Span<byte> lane = stackalloc byte[ResultWidth];
        if (!_seen[group] && !_zeroWhenEmpty)
        {
            copier.AppendRaw(lane, valid: false);
            return;
        }

        switch (ResultKind)
        {
            case ColumnKind.Float:
                MemoryMarshal.Write(lane, _seen[group] ? _singles[group] : 0f);
                break;
            case ColumnKind.Double:
                MemoryMarshal.Write(lane, _seen[group] ? _doubles[group] : 0d);
                break;
            case ColumnKind.Decimal128:
                Decimals.Write(
                    lane,
                    _seen[group] ? _decimals[group] : decimal.Zero,
                    ResultType.Precision,
                    ResultType.Scale);
                break;
            default:
                NumberLanes.WriteInteger(ResultKind, ResultType, _seen[group] ? _integers[group] : 0L, lane);
                break;
        }

        copier.AppendRaw(lane, valid: true);
    }

    private long ReadInteger(ReadOnlySpan<byte> lane) => _argumentKind switch
    {
        ColumnKind.Int8 => (sbyte)lane[0],
        ColumnKind.Int16 => MemoryMarshal.Read<short>(lane),
        ColumnKind.Int32 => MemoryMarshal.Read<int>(lane),
        ColumnKind.Int64 => MemoryMarshal.Read<long>(lane),
        ColumnKind.Float => checked((long)MemoryMarshal.Read<float>(lane)),
        ColumnKind.Double => checked((long)MemoryMarshal.Read<double>(lane)),
        _ => checked((long)Decimals.Read(lane, _argumentScale)),
    };

    private double ReadDouble(ReadOnlySpan<byte> lane) => _argumentKind switch
    {
        ColumnKind.Float => MemoryMarshal.Read<float>(lane),
        ColumnKind.Double => MemoryMarshal.Read<double>(lane),
        ColumnKind.Decimal128 => (double)Decimals.Read(lane, _argumentScale),
        _ => ReadInteger(lane),
    };

    private decimal ReadDecimal(ReadOnlySpan<byte> lane) => _argumentKind switch
    {
        ColumnKind.Decimal128 => Decimals.Read(lane, _argumentScale),
        ColumnKind.Float => (decimal)MemoryMarshal.Read<float>(lane),
        ColumnKind.Double => (decimal)MemoryMarshal.Read<double>(lane),
        _ => ReadInteger(lane),
    };
}

/// <summary>
/// MIN and MAX: the extreme lane seen, kept as bytes. NULL over an empty or all-NULL group, per §4.
/// </summary>
internal sealed class MinMaxAccumulator : MeasureAccumulator
{
    private readonly bool _max;
    private readonly bool _variableLength;
    private byte[] _values = [];
    private int[] _starts = [];
    private int[] _lengths = [];
    private bool[] _seen = [];
    private int _used;

    public MinMaxAccumulator(ChalkType resultType, bool max)
        : base(resultType)
    {
        _max = max;
        _variableLength = ColumnKinds.IsVariableLength(ResultKind);
    }

    public override void EnsureCapacity(int groups)
    {
        if (_seen.Length >= groups)
        {
            return;
        }

        _seen = Grow(_seen, groups);
        var size = _seen.Length;
        if (_variableLength)
        {
            _starts = Grow(_starts, size);
            _lengths = Grow(_lengths, size);
        }
        else if (_values.Length < size * ResultWidth)
        {
            _values = Grow(_values, size * ResultWidth);
        }
    }

    protected override void ReleaseCore()
    {
        Give(ref _seen);
        Give(ref _starts);
        Give(ref _lengths);
        Give(ref _values);
        _used = 0;
    }

    public override void Add(int group, ReadOnlySpan<byte> lane, bool valid)
    {
        if (!valid)
        {
            return;
        }

        if (!_seen[group])
        {
            _seen[group] = true;
            Store(group, lane);
            return;
        }

        var comparison = LaneComparer.Compare(ResultKind, lane, Stored(group));
        if (_max ? comparison > 0 : comparison < 0)
        {
            Store(group, lane);
        }
    }

    /// <summary>
    /// The extremum kernels (D255), over the four fixed-width layouts the design names. A
    /// variable-length winner is stored by appending, which is not a scatter, so STRING and BINARY
    /// stay on the generic path — as do <c>FP32</c>, UUID and <c>BOOL</c>.
    /// </summary>
    public override bool TryAddBatch(in GroupedBatch batch, in Vector argument, bool hasArgument)
    {
        if (!hasArgument || argument.IsScalar || _variableLength)
        {
            return false;
        }

        var view = argument.View;
        switch (ResultKind)
        {
            case ColumnKind.Int64:
                AccumulateKernels.MinMaxInt64(
                    batch, view.Lanes<long>(), MemoryMarshal.Cast<byte, long>(_values.AsSpan()), _seen, _max);
                return true;
            case ColumnKind.Int32:
                AccumulateKernels.MinMaxInt32(
                    batch, view.Lanes<int>(), MemoryMarshal.Cast<byte, int>(_values.AsSpan()), _seen, _max);
                return true;
            case ColumnKind.Double:
                AccumulateKernels.MinMaxDouble(
                    batch, view.Lanes<double>(), MemoryMarshal.Cast<byte, double>(_values.AsSpan()), _seen, _max);
                return true;
            case ColumnKind.Decimal128:
                AccumulateKernels.MinMaxDecimal(batch, view.RawLanes(16), _values, _seen, _max);
                return true;
            default:
                return false;
        }
    }

    public override void Emit(ColumnCopier copier, int group)
    {
        if (!_seen[group])
        {
            Span<byte> empty = stackalloc byte[_variableLength ? 0 : ResultWidth];
            copier.AppendRaw(empty, valid: false);
            return;
        }

        copier.AppendRaw(Stored(group), valid: true);
    }

    private ReadOnlySpan<byte> Stored(int group) => _variableLength
        ? _values.AsSpan(_starts[group], _lengths[group])
        : _values.AsSpan(group * ResultWidth, ResultWidth);

    /// <summary>
    /// A variable-length winner is appended rather than overwritten: the old bytes become garbage in
    /// the buffer, which is bounded by the number of improvements, not by the number of rows.
    /// </summary>
    private void Store(int group, ReadOnlySpan<byte> lane)
    {
        if (!_variableLength)
        {
            var slot = _values.AsSpan(group * ResultWidth, ResultWidth);
            slot.Clear();
            lane[..ResultWidth].CopyTo(slot);
            return;
        }

        if (_values.Length < _used + lane.Length)
        {
            _values = Grow(_values, _used + lane.Length);
        }

        lane.CopyTo(_values.AsSpan(_used));

        // Each group owns an independent window, so a new winner appends without disturbing anyone
        // else. The abandoned bytes are bounded by the number of improvements, not by the row count.
        _starts[group] = _used;
        _lengths[group] = lane.Length;
        _used += lane.Length;
    }
}
