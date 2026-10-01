using Chalk.Execution.Tests.Harness;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;
using IrType = Chalk.Ir.Type;

namespace Chalk.Execution.Tests;

/// <summary>
/// The string kernels of <c>02-ir.md</c> §6: CONCAT, UPPER/LOWER, CHAR_LENGTH in code points,
/// SQL:2011 SUBSTRING, and LIKE with a constant pattern.
/// </summary>
public sealed class StringKernelTests
{
    private static readonly TestTable Table = TestData.Strings;
    private static readonly TestSource Source = TestData.Source(Table, TestData.SingleRow, TestData.Empty);

    public static TheoryData<int> BatchSizes() => TestData.BatchSizeData;

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Concat_is_null_when_any_operand_is(int batchSize)
    {
        var row = Table.RowType();
        var expr = IrBuilder.Call(
            FunctionId.Concat,
            IrBuilder.Str(true),
            IrBuilder.Ref(row, 0),
            IrBuilder.Lit("-x"));

        var values = await Runner.ProjectAsync(expr, Source, Table, batchSize);

        Assert.Equal("abcdef-x", values[0]);
        Assert.Equal("-x", values[1]);      // the empty string is not a NULL
        Assert.Null(values[5]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Char_length_counts_code_points(int batchSize)
    {
        var row = Table.RowType();
        var expr = IrBuilder.Call(FunctionId.CharLength, IrBuilder.I32(true), IrBuilder.Ref(row, 0));

        var values = await Runner.ProjectAsync(expr, Source, Table, batchSize);

        Assert.Equal(6L, values[0]);       // abcdef
        Assert.Equal(0L, values[1]);       // ""
        Assert.Equal(4L, values[2]);       // épée: four code points, six UTF-8 bytes
        Assert.Equal(2L, values[3]);       // an astral pair is one code point plus one ASCII
        Assert.Null(values[5]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Upper_and_lower_are_culture_invariant(int batchSize)
    {
        var row = Table.RowType();
        var upper = IrBuilder.Call(FunctionId.Upper, IrBuilder.Str(true), IrBuilder.Ref(row, 0));
        var lower = IrBuilder.Call(FunctionId.Lower, IrBuilder.Str(true), IrBuilder.Ref(row, 0));

        var uppers = await Runner.ProjectAsync(upper, Source, Table, batchSize);
        var lowers = await Runner.ProjectAsync(lower, Source, Table, batchSize);

        Assert.Equal("ABCDEF", uppers[0]);
        Assert.Equal("ÉPÉE", uppers[2]);
        Assert.Equal("abcdef", lowers[0]);
        Assert.Null(uppers[5]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Substring_follows_SQL_2011_for_negative_and_overflowing_windows(int batchSize)
    {
        var row = Table.RowType();

        Assert.Equal("abc", await One(Substring(row, 1, 3), batchSize));
        Assert.Equal("ab", await One(Substring(row, -1, 4), batchSize));       // window -1..2 clipped to 1..2
        Assert.Equal(string.Empty, await One(Substring(row, -5, 4), batchSize)); // window entirely before the string
        Assert.Equal("abcdef", await One(Substring(row, 1, 100), batchSize));  // clipped to the string
        Assert.Equal("cdef", await One(Substring(row, 3, null), batchSize));   // no length: to the end
        Assert.Equal(string.Empty, await One(Substring(row, 9, 2), batchSize));

        async Task<object?> One(Expr expr, int size) =>
            (await Runner.ProjectAsync(expr, Source, Table, size))[0];
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Substring_by_code_point_not_by_utf16_unit(int batchSize)
    {
        var row = Table.RowType();
        var values = await Runner.ProjectAsync(Substring(row, 1, 1), Source, Table, batchSize);

        Assert.Equal("a", values[0]);
        Assert.Equal("é", values[2]);
        Assert.Equal("\U0001F600", values[3]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Substring_with_a_negative_length_is_a_data_error(int batchSize)
    {
        var row = Table.RowType();

        await Assert.ThrowsAsync<ExecutionException>(
            () => Runner.ProjectAsync(Substring(row, 1, -1), Source, Table, batchSize));
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Like_handles_percent_underscore_and_an_escape(int batchSize)
    {
        var row = Table.RowType();

        Assert.Equal(
            [true, false, false, false, true, null],
            await Runner.ProjectAsync(Like(row, "abc%"), Source, Table, batchSize));
        Assert.Equal(
            [true, false, false, false, true, null],
            await Runner.ProjectAsync(Like(row, "a_c%"), Source, Table, batchSize));
        Assert.Equal(
            [false, true, false, false, false, null],
            await Runner.ProjectAsync(Like(row, string.Empty), Source, Table, batchSize));
        // '%' matches zero or more code points, so it matches the empty string too.
        Assert.Equal(
            [true, true, true, true, true, null],
            await Runner.ProjectAsync(Like(row, "%"), Source, Table, batchSize));

        // The escape makes a literal percent sign, so only a string that really ends in one matches.
        Assert.Equal(
            [false, false, false, false, true, null],
            await Runner.ProjectAsync(Like(row, "%100!%", "!"), Source, Table, batchSize));
        Assert.Equal(
            [false, false, false, false, false, null],
            await Runner.ProjectAsync(Like(row, "100!%", "!"), Source, Table, batchSize));
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Like_matches_by_code_point(int batchSize)
    {
        var row = Table.RowType();

        Assert.Equal(
            [false, false, true, false, false, null],
            await Runner.ProjectAsync(Like(row, "_p_e"), Source, Table, batchSize));
    }

    /// <summary>
    /// D314: a parameter pattern is compiled once per execution, when its value is known, and gives
    /// exactly what the same pattern as a literal gives — on both engines. A NULL pattern makes every
    /// row's answer NULL.
    /// </summary>
    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Like_with_a_parameter_pattern_is_the_literal_pattern_bound_late(int batchSize)
    {
        var row = Table.RowType();
        IrType[] types = [IrBuilder.Str(true)];
        var pattern = IrBuilder.Call(
            FunctionId.Like, IrBuilder.Bool(true), IrBuilder.Ref(row, 0), IrBuilder.Param(0, IrBuilder.Str(true)));
        var escaped = IrBuilder.Call(
            FunctionId.Like,
            IrBuilder.Bool(true),
            IrBuilder.Ref(row, 0),
            IrBuilder.Param(0, IrBuilder.Str(true)),
            IrBuilder.Lit("!"));

        foreach (var reference in new[] { false, true })
        {
            foreach (var value in new[] { "abc%", "a_c%", "%", string.Empty, "_p_e" })
            {
                Assert.Equal(
                    await Runner.ProjectAsync(Like(row, value), Source, Table, batchSize),
                    await Runner.ProjectAsync(
                        pattern, Source, Table, batchSize, reference, [value], types));
            }

            Assert.Equal(
                await Runner.ProjectAsync(Like(row, "%100!%", "!"), Source, Table, batchSize),
                await Runner.ProjectAsync(
                    escaped, Source, Table, batchSize, reference, ["%100!%"], types));

            Assert.All(
                await Runner.ProjectAsync(pattern, Source, Table, batchSize, reference, [null], types),
                Assert.Null);
        }
    }

    /// <summary>
    /// D312: a parameter's value malformed under its escape is refused before the execution starts,
    /// on both engines, with the rule it breaks and without the value.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Like_with_a_malformed_parameter_pattern_is_refused_before_it_runs(bool reference)
    {
        var row = Table.RowType();
        var escaped = IrBuilder.Call(
            FunctionId.Like,
            IrBuilder.Bool(true),
            IrBuilder.Ref(row, 0),
            IrBuilder.Param(0, IrBuilder.Str(true)),
            IrBuilder.Lit("!"));

        var error = await Assert.ThrowsAsync<ParameterBindingException>(
            () => Runner.ProjectAsync(
                escaped, Source, Table, 4096, reference, ["secret!"], [IrBuilder.Str(true)]));
        Assert.Equal(ChalkErrorCodes.ParameterBinding, error.Code);

        Assert.Contains("ends with its escape character", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
    }

    /// <summary>D312: a literal pattern or escape the rules refuse is refused at compilation.</summary>
    [Theory]
    [InlineData("ab!", "!", "ends with its escape character")]
    [InlineData("a!b", "!", "before an ordinary character")]
    [InlineData("abc%", "!!", "exactly one character")]
    [InlineData("abc%", "", "not the empty string")]
    public void Like_with_a_malformed_literal_is_refused_at_compilation(
        string pattern, string escape, string defect)
    {
        var plan = Runner.ProjectionPlan(Like(Table.RowType(), pattern, escape), Table);

        var error = Assert.Throws<ArgumentException>(() => Runner.Compile(plan, Source));

        Assert.Contains(defect, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Like_with_a_non_constant_pattern_is_rejected_at_compilation()
    {
        var row = Table.RowType();
        var expr = IrBuilder.Call(
            FunctionId.Like, IrBuilder.Bool(true), IrBuilder.Ref(row, 0), IrBuilder.Ref(row, 0));
        var plan = Runner.ProjectionPlan(expr, Table);

        var failure = Assert.Throws<UnsupportedFeatureException>(() => Runner.Compile(plan, Source));

        Assert.Contains("LIKE", failure.Feature, StringComparison.Ordinal);
    }

    [Fact]
    public async Task String_kernels_over_a_single_row_and_over_no_rows()
    {
        var singleSource = TestData.Source(TestData.SingleRow);
        var single = TestData.SingleRow.RowType();
        var upper = IrBuilder.Call(FunctionId.Upper, IrBuilder.Str(), IrBuilder.Ref(single, 1));
        Assert.Equal(["ONLY"], await Runner.ProjectAsync(upper, singleSource, TestData.SingleRow));

        var emptyRow = TestData.Empty.RowType();
        var length = IrBuilder.Call(
            FunctionId.CharLength, IrBuilder.I32(true), IrBuilder.Ref(emptyRow, 5));
        Assert.Empty(await Runner.ProjectAsync(length, Source, TestData.Empty));
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Both_engines_agree_on_the_string_kernels(int batchSize)
    {
        var row = Table.RowType();
        foreach (var expr in new[]
                 {
                     IrBuilder.Call(FunctionId.Upper, IrBuilder.Str(true), IrBuilder.Ref(row, 0)),
                     IrBuilder.Call(FunctionId.Lower, IrBuilder.Str(true), IrBuilder.Ref(row, 0)),
                     IrBuilder.Call(FunctionId.CharLength, IrBuilder.I32(true), IrBuilder.Ref(row, 0)),
                     Substring(row, 2, 3),
                     Substring(row, -2, 5),
                     Substring(row, 2, null),
                     Like(row, "%e%"),
                     Like(row, "a_c___"),
                     IrBuilder.Call(
                         FunctionId.Concat,
                         IrBuilder.Str(true),
                         IrBuilder.Ref(row, 0),
                         IrBuilder.Lit("!")),
                 })
        {
            var vectorised = await Runner.ProjectAsync(expr, Source, Table, batchSize);
            var expected = await Runner.ProjectAsync(expr, Source, Table, batchSize, reference: true);
            Assert.Equal(expected, vectorised);
        }
    }

    private static Expr Substring(RowType row, int start, int? length) => length is null
        ? IrBuilder.Call(
            FunctionId.Substring, IrBuilder.Str(true), IrBuilder.Ref(row, 0), IrBuilder.Lit(start))
        : IrBuilder.Call(
            FunctionId.Substring,
            IrBuilder.Str(true),
            IrBuilder.Ref(row, 0),
            IrBuilder.Lit(start),
            IrBuilder.Lit(length.Value));

    private static Expr Like(RowType row, string pattern, string? escape = null) => escape is null
        ? IrBuilder.Call(
            FunctionId.Like, IrBuilder.Bool(true), IrBuilder.Ref(row, 0), IrBuilder.Lit(pattern))
        : IrBuilder.Call(
            FunctionId.Like,
            IrBuilder.Bool(true),
            IrBuilder.Ref(row, 0),
            IrBuilder.Lit(pattern),
            IrBuilder.Lit(escape));
}
