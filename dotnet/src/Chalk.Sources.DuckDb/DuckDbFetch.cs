using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Sources.Ado;
using DuckDB.NET.Data;
using DuckDB.NET.Native;

namespace Chalk.Sources.DuckDb;

/// <summary>
/// The native DuckDB reader (D148, <c>docs/design/24-zero-gc.md</c> §6): a prepared statement, a
/// streaming result, and its data chunks copied straight into arena staging.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole point of the step on the remote path. A <c>DbDataReader</c> hands over one cell
/// at a time and makes a .NET object for each text value, which no amount of care on Chalk's side
/// removes; a DuckDB data chunk is a set of contiguous vectors, so a column is a <c>memcpy</c> and a
/// <c>duckdb_string_t</c> is bytes with a length. Nothing here allocates per row.
/// </para>
/// <para>
/// A result column whose logical type is outside <see cref="DuckDbTypes"/>'s table — a LIST, a
/// STRUCT, an ENUM, an INTERVAL, or a pair the declared Chalk type could not hold — sends the whole
/// query to <see cref="DbDataReaderFetch"/>, decided from the result's column types before the first
/// chunk is fetched. <c>ExecutionStats.SourcePaths</c> then names both paths.
/// </para>
/// <para>
/// Cancellation is <c>duckdb_interrupt</c> on the native connection, registered on the token, plus a
/// token check between chunks: a fetch already inside the native call is what the interrupt is for,
/// and the check is what stops the next one. Errors come from <c>duckdb_prepare_error</c> and
/// <c>duckdb_result_error</c> as a <see cref="DbException"/>, so the source attributes them exactly
/// as it attributes a provider's own.
/// </para>
/// </remarks>
public sealed class DuckDbFetch : IRemoteFetch
{
    /// <summary>The one instance; it holds no state.</summary>
    public static DuckDbFetch Instance { get; } = new();

    private DuckDbFetch()
    {
    }

    /// <inheritdoc />
    public string Name => "duckdb-native";

    /// <inheritdoc />
    public IAsyncEnumerable<RecordBatch> FetchAsync(
        RemoteFetchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A host that pointed an ADO source at DuckDB through some other connection type gets the
        // path every provider gets, rather than a failure.
        return request.Connection is DuckDBConnection duck
            ? NativeAsync(request, duck, ct)
            : Fallback(request, ct);
    }

    private static IAsyncEnumerable<RecordBatch> Fallback(
        RemoteFetchRequest request, CancellationToken ct)
    {
        request.Stats.RecordSourcePath(request.SourceId, DbDataReaderFetch.Instance.Name);
        return DbDataReaderFetch.Instance.FetchAsync(request, ct);
    }

    private static async IAsyncEnumerable<RecordBatch> NativeAsync(
        RemoteFetchRequest request,
        DuckDBConnection connection,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var native = connection.NativeConnection;
        if (NativeMethods.PreparedStatements.DuckDBPrepare(native, request.Sql, out var prepared)
            != DuckDBState.Success)
        {
            var message = NativeMethods.PreparedStatements.DuckDBPrepareError(prepared);
            prepared.Dispose();
            throw new DuckDbNativeException(
                $"DuckDB could not prepare the query: {message}");
        }

        using (prepared)
        {
            DuckDbParameters.Bind(prepared, request.Parameters, request.ParameterTypes);

            if (NativeMethods.PreparedStatements.DuckDBExecutePreparedStreaming(
                    prepared, out var result) != DuckDBState.Success)
            {
                var message = NativeMethods.Query.DuckDBResultError(ref result);
                NativeMethods.Query.DuckDBDestroyResult(ref result);
                throw new DuckDbNativeException($"DuckDB could not run the query: {message}");
            }

            // Decided before the first chunk: either every column is one this reader copies, or the
            // query runs on the provider's own reader from the start.
            var reader = DuckDbChunkReader.TryCreate(request, ref result);
            if (reader is null)
            {
                NativeMethods.Query.DuckDBDestroyResult(ref result);
                await foreach (var batch in Fallback(request, ct).ConfigureAwait(false))
                {
                    yield return batch;
                }

                yield break;
            }

            using (reader)
            {
                // The native call blocks; duckdb_interrupt is the only thing that reaches it.
                await using var interrupting = ct.Register(
                    static state => ((DuckDBNativeConnection)state!).Interrupt(), native);

                try
                {
                    await foreach (var batch in reader.ReadAsync(result, ct).ConfigureAwait(false))
                    {
                        yield return batch;
                    }
                }
                finally
                {
                    NativeMethods.Query.DuckDBDestroyResult(ref result);
                }
            }
        }
    }
}

