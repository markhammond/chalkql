using Apache.Arrow;
using Apache.Arrow.Arrays;
using Chalk.Catalog;

namespace Chalk.Sources.Poco.Tests;

/// <summary>Drives a <see cref="PocoSource"/> the way the engine's <c>ScanOperator</c> will (§6.3).</summary>
internal static class PocoTestSupport
{
    public static TableDescriptor Describe(PocoSource source, string table) =>
        source.DescribeSchema().FindTable(table)
        ?? throw new InvalidOperationException($"no table '{table}'");

    public static ScanRequest Request(
        PocoSource source, string table, int batchSize, IReadOnlyList<int>? projection = null)
    {
        var descriptor = Describe(source, table);
        projection ??= Enumerable.Range(0, descriptor.Columns.Count).ToArray();
        return new ScanRequest
        {
            Table = table,
            Projection = projection,
            OutputSchema = ArrowTypeMapping.ToArrowSchema(descriptor, projection),
            BatchSize = batchSize,
        };
    }

    /// <summary>
    /// A scan context over <paramref name="arena"/>, or over a throwaway one. A test that measures
    /// allocation passes the same arena to every scan, because that is what the engine's arena pool
    /// does (08-execution-arena.md §2.1).
    /// </summary>
    public static ScanContext Context(ExecutionStats? stats = null, ExecutionArena? arena = null) => new()
    {
        Stats = stats ?? new ExecutionStats(),
        Arena = arena ?? new ExecutionArena(),
    };

    /// <summary>Runs a whole scan and keeps the batches; the caller disposes them.</summary>
    public static async Task<List<RecordBatch>> ScanAsync(
        PocoSource source,
        string table,
        int batchSize,
        IReadOnlyList<int>? projection = null,
        ExecutionStats? stats = null,
        ExecutionArena? arena = null)
    {
        var batches = new List<RecordBatch>();
        var request = Request(source, table, batchSize, projection);
        await foreach (var batch in source.ScanAsync(
            request, Context(stats, arena), TestContext.Current.CancellationToken))
        {
            batches.Add(batch);
        }

        return batches;
    }

    /// <summary>One column of a whole scan, flattened, as CLR values with null for a NULL cell.</summary>
    public static async Task<List<object?>> ColumnAsync(
        PocoSource source,
        string table,
        int column,
        int batchSize,
        IReadOnlyList<int>? projection = null)
    {
        var batches = await ScanAsync(source, table, batchSize, projection);
        try
        {
            var values = new List<object?>();
            foreach (var batch in batches)
            {
                var array = batch.Column(column);
                for (var i = 0; i < array.Length; i++)
                {
                    values.Add(Read(array, i));
                }
            }

            return values;
        }
        finally
        {
            Dispose(batches);
        }
    }

    /// <summary>A LIST cell, as a list of its element values (D58).</summary>
    private static List<object?> ReadList(ListArray array, int index)
    {
        var values = array.Values;
        var start = array.ValueOffsets[index];
        var end = array.ValueOffsets[index + 1];
        var elements = new List<object?>(end - start);
        for (var i = start; i < end; i++)
        {
            elements.Add(Read(values, i));
        }

        return elements;
    }

    public static void Dispose(IEnumerable<RecordBatch> batches)
    {
        foreach (var batch in batches)
        {
            batch.Dispose();
        }
    }

    /// <summary>
    /// Reads one cell out of an Arrow array. Deliberately not the engine's future
    /// <c>RecordBatchExtensions.ToRows()</c> (§7.2): a test that shares the production converter would
    /// hide a mistake the two of them agreed on.
    /// </summary>
    public static object? Read(IArrowArray array, int index) => array switch
    {
        BooleanArray a => a.IsNull(index) ? null : a.GetValue(index),
        Int8Array a => a.IsNull(index) ? null : a.GetValue(index),
        Int16Array a => a.IsNull(index) ? null : a.GetValue(index),
        Int32Array a => a.IsNull(index) ? null : a.GetValue(index),
        Int64Array a => a.IsNull(index) ? null : a.GetValue(index),
        FloatArray a => a.IsNull(index) ? null : a.GetValue(index),
        DoubleArray a => a.IsNull(index) ? null : a.GetValue(index),
        Decimal128Array a => a.IsNull(index) ? null : a.GetValue(index),
        StringArray a => a.IsNull(index) ? null : a.GetString(index),
        Date32Array a => a.IsNull(index) ? null : a.GetValue(index),
        Time64Array a => a.IsNull(index) ? null : a.GetValue(index),
        TimestampArray a => a.IsNull(index) ? null : a.GetValue(index),
        DurationArray a => a.IsNull(index) ? null : a.GetValue(index),
        FixedSizeBinaryArray a => a.IsNull(index) ? null : new Guid(a.GetBytes(index), bigEndian: true),
        BinaryArray a => a.IsNull(index) ? null : a.GetBytes(index).ToArray(),
        ListArray a => a.IsNull(index) ? null : ReadList(a, index),
        _ => throw new InvalidOperationException($"unhandled array {array.GetType().Name}"),
    };
}
