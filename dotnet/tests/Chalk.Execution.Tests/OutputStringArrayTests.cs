using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Chalk.Catalog;
using Chalk.Execution.Memory;
using Chalk.Execution.Operators;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using Chalk.TestKit;
using ArrowSchema = Apache.Arrow.Schema;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Execution.Tests;

/// <summary>
/// What the output materialiser actually emits for a STRING column (D244): the declared layout,
/// every time, and no payload copy the declaration does not force.
/// </summary>
/// <remarks>
/// The materialiser is driven directly rather than through a plan, because what is under test is the
/// four cells of one matrix — the arriving layout against the declared one — and two of them need a
/// producer that can hand its own memory over, which is what <c>IManagedColumnPublisher</c> is. The
/// plan-level half of the same contract is <see cref="OutputStringLayoutTests"/>'s and the
/// integration suite's.
/// </remarks>
[Experimental("CHALK001")]
public sealed class OutputStringArrayTests
{
    private static readonly string?[] Values =
    [
        "short",
        "a value that is comfortably longer than twelve bytes",
        null,
        string.Empty,
        "exactly12345",   // 12 bytes: the last length that is inlined
        "thirteen bytes",  // 14 bytes, and "exactly123456" below is 13
        "exactly123456",
        "épée",  // 6 bytes, 4 characters
    ];

    private static readonly ChalkType Text = ChalkType.String(nullable: true);

    // ---- the four cells of the matrix ------------------------------------------------------

    /// <summary>Classic in, classic declared: the producer's own buffers, published as they stand.</summary>
    [Fact]
    public void A_classic_column_declared_classic_is_published_with_no_copy()
    {
        var producer = new ClassicProducer(Values);
        using var harness = new Harness(StringLayouts.Utf8, pooled: false);

        var batch = harness.Materialise(producer.View(harness.Arena));

        var array = Assert.IsType<StringArray>(batch.Column(0));
        Assert.Equal(1, producer.Publishes);
        Assert.Same(producer.Payload, BackingArray(array.Data.Buffers[2]));
        Assert.Same(producer.Offsets, BackingArray(array.Data.Buffers[1]));
        AssertValues(array);
        batch.Dispose();
    }

    /// <summary>
    /// Classic in, views declared: the classic payload becomes variadic buffer zero and no payload
    /// byte moves. The sixteen-byte lanes are the only thing built.
    /// </summary>
    [Fact]
    public void A_classic_column_declared_as_views_keeps_its_payload_buffer()
    {
        var producer = new ClassicProducer(Values);
        using var harness = new Harness(StringLayouts.Utf8View, pooled: false);

        var batch = harness.Materialise(producer.View(harness.Arena));

        var array = Assert.IsType<StringViewArray>(batch.Column(0));
        Assert.Equal(1, producer.Publishes);
        Assert.Equal(3, array.Data.Buffers.Length);
        Assert.Same(producer.Payload, BackingArray(array.Data.Buffers[2]));
        AssertValues(array);
        batch.Dispose();
    }

    /// <summary>Views in, views declared: the copier's own lanes and payload, one pass and no more.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_view_column_declared_as_views_stays_a_view(bool pooled)
    {
        using var harness = new Harness(StringLayouts.Utf8View, pooled);

        var batch = harness.Materialise(ViewColumn(Values, harness.Arena));

        var array = Assert.IsType<StringViewArray>(batch.Column(0));
        AssertValues(array);
        batch.Dispose();
    }

    /// <summary>Views in, classic declared: the one payload copy the decision permits.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_view_column_declared_classic_is_converted(bool pooled)
    {
        using var harness = new Harness(StringLayouts.Utf8, pooled);

        var batch = harness.Materialise(ViewColumn(Values, harness.Arena));

        var array = Assert.IsType<StringArray>(batch.Column(0));
        AssertValues(array);
        batch.Dispose();
    }

    /// <summary>Classic in, classic declared, with no producer to hand its buffers over: still classic.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_borrowed_classic_column_declared_classic_is_copied_once(bool pooled)
    {
        using var harness = new Harness(StringLayouts.Utf8, pooled);

        var batch = harness.Materialise(ClassicColumn(Values, harness.Arena));

        AssertValues(Assert.IsType<StringArray>(batch.Column(0)));
        batch.Dispose();
    }

