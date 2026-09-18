using Apache.Arrow;
using Apache.Arrow.Types;
using Chalk.Catalog;
using Chalk.Execution.Memory;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Operators;

/// <summary>
/// The host output boundary: one Arrow batch per pipeline batch, matching the declared schema
/// exactly — a STRING column included (D244).
/// </summary>
/// <remarks>
/// The declared layout is read back off <see cref="ArrowSchema"/> rather than plumbed separately, so
/// "the schema equals the arrays" is true by construction rather than by two things agreeing. What
/// the declaration then decides is how much work a column costs: a column that arrives in the
/// declared layout is published as it stands wherever its memory can outlive the pipeline, and only
/// a column that arrives in the other one is converted.
/// </remarks>
internal sealed class OutputMaterialiser
{
    private const int StackColumnLimit = 64;

    private readonly ArrowSchema _schema;
    private readonly ColumnCopier[] _copiers;
    private readonly StringLayouts[] _strings;
    private readonly OperatorContext _context;
    private readonly bool _pooled;

    public OutputMaterialiser(
        OperatorContext context,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> types,
        bool pooled)
    {
        ArgumentNullException.ThrowIfNull(
            context);

        ArgumentNullException.ThrowIfNull(
            schema);

        ArgumentNullException.ThrowIfNull(
            types);

        _context = context;
        _schema = schema;
        _pooled = pooled;

        _strings =
            new StringLayouts[types.Count];

        for (var i = 0; i < _strings.Length; i++)
        {
            _strings[i] =
                Declared(
                    schema.FieldsList[i].DataType);
        }

        _copiers =
            new ColumnCopier[types.Count];

        for (var i = 0; i < _copiers.Length; i++)
        {
            _copiers[i] =
                new ColumnCopier(
                    types[i],
                    _strings[i]);
        }
    }

    /// <summary>
    /// The STRING layout a declared field names, a LIST's element included. A field that holds no
    /// STRING answers <see cref="StringLayouts.Utf8"/>, which nothing then reads.
    /// </summary>
    private static StringLayouts Declared(
        IArrowType type) =>
        type switch
        {
            StringViewType => StringLayouts.Utf8View,
            ListType list => Declared(list.ValueDataType),
            _ => StringLayouts.Utf8,
        };

    public bool Pooled =>
        _pooled;

    public RecordBatch Materialise(
        ColumnarBatch batch)
    {
        ArgumentNullException.ThrowIfNull(
            batch);

        if (batch.ColumnCount !=
            _copiers.Length)
        {
            throw new InvalidOperationException(
                $"Output batch contains {batch.ColumnCount} columns; "
                + $"the compiled output expects {_copiers.Length}.");
        }

        return _pooled
            ? MaterialisePooled(batch)
            : MaterialiseManaged(batch);
    }

    private RecordBatch MaterialisePooled(
        ColumnarBatch batch)
    {
        var arrays =
            new IArrowArray[_copiers.Length];

        //
        // A UTF8 column normally needs validity + offsets + payload.
        // The collector grows from ArrayPool if nested LISTs require more.
        //
        var rentals =
            new PooledBatchRentalCollector(
                _context.Arena,
                Math.Max(
                    8,
                    _copiers.Length * 3));

        try
        {
            if (batch.HasSelection)
            {
                //
                // Selection changes the logical rows, so one semantic gather
                // is unavoidable. The copier's gather buffers themselves
                // become the Arrow backing; FinishPooled performs no copy.
                //
                for (var i = 0; i < arrays.Length; i++)
                {
                    var copier = _copiers[i];
                    copier.Begin();
                    copier.AppendBatch(batch, i);

                    arrays[i] = copier.FinishPooled(ref rentals);
                }
            }
            else
            {
                //
                // No selection: avoid the intermediate ColumnCopier whenever
                // the view can be consumed directly.
                //
                for (var i = 0; i < arrays.Length; i++)
                {
                    var copier =
                        _copiers[i];

                    var view = batch.Column(i);

                    if (CanMaterialiseDirect(view, _strings[i]))
                    {
                        arrays[i] = copier.ToArrowPooled(view, ref rentals);
                        continue;
                    }

                    copier.Begin();
                    copier.AppendBatch(batch, i);

                    arrays[i] = copier.FinishPooled(ref rentals);
                }
            }

            //
            // Construct the batch while the collector still owns the rental
            // set. If construction throws, the catch below safely returns all
            // adopted arrays.
            //
            var result =
                new PooledRecordBatch(
                    _schema,
                    arrays,
                    batch.Count,
                    _context.Arena,
                    rentals.Rentals,
                    rentals.Count);

            //
            // PooledRecordBatch construction succeeded. Ownership of the
            // bookkeeping array and every adopted arena rental moves to it.
            //
            rentals.RelinquishToBatch();

            return result;
        }
        catch
        {
            //
            // Arrow buffers are non-owning in this path, but dispose their
            // wrapper graph before returning the backing arrays.
            //
            DisposeArrays(
                arrays);

            rentals.Dispose();

            throw;
        }
    }

