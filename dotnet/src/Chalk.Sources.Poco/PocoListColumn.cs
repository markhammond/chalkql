using System.Linq.Expressions;
using System.Runtime.InteropServices;
using Apache.Arrow;
using Apache.Arrow.Types;
using Chalk.Catalog;

namespace Chalk.Sources.Poco;

/// <summary>
/// A <c>LIST</c> column (D58, {@code 14-windows-ii.md} §5): a <c>T[]</c>, <c>List&lt;T&gt;</c>,
/// <c>IReadOnlyList&lt;T&gt;</c> or <c>ImmutableArray&lt;T&gt;</c> member becomes an Arrow
/// <c>List&lt;element&gt;</c> with 32-bit offsets.
/// </summary>
/// <remarks>
/// The child array is built by <see cref="PocoColumnFactory"/> over the flattened elements, so every
/// element kind an ordinary column supports — including DECIMAL, UUID and BOOL, whose encodings are
/// not simple memory copies — is supported inside a list too, and by the same code. The flattening
/// buffer is a <see cref="List{T}"/> reused for the life of the scan rather than an arena rental,
/// because the element type is not constrained to <c>unmanaged</c>; it is cleared, never regrown,
/// after the first batch that reaches its size.
/// </remarks>
internal sealed class PocoListColumn<T, TElement> : PocoColumn<T>
{
    private readonly PocoValueBinding _binding;
    private readonly PocoChunkLoopSet<T, IReadOnlyList<TElement>> _loops;
    private readonly PocoColumn<TElement> _elements;
    private readonly ListType _arrowType;

    public PocoListColumn(
        string name,
        ChalkType type,
        PocoValueBinding binding,
        PocoChunkLoopSet<T, IReadOnlyList<TElement>> loops,
        PocoColumn<TElement> elements)
        : base(name, type)
    {
        _binding = binding;
        _loops = loops;
        _elements = elements;
        _arrowType = (ListType)ArrowTypeMapping.ToArrow(type);
    }

    public override PocoChunkWriter<T> CreateWriter() =>
        new Writer(Type, _loops, _elements, _arrowType);

    public override Func<T, object?> CompileLogicalAccessor() =>
        PocoChunkCompiler.CompileLogicalAccessor<T>(_binding);

    /// <summary>The element type's own plan, as an identity binding over the element itself.</summary>
    public static PocoValueBinding ElementBinding(PocoColumnPlan element, Type clrType)
    {
        var row = Expression.Parameter(clrType, "element");
        return new PocoValueBinding
        {
            Row = row,
            Value = row,
            CanBeNull = !clrType.IsValueType || Nullable.GetUnderlyingType(clrType) is not null,
            ToStorage = element.ToStorage,
        };
    }

    private sealed class Writer : PocoChunkWriter<T>
    {
        private readonly PocoChunkLoopSet<T, IReadOnlyList<TElement>> _loops;
        private readonly PocoColumn<TElement> _elements;
        private readonly ListType _arrowType;
        private readonly List<TElement> _flattened = [];
        private readonly PocoByteMemory<int> _offsetBytes = new();
        private readonly ColumnView[] _child = new ColumnView[1];

        // Not an arena rental: TElement is not constrained to unmanaged, and the arena's pools are.
        // One array per scan, reused for every batch of it (the shape ColumnCopier's staging has).
        private IReadOnlyList<TElement>?[] _lists = [];
        private int[] _offsets = [];
        private PocoChunkWriter<TElement>? _elementWriter;
        private int _elementCapacity;

        public Writer(
            ChalkType type,
            PocoChunkLoopSet<T, IReadOnlyList<TElement>> loops,
            PocoColumn<TElement> elements,
            ListType arrowType)
            // Always nullable: a null array, or a default ImmutableArray, is a NULL list.
            : base(type with { Nullable = true })
        {
            _loops = loops;
            _elements = elements;
            _arrowType = arrowType;
        }

        protected override void AcquireCore(ExecutionArena arena, int capacity)
        {
            if (_lists.Length < capacity)
            {
                _lists = new IReadOnlyList<TElement>?[capacity];
            }

            _offsets = arena.Rent<int>(capacity + 1);
        }