/// <summary>
/// A failure DuckDB reported through the C API. A <see cref="DbException"/> on purpose: the ADO
/// source's attribution — provider error against timeout — reads the same shapes whichever path
/// produced them (§6).
/// </summary>
public sealed class DuckDbNativeException : DbException
{
    public DuckDbNativeException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Binding by ordinal, which is all the native path needs: M4's rewrite turns every placeholder into
/// a positional <c>?</c> for this dialect, so parameter <c>i</c> is <c>duckdb_bind_*(stmt, i + 1)</c>.
/// </summary>
internal static class DuckDbParameters
{
    public static void Bind(
        DuckDBPreparedStatement statement,
        IReadOnlyList<object?> values,
        IReadOnlyList<ChalkType> types)
    {
        for (var i = 0; i < values.Count; i++)
        {
            var index = i + 1;
            var value = values[i];
            var state = value is null
                ? NativeMethods.PreparedStatements.DuckDBBindNull(statement, index)
                : BindValue(statement, index, value, i < types.Count ? types[i] : null);
            if (state != DuckDBState.Success)
            {
                throw new DuckDbNativeException(
                    $"DuckDB refused parameter {index} "
                    + $"({(value is null ? "NULL" : value.GetType().Name)}): "
                    + NativeMethods.PreparedStatements.DuckDBPrepareError(statement));
            }
        }
    }

    private static DuckDBState BindValue(
        DuckDBPreparedStatement statement, long index, object value, ChalkType? type)
    {
        switch (value)
        {
            case bool flag:
                return NativeMethods.PreparedStatements.DuckDBBindBoolean(statement, index, flag);
            case sbyte v:
                return NativeMethods.PreparedStatements.DuckDBBindInt8(statement, index, v);
            case short v:
                return NativeMethods.PreparedStatements.DuckDBBindInt16(statement, index, v);
            case int v:
                return NativeMethods.PreparedStatements.DuckDBBindInt32(statement, index, v);
            case long v:
                return NativeMethods.PreparedStatements.DuckDBBindInt64(statement, index, v);
            case byte v:
                return NativeMethods.PreparedStatements.DuckDBBindUInt8(statement, index, v);
            case ushort v:
                return NativeMethods.PreparedStatements.DuckDBBindUInt16(statement, index, v);
            case uint v:
                return NativeMethods.PreparedStatements.DuckDBBindUInt32(statement, index, v);
            case ulong v:
                return NativeMethods.PreparedStatements.DuckDBBindUInt64(statement, index, v);
            case float v:
                return NativeMethods.PreparedStatements.DuckDBBindFloat(statement, index, v);
            case double v:
                return NativeMethods.PreparedStatements.DuckDBBindDouble(statement, index, v);
            case string v:
                return NativeMethods.PreparedStatements.DuckDBBindVarchar(statement, index, v);
            case Utf8String v:
                return NativeMethods.PreparedStatements.DuckDBBindVarchar(
                    statement, index, v.ToString());
            case byte[] v:
                return NativeMethods.PreparedStatements.DuckDBBindBlob(statement, index, v, v.Length);
            case ReadOnlyMemory<byte> v:
                var bytes = v.ToArray();
                return NativeMethods.PreparedStatements.DuckDBBindBlob(
                    statement, index, bytes, bytes.Length);
            case DateOnly v:
                return NativeMethods.PreparedStatements.DuckDBBindDate(
                    statement, index, ((DuckDBDateOnly)v).ToDuckDBDate());
            case TimeOnly v:
                return NativeMethods.PreparedStatements.DuckDBBindTime(
                    statement, index,
                    NativeMethods.DateTimeHelpers.DuckDBToTime((DuckDBTimeOnly)v));
            case DateTime v:
                return NativeMethods.PreparedStatements.DuckDBBindTimestamp(
                    statement, index,
                    DuckDBTimestamp.FromDateTime(v).ToDuckDBTimestampStruct());
            case DateTimeOffset v:
                return NativeMethods.PreparedStatements.DuckDBBindTimestamp(
                    statement, index,
                    DuckDBTimestamp.FromDateTime(v.UtcDateTime).ToDuckDBTimestampStruct());

            // A decimal has no duckdb_bind_decimal in the C API; the varchar form is exact and a
            // parameter is bound once per query, not once per row.
            case decimal v:
                return NativeMethods.PreparedStatements.DuckDBBindVarchar(
                    statement, index, v.ToString(CultureInfo.InvariantCulture));
            case Guid v:
                return NativeMethods.PreparedStatements.DuckDBBindVarchar(
                    statement, index, v.ToString("D", CultureInfo.InvariantCulture));
            default:
                throw new UnsupportedFeatureException(
                    $"a DuckDB parameter of CLR type {value.GetType().Name}"
                    + (type is null ? string.Empty : $" for declared type {type}"),
                    "The native DuckDB reader binds the CLR shapes docs/design/18-m4-capabilities-"
                    + "and-pushdown.md §3 lists. Use the DbDataReader path for anything else.");
        }
    }
}
