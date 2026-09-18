
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Arrays;
using Apache.Arrow.Scalars;
using Apache.Arrow.Types;
using Chalk.Catalog;

namespace Chalk.Sources.Poco;

/// <summary>Builds the Arrow array for one batch of a fixed-width column.</summary>
internal delegate IArrowArray PocoArrayFactory(ArrowBuffer values, ArrowBuffer nulls, int length, int nullCount);

/// <summary>
/// A column whose storage element is a blittable value that Arrow stores one-per-slot: every integer
/// and floating-point kind, plus DATE, TIME, TIMESTAMP and INTERVAL_DAY after their unit conversion.
/// </summary>
internal sealed class PocoFixedWidthColumn<T, TStorage> : PocoColumn<T>
    where TStorage : unmanaged
{
    private readonly PocoValueBinding _binding;
    private readonly PocoChunkLoopSet<T, TStorage> _loops;
    private readonly PocoArrayFactory _create;

    public PocoFixedWidthColumn(
        string name,
        ChalkType type,
        PocoValueBinding binding,
        PocoChunkLoopSet<T, TStorage> loops,
        PocoArrayFactory create)
        : base(name, type)
    {
        _binding = binding;
        _loops = loops;
        _create = create;
    }

    public override PocoChunkWriter<T> CreateWriter() => new Writer(Type, _loops, _create);

    public override Func<T, object?> CompileLogicalAccessor() =>
        PocoChunkCompiler.CompileLogicalAccessor<T>(_binding);

    private sealed class Writer : PocoChunkWriter<T>
    {
        private readonly PocoChunkLoopSet<T, TStorage> _loops;
        private readonly PocoArrayFactory _create;
        private readonly PocoByteMemory<TStorage> _bytes = new();
        private TStorage[] _values = [];

        public Writer(ChalkType type, PocoChunkLoopSet<T, TStorage> loops, PocoArrayFactory create)
            : base(type)
        {
            _loops = loops;
            _create = create;
        }

        protected override void AcquireCore(ExecutionArena arena, int capacity) =>
            _values = arena.Rent<TStorage>(capacity);

        protected override void Fill(IReadOnlyList<T> rows, int[]? positions, int start, int count)
        {
            if (positions is null)
            {
                _loops.Run(rows, start, count, _values, Valid);
            }
            else
            {
                _loops.RunGather(rows, positions, start, count, _values, Valid);
            }
        }

        protected override IArrowArray Build(int count)
        {
            var values = Arena.CreateBuffer(MemoryMarshal.AsBytes(_values.AsSpan(0, count)));
            var (nulls, nullCount) = BuildValidity(count);
            return _create(values, nulls, count, nullCount);
        }

        protected override ColumnView BuildView(int count)
        {
            var (bits, nulls) = BuildValidityView(count);
            return new ColumnView
            {
                Type = Type,
                Length = count,
                Values = _bytes.Of(_values, count),
                Validity = bits,
                NullCount = nulls,
            };
        }

        protected override void ReleaseCore()
        {
            Arena.Return(_values);
            _values = [];
        }
    }
}

/// <summary>BOOL. Arrow bit-packs booleans, so the byte-per-row chunk is packed twice: values and validity.</summary>
internal sealed class PocoBooleanColumn<T> : PocoColumn<T>
{
    private readonly PocoValueBinding _binding;
    private readonly PocoChunkLoopSet<T, byte> _loops;

    public PocoBooleanColumn(string name, ChalkType type, PocoValueBinding binding, PocoChunkLoopSet<T, byte> loops)
        : base(name, type)
    {
        _binding = binding;
        _loops = loops;
    }

    public override PocoChunkWriter<T> CreateWriter() => new Writer(Type, _loops);

    public override Func<T, object?> CompileLogicalAccessor() =>
        PocoChunkCompiler.CompileLogicalAccessor<T>(_binding);

    private sealed class Writer : PocoChunkWriter<T>
    {
        private readonly PocoChunkLoopSet<T, byte> _loops;
        private byte[] _values = [];
        private byte[] _packed = [];

        public Writer(ChalkType type, PocoChunkLoopSet<T, byte> loops)
            : base(type) => _loops = loops;