    /// <summary>Classic in, views declared, with no producer to hand its buffers over.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_borrowed_classic_column_declared_as_views_is_converted(bool pooled)
    {
        using var harness = new Harness(StringLayouts.Utf8View, pooled);

        var batch = harness.Materialise(ClassicColumn(Values, harness.Arena));

        AssertValues(Assert.IsType<StringViewArray>(batch.Column(0)));
        batch.Dispose();
    }

    /// <summary>
    /// A selection is a gather, and a gather ends in the declared layout too — including the rows a
    /// slice leaves behind, which is the shape a batch narrowed by OFFSET/FETCH arrives in.
    /// </summary>
    [Theory]
    [InlineData(StringLayouts.Utf8, false)]
    [InlineData(StringLayouts.Utf8, true)]
    [InlineData(StringLayouts.Utf8View, false)]
    [InlineData(StringLayouts.Utf8View, true)]
    public void A_gathered_column_ends_in_the_declared_layout(StringLayouts declared, bool views)
    {
        using var harness = new Harness(declared, pooled: false);
        var column = views
            ? ViewColumn(Values, harness.Arena)
            : ClassicColumn(Values, harness.Arena);

        int[] selection = [6, 1, 0, 4, 2];
        var batch = harness.Materialise(column, selection);

        Assert.Equal(
            selection.Select(i => Values[i]).ToArray(),
            Strings(batch.Column(0), selection.Length));
        AssertDeclared(batch.Column(0), declared);
        batch.Dispose();
    }

    /// <summary>
    /// A slice with a non-zero row offset never takes a direct path, whichever layout is declared:
    /// the validity bitmap cannot be sliced without shifting bits, so it goes through the copier.
    /// </summary>
    [Theory]
    [InlineData(StringLayouts.Utf8, false)]
    [InlineData(StringLayouts.Utf8, true)]
    [InlineData(StringLayouts.Utf8View, false)]
    [InlineData(StringLayouts.Utf8View, true)]
    public void A_sliced_column_ends_in_the_declared_layout(StringLayouts declared, bool views)
    {
        using var harness = new Harness(declared, pooled: false);
        var column = views
            ? ViewColumn(Values, harness.Arena)
            : ClassicColumn(Values, harness.Arena);

        const int Start = 2;
        var sliced = column.Slice(Start, Values.Length - Start);
        Assert.Equal(Start, sliced.Offset);

        var batch = harness.Materialise(sliced);

        Assert.Equal(Values[Start..], Strings(batch.Column(0), Values.Length - Start));
        AssertDeclared(batch.Column(0), declared);
        batch.Dispose();
    }

    // ---- the conversions, on their own -----------------------------------------------------

    /// <summary>
    /// Re-describing a classic array as views shares its payload buffer and its validity bitmap, and
    /// builds nothing but the lanes.
    /// </summary>
    [Fact]
    public void Re_describing_a_classic_array_as_views_moves_no_payload()
    {
        var classic = Classic(Values);

        var views = StringViewArrays.OverClassic(classic);

        Assert.Same(BackingArray(classic.Data.Buffers[2]), BackingArray(views.Data.Buffers[2]));
        Assert.Same(BackingArray(classic.Data.Buffers[0]), BackingArray(views.Data.Buffers[0]));
        Assert.Equal(classic.NullCount, views.NullCount);
        AssertValues(views);
    }

    /// <summary>A column whose every value is inlined needs no variadic buffer at all.</summary>
    [Fact]
    public void An_all_inline_column_re_describes_without_a_variadic_buffer()
    {
        string?[] inline = ["a", null, string.Empty, "exactly12345"];

        var views = StringViewArrays.OverClassic(Classic(inline));

        Assert.Equal(2, views.Data.Buffers.Length);
        Assert.Equal(inline, Strings(views, inline.Length));
    }

    /// <summary>
    /// A view-typed batch survives Arrow's own IPC, which is the contract a host outside .NET reads
    /// through. Long values, short ones, the empty string and NULLs.
    /// </summary>
    [Fact]
    public void A_view_typed_batch_survives_an_IPC_round_trip()
    {
        var schema = new ArrowSchema([new Field("s", StringViewType.Default, nullable: true)], null);
        using var written = new RecordBatch(
            schema, [StringViewArrays.OverClassic(Classic(Values))], Values.Length);

        using var stream = new MemoryStream();
        using (var writer = new ArrowStreamWriter(stream, schema, leaveOpen: true))
        {
            writer.WriteRecordBatch(written);
            writer.WriteEnd();
        }

        stream.Position = 0;
        using var reader = new ArrowStreamReader(stream);
        using var read = reader.ReadNextRecordBatch();

        Assert.NotNull(read);
        Assert.Equal("utf8view", read.Schema.FieldsList[0].DataType.Name);
        Assert.Equal(Values, Strings(read.Column(0), Values.Length));
    }

    // ---- harness ---------------------------------------------------------------------------

    private static void AssertDeclared(IArrowArray array, StringLayouts declared)
    {
        if (declared == StringLayouts.Utf8)
        {
            Assert.IsType<StringArray>(array);
        }
        else
        {
            Assert.IsType<StringViewArray>(array);
        }
    }

    private static void AssertValues(IArrowArray array) =>
        Assert.Equal(Values, Strings(array, Values.Length));

    private static string?[] Strings(IArrowArray array, int length)
    {
        var values = new string?[length];
        for (var i = 0; i < length; i++)
        {
            values[i] = array switch
            {
                StringArray a => a.IsNull(i) ? null : a.GetString(i),
                StringViewArray a => a.IsNull(i) ? null : a.GetString(i),
                _ => throw new InvalidOperationException($"not a UTF-8 array: {array.GetType().Name}"),
            };
        }

        return values;
    }

    private static byte[]? BackingArray(ArrowBuffer buffer) =>
        MemoryMarshal.TryGetArray(buffer.Memory, out ArraySegment<byte> segment) ? segment.Array : null;

    /// <summary>The classic Arrow spelling of <paramref name="values"/>, in GC arrays.</summary>
    private static StringArray Classic(IReadOnlyList<string?> values)
    {
        var (offsets, payload, validity, nulls) = ClassicBuffers(values);
        return new StringArray(
            values.Count,
            new ArrowBuffer(offsets),
            new ArrowBuffer(payload),
            nulls == 0 ? ArrowBuffer.Empty : new ArrowBuffer(validity),
            nulls,
            0);
    }

    private static (byte[] Offsets, byte[] Payload, byte[] Validity, int Nulls) ClassicBuffers(
        IReadOnlyList<string?> values)
    {
        var offsets = new byte[(values.Count + 1) * sizeof(int)];
        var lanes = MemoryMarshal.Cast<byte, int>(offsets.AsSpan());
        var validity = new byte[Math.Max(1, (values.Count + 7) / 8)];
        var payload = new List<byte>();
        var nulls = 0;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is { } value)
            {
                BitUtility.SetBit(validity, i);
                payload.AddRange(Encoding.UTF8.GetBytes(value));
            }
            else
            {
                nulls++;
            }

            lanes[i + 1] = payload.Count;
        }

        return (offsets, payload.ToArray(), validity, nulls);
    }

    /// <summary>A classic column view over borrowed memory — no producer, nothing to publish.</summary>
    private static ColumnView ClassicColumn(IReadOnlyList<string?> values, ExecutionArena arena)
    {
        _ = arena;
        var (offsets, payload, validity, nulls) = ClassicBuffers(values);
        return new ColumnView
        {
            Type = Text,
            Length = values.Count,
            Values = payload,
            Offsets = offsets,
            Validity = nulls == 0 ? default : validity,
            NullCount = nulls,
        };
    }

    /// <summary>A STRING_VIEW column view, built the way an arena lane set is.</summary>
    private static ColumnView ViewColumn(IReadOnlyList<string?> values, ExecutionArena arena)
    {
        _ = arena;
        const int Width = 16;
        const int InlineLength = 12;

        var lanes = new byte[values.Count * Width];
        var descriptors = MemoryMarshal.Cast<byte, int>(lanes.AsSpan());
        var validity = new byte[Math.Max(1, (values.Count + 7) / 8)];
        var payload = new List<byte>();
        var nulls = 0;

        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is not { } value)
            {
                nulls++;
                continue;
            }

            BitUtility.SetBit(validity, i);
            var bytes = Encoding.UTF8.GetBytes(value);
            descriptors[i * 4] = bytes.Length;
            if (bytes.Length <= InlineLength)
            {
                bytes.CopyTo(lanes.AsSpan((i * Width) + sizeof(int), bytes.Length));
                continue;
            }

            descriptors[(i * 4) + 1] = MemoryMarshal.Read<int>(bytes.AsSpan(0, sizeof(int)));
            descriptors[(i * 4) + 3] = payload.Count;
            payload.AddRange(bytes);
        }

        var buffers = new ReadOnlyMemory<byte>[1];
        buffers[0] = payload.ToArray();

        return new ColumnView
        {
            Type = Text,
            Length = values.Count,
            Values = lanes,
            Validity = nulls == 0 ? default : validity,
            NullCount = nulls,
            ViewBuffers = buffers,
            ViewBufferCount = payload.Count == 0 ? 0 : 1,
        };
    }

    /// <summary>
    /// A producer that owns its classic buffers and hands them to the host, which is what the in-box
    /// POCO source's <c>string</c> writer does.
    /// </summary>
    private sealed class ClassicProducer : IManagedColumnPublisher
    {
        private readonly IReadOnlyList<string?> _values;
        private readonly byte[] _validity;
        private readonly int _nulls;

        public ClassicProducer(IReadOnlyList<string?> values)
        {
            _values = values;
            (Offsets, Payload, _validity, _nulls) = ClassicBuffers(values);
        }

        public byte[] Offsets { get; }

        public byte[] Payload { get; }

        public int Publishes { get; private set; }

        public ColumnView View(ExecutionArena arena)
        {
            _ = arena;
            return new ColumnView
            {
                Type = Text,
                Length = _values.Count,
                Values = Payload,
                Offsets = Offsets,
                Validity = _nulls == 0 ? default : _validity,
                NullCount = _nulls,
                ManagedPublisher = this,
            };
        }

        public bool CanPublish(in ColumnView view) =>
            ReferenceEquals(view.ManagedPublisher, this) && Publishes == 0;

        public IArrowArray PublishManaged(in ColumnView view)
        {
            Publishes++;
            return new StringArray(
                view.Length,
                new ArrowBuffer(Offsets),
                new ArrowBuffer(Payload),
                _nulls == 0 ? ArrowBuffer.Empty : new ArrowBuffer(_validity),
                _nulls,
                0);
        }
    }

    /// <summary>One materialiser over one column, with an arena and a context of its own.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly OperatorContext _context;
        private readonly OutputMaterialiser _materialiser;
        private readonly ColumnarBatch _batch;

        public Harness(StringLayouts declared, bool pooled)
        {
            Arena = new ExecutionArena();
            var settings = new ExecutionSettings { OutputStrings = declared, PooledOutput = pooled };
            _context = new OperatorContext
            {
                Settings = settings,
                Catalog = new CatalogContext { ContextId = "test", Epoch = 1, Schemas = [] },
                Sources = new Dictionary<string, ISourceRuntime>(StringComparer.Ordinal),
                PlanDigest = 0,
            };

            var schema = new ArrowSchema(
                [ArrowTypeMapping.ToArrowField("s", Text, declared)], metadata: null);

            using (new ScratchScope(_context))
            {
                _materialiser = new OutputMaterialiser(_context, schema, [Text], pooled);
            }

            _batch = new ColumnarBatch([Text]);
            _context.Begin(new ExecutionStats(), Arena, [], DateTimeOffset.UnixEpoch);
        }

        public ExecutionArena Arena { get; }

        public RecordBatch Materialise(in ColumnView column, int[]? selection = null)
        {
            _batch.Begin(column.Length);
            _batch.Set(0, column);
            if (selection is not null)
            {
                _batch.Select(selection, selection.Length);
            }

            return _materialiser.Materialise(_batch);
        }

        public void Dispose()
        {
            _context.End();
            Arena.Dispose();
        }
    }
}
