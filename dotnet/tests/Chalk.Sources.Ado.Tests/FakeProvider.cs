using System.Collections;
using System.Data;
using System.Data.Common;

namespace Chalk.Sources.Ado.Tests;

/// <summary>
/// A <see cref="DbConnection"/> that answers from a script and counts what was done to it.
/// </summary>
/// <remarks>
/// A real provider cannot be asked whether it leaked a connection or whether
/// <see cref="DbCommand.Cancel"/> was reached — it can only be observed not to have run out of pool.
/// This one records both, which is what turns "no connection leaks" and "cancellation reaches the
/// driver" from claims into assertions. The tests that use a real SQLite and DuckDB alongside it
/// are what stop this fake from drifting into a shape no provider has.
/// </remarks>
internal sealed class FakeConnection : DbConnection
{
    private readonly FakeProvider _provider;
    private ConnectionState _state = ConnectionState.Closed;

    public FakeConnection(FakeProvider provider) => _provider = provider;

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;

    public override string Database => "fake";

    public override string DataSource => "fake";

    public override string ServerVersion => "1.0";

    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

    public override void Close() => _state = ConnectionState.Closed;

    public override void Open()
    {
        Interlocked.Increment(ref _provider.Opened);
        _state = ConnectionState.Open;
    }

    protected override DbCommand CreateDbCommand() => new FakeCommand(_provider) { Connection = this };

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Interlocked.Increment(ref _provider.Disposed);
        }

        base.Dispose(disposing);
    }
}

/// <summary>What the fake provider was asked to do, shared by every connection it hands out.</summary>
internal sealed class FakeProvider
{
    public int Opened;
    public int Disposed;
    public int Cancelled;

    /// <summary>Rows the reader produces, one object[] per row. Columns are (id BIGINT, v BIGINT).</summary>
    public List<object?[]> Rows { get; init; } = [];

    /// <summary>
    /// The column names the reader reports. The default is what the plan expects; a test that
    /// wants the reader's own check to fire (F35) gives it something else.
    /// </summary>
    public IReadOnlyList<string> Names { get; init; } = ["id", "v"];

    /// <summary>Thrown from <c>ExecuteReader</c> when set: a provider that refuses the query.</summary>
    public Exception? FailOnExecute { get; init; }

    /// <summary>Thrown from <c>Read</c> at this row index when set: a provider that faults mid-stream.</summary>
    public int? FailAtRow { get; init; }

    /// <summary>Called before each row is produced. Where a test blocks, or trips cancellation.</summary>
    public Func<int, CancellationToken, Task>? BeforeRow { get; init; }

    public DbConnection Connect() => new FakeConnection(this);
}

internal sealed class FakeCommand : DbCommand
{
    private readonly FakeProvider _provider;

    public FakeCommand(FakeProvider provider) => _provider = provider;

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string CommandText { get; set; } = string.Empty;

    public override int CommandTimeout { get; set; }

    public override CommandType CommandType { get; set; } = CommandType.Text;

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection? DbConnection { get; set; }

    protected override DbParameterCollection DbParameterCollection { get; } = new FakeParameters();

    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel() => Interlocked.Increment(ref _provider.Cancelled);

    public override int ExecuteNonQuery() => 0;

    public override object? ExecuteScalar() => _provider.Rows.Count;

    public override void Prepare()
    {
    }

    protected override DbParameter CreateDbParameter() => new FakeParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        _provider.FailOnExecute is { } failure ? throw failure : new FakeReader(_provider);
}

internal sealed class FakeReader : DbDataReader
{
    private readonly FakeProvider _provider;
    private int _row = -1;

    public FakeReader(FakeProvider provider) => _provider = provider;

    public override int FieldCount => 2;

    public override bool HasRows => _provider.Rows.Count > 0;

    public override bool IsClosed { get; }

    public override int RecordsAffected => 0;

    public override int Depth => 0;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override string GetName(int ordinal) => _provider.Names[ordinal];

    public override int GetOrdinal(string name) => string.Equals(name, "id", StringComparison.Ordinal) ? 0 : 1;

    public override Type GetFieldType(int ordinal) => typeof(long);

    public override string GetDataTypeName(int ordinal) => "BIGINT";

    public override bool IsDBNull(int ordinal) => _provider.Rows[_row][ordinal] is null;

    public override object GetValue(int ordinal) => _provider.Rows[_row][ordinal] ?? DBNull.Value;

    public override long GetInt64(int ordinal) => (long)GetValue(ordinal);

    public override int GetInt32(int ordinal) => (int)(long)GetValue(ordinal);

    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);

    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);

    public override char GetChar(int ordinal) => (char)GetValue(ordinal);

    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);

    public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);

    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);

    public override float GetFloat(int ordinal) => (float)GetValue(ordinal);

    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);

    public override short GetInt16(int ordinal) => (short)GetValue(ordinal);

    public override string GetString(int ordinal) => (string)GetValue(ordinal);

    public override long GetBytes(int ordinal, long offset, byte[]? buffer, int bufferOffset, int length) => 0;

    public override long GetChars(int ordinal, long offset, char[]? buffer, int bufferOffset, int length) => 0;

    public override int GetValues(object[] values) => 0;

    public override IEnumerator GetEnumerator() => throw new NotSupportedException();

    public override bool NextResult() => false;

    public override bool Read() => ReadAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<bool> ReadAsync(CancellationToken ct)
    {
        var next = _row + 1;
        if (_provider.BeforeRow is { } before)
        {
            await before(next, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        if (next == _provider.FailAtRow)
        {
            throw new InvalidOperationException("the fake provider faulted mid-stream");
        }

        if (next >= _provider.Rows.Count)
        {
            return false;
        }

        _row = next;
        return true;
    }
}

internal sealed class FakeParameter : DbParameter
{
    public override DbType DbType { get; set; }

    public override ParameterDirection Direction { get; set; }

    public override bool IsNullable { get; set; }

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ParameterName { get; set; } = string.Empty;

    public override int Size { get; set; }

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string SourceColumn { get; set; } = string.Empty;

    public override bool SourceColumnNullMapping { get; set; }

    public override object? Value { get; set; }

    public override void ResetDbType()
    {
    }
}

internal sealed class FakeParameters : DbParameterCollection
{
    private readonly List<DbParameter> _items = [];

    public override int Count => _items.Count;

    public override object SyncRoot { get; } = new();

    public override int Add(object value)
    {
        _items.Add((DbParameter)value);
        return _items.Count - 1;
    }

    public override void AddRange(System.Array values)
    {
        foreach (var value in values)
        {
            Add(value);
        }
    }

    public override void Clear() => _items.Clear();

    public override bool Contains(object value) => _items.Contains((DbParameter)value);

    public override bool Contains(string value) => IndexOf(value) >= 0;

    public override void CopyTo(System.Array array, int index) => ((IList)_items).CopyTo(array, index);

    public override IEnumerator GetEnumerator() => _items.GetEnumerator();

    public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);

    public override int IndexOf(string parameterName) =>
        _items.FindIndex(p => string.Equals(p.ParameterName, parameterName, StringComparison.Ordinal));

    public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);

    public override void Remove(object value) => _items.Remove((DbParameter)value);

    public override void RemoveAt(int index) => _items.RemoveAt(index);

    public override void RemoveAt(string parameterName) => RemoveAt(IndexOf(parameterName));

    protected override DbParameter GetParameter(int index) => _items[index];

    protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];

    protected override void SetParameter(int index, DbParameter value) => _items[index] = value;

    protected override void SetParameter(string parameterName, DbParameter value) =>
        _items[IndexOf(parameterName)] = value;
}