        protected override void AcquireCore(ExecutionArena arena, int capacity)
        {
            _values = arena.Rent<byte>(capacity);
            _packed = arena.Rent<byte>(BitmapBytes(capacity));
        }

        protected override void Fill(IReadOnlyList<T> rows, int[]? positions, int start, int count)
        {
            if (positions is null)
            {
                _loops.Run(rows, start, count, _values, Valid);
            }
            else
            {
                _loops.RunGather(rows, positions, start, count, _values, Valid);
            }
        }

        protected override IArrowArray Build(int count)
        {
            var bytes = BitmapBytes(count);
            _packed = Grow(_packed, bytes, keep: 0);
            var bits = _packed.AsSpan(0, bytes);
            bits.Clear();
            for (var i = 0; i < count; i++)
            {
                if (_values[i] != 0)
                {
                    BitUtility.SetBit(bits, i);
                }
            }

            var values = Arena.CreateBuffer(bits);
            var (nulls, nullCount) = BuildValidity(count);
            return new BooleanArray(values, nulls, count, nullCount, 0);
        }

        /// <summary>
        /// The engine's own booleans are a byte per row (§6.4), so the fast path hands the chunk
        /// loop's staging over as it stands and nothing is packed until the batch reaches the host.
        /// </summary>
        protected override ColumnView BuildView(int count)
        {
            var (bits, nulls) = BuildValidityView(count);
            return new ColumnView
            {
                Type = Type,
                Length = count,
                Values = _values.AsMemory(0, count),
                Validity = bits,
                NullCount = nulls,
            };
        }

        protected override void ReleaseCore()
        {
            Arena.Return(_values);
            Arena.Return(_packed);
            _values = [];
            _packed = [];
        }
    }
}

/// <summary>STRING. The chunk loop collects references; the second pass encodes them as UTF-8 (§5.3).</summary>
internal sealed class PocoStringColumn<T> : PocoColumn<T>
{
    internal const bool TrustSourceUtf16Validation = false;

    private readonly PocoValueBinding _binding;
    private readonly PocoChunkLoopSet<T, string> _loops;

