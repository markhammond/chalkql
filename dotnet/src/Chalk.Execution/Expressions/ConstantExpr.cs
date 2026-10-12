using Chalk.Catalog;
using Chalk.Execution.Vectors;

namespace Chalk.Execution.Expressions;

/// <summary>
/// An expression that reads no column and is the same for every row of an execution, answered
/// once for the execution rather than once a row (D337): <c>CAST(? AS TIMESTAMP(9)) - INTERVAL '5'
/// MINUTE</c>, say. Its leaves are literals, parameters and the current time, and nothing in it is
/// <c>VOLATILE</c>.
/// </summary>
/// <remarks>
/// It is evaluated the first time a batch with a selected lane asks for it, over one row, and kept
/// as a scalar until the execution changes. A batch that selects no lane gets a NULL without the
/// expression being evaluated, so an expression that would raise — a division by zero — raises
/// exactly where evaluating it lane by lane would have.
/// </remarks>
internal sealed class ConstantExpr : IVectorExpr
{
    private readonly IVectorExpr _inner;
    private ScalarValue _value;
    private int _generation = -1;

    public ConstantExpr(IVectorExpr inner)
    {
        _inner = inner;
        _value = ScalarValue.Null(inner.Type);
    }

    public ChalkType Type => _inner.Type;

    /// <summary>How many times this execution has computed the value: once, or not at all. For tests.</summary>
    public int Computed { get; private set; }

    public Vector Evaluate(EvalContext context)
    {
        if (_generation != context.Generation)
        {
            if (!AnySelected(context))
            {
                return Vector.FromScalar(ScalarValue.Null(Type), context.Length);
            }

            var answer = context.EvaluateOnce(_inner);
            _value = answer.IsScalar ? answer.Scalar : ScalarValue.FromLane(answer.View, 0, Type);
            _generation = context.Generation;
            Computed++;
        }

        return Vector.FromScalar(_value, context.Length);
    }

    private static bool AnySelected(EvalContext context)
    {
        if (context.Length == 0)
        {
            return false;
        }

        var mask = context.SelectionMask;
        if (mask.IsEmpty)
        {
            return true;
        }

        foreach (var bits in mask)
        {
            if (bits != 0)
            {
                return true;
            }
        }

        return false;
    }
}
