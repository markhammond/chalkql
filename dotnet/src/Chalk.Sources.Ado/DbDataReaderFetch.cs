using System.Data;
using System.Data.Common;
using Apache.Arrow;

namespace Chalk.Sources.Ado;

/// <summary>
/// The default <see cref="IRemoteFetch"/>: a <see cref="DbCommand"/>, its
/// <see cref="DbDataReader"/>, and <c>AdoBatchReader</c> over it (D148).
/// </summary>
/// <remarks>
/// This is the path every provider gets, and the one every other path falls back to. It is exactly
/// what <c>AdoSource</c> did before the extension point existed: the same command, the same
/// <see cref="CommandBehavior.SequentialAccess"/>, the same <c>DbCommand.Cancel</c> registration
/// (D86), and the same reader.
/// </remarks>
public sealed class DbDataReaderFetch : IRemoteFetch
{
    /// <summary>The one instance; it holds no state.</summary>
    public static DbDataReaderFetch Instance { get; } = new();

    private DbDataReaderFetch()
    {
    }

    /// <inheritdoc />
    public string Name => "DbDataReader";

    /// <inheritdoc />
    public async IAsyncEnumerable<RecordBatch> FetchAsync(
        RemoteFetchRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // An already-cancelled token is answered here, before a command exists: registering it
        // further down would invoke the provider's Cancel inline against a command that has not
        // begun, which a native provider does not survive.
        ct.ThrowIfCancellationRequested();

        DbCommand? command = null;
        DbDataReader? reader = null;
        try
        {
            command = request.Connection.CreateCommand();
            command.CommandText = request.Sql;
            command.CommandTimeout = request.Timeout == System.Threading.Timeout.InfiniteTimeSpan
                ? 0
                : Math.Max(1, (int)Math.Ceiling(request.Timeout.TotalSeconds));
            AdoParameters.Bind(
                command, request.ParameterNames, request.Parameters, request.ParameterTypes);

            // Cancellation reaches the driver: ExecuteReaderAsync's token is what a provider turns
            // into DbCommand.Cancel, and the token already carries the caller's and the timeout's.
            reader = await command
                .ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            if (reader is not null)
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }

            command?.Dispose();
            throw;
        }

        await using (command)
        await using (reader)
        {
            // Cancelling the token is not on its own enough to stop a provider that is blocked inside
            // ReadAsync: several ignore the token once the command is executing, and the standard way
            // to reach the server is DbCommand.Cancel (D86). A command that has already finished may
            // refuse it, which is not a failure of the query -- the rows are already read.
            //
            // Registered inside the command's and the reader's own scope so that it is disposed
            // *before* either of them: a cancellation landing after the command was disposed would
            // otherwise call Cancel on a dead command, which a native provider answers with an
            // access violation rather than an exception.
            await using var cancelling = ct.Register(
                static state =>
                {
                    try
                    {
                        ((DbCommand)state!).Cancel();
                    }
                    catch (Exception)
                    {
                        // The command is past cancelling. Nothing to do and nothing to report.
                    }
                },
                command);

            using var batches = new AdoBatchReader(
                request.SourceId,
                request.Subject,
                request.OutputSchema,
                request.ColumnTypes,
                request.Arena,
                request.BatchSize,
                request.Profile,
                request.Traits);
            while (await batches.ReadBatchAsync(reader, ct).ConfigureAwait(false) is { } batch)
            {
                yield return batch;
            }
        }
    }
}
