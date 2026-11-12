using System.Runtime.CompilerServices;
using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Operators;

/// <summary>
/// The root of a quarantine (F302, design 82): the subtree an entitlement barrier stood over, the
/// policy's own evaluation. A data error raised beneath that nothing below settled closed, a mask's, a
/// join's or a source's, makes the policy's decision indeterminate for a row it cannot be settled for
/// without the result, so the result is withheld: the error becomes a
/// <see cref="PolicyIndeterminateException"/> naming the tables and quoting nothing, and is recorded,
/// with its cause, for a host's observer. Batches pass through untouched.
/// </summary>
internal sealed class QuarantineOperator : OperatorBase
{
    private readonly IBatchOperator _input;
    private readonly string _tables;

    public QuarantineOperator(OperatorContext context, string path, IBatchOperator input, string tables)
        : base(context, input.Schema, input.ColumnTypes, path)
    {
        _input = input;
        _tables = tables;
    }

    protected override ValueTask DisposeCoreAsync() => _input.DisposeAsync();

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var batches = _input.ExecuteAsync(ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await batches.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception error) when (IndeterminateLedger.IsDataError(error))
                {
                    throw Withheld(error);
                }

                if (!moved)
                {
                    yield break;
                }

                yield return batches.Current;
            }
        }
        finally
        {
            await batches.DisposeAsync().ConfigureAwait(false);
        }
    }

    private PolicyIndeterminateException Withheld(Exception cause)
    {
        var ledger = Context.Arena.Indeterminate;
        ledger?.Record(_tables, IndeterminateResolution.StatementWithheld, 1, cause);
        return new PolicyIndeterminateException(
            _tables, ledger?.CorrelationId ?? Guid.NewGuid(), IndeterminateLedger.SqlStateOf(cause));
    }
}
