using System.Collections.Immutable;
using Apache.Arrow;

namespace Chalk.Sample.Blotter.Grid;

/// <summary>
/// A tab of the grid: the table its rows are read from, how they are keyed, sorted by default and
/// shown. What its rows are is the domain's business: it gives the tab its columns and its readers.
/// </summary>
/// <remarks>
/// Rows that change are versioned, and a row the live tail holds stands in for the database's row of
/// the same key when its version is newer (<see cref="Overrides"/>). Rows that never change are the
/// same row on either side, and nothing is overridden.
/// </remarks>
public sealed class Tab
{
    public Tab(
        string name, string title, IReadOnlyList<Column> columns, Column key, Column time, string table,
        string select, Func<RecordBatch, int, IRow> read, bool overrides, int frozen,
        IReadOnlySet<string>? hiddenAtFirst = null, bool home = false, Func<IRow, string>? describe = null,
        IReadOnlyList<TabPath>? paths = null)
    {
        Paths = paths ?? [];
        HiddenAtFirst = hiddenAtFirst ?? ImmutableHashSet<string>.Empty;
        Home = home;
        Describe = describe ?? (row => row.Id.ToString());
        Frozen = frozen;
        Name = name;
        Title = title;
        Columns = columns;
        Key = key;
        Time = time;
        Table = table;
        Select = select;
        Read = read;
        Overrides = overrides;
    }

    public string Name { get; }

    public string Title { get; }

    /// <summary>Every column, in the grid's order.</summary>
    public IReadOnlyList<Column> Columns { get; }

    /// <summary>
    /// How many of <see cref="Columns"/>, from the first, stay in place while the grid scrolls sideways:
    /// enough to tell which row is which.
    /// </summary>
    public int Frozen { get; }

    /// <summary>The unique key, which breaks every tie in a sort.</summary>
    public Column Key { get; }

    /// <summary>When the row happened: the period of interest that bounds a filter's list of values.</summary>
    public Column Time { get; }

    /// <summary>The table pages are read from, as ChalkQL names it.</summary>
    public string Table { get; }

    public string Select { get; }

    public Func<RecordBatch, int, IRow> Read { get; }

    /// <summary>Whether a row in the tail stands in for the database's row of the same key, its version being newer.</summary>
    public bool Overrides { get; }

    /// <summary>The newest first, which is what a blotter opens on.</summary>
    public SortSpec DefaultSort => SortSpec.Default(this);

    /// <summary>The columns hidden until the reader shows them, by name.</summary>
    public IReadOnlySet<string> HiddenAtFirst { get; }

    /// <summary>Whether this is the tab the grid opens on, which a view's link need not name.</summary>
    public bool Home { get; }

    /// <summary>A row in a line, as an alert or a notification says what fired it.</summary>
    public Func<IRow, string> Describe { get; }

    /// <summary>The paths from this tab's rows to other tabs' rows, which a condition reads along.</summary>
    public IReadOnlyList<TabPath> Paths { get; }

    public Column Named(string name) =>
        Columns.FirstOrDefault(c => c.Name == name) ?? throw new ArgumentException($"{Name} has no column '{name}'", nameof(name));

    public override string ToString() => Name;
}

/// <summary>
/// A path from a tab's rows to another tab's, by a column that holds the other's key: a fill's order,
/// by its <c>order_id</c>. A condition reads the other row's columns as <c>order.status</c>, and the
/// host joins for it.
/// </summary>
public sealed record TabPath(string Name, Tab Target, string Via, string Description);
