using System.Linq.Expressions;

namespace Chalk.Sources.Poco;

/// <summary>
/// Where each key of a keyed table stands among the rows of one of its snapshots (D367): what a
/// refresh of upserts and deletes finds its rows by, rather than by reading the key of every row.
/// </summary>
/// <remarks>
/// <para>
/// Built from the first snapshot a keyed refresh meets, and moved on by each refresh the table
/// commits. A refresh reads it while it is prepared and changes it only when it commits, so a
/// refresh that is not committed leaves it describing the snapshot the table still serves. A snapshot
/// it does not describe, such as one a replacement made, builds it again.
/// </para>
/// <para>
/// A key leads to an entry, and an entry to a position. A delete moves every later row up by one, so
/// moving them is a pass over an array of integers rather than over the keys. A key of one column of
/// a value type is held as itself; any other as the row's key values.
/// </para>
/// </remarks>
internal sealed class PocoKeyPositions<T>
{
    private readonly KeyIndex _keys;
    private readonly Stack<int> _free = new();
    private int[] _positionOf = [];
    private int[] _entryAt = [];
    private int _entries;
    private int _count;
    private long _describes = -1;

    public PocoKeyPositions(PocoChanges<T> keyed) => _keys = KeyIndex.For(keyed);

    /// <summary>Whether this says where the keys of <paramref name="snapshot"/>'s rows stand.</summary>
    public bool Describes(PocoTableSnapshot<T> snapshot) => _describes == snapshot.Sequence;

    /// <summary>Builds this over <paramref name="snapshot"/>'s rows, one entry for each.</summary>
    public void Build(PocoTableSnapshot<T> snapshot)
    {
        var rows = snapshot.Rows
            ?? throw new InvalidOperationException("a keyed table's rows have positions.");
        var count = snapshot.RowCount;
        _keys.Clear(count);
        _free.Clear();
        _positionOf = new int[Room(count)];
        _entryAt = new int[Room(count)];
        for (var i = 0; i < count; i++)
        {
            _keys.Add(rows[i], i);
            _positionOf[i] = i;
            _entryAt[i] = i;
        }

        _entries = count;
        _count = count;
        _describes = snapshot.Sequence;
    }

    /// <summary>The position of the row whose key <paramref name="row"/> has, or -1 for none.</summary>
    public int PositionOf(T row) => _keys.TryGet(row, out var entry) ? _positionOf[entry] : -1;

    /// <summary>
    /// Moves this on to <paramref name="next"/>, which <paramref name="change"/> made from the snapshot
    /// this describes: the rows it deleted gone, the rows it replaced keeping their keys, and the rows
    /// it appended after the rest.
    /// </summary>
    public void Apply(PocoTableSnapshot<T> next, PocoKeyedChange<T> change)
    {
        var deleted = change.Deleted;
        if (deleted.Length > 0)
        {
            foreach (var row in change.DeletedRows)
            {
                if (_keys.Remove(row, out var entry))
                {
                    _free.Push(entry);
                }
            }

            // Every row after the first deleted one moves up by the deleted rows before it.
            var write = deleted[0];
            var skip = 0;
            for (var read = deleted[0]; read < _count; read++)
            {
                if (skip < deleted.Length && deleted[skip] == read)
                {
                    skip++;
                    continue;
                }

                var entry = _entryAt[read];
                _entryAt[write] = entry;
                _positionOf[entry] = write;
                write++;
            }

            _count = write;
        }

        foreach (var row in change.Appended)
        {
            var entry = _free.Count > 0 ? _free.Pop() : _entries++;
            if (entry >= _positionOf.Length)
            {
                Array.Resize(ref _positionOf, Room(entry + 1));
            }

            if (_count >= _entryAt.Length)
            {
                Array.Resize(ref _entryAt, Room(_count + 1));
            }

            _keys.Add(row, entry);
            _positionOf[entry] = _count;
            _entryAt[_count] = entry;
            _count++;
        }

        _describes = next.Sequence;
    }

