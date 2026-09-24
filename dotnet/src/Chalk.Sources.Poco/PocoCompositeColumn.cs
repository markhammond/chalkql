using Apache.Arrow;
using Apache.Arrow.Types;
using Chalk.Catalog;

namespace Chalk.Sources.Poco;

/// <summary>
/// A <c>COMPOSITE</c> column (D302): a member whose type is a record — a class or a struct of Tier 1
/// properties — becomes an Arrow <c>Struct</c> with one child per property, in the order and under
/// the names a function's record result is read with (D294).
/// </summary>
/// <remarks>
/// The member is read once per row into a staging array of records, and each field is then a column
/// of its own over those records, built by <see cref="PocoColumnFactory"/> exactly as a member of the
/// field's type is — so every kind a column supports is supported as a field, and by the same code,
/// as a <c>LIST</c>'s elements are (<see cref="PocoListColumn{T, TElement}"/>). The composite's
/// validity is the member's null: a struct member is never NULL, and a class or a
/// <c>Nullable&lt;TStruct&gt;</c> member may be. The staging array is not an arena rental, because
/// <typeparamref name="TRecord"/> is not constrained to <c>unmanaged</c>; it is one array per scan,
/// reused for every batch, and cleared when the scan ends so a scanned column holds no row of the
/// host's.
/// </remarks>
internal sealed class PocoCompositeColumn<T, TRecord> : PocoColumn<T>
{
    private readonly PocoValueBinding _binding;
    private readonly PocoChunkLoopSet<T, TRecord> _loops;
    private readonly PocoColumn<TRecord>[] _fields;
    private readonly StructType _arrowType;

    public PocoCompositeColumn(
        string name,
        ChalkType type,
        PocoValueBinding binding,
        PocoChunkLoopSet<T, TRecord> loops,
        PocoColumn<TRecord>[] fields)
        : base(name, type)
    {
        _binding = binding;
        _loops = loops;
        _fields = fields;
        _arrowType = (StructType)ArrowTypeMapping.ToArrow(type);
    }

    public override PocoChunkWriter<T> CreateWriter() => new Writer(Type, _loops, _fields, _arrowType);

    public override Func<T, object?> CompileLogicalAccessor() =>
        PocoChunkCompiler.CompileLogicalAccessor<T>(_binding);

    private sealed class Writer : PocoChunkWriter<T>
    {
        private readonly PocoChunkLoopSet<T, TRecord> _loops;
        private readonly PocoColumn<TRecord>[] _fields;
        private readonly StructType _arrowType;
        private readonly PocoChunkWriter<TRecord>?[] _fieldWriters;
        private readonly ColumnView[] _children;
        private TRecord[] _records = [];

        public Writer(
            ChalkType type,
            PocoChunkLoopSet<T, TRecord> loops,
            PocoColumn<TRecord>[] fields,
            StructType arrowType)
            : base(type)
        {
            _loops = loops;
            _fields = fields;
            _arrowType = arrowType;
            _fieldWriters = new PocoChunkWriter<TRecord>?[fields.Length];
            _children = new ColumnView[fields.Length];
        }

        protected override void AcquireCore(ExecutionArena arena, int capacity)
        {
            if (_records.Length < capacity)
            {
                _records = new TRecord[capacity];
            }

            for (var f = 0; f < _fields.Length; f++)
            {
                _fieldWriters[f] = _fields[f].AcquireWriter(arena, capacity);
            }
        }

        protected override void ReleaseCore()
        {
            for (var f = 0; f < _fields.Length; f++)
            {
                if (_fieldWriters[f] is { } writer)
                {
                    _fields[f].ReleaseWriter(writer);
                    _fieldWriters[f] = null;
                }
            }

            // The staged records are the host's rows' members; a scanned column keeps none of them.
            System.Array.Clear(_records);
            System.Array.Clear(_children);
        }

        protected override void Fill(IReadOnlyList<T> rows, int[]? positions, int start, int count)
        {
            if (_records.Length < count)
            {
                _records = new TRecord[count];
            }

            if (positions is null)
            {
                _loops.Run(rows, start, count, _records, Valid);
            }
            else
            {
                _loops.RunGather(rows, positions, start, count, _records, Valid);
            }
        }

        protected override IArrowArray Build(int count)
        {
            var children = new IArrowArray[_fields.Length];
            for (var f = 0; f < children.Length; f++)
            {
                children[f] = Field(f).Write(_records, 0, count);
            }

            var (nulls, nullCount) = BuildValidity(count);
            return new StructArray(_arrowType, count, children, nulls, nullCount);
        }

        protected override ColumnView BuildView(int count)
        {
            for (var f = 0; f < _children.Length; f++)
            {
                _children[f] = Field(f).WriteView(_records, 0, count);
            }

            var (bits, nulls) = BuildValidityView(count);
            return new ColumnView
            {
                Type = Type,
                Length = count,
                Validity = bits,
                NullCount = nulls,
                Children = _children,
            };
        }

        private PocoChunkWriter<TRecord> Field(int f) =>
            _fieldWriters[f]
            ?? throw new InvalidOperationException(
                "this composite column's writer is not attached to an execution; Acquire must be called before it writes.");
    }
}