    private RecordBatch MaterialiseManaged(
        ColumnarBatch batch)
    {
        var arrays =
            new IArrowArray[_copiers.Length];

        try
        {
            if (batch.HasSelection)
            {
                for (var i = 0;
                     i < arrays.Length;
                     i++)
                {
                    var copier =
                        _copiers[i];

                    copier.Begin();

                    copier.AppendBatch(
                        batch,
                        i);

                    arrays[i] =
                        copier.FinishManaged();
                }

                return new RecordBatch(
                    _schema,
                    arrays,
                    batch.Count);
            }

            Span<byte> publish =
                arrays.Length <=
                StackColumnLimit
                    ? stackalloc byte[
                        arrays.Length]
                    : new byte[
                        arrays.Length];

            publish.Clear();

            //
            // At most one output can destructively take a producer's
            // managed backing storage.
            //
            for (var i = 0;
                 i < arrays.Length;
                 i++)
            {
                var view =
                    batch.Column(i);

                if (!CanPublishManaged(view, _strings[i]) ||
                    view.ManagedPublisher
                        is not { } publisher ||
                    !publisher.CanPublish(view))
                {
                    continue;
                }

                var duplicate = false;

                for (var j = 0;
                     j < i;
                     j++)
                {
                    if (publish[j] != 0 &&
                        ReferenceEquals(
                            publisher,
                            batch.Column(j)
                                .ManagedPublisher))
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                {
                    publish[i] = 1;
                }
            }

            //
            // Finish every reader before destructive managed publication.
            //
            for (var i = 0;
                 i < arrays.Length;
                 i++)
            {
                if (publish[i] != 0)
                    continue;

                var view =
                    batch.Column(i);

                var copier = _copiers[i];

                if (CanMaterialiseDirect(
                        view,
                        _strings[i]))
                {
                    arrays[i] = copier.ToArrow(view, arena: null);

                    continue;
                }

                copier.Begin();

                copier.AppendBatch(batch, i);

                arrays[i] = copier.FinishManaged();
            }

            //
            // Destructive ownership transfers last.
            //
            for (var i = 0;
                 i < arrays.Length;
                 i++)
            {
                if (publish[i] == 0)
                    continue;

                var view =
                    batch.Column(i);

                var publisher =
                    view.ManagedPublisher!;

                if (!publisher.CanPublish(
                        view))
                {
                    throw new InvalidOperationException(
                        "A column became unpublishable after "
                        + "publication preflight.");
                }

                arrays[i] =
                    AsDeclared(
                        publisher.PublishManaged(
                            view),
                        _strings[i]);
            }

            return new RecordBatch(
                _schema,
                arrays,
                batch.Count);
        }
        catch
        {
            DisposeArrays(
                arrays);

            throw;
        }
    }

    /// <summary>
    /// Whether this column can be turned into its Arrow array without a pass through a copier. For
    /// STRING that is exactly the case where the arriving layout is the declared one and the direct
    /// builder can produce it: classic in, classic declared (D244). Everything else — views in,
    /// views declared with no owner to hand them over, or the two disagreeing — goes through the
    /// copier, which is the one place that converts.
    /// </summary>
    private static bool CanMaterialiseDirect(
        in ColumnView view,
        StringLayouts declared)
    {
        if (view.Offset != 0 || view.NullCount < 0)
        {
            return false;
        }

        if (ColumnKinds.Of(view.Type) == ColumnKind.Utf8)
        {
            return declared == StringLayouts.Utf8 && !view.IsUtf8View;
        }

        if (view.Children is not { Length: > 0 })
        {
            return true;
        }

        foreach (var child in view.Children)
        {
            if (!CanMaterialiseDirect(child, declared))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether this column's producer may hand its own memory to the host (§6.1). A publisher
    /// relinquishes classic buffers — offsets and payload — so a STRING column qualifies whenever it
    /// arrives classic, whichever layout was declared: under <c>utf8</c> the published array is the
    /// answer, and under <c>utf8view</c> the views are built over the very buffers it gave up, which
    /// moves no payload byte.
    /// </summary>
    private static bool CanPublishManaged(
        in ColumnView view,
        StringLayouts declared)
    {
        if (ColumnKinds.Of(view.Type) == ColumnKind.Utf8)
        {
            return view is { Offset: 0, NullCount: >= 0, IsUtf8View: false };
        }

        return CanMaterialiseDirect(view, declared);
    }

    /// <summary>
    /// A published array in the declared layout. Only a classic STRING array is ever re-described,
    /// and re-describing it copies nothing but the sixteen-byte view lanes.
    /// </summary>
    private static IArrowArray AsDeclared(
        IArrowArray published,
        StringLayouts declared)
    {
        if (declared != StringLayouts.Utf8View)
        {
            return published;
        }

        switch (published)
        {
            case StringViewArray already:
                return already;

            case StringArray classic:
                try
                {
                    return StringViewArrays.OverClassic(classic);
                }
                catch
                {
                    classic.Dispose();
                    throw;
                }

            default:
                published.Dispose();
                throw new InvalidOperationException(
                    $"a column declared utf8view was published as {published.GetType().Name}.");
        }
    }

    private static void DisposeArrays(
        IArrowArray[] arrays)
    {
        for (var i = 0;
             i < arrays.Length;
             i++)
        {
            arrays[i]?.Dispose();
        }
    }
}