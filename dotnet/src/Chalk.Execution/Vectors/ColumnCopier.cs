using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apache.Arrow;
using Apache.Arrow.Types;
using Chalk.Catalog;
using Chalk.Execution.Memory;
using Chalk.Sources;

namespace Chalk.Execution.Vectors;

/// <summary>
/// Builds one owned column by appending: a whole view, a gathered selection, a broadcast constant, or
/// several of those in a row (which is how <c>SortOperator</c> concatenates its input). One copier per
/// output column per operator — the buffers inside it are rented from the execution's arena and
/// reused for every batch, which is what keeps a gather off the per-row allocation budget (ADR 0007,
/// and D64 for where the memory comes from).
/// </summary>
/// <remarks>
/// The result is a <see cref="ColumnView"/> over those buffers. Only <see cref="Finish"/> — the
/// host output boundary — builds an Arrow array, and that is also the only place the engine's
/// byte-per-row booleans become Arrow's bit-packed ones (§6.4).
/// </remarks>
internal sealed class ColumnCopier : IArenaScratch
{
    private enum StringStorage : byte
    {
        Unset,
        Classic,
        View,
    }

    private StringStorage _stringStorage;

    /// <summary>
    /// The layout this copier must finish STRING in (D244), or null to follow whatever arrives —
    /// which is what every operator inside the pipeline wants, and what the output boundary does not.
    /// </summary>
    private readonly StringLayouts? _stringTarget;

    private ExecutionArena? _arena;

    private ReadOnlyMemory<byte>[] _viewBuffers = [];
    private int _viewBufferCount;

    private readonly ColumnKind _kind;
    private readonly int _width;
    private readonly bool _variable;
    private readonly IArrowType _arrowType;

    private ArenaBuffer _values = new();
    private ArenaBuffer _validity = new();
    private ArenaBuffer _offsets = new();
    private ArenaBuffer _data = new();
    private readonly int[] _one = new int[1];

    /// <summary>Where a boolean is bit-packed on the way out; arena-backed like everything else.</summary>
    private ArenaBuffer _packed = new();

    private readonly ColumnView[] _child = new ColumnView[1];

    /// <summary>A LIST's element copier (D58), and null for every other kind.</summary>
    private readonly ColumnCopier? _children;

    /// <summary>
    /// A STRUCT's field copiers, one per field in order (D291), and null for every other kind. Every
    /// append walks them at the struct's own row, so each field has exactly the struct's rows.
    /// </summary>
    private readonly ColumnCopier[]? _fields;

    /// <summary>The finished field views a struct view points at, reused batch after batch.</summary>
    private readonly ColumnView[]? _fieldViews;

    private int _rows;
    private int _nulls;
    private int _dataUsed;
    private bool _validityMaterialised;

    /// <summary>
    /// How many rows the plan says this column is about to be given, or zero for "no estimate"
    /// (D258.3). Reset by every <see cref="Begin()"/>.
    /// </summary>
    private int _capacityHint;

    /// <summary>Offsets written so far. Entry zero is the leading zero <see cref="Begin"/> writes.</summary>
    private int _offsetCursor;

    /// <summary>Elements appended so far, which is what a LIST's offsets count.</summary>
    private int _elements;

    /// <param name="type">The column's logical type.</param>
    /// <param name="strings">
    /// The Arrow layout STRING must be finished in (D244), reaching a LIST's elements as well. Null
    /// — every copier inside the pipeline — follows what arrives: classic while only classic has
    /// been appended, views from the first view onwards.
    /// </param>
    public ColumnCopier(ChalkType type, StringLayouts? strings = null)
    {
        Type = type;
        _kind = ColumnKinds.Of(type);
        _width = ColumnKinds.Width(_kind);
        _variable = ColumnKinds.IsVariableLength(_kind);
        _stringTarget = strings;
        _arrowType = strings is { } layout
            ? ArrowTypeMapping.ToArrow(type, layout)
            : ArrowTypeMapping.ToArrow(type);
        _children = _kind == ColumnKind.List
            ? new ColumnCopier(
                type.Element
                ?? throw new UnsupportedFeatureException(
                    "a LIST column with no element type",
                    "docs/design/02-ir.md §3 requires Type.element on a LIST."),
                strings)
            : null;
        if (_kind == ColumnKind.Struct)
        {
            _fields = [.. type.Fields.Select(f => new ColumnCopier(f.Type, strings))];
            _fieldViews = new ColumnView[_fields.Length];
        }

        ScratchScope.Register(this);
    }

    public ChalkType Type { get; }

    /// <summary>Rows appended since <see cref="Begin"/>.</summary>
    public int Rows => _rows;

    public void Acquire(ExecutionArena arena)
    {
        _arena = arena;

        _values.Acquire(arena);
        _validity.Acquire(arena);
        _offsets.Acquire(arena);
        _data.Acquire(arena);
        _packed.Acquire(arena);
        _children?.Acquire(arena);
        foreach (var field in _fields ?? [])
        {
            field.Acquire(arena);
        }
    }

    public void Release()
    {
        _values.Release();
        _validity.Release();
        _offsets.Release();
        _data.Release();
        _packed.Release();
        _children?.Release();
        foreach (var field in _fields ?? [])
        {
            field.Release();
        }

        if (_viewBuffers.Length != 0)
        {
            _viewBuffers.AsSpan(0, _viewBufferCount).Clear();
            _arena!.Return(_viewBuffers);
            _viewBuffers = [];
        }

        _viewBufferCount = 0;
        _arena = null;
    }

    /// <summary>
    /// Starts a new column that is about to be given roughly <paramref name="capacityHint"/> rows,
    /// from the plan's <c>est_row_count</c> (D258.3). The hint is a floor on the first growth of each
    /// buffer rather than a rental of its own: a STRING column does not know until its first append
    /// whether it is stored as sixteen-byte views or as offsets and a payload, so each buffer applies
    /// the hint where its own width is known. Zero — no estimate — is <see cref="Begin()"/>.
    /// </summary>
    public void Begin(int capacityHint)
    {
        Begin();
        _capacityHint = capacityHint > 0 ? capacityHint : 0;
        _children?.Begin(capacityHint);
        foreach (var field in _fields ?? [])
        {
            field.Begin(capacityHint);
        }
    }

    /// <summary>Starts a new column.</summary>
    public void Begin()
    {
        _rows = 0;
        _nulls = 0;
        _dataUsed = 0;
        _elements = 0;
        _offsetCursor = 0;
        _validityMaterialised = false;
        _capacityHint = 0;

        _stringStorage = StringStorage.Unset;

        if (_viewBufferCount != 0)
        {
            _viewBuffers.AsSpan(0, _viewBufferCount).Clear();
            _viewBufferCount = 0;
        }

        _children?.Begin();
        foreach (var field in _fields ?? [])
        {
            field.Begin();
        }

        if (_variable || _kind == ColumnKind.List)
        {
            EnsureOffsets(1);
            OffsetLanes[0] = 0;
            _offsetCursor = 1;
        }
    }

    /// <summary>Appends the first <paramref name="count"/> rows of a view.</summary>
    public void Append(in ColumnView source, int count) => Copy(source, default, count);

    /// <summary>Appends the rows named by <paramref name="indices"/>, in that order.</summary>
    public void AppendGather(in ColumnView source, ReadOnlySpan<int> indices) => Copy(source, indices, -1);

