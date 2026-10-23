using System.Globalization;

namespace Chalk.Sample.Blotter.Trades;

/// <summary>What a column holds, which decides how it is shown, sorted and filtered.</summary>
public enum ColumnKind
{
    /// <summary>An id: filtered by values typed or pasted, matched exactly.</summary>
    Id,

    /// <summary>A whole number: filtered by a range.</summary>
    Integer,

    /// <summary>A number with a fraction: filtered by a range.</summary>
    Number,

    /// <summary>Text of open-ended values: filtered by values picked or typed, or by what they start with.</summary>
    Text,

    /// <summary>Text from a short fixed list: filtered by ticking the values.</summary>
    Choice,

    /// <summary>A time: filtered by a range.</summary>
    Time,
}

/// <summary>A column of a tab: its SQL name, how the host compares, shows and binds it.</summary>
/// <remarks>
/// The host's comparison must order rows exactly as the database's <c>ORDER BY</c> does, because a
/// page is merged from both: text byte by byte (DuckDB's default collation is binary, and every
/// value here is ASCII), numbers and times by value. No column is nullable, so no rule for nulls is
/// needed on either side.
/// </remarks>
public sealed class Column
{
    private readonly Func<IRow, object> _value;

    private Column(
        string name, string title, ColumnKind kind, Type type, Func<IRow, object> value, Comparison<IRow> compare,
        Func<IRow, string> show, IReadOnlyList<string>? choices)
    {
        Type = type;
        Name = name;
        Title = title;
        Kind = kind;
        _value = value;
        Compare = compare;
        Show = show;
        Choices = choices ?? [];
    }

    public string Name { get; }

    public string Title { get; }

    public ColumnKind Kind { get; }

    /// <summary>The CLR type a value of the column binds as, exactly: a parameter compared with it takes no other.</summary>
    public Type Type { get; }

    /// <summary>A number typed into a range, as the column binds it.</summary>
    public object Bind(double value) => Type switch
    {
        _ when Type == typeof(int) => (int)value,
        _ when Type == typeof(long) => (long)value,
        _ => (object)value,
    };

    /// <summary>Right-aligned in the grid.</summary>
    public bool Numeric => Kind is ColumnKind.Integer or ColumnKind.Number;

    public Comparison<IRow> Compare { get; }

    public Func<IRow, string> Show { get; }

    /// <summary>The values of a <see cref="ColumnKind.Choice"/> column.</summary>
    public IReadOnlyList<string> Choices { get; }

    /// <summary>The row's value, as a parameter binds it: the column's own CLR type.</summary>
    public object Value(IRow row) => _value(row);

    public static Column Uuid<T>(string name, string title, Func<T, Guid> value) where T : IRow => new(
        name, title, ColumnKind.Id, typeof(Guid), r => value((T)r), (a, b) => value((T)a).CompareTo(value((T)b)),
        r => value((T)r).ToString(), null);

    public static Column Long<T>(string name, string title, Func<T, long> value) where T : IRow => new(
        name, title, ColumnKind.Integer, typeof(long), r => value((T)r), (a, b) => value((T)a).CompareTo(value((T)b)),
        r => value((T)r).ToString("#,0", CultureInfo.InvariantCulture), null);

    public static Column Int<T>(string name, string title, Func<T, int> value) where T : IRow => new(
        name, title, ColumnKind.Integer, typeof(int), r => value((T)r), (a, b) => value((T)a).CompareTo(value((T)b)),
        r => value((T)r).ToString("#,0", CultureInfo.InvariantCulture), null);

    public static Column Number<T>(string name, string title, Func<T, double> value, string format) where T : IRow => new(
        name, title, ColumnKind.Number, typeof(double), r => value((T)r), (a, b) => value((T)a).CompareTo(value((T)b)),
        r => value((T)r).ToString(format, CultureInfo.InvariantCulture), null);

    public static Column Text<T>(string name, string title, Func<T, string> value) where T : IRow => new(
        name, title, ColumnKind.Text, typeof(string), r => value((T)r), (a, b) => string.CompareOrdinal(value((T)a), value((T)b)),
        r => value((T)r), null);

    public static Column Choice<T>(string name, string title, Func<T, string> value, IReadOnlyList<string> choices) where T : IRow => new(
        name, title, ColumnKind.Choice, typeof(string), r => value((T)r), (a, b) => string.CompareOrdinal(value((T)a), value((T)b)),
        r => value((T)r), choices);

    public static Column Time<T>(string name, string title, Func<T, DateTime> value) where T : IRow => new(
        name, title, ColumnKind.Time, typeof(DateTime), r => value((T)r), (a, b) => value((T)a).CompareTo(value((T)b)),
        r => value((T)r).ToString("MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), null);

    public override string ToString() => Name;
}
