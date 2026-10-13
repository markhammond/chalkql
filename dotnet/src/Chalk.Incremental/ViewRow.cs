using System.Globalization;
using Apache.Arrow;
using Apache.Arrow.Types;
using Chalk.Arrow;

namespace Chalk.Incremental;

/// <summary>
/// One row of a view's result, as its <see cref="ViewMapper{TKey, TRow}"/> reads it (D339): typed
/// getters over the row's columns, text as UTF-8 spans over the result's own buffers, nothing
/// allocated but by <see cref="GetString"/>. Valid only for the length of the call it is handed to.
/// </summary>
/// <remarks>
/// A getter reads a column of its own kind or a narrower one of the same family — an
/// <see cref="GetInt64"/> reads any integer — and refuses any other, naming the column. Read
/// <see cref="IsNull"/> first for a nullable column: a getter over a NULL returns its kind's default.
/// </remarks>
public readonly ref struct ViewRow
{
    private readonly IArrowArray[] _columns;
    private readonly Schema _schema;
    private readonly int _row;

    internal ViewRow(IArrowArray[] columns, Schema schema, int row)
    {
        _columns = columns;
        _schema = schema;
        _row = row;
    }

    /// <summary>How many columns the result has.</summary>
    public int ColumnCount => _columns.Length;

    /// <summary>A column's position by its name, case-insensitively, or -1.</summary>
    public int Ordinal(string name) => ViewColumns.Ordinal(_schema, name);

    /// <summary>A column's name.</summary>
    public string Name(int column) => _schema.FieldsList[column].Name;

    /// <summary>Whether the cell is NULL.</summary>
    public bool IsNull(int column) => _columns[column].IsNull(_row);

    /// <summary>A STRING cell's UTF-8 bytes, over the result's own buffer; empty for a NULL.</summary>
    public ReadOnlySpan<byte> GetUtf8(int column) =>
        _columns[column] is StringArray or StringViewArray or LargeStringArray
            ? (IsNull(column) ? [] : _columns[column].GetUtf8(_row))
            : throw Mismatch(column, "a STRING");

    /// <summary>A STRING cell as a string, which allocates; null for a NULL.</summary>
    public string? GetString(int column) =>
        IsNull(column) ? null : System.Text.Encoding.UTF8.GetString(GetUtf8(column));

    /// <summary>A BOOLEAN cell.</summary>
    public bool GetBoolean(int column) =>
        _columns[column] is BooleanArray booleans
            ? booleans.GetValue(_row) ?? false
            : throw Mismatch(column, "a BOOLEAN");

    /// <summary>An integer cell of any width, as a <see cref="long"/>.</summary>
    public long GetInt64(int column) => _columns[column] switch
    {
        Int8Array a => a.GetValue(_row) ?? 0,
        Int16Array a => a.GetValue(_row) ?? 0,
        Int32Array a => a.GetValue(_row) ?? 0,
        Int64Array a => a.GetValue(_row) ?? 0,
        _ => throw Mismatch(column, "an integer"),
    };

    /// <summary>An INTEGER cell, or a narrower one, as an <see cref="int"/>.</summary>
    public int GetInt32(int column) => _columns[column] switch
    {
        Int8Array a => a.GetValue(_row) ?? 0,
        Int16Array a => a.GetValue(_row) ?? 0,
        Int32Array a => a.GetValue(_row) ?? 0,
        _ => throw Mismatch(column, "an INTEGER or narrower"),
    };

    /// <summary>A REAL or DOUBLE cell, as a <see cref="double"/>.</summary>
    public double GetDouble(int column) => _columns[column] switch
    {
        FloatArray a => a.GetValue(_row) ?? 0,
        DoubleArray a => a.GetValue(_row) ?? 0,
        _ => throw Mismatch(column, "a REAL or DOUBLE"),
    };

    /// <summary>
    /// A DECIMAL cell, as a <see cref="decimal"/>. A value past the 28 or so digits a
    /// <see cref="decimal"/> holds throws <see cref="OverflowException"/>.
    /// </summary>
    public decimal GetDecimal(int column) =>
        _columns[column] is Decimal128Array decimals
            ? decimals.GetValue(_row) ?? 0m
            : throw Mismatch(column, "a DECIMAL");

    /// <summary>
    /// A TIMESTAMP cell, as a <see cref="DateTime"/>: of kind <see cref="DateTimeKind.Unspecified"/>
    /// for a TIMESTAMP, UTC for one with a time zone. A DATE reads as its midnight.
    /// </summary>
    public DateTime GetDateTime(int column) => _columns[column] switch
    {
        TimestampArray a when IsNull(column) => default,
        TimestampArray a => ((TimestampType)a.Data.DataType).Timezone is { Length: > 0 }
            ? a.GetTimestamp(_row)!.Value.UtcDateTime
            : DateTime.SpecifyKind(a.GetTimestamp(_row)!.Value.UtcDateTime, DateTimeKind.Unspecified),
        Date32Array a => a.GetDateTime(_row) ?? default,
        _ => throw Mismatch(column, "a TIMESTAMP or DATE"),
    };

    /// <summary>Any cell, boxed, as <see cref="RecordBatchExtensions.ValueAt"/> reads it. Allocates.</summary>
    public object? GetValue(int column) => RecordBatchExtensions.ValueAt(_columns[column], _row);

    private InvalidCastException Mismatch(int column, string wanted) =>
        new($"Column {column}, '{Name(column)}', is {_schema.FieldsList[column].DataType.Name}, not {wanted}. "
            + "Read it with the getter of its kind.");
}

/// <summary>Column lookups shared by a row and a view.</summary>
internal static class ViewColumns
{
    public static int Ordinal(Schema schema, string name)
    {
        for (var i = 0; i < schema.FieldsList.Count; i++)
        {
            if (string.Equals(schema.FieldsList[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>A cell as a message shows it: its value, or NULL.</summary>
    public static string Render(IArrowArray column, int row) =>
        RecordBatchExtensions.ValueAt(column, row) switch
        {
            null => "NULL",
            string text => $"'{text}'",
            IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
            var value => value.ToString() ?? "NULL",
        };
}
