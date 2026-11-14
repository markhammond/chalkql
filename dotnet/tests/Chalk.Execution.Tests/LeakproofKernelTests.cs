using Chalk.Catalog;
using Chalk.Execution.Tests.Harness;
using Chalk.Ir;
using Chalk.TestKit;
using IrType = Chalk.Ir.Type;

namespace Chalk.Execution.Tests;

/// <summary>
/// The leakproof list's own rule (F302, design 82 §5): a function is on it only if it raises for no input
/// on either executor, NaN, the infinities and each type's extremes included. Otherwise a value an author
/// can make, an infinity for a chosen value, could be turned into an error further up, and the barrier
/// and the policy's expressions rely on it not being. Each case runs every row of edge values through one
/// leakproof function, on both engines, and asserts only that nothing raises.
/// </summary>
public sealed class LeakproofKernelTests
{
    private static readonly TestTable Table = new()
    {
        Name = "edges",
        Columns = [("d", ChalkType.Float64(nullable: true)), ("s", ChalkType.String(nullable: true))],
        Rows =
        [
            [double.NaN, ""],
            [double.PositiveInfinity, "x"],
            [double.NegativeInfinity, "\U0001F600"],
            [double.MaxValue, "\u0000"],
            [double.MinValue, "%_"],
            [double.Epsilon, null],
            [0.0, new string('a', 10_000)],
            [-0.0, "İ"],
            [null, "ß"],
            [1e308, "-1"],
        ],
    };

    private static readonly TestSource Source = TestData.Source(Table);

    private static Expr D => IrBuilder.Ref(Table.RowType(), 0);

    private static Expr S => IrBuilder.Ref(Table.RowType(), 1);

    private static IrType Double => IrBuilder.Fp64(true);

    private static IrType Str => IrBuilder.Str(true);

    private static IrType Bool => IrBuilder.Bool(true);

    public static TheoryData<string, bool> Cases()
    {
        var data = new TheoryData<string, bool>();
        foreach (var name in Expressions().Keys)
        {
            data.Add(name, false);
            data.Add(name, true);
        }

        return data;
    }

    private static Dictionary<string, Expr> Expressions()
    {
        var all = new Dictionary<string, Expr>(StringComparer.Ordinal);
        foreach (var f in new[] { FunctionId.Eq, FunctionId.Ne, FunctionId.Lt, FunctionId.Le, FunctionId.Gt, FunctionId.Ge })
        {
            all[$"{f} over doubles"] = IrBuilder.Call(f, Bool, D, D);
            all[$"{f} over strings"] = IrBuilder.Call(f, Bool, S, S);
        }

        foreach (var f in new[] { FunctionId.IsDistinctFrom, FunctionId.IsNotDistinctFrom })
        {
            all[f.ToString()] = IrBuilder.Call(f, IrBuilder.Bool(), D, D);
        }

        foreach (var f in new[] { FunctionId.IsNull, FunctionId.IsNotNull })
        {
            all[f.ToString()] = IrBuilder.Call(f, IrBuilder.Bool(), D);
        }

        var test = IrBuilder.Call(FunctionId.Gt, Bool, D, IrBuilder.Lit(0.0));
        foreach (var f in new[] { FunctionId.IsTrue, FunctionId.IsNotTrue, FunctionId.IsFalse, FunctionId.IsNotFalse })
        {
            all[f.ToString()] = IrBuilder.Call(f, IrBuilder.Bool(), test);
        }

        all["AND, OR, NOT"] = IrBuilder.Call(
            FunctionId.Or, Bool,
            IrBuilder.Call(FunctionId.And, Bool, test, IrBuilder.Call(FunctionId.Not, Bool, test)),
            IrBuilder.Call(FunctionId.Eq, Bool, S, IrBuilder.Lit("x")));
        all["COALESCE"] = IrBuilder.Call(FunctionId.Coalesce, Double, D, IrBuilder.Lit(1.0));
        all["UPPER"] = IrBuilder.Call(FunctionId.Upper, Str, S);
        all["LOWER"] = IrBuilder.Call(FunctionId.Lower, Str, S);
        all["concatenation"] = IrBuilder.Call(FunctionId.Concat, Str, S, S);
        all["CHAR_LENGTH"] = IrBuilder.Call(FunctionId.CharLength, IrBuilder.I32(true), S);
        all["SUBSTRING of a literal length"] = IrBuilder.Call(
            FunctionId.Substring, Str, S, IrBuilder.Lit(2L), IrBuilder.Lit(3L));
        all["SUBSTRING with no length, from before the start"] = IrBuilder.Call(
            FunctionId.Substring, Str, S, IrBuilder.Lit(-5L));
        all["LIKE"] = IrBuilder.Call(FunctionId.Like, Bool, S, IrBuilder.Lit("%x%"));
        all["FINGERPRINT"] = IrBuilder.Call(FunctionId.Fingerprint, Str, S, IrBuilder.Lit("k"));
        all["PRESENT"] = IrBuilder.Call(FunctionId.Present, IrBuilder.Bool(), S);
        foreach (var f in new[] { FunctionId.Round, FunctionId.Floor, FunctionId.Ceil })
        {
            all[$"{f} of a double"] = IrBuilder.Call(f, Double, D);
        }

        all["ROUND of a double to digits"] = IrBuilder.Call(FunctionId.Round, Double, D, IrBuilder.Lit(2));
        // MODULUS is not on the list: ChalkQL runs it over exact kinds alone.
        foreach (var f in new[] { FunctionId.Add, FunctionId.Subtract, FunctionId.Multiply, FunctionId.Divide })
        {
            all[$"{f} over doubles"] = IrBuilder.Call(f, Double, D, D);
            all[$"{f} by zero"] = IrBuilder.Call(f, Double, D, IrBuilder.Lit(0.0));
        }

        all["negation of a double"] = IrBuilder.Call(FunctionId.Negate, Double, D);
        return all;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_leakproof_function_raises_for_no_input(string function, bool reference)
    {
        var values = await Runner.ProjectAsync(Expressions()[function], Source, Table, reference: reference);

        Assert.Equal(Table.Rows.Count, values.Count);
    }
}