        protected override void Fill(IReadOnlyList<T> rows, int[]? positions, int start, int count)
        {
            if (_lists.Length < count)
            {
                _lists = new IReadOnlyList<TElement>?[count];
            }

            // The compiled loop writes a null entry where the member was null; a value type that
            // cannot be null still goes through the same slot, which is what keeps one loop per
            // column rather than two.
            var values = _lists!;
            if (positions is null)
            {
                _loops.Run(rows, start, count, values!, Valid);
            }
            else
            {
                _loops.RunGather(rows, positions, start, count, values!, Valid);
            }
        }

        protected override IArrowArray Build(int count)
        {
            Flatten(count);
            var child = BuildElements();
            var offsets = Arena.CreateBuffer(MemoryMarshal.AsBytes(_offsets.AsSpan(0, count + 1)));
            var (nulls, nullCount) = BuildValidity(count);
            return new ListArray(
                new ArrayData(
                    _arrowType,
                    count,
                    nullCount,
                    0,
                    new[] { nulls, offsets },
                    new[] { child.Data }));
        }

        protected override ColumnView BuildView(int count)
        {
            Flatten(count);
            _child[0] = BuildElementsView();
            var (bits, nulls) = BuildValidityView(count);
            return new ColumnView
            {
                Type = Type,
                Length = count,
                Offsets = _offsetBytes.Of(_offsets, count + 1),
                Validity = bits,
                NullCount = nulls,
                Children = _child,
            };
        }

        /// <summary>Flattens the batch's lists into one element buffer and writes the offsets.</summary>
        private void Flatten(int count)
        {
            if (_offsets.Length < count + 1)
            {
                var grown = Arena.Rent<int>(count + 1);
                Arena.Return(_offsets);
                _offsets = grown;
            }

            _flattened.Clear();
            _offsets[0] = 0;
            for (var i = 0; i < count; i++)
            {
                var list = _lists[i];

                // A null member is a NULL list. ImmutableArray's default unwraps to null too, which
                // is why the unwrap in PocoTypeMapping is a marshal rather than a cast.
                if (list is null)
                {
                    if (Valid is { } valid)
                    {
                        valid[i] = 0;
                    }

                    _offsets[i + 1] = _flattened.Count;
                    continue;
                }

                for (var e = 0; e < list.Count; e++)
                {
                    _flattened.Add(list[e]);
                }

                _offsets[i + 1] = _flattened.Count;
                _lists[i] = null;
            }
        }

        /// <summary>
        /// The child array, built by the element column's own writer over the flattened elements.
        /// The writer is sized to the largest element count the scan has met and kept, so a scan of
        /// many batches allocates its staging once.
        /// </summary>
        private IArrowArray BuildElements()
        {
            EnsureElementWriter();
            return _elementWriter!.Write(_flattened, 0, _flattened.Count);
        }

        /// <summary>The child column as a view, for the columnar fast path.</summary>
        private ColumnView BuildElementsView()
        {
            EnsureElementWriter();
            return _elementWriter!.WriteView(_flattened, 0, _flattened.Count);
        }

        private void EnsureElementWriter()
        {
            if (_elementWriter is null || _elementCapacity < _flattened.Count)
            {
                ReleaseElementWriter();
                _elementCapacity = Math.Max(_flattened.Count, Math.Max(_elementCapacity * 2, 16));
                _elementWriter = _elements.AcquireWriter(Arena, _elementCapacity);
            }
        }

        private void ReleaseElementWriter()
        {
            if (_elementWriter is { } writer)
            {
                _elementWriter = null;
                _elements.ReleaseWriter(writer);
            }
        }

        protected override void ReleaseCore()
        {
            ReleaseElementWriter();
            _elementCapacity = 0;
            Arena.Return(_offsets);
            _offsets = [];

            // The list staging is not arena memory (TElement is unconstrained), so it stays with the
            // pooled writer — but the host's own row objects must not.
            System.Array.Clear(_lists);
            _flattened.Clear();
            _flattened.TrimExcess();
        }
    }
}
