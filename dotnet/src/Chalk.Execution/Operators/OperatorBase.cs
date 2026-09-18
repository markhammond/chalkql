using Chalk.Catalog;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Operators;

/// <summary>
/// The parts every operator shares: its schema, its IR path for error messages, the wrapper that
/// turns any failure into an <see cref="ExecutionException"/> naming the plan and the operator
/// (§6.7), and the adapter from the operator's own iteration to the pull path of §3.
/// </summary>
/// <remarks>
/// An operator body stays an <c>async IAsyncEnumerable</c>, which is how it reads best; the pull
/// path takes the fast exit whenever <c>MoveNextAsync</c> completes synchronously, which is every
/// time for an in-process source. The enumerator itself is allocated once per execution, so the
/// steady-state cost of the whole arrangement is zero bytes per batch (D63).
/// </remarks>
internal abstract class OperatorBase : IBatchOperator, IAsyncEnumerable<ColumnarBatch>,
    IAsyncEnumerator<ColumnarBatch>
{
    private IAsyncEnumerator<ColumnarBatch>? _batches;
    private ValueTask<bool> _pending;
    private bool _hasPending;
    private bool _done;
    private CancellationToken _token;
    private int _generation = -1;

    protected OperatorBase(
        OperatorContext context, ArrowSchema schema, IReadOnlyList<ChalkType> columnTypes, string path)
    {
        Context = context;
        Schema = schema;
        ColumnTypes = columnTypes;
        Path = path;
    }

    public ArrowSchema Schema { get; }

    public IReadOnlyList<ChalkType> ColumnTypes { get; }

    /// <summary>Where this operator sits in the plan, e.g. <c>root/HashAggregate/Filter/Read</c>.</summary>
    public string Path { get; }

    public ColumnarBatch? Current { get; private set; }

    public bool IsDone => _done;

    protected OperatorContext Context { get; }

    /// <summary>What a source is handed for one scan. Rebuilt per execution: it names the arena.</summary>
    internal static ScanContext ScanContextFor(OperatorContext context) => new()
    {
        Stats = context.Stats,
        Arena = context.Arena,
        Parameters = [],
    };

    /// <summary>This operator's output slot: one per operator, reused for every batch it produces.</summary>
    protected ColumnarBatch NewOutput() => new(ColumnTypes)
    {
        ValidateLifetimes = Context.Settings.ValidateBatchLifetimes,
    };

    /// <summary>An output slot of a shape this operator's own schema does not describe.</summary>
    protected ColumnarBatch NewOutput(IReadOnlyList<ChalkType> types) => new(types)
    {
        ValidateLifetimes = Context.Settings.ValidateBatchLifetimes,
    };

    public bool TryNext(out ColumnarBatch? batch)
    {
        Restart();
        batch = null;
        if (_done)
        {
            return false;
        }

        var enumerator = _batches ??= Start(_token);
        ValueTask<bool> move;
        try
        {
            move = enumerator.MoveNextAsync();
        }
        catch (Exception exception) when (Wraps(exception))
        {
            throw new ExecutionException(Context.PlanDigest, Path, exception.Message, exception);
        }

        if (!move.IsCompleted)
        {
            _pending = move;
            _hasPending = true;
            return false;
        }

        bool moved;
        try
        {
            moved = move.GetAwaiter().GetResult();
        }
        catch (Exception exception) when (Wraps(exception))
        {
            throw new ExecutionException(Context.PlanDigest, Path, exception.Message, exception);
        }

        if (!moved)
        {
            _done = true;
            Current = null;
            return false;
        }

        Current = enumerator.Current;
        batch = Current;
        return true;
    }

    public async ValueTask<bool> WaitAsync(CancellationToken ct)
    {
        Restart();
        if (_done)
        {
            return false;
        }

        _token = ct;
        if (!_hasPending)
        {
            // Nothing was left pending, so the caller is simply asking for the next batch and is
            // willing to wait for it.
            if (TryNext(out _))
            {
                return true;
            }

            if (_done)
            {
                return false;
            }
        }

        _hasPending = false;
        bool moved;
        try
        {
            moved = await _pending.ConfigureAwait(false);
        }
        catch (Exception exception) when (Wraps(exception))
        {
            throw new ExecutionException(Context.PlanDigest, Path, exception.Message, exception);
        }

        if (!moved)
        {
            _done = true;
            Current = null;
            return false;
        }

        Current = _batches!.Current;
        return true;
    }

    /// <summary>
    /// The batches as a sequence. The operator is its own enumerable and its own enumerator: an
    /// operator has exactly one consumer, and a state machine per edge per execution is a
    /// measurable part of §5's fixed cost.
    /// </summary>
    public IAsyncEnumerable<ColumnarBatch> ExecuteAsync(CancellationToken ct)
    {
        _token = ct;
        return this;
    }

    IAsyncEnumerator<ColumnarBatch> IAsyncEnumerable<ColumnarBatch>.GetAsyncEnumerator(
        CancellationToken cancellationToken)
    {
        if (cancellationToken.CanBeCanceled)
        {
            _token = cancellationToken;
        }

        return this;
    }

    ColumnarBatch IAsyncEnumerator<ColumnarBatch>.Current => Current!;

    ValueTask<bool> IAsyncEnumerator<ColumnarBatch>.MoveNextAsync() =>
        TryNext(out _) ? ValueTask.FromResult(true)
        : _done ? ValueTask.FromResult(false)
        : WaitAsync(_token);

    /// <summary>
    /// Ends the operator's iteration: the body's enumerator first, so its <c>finally</c> blocks
    /// return what they rented from the arena, then whatever the operator itself holds. An operator
    /// stays usable afterwards — <see cref="Restart"/> gives the next execution a fresh iteration —
    /// which is what lets a warm tree be handed on (ADR 0011 §3, re-measured in ADR 0019).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_batches is { } enumerator)
        {
            _batches = null;
            _done = true;
            Current = null;
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        await DisposeCoreAsync().ConfigureAwait(false);
    }


    /// <summary>What this operator holds beyond its own iteration — usually its input.</summary>
    protected abstract ValueTask DisposeCoreAsync();

    /// <summary>The operator's own iteration. Failures are wrapped by the pull path.</summary>
    protected abstract IAsyncEnumerable<ColumnarBatch> RunAsync(CancellationToken ct);

    private IAsyncEnumerator<ColumnarBatch> Start(CancellationToken ct) =>
        RunAsync(ct).GetAsyncEnumerator(ct);

    /// <summary>
    /// Points a reused operator at the next execution. A warm tree is handed on between executions
    /// (ADR 0011 §3, re-measured in ADR 0019), and the pull state — the body's enumerator, whether it
    /// finished, what it last produced — belongs to one execution, not to the tree.
    /// </summary>
    private void Restart()
    {
        if (_generation == Context.Generation)
        {
            return;
        }

        _generation = Context.Generation;
        _batches = null;
        _hasPending = false;
        _done = false;
        Current = null;
    }

    /// <summary>
    /// Cancellation is not a failure, and an <see cref="ExecutionException"/> from a child already
    /// names the operator that actually broke — re-wrapping it would bury the useful path.
    /// </summary>
    private static bool Wraps(Exception exception) =>
        exception is not (OperationCanceledException or ExecutionException);
}
