using System.Data;
using System.Data.Common;

namespace Chalk.Integration.Tests;

/// <summary>
/// A <see cref="DbConnection"/> that wraps another and records the text of every command run on it.
/// </summary>
/// <remarks>
/// It is the last place the query text exists before the driver has it, which is what makes it the
/// honest place to assert what a source was actually sent: a plan says what the planner meant, and
/// this says what went over the wire. Everything is delegated; nothing is intercepted but the text.
/// </remarks>
internal sealed class CapturingConnection(DbConnection inner, List<string> sent) : DbConnection
{
    private readonly DbConnection _inner = inner;
    private readonly List<string> _sent = sent;

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString
    {
        get => _inner.ConnectionString;
        set => _inner.ConnectionString = value;
    }

    public override string Database => _inner.Database;

    public override string DataSource => _inner.DataSource;

    public override string ServerVersion => _inner.ServerVersion;

    public override ConnectionState State => _inner.State;

    public override void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);

    public override void Close() => _inner.Close();

    public override void Open() => _inner.Open();

    public override Task OpenAsync(CancellationToken cancellationToken) =>
        _inner.OpenAsync(cancellationToken);

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        _inner.BeginTransaction(isolationLevel);

    protected override DbCommand CreateDbCommand() =>
        new CapturingCommand(_inner.CreateCommand(), this, _sent);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => _inner.DisposeAsync();

    private sealed class CapturingCommand(DbCommand inner, DbConnection owner, List<string> sent)
        : DbCommand
    {
        private readonly DbCommand _inner = inner;
        private readonly List<string> _sent = sent;
        private DbConnection? _connection = owner;

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText
        {
            get => _inner.CommandText;
            set
            {
                lock (_sent)
                {
                    _sent.Add(value ?? string.Empty);
                }

                _inner.CommandText = value;
            }
        }

        public override int CommandTimeout
        {
            get => _inner.CommandTimeout;
            set => _inner.CommandTimeout = value;
        }

        public override CommandType CommandType
        {
            get => _inner.CommandType;
            set => _inner.CommandType = value;
        }

        public override bool DesignTimeVisible
        {
            get => _inner.DesignTimeVisible;
            set => _inner.DesignTimeVisible = value;
        }

        public override UpdateRowSource UpdatedRowSource
        {
            get => _inner.UpdatedRowSource;
            set => _inner.UpdatedRowSource = value;
        }

        protected override DbConnection? DbConnection
        {
            get => _connection;
            set => _connection = value;
        }

        protected override DbParameterCollection DbParameterCollection => _inner.Parameters;

        protected override DbTransaction? DbTransaction
        {
            get => _inner.Transaction;
            set => _inner.Transaction = value;
        }

        public override void Cancel() => _inner.Cancel();

        public override int ExecuteNonQuery() => _inner.ExecuteNonQuery();

        public override object? ExecuteScalar() => _inner.ExecuteScalar();

        public override void Prepare() => _inner.Prepare();

        protected override DbParameter CreateDbParameter() => _inner.CreateParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            _inner.ExecuteReader(behavior);

        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior, CancellationToken cancellationToken) =>
            _inner.ExecuteReaderAsync(behavior, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