    /// <summary>Room for <paramref name="count"/>, and an eighth more for the rows still to come.</summary>
    private static int Room(int count) => (int)Math.Min(Array.MaxLength, count + Math.Max(16L, count / 8L));

    /// <summary>A key, read from a row, to the entry that says where the row stands.</summary>
    private abstract class KeyIndex
    {
        public static KeyIndex For(PocoChanges<T> keyed)
        {
            if (keyed.KeyExpressions is [var only])
            {
                var body = only.Body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert
                    ? convert.Operand
                    : only.Body;
                if (body.Type.IsValueType && Nullable.GetUnderlyingType(body.Type) is null)
                {
                    var read = Expression.Lambda(typeof(Func<,>).MakeGenericType(typeof(T), body.Type), body, only.Parameters);
                    return (KeyIndex)Activator.CreateInstance(
                        typeof(Typed<>).MakeGenericType(typeof(T), body.Type), read.Compile())!;
                }
            }

            return new Boxed(keyed);
        }

        public abstract void Clear(int capacity);

        public abstract void Add(T row, int entry);

        public abstract bool TryGet(T row, out int entry);

        public abstract bool Remove(T row, out int entry);
    }

    /// <summary>A key of one column of a value type, held as itself: no object per row.</summary>
    private sealed class Typed<TKey> : KeyIndex
        where TKey : struct
    {
        private readonly Func<T, TKey> _read;
        private Dictionary<TKey, int> _entries = [];

        public Typed(Func<T, TKey> read) => _read = read;

        public override void Clear(int capacity) => _entries = new Dictionary<TKey, int>(capacity);

        public override void Add(T row, int entry) => _entries.Add(_read(row), entry);

        public override bool TryGet(T row, out int entry) => _entries.TryGetValue(_read(row), out entry);

        public override bool Remove(T row, out int entry) => _entries.Remove(_read(row), out entry);
    }

    /// <summary>Any other key, held as the row's key values and compared as a keyed refresh compares them.</summary>
    private sealed class Boxed : KeyIndex
    {
        private readonly PocoChanges<T> _keyed;
        private Dictionary<object?[], int> _entries;

        public Boxed(PocoChanges<T> keyed)
        {
            _keyed = keyed;
            _entries = new Dictionary<object?[], int>(PocoChanges<T>.KeyComparer);
        }

        public override void Clear(int capacity) =>
            _entries = new Dictionary<object?[], int>(capacity, PocoChanges<T>.KeyComparer);

        public override void Add(T row, int entry) => _entries.Add(_keyed.KeyOf(row), entry);

        public override bool TryGet(T row, out int entry) => _entries.TryGetValue(_keyed.KeyOf(row), out entry);

        public override bool Remove(T row, out int entry) => _entries.Remove(_keyed.KeyOf(row), out entry);
    }
}

/// <summary>
/// What a refresh of upserts and deletes did to a keyed table (D367), found through its
/// <see cref="PocoKeyPositions{T}"/>: the positions of the rows it deleted and of the rows it replaced,
/// in the earlier snapshot, with each replacement; and the rows of keys the table did not hold, which
/// go after the rest in the order they were given.
/// </summary>
internal sealed class PocoKeyedChange<T>
{
    /// <summary>Positions of the rows deleted, in the earlier snapshot, ascending.</summary>
    public required int[] Deleted { get; init; }

    /// <summary>The rows deleted, as the earlier snapshot holds them, in <see cref="Deleted"/>'s order.</summary>
    public required T[] DeletedRows { get; init; }

    /// <summary>Positions of the rows replaced, in the earlier snapshot, ascending, each with its replacement.</summary>
    public required (int Position, T Row)[] Replaced { get; init; }

    /// <summary>The rows of keys the earlier snapshot did not hold, in the order they were given.</summary>
    public required T[] Appended { get; init; }

    /// <summary>Whether the change only appends, keeping every earlier row where it was.</summary>
    public bool OnlyAppends => Deleted.Length == 0 && Replaced.Length == 0;
}