    private static readonly UTF8Encoding Utf8 =
        new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: false);

    private static readonly UTF8Encoding StrictUtf8 =
        new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

    public PocoStringColumn(
        string name,
        ChalkType type,
        PocoValueBinding binding,
        PocoChunkLoopSet<T, string> loops)
        : base(name, type)
    {
        _binding = binding;
        _loops = loops;
    }

    public override PocoChunkWriter<T> CreateWriter() => new Writer(Type, _loops, Name);

    public override Func<T, object?> CompileLogicalAccessor() =>
        PocoChunkCompiler.CompileLogicalAccessor<T>(_binding);

    private sealed class Writer : PocoChunkWriter<T>, IManagedColumnPublisher
    {
        private readonly PocoChunkLoopSet<T, string> _loops;
        private readonly string _column;

        // _values is source-side scratch and remains owned by the writer.
        // _offsets and _encoded are already in Arrow's physical layout and may be
        // relinquished to the GC when this exact view reaches managed output.
        private string[] _values = [];
        private byte[] _offsets = [];
        private byte[] _encoded = [];

        public Writer(
            ChalkType type,
            PocoChunkLoopSet<T, string> loops,
            string column)
            : base(type)
        {
            _loops = loops;
            _column = column;
        }

        private Span<int> OffsetLanes =>
            MemoryMarshal.Cast<byte, int>(_offsets.AsSpan());

        protected override void AcquireCore(
            ExecutionArena arena,
            int capacity)
        {
            _values = arena.RentStrings(capacity);
            _offsets = arena.Rent<byte>(OffsetByteCount(capacity));

            // The payload scratch grows to the widest batch the scan meets and is then reused.
            _encoded = arena.Rent<byte>(capacity * 8);
        }

        protected override void Fill(
            IReadOnlyList<T> rows,
            int[]? positions,
            int start,
            int count)
        {
            if (positions is null)
            {
                _loops.Run(rows, start, count, _values, Valid);
            }
            else
            {
                _loops.RunGather(rows, positions, start, count, _values, Valid);
            }
        }

        protected override IArrowArray Build(int count)
        {
            var used = Encode(count);
            var offsets = Arena.CreateBuffer(_offsets.AsSpan(0, OffsetByteCount(count)));
            var data = Arena.CreateBuffer(_encoded.AsSpan(0, used));
            var (nulls, nullCount) = BuildValidity(count);
            return new StringArray(count, offsets, data, nulls, nullCount, 0);
        }

        protected override ColumnView BuildView(int count)
        {
            var used = Encode(count);
            var (bits, nulls) = BuildValidityView(count);

            return new ColumnView
            {
                Type = Type,
                Length = count,
                Values = _encoded.AsMemory(0, used),
                Offsets = _offsets.AsMemory(0, OffsetByteCount(count)),
                Validity = bits,
                NullCount = nulls,
                ManagedPublisher = this,
            };
        }

        public bool CanPublish(in ColumnView view)
        {
            if (!ReferenceEquals(view.ManagedPublisher, this)
                || view.Offset != 0
                || view.NullCount < 0
                || view.Length < 0
                || view.Offsets.Length != OffsetByteCount(view.Length)
                || !IsBackedBy(view.Offsets, _offsets))
            {
                return false;
            }

            if (view.Values.Length != 0 && !IsBackedBy(view.Values, _encoded))
            {
                return false;
            }

            return view.NullCount == 0 || IsCurrentValidity(view);
        }

        public IArrowArray PublishManaged(in ColumnView view)
        {
            if (!CanPublish(view))
            {
                throw new InvalidOperationException(
                    "the STRING column view no longer describes this writer's current buffers.");
            }

            var offsetLength = view.Offsets.Length;
            var offsets = _offsets;
            Arena.RelinquishToGc(offsets);
            _offsets = [];

            var offsetBuffer =
                new ArrowBuffer(offsets.AsMemory(0, offsetLength));

            ArrowBuffer dataBuffer;
            if (view.Values.Length == 0)
            {
                // Keep the writer's payload allocation warm when this batch has no bytes.
                dataBuffer = ArrowBuffer.Empty;
            }
            else
            {
                var encoded = _encoded;
                Arena.RelinquishToGc(encoded);
                _encoded = [];
                dataBuffer = new ArrowBuffer(encoded.AsMemory(0, view.Values.Length));
            }

            var validity = PublishValidityManaged(view);

            return new StringArray(
                view.Length,
                offsetBuffer,
                dataBuffer,
                validity,
                view.NullCount,
                0);
        }

        private int Encode(int count)
        {
            try
            {
                return EncodeCore(count);
            }
            catch (EncoderFallbackException)
            {
                ThrowInvalidUtf16(count);
                throw; // unreachable unless diagnostics unexpectedly find nothing
            }
        }

        [DoesNotReturn]
        private void ThrowInvalidUtf16(int count)
        {
            for (var i = 0; i < count; i++)
            {
                try
                {
                    _ = StrictUtf8.GetByteCount(_values[i].AsSpan());
                }
                catch (EncoderFallbackException)
                {
                    throw new InvalidUtf8Exception(
                        $"column '{_column}'",
                        i);
                }
            }

            throw new InvalidOperationException(
                "UTF-16 encoding failed but the offending row could not be identified.");
        }

        private int EncodeCore(int count)
        {
            _offsets = Grow(
                _offsets,
                OffsetByteCount(count),
                keep: 0);

            var offsets = OffsetLanes.Slice(0, count + 1);
            offsets[0] = 0;

#pragma warning disable CS0162 // Unreachable code detected
            var encoding =
                !TrustSourceUtf16Validation
                    ? StrictUtf8
                    : Utf8;
#pragma warning restore CS0162 // Unreachable code detected

            var used = 0;
            for (var i = 0; i < count; i++)
            {
                used += encoding.GetByteCount(_values[i].AsSpan());
                offsets[i + 1] = used;
            }

            _encoded = Grow(
                _encoded,
                used,
                keep: 0);

            var destination = _encoded.AsSpan(0, used);

            for (var i = 0; i < count; i++)
            {
                var chars = _values[i].AsSpan();
                if (chars.IsEmpty)
                {
                    continue;
                }

                var start = offsets[i];
                var length = offsets[i + 1] - start;
                _ = Utf8.GetBytes(chars, destination.Slice(start, length));
            }

            return used;
        }

        protected override void ReleaseCore()
        {
            Arena.ReturnStrings(_values);
            Arena.Return(_offsets);
            Arena.Return(_encoded);
            _values = [];
            _offsets = [];
            _encoded = [];
        }

        private static int OffsetByteCount(int rows) =>
            checked((rows + 1) * sizeof(int));

        private static bool IsBackedBy(
            ReadOnlyMemory<byte> memory,
            byte[] array) =>
            MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment)
            && ReferenceEquals(segment.Array, array)
            && segment.Offset == 0;
    }
}