    /// <summary>
    /// Appends one column of a batch, honouring its selection: a blocking operator copies the
    /// selected rows into its store and the selection stops there (§2).
    /// </summary>
    public void AppendBatch(ColumnarBatch batch, int column)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var view = batch.Column(column);
        if (batch.HasSelection)
        {
            AppendGather(view, batch.Selection);
        }
        else
        {
            Append(view, batch.RowCount);
        }
    }

    /// <summary>Appends one row of a view — how TopN assembles a result drawn from several batches.</summary>
    public void AppendRow(in ColumnView source, int row)
    {
        _one[0] = row;
        Copy(source, _one, -1);
    }

    /// <summary>
    /// Appends the rows named by <paramref name="indexes"/>; a negative index appends NULL. This is
    /// how an outer join pads the side that has no matching row (12-joins.md §4).
    /// </summary>
    public void AppendGatherOrNull(in ColumnView source, ReadOnlySpan<int> indexes)
    {
        foreach (var index in indexes)
        {
            if (index < 0)
            {
                AppendNulls(1);
            }
            else
            {
                AppendRow(source, index);
            }
        }
    }

    /// <summary>Appends <paramref name="count"/> NULLs.</summary>
    public void AppendNulls(int count)
    {
        if (_kind == ColumnKind.Struct)
        {
            // D291: a NULL struct still has a row in every field, NULL where the field may be and
            // its type's default where it may not.
            foreach (var field in _fields!)
            {
                field.AppendUndefined(count);
            }

            for (var i = 0; i < count; i++)
            {
                EndStruct(valid: false);
            }

            return;
        }

        if (_kind == ColumnKind.List)
        {
            for (var i = 0; i < count; i++)
            {
                EndList(valid: false);
            }

            return;
        }

        if (_kind == ColumnKind.Utf8)
        {
            AppendUtf8Nulls(count);
            return;
        }

        for (var i = 0; i < count; i++)
        {
            AppendRaw(default, valid: false);
        }
    }

    private void AppendUtf8Nulls(
        int count)
    {
        if (count <= 0)
            return;

        var first =
            _rows;

        EnsureValidityMaterialised(
            first + count);

        var validity =
            _validity.Bytes.AsSpan();

        for (var i = 0; i < count; i++)
        {
            BitUtility.ClearBit(
                validity,
                first + i);
        }

        _nulls += count;
        _rows += count;

        //
        // Maintain classic offsets regardless of which representation is
        // ultimately chosen. NULL consumes no payload.
        //
        EnsureOffsets(
            _offsetCursor +
            count);

        var offsets =
            OffsetLanes;

        for (var i = 0; i < count; i++)
        {
            offsets[_offsetCursor++] =
                _dataUsed;
        }

        //
        // If StringView has already been selected, the new physical lanes
        // must exist too. Zero is a harmless empty descriptor for NULL.
        //
        if (_stringStorage ==
            StringStorage.View)
        {
            _values.EnsureKeeping(
                checked(
                    _rows *
                    StringViewWidth),
                checked(
                    first *
                    StringViewWidth));

            _values.Bytes.AsSpan(
                    checked(
                        first *
                        StringViewWidth),
                    checked(
                        count *
                        StringViewWidth))
                .Clear();
        }

        //
        // Deliberately leave Unset as Unset.
        //
    }

    // ---- LIST (D58) -------------------------------------------------------------------------

    /// <summary>The element copier a LIST's values are appended through.</summary>
    public ColumnCopier Elements => _children
                                    ?? throw new InvalidOperationException("This column is not a LIST.");

    /// <summary>
    /// Closes the list under construction: everything appended to <see cref="Elements"/> since the
    /// previous close becomes one row. A row closed as invalid is a NULL list, and the elements
    /// appended before it — there should be none — still belong to it.
    /// </summary>
    public void EndList(bool valid)
    {
        SetValidity(_rows, valid);
        _rows++;
        if (!valid)
        {
            _nulls++;
        }

        AppendOffset(_elements);
    }

    /// <summary>Notes that one element has been appended to <see cref="Elements"/>.</summary>
    public void ElementAppended() => _elements++;

    /// <summary>Copies one whole list cell — its elements and its validity — from a LIST view.</summary>
    public void AppendListRow(in ColumnView source, int row)
    {
        if (!source.IsValid(row))
        {
            EndList(valid: false);
            return;
        }

        var offsets = source.OffsetLanes();
        var elements = source.Child;
        for (var i = offsets[row]; i < offsets[row + 1]; i++)
        {
            Elements.AppendRow(elements, i);
            _elements++;
        }

        EndList(valid: true);
    }

    // ---- STRUCT (D291) -----------------------------------------------------------------------

    /// <summary>The copier field <paramref name="index"/> of a STRUCT is appended through.</summary>
    public ColumnCopier Field(int index) => _fields?[index]
        ?? throw new InvalidOperationException("This column is not a STRUCT.");

    /// <summary>
    /// Closes the struct row under construction: each field has had exactly one row appended since
    /// the previous close. A row closed as invalid is a NULL struct.
    /// </summary>
    public void EndStruct(bool valid)
    {
        SetValidity(_rows, valid);
        _rows++;
        if (!valid)
        {
            _nulls++;
        }
    }

    /// <summary>
    /// Appends <paramref name="count"/> rows of a NULL struct's field: NULL when this column's type
    /// may be NULL, and the type's default when it may not, so a non-nullable field never holds one.
    /// </summary>
    public void AppendUndefined(int count)
    {
        if (Type.Nullable)
        {
            AppendNulls(count);
            return;
        }

        Span<byte> zero = stackalloc byte[16];
        zero.Clear();
        for (var i = 0; i < count; i++)
        {
            AppendRaw(_variable ? default : zero, valid: true);
        }
    }

    /// <summary>Appends <paramref name="count"/> copies of one constant.</summary>
    public void AppendConstant(ScalarValue value, int count)
    {
        if (_kind == ColumnKind.Struct)
        {
            // A struct is produced by a function and never written as a constant (D291), so the only
            // constant of this type is a typed NULL — an outer join's padding, say.
            if (!value.IsNull)
            {
                throw new UnsupportedFeatureException(
                    "a STRUCT constant",
                    "A struct comes from a function and has no literal (docs/design/51-structured-function-results.md §1).");
            }

            AppendNulls(count);
            return;
        }

        if (_kind == ColumnKind.List)
        {
            for (var i = 0; i < count; i++)
            {
                if (value.IsNull)
                {
                    EndList(valid: false);
                    continue;
                }

                foreach (var element in value.Elements)
                {
                    Elements.AppendConstant(element, 1);
                    _elements++;
                }

                EndList(valid: true);
            }

            return;
        }

        if (_kind == ColumnKind.Utf8 &&
            value.IsNull)
        {
            AppendUtf8Nulls(count);
            return;
        }

        var first = _rows;

        if (value.IsNull)
        {
            EnsureValidityMaterialised(first + count);

            var bits = _validity.Bytes.AsSpan();

            for (var i = 0; i < count; i++)
            {
                BitUtility.ClearBit(bits, first + i);
            }

            _nulls += count;
        }
        else if (_validityMaterialised)
        {
            EnsureValidity(first + count);

            var bits = _validity.Bytes.AsSpan();

            for (var i = 0; i < count; i++)
            {
                BitUtility.SetBit(bits, first + i);
            }
        }

        _rows +=
            count;

        if (_variable)
        {
            var payload =
                value.ReadBytes();

            if (_kind == ColumnKind.Utf8 &&
                _stringStorage == StringStorage.View)
            {
                AppendUtf8ViewConstant(
                    payload,
                    first,
                    count);

                return;
            }

            RequireClassicVariableStorage();

            for (var i = 0; i < count; i++)
            {
                if (!payload.IsEmpty)
                {
                    AppendData(
                        payload);
                }

                AppendOffset(
                    _dataUsed);
            }

            return;
        }

        EnsureValues(_rows, first);
        var lanes = _values.Bytes.AsSpan(first * _width, count * _width);
        lanes.Clear();
        if (!value.IsNull)
        {
            var lane = lanes[.._width];
            if (_kind == ColumnKind.Boolean)
            {
                lane[0] = (byte)(value.Integer != 0 ? 1 : 0);
            }
            else
            {
                ScalarLanes.Write(_kind, value, lane);
            }

            for (var i = 1; i < count; i++)
            {
                lane.CopyTo(lanes.Slice(i * _width, _width));
            }
        }
    }
    
    private void AppendUtf8ViewConstant(
        ReadOnlySpan<byte> value,
        int first,
        int count)
    {
        Debug.Assert(
            _stringStorage ==
            StringStorage.View);

        _values.EnsureKeeping(
            checked(
                _rows *
                StringViewWidth),
            checked(
                first *
                StringViewWidth));

        var destination =
            _values.Bytes.AsSpan(
                checked(
                    first *
                    StringViewWidth),
                checked(
                    count *
                    StringViewWidth));

        Span<byte> template =
            stackalloc byte[
                StringViewWidth];

        template.Clear();

        var length =
            value.Length;

        MemoryMarshal.Write(
            template,
            in length);

        if (length <=
            StringViewInlineLength)
        {
            value.CopyTo(
                template[
                    sizeof(int)..]);
        }
        else
        {
            value[
                    ..sizeof(int)]
                .CopyTo(
                    template.Slice(
                        sizeof(int),
                        sizeof(int)));

            var offset =
                _dataUsed;

            AppendData(
                value);

            MemoryMarshal.Write(
                template.Slice(12),
                in offset);
        }

        for (var i = 0; i < count; i++)
        {
            template.CopyTo(
                destination.Slice(
                    i *
                    StringViewWidth,
                    StringViewWidth));
        }
    }

    /// <summary>Appends one already-encoded lane — how the aggregate emits its keys and states.</summary>
    public void AppendRaw(
        ReadOnlySpan<byte> lane,
        bool valid)
    {
        if (_kind == ColumnKind.Struct)
        {
            throw new InvalidOperationException(
                "A STRUCT has no lane of its own: append its fields through Field(i) and close the "
                + "row with EndStruct.");
        }

        if (_kind == ColumnKind.Utf8 &&
            !valid)
        {
            AppendUtf8Nulls(1);
            return;
        }

        SetValidity(_rows, valid);

        _rows++;

        if (!valid)
            _nulls++;

        if (_variable)
        {
            if (_kind == ColumnKind.Utf8 &&
                _stringStorage == StringStorage.View)
            {
                AppendUtf8ViewRaw(
                    lane);

                return;
            }

            RequireClassicVariableStorage();

            if (!lane.IsEmpty)
            {
                AppendData(
                    lane);
            }

            AppendOffset(
                _dataUsed);

            return;
        }

        EnsureValues(
            _rows,
            _rows - 1);

        var slot =
            _values.Bytes.AsSpan(
                (_rows - 1) * _width,
                _width);

        slot.Clear();

        if (valid)
        {
            lane[.._width]
                .CopyTo(slot);
        }
    }
    
    private void AppendUtf8ViewRaw(
        ReadOnlySpan<byte> value)
    {
        Debug.Assert(
            _stringStorage ==
            StringStorage.View);

        var row =
            _rows - 1;

        _values.EnsureKeeping(
            checked(
                _rows *
                StringViewWidth),
            checked(
                row *
                StringViewWidth));

        var descriptor =
            _values.Bytes.AsSpan(
                checked(
                    row *
                    StringViewWidth),
                StringViewWidth);

        WriteOwnedStringView(
            value,
            descriptor);
    }
    
    private void WriteOwnedStringView(
        ReadOnlySpan<byte> value,
        Span<byte> descriptor)
    {
        Debug.Assert(
            descriptor.Length >=
            StringViewWidth);

        descriptor[
                ..StringViewWidth]
            .Clear();

        var length =
            value.Length;

        MemoryMarshal.Write(
            descriptor,
            in length);

        if (length <=
            StringViewInlineLength)
        {
            value.CopyTo(
                descriptor[
                    sizeof(int)..]);

            return;
        }

        //
        // Prefix.
        //
        value[
                ..sizeof(int)]
            .CopyTo(
                descriptor.Slice(
                    sizeof(int),
                    sizeof(int)));

        //
        // descriptor[8..12] remains zero => bufferIndex 0.
        //
        var offset =
            _dataUsed;

        AppendData(
            value);

        MemoryMarshal.Write(
            descriptor.Slice(12),
            in offset);
    }

    /// <summary>
    /// The column as a view over this copier's own buffers, valid until the next
    /// <see cref="Begin"/>. This is what an operator puts into its output slot (D61).
    /// </summary>
    public ColumnView FinishView()
    {
        if (_kind == ColumnKind.Struct)
        {
            for (var i = 0; i < _fields!.Length; i++)
            {
                _fieldViews![i] = _fields[i].FinishView();
            }

            return new ColumnView
            {
                Type = Type,
                Length = _rows,
                Validity = ValidityMemory(),
                NullCount = _nulls,
                Children = _fieldViews,
            };
        }

        if (_kind == ColumnKind.List)
        {
            _child[0] = Elements.FinishView();
            return new ColumnView
            {
                Type = Type,
                Length = _rows,
                Offsets = _offsets.Bytes.AsMemory(0, (_rows + 1) * sizeof(int)),
                Validity = ValidityMemory(),
                NullCount = _nulls,
                Children = _child,
            };
        }

        if (_stringStorage ==
            StringStorage.View)
        {
            PrepareOwnedViewBuffers();

            return new ColumnView
            {
                Type = Type,
                Length = _rows,

                Values =
                    _values.Bytes.AsMemory(
                        0,
                        checked(
                            _rows *
                            StringViewWidth)),

                Validity =
                    ValidityMemory(),

                NullCount =
                    _nulls,

                //
                // Non-null is the physical StringView discriminator.
                //
                ViewBuffers =
                    _viewBuffers,

                ViewBufferCount =
                    _viewBufferCount,
            };
        }

        return new ColumnView
        {
            Type = Type,
            Length = _rows,
            Values = _variable
                ? _data.Bytes.AsMemory(0, _dataUsed)
                : _values.Bytes.AsMemory(0, _rows * _width),
            Offsets = _variable ? _offsets.Bytes.AsMemory(0, (_rows + 1) * sizeof(int)) : default,
            Validity = ValidityMemory(),
            NullCount = _nulls,
        };
    }

    /// <summary>
    /// The column as an owned Arrow array, for the host output boundary and nowhere else (§1). The
    /// buffers are copied into <paramref name="allocator"/> memory, so the batch a host receives owns
    /// what it points at and the pipeline is free to overwrite its own slot.
    /// </summary>
    /// <remarks>
    /// Every <c>Build</c> claims allocator memory, and under an arena that memory counts against
    /// <c>MaxBytes</c>. A budget that runs out on the second or third buffer would strand the ones
    /// already built — nothing owns them until the <see cref="ArrayData"/> below does — so the buffers
    /// are assembled first and released together if any of them is refused (ADR 0012 §2).
    /// </remarks>
    public IArrowArray Finish(ExecutionArena? arena) => ToArrow(FinishView(), arena);

    public IArrowArray FinishManaged()
    {
        if (_kind == ColumnKind.Struct)
        {
            var fields = new IArrowArray?[_fields!.Length];
            var structBuffers = new ArrowBuffer[1];
            var structBuilt = 0;
            try
            {
                for (var i = 0; i < fields.Length; i++)
                {
                    fields[i] = _fields[i].FinishManaged();
                }

                structBuffers[0] = _nulls == 0
                    ? ArrowBuffer.Empty
                    : DetachManaged(ref _validity, Validity.ByteCount(_rows));
                structBuilt = 1;

                return new StructArray(
                    new ArrayData(
                        _arrowType, _rows, _nulls, 0, structBuffers, fields.Select(f => f!.Data)));
            }
            catch
            {
                Release(structBuffers, structBuilt);
                DisposeAll(fields);
                throw;
            }
        }

        if (_kind == ColumnKind.List)
        {
            IArrowArray? child = null;
            var listBuffers = new ArrowBuffer[2];
            var listBuilt = 0;

            try
            {
                child = Elements.FinishManaged();

                listBuffers[0] =
                    _nulls == 0
                        ? ArrowBuffer.Empty
                        : DetachManaged(
                            ref _validity,
                            Validity.ByteCount(_rows));

                listBuilt = 1;

                listBuffers[1] =
                    DetachManaged(
                        ref _offsets,
                        checked((_rows + 1) * sizeof(int)));

                listBuilt = 2;

                return new ListArray(
                    new ArrayData(
                        _arrowType,
                        _rows,
                        _nulls,
                        0,
                        listBuffers,
                        [child.Data]));
            }
            catch
            {
                Release(listBuffers, listBuilt);
                child?.Dispose();
                throw;
            }
        }

        //
        // Must happen before any generic buffer is detached. A copier that was told to finish
        // classic (D244) never selected view storage, so it falls through to the generic
        // variable-length path below, whose _arrowType is utf8 and whose buffers already are a
        // StringArray's.
        //
        if (_kind == ColumnKind.Utf8 &&
            _stringTarget != StringLayouts.Utf8)
        {
            return _stringStorage switch
            {
                StringStorage.View =>
                    FinishStringViewManaged(),

                StringStorage.Classic =>
                    FinishClassicAsStringViewManaged(),

                StringStorage.Unset =>
                    FinishEmptyStringViewManaged(),

                _ =>
                    throw new UnreachableException(),
            };
        }

        Debug.Assert(
            _kind != ColumnKind.Utf8 ||
            _stringStorage != StringStorage.View);

        var buffers =
            new ArrowBuffer[_variable ? 3 : 2];

        var built = 0;

        try
        {
            buffers[0] =
                _nulls == 0
                    ? ArrowBuffer.Empty
                    : DetachManaged(
                        ref _validity,
                        Validity.ByteCount(_rows));

            built = 1;

            if (_variable)
            {
                buffers[1] =
                    DetachManaged(
                        ref _offsets,
                        checked((_rows + 1) * sizeof(int)));

                built = 2;

                buffers[2] =
                    DetachManaged(
                        ref _data,
                        _dataUsed);

                built = 3;
            }
            else if (_kind == ColumnKind.Boolean)
            {
                PackBits();

                buffers[1] =
                    DetachManaged(
                        ref _packed,
                        Validity.ByteCount(_rows));

                built = 2;
            }
            else
            {
                buffers[1] =
                    DetachManaged(
                        ref _values,
                        checked(_rows * _width));

                built = 2;
            }

            return ArrowArrayFactory.BuildArray(
                new ArrayData(
                    _arrowType,
                    _rows,
                    _nulls,
                    0,
                    buffers,
                    children: null));
        }
        catch
        {
            Release(buffers, built);
            throw;
        }
    }

    private IArrowArray FinishClassicAsStringViewPooled(
        ref PooledBatchRentalCollector rentals)
    {
        var hasExternalData =
            BuildClassicStringViews();

        var viewByteCount =
            checked(_rows * StringViewWidth);

        var buffers =
            new ArrowBuffer[
                hasExternalData
                    ? 3
                    : 2];

        var built = 0;

        try
        {
            buffers[0] =
                _nulls == 0
                    ? ArrowBuffer.Empty
                    : rentals.Adopt(
                        ref _validity,
                        Validity.ByteCount(_rows));

            built = 1;

            buffers[1] =
                viewByteCount == 0
                    ? ArrowBuffer.Empty
                    : rentals.Adopt(
                        ref _values,
                        viewByteCount);

            built = 2;

            if (hasExternalData)
            {
                buffers[2] =
                    rentals.Adopt(
                        ref _data,
                        _dataUsed);

                built = 3;
            }

            return new StringViewArray(
                new ArrayData(
                    StringViewType.Default,
                    _rows,
                    _nulls,
                    0,
                    buffers,
                    children: null));
        }
        catch
        {
            Release(buffers, built);
            throw;
        }
    }

    private IArrowArray FinishClassicAsStringViewManaged()
    {
        var hasExternalData =
            BuildClassicStringViews();

        var viewByteCount =
            checked(_rows * StringViewWidth);

        var buffers =
            new ArrowBuffer[
                hasExternalData
                    ? 3
                    : 2];

        var built = 0;

        try
        {
            buffers[0] =
                _nulls == 0
                    ? ArrowBuffer.Empty
                    : DetachManaged(
                        ref _validity,
                        Validity.ByteCount(_rows));

            built = 1;

            buffers[1] =
                viewByteCount == 0
                    ? ArrowBuffer.Empty
                    : DetachManaged(
                        ref _values,
                        viewByteCount);

            built = 2;

            if (hasExternalData)
            {
                //
                // Long descriptors reference this exact buffer as
                // variadic buffer zero.
                //
                buffers[2] =
                    DetachManaged(
                        ref _data,
                        _dataUsed);

                built = 3;
            }

            return new StringViewArray(
                new ArrayData(
                    StringViewType.Default,
                    _rows,
                    _nulls,
                    0,
                    buffers,
                    children: null));
        }
        catch
        {
            Release(buffers, built);
            throw;
        }
    }

    private bool BuildClassicStringViews()
    {
        Debug.Assert(_kind == ColumnKind.Utf8);
        Debug.Assert(_stringStorage == StringStorage.Classic);
        Debug.Assert(_offsetCursor == _rows + 1);

        var viewByteCount =
            checked(_rows * StringViewWidth);

        _values.EnsureKeeping(
            Math.Max(viewByteCount, 1),
            keep: 0);

        var views =
            _values.Bytes.AsSpan(
                0,
                viewByteCount);

        views.Clear();

        var offsets =
            OffsetLanes[..(_rows + 1)];

        var data =
            _data.Bytes.AsSpan(
                0,
                _dataUsed);

        Debug.Assert(
            offsets[_rows] == _dataUsed);

        var descriptors =
            MemoryMarshal.Cast<byte, int>(
                views);

        var hasExternalData = false;

        for (var row = 0; row < _rows; row++)
        {
            var start =
                offsets[row];

            var length =
                offsets[row + 1] - start;

            Debug.Assert(start >= 0);
            Debug.Assert(length >= 0);
            Debug.Assert(start + length <= _dataUsed);

            var lane =
                row * 4;

            //
            // length
            //
            descriptors[lane] =
                length;

            if (length <= StringViewInlineLength)
            {
                if (length != 0)
                {
                    data.Slice(
                            start,
                            length)
                        .CopyTo(
                            views.Slice(
                                row * StringViewWidth +
                                sizeof(int),
                                length));
                }

                continue;
            }

            //
            // prefix
            //
            descriptors[lane + 1] =
                MemoryMarshal.Read<int>(
                    data.Slice(
                        start,
                        sizeof(int)));

            //
            // descriptors[lane + 2] is bufferIndex == 0.
            // views.Clear() already established this.
            //

            descriptors[lane + 3] =
                start;

            hasExternalData = true;
        }

        return hasExternalData;
    }

    private void PromoteClassicToView()
    {
        Debug.Assert(_kind == ColumnKind.Utf8);
        Debug.Assert(_stringStorage == StringStorage.Classic);

        var hasExternalData =
            BuildClassicStringViews();

        //
        // If every existing value fitted inline, the old classic payload
        // is no longer referenced by any descriptor. Reclaim its logical
        // contents so subsequent long views can start at offset zero.
        //
        if (!hasExternalData)
        {
            _dataUsed = 0;
        }

        _stringStorage =
            StringStorage.View;
    }

    public IArrowArray FinishPooled(
        ref PooledBatchRentalCollector rentals)
    {
        if (_kind == ColumnKind.Struct)
        {
            var fields = new IArrowArray?[_fields!.Length];
            var structBuffers = new ArrowBuffer[1];
            var structBuilt = 0;
            try
            {
                for (var i = 0; i < fields.Length; i++)
                {
                    fields[i] = _fields[i].FinishPooled(ref rentals);
                }

                structBuffers[0] = _nulls == 0
                    ? ArrowBuffer.Empty
                    : rentals.Adopt(ref _validity, Validity.ByteCount(_rows));
                structBuilt = 1;

                return new StructArray(
                    new ArrayData(
                        _arrowType, _rows, _nulls, 0, structBuffers, fields.Select(f => f!.Data)));
            }
            catch
            {
                Release(structBuffers, structBuilt);
                DisposeAll(fields);
                throw;
            }
        }

        if (_kind == ColumnKind.List)
        {
            IArrowArray? child = null;

            var listBuffers = new ArrowBuffer[2];
            var listBuilt = 0;

            try
            {
                child = Elements.FinishPooled(ref rentals);

                listBuffers[0] = _nulls == 0
                    ? ArrowBuffer.Empty
                    : rentals.Adopt(ref _validity, Validity.ByteCount(_rows));

                listBuilt = 1;

                listBuffers[1] = rentals.Adopt(ref _offsets, checked((_rows + 1) * sizeof(int)));

                listBuilt = 2;

                return new ListArray(
                    new ArrayData(_arrowType, _rows, _nulls, 0, listBuffers, [child.Data]));
            }
            catch
            {
                Release(listBuffers, listBuilt);
                child?.Dispose();

                throw;
            }
        }

        // As FinishManaged: a copier told to finish classic (D244) goes through the generic
        // variable-length path, which is a StringArray's buffers already.
        if (_kind == ColumnKind.Utf8 &&
            _stringTarget != StringLayouts.Utf8)
        {
            return _stringStorage switch
            {
                StringStorage.View =>
                    FinishStringViewPooled(
                        ref rentals),

                StringStorage.Classic =>
                    FinishClassicAsStringViewPooled(
                        ref rentals),

                _ =>
                    FinishEmptyStringViewPooled(
                        ref rentals),
            };
        }

        Debug.Assert(
            _kind != ColumnKind.Utf8 ||
            _stringStorage != StringStorage.View);

        var buffers = new ArrowBuffer[_variable ? 3 : 2];
        var built = 0;

        try
        {
            buffers[0] = _nulls == 0
                ? ArrowBuffer.Empty
                : rentals.Adopt(ref _validity, Validity.ByteCount(_rows));

            built = 1;

            if (_variable)
            {
                buffers[1] = rentals.Adopt(ref _offsets, checked((_rows + 1) * sizeof(int)));

                built = 2;
                buffers[2] = rentals.Adopt(ref _data, _dataUsed);

                built = 3;
            }
            else if (_kind ==
                     ColumnKind.Boolean)
            {
                PackBits();

                buffers[1] = rentals.Adopt(ref _packed, Validity.ByteCount(_rows));
                built = 2;
            }
            else
            {
                buffers[1] = rentals.Adopt(ref _values, checked(_rows * _width));
                built = 2;
            }

            return ArrowArrayFactory.BuildArray(
                new ArrayData(_arrowType, _rows, _nulls, 0, buffers, children: null));
        }
        catch
        {
            Release(buffers, built);
            throw;
        }
    }

    private IArrowArray FinishStringViewPooled(
        ref PooledBatchRentalCollector rentals)
    {
        Debug.Assert(
            _stringStorage ==
            StringStorage.View);

        var hasExternalData =
            _dataUsed != 0;

        var buffers =
            new ArrowBuffer[
                hasExternalData
                    ? 3
                    : 2];

        var built = 0;

        try
        {
            buffers[0] =
                _nulls == 0
                    ? ArrowBuffer.Empty
                    : rentals.Adopt(
                        ref _validity,
                        Validity.ByteCount(
                            _rows));

            built = 1;

            buffers[1] =
                _rows == 0
                    ? ArrowBuffer.Empty
                    : rentals.Adopt(
                        ref _values,
                        checked(
                            _rows *
                            StringViewWidth));

            built = 2;

            if (hasExternalData)
            {
                //
                // Payload became copier-owned in CopyStringView(),
                // so this is an ownership transfer, not another copy.
                //
                buffers[2] =
                    rentals.Adopt(
                        ref _data,
                        _dataUsed);

                built = 3;
            }

            return new StringViewArray(
                new ArrayData(
                    StringViewType.Default,
                    _rows,
                    _nulls,
                    0,
                    buffers,
                    children: null));
        }
        catch
        {
            Release(
                buffers,
                built);

            throw;
        }
    }

    private IArrowArray FinishStringViewManaged()
    {
        Debug.Assert(
            _stringStorage ==
            StringStorage.View);

        var hasExternalData =
            _dataUsed != 0;

        var buffers =
            new ArrowBuffer[
                hasExternalData
                    ? 3
                    : 2];

        var built = 0;

        try
        {
            buffers[0] =
                _nulls == 0
                    ? ArrowBuffer.Empty
                    : DetachManaged(
                        ref _validity,
                        Validity.ByteCount(
                            _rows));

            built = 1;

            buffers[1] =
                _rows == 0
                    ? ArrowBuffer.Empty
                    : DetachManaged(
                        ref _values,
                        checked(
                            _rows *
                            StringViewWidth));

            built = 2;

            if (hasExternalData)
            {
                buffers[2] =
                    DetachManaged(
                        ref _data,
                        _dataUsed);

                built = 3;
            }

            return new StringViewArray(
                new ArrayData(
                    StringViewType.Default,
                    _rows,
                    _nulls,
                    0,
                    buffers,
                    children: null));
        }
        catch
        {
            Release(
                buffers,
                built);

            throw;
        }
    }

    private void PackBits()
    {
        var bytes = Validity.ByteCount(_rows);

        _packed.Ensure(Math.Max(bytes, 1));

        var bits = _packed.Bytes.AsSpan(0, bytes);
        bits.Clear();

        var values = _values.Bytes.AsSpan();

        for (var row = 0; row < _rows; row++)
        {
            if (values[row] != 0)
            {
                BitUtility.SetBit(bits, row);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ArrowBuffer DetachManaged(
        ref ArenaBuffer buffer,
        int length)
    {
        if (length == 0)
            return ArrowBuffer.Empty;

        var bytes =
            buffer.RelinquishToGc();

        Debug.Assert(
            bytes.Length >= length);

        return new ArrowBuffer(
            bytes.AsMemory(0, length));
    }

    /// <summary>Builds a whole column in one call: <see cref="Begin"/>, one append, <see cref="FinishView"/>.</summary>
    public ColumnView CopyAll(in Vector vector, int length)
    {
        Begin();
        if (vector.IsScalar)
        {
            AppendConstant(vector.Scalar, length);
        }
        else
        {
            Append(vector.View, length);
        }

        return FinishView();
    }

    /// <summary>Builds a whole column from a gather list.</summary>
    public ColumnView Gather(in ColumnView source, ReadOnlySpan<int> indices)
    {
        Begin();
        AppendGather(source, indices);
        return FinishView();
    }

    /// <summary>
    /// One column view as an owned Arrow array. The one place Arrow objects are created on the way
    /// out, and the one place a boolean is bit-packed. The builders are this copier's own, so an
    /// output batch costs the copies and nothing more.
    /// </summary>
    public IArrowArray ToArrow(in ColumnView view, ExecutionArena? arena)
    {
        if (view.Offset != 0)
        {
            // The validity bitmap and the offsets buffer are read from byte zero below, which is
            // only right for a view this copier produced. A sliced view would come out silently
            // misaligned, so it is refused rather than answered wrongly.
            throw new InvalidOperationException(
                "a column view with a row offset cannot be turned into an Arrow array directly; "
                + "copy it through a ColumnCopier first.");
        }

        if (_kind == ColumnKind.Struct)
        {
            var fields = new IArrowArray?[_fields!.Length];
            var structBuffers = new ArrowBuffer[1];
            try
            {
                for (var i = 0; i < fields.Length; i++)
                {
                    fields[i] = _fields[i].ToArrow(view.Children![i], arena);
                }

                structBuffers[0] = view.NullCount == 0
                    ? ArrowBuffer.Empty
                    : Owned(view.Validity.Span[..Validity.ByteCount(view.Length)], arena);
            }
            catch
            {
                DisposeAll(fields);
                throw;
            }

            return new StructArray(
                new ArrayData(
                    _arrowType, view.Length, view.NullCount, 0, structBuffers, fields.Select(f => f!.Data)));
        }

        if (_kind == ColumnKind.List)
        {
            var child = Elements.ToArrow(view.Child, arena);
            var listBuffers = new ArrowBuffer[2];
            try
            {
                listBuffers[0] = view.NullCount == 0
                    ? ArrowBuffer.Empty
                    : Owned(view.Validity.Span[..Validity.ByteCount(view.Length)], arena);
                listBuffers[1] = Owned(view.Offsets.Span[..((view.Length + 1) * sizeof(int))], arena);
            }
            catch
            {
                child.Dispose();
                Release(listBuffers, 1);
                throw;
            }

            return new ListArray(
                new ArrayData(_arrowType, view.Length, view.NullCount, 0, listBuffers, [child.Data]));
        }

        var buffers = new ArrowBuffer[_variable ? 3 : 2];
        var built = 0;
        try
        {
            // Arrow wants no bitmap at all rather than a bitmap of all ones.
            buffers[0] = view.NullCount == 0
                ? ArrowBuffer.Empty
                : Owned(view.Validity.Span[..Validity.ByteCount(view.Length)], arena);
            built = 1;
            if (_variable)
            {
                if (view.ViewBuffers is not null)
                {
                    throw new InvalidOperationException(
                        "A STRING_VIEW column must be normalised through "
                        + "ColumnCopier before classic Arrow STRING materialisation.");
                }

                if (_kind == ColumnKind.Utf8 &&
                    _stringTarget == StringLayouts.Utf8View)
                {
                    throw new InvalidOperationException(
                        "A STRING column declared as utf8view cannot be built directly from a "
                        + "classic view; append it through this copier instead (D244).");
                }

                buffers[1] = Owned(view.Offsets.Span[..((view.Length + 1) * sizeof(int))], arena);
                built = 2;
                buffers[2] = Owned(view.Values.Span, arena);
                built = 3;
            }
            else if (_kind == ColumnKind.Boolean)
            {
                buffers[1] = PackBits(view, arena);
                built = 2;
            }
            else
            {
                buffers[1] = Owned(
                    view.Values.Span.Slice(view.Offset * _width, view.Length * _width), arena);
                built = 2;
            }
        }
        catch
        {
            Release(buffers, built);
            throw;
        }

        return ArrowArrayFactory.BuildArray(
            new ArrayData(_arrowType, view.Length, view.NullCount, 0, buffers, children: null));
    }

    public IArrowArray ToArrowPooled(
        in ColumnView view,
        ref PooledBatchRentalCollector rentals)
    {
        if (view.Offset != 0)
        {
            throw new InvalidOperationException(
                "A column view with a row offset cannot be converted "
                + "directly; normalise it through a ColumnCopier first.");
        }

        if (_kind == ColumnKind.Struct)
        {
            var fields = new IArrowArray?[_fields!.Length];
            var structBuffers = new ArrowBuffer[1];
            var structBuilt = 0;
            try
            {
                for (var i = 0; i < fields.Length; i++)
                {
                    fields[i] = _fields[i].ToArrowPooled(view.Children![i], ref rentals);
                }

                structBuffers[0] = view.NullCount == 0
                    ? ArrowBuffer.Empty
                    : rentals.CopyFrom(view.Validity.Span[..Validity.ByteCount(view.Length)]);
                structBuilt = 1;

                return new StructArray(
                    new ArrayData(
                        _arrowType, view.Length, view.NullCount, 0, structBuffers, fields.Select(f => f!.Data)));
            }
            catch
            {
                Release(structBuffers, structBuilt);
                DisposeAll(fields);
                throw;
            }
        }

        if (_kind == ColumnKind.List)
        {
            var child = Elements.ToArrowPooled(view.Child, ref rentals);

            var listBuffers = new ArrowBuffer[2];

            var listBuilt = 0;

            try
            {
                listBuffers[0] =
                    view.NullCount == 0
                        ? ArrowBuffer.Empty
                        : rentals.CopyFrom(view.Validity.Span[..Validity.ByteCount(view.Length)]);

                listBuilt = 1;
                listBuffers[1] = rentals.CopyFrom(view.Offsets.Span[..checked((view.Length + 1) * sizeof(int))]);
                listBuilt = 2;

                return new ListArray(
                    new ArrayData(_arrowType, view.Length, view.NullCount, 0, listBuffers, [child.Data]));
            }
            catch
            {
                Release(listBuffers, listBuilt);
                child.Dispose();
                throw;
            }
        }

        var buffers = new ArrowBuffer[_variable ? 3 : 2];

        var built = 0;

        try
        {
            buffers[0] = view.NullCount == 0
                ? ArrowBuffer.Empty
                : rentals.CopyFrom(view.Validity.Span[..Validity.ByteCount(view.Length)]);

            built = 1;

            if (_variable)
            {
                if (view.ViewBuffers is not null)
                {
                    throw new InvalidOperationException(
                        "A STRING_VIEW column must be normalised through "
                        + "ColumnCopier before classic Arrow STRING materialisation.");
                }

                if (_kind == ColumnKind.Utf8 &&
                    _stringTarget == StringLayouts.Utf8View)
                {
                    throw new InvalidOperationException(
                        "A STRING column declared as utf8view cannot be built directly from a "
                        + "classic view; append it through this copier instead (D244).");
                }

                buffers[1] = rentals.CopyFrom(view.Offsets.Span[..checked((view.Length + 1) * sizeof(int))]);
                built = 2;
                buffers[2] = rentals.CopyFrom(view.Values.Span);
                built = 3;
            }
            else if (_kind ==
                     ColumnKind.Boolean)
            {
                buffers[1] = PackBitsPooled(view, ref rentals);
                built = 2;
            }
            else
            {
                buffers[1] =
                    rentals.CopyFrom(
                        view.Values.Span.Slice(
                            view.Offset * _width,
                            checked(
                                view.Length *
                                _width)));

                built = 2;
            }

            return ArrowArrayFactory.BuildArray(
                new ArrayData(
                    _arrowType,
                    view.Length,
                    view.NullCount,
                    0,
                    buffers,
                    children: null));
        }
        catch
        {
            Release(buffers, built);

            throw;
        }
    }

    private ArrowBuffer PackBitsPooled(
        in ColumnView view,
        ref PooledBatchRentalCollector rentals)
    {
        var bytes = Validity.ByteCount(view.Length);

        if (bytes == 0)
            return ArrowBuffer.Empty;

        //
        // Use the copier's packed arena buffer as the final output
        // buffer rather than pack and then copy it.
        //
        _packed.Ensure(Math.Max(bytes, 1));

        var bits =
            _packed.Bytes.AsSpan(0,
                bytes);
        bits.Clear();

        for (var i = 0; i < view.Length; i++)
        {
            if (view.BoolAt(i))
            {
                BitUtility.SetBit(bits, i);
            }
        }

        return rentals.Adopt(ref _packed, bytes);
    }

    private static void Release(ArrowBuffer[] buffers, int built)
    {
        for (var i = 0; i < built; i++)
        {
            if (!buffers[i].IsEmpty)
            {
                buffers[i].Dispose();
            }
        }
    }

    /// <summary>Disposes the field arrays a failed STRUCT build had already made.</summary>
    private static void DisposeAll(IArrowArray?[] arrays)
    {
        foreach (var array in arrays)
        {
            array?.Dispose();
        }
    }

    /// <summary>Bit-packs a byte-per-row boolean into Arrow's representation.</summary>
    private ArrowBuffer PackBits(in ColumnView view, ExecutionArena? arena)
    {
        var bytes = Validity.ByteCount(view.Length);
        _packed.Ensure(Math.Max(bytes, 1));
        var bits = _packed.Bytes.AsSpan(0, bytes);
        bits.Clear();
        for (var i = 0; i < view.Length; i++)
        {
            if (view.BoolAt(i))
            {
                BitUtility.SetBit(bits, i);
            }
        }

        return Owned(bits, arena);
    }

    /// <summary>
    /// Copies a span into memory the batch that receives it owns. Under <c>Pooled</c> that is arena
    /// memory, released when the host disposes the batch; otherwise it is a GC array the host may
    /// keep for ever, wrapped by the public non-owning <see cref="ArrowBuffer"/> constructor
    /// (ADR 0007).
    /// </summary>
    private static ArrowBuffer Owned(ReadOnlySpan<byte> content, ExecutionArena? arena)
    {
        if (content.IsEmpty)
        {
            return ArrowBuffer.Empty;
        }

        if (arena is not null)
        {
            return arena.CreateBuffer(content);
        }

        var managed = new byte[content.Length];
        content.CopyTo(managed);
        return new ArrowBuffer(managed);
    }

    private void Copy(
        in ColumnView source,
        ReadOnlySpan<int> indices,
        int identityCount)
    {
        var identity =
            identityCount >= 0;

        var count =
            identity
                ? identityCount
                : indices.Length;

        if (count == 0)
            return;

        if (_kind == ColumnKind.Struct)
        {
            // D291: the struct's own validity, then every field at the same rows — a field of the
            // source seen through the source's offset, as Arrow aligns them.
            CopyValidity(source, indices, identity, _rows, count);
            _rows += count;
            for (var i = 0; i < _fields!.Length; i++)
            {
                var field = source.StructField(i);
                if (identity)
                {
                    _fields[i].Append(field, count);
                }
                else
                {
                    _fields[i].AppendGather(field, indices);
                }
            }

            return;
        }

        if (_kind == ColumnKind.List)
        {
            for (var i = 0; i < count; i++)
            {
                AppendListRow(
                    source,
                    identity
                        ? i
                        : indices[i]);
            }

            return;
        }

        var first =
            _rows;

        //
        // IMPORTANT:
        // Promotion consumes the rows currently in the copier, so it must
        // happen before _rows includes the incoming StringView rows.
        //
        // A copier with a declared target (D244) promotes on the target rather
        // than on what arrives: views are selected from the first row, so a
        // classic arrival is converted as it is appended, and a classic target
        // never promotes at all.
        //
        if (_kind == ColumnKind.Utf8 &&
            _stringStorage == StringStorage.Classic &&
            (source.IsUtf8View || _stringTarget == StringLayouts.Utf8View))
        {
            PromoteClassicToView();
        }
        else if (_kind == ColumnKind.Utf8 &&
                 _stringStorage == StringStorage.Unset &&
                 _stringTarget == StringLayouts.Utf8View)
        {
            SelectViewStorage(first);
        }

        CopyValidity(
            source,
            indices,
            identity,
            first,
            count);

        _rows += count;

        if (_kind == ColumnKind.Utf8)
        {
            if (source.IsUtf8View)
            {
                if (_stringTarget == StringLayouts.Utf8)
                {
                    // The one payload copy D244 permits, paid only by a host that asked for the
                    // classic layout over a column that reached the boundary as views.
                    CopyStringViewToClassic(
                        source,
                        indices,
                        identity,
                        count);

                    return;
                }

                CopyStringView(
                    source,
                    indices,
                    identity,
                    count);

                return;
            }

            if (_stringStorage == StringStorage.View)
            {
                CopyClassicUtf8ToView(
                    source,
                    indices,
                    identity,
                    count);

                return;
            }
        }

        if (_variable)
        {
            RequireClassicVariableStorage();

            if (identity)
            {
                CopyVariableIdentity(
                    source,
                    count);
            }
            else
            {
                CopyVariableGather(
                    source,
                    indices);
            }

            return;
        }

        EnsureValues(_rows, first);

        var dst =
            _values.Bytes.AsSpan(first * _width, count * _width);

        if (_kind == ColumnKind.Boolean)
        {
            for (var i = 0; i < count; i++)
            {
                dst[i] =
                    source.BoolAt(
                        identity
                            ? i
                            : indices[i])
                        ? (byte)1
                        : (byte)0;
            }

            return;
        }

        var src = source.RawLanes(_width);

        if (identity)
        {
            src[..(count * _width)]
                .CopyTo(dst);

            return;
        }

        CopyFixedGather(src, dst, indices);
    }

    private void CopyVariableIdentity(
        in ColumnView source,
        int count)
    {
        var sourceOffsets = source.OffsetLanes();

        var sourceData = source.VarData();

        var sourceStart = sourceOffsets[0];

        var sourceEnd = sourceOffsets[count];

        Debug.Assert(sourceStart >= 0 && sourceEnd >= sourceStart && sourceEnd <= sourceData.Length);

        var byteCount = sourceEnd - sourceStart;

        var destinationStart = _dataUsed;

        var requiredData = checked(destinationStart + byteCount);

        _data.EnsureKeeping(requiredData, destinationStart);

        if (byteCount != 0)
        {
            sourceData
                .Slice(sourceStart, byteCount)
                .CopyTo(_data.Bytes.AsSpan(destinationStart, byteCount));
        }

        EnsureOffsets(_offsetCursor + count);

        var destinationOffsets = OffsetLanes.Slice(_offsetCursor, count);

        var adjustment = destinationStart - sourceStart;

        //
        // Offset zero already exists in the destination.
        // Copy source offsets 1..count.
        //
        for (var i = 0; i < count; i++)
        {
            destinationOffsets[i] = sourceOffsets[i + 1] + adjustment;
        }

        _offsetCursor += count;

        _dataUsed = requiredData;
    }

    private void CopyVariableGather(
        in ColumnView source,
        ReadOnlySpan<int> indices)
    {
        var offsets = source.OffsetLanes();

        var sourceData = source.VarData();

        var count = indices.Length;

        var byteCount = 0;

        for (var i = 0; i < count; i++)
        {
            var row = indices[i];

            byteCount = checked(byteCount + offsets[row + 1] - offsets[row]);
        }

        var destinationStart = _dataUsed;

        var required = checked(destinationStart + byteCount);

        _data.EnsureKeeping(required, destinationStart);

        EnsureOffsets(_offsetCursor + count);

        var destination = _data.Bytes.AsSpan();

        var destinationOffsets = OffsetLanes.Slice(_offsetCursor, count);

        var write = destinationStart;

        var index = 0;

        while (index < count)
        {
            var firstIndex = index;

            var firstRow = indices[index];

            var lastRow = firstRow;

            //
            // Extend through physically consecutive source rows.
            //
            while (++index < count && indices[index] == lastRow + 1)
            {
                lastRow++;
            }

            var sourceStart = offsets[firstRow];

            var sourceEnd = offsets[lastRow + 1];

            var runLength = sourceEnd - sourceStart;

            //
            // One memmove for the whole run.
            //
            if (runLength != 0)
            {
                sourceData
                    .Slice(sourceStart, runLength)
                    .CopyTo(
                        destination.Slice(write, runLength));
            }

            //
            // Rebase each row's end offset into destination space.
            //
            for (var i = firstIndex; i < index; i++)
            {
                destinationOffsets[i] = write + offsets[indices[i] + 1] - sourceStart;
            }

            write += runLength;
        }

        _offsetCursor += count;

        _dataUsed = write;

        Debug.Assert(
            write == required);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CopyFixedGather(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        ReadOnlySpan<int> indices)
    {
        switch (_width)
        {
            case 1:
                CopyGather<byte>(
                    source,
                    destination,
                    indices);
                return;

            case 2:
                CopyGather<ushort>(
                    source,
                    destination,
                    indices);
                return;

            case 4:
                CopyGather<uint>(
                    source,
                    destination,
                    indices);
                return;

            case 8:
                CopyGather<ulong>(
                    source,
                    destination,
                    indices);
                return;

            case 16:
                CopyGather<UInt128>(
                    source,
                    destination,
                    indices);
                return;

            default:
                CopyFixedGatherFallback(
                    source,
                    destination,
                    indices);
                return;
        }
    }

    private void CopyFixedGatherFallback(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        ReadOnlySpan<int> indices)
    {
        for (var i = 0; i < indices.Length; i++)
        {
            source
                .Slice(
                    indices[i] * _width,
                    _width)
                .CopyTo(
                    destination.Slice(
                        i * _width,
                        _width));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyGather<T>(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        ReadOnlySpan<int> indices)
        where T : unmanaged
    {
        var src =
            MemoryMarshal.Cast<byte, T>(
                source);

        var dst =
            MemoryMarshal.Cast<byte, T>(
                destination);

        for (var i = 0; i < indices.Length; i++)
        {
            dst[i] =
                src[indices[i]];
        }
    }

    private const int StringViewWidth = 16;
    private const int StringViewInlineLength = 12;

    private void CopyStringView(
        in ColumnView source,
        ReadOnlySpan<int> indices,
        bool identity,
        int count)
    {
        Debug.Assert(
            source.ViewBuffers is not null);

        Debug.Assert(
            _stringStorage !=
            StringStorage.Classic);

        var first =
            _rows - count;

        var selectingView =
            _stringStorage ==
            StringStorage.Unset;

        _stringStorage =
            StringStorage.View;

        //
        // Acquire physical source views once.
        //
        var sourceViews =
            source.RawLanes(
                StringViewWidth);

        //
        // Make room for destination descriptors.
        //
        // If this is the first representation selected, preceding rows
        // can only be representation-neutral NULLs, so there are no
        // existing descriptor bytes to preserve.
        //
        _values.EnsureKeeping(
            Hinted(
                checked(
                    _rows *
                    StringViewWidth),
                (long)_capacityHint *
                StringViewWidth),
            selectingView
                ? 0
                : checked(
                    first *
                    StringViewWidth));

        if (selectingView &&
            first != 0)
        {
            _values.Bytes
                .AsSpan(
                    0,
                    checked(
                        first *
                        StringViewWidth))
                .Clear();
        }

        var destination =
            _values.Bytes.AsSpan(
                checked(
                    first *
                    StringViewWidth),
                checked(
                    count *
                    StringViewWidth));

        //
        // First preserve/gather the 16-byte descriptors.
        //
        if (identity)
        {
            sourceViews[
                    ..checked(
                        count *
                        StringViewWidth)]
                .CopyTo(
                    destination);
        }
        else
        {
            CopyGather<UInt128>(
                sourceViews,
                destination,
                indices);
        }

        //
        // No external buffers means every value is inline.
        //
        if (source.ViewBufferCount == 0)
            return;

        var sourceValidity =
            source.ValidityBits();

        var descriptors =
            MemoryMarshal.Cast<byte, int>(
                destination);

        //
        // Pass 1: determine exactly how much long-string payload
        // needs to become copier-owned.
        //
        var longBytes = 0;

        for (var i = 0;
             i < count;
             i++)
        {
            var sourceRow =
                identity
                    ? i
                    : indices[i];

            if (!sourceValidity.IsEmpty &&
                !BitUtility.GetBit(
                    sourceValidity,
                    source.Offset +
                    sourceRow))
            {
                continue;
            }

            var lane =
                i * 4;

            var length =
                descriptors[lane];

            if (length >
                StringViewInlineLength)
            {
                longBytes =
                    checked(
                        longBytes +
                        length);
            }
        }

        if (longBytes == 0)
            return;

        var destinationStart =
            _dataUsed;

        var required =
            checked(
                destinationStart +
                longBytes);

        //
        // Grow once, preserving payload owned from previous appends.
        //
        _data.EnsureKeeping(
            required,
            destinationStart);

        var ownedData =
            _data.Bytes.AsSpan();

        var sourceBuffers =
            source.ViewBuffers!;

        var sourceBufferCount =
            source.ViewBufferCount;

        var write =
            destinationStart;

        //
        // Pass 2: copy only selected long values and rewrite their
        // descriptors to reference copier-owned buffer zero.
        //
        for (var i = 0;
             i < count;
             i++)
        {
            var sourceRow =
                identity
                    ? i
                    : indices[i];

            if (!sourceValidity.IsEmpty &&
                !BitUtility.GetBit(
                    sourceValidity,
                    source.Offset +
                    sourceRow))
            {
                continue;
            }

            var lane =
                i * 4;

            var length =
                descriptors[lane];

            if (length <=
                StringViewInlineLength)
            {
                continue;
            }

            var sourceBufferIndex =
                descriptors[lane + 2];

            var sourceBufferOffset =
                descriptors[lane + 3];

            if ((uint)sourceBufferIndex >=
                (uint)sourceBufferCount)
            {
                throw new InvalidOperationException(
                    $"STRING_VIEW references buffer {sourceBufferIndex}, "
                    + $"but only {sourceBufferCount} variadic buffers exist.");
            }

            var sourceBuffer =
                sourceBuffers[
                    sourceBufferIndex].Span;

            if (sourceBufferOffset < 0 ||
                sourceBufferOffset >
                sourceBuffer.Length ||
                length >
                sourceBuffer.Length -
                sourceBufferOffset)
            {
                throw new InvalidOperationException(
                    "STRING_VIEW references bytes outside its backing buffer.");
            }

            sourceBuffer
                .Slice(
                    sourceBufferOffset,
                    length)
                .CopyTo(
                    ownedData.Slice(
                        write,
                        length));

            //
            // Prefix and length remain unchanged.
            //
            descriptors[lane + 2] =
                0;

            descriptors[lane + 3] =
                write;

            write +=
                length;
        }

        _dataUsed =
            write;

        Debug.Assert(
            write == required);
    }

    /// <summary>
    /// Selects view storage before anything has chosen a representation (D244). Rows appended
    /// before now can only be representation-neutral NULLs, and their lanes have to exist and be
    /// zero — an empty descriptor is a harmless physical value underneath a NULL.
    /// </summary>
    private void SelectViewStorage(
        int rows)
    {
        Debug.Assert(_kind == ColumnKind.Utf8);
        Debug.Assert(_stringStorage == StringStorage.Unset);

        _stringStorage =
            StringStorage.View;

        if (rows == 0)
            return;

        var byteCount =
            checked(rows * StringViewWidth);

        _values.EnsureKeeping(
            byteCount,
            keep: 0);

        _values.Bytes
            .AsSpan(0, byteCount)
            .Clear();
    }

    /// <summary>
    /// Views in, classic offsets and payload out (D244). The one payload copy the decision permits,
    /// and it happens only where a host asked for the classic layout over a column that reached the
    /// output boundary as views.
    /// </summary>
    private void CopyStringViewToClassic(
        in ColumnView source,
        ReadOnlySpan<int> indices,
        bool identity,
        int count)
    {
        Debug.Assert(_kind == ColumnKind.Utf8);
        Debug.Assert(source.ViewBuffers is not null);
        Debug.Assert(_stringTarget == StringLayouts.Utf8);

        RequireClassicVariableStorage();

        var descriptors =
            MemoryMarshal.Cast<byte, int>(
                source.RawLanes(
                    StringViewWidth));

        var validity =
            source.ValidityBits();

        //
        // Pass 1: the exact payload size, so the destination grows once.
        //
        var payload = 0;

        for (var i = 0; i < count; i++)
        {
            var row =
                identity
                    ? i
                    : indices[i];

            if (!validity.IsEmpty &&
                !BitUtility.GetBit(
                    validity,
                    source.Offset + row))
            {
                continue;
            }

            payload =
                checked(
                    payload +
                    descriptors[row * 4]);
        }

        var destinationStart =
            _dataUsed;

        var required =
            checked(
                destinationStart +
                payload);

        _data.EnsureKeeping(
            required,
            destinationStart);

        EnsureOffsets(
            _offsetCursor + count);

        var destination =
            _data.Bytes.AsSpan();

        var offsets =
            OffsetLanes;

        var write =
            destinationStart;

        //
        // Pass 2: one value at a time, inline or out of line — Utf8ViewMemory
        // knows which, and a NULL contributes no bytes and repeats the offset.
        //
        for (var i = 0; i < count; i++)
        {
            var row =
                identity
                    ? i
                    : indices[i];

            if (validity.IsEmpty ||
                BitUtility.GetBit(
                    validity,
                    source.Offset + row))
            {
                var value =
                    source.Utf8ViewMemory(row).Span;

                if (!value.IsEmpty)
                {
                    value.CopyTo(
                        destination.Slice(
                            write,
                            value.Length));

                    write += value.Length;
                }
            }

            offsets[_offsetCursor++] =
                write;
        }

        _dataUsed =
            write;

        Debug.Assert(
            write == required);
    }

    private void CopyClassicUtf8ToView(
        in ColumnView source,
        ReadOnlySpan<int> indices,
        bool identity,
        int count)
    {
        Debug.Assert(
            _kind == ColumnKind.Utf8);

        Debug.Assert(
            _stringStorage ==
            StringStorage.View);

        Debug.Assert(
            !source.IsUtf8View);

        var first =
            _rows - count;

        var sourceOffsets =
            source.OffsetLanes();

        var sourceData =
            source.VarData();

        var sourceValidity =
            source.ValidityBits();

        _values.EnsureKeeping(
            Hinted(
                checked(
                    _rows *
                    StringViewWidth),
                (long)_capacityHint *
                StringViewWidth),
            checked(
                first *
                StringViewWidth));

        var destination =
            _values.Bytes.AsSpan(
                checked(
                    first *
                    StringViewWidth),
                checked(
                    count *
                    StringViewWidth));

        //
        // Null lanes and unused inline bytes should be deterministic zero.
        //
        destination.Clear();

        var descriptors =
            MemoryMarshal.Cast<byte, int>(
                destination);

        var longBytes = 0;

        //
        // Pass 1:
        // encode length + inline values / prefixes and calculate external
        // payload size.
        //
        for (var i = 0; i < count; i++)
        {
            var sourceRow =
                identity
                    ? i
                    : indices[i];

            if (!sourceValidity.IsEmpty &&
                !BitUtility.GetBit(
                    sourceValidity,
                    source.Offset +
                    sourceRow))
            {
                continue;
            }

            var start =
                sourceOffsets[sourceRow];

            var length =
                sourceOffsets[sourceRow + 1] -
                start;

            Debug.Assert(start >= 0);
            Debug.Assert(length >= 0);
            Debug.Assert(
                start + length <=
                sourceData.Length);

            var lane =
                i * 4;

            descriptors[lane] =
                length;

            if (length <=
                StringViewInlineLength)
            {
                if (length != 0)
                {
                    sourceData
                        .Slice(
                            start,
                            length)
                        .CopyTo(
                            destination.Slice(
                                i * StringViewWidth +
                                sizeof(int),
                                length));
                }

                continue;
            }

            descriptors[lane + 1] =
                MemoryMarshal.Read<int>(
                    sourceData.Slice(
                        start,
                        sizeof(int)));

            longBytes =
                checked(
                    longBytes +
                    length);
        }

        if (longBytes == 0)
            return;

        var destinationStart =
            _dataUsed;

        var required =
            checked(
                destinationStart +
                longBytes);

        _data.EnsureKeeping(
            required,
            destinationStart);

        var ownedData =
            _data.Bytes.AsSpan();

        var write =
            destinationStart;

        //
        // Pass 2:
        // copy only external values and point every descriptor at our
        // single owned buffer zero.
        //
        for (var i = 0; i < count; i++)
        {
            var sourceRow =
                identity
                    ? i
                    : indices[i];

            if (!sourceValidity.IsEmpty &&
                !BitUtility.GetBit(
                    sourceValidity,
                    source.Offset +
                    sourceRow))
            {
                continue;
            }

            var lane =
                i * 4;

            var length =
                descriptors[lane];

            if (length <=
                StringViewInlineLength)
            {
                continue;
            }

            var start =
                sourceOffsets[sourceRow];

            sourceData
                .Slice(
                    start,
                    length)
                .CopyTo(
                    ownedData.Slice(
                        write,
                        length));

            //
            // bufferIndex == 0 because destination.Clear() established it.
            //
            descriptors[lane + 3] =
                write;

            write +=
                length;
        }

        _dataUsed =
            write;

        Debug.Assert(
            write == required);
    }

    private void EnsureViewBufferCapacity(
        int required)
    {
        if (_viewBuffers.Length >= required)
            return;

        var arena = _arena
                    ?? throw new InvalidOperationException(
                        "ColumnCopier has not acquired an arena.");

        var grown =
            arena.Rent<ReadOnlyMemory<byte>>(
                required);

        if (_viewBufferCount != 0)
        {
            _viewBuffers.AsSpan(0, _viewBufferCount).CopyTo(grown);
        }

        if (_viewBuffers.Length != 0)
        {
            //
            // Do not let the arena retain references to old string backing
            // stores while this array is sitting in its pool.
            //
            _viewBuffers.AsSpan(
                0, _viewBufferCount).Clear();
            arena.Return(_viewBuffers);
        }

        _viewBuffers = grown;
    }

    private void PrepareOwnedViewBuffers()
    {
        if (_viewBufferCount != 0)
        {
            _viewBuffers
                .AsSpan(
                    0,
                    _viewBufferCount)
                .Clear();

            _viewBufferCount =
                0;
        }

        if (_dataUsed == 0)
            return;

        EnsureViewBufferCapacity(1);

        _viewBuffers[0] =
            _data.Bytes.AsMemory(
                0,
                _dataUsed);

        _viewBufferCount =
            1;
    }

    private void CopyValidity(
        in ColumnView source,
        ReadOnlySpan<int> indices,
        bool identity,
        int first,
        int count)
    {
        var sourceBits =
            source.ValidityBits();

        //
        // Most common case: source has no NULLs and neither
        // does anything copied before it.
        //
        if (sourceBits.IsEmpty &&
            !_validityMaterialised)
        {
            return;
        }

        //
        // Once the destination has a bitmap, valid rows appended
        // subsequently must explicitly become 1.
        //
        if (sourceBits.IsEmpty)
        {
            EnsureValidity(
                first + count);

            var destination =
                _validity.Bytes.AsSpan();

            for (var i = 0;
                 i < count;
                 i++)
            {
                BitUtility.SetBit(
                    destination,
                    first + i);
            }

            return;
        }

        //
        // The source contains explicit validity. Materialising here
        // also marks all preceding implicitly-valid destination rows.
        //
        EnsureValidityMaterialised(
            first + count);

        var destinationBits =
            _validity.Bytes.AsSpan();

        for (var i = 0;
             i < count;
             i++)
        {
            var sourceRow =
                identity
                    ? i
                    : indices[i];

            var valid =
                BitUtility.GetBit(
                    sourceBits,
                    source.Offset +
                    sourceRow);

            if (!valid)
                _nulls++;

            WriteValidity(
                destinationBits,
                first + i,
                valid);
        }
    }

    private ReadOnlyMemory<byte> ValidityMemory() =>
        _nulls == 0 ? default : _validity.Bytes.AsMemory(0, Validity.ByteCount(Math.Max(_rows, 1)));

    private Span<int> OffsetLanes => MemoryMarshal.Cast<byte, int>(_offsets.Bytes.AsSpan());

    private void EnsureValues(int rows, int keepRows) =>
        _values.EnsureKeeping(
            Hinted(rows * _width, (long)_capacityHint * _width), keepRows * _width);

    /// <summary>
    /// The larger of what this append needs and what the plan's row estimate asks for (D258.3).
    /// Growth still doubles from whatever comes back, so a hint is only ever the first size; a
    /// reservation past <see cref="MaxHintedBytes"/> is declined rather than risk the buffer's own
    /// doubling leaving an <see cref="int"/>.
    /// </summary>
    private int Hinted(int needed, long hintBytes) =>
        _capacityHint != 0 && hintBytes > needed && hintBytes <= MaxHintedBytes
            ? (int)hintBytes
            : needed;

    private const int MaxHintedBytes = int.MaxValue / 2;

    private void EnsureValidity(int rows)
    {
        var bytes = Hinted(
            Validity.ByteCount(Math.Max(rows, 1)), Validity.ByteCount(Math.Max(_capacityHint, 1)));
        var before = _validity.Capacity;
        if (_validity.EnsureKeeping(bytes, Math.Min(before, bytes)))
        {
            _validity.Bytes.AsSpan(before).Clear();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RequireClassicVariableStorage()
    {
        if (_kind != ColumnKind.Utf8)
            return;

        if (_stringStorage == StringStorage.View)
        {
            throw new InvalidOperationException(
                "Cannot append classic STRING storage after STRING_VIEW "
                + "storage in the same ColumnCopier.");
        }

        if (_stringStorage == StringStorage.Unset)
        {
            _stringStorage =
                StringStorage.Classic;
        }
    }

    private int BuildNullStringViews()
    {
        Debug.Assert(
            _kind == ColumnKind.Utf8);

        Debug.Assert(
            _stringStorage ==
            StringStorage.Unset);

        //
        // If no representation has ever been selected, every row
        // necessarily has to be NULL.
        //
        Debug.Assert(
            _nulls == _rows);

        var byteCount =
            checked(
                _rows *
                StringViewWidth);

        if (byteCount == 0)
            return 0;

        _values.EnsureKeeping(
            byteCount,
            keep: 0);

        //
        // A zero descriptor is a harmless physical value underneath NULL.
        //
        _values.Bytes
            .AsSpan(
                0,
                byteCount)
            .Clear();

        return byteCount;
    }

    private IArrowArray FinishEmptyStringViewPooled(
        ref PooledBatchRentalCollector rentals)
    {
        var viewByteCount =
            BuildNullStringViews();

        var buffers =
            new ArrowBuffer[2];

        var built = 0;

        try
        {
            buffers[0] =
                _nulls == 0
                    ? ArrowBuffer.Empty
                    : rentals.Adopt(
                        ref _validity,
                        Validity.ByteCount(_rows));

            built = 1;

            buffers[1] =
                viewByteCount == 0
                    ? ArrowBuffer.Empty
                    : rentals.Adopt(
                        ref _values,
                        viewByteCount);

            built = 2;

            return new StringViewArray(
                new ArrayData(
                    StringViewType.Default,
                    _rows,
                    _nulls,
                    0,
                    buffers,
                    children: null));
        }
        catch
        {
            Release(buffers, built);
            throw;
        }
    }

    private IArrowArray FinishEmptyStringViewManaged()
    {
        var viewByteCount =
            BuildNullStringViews();

        var buffers =
            new ArrowBuffer[2];

        var built = 0;

        try
        {
            buffers[0] =
                _nulls == 0
                    ? ArrowBuffer.Empty
                    : DetachManaged(
                        ref _validity,
                        Validity.ByteCount(_rows));

            built = 1;

            buffers[1] =
                viewByteCount == 0
                    ? ArrowBuffer.Empty
                    : DetachManaged(
                        ref _values,
                        viewByteCount);

            built = 2;

            return new StringViewArray(
                new ArrayData(
                    StringViewType.Default,
                    _rows,
                    _nulls,
                    0,
                    buffers,
                    children: null));
        }
        catch
        {
            Release(buffers, built);
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureValidityMaterialised(
        int rows)
    {
        EnsureValidity(rows);

        if (_validityMaterialised)
            return;

        //
        // All existing and provisionally requested rows were
        // implicitly valid before materialisation.
        //
        var byteCount = Validity.ByteCount(Math.Max(rows, 1));

        _validity.Bytes.AsSpan(0, byteCount).Fill(0xff);
        _validityMaterialised = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureOffsets(int entries)
    {
        Debug.Assert(
            entries >= _offsetCursor);

        // One offset per row and a leading zero, so the hint is a row more than the estimate:
        // asking for exactly the estimate would force one last doubling on the final row.
        _offsets.EnsureKeeping(
            Hinted(entries * sizeof(int), ((long)_capacityHint + 1) * sizeof(int)),
            _offsetCursor * sizeof(int));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteValidity(Span<byte> bits, int row, bool valid)
    {
        if (valid)
        {
            BitUtility.SetBit(bits, row);
        }
        else
        {
            BitUtility.ClearBit(bits, row);
        }
    }

    private void SetValidity(int row, bool valid)
    {
        if (!_validityMaterialised)
        {
            //
            // Valid is still the implicit state.
            //
            if (valid) return;

            EnsureValidityMaterialised(row + 1);
        }
        else
        {
            EnsureValidity(row + 1);
        }

        WriteValidity(_validity.Bytes.AsSpan(), row, valid);
    }

    private void AppendOffset(int value)
    {
        EnsureOffsets(_offsetCursor + 1);
        OffsetLanes[_offsetCursor++] = value;
    }

    private void AppendData(ReadOnlySpan<byte> bytes)
    {
        _data.EnsureKeeping(_dataUsed + bytes.Length, _dataUsed);
        bytes.CopyTo(_data.Bytes.AsSpan(_dataUsed));
        _dataUsed += bytes.Length;
    }
}