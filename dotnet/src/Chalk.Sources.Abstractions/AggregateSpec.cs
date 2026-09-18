namespace Chalk.Sources;

/// <summary>One row folded into a group's state (D80). The state is a struct held in arena memory.</summary>
public delegate void Accumulate<TState, in TIn>(ref TState state, TIn value)
    where TState : struct;

/// <summary>
/// The aggregate protocol of Tier 1 (D80,
/// <c>docs/design/17-user-defined-functions.md</c> §3): initialise, add, optionally remove and
/// merge, finish.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Remove"/> and <see cref="Merge"/> are what decide the window algorithm, and declaring
/// one is a promise about arithmetic, not a hint. <see cref="Remove"/> says the state can be moved
/// backwards exactly, which lets a sliding frame be maintained rather than recomputed;
/// <see cref="Merge"/> says two partial states combine associatively, which lets the segment tree of
/// step 19 answer any frame in O(log n). With neither, a frame is recomputed from its rows — the
/// same answer, more work.
/// </para>
/// <para>
/// <typeparamref name="TState"/> is a struct so that a group's state lives in an arena-owned array
/// and accumulation allocates nothing. It must be self-contained: a reference field would put an
/// object per group on the heap, which is exactly what the arena exists to avoid.
/// </para>
/// </remarks>
/// <typeparam name="TState">The per-group state.</typeparam>
/// <typeparam name="TIn">The CLR type of the value being aggregated.</typeparam>
/// <typeparam name="TOut">The CLR type of the result.</typeparam>
public sealed class AggregateSpec<TState, TIn, TOut>
    where TState : struct
{
    /// <summary>The state of an empty group.</summary>
    public required Func<TState> Init { get; init; }

    /// <summary>Folds one non-NULL value in. NULL arguments never reach it.</summary>
    public required Accumulate<TState, TIn> Add { get; init; }

    /// <summary>
    /// Undoes one <see cref="Add"/>, in any order. Enables sliding frames without recomputation —
    /// PostgreSQL's inverse transition function. Null when the aggregate has no exact inverse.
    /// </summary>
    public Accumulate<TState, TIn>? Remove { get; init; }

    /// <summary>
    /// Combines two states. Enables the segment tree and partial aggregation. Must be associative
    /// and must treat <see cref="Init"/>'s value as the identity.
    /// </summary>
    public Func<TState, TState, TState>? Merge { get; init; }

    /// <summary>
    /// The group's answer. Called once per group, and on the state of an empty group when the query
    /// asks for one — an aggregate that has no answer for an empty group returns the type's NULL by
    /// declaring a nullable <typeparamref name="TOut"/>.
    /// </summary>
    public required Func<TState, TOut> Finish { get; init; }
}
