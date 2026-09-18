using System.Diagnostics.CodeAnalysis;
using Chalk.Catalog;

namespace Chalk.Sources;

/// <summary>
/// Tier 2 of the public kernel interface (D79,
/// <c>docs/design/17-user-defined-functions.md</c> §3): the expression evaluator's own contract,
/// exposed for a host that needs SIMD or wants to avoid the per-lane delegate call.
/// </summary>
/// <remarks>
/// <para>
/// A kernel is handed one batch's columns and writes one batch's results. It runs on the execution's
/// thread, may keep per-instance scratch, and must not allocate per row — that is the whole reason
/// this tier exists.
/// </para>
/// <para>
/// The engine owns validity either side of the call: it narrows the result to the batch's selected
/// lanes afterwards, and for a <c>STRICT</c> function it also intersects the arguments' validity, so
/// a kernel may compute whatever it likes on a lane whose input is NULL as long as it does not
/// throw. What a kernel writes into <see cref="ColumnWriter.BeginValidity"/> is honoured and then
/// narrowed; a kernel that calls <see cref="ColumnWriter.NoValidity"/> says every lane holds a value.
/// </para>
/// <para>
/// Experimental under <c>CHALK001</c>: this is the engine's internal shape made visible, and it may
/// change until the first tagged release, at which point it either stabilises or stays experimental.
/// Tier 1 — the delegate registry — is implemented on top of this one, so the two cannot drift.
/// </para>
/// </remarks>
[Experimental("CHALK001")]
public interface IVectorFunction
{
    /// <summary>What this kernel implements. Checked against the catalog at engine creation.</summary>
    FunctionSignature Signature { get; }

    /// <summary>Evaluates one batch. <paramref name="args"/> is in declared parameter order.</summary>
    void Invoke(ReadOnlySpan<ColumnView> args, ColumnWriter result, in FunctionContext context);
}

/// <summary>The types a kernel implements, as the catalog declares them.</summary>
[Experimental("CHALK001")]
public sealed class FunctionSignature
{
    /// <summary>The registration key, which is the catalog's <c>ClientBody.registration</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Parameter types in declared order.</summary>
    public required IReadOnlyList<ChalkType> Parameters { get; init; }

    /// <summary>The result type.</summary>
    public required ChalkType ReturnType { get; init; }
}

/// <summary>What a kernel is evaluated against: the batch's size and the execution's clock.</summary>
/// <remarks>
/// The clock is fixed for the whole execution, so two lanes never disagree about "now" — which is
/// also what makes a <c>STABLE</c> function's one value per execution meaningful.
/// </remarks>
[Experimental("CHALK001")]
public readonly struct FunctionContext
{
    /// <summary>Rows in this batch. Every argument view has this length.</summary>
    public required int RowCount { get; init; }

    /// <summary>The execution's clock, evaluated once (D22).</summary>
    public required DateTimeOffset Now { get; init; }

    /// <summary>
    /// The execution's arena, for a kernel that needs scratch of its own. Anything rented must be
    /// returned before the execution ends.
    /// </summary>
    public ExecutionArena? Arena { get; init; }
}

/// <summary>
/// Where a kernel writes its batch: arena-backed buffers in Arrow's physical layout, the twin of
/// <see cref="ColumnView"/> on the way out.
/// </summary>
/// <remarks>
/// Fixed-width results are written through <see cref="Values{T}"/> and an optional validity bitmap;
/// variable-length ones are appended in row order between <see cref="BeginVarLength"/> and the end
/// of the call. A writer belongs to one expression node and is reused batch after batch, so nothing
/// it hands out survives the next <see cref="IVectorFunction.Invoke"/>.
/// </remarks>
[Experimental("CHALK001")]
public sealed class ColumnWriter
{
    private readonly IColumnSink _sink;

    internal ColumnWriter(IColumnSink sink) => _sink = sink;

    /// <summary>The result type, straight from the catalog.</summary>
    public ChalkType Type => _sink.Type;

    /// <summary>
    /// The value lanes for a fixed-width result, sized for <paramref name="length"/> rows.
    /// <typeparamref name="T"/> must be the type's own layout — <c>double</c> for FP64,
    /// <c>long</c> for I64 and the temporal kinds, <c>byte</c> for BOOL.
    /// </summary>
    public Span<T> Values<T>(int length)
        where T : unmanaged => _sink.Values<T>(length);

    /// <summary>
    /// A zeroed validity bitmap to fill in, one bit per row, set meaning "holds a value". Calling
    /// this makes the result nullable; not calling it means every row holds a value.
    /// </summary>
    public Span<byte> BeginValidity(int length) => _sink.BeginValidity(length);

    /// <summary>Declares that every row of this batch holds a value.</summary>
    public void NoValidity() => _sink.NoValidity();

    /// <summary>Starts a variable-length result: append exactly <paramref name="length"/> rows.</summary>
    public void BeginVarLength(int length, bool nullable) => _sink.BeginVarLength(length, nullable);

    /// <summary>Appends one value to a variable-length result.</summary>
    public void AppendValue(ReadOnlySpan<byte> bytes) => _sink.AppendValue(bytes);

    /// <summary>Appends a NULL to a variable-length result.</summary>
    public void AppendNull() => _sink.AppendNull();
}

/// <summary>
/// The engine's side of a <see cref="ColumnWriter"/>. Internal on purpose: a host writes through the
/// writer and never implements one, which is what lets the buffers stay the engine's own.
/// </summary>
internal interface IColumnSink
{
    ChalkType Type { get; }

    Span<T> Values<T>(int length)
        where T : unmanaged;

    Span<byte> BeginValidity(int length);

    void NoValidity();

    void BeginVarLength(int length, bool nullable);

    void AppendValue(ReadOnlySpan<byte> bytes);

    void AppendNull();
}

/// <summary>Builds a <see cref="ColumnWriter"/> over an engine-owned sink.</summary>
internal static class ColumnWriters
{
#pragma warning disable CHALK001
    public static ColumnWriter Over(IColumnSink sink) => new(sink);
#pragma warning restore CHALK001
}
