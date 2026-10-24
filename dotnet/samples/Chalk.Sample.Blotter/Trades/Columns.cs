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
/// page is merged from both: text byte by byte (the tables' text columns are collated "C", and every
/// value here is ASCII), numbers and times by value. No column is nullable, so no rule for nulls is
/// needed on either side.
/// </remarks>
public sealed class Column
{
    private readonly Func<IRow, object> _value;

    private Column(
        string name, string title, ColumnKind kind, Type type, Func<IRow, object> value, Comparison<IRow> compare,
        Func<IRow, string> show, IReadOnlyList<string>? choices, string? description = null, Func<IRow, string>? detail = null,
        bool derived = false)
    {
        Type = type;
        Name = name;
        Title = title;
        Description = description;
        Kind = kind;
        _value = value;
        Compare = compare;
        Show = show;
        Detail = detail ?? show;
        Derived = derived;
        Choices = choices ?? [];
    }

    public string Name { get; }

    public string Title { get; }

    /// <summary>What the column holds, where its title alone leaves it open; shown on its heading.</summary>
    public string? Description { get; }

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

    /// <summary>The value as the grid shows it.</summary>
    public Func<IRow, string> Show { get; }

    /// <summary>The value in full, as an order's own page and a time's hover show it: a time to the microsecond.</summary>
    public Func<IRow, string> Detail { get; }

    /// <summary>
    /// Worked out from the row's other values, and only shown: not a column of the database's rows,
    /// so a page is neither sorted nor filtered by it.
    /// </summary>
    public bool Derived { get; }

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

    /// <param name="decimals">The most decimal places shown.</param>
    public static Column Number<T>(string name, string title, Func<T, double> value, int decimals) where T : IRow => new(
        name, title, ColumnKind.Number, typeof(double), r => value((T)r), (a, b) => value((T)a).CompareTo(value((T)b)),
        r => Finance(value((T)r), decimals), null);

    /// <summary>A price: to six significant figures, see <see cref="Significant"/>.</summary>
    public static Column Price<T>(string name, string title, Func<T, double> value) where T : IRow => new(
        name, title, ColumnKind.Number, typeof(double), r => value((T)r), (a, b) => value((T)a).CompareTo(value((T)b)),
        r => Significant(value((T)r)), null);

    public static Column Text<T>(string name, string title, Func<T, string> value) where T : IRow => new(
        name, title, ColumnKind.Text, typeof(string), r => value((T)r), (a, b) => string.CompareOrdinal(value((T)a), value((T)b)),
        r => value((T)r), null);

    public static Column Choice<T>(string name, string title, Func<T, string> value, IReadOnlyList<string> choices) where T : IRow => new(
        name, title, ColumnKind.Choice, typeof(string), r => value((T)r), (a, b) => string.CompareOrdinal(value((T)a), value((T)b)),
        r => value((T)r), choices);

    public static Column Time<T>(string name, string title, Func<T, DateTime> value, string? description = null) where T : IRow => new(
        name, title, ColumnKind.Time, typeof(DateTime), r => value((T)r), (a, b) => value((T)a).CompareTo(value((T)b)),
        r => value((T)r).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), null, description,
        r => value((T)r).ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture));

    /// <summary>A value worked out from others and only shown, see <see cref="Derived"/>: right-aligned if <paramref name="numeric"/>.</summary>
    public static Column Shown<T>(string name, string title, Func<T, string> show, bool numeric, string? description = null) where T : IRow => new(
        name, title, numeric ? ColumnKind.Number : ColumnKind.Text, typeof(string), r => show((T)r),
        (a, b) => string.CompareOrdinal(show((T)a), show((T)b)), r => show((T)r), null, description, derived: true);

    /// <summary>What a value of nought shows as: a dash, which the eye passes over.</summary>
    public const string Nought = "–";

    /// <summary>
    /// A quantity as finance writes it: no zero before the point of a fraction, and none after its
    /// last digit that counts (0.2500 is .25); nought is a dash.
    /// </summary>
    public static string Finance(double value, int decimals)
    {
        var text = value.ToString("#,0." + new string('#', decimals), CultureInfo.InvariantCulture);
        return text switch
        {
            "0" or "-0" => Nought,
            ['0', '.', ..] => text[1..],
            ['-', '0', '.', ..] => "-" + text[2..],
            _ => text,
        };
    }

    /// <summary>
    /// A price to <paramref name="figures"/> significant figures at most, written as
    /// <see cref="Finance"/> writes a quantity; in scientific notation (1.23457e-5) where that would
    /// take more than <paramref name="digits"/> digits.
    /// </summary>
    public static string Significant(double value, int figures = 6, int digits = 8)
    {
        var rounded = double.Parse(value.ToString("G" + figures, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        if (rounded == 0)
        {
            return Nought;
        }

        var magnitude = (int)Math.Floor(Math.Log10(Math.Abs(rounded)));
        var text = Finance(rounded, Math.Max(0, figures - 1 - magnitude));
        return text.Count(char.IsAsciiDigit) > digits
            ? rounded.ToString("0." + new string('#', figures - 1) + "e0", CultureInfo.InvariantCulture)
            : text;
    }

    public override string ToString() => Name;
}
