using System.Linq.Expressions;
using System.Reflection;

namespace Chalk.Sources.Poco;

/// <summary>
/// One column's loop over a chunk of rows: reads <c>rows[start + i]</c>, writes <c>values[i]</c> and,
/// when the column is nullable, <c>valid[i]</c>. The loop lives <em>inside</em> the delegate (§5.3),
/// so a scan pays one call per column per batch, not one per row.
/// </summary>
/// <remarks>
/// The value array is a <c>TStorage[]</c> and never a <see cref="Span{T}"/>: expression trees cannot
/// contain ref structs (D11). Packing into Arrow's buffers happens afterwards, outside the tree.
/// </remarks>
internal delegate void PocoChunkLoop<TList, TStorage>(
    TList rows, int start, int count, TStorage[] values, byte[]? valid);

/// <summary>
/// The same loop over <c>rows[positions[start + i]]</c> instead of <c>rows[start + i]</c>: what an
/// index lookup gathers with (M2, §5). Compiled on first use, so a table nobody looks up in pays
/// nothing for it.
/// </summary>
internal delegate void PocoGatherLoop<TList, TStorage>(
    TList rows, int[] positions, int start, int count, TStorage[] values, byte[]? valid);

/// <summary>
/// The same loop compiled three times, once per collection shape. <c>T[]</c> and <c>List&lt;T&gt;</c>
/// index directly; everything else goes through <see cref="IReadOnlyList{T}"/> (A2). The one type
/// test costs a branch per chunk, not per row.
/// </summary>
internal sealed class PocoChunkLoopSet<T, TStorage>
{
    private readonly PocoChunkLoop<T[], TStorage> _array;
    private readonly PocoChunkLoop<List<T>, TStorage> _list;
    private readonly PocoChunkLoop<IReadOnlyList<T>, TStorage> _general;
    private readonly Lazy<GatherLoops> _gather;

    public PocoChunkLoopSet(
        PocoChunkLoop<T[], TStorage> array,
        PocoChunkLoop<List<T>, TStorage> list,
        PocoChunkLoop<IReadOnlyList<T>, TStorage> general,
        Func<GatherLoops> gather)
    {
        _array = array;
        _list = list;
        _general = general;
        _gather = new Lazy<GatherLoops>(gather);
    }

    /// <summary>The gather variant of the three loops.</summary>
    internal sealed record GatherLoops(
        PocoGatherLoop<T[], TStorage> Array,
        PocoGatherLoop<List<T>, TStorage> List,
        PocoGatherLoop<IReadOnlyList<T>, TStorage> General);

    public void Run(IReadOnlyList<T> rows, int start, int count, TStorage[] values, byte[]? valid)
    {
        switch (rows)
        {
            case T[] array:
                _array(array, start, count, values, valid);
                break;

            // An appended table's rows are an array Chalk owns with room to spare (D260 §3); a scan
            // only ever walks the rows its snapshot holds, so the array loop is the right one.
            case PocoAppendedRows<T> appended:
                _array(appended.Buffer, start, count, values, valid);
                break;
            case List<T> list:
                _list(list, start, count, values, valid);
                break;
            default:
                _general(rows, start, count, values, valid);
                break;
        }
    }

    public void RunGather(
        IReadOnlyList<T> rows, int[] positions, int start, int count, TStorage[] values, byte[]? valid)
    {
        var loops = _gather.Value;
        switch (rows)
        {
            case T[] array:
                loops.Array(array, positions, start, count, values, valid);
                break;
            case PocoAppendedRows<T> appended:
                loops.Array(appended.Buffer, positions, start, count, values, valid);
                break;
            case List<T> list:
                loops.List(list, positions, start, count, values, valid);
                break;
            default:
                loops.General(rows, positions, start, count, values, valid);
                break;
        }
    }
}

/// <summary>How one column's value is reached from a row, before any Arrow encoding.</summary>
internal sealed class PocoValueBinding
{
    /// <summary>The row parameter <see cref="Value"/> is written against.</summary>
    public required ParameterExpression Row { get; init; }

    /// <summary>The member access or projection body. May be a <see cref="Nullable{T}"/> or a reference type.</summary>
    public required Expression Value { get; init; }

    /// <summary>Whether a null check has to be emitted at all (§5.2 nullability rules).</summary>
    public required bool CanBeNull { get; init; }

