using System.Globalization;
using System.Linq.Expressions;
using Chalk.Catalog;

namespace Chalk.Sources.Poco;

/// <summary>How a table says it changes (D339, <c>docs/design/64-incremental-views.md</c> §2).</summary>
internal enum PocoChangeKind
{
    /// <summary>Nothing declared: replaced or appended to, as any table is.</summary>
    None = 0,

    /// <summary>A log, read by time: rows are only appended, and a late one is refused.</summary>
    AppendOnly = 1,

    /// <summary>A log read by key and time, as an ASOF join reads it.</summary>
    AsOf = 2,

    /// <summary>Rows change by key: upserted and deleted.</summary>
    Keyed = 3,
}

/// <summary>A change kind as the builder was told it, before <c>Build()</c> resolves its members.</summary>
internal sealed class PocoChangeDeclaration<T>
{
    public required PocoChangeKind Kind { get; init; }

    /// <summary>The time member, for an append-only or as-of table.</summary>
    public string? TimeMember { get; init; }

    public LambdaExpression? Time { get; init; }

    public TimeSpan Lateness { get; init; }

    /// <summary>The key members, in the order declared: an as-of table's, or a keyed table's.</summary>
    public required string[] KeyMembers { get; init; }

    public required Expression<Func<T, object?>>[] Keys { get; init; }

    /// <summary>How the declaration reads in a message: <c>AppendOnly(Ts)</c>, <c>Keyed(Book, Asset)</c>.</summary>
    public string Spelling => Kind switch
    {
        PocoChangeKind.AppendOnly => Lateness == TimeSpan.Zero
            ? $"AppendOnly({TimeMember})"
            : $"AppendOnly({TimeMember}, lateness {Lateness})",
        PocoChangeKind.AsOf => Lateness == TimeSpan.Zero
            ? $"AsOf({TimeMember}; {string.Join(", ", KeyMembers)})"
            : $"AsOf({TimeMember}, lateness {Lateness}; {string.Join(", ", KeyMembers)})",
        PocoChangeKind.Keyed => $"Keyed({string.Join(", ", KeyMembers)})",
        _ => "no change kind",
    };

    public PocoChanges<T> Compile() => new(this);
}

/// <summary>
/// One table's change kind at run time: its time and key read from a row, its lateness, and its
/// frontier — the greatest time it has been given, by a row or by an advance.
/// </summary>
/// <remarks>
/// The frontier moves only when a refresh commits, and is read only while a refresh is being
/// prepared, under the source's refresh lock and the engine's, so it needs no lock of its own.
/// </remarks>
internal sealed class PocoChanges<T>
{
    private readonly Func<T, object?>? _time;
    private readonly Func<T, long?>? _ticks;
    private readonly Func<T, object?>[] _keys;

    public PocoChanges(PocoChangeDeclaration<T> declaration)
    {
        Kind = declaration.Kind;
        Spelling = declaration.Spelling;
        TimeMember = declaration.TimeMember;
        KeyMembers = declaration.KeyMembers;
        LatenessTicks = declaration.Lateness.Ticks;
        if (declaration.Time is { } time)
        {
            TimeType = Nullable.GetUnderlyingType(time.ReturnType) ?? time.ReturnType;
            var boxed = Expression.Lambda<Func<T, object?>>(
                Expression.Convert(time.Body, typeof(object)), time.Parameters);
            _time = boxed.Compile();
            _ticks = CompileTicks(time, TimeType);
        }

        _keys = Array.ConvertAll(declaration.Keys, key => key.Compile());
    }

    public PocoChangeKind Kind { get; }

    public string Spelling { get; }

    public string? TimeMember { get; }

    /// <summary><see cref="DateTime"/> or <see cref="DateTimeOffset"/>, for a table with a time.</summary>
    public Type? TimeType { get; }

    public string[] KeyMembers { get; }

    public long LatenessTicks { get; }

    public bool HasTime => _time is not null;

    /// <summary>The greatest time this table has been given, as ticks, or null before its first.</summary>
    public long? Frontier { get; set; }

    /// <summary>A row's time as ticks — UTC ticks for an offset — or null when it has none.</summary>
    public long? TicksOf(T row) => _ticks!(row);

