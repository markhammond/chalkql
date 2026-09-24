using System.Diagnostics.CodeAnalysis;
using System.Text;
using Apache.Arrow;
using Chalk.Arrow;
using Chalk.Client;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// Reading a STRING column as bytes — the convention: <c>GetUtf8</c> gives a
/// <c>ReadOnlySpan&lt;byte&gt;</c> over the batch's own buffer for every layout, no copy and no
/// decode, and a host's dictionary answers it through <c>Utf8StringComparer</c> without a string
/// being made.
/// </summary>
[Collection(SidecarCollection.Name)]
[Experimental("CHALK001")]
public sealed class Utf8SpanReadTests(SharedSidecar sidecar)
{
    private static readonly string[] Values = ["ADA", "日経", "A_VERY_LONG_SYMBOL_NAME", "", "naïve"];

    [Fact]
    public void The_classic_layout_reads_as_bytes()
    {
        var array = Classic(Values, nullAt: 3);
        AssertReads(array);
    }

    [Fact]
    public void The_view_layout_reads_as_bytes_inline_and_variadic_alike()
    {
        var array = View(Values, nullAt: 3);
        AssertReads(array);

        // Twelve bytes or fewer live inside the sixteen-byte lane; longer ones in the variadic
        // buffer. Both read the same way, and neither is copied.
        Assert.True("ADA"u8.Length <= 12);
        Assert.True(Encoding.UTF8.GetByteCount(Values[2]) > 12);
    }

    [Fact]
    public void The_large_layout_reads_as_bytes()
    {
        var builder = new LargeStringArray.Builder();
        for (var i = 0; i < Values.Length; i++)
        {
            if (i == 3)
            {
                builder.AppendNull();
            }
            else
            {
                builder.Append(Values[i]);
            }
        }

        AssertReads(builder.Build());
    }

    [Fact]
    public void A_sliced_array_reads_its_own_rows()
    {
        var array = (StringViewArray)View(Values, nullAt: 3).Slice(1, 3);
        Assert.Equal(3, array.Length);
        Assert.True(array.GetUtf8(0).SequenceEqual("日経"u8));
        Assert.True(array.GetUtf8(1).SequenceEqual(Encoding.UTF8.GetBytes(Values[2])));
        Assert.False(array.TryGetUtf8(2, out var none));
        Assert.True(none.IsEmpty);
    }

    [Fact]
    public void A_record_batch_is_read_by_column_and_row()
    {
        var array = Classic(Values, nullAt: 3);
        var schema = new Schema([new Field("s", Apache.Arrow.Types.StringType.Default, nullable: true)], null);
        using var batch = new RecordBatch(schema, [array], array.Length);
        Assert.True(batch.GetUtf8(0, 1).SequenceEqual("日経"u8));
        Assert.True(batch.GetUtf8(0, 3).IsEmpty);
    }

    [Fact]
    public void A_non_string_array_is_refused_by_name()
    {
        var numbers = new Int32Array.Builder().Append(1).Build();
        var refusal = Assert.Throws<ArgumentException>(() => numbers.GetUtf8(0).Length);
        Assert.Contains("UTF-8", refusal.Message);
    }

    [Fact]
    public async Task A_host_reads_every_symbol_and_looks_it_up_without_allocating()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = Utf8Fixture.ContextId,
            Functions = Utf8Fixture.Register,
            Sources = Utf8Fixture.Shared.Sources,
            Planner = sidecar.Sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { BatchSize = 512 },
        });

        // A string-keyed dictionary, as a host has, and a Utf8String-keyed one; both answer bytes.
        var byString = new Dictionary<string, int>(Utf8StringComparer.ForString);
        var byUtf8 = new Dictionary<Utf8String, int>(Utf8StringComparer.Ordinal);
        foreach (var (name, i) in Utf8Fixture.Bases.Select((b, i) => (b + "USDT", i)))
        {
            byString[name] = i;
            byUtf8[Utf8String.FromString(name)] = i;
        }

        var query = await engine.PrepareAsync("SELECT symbol FROM utf8_quotes WHERE price > 10");

        // The first run warms the JIT and the engine; the second is the one counted, and only the
        // host's own loop is inside the window — the batch has arrived before it opens.
        await ReadAsync(engine, query, byString, byUtf8);
        var (rows, found, bytes) = await ReadAsync(engine, query, byString, byUtf8);

        Assert.True(rows > 1_000, $"expected more than a thousand rows, read {rows}");
        Assert.Equal(rows, found);
        Assert.Equal(0, bytes);
    }

    private static async Task<(long Rows, long Found, long Bytes)> ReadAsync(
        ChalkEngine engine,
        PreparedQuery query,
        Dictionary<string, int> byString,
        Dictionary<Utf8String, int> byUtf8)
    {
        var strings = byString.GetAlternateLookup<ReadOnlySpan<byte>>();
        var values = byUtf8.GetAlternateLookup<ReadOnlySpan<byte>>();
        long rows = 0, found = 0, bytes = 0;
        await using var execution = await engine.ExecuteAsync(query);
        await foreach (var batch in execution.Batches)
        {
            var symbols = batch.Column(0);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var row = 0; row < batch.Length; row++)
            {
                ReadOnlySpan<byte> symbol = symbols.GetUtf8(row);
                if (strings.TryGetValue(symbol, out var a) && values.TryGetValue(symbol, out var b) && a == b)
                {
                    found++;
                }

                rows++;
            }

            bytes += GC.GetAllocatedBytesForCurrentThread() - before;
            batch.Dispose();
        }

        return (rows, found, bytes);
    }

    private static void AssertReads(IArrowArray array)
    {
        for (var i = 0; i < Values.Length; i++)
        {
            if (i == 3)
            {
                Assert.True(array.IsNullAt(i));
                Assert.True(array.GetUtf8(i).IsEmpty);
                Assert.False(array.TryGetUtf8(i, out _));
                Assert.Null(array.GetUtf8String(i));
                continue;
            }

            var expected = Encoding.UTF8.GetBytes(Values[i]);
            Assert.True(array.GetUtf8(i).SequenceEqual(expected), $"row {i}");
            Assert.True(array.TryGetUtf8(i, out var read));
            Assert.True(read.SequenceEqual(expected));
            Assert.True(array.GetUtf8(i).TextEquals(Values[i]));
            Assert.Equal(Values[i], array.GetUtf8String(i));
        }
    }

    private static StringArray Classic(string[] values, int nullAt)
    {
        var builder = new StringArray.Builder();
        for (var i = 0; i < values.Length; i++)
        {
            if (i == nullAt)
            {
                builder.AppendNull();
            }
            else
            {
                builder.Append(values[i]);
            }
        }

        return builder.Build();
    }

    private static StringViewArray View(string[] values, int nullAt)
    {
        var builder = new StringViewArray.Builder();
        for (var i = 0; i < values.Length; i++)
        {
            if (i == nullAt)
            {
                builder.AppendNull();
            }
            else
            {
                builder.Append(values[i]);
            }
        }

        return builder.Build();
    }
}