    /// <summary>Maps the unwrapped, non-null value to the storage element type.</summary>
    public required Func<Expression, Expression> ToStorage { get; init; }
}

/// <summary>Builds the chunk loops, and the boxing accessor that verification (D17) compares with.</summary>
internal static class PocoChunkCompiler
{
    public static PocoChunkLoopSet<T, TStorage> Compile<T, TStorage>(
        PocoValueBinding binding, bool nullable, string sourceId, string table, string column)
    {
        return new PocoChunkLoopSet<T, TStorage>(
            CompileLoop<T[], TStorage>(
                binding, nullable, sourceId, table, column, static (rows, i) => Expression.ArrayAccess(rows, i)),
            CompileLoop<List<T>, TStorage>(
                binding, nullable, sourceId, table, column, static (rows, i) => Expression.Property(rows, Indexer<List<T>>(), i)),
            CompileLoop<IReadOnlyList<T>, TStorage>(
                binding, nullable, sourceId, table, column, static (rows, i) => Expression.Property(rows, Indexer<IReadOnlyList<T>>(), i)),
            () => new PocoChunkLoopSet<T, TStorage>.GatherLoops(
                CompileGatherLoop<T[], TStorage>(
                    binding, nullable, sourceId, table, column, static (rows, i) => Expression.ArrayAccess(rows, i)),
                CompileGatherLoop<List<T>, TStorage>(
                    binding, nullable, sourceId, table, column, static (rows, i) => Expression.Property(rows, Indexer<List<T>>(), i)),
                CompileGatherLoop<IReadOnlyList<T>, TStorage>(
                    binding, nullable, sourceId, table, column, static (rows, i) => Expression.Property(rows, Indexer<IReadOnlyList<T>>(), i))));
    }

    /// <summary>
    /// The column's value as an object, or null. Verification (§5.4) compares what the scan will emit
    /// rather than the raw member, so a collation over an enum column is checked in the STRING order
    /// the planner will see, not in CLR enum order. Boxing is fine here: this runs once, at
    /// <c>Build()</c>, never on the scan path.
    /// </summary>
    public static Func<T, object?> CompileLogicalAccessor<T>(PocoValueBinding binding)
    {
        Expression body;
        if (!binding.CanBeNull)
        {
            body = Expression.Convert(binding.ToStorage(binding.Value), typeof(object));
        }
        else
        {
            var local = Expression.Variable(binding.Value.Type, "v");
            body = Expression.Block(
                typeof(object),
                new[] { local },
                Expression.Assign(local, binding.Value),
                Expression.Condition(
                    HasValue(local),
                    Expression.Convert(binding.ToStorage(Unwrap(local)), typeof(object)),
                    Expression.Constant(null, typeof(object))));
        }

        return Expression.Lambda<Func<T, object?>>(body, binding.Row).Compile();
    }

    private static PocoChunkLoop<TList, TStorage> CompileLoop<TList, TStorage>(
        PocoValueBinding binding,
        bool nullable,
        string sourceId,
        string table,
        string column,
        Func<Expression, Expression, Expression> index)
    {
        var rows = Expression.Parameter(typeof(TList), "rows");
        var start = Expression.Parameter(typeof(int), "start");
        var count = Expression.Parameter(typeof(int), "count");
        var values = Expression.Parameter(typeof(TStorage[]), "values");
        var valid = Expression.Parameter(typeof(byte[]), "valid");

        var i = Expression.Variable(typeof(int), "i");
        var exit = Expression.Label("exit");

        var body = Expression.Block(
            typeof(void),
            new[] { binding.Row },
            Expression.Assign(binding.Row, index(rows, Expression.Add(start, i))),
            WriteSlot<TStorage>(binding, i, values, valid, nullable, sourceId, table, column),
            Expression.PostIncrementAssign(i));

        var loop = Expression.Loop(
            Expression.IfThenElse(Expression.LessThan(i, count), body, Expression.Break(exit)),
            exit);

        var lambda = Expression.Lambda<PocoChunkLoop<TList, TStorage>>(
            Expression.Block(
                typeof(void),
                new[] { i },
                Expression.Assign(i, Expression.Constant(0)),
                loop),
            rows,
            start,
            count,
            values,
            valid);

        return lambda.Compile();
    }

