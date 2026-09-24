using Chalk.Catalog;
using Chalk.Execution.Vectors;

namespace Chalk.Execution.Expressions;

/// <summary>
/// A compiled subtree one operator's expressions may name more than once (D293, D299): a built-in
/// call, a cast, a <c>CASE</c>, an <c>IN</c>, a field access or a user call, holding no
/// <c>VOLATILE</c> call. The expression compiler hands the same node to every occurrence, and once a
/// second occurrence has asked, the node answers once per batch — it remembers the batch it last
/// answered and hands the same vector to every expression that asks within it.
/// </summary>
/// <remarks>
/// <para>
/// The batch is a sufficient key because an operator evaluates all of its expressions under the
/// batch's one selection, and only the four operators that evaluate expressions — the projection,
/// the filter, the hash aggregate and a pair join's residual — point their context at a batch, each
/// once per batch. No expression narrows the context for a sub-expression: a <c>CASE</c> evaluates
/// every branch over the whole batch and chooses per lane, and <c>AND</c> and <c>OR</c> evaluate
/// every operand. One that did would have to make the selection part of the key.
/// </para>
/// <para>
/// A cached vector stays valid for the batch because no consumer writes into what it reads: an
/// expression's inputs are read-only views, and every node writes into scratch of its own. Until a
/// second occurrence asks, the node is a pass-through: one flag test and no caching, so a subtree
/// named once evaluates exactly as it did before sharing existed.
/// </para>
/// </remarks>
internal sealed class SharedExpr : IVectorExpr
{
    private readonly IVectorExpr _inner;
    private readonly SharingTally? _tally;
    private bool _shared;

    /// <summary>
    /// The execution and batch this node last answered, and what it answered. The execution too, so
    /// that a context no operator ever pointed at a batch cannot carry an answer over.
    /// </summary>
    private long _answeredBatch = -1;
    private int _answeredGeneration = -1;
    private Vector _answer;

    public SharedExpr(IVectorExpr inner, SharingTally? tally)
    {
        _inner = inner;
        _tally = tally;
    }

    public ChalkType Type => _inner.Type;

    /// <summary>Whether a second occurrence has asked for this node.</summary>
    public bool IsShared => _shared;

    /// <summary>Marks this node as answering for more than one occurrence. Only the compiler calls it.</summary>
    public void Share() => _shared = true;

    public Vector Evaluate(EvalContext context)
    {
        if (!_shared)
        {
            return _inner.Evaluate(context);
        }

        if (_answeredBatch == context.BatchSequence && _answeredGeneration == context.Generation)
        {
            _tally?.NoteReused();
            return _answer;
        }

        var answer = _inner.Evaluate(context);
        _answeredBatch = context.BatchSequence;
        _answeredGeneration = context.Generation;
        _answer = answer;
        _tally?.NoteComputed();
        return answer;
    }
}

/// <summary>
/// What one operator's expression sharing came to (D299), read by tests and by nothing that
/// decides anything: how many distinct nodes its expressions share, and how often a shared node ran
/// and how often it answered from its batch's result. One per operator in the plan, shared by every
/// execution of it; the running counts are plain increments, exact for one execution at a time.
/// </summary>
internal sealed class SharingTally
{
    /// <summary>Distinct nodes handed to more than one expression of the operator.</summary>
    public int SharedNodes { get; private set; }

    /// <summary>Evaluations of a shared node that ran it: one per batch per shared node.</summary>
    public long Computed { get; private set; }

    /// <summary>Evaluations of a shared node answered from the result its batch already had.</summary>
    public long Reused { get; private set; }

    /// <summary>
    /// A compiler's count of shared nodes so far. Every execution compiles the operator's expressions
    /// afresh and counts up to the same number, so the largest seen is the operator's.
    /// </summary>
    internal void Observe(int sharedNodes) => SharedNodes = Math.Max(SharedNodes, sharedNodes);

    internal void NoteComputed() => Computed++;

    internal void NoteReused() => Reused++;
}