/// <summary>
/// STRING already held as UTF-8. The vectorised path represents values as
/// Arrow STRING_VIEW: short values are embedded directly in a 16-byte view;
/// longer values reference their existing Utf8String backing memory.
///
/// The legacy Arrow-array path remains classic STRING until the host output
/// schema is taught to expose StringViewType.
/// </summary>
internal sealed class PocoUtf8Column<T> : PocoColumn<T>
{
    internal const bool TrustSourceUtf8Validation = true;

    private readonly PocoValueBinding _binding;
    private readonly PocoChunkLoopSet<T, Utf8String> _loops;
    private readonly string _table;

    private static readonly UTF8Encoding StrictUtf8 =
        new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

    public PocoUtf8Column(
        string name,
        ChalkType type,
        PocoValueBinding binding,
        PocoChunkLoopSet<T, Utf8String> loops,
        string table)
        : base(name, type)
    {
        _binding = binding;
        _loops = loops;
        _table = table;
    }

    public override PocoChunkWriter<T> CreateWriter() =>
        new Writer(
            Type,
            _loops,
            _table,
            Name);

    public override Func<T, object?> CompileLogicalAccessor() =>
        PocoChunkCompiler.CompileLogicalAccessor<T>(
            _binding);

    private sealed class Writer : PocoChunkWriter<T>
    {
        private readonly PocoChunkLoopSet<T, Utf8String> _loops;
        private readonly string _table;
        private readonly string _column;

        //
        // Source-side references gathered by the compiled POCO loop.
        //
        private Utf8String[] _values = [];

        //
        // STRING_VIEW physical lanes. BinaryView is exactly the Arrow
        // 16-byte view representation.
        //
        private readonly PocoByteMemory<BinaryView> _viewBytes = new();
        private BinaryView[] _views = [];

        //
        // Registry referenced by non-inline BinaryView.BufferIndex.
        //
        // Initially use one entry per long Utf8String. We can later
        // coalesce/deduplicate common backing buffers without changing
        // ColumnView's representation.
        //
        private ReadOnlyMemory<byte>[] _viewBuffers = [];
        private int _viewBufferCount;

        //
        // Classic STRING scratch is retained only for Build(), not
        // BuildView(). It is acquired lazily through Grow().
        //
        private byte[] _offsets = [];
        private byte[] _encoded = [];

        public Writer(
            ChalkType type,
            PocoChunkLoopSet<T, Utf8String> loops,
            string table,
            string column)
            : base(type)
        {
            _loops = loops;
            _table = table;
            _column = column;
        }

        private Span<int> OffsetLanes =>
            MemoryMarshal.Cast<byte, int>(
                _offsets.AsSpan());

        protected override void AcquireCore(
            ExecutionArena arena,
            int capacity)
        {
            _values =
                arena.Rent<Utf8String>(
                    capacity);

            _views =
                arena.Rent<BinaryView>(
                    capacity);

            //
            // Worst case: every row is a long value with independent
            // backing memory.
            //
            _viewBuffers =
                arena.Rent<ReadOnlyMemory<byte>>(
                    capacity);

            _viewBufferCount = 0;

            //
            // Do not eagerly rent classic STRING storage. In the vectorised
            // path it is no longer needed.
            //
            _offsets = [];
            _encoded = [];
        }

