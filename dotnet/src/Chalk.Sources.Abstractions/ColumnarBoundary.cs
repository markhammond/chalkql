using Apache.Arrow;
using Apache.Arrow.Types;
using Chalk.Catalog;

namespace Chalk.Sources;

/// <summary>
/// Where Arrow and the column views meet (<c>docs/design/15-zero-allocation-execution.md</c> §1).
/// Arrow objects exist at exactly two places — a source's input and the host's output — and this is
/// the input half: an Arrow array is wrapped as a view without copying a byte, because
/// <c>ArrowBuffer.Memory</c> is the same memory.
/// </summary>
internal static class ColumnarBoundary
{
    /// <summary>
    /// Wraps one Arrow array as a view of <paramref name="type"/>. No copy: the view borrows the
    /// array's buffers and is valid as long as the array is.
    /// </summary>
    /// <param name="child">
    /// A reusable one-element holder for a LIST's element view, so wrapping a list column costs no
    /// allocation after the first batch. Null for every other kind.
    /// </param>
    public static ColumnView Wrap(IArrowArray array, ChalkType type, ColumnView[]? child = null)
    {
        ArgumentNullException.ThrowIfNull(array);
        var data = array.Data;
        var packed = data.DataType.TypeId == ArrowTypeId.Boolean;

        // D291: one view per field, over the array's child arrays; the struct's offset applies to
        // them as the view's own, as Arrow's does. No source produces one today — a struct comes from
        // a function — so this is the boundary being total rather than a path anything takes.
        if (type.Kind == Ir.TypeKind.Struct)
        {
            var holder = child ?? new ColumnView[type.Fields.Count];
            for (var i = 0; i < holder.Length; i++)
            {
                holder[i] = Wrap(ArrowArrayFactory.BuildArray(data.Children[i]), type.Fields[i].Type);
            }

            return new ColumnView
            {
                Type = type,
                Length = array.Length,
                Validity = Buffer(data, 0),
                NullCount = data.NullCount,
                Offset = array.Offset,
                Children = holder,
            };
        }

        if (type.Kind == Ir.TypeKind.List)
        {
            var elements = data.Children[0];
            var holder = child ?? new ColumnView[1];
            holder[0] = Wrap(
                ArrowArrayFactory.BuildArray(elements),
                type.Element ?? throw new InvalidOperationException("a LIST view needs an element type."));
            return new ColumnView
            {
                Type = type,
                Length = array.Length,
                Offsets = Buffer(data, 1),
                Validity = Buffer(data, 0),
                NullCount = data.NullCount,
                Offset = array.Offset,
                Children = holder,
            };
        }

        var variable = data.Buffers.Length >= 3;
        return new ColumnView
        {
            Type = type,
            Length = array.Length,
            Values = variable ? Buffer(data, 2) : Buffer(data, 1),
            Offsets = variable ? Buffer(data, 1) : default,
            Validity = Buffer(data, 0),
            NullCount = data.NullCount,
            Offset = array.Offset,
            BitPacked = packed,
        };
    }

    private static ReadOnlyMemory<byte> Buffer(ArrayData data, int index) =>
        index < data.Buffers.Length ? data.Buffers[index].Memory : default;
}

/// <summary>
/// A source that can fill an operator's slot directly, with no Arrow object at all
/// (<c>docs/design/15-zero-allocation-execution.md</c> §1, D61). The in-box sources implement it —
/// the POCO source now, ADO.NET in M5 — and everything else keeps the Arrow contract of
/// <see cref="ISourceRuntime"/>, which is public and stays as it is.
/// </summary>
/// <remarks>
/// The implementation writes into buffers it rents from <see cref="ScanContext.Arena"/> and hands
/// back views over them; the caller's slot is valid until the next <see cref="MoveNextAsync"/>, the
/// same contract every operator has.
/// </remarks>
public interface IColumnarScan : IAsyncDisposable
{
    /// <summary>
    /// The next batch, into <paramref name="batch"/>. False when the scan is done. Returns
    /// synchronously whenever the source has rows ready, which the in-box sources always do (D63).
    /// </summary>
    bool TryNext(ColumnarBatch batch);

    /// <summary>
    /// Awaited only when <see cref="TryNext"/> could not answer without waiting. The in-box sources
    /// never reach it; a remote source in M5 does.
    /// </summary>
    ValueTask<bool> WaitAsync(ColumnarBatch batch, CancellationToken ct);
}

/// <summary>A source that can serve <see cref="IColumnarScan"/>s as well as Arrow batches.</summary>
public interface IColumnarBatchSource
{
    /// <summary>A scan of <paramref name="request"/> that fills the caller's slot, or null when this source cannot.</summary>
    IColumnarScan? ColumnarScan(ScanRequest request, ScanContext context);

    /// <summary>An index lookup that fills the caller's slot, or null when this source cannot.</summary>
    IColumnarScan? ColumnarLookup(IndexLookupRequest request, ScanContext context);
}
