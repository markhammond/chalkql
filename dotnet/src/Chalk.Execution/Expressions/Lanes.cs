using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apache.Arrow;
using Chalk.Execution.Vectors;
using Chalk.Sources;

namespace Chalk.Execution.Expressions;

/// <summary>
/// One operand of a kernel, read the same way whether it is an array or a broadcast constant (§6.4).
/// Collapsing the array/array, array/scalar and scalar/array cases into one indexer keeps each kernel
/// a single loop instead of three.
/// </summary>
internal readonly ref struct Lanes<T>
    where T : unmanaged
{
    private readonly ReadOnlySpan<T> _span;
    private readonly ReadOnlySpan<byte> _validity;
    private readonly int _offset;
    private readonly T _constant;

    private Lanes(ReadOnlySpan<T> span, ReadOnlySpan<byte> validity, int offset, T constant, bool scalar, bool isNull)
    {
        _span = span;
        _validity = validity;
        _offset = offset;
        _constant = constant;
        IsScalar = scalar;
        ScalarIsNull = isNull;
    }

    public bool IsScalar { get; }

    public bool ScalarIsNull { get; }

    /// <summary>True when no row of this operand is NULL, so a kernel can skip the validity work.</summary>
    public bool AllValid => IsScalar ? !ScalarIsNull : _validity.IsEmpty;

    public T this[int row] => IsScalar ? _constant : _span[row];

    public bool IsValid(int row) => IsScalar
        ? !ScalarIsNull
        : _validity.IsEmpty || BitUtility.GetBit(_validity, _offset + row);

    public static Lanes<T> From(in Vector vector) => vector.IsScalar
        ? new Lanes<T>(
            default,
            default,
            0,
            vector.Scalar.IsNull ? default : vector.Scalar.Read<T>(),
            scalar: true,
            vector.Scalar.IsNull)
        : new Lanes<T>(
            vector.View.Lanes<T>(),
            vector.View.ValidityBits(),
            vector.View.Offset,
            default,
            scalar: false,
            isNull: false);
}

/// <summary>An operand whose lanes are a fixed number of opaque bytes: DECIMAL and UUID.</summary>
internal readonly ref struct RawLanes
{
    private readonly ReadOnlySpan<byte> _span;
    private readonly ReadOnlySpan<byte> _validity;
    private readonly int _offset;
    private readonly int _width;

    private RawLanes(
        ReadOnlySpan<byte> span, ReadOnlySpan<byte> validity, int offset, int width, bool scalar, bool isNull)
    {
        _span = span;
        _validity = validity;
        _offset = offset;
        _width = width;
        IsScalar = scalar;
        ScalarIsNull = isNull;
    }

    public bool IsScalar { get; }

    public bool ScalarIsNull { get; }

    public bool AllValid => IsScalar ? !ScalarIsNull : _validity.IsEmpty;

    public ReadOnlySpan<byte> this[int row] => IsScalar ? _span : _span.Slice(row * _width, _width);

    public bool IsValid(int row) => IsScalar
        ? !ScalarIsNull
        : _validity.IsEmpty || BitUtility.GetBit(_validity, _offset + row);

    public static RawLanes From(in Vector vector, int width) => vector.IsScalar
        ? new RawLanes(vector.Scalar.ReadBytes(), default, 0, width, scalar: true, vector.Scalar.IsNull)
        : new RawLanes(
            vector.View.RawLanes(width),
            vector.View.ValidityBits(),
            vector.View.Offset,
            width,
            scalar: false,
            isNull: false);
}

/// <summary>
/// An operand of variable-length bytes: STRING (UTF-8) and BINARY.
///
/// Supports both classic Arrow-style offsets + payload and Chalk's
/// STRING_VIEW physical representation.
/// </summary>
internal readonly ref struct VarLanes
{
    private const int StringViewWidth = 16;
    private const int StringViewInlineLength = 12;

    //
    // Classic representation.
    //
    private readonly ReadOnlySpan<int> _offsets;
    private readonly ReadOnlySpan<byte> _data;

    //
    // STRING_VIEW representation.
    //
    private readonly ReadOnlySpan<byte> _views;
    private readonly ReadOnlySpan<ReadOnlyMemory<byte>> _viewBuffers;
    private readonly bool _isView;

    private readonly ReadOnlySpan<byte> _validity;
    private readonly int _offset;

    private VarLanes(
        ReadOnlySpan<int> offsets,
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> views,
        ReadOnlySpan<ReadOnlyMemory<byte>> viewBuffers,
        ReadOnlySpan<byte> validity,
        int offset,
        bool isView,
        bool scalar,
        bool isNull)
    {
        _offsets = offsets;
        _data = data;

        _views = views;
        _viewBuffers = viewBuffers;
        _isView = isView;

        _validity = validity;
        _offset = offset;

        IsScalar = scalar;
        ScalarIsNull = isNull;
    }

    public bool IsScalar { get; }

    public bool ScalarIsNull { get; }

    public bool AllValid =>
        IsScalar
            ? !ScalarIsNull
            : _validity.IsEmpty;

    public ReadOnlySpan<byte> this[int row]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (IsScalar)
                return _data;

            if (!_isView)
            {
                var start =
                    _offsets[row];

                return _data.Slice(
                    start,
                    _offsets[row + 1] - start);
            }

            var lane =
                _views.Slice(
                    row * StringViewWidth,
                    StringViewWidth);

            var length =
                MemoryMarshal.Read<int>(
                    lane);

            if ((uint)length <=
                StringViewInlineLength)
            {
                return lane.Slice(
                    sizeof(int),
                    length);
            }

            var bufferIndex =
                MemoryMarshal.Read<int>(
                    lane.Slice(8));

            var bufferOffset =
                MemoryMarshal.Read<int>(
                    lane.Slice(12));

            return _viewBuffers[
                    bufferIndex]
                .Span
                .Slice(
                    bufferOffset,
                    length);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsValid(int row) =>
        IsScalar
            ? !ScalarIsNull
            : _validity.IsEmpty ||
              BitUtility.GetBit(
                  _validity,
                  _offset + row);

    /// <summary>
    /// The scalar form needs the constant's UTF-8 bytes,
    /// which the caller encodes once.
    /// </summary>
    public static VarLanes FromScalar(
        ReadOnlySpan<byte> bytes,
        bool isNull) =>
        new(
            offsets: default,
            data: bytes,
            views: default,
            viewBuffers: default,
            validity: default,
            offset: 0,
            isView: false,
            scalar: true,
            isNull);

    public static VarLanes FromView(
        ColumnView view)
    {
        var validity =
            view.ValidityBits();

        if (view.IsUtf8View)
        {
            var buffers =
                view.ViewBuffers
                ?? throw new InvalidOperationException(
                    "STRING_VIEW has no backing-buffer registry.");

            return new VarLanes(
                offsets: default,
                data: default,

                //
                // RawLanes() rebases the descriptor span to
                // ColumnView.Offset once here, outside the kernel loop.
                //
                views: view.RawLanes(
                    StringViewWidth),

                viewBuffers:
                    buffers.AsSpan(
                        0,
                        view.ViewBufferCount),

                validity,
                view.Offset,
                isView: true,
                scalar: false,
                isNull: false);
        }

        return new VarLanes(
            offsets:
                view.OffsetLanes(),

            data:
                view.VarData(),

            views: default,
            viewBuffers: default,

            validity,
            view.Offset,
            isView: false,
            scalar: false,
            isNull: false);
    }
}