    /// <summary>
    /// The time member read as ticks, unboxed (design 69 §5): a refresh reads every row's time to find
    /// the frontier, and a boxed read made one object per row.
    /// </summary>
    private static Func<T, long?> CompileTicks(LambdaExpression time, Type timeType)
    {
        var ticks = timeType == typeof(DateTimeOffset) ? nameof(DateTimeOffset.UtcTicks) : nameof(DateTime.Ticks);
        Expression body;
        if (Nullable.GetUnderlyingType(time.Body.Type) is not null)
        {
            var local = Expression.Variable(time.Body.Type, "t");
            body = Expression.Block(
                typeof(long?),
                new[] { local },
                Expression.Assign(local, time.Body),
                Expression.Condition(
                    Expression.Property(local, nameof(Nullable<DateTime>.HasValue)),
                    Expression.Convert(
                        Expression.Property(Expression.Property(local, nameof(Nullable<DateTime>.Value)), ticks),
                        typeof(long?)),
                    Expression.Constant(null, typeof(long?))));
        }
        else
        {
            body = Expression.Convert(Expression.Property(time.Body, ticks), typeof(long?));
        }

        return Expression.Lambda<Func<T, long?>>(body, time.Parameters).Compile();
    }

    /// <summary>A time of this table's type as ticks, or null when it is not one.</summary>
    public long? Ticks(object? value) => value switch
    {
        DateTime time when TimeType == typeof(DateTime) => time.Ticks,
        DateTimeOffset time when TimeType == typeof(DateTimeOffset) => time.UtcTicks,
        _ => null,
    };

    /// <summary>Ticks as a host would read them in a message.</summary>
    public string Render(long ticks) =>
        TimeType == typeof(DateTimeOffset)
            ? new DateTimeOffset(ticks, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture)
            : new DateTime(ticks).ToString("O", CultureInfo.InvariantCulture);

    /// <summary>A row's key, compared by <see cref="KeyComparer"/>.</summary>
    public object?[] KeyOf(T row)
    {
        var key = new object?[_keys.Length];
        for (var i = 0; i < _keys.Length; i++)
        {
            key[i] = _keys[i](row);
        }

        return key;
    }

    public static IEqualityComparer<object?[]> KeyComparer { get; } = new KeyEquality();

    /// <summary>A key as a host would read it in a message: <c>(ACME, 7)</c>.</summary>
    public static string Render(object?[] key) =>
        "(" + string.Join(", ", key.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "NULL")) + ")";

    private sealed class KeyEquality : IEqualityComparer<object?[]>
    {
        public bool Equals(object?[]? x, object?[]? y)
        {
            if (x is null || y is null || x.Length != y.Length)
            {
                return ReferenceEquals(x, y);
            }

            for (var i = 0; i < x.Length; i++)
            {
                if (!object.Equals(x[i], y[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(object?[] key)
        {
            var hash = new HashCode();
            foreach (var value in key)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }
}

/// <summary>Where a change kind's refusals are worded, so each says what to do the same way.</summary>
internal static class PocoChangeRefusals
{
    public static CatalogVerificationException Late<T>(
        string table, PocoChanges<T> changes, int row, long time, long frontier) =>
        new(
            table,
            changes.Spelling,
            row,
            $"row {row} of the rows appended has {changes.TimeMember} {changes.Render(time)}, older than "
            + (changes.LatenessTicks == 0
                ? $"the table's frontier, {changes.Render(frontier)}, which no row may be"
                : $"the table's frontier, {changes.Render(frontier)}, less its lateness of "
                  + $"{TimeSpan.FromTicks(changes.LatenessTicks)}"),
            "Nothing in the refresh was applied. Declare a lateness on the table for a feed whose rows "
            + "arrive late, or replace the table's rows to rewrite its history.");

    public static CatalogVerificationException UntimedRow<T>(string table, PocoChanges<T> changes, int row) =>
        new(
            table,
            changes.Spelling,
            row,
            $"row {row} of the rows appended has no {changes.TimeMember}, and every row of a table "
            + "that declares a time needs one",
            "Nothing in the refresh was applied. Give every row its time.");

    public static CatalogVerificationException KeyTwice<T>(
        string table, PocoChanges<T> changes, int first, int second, object?[] key) =>
        new(
            table,
            changes.Spelling,
            second,
            $"rows {first} and {second} of the upserts share the key {PocoChanges<T>.Render(key)}",
            "Nothing in the refresh was applied. Say once in a refresh what each key becomes.");
}
