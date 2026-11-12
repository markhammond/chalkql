using Chalk.Catalog;
using Chalk.Execution.Tests.Harness;
using Chalk.Ir;
using Chalk.TestKit;

namespace Chalk.Execution.Tests;

/// <summary>
/// F300 — the planner wraps a mask that reads the row in <c>MASKED(mask, label)</c>: the mask's own
/// value, and an error raised computing it reported as that mask having failed, without the error's
/// text, which could quote the value the mask hides. Registration refuses a mask that can raise, so
/// this is what holds should that judgement miss. Both engines.
/// </summary>
public sealed class MaskedKernelTests
{
    private const string Label = "s.reports.SourceIdentity";

    private static readonly TestTable Table = new()
    {
        Name = "reports",
        Columns = [("s", ChalkType.String(nullable: true))],
        Rows = [["12"], ["SOURCE-01"], [null]],
    };

    private static readonly TestSource Source = TestData.Source(Table);

    public static TheoryData<bool> Engines => new() { false, true };

    private static Expr Masked(Expr mask, Chalk.Ir.Type type) =>
        IrBuilder.Call(FunctionId.Masked, type, mask, IrBuilder.Lit(Label));

    private static Expr FailingCast() =>
        IrBuilder.Cast(IrBuilder.Ref(Table.RowType(), 0), IrBuilder.I32(true));

    /// <summary>A mask that fails names the column it masks, and nothing of the row.</summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_mask_that_fails_is_reported_without_the_value(bool reference)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => Runner.ProjectAsync(Masked(FailingCast(), IrBuilder.I32(true)), Source, Table, reference: reference));

        Assert.Contains($"The mask on {Label} could not be computed for a row", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("SOURCE-01", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The same cast unwrapped quotes the row, which is what the wrapper is for.</summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public async Task The_same_cast_unwrapped_quotes_the_row(bool reference)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => Runner.ProjectAsync(FailingCast(), Source, Table, reference: reference));

        Assert.Contains("SOURCE-01", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A mask that does not fail answers its own value, NULL for NULL.</summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_mask_that_does_not_fail_answers_its_value(bool reference)
    {
        var upper = IrBuilder.Call(FunctionId.Lower, IrBuilder.Str(true), IrBuilder.Ref(Table.RowType(), 0));

        Assert.Equal(
            ["12", "source-01", null],
            await Runner.ProjectAsync(Masked(upper, IrBuilder.Str(true)), Source, Table, reference: reference));
    }
}
