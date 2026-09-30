using Chalk.Execution.Tests.Harness;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;

namespace Chalk.Execution.Tests;

/// <summary>
/// A membership over a context list the executor holds (F161): the tuple of a row's columns against
/// the rows the execution bound under the list's name — SQL's <c>IN</c> over a tuple, three-valued,
/// answered by both engines alike.
/// </summary>
/// <remarks>
/// <see cref="TestData.Numbers"/> has a NULL in every column, a NaN, a negative zero and a decimal
/// zero, which is every case the set's encoding has to get right: TRUE where some row equals the
/// tuple in every column, UNKNOWN where none does and some row differs from it only where one side is
/// NULL, FALSE otherwise.
/// </remarks>
public sealed class ContextMembershipKernelTests
{
    private static readonly TestTable Table = TestData.Numbers;
    private static readonly TestSource Source = TestData.Source(Table);

    public static TheoryData<int, bool> Engines()
    {
        var data = new TheoryData<int, bool>();
        foreach (var batchSize in Runner.BatchSizes)
        {
            data.Add(batchSize, false);
            data.Add(batchSize, true);
        }

        return data;
    }

    private static Expr Membership(string list, params int[] columns)
    {
        var row = Table.RowType();
        var membership = new ContextMembership { List = list };
        foreach (var column in columns)
        {
            membership.Columns.Add(IrBuilder.Ref(row, column));
        }

        return new Expr { Type = IrBuilder.Bool(nullable: true), ContextMembership = membership };
    }

    private static Dictionary<string, IReadOnlyList<IReadOnlyList<object?>>> Lists(
        string name, params object?[][] rows) =>
        new(StringComparer.Ordinal) { [name] = rows };

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_key_is_in_the_list_or_is_not_and_a_null_key_is_unknown(int batchSize, bool reference)
    {
        var values = await Runner.ProjectAsync(
            Membership("orgs", 0), Source, Table, Lists("orgs", [1], [4], [9]), batchSize, reference);

        // i32: 1, 2, NULL, 4, 5, -6, 7, 0, 9.
        Assert.Equal([true, false, null, true, false, false, false, false, true], values);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_null_in_the_list_leaves_every_miss_unknown(int batchSize, bool reference)
    {
        var values = await Runner.ProjectAsync(
            Membership("orgs", 0), Source, Table, Lists("orgs", [1], [null]), batchSize, reference);

        Assert.Equal([true, null, null, null, null, null, null, null, null], values);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Nothing_is_in_an_empty_list_a_null_included(int batchSize, bool reference)
    {
        var values = await Runner.ProjectAsync(
            Membership("orgs", 0), Source, Table, Lists("orgs"), batchSize, reference);

        Assert.All(values, value => Assert.Equal(false, value));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_tuple_matches_every_column_and_a_null_either_side_leaves_it_unknown(
        int batchSize, bool reference)
    {
        var values = await Runner.ProjectAsync(
            Membership("pairs", 0, 5),
            Source,
            Table,
            Lists("pairs", [1, "alpha"], [4, null], [9, "zzz"]),
            batchSize,
            reference);

        // (i32, s): (1, alpha) is a row; (2, beta) differs from every row where both hold a value;
        // (NULL, gamma) and (4, NULL) agree with (4, NULL) wherever both do; (5, delta) differs from
        // (4, NULL) in its first column.
        Assert.Equal([true, false, null, null, false, false, false, false, true], values);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Minus_zero_is_zero_and_nan_is_nothing(int batchSize, bool reference)
    {
        var values = await Runner.ProjectAsync(
            Membership("amounts", 2), Source, Table, Lists("amounts", [0d], [1.5d], [double.NaN]), batchSize, reference);

        // f64: 1.5, -2.5, 3.5, NaN, NULL, 0, 1e308, -0, 2.5.
        Assert.Equal([true, false, false, false, null, true, false, true, false], values);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Decimals_and_strings_compare_by_value(int batchSize, bool reference)
    {
        var decimals = await Runner.ProjectAsync(
            Membership("prices", 4), Source, Table, Lists("prices", [0m], [12345.6789m]), batchSize, reference);
        var strings = await Runner.ProjectAsync(
            Membership("names", 5), Source, Table, Lists("names", ["épée"], [string.Empty]), batchSize, reference);

        // dec: 1.5, -2.5, 3.5, 0.0, NULL, -0.0005, 12345.6789, 0, 2.5.
        Assert.Equal([false, false, false, true, null, false, true, true, false], decimals);

        // s: alpha, beta, gamma, NULL, delta, Delta, épée, "", zzz — byte for byte, so Delta is not delta.
        Assert.Equal([false, false, false, null, false, false, true, true, false], strings);
    }

    [Fact]
    public async Task A_list_the_execution_did_not_bind_is_refused_by_name()
    {
        var error = await Assert.ThrowsAsync<ExecutionException>(
            () => Runner.ProjectAsync(Membership("orgs", 0), Source, Table, Lists("other", [1])));

        Assert.Contains("the context list 'orgs'", error.Message, StringComparison.Ordinal);
        Assert.Contains("the execution bound none", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_row_of_the_wrong_width_is_refused_by_name()
    {
        var error = await Assert.ThrowsAsync<ExecutionException>(
            () => Runner.ProjectAsync(Membership("pairs", 0, 5), Source, Table, Lists("pairs", [1])));

        Assert.Contains("row 0 of the context list 'pairs' has 1 value(s)", error.Message, StringComparison.Ordinal);
    }
}