        protected override void Fill(
            IReadOnlyList<T> rows,
            int[]? positions,
            int start,
            int count)
        {
            if (positions is null)
            {
                _loops.Run(
                    rows,
                    start,
                    count,
                    _values,
                    Valid);
            }
            else
            {
                _loops.RunGather(
                    rows,
                    positions,
                    start,
                    count,
                    _values,
                    Valid);
            }
        }

        //
        // Keep the legacy Arrow-array path on classic STRING for now.
        //
        // This is intentional: switching this to StringViewArray also
        // requires the corresponding Arrow schema field to become
        // StringViewType.
        //
        protected override IArrowArray Build(
            int count)
        {
            var used =
                EncodeClassic(count);

            var offsets =
                Arena.CreateBuffer(
                    _offsets.AsSpan(
                        0,
                        OffsetByteCount(count)));

            var data =
                Arena.CreateBuffer(
                    _encoded.AsSpan(
                        0,
                        used));

            var (nulls, nullCount) =
                BuildValidity(count);

            return new StringArray(
                count,
                offsets,
                data,
                nulls,
                nullCount,
                0);
        }

        //
        // Vectorised execution uses STRING_VIEW.
        //
        protected override ColumnView BuildView(
            int count)
        {
            BuildViews(count);

            var (bits, nulls) =
                BuildValidityView(count);

            return new ColumnView
            {
                Type = Type,
                Length = count,

                //
                // N × 16-byte Arrow BinaryView/StringView lanes.
                //
                Values = _viewBytes.Of(_views, count),

                Validity = bits,
                NullCount = nulls,

                ViewBuffers = _viewBuffers,

                ViewBufferCount = _viewBufferCount,

                //
                // Deliberately no ManagedPublisher yet.
                //
            };
        }

        private void BuildViews(
            int count)
        {
            //
            // The array is reusable arena storage. Clear references left by
            // the previous batch so old Utf8String backing stores are not
            // accidentally kept alive.
            //
            if (_viewBufferCount != 0)
            {
                _viewBuffers
                    .AsSpan(
                        0,
                        _viewBufferCount)
                    .Clear();

                _viewBufferCount = 0;
            }

            for (var row = 0;
                 row < count;
                 row++)
            {
                var memory =
                    _values[row].AsMemory();

                var bytes =
                    memory.Span;

#pragma warning disable CS0162 // Unreachable code detected
                if (!TrustSourceUtf8Validation &&
                    !bytes.IsEmpty)
                {
                    ValidateValue(
                        bytes,
                        row);
                }
#pragma warning restore CS0162 // Unreachable code detected

                if (bytes.Length <=
                    BinaryView.MaxInlineLength)
                {
                    //
                    // [length:4][payload:12]
                    //
                    // No external payload buffer and no payload copy after
                    // this 16-byte lane is produced.
                    //
                    _views[row] =
                        new BinaryView(bytes);

                    continue;
                }

                //
                // The backing memory itself becomes a variadic StringView
                // data buffer. Because we register the already-sliced
                // ReadOnlyMemory, its buffer offset is zero.
                //
                var bufferIndex =
                    _viewBufferCount++;

                _viewBuffers[bufferIndex] =
                    memory;

                _views[row] =
                    new BinaryView(
                        bytes.Length,
                        bytes[..BinaryView.PrefixLength],
                        bufferIndex,
                        bufferOffset: 0);
            }
        }

