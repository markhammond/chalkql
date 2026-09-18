using Chalk.Ir;

namespace Chalk.Execution.Joins;

/// <summary>
/// Splits a logical <c>Join</c>'s condition into equality keys and what is left over — Calcite's
/// <c>JoinInfo</c>, on the executor's side of the wire.
/// </summary>
/// <remarks>
/// A physical join arrives with its keys already separated; a logical one does not, and
/// <c>02-ir.md</c> §4 says a logical node is always executable. This is what lets the executor run
/// <c>Join</c> as a hash join whenever the condition contains an equality between the two inputs.
/// </remarks>
internal static class JoinConditions
{
    /// <summary>
    /// Fills <paramref name="leftKeys"/> and <paramref name="rightKeys"/> with the equalities that
    /// compare one left column with one right column, and returns the rest of the condition (null
    /// when the equalities are all of it).
    /// </summary>
    public static Expr? Split(
        Expr? condition, int leftWidth, int rightWidth, List<int> leftKeys, List<int> rightKeys)
    {
        if (condition is null)
        {
            return null;
        }

        var conjuncts = new List<Expr>();
        Flatten(condition, conjuncts);

        var rest = new List<Expr>();
        foreach (var conjunct in conjuncts)
        {
            if (TryKey(conjunct, leftWidth, leftWidth + rightWidth, out var left, out var right))
            {
                leftKeys.Add(left);
                rightKeys.Add(right);
            }
            else
            {
                rest.Add(conjunct);
            }
        }

        if (rest.Count == 0)
        {
            return null;
        }

        if (rest.Count == conjuncts.Count)
        {
            return condition;
        }

        var result = rest[0];
        for (var i = 1; i < rest.Count; i++)
        {
            var and = new ScalarCall { Function = FunctionId.And };
            and.Args.Add(result);
            and.Args.Add(rest[i]);
            result = new Expr { Type = condition.Type, Call = and };
        }

        return result;
    }

    private static void Flatten(Expr expr, List<Expr> conjuncts)
    {
        if (expr.KindCase == Expr.KindOneofCase.Call
            && expr.Call.Function == FunctionId.And
            && expr.Call.Args.Count >= 2)
        {
            foreach (var arg in expr.Call.Args)
            {
                Flatten(arg, conjuncts);
            }

            return;
        }

        conjuncts.Add(expr);
    }

    /// <summary>An <c>=</c> between one field of the left row and one of the right row.</summary>
    private static bool TryKey(Expr expr, int leftWidth, int width, out int left, out int right)
    {
        left = -1;
        right = -1;
        if (expr.KindCase != Expr.KindOneofCase.Call
            || expr.Call.Function != FunctionId.Eq
            || expr.Call.Args.Count != 2)
        {
            return false;
        }

        var a = expr.Call.Args[0];
        var b = expr.Call.Args[1];
        if (a.KindCase != Expr.KindOneofCase.FieldRef || b.KindCase != Expr.KindOneofCase.FieldRef)
        {
            return false;
        }

        var first = (int)a.FieldRef.Index;
        var second = (int)b.FieldRef.Index;
        if (first >= width || second >= width)
        {
            return false;
        }

        if (first < leftWidth && second >= leftWidth)
        {
            left = first;
            right = second - leftWidth;
            return true;
        }

        if (second < leftWidth && first >= leftWidth)
        {
            left = second;
            right = first - leftWidth;
            return true;
        }

        return false;
    }
}
