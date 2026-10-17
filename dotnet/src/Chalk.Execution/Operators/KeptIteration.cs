using Chalk.Sources;

namespace Chalk.Execution.Operators;

/// <summary>
/// An operator's iteration made with the operator and begun again for each execution (F244). A body
/// written as an async iterator makes a state machine each time the operator runs, and a maintained
/// plan runs its operators at every update; one of these is made once instead.
/// </summary>
/// <remarks>
/// <see cref="MoveNextAsync"/> completes at once while the operator's input does, as an in-process
/// source's always does (D63), and waits only when the input must be waited for. What an execution
/// rents is rented when it starts and returned when its iteration ends, at its last batch or when it
/// is disposed unfinished, as a body's <c>finally</c> returned it.
/// </remarks>
internal abstract class KeptIteration : IAsyncEnumerator<ColumnarBatch>
{
    /// <summary>The token the execution runs under.</summary>
    protected CancellationToken Token { get; private set; }

    public ColumnarBatch Current { get; protected set; } = null!;

    /// <summary>Points the iteration at a new execution, before its first batch.</summary>
    public IAsyncEnumerator<ColumnarBatch> Begin(CancellationToken ct)
    {
        Token = ct;
        Current = null!;
        Started();
        return this;
    }

    /// <summary>Puts the iteration back at its start. Nothing here may throw: the first move does the work.</summary>
    protected abstract void Started();

    public abstract ValueTask<bool> MoveNextAsync();

    /// <summary>Ends the execution's iteration, at its end or abandoned, returning what it rented.</summary>
    public virtual ValueTask DisposeAsync() => default;
}

/// <summary>An operator that has its batches in hand when it runs, or works them out first.</summary>
internal interface IBatchesInHand
{
    /// <summary>Makes this run's batches ready, and says how many there are.</summary>
    ValueTask<int> PrepareAsync(CancellationToken ct);

    /// <summary>This run's batch at <paramref name="index"/>.</summary>
    ColumnarBatch Batch(int index);

    /// <summary>The run is over, at its last batch or abandoned: what it rented goes back.</summary>
    void Ended()
    {
    }
}

/// <summary>The kept iteration of an <see cref="IBatchesInHand"/>: its batches, prepared, one by one.</summary>
internal sealed class BatchesInHand(IBatchesInHand owner) : KeptIteration
{
    private int _count;
    private int _next;
    private bool _prepared;
    private bool _ended;

    protected override void Started()
    {
        _count = 0;
        _next = 0;
        _prepared = false;
        _ended = false;
    }

    public override ValueTask<bool> MoveNextAsync()
    {
        if (!_prepared)
        {
            var preparing = owner.PrepareAsync(Token);
            if (!preparing.IsCompletedSuccessfully)
            {
                return PreparedAsync(preparing);
            }

            _count = preparing.Result;
            _prepared = true;
        }

        return new ValueTask<bool>(Next());
    }

    private async ValueTask<bool> PreparedAsync(ValueTask<int> preparing)
    {
        _count = await preparing.ConfigureAwait(false);
        _prepared = true;
        return Next();
    }

    private bool Next()
    {
        if (_next >= _count)
        {
            End();
            return false;
        }

        Token.ThrowIfCancellationRequested();
        Current = owner.Batch(_next++);
        return true;
    }

    private void End()
    {
        if (!_ended)
        {
            _ended = true;
            owner.Ended();
        }
    }

    public override ValueTask DisposeAsync()
    {
        End();
        return default;
    }
}

/// <summary>An operator that turns each batch of its input into at most one of its own.</summary>
internal interface IStreams
{
    /// <summary>The operator's input.</summary>
    IBatchOperator Input { get; }

    /// <summary>An execution starts: what it needs is rented.</summary>
    void Started()
    {
    }

    /// <summary>The operator's batch for <paramref name="input"/>, or null when it gives none for it.</summary>
    ColumnarBatch? Step(ColumnarBatch input);

    /// <summary>The execution's iteration is over: what it rented goes back.</summary>
    void Ended()
    {
    }
}

/// <summary>The kept iteration of an <see cref="IStreams"/>: its input pulled batch by batch, each stepped.</summary>
internal sealed class Streaming(IStreams owner) : KeptIteration
{
    private bool _started;

    protected override void Started() => _started = false;

    public override ValueTask<bool> MoveNextAsync()
    {
        var input = owner.Input;
        if (!_started)
        {
            // The input runs under this execution's token, as a body's await foreach would have it.
            _ = input.ExecuteAsync(Token);
            owner.Started();
            _started = true;
        }

        while (input.TryNext(out var batch))
        {
            if (owner.Step(batch!) is { } output)
            {
                Current = output;
                return new ValueTask<bool>(true);
            }
        }

        if (input.IsDone)
        {
            End();
            return new ValueTask<bool>(false);
        }

        return WaitAsync(input);
    }

    /// <summary>The input could not answer at once: waited for, then pulled again as long as it answers.</summary>
    private async ValueTask<bool> WaitAsync(IBatchOperator input)
    {
        while (await input.WaitAsync(Token).ConfigureAwait(false))
        {
            if (owner.Step(input.Current!) is { } output)
            {
                Current = output;
                return true;
            }

            while (input.TryNext(out var batch))
            {
                if (owner.Step(batch!) is { } next)
                {
                    Current = next;
                    return true;
                }
            }

            if (input.IsDone)
            {
                break;
            }
        }

        End();
        return false;
    }

    private void End()
    {
        if (_started)
        {
            _started = false;
            owner.Ended();
        }
    }

    public override ValueTask DisposeAsync()
    {
        End();
        return default;
    }
}
