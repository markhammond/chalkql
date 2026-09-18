using Apache.Arrow;
using Apache.Arrow.Types;

namespace Chalk.Sources.Poco;

/// <summary>
/// One encoder per <see cref="PocoStorageKind"/>. Split out of <c>PocoTableBuilder</c> because a
/// <c>LIST</c> column builds a second column for its elements (D58): the child array of a list is
/// exactly a column of the element type over the flattened elements, so it is built by this same
/// table rather than by a parallel one that could drift from it.
/// </summary>
internal static class PocoColumnFactory
{
    public static PocoColumn<T> Create<T>(
        string name, PocoColumnPlan plan, PocoValueBinding binding, string sourceId, string table)
    {
        var arrow = ArrowTypeMapping.ToArrow(plan.Type);
        var nullable = plan.Type.Nullable;

        PocoChunkLoopSet<T, TStorage> Loops<TStorage>() =>
            PocoChunkCompiler.Compile<T, TStorage>(binding, nullable, sourceId, table, name);

        return plan.Storage switch
        {
            PocoStorageKind.Bool =>
                new PocoBooleanColumn<T>(name, plan.Type, binding, Loops<byte>()),
            PocoStorageKind.Int8 =>
                new PocoFixedWidthColumn<T, sbyte>(
                    name, plan.Type, binding, Loops<sbyte>(),
                    static (v, n, len, nc) => new Int8Array(v, n, len, nc, 0)),
            PocoStorageKind.Int16 =>
                new PocoFixedWidthColumn<T, short>(
                    name, plan.Type, binding, Loops<short>(),
                    static (v, n, len, nc) => new Int16Array(v, n, len, nc, 0)),
            PocoStorageKind.Int32 =>
                new PocoFixedWidthColumn<T, int>(
                    name, plan.Type, binding, Loops<int>(),
                    static (v, n, len, nc) => new Int32Array(v, n, len, nc, 0)),
            PocoStorageKind.Int64 =>
                new PocoFixedWidthColumn<T, long>(
                    name, plan.Type, binding, Loops<long>(),
                    static (v, n, len, nc) => new Int64Array(v, n, len, nc, 0)),
            PocoStorageKind.Float32 =>
                new PocoFixedWidthColumn<T, float>(
                    name, plan.Type, binding, Loops<float>(),
                    static (v, n, len, nc) => new FloatArray(v, n, len, nc, 0)),
            PocoStorageKind.Float64 =>
                new PocoFixedWidthColumn<T, double>(
                    name, plan.Type, binding, Loops<double>(),
                    static (v, n, len, nc) => new DoubleArray(v, n, len, nc, 0)),
            PocoStorageKind.Date32 =>
                new PocoFixedWidthColumn<T, int>(
                    name, plan.Type, binding, Loops<int>(),
                    static (v, n, len, nc) => new Date32Array(v, n, len, nc, 0)),
            PocoStorageKind.Time64 =>
                new PocoFixedWidthColumn<T, long>(
                    name, plan.Type, binding, Loops<long>(),
                    Time64Factory((Time64Type)arrow)),
            PocoStorageKind.Timestamp =>
                new PocoFixedWidthColumn<T, long>(
                    name, plan.Type, binding, Loops<long>(),
                    TimestampFactory((TimestampType)arrow)),
            PocoStorageKind.Duration =>
                new PocoFixedWidthColumn<T, long>(
                    name, plan.Type, binding, Loops<long>(),
                    DurationFactory((DurationType)arrow)),
            PocoStorageKind.Decimal =>
                new PocoDecimalColumn<T>(name, plan.Type, binding, Loops<decimal>(), sourceId, table),
            PocoStorageKind.String =>
                new PocoStringColumn<T>(name, plan.Type, binding, Loops<string>()),
            PocoStorageKind.Utf8 =>
                new PocoUtf8Column<T>(name, plan.Type, binding, Loops<Utf8String>(), table),
            PocoStorageKind.Binary =>
                new PocoBinaryColumn<T>(name, plan.Type, binding, Loops<ReadOnlyMemory<byte>>()),
            PocoStorageKind.Uuid =>
                new PocoUuidColumn<T>(name, plan.Type, binding, Loops<Guid>()),
            PocoStorageKind.List => CreateList<T>(name, plan, binding, sourceId, table),
            _ => throw new UnsupportedFeatureException(
                $"POCO storage {plan.Storage}", "There is no encoder for it."),
        };
    }

    /// <summary>
    /// A LIST column, and the column its elements are built by (D58). The element column is created
    /// through this same method over an identity binding, so a list of DECIMALs encodes its elements
    /// exactly as a DECIMAL column does.
    /// </summary>
    private static PocoColumn<T> CreateList<T>(
        string name, PocoColumnPlan plan, PocoValueBinding binding, string sourceId, string table)
    {
        var elementClrType =
            plan.ElementClrType
            ?? throw new UnsupportedFeatureException(
                $"POCO list column '{name}'", "The plan carries no element CLR type.");
        var elementPlan =
            plan.Element
            ?? throw new UnsupportedFeatureException(
                $"POCO list column '{name}'", "The plan carries no element plan.");

        return (PocoColumn<T>)
            typeof(PocoColumnFactory)
                .GetMethod(nameof(CreateListOf), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(typeof(T), elementClrType)
                .Invoke(null, [name, plan, elementPlan, binding, sourceId, table])!;
    }

    private static PocoColumn<TRow> CreateListOf<TRow, TElement>(
        string name,
        PocoColumnPlan plan,
        PocoColumnPlan elementPlan,
        PocoValueBinding binding,
        string sourceId,
        string table)
    {
        var elementBinding = PocoListColumn<TRow, TElement>.ElementBinding(elementPlan, typeof(TElement));
        var elements =
            Create<TElement>(name + ".item", elementPlan, elementBinding, sourceId, table);
        return new PocoListColumn<TRow, TElement>(
            name,
            plan.Type,
            binding,
            PocoChunkCompiler.Compile<TRow, IReadOnlyList<TElement>>(
                binding, /* nullable= */ true, sourceId, table, name),
            elements);
    }

    private static PocoArrayFactory Time64Factory(Time64Type type) =>
        (v, n, len, nc) => new Time64Array(type, v, n, len, nc, 0);

    private static PocoArrayFactory TimestampFactory(TimestampType type) =>
        (v, n, len, nc) => new TimestampArray(type, v, n, len, nc, 0);

    private static PocoArrayFactory DurationFactory(DurationType type) =>
        (v, n, len, nc) => new DurationArray(type, v, n, len, nc, 0);
}
