using Chalk.Catalog;
using Chalk.Execution.Tests.Harness;
using Chalk.Ir;
using Chalk.TestKit;
using IrType = Chalk.Ir.Type;

namespace Chalk.Execution.Tests;

/// <summary>
/// D316 — ILIKE is LIKE with the value and the pattern's literals folded by the culture-invariant
/// simple lowercase mapping LOWER applies: so <c>É</c> matches <c>é</c>, <c>Σ</c> matches <c>σ</c>
/// and the Kelvin sign matches <c>k</c>, while the Turkish capital <c>İ</c>, the final sigma and
/// <c>ß</c> against <c>SS</c> do not — which is exactly what <c>LOWER(x) LIKE LOWER(p)</c> says. Both
/// engines, a literal pattern and a parameter one.
/// </summary>
public sealed class IlikeKernelTests
{
    private static readonly TestTable Table = new()
    {
        Name = "cased",
        Columns = [("s", ChalkType.String(nullable: true))],
        Rows =
        [
            ["KB-1"], ["kb-2"], ["Kb-3"], ["ÉPÉE"], ["épée"],
            ["Σ"], ["σ"], ["ς"], ["K"], ["k"], ["İ"], ["i"],
            ["STRASSE"], ["straße"], ["\U00010400"], ["\U00010428"], ["ABC100%"], [null],
        ],
    };

    private static readonly TestSource Source = TestData.Source(Table);

    public static TheoryData<string, string?, int[]> Cases() => new()
    {
        { "kb%", null, [0, 1, 2] },
        { "KB%", null, [0, 1, 2] },
        { "épée", null, [3, 4] },
        { "ÉPÉE", null, [3, 4] },
        { "σ", null, [5, 6] },
        { "k", null, [8, 9] },
        { "K", null, [8, 9] },
        { "i", null, [11] },
        { "straße", null, [13] },
        { "\U00010428", null, [14, 15] },
        { "_", null, [5, 6, 7, 8, 9, 10, 11, 14, 15] },
        { "abc100!%", "!", [16] },
        { "%E", null, [3, 4, 12, 13] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Ilike_folds_as_lower_does(string pattern, string? escape, int[] matched)
    {
        var row = Table.RowType();
        var expected = Expected(matched);

        foreach (var reference in new[] { false, true })
        {
            Assert.Equal(expected, await Runner.ProjectAsync(Ilike(row, IrBuilder.Lit(pattern), escape), Source, Table, reference: reference));
            Assert.Equal(
                expected,
                await Runner.ProjectAsync(
                    Ilike(row, IrBuilder.Param(0, IrBuilder.Str(true)), escape),
                    Source,
                    Table,
                    reference: reference,
                    parameters: [pattern],
                    parameterTypes: [IrBuilder.Str(true)]));
        }
    }

    /// <summary>The same answer as LOWER(x) LIKE LOWER(p), computed by the engine itself.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Ilike_is_lower_like_lower(string pattern, string? escape, int[] matched)
    {
        _ = matched;
        var row = Table.RowType();
        var lowered = IrBuilder.Call(FunctionId.Lower, IrBuilder.Str(true), IrBuilder.Ref(row, 0));
        var like = escape is null
            ? IrBuilder.Call(FunctionId.Like, IrBuilder.Bool(true), lowered, IrBuilder.Lit(pattern.ToLowerInvariant()))
            : IrBuilder.Call(
                FunctionId.Like, IrBuilder.Bool(true), lowered, IrBuilder.Lit(pattern.ToLowerInvariant()), IrBuilder.Lit(escape));

        Assert.Equal(
            await Runner.ProjectAsync(like, Source, Table),
            await Runner.ProjectAsync(Ilike(row, IrBuilder.Lit(pattern), escape), Source, Table));
    }

    [Fact]
    public async Task A_null_pattern_answers_null()
    {
        var row = Table.RowType();

        Assert.All(
            await Runner.ProjectAsync(
                Ilike(row, IrBuilder.Param(0, IrBuilder.Str(true)), null),
                Source,
                Table,
                parameters: [null],
                parameterTypes: [IrBuilder.Str(true)]),
            Assert.Null);
    }

    /// <summary>The escape is read as written, before folding: a letter escape quotes, and folds as a literal.</summary>
    [Fact]
    public async Task A_letter_escape_is_read_before_the_fold()
    {
        var row = Table.RowType();

        Assert.Equal(
            Expected([16]),
            await Runner.ProjectAsync(Ilike(row, IrBuilder.Lit("abc100X%"), "X"), Source, Table));
        Assert.Equal(
            Expected([]),
            await Runner.ProjectAsync(Ilike(row, IrBuilder.Lit("abc100x%"), "X"), Source, Table));
    }

    [Fact]
    public void A_malformed_escape_is_refused_as_for_like()
    {
        var plan = Runner.ProjectionPlan(Ilike(Table.RowType(), IrBuilder.Lit("ab!"), "!"), Table);

        Assert.Throws<ArgumentException>(() => Runner.Compile(plan, Source));
    }

    private static List<object?> Expected(int[] matched) =>
        [.. Table.Rows.Select((r, i) => r[0] is null ? (object?)null : matched.Contains(i))];

    private static Expr Ilike(RowType row, Expr pattern, string? escape) => escape is null
        ? IrBuilder.Call(FunctionId.Ilike, IrBuilder.Bool(true), IrBuilder.Ref(row, 0), pattern)
        : IrBuilder.Call(FunctionId.Ilike, IrBuilder.Bool(true), IrBuilder.Ref(row, 0), pattern, IrBuilder.Lit(escape));
}