        private void ValidateValue(
            ReadOnlySpan<byte> bytes,
            int row)
        {
            try
            {
                _ =
                    StrictUtf8.GetCharCount(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw new InvalidUtf8Exception(
                    $"column '{_table}.{_column}'",
                    row);
            }
        }

        //
        // Temporary compatibility path for Build().
        //
        private int EncodeClassic(
            int count)
        {
            _offsets =
                Grow(
                    _offsets,
                    OffsetByteCount(count),
                    keep: 0);

            var offsets =
                OffsetLanes.Slice(
                    0,
                    count + 1);

            offsets[0] = 0;

            var used = 0;

            for (var i = 0;
                 i < count;
                 i++)
            {
                used =
                    checked(
                        used +
                        _values[i].Length);

                offsets[i + 1] =
                    used;
            }

            _encoded =
                Grow(
                    _encoded,
                    used,
                    keep: 0);

            var destination =
                _encoded.AsSpan(
                    0,
                    used);

            for (var i = 0;
                 i < count;
                 i++)
            {
                var start =
                    offsets[i];

                var end =
                    offsets[i + 1];

                if (start == end)
                    continue;

                var source =
                    _values[i].AsSpan();

#pragma warning disable CS0162 // Unreachable code detected
                if (!TrustSourceUtf8Validation)
                {
                    ValidateValue(
                        source,
                        i);
                }
#pragma warning restore CS0162 // Unreachable code detected

                source.CopyTo(
                    destination[
                        start..end]);
            }

            return used;
        }

        protected override void ReleaseCore()
        {
            //
            // ReadOnlyMemory<byte> retains its backing object, so clear the
            // active prefix before returning the array to the arena.
            //
            if (_viewBufferCount != 0)
            {
                _viewBuffers
                    .AsSpan(
                        0,
                        _viewBufferCount)
                    .Clear();
            }

            _viewBufferCount = 0;

            Arena.Return(_values);
            Arena.Return(_views);
            Arena.Return(_viewBuffers);

            if (_offsets.Length != 0)
            {
                Arena.Return(
                    _offsets);
            }

            if (_encoded.Length != 0)
            {
                Arena.Return(
                    _encoded);
            }

            _values = [];
            _views = [];
            _viewBuffers = [];

            _offsets = [];
            _encoded = [];
        }

        private static int OffsetByteCount(
            int rows) =>
            checked(
                (rows + 1) *
                sizeof(int));
    }
}

/// <summary>BINARY. <c>byte[]</c> and <c>ReadOnlyMemory&lt;byte&gt;</c> both arrive as memory handles.</summary>
internal sealed class PocoBinaryColumn<T> : PocoColumn<T>
{
    private readonly PocoValueBinding _binding;
    private readonly PocoChunkLoopSet<T, ReadOnlyMemory<byte>> _loops;

    public PocoBinaryColumn(
        string name,
        ChalkType type,
        PocoValueBinding binding,
        PocoChunkLoopSet<T, ReadOnlyMemory<byte>> loops)
        : base(name, type)
    {
        _binding = binding;
        _loops = loops;
    }

    public override PocoChunkWriter<T> CreateWriter() => new Writer(Type, _loops);

    public override Func<T, object?> CompileLogicalAccessor() =>
        PocoChunkCompiler.CompileLogicalAccessor<T>(_binding);

    private sealed class Writer : PocoChunkWriter<T>
    {
        private readonly PocoChunkLoopSet<T, ReadOnlyMemory<byte>> _loops;
        private readonly PocoByteMemory<int> _offsetBytes = new();
        private ReadOnlyMemory<byte>[] _values = [];
        private int[] _offsets = [];
        private byte[] _encoded = [];

        public Writer(
            ChalkType type,
            PocoChunkLoopSet<T, ReadOnlyMemory<byte>> loops)
            : base(type) => _loops = loops;

        protected override void AcquireCore(ExecutionArena arena, int capacity)
        {
            _values = arena.Rent<ReadOnlyMemory<byte>>(capacity);
            _offsets = arena.Rent<int>(capacity + 1);
            _encoded = arena.Rent<byte>(capacity * 8);
        }

        protected override void Fill(
            IReadOnlyList<T> rows,
            int[]? positions,
            int start,
            int count)
        {
            if (positions is null)
            {
                _loops.Run(rows, start, count, _values, Valid);
            }
            else
            {
                _loops.RunGather(rows, positions, start, count, _values, Valid);
            }
        }

        protected override IArrowArray Build(int count)
        {
            var used = Encode(count);
            var offsets = Arena.CreateBuffer(MemoryMarshal.AsBytes(_offsets.AsSpan(0, count + 1)));
            var data = Arena.CreateBuffer(_encoded.AsSpan(0, used));
            var (nulls, nullCount) = BuildValidity(count);
            return new BinaryArray(BinaryType.Default, count, offsets, data, nulls, nullCount, 0);
        }

        protected override ColumnView BuildView(int count)
        {
            var used = Encode(count);
            var (bits, nulls) = BuildValidityView(count);

            return new ColumnView
            {
                Type = Type,
                Length = count,
                Values = _encoded.AsMemory(0, used),
                Offsets = _offsetBytes.Of(_offsets, count + 1),
                Validity = bits,
                NullCount = nulls,
            };
        }