    /// <summary>
    /// The gather twin of <see cref="CompileLoop{TList,TStorage}"/>: the same body, reading
    /// <c>rows[positions[start + i]]</c>. Written out rather than folded into one method because the
    /// two delegates have different shapes and expression trees cannot be parameterised over that.
    /// </summary>
    private static PocoGatherLoop<TList, TStorage> CompileGatherLoop<TList, TStorage>(
        PocoValueBinding binding,
        bool nullable,
        string sourceId,
        string table,
        string column,
        Func<Expression, Expression, Expression> index)
    {
        var rows = Expression.Parameter(typeof(TList), "rows");
        var positions = Expression.Parameter(typeof(int[]), "positions");
        var start = Expression.Parameter(typeof(int), "start");
        var count = Expression.Parameter(typeof(int), "count");
        var values = Expression.Parameter(typeof(TStorage[]), "values");
        var valid = Expression.Parameter(typeof(byte[]), "valid");

        var i = Expression.Variable(typeof(int), "i");
        var exit = Expression.Label("exit");

        var body = Expression.Block(
            typeof(void),
            new[] { binding.Row },
            Expression.Assign(
                binding.Row,
                index(rows, Expression.ArrayIndex(positions, Expression.Add(start, i)))),
            WriteSlot<TStorage>(binding, i, values, valid, nullable, sourceId, table, column),
            Expression.PostIncrementAssign(i));

        var loop = Expression.Loop(
            Expression.IfThenElse(Expression.LessThan(i, count), body, Expression.Break(exit)),
            exit);

        return Expression.Lambda<PocoGatherLoop<TList, TStorage>>(
            Expression.Block(
                typeof(void),
                new[] { i },
                Expression.Assign(i, Expression.Constant(0)),
                loop),
            rows,
            positions,
            start,
            count,
            values,
            valid).Compile();
    }

    private static Expression WriteSlot<TStorage>(
        PocoValueBinding binding,
        ParameterExpression i,
        ParameterExpression values,
        ParameterExpression valid,
        bool nullable,
        string sourceId,
        string table,
        string column)
    {
        var slot = Expression.ArrayAccess(values, i);

        if (!binding.CanBeNull)
        {
            var write = Expression.Assign(slot, binding.ToStorage(binding.Value));
            return nullable
                ? Expression.Block(typeof(void), write, SetValid(valid, i, 1))
                : Expression.Block(typeof(void), write);
        }

        var local = Expression.Variable(binding.Value.Type, "v");

        var present = nullable
            ? Expression.Block(
                typeof(void),
                Expression.Assign(slot, binding.ToStorage(Unwrap(local))),
                SetValid(valid, i, 1))
            : Expression.Block(typeof(void), Expression.Assign(slot, binding.ToStorage(Unwrap(local))));

        var absent = nullable
            ? Expression.Block(
                typeof(void),
                Expression.Assign(slot, Expression.Default(typeof(TStorage))),
                SetValid(valid, i, 0))
            : Expression.Block(
                typeof(void),
                Expression.Call(
                    ThrowUnexpectedNullMethod,
                    Expression.Constant(sourceId),
                    Expression.Constant(table),
                    Expression.Constant(column)));

        return Expression.Block(
            typeof(void),
            new[] { local },
            Expression.Assign(local, binding.Value),
            Expression.IfThenElse(HasValue(local), present, absent));
    }

    private static Expression SetValid(ParameterExpression valid, ParameterExpression i, byte value) =>
        Expression.Assign(Expression.ArrayAccess(valid, i), Expression.Constant(value));

    private static Expression HasValue(Expression value) =>
        Nullable.GetUnderlyingType(value.Type) is not null
            ? Expression.Property(value, "HasValue")
            : Expression.ReferenceNotEqual(value, Expression.Constant(null, value.Type));

    private static Expression Unwrap(Expression value) =>
        Nullable.GetUnderlyingType(value.Type) is not null
            ? Expression.Call(value, value.Type.GetMethod("GetValueOrDefault", Type.EmptyTypes)!)
            : value;

    private static PropertyInfo Indexer<TList>() =>
        typeof(TList).GetProperty("Item", new[] { typeof(int) })
        ?? throw new InvalidOperationException($"{typeof(TList)} has no int indexer.");

    private static readonly MethodInfo ThrowUnexpectedNullMethod =
        typeof(PocoConvert).GetMethod(nameof(PocoConvert.ThrowUnexpectedNull))!;
}