        private int Encode(int count)
        {
            if (_offsets.Length < count + 1)
            {
                var grown = Arena.Rent<int>(count + 1);
                Arena.Return(_offsets);
                _offsets = grown;
            }

            _offsets[0] = 0;

            var used = 0;
            for (var i = 0; i < count; i++)
            {
                used += _values[i].Length;
                _offsets[i + 1] = used;
            }

            _encoded = Grow(
                _encoded,
                used,
                keep: 0);

            var destination = _encoded.AsSpan(0, used);
            for (var i = 0; i < count; i++)
            {
                var value = _values[i];
                if (value.IsEmpty)
                {
                    continue;
                }

                value.Span.CopyTo(
                    destination.Slice(
                        _offsets[i],
                        value.Length));
            }

            return used;
        }

        protected override void ReleaseCore()
        {
            Arena.Return(_values);
            Arena.Return(_offsets);
            Arena.Return(_encoded);
            _values = [];
            _offsets = [];
            _encoded = [];
        }
    }
}


/// <summary>DECIMAL. 16-byte little-endian two's-complement unscaled values (<c>02-ir.md</c> §3).</summary>
internal sealed class PocoDecimalColumn<T> : PocoColumn<T>
{
    private const int ByteWidth = 16;

    private readonly PocoValueBinding _binding;
    private readonly PocoChunkLoopSet<T, decimal> _loops;
    private readonly Decimal128Type _arrowType;
    private readonly string _sourceId;
    private readonly string _table;

    public PocoDecimalColumn(
        string name,
        ChalkType type,
        PocoValueBinding binding,
        PocoChunkLoopSet<T, decimal> loops,
        string sourceId,
        string table)
        : base(name, type)
    {
        _binding = binding;
        _loops = loops;
        _arrowType = (Decimal128Type)ArrowTypeMapping.ToArrow(type);
        _sourceId = sourceId;
        _table = table;
    }

    public override PocoChunkWriter<T> CreateWriter() =>
        new Writer(Type, _loops, _arrowType, Name, _sourceId, _table);

    public override Func<T, object?> CompileLogicalAccessor() =>
        PocoChunkCompiler.CompileLogicalAccessor<T>(_binding);

    private sealed class Writer : PocoChunkWriter<T>
    {
        private readonly PocoChunkLoopSet<T, decimal> _loops;
        private readonly Decimal128Type _arrowType;
        private readonly string _column;
        private readonly string _sourceId;
        private readonly string _table;
        private decimal[] _values = [];
        private byte[] _encoded = [];

        public Writer(
            ChalkType type,
            PocoChunkLoopSet<T, decimal> loops,
            Decimal128Type arrowType,
            string column,
            string sourceId,
            string table)
            : base(type)
        {
            _loops = loops;
            _arrowType = arrowType;
            _column = column;
            _sourceId = sourceId;
            _table = table;
        }

        protected override void AcquireCore(ExecutionArena arena, int capacity)
        {
            _values = arena.Rent<decimal>(capacity);
            _encoded = arena.Rent<byte>(capacity * ByteWidth);
        }

        protected override void Fill(IReadOnlyList<T> rows, int[]? positions, int start, int count)
        {
            if (positions is null)
            {
                _loops.Run(rows, start, count, _values, Valid);
            }
            else
            {
                _loops.RunGather(rows, positions, start, count, _values, Valid);
            }
        }

        protected override IArrowArray Build(int count)
        {
            var span = Encode(count);
            var (nulls, nullCount) = BuildValidity(count);
            return new Decimal128Array(
                new ArrayData(_arrowType, count, nullCount, 0, new[] { nulls, Arena.CreateBuffer(span) }));
        }

        protected override ColumnView BuildView(int count)
        {
            Encode(count);
            var (bits, nulls) = BuildValidityView(count);
            return new ColumnView
            {
                Type = Type,
                Length = count,
                Values = _encoded.AsMemory(0, count * ByteWidth),
                Validity = bits,
                NullCount = nulls,
            };
        }

        /// <summary>Writes the unscaled 16-byte lanes into the staging and returns them.</summary>
        private Span<byte> Encode(int count)
        {
            _encoded = Grow(_encoded, count * ByteWidth, keep: 0);
            var span = _encoded.AsSpan(0, count * ByteWidth);
            span.Clear();
            for (var i = 0; i < count; i++)
            {
                if (_values[i] != decimal.Zero)
                {
                    PocoConvert.WriteDecimal(
                        _values[i],
                        _arrowType.Precision,
                        _arrowType.Scale,
                        span.Slice(i * ByteWidth, ByteWidth),
                        _sourceId,
                        _table,
                        _column);
                }
            }

            return span;
        }

        protected override void ReleaseCore()
        {
            Arena.Return(_values);
            Arena.Return(_encoded);
            _values = [];
            _encoded = [];
        }
    }
}

/// <summary>UUID, stored as Arrow <c>FixedSizeBinary(16)</c> in RFC 4122 (big-endian) byte order.</summary>
internal sealed class PocoUuidColumn<T> : PocoColumn<T>
{
    private const int ByteWidth = 16;

    private readonly PocoValueBinding _binding;
    private readonly PocoChunkLoopSet<T, Guid> _loops;
    private readonly FixedSizeBinaryType _arrowType;

    public PocoUuidColumn(string name, ChalkType type, PocoValueBinding binding, PocoChunkLoopSet<T, Guid> loops)
        : base(name, type)
    {
        _binding = binding;
        _loops = loops;
        _arrowType = (FixedSizeBinaryType)ArrowTypeMapping.ToArrow(type);
    }

    public override PocoChunkWriter<T> CreateWriter() => new Writer(Type, _loops, _arrowType);

    public override Func<T, object?> CompileLogicalAccessor() =>
        PocoChunkCompiler.CompileLogicalAccessor<T>(_binding);

    private sealed class Writer : PocoChunkWriter<T>
    {
        private readonly PocoChunkLoopSet<T, Guid> _loops;
        private readonly FixedSizeBinaryType _arrowType;
        private Guid[] _values = [];
        private byte[] _encoded = [];

        public Writer(ChalkType type, PocoChunkLoopSet<T, Guid> loops, FixedSizeBinaryType arrowType)
            : base(type)
        {
            _loops = loops;
            _arrowType = arrowType;
        }

        protected override void AcquireCore(ExecutionArena arena, int capacity)
        {
            _values = arena.Rent<Guid>(capacity);
            _encoded = arena.Rent<byte>(capacity * ByteWidth);
        }

        protected override void Fill(IReadOnlyList<T> rows, int[]? positions, int start, int count)
        {
            if (positions is null)
            {
                _loops.Run(rows, start, count, _values, Valid);
            }
            else
            {
                _loops.RunGather(rows, positions, start, count, _values, Valid);
            }
        }

        protected override IArrowArray Build(int count)
        {
            var span = Encode(count);
            var (nulls, nullCount) = BuildValidity(count);
            return new FixedSizeBinaryArray(
                new ArrayData(_arrowType, count, nullCount, 0, new[] { nulls, Arena.CreateBuffer(span) }));
        }

        protected override ColumnView BuildView(int count)
        {
            Encode(count);
            var (bits, nulls) = BuildValidityView(count);
            return new ColumnView
            {
                Type = Type,
                Length = count,
                Values = _encoded.AsMemory(0, count * ByteWidth),
                Validity = bits,
                NullCount = nulls,
            };
        }

        private Span<byte> Encode(int count)
        {
            _encoded = Grow(_encoded, count * ByteWidth, keep: 0);
            var span = _encoded.AsSpan(0, count * ByteWidth);
            for (var i = 0; i < count; i++)
            {
                // Big-endian is the RFC 4122 order Arrow's UUID canonical extension uses; the CLR's
                // native layout is mixed-endian and would not interoperate.
                _values[i].TryWriteBytes(span.Slice(i * ByteWidth, ByteWidth), bigEndian: true, out _);
            }

            return span;
        }

        protected override void ReleaseCore()
        {
            Arena.Return(_values);
            Arena.Return(_encoded);
            _values = [];
            _encoded = [];
        }
    }
}
