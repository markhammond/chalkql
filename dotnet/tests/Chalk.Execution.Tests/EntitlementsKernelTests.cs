using Chalk.Execution.Tests.Harness;
using Chalk.Ir;
using Chalk.TestKit;

namespace Chalk.Execution.Tests;

/// <summary>
/// The two kernels the entitlement layer ships (<c>docs/design/16-entitlements.md</c> §4):
/// <c>FINGERPRINT(value, key)</c> and <c>PRESENT(value)</c>.
/// </summary>
/// <remarks>
/// The fingerprint vectors are <c>openssl</c>'s, not the engine's own — <c>printf 'alpha' | openssl
/// dgst -sha256 -hmac s3cret</c>, first sixteen bytes — so a test that agreed with a wrong
/// implementation would still fail. The reference executor computes them its own way and the
/// differential suite compares the two.
/// </remarks>
public sealed class EntitlementsKernelTests
{
    private static readonly TestTable Table = TestData.Strings;
    private static readonly TestSource Source =
        TestData.Source(Table, TestData.SingleRow, TestData.Empty);

    public static TheoryData<int> BatchSizes() => TestData.BatchSizeData;

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Fingerprint_is_the_keyed_hmac_truncated_to_sixteen_bytes(int batchSize)
    {
        var values = await Runner.ProjectAsync(
            Fingerprint("s3cret"), Source, Table, batchSize);

        // openssl: printf 'abcdef' | openssl dgst -sha256 -hmac s3cret
        Assert.Equal("c68e18f1a3f1055ac238dfeae92440a1", values[0]);
        // The empty string is a value, and gets a token: PRESENT is what tells the two apart.
        Assert.Equal("91dfac70c5348b04e1babb8b421ac92c", values[1]);
        Assert.Equal("92b733ee631fff6ad4d7870364615013", values[2]);   // épée, in UTF-8
        Assert.Null(values[5]);                                        // NULL in, NULL out
        Assert.All(
            values.Where(v => v is not null),
            v => Assert.Matches("^[0-9a-f]{32}$", (string)v!));
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Fingerprint_is_stable_under_one_key_and_unrelated_under_another(int batchSize)
    {
        var one = await Runner.ProjectAsync(Fingerprint("s3cret"), Source, Table, batchSize);
        var again = await Runner.ProjectAsync(Fingerprint("s3cret"), Source, Table, batchSize);
        var other = await Runner.ProjectAsync(Fingerprint("other"), Source, Table, batchSize);

        // Stability is the property the layer relies on: it is what lets an equality join on tokens
        // match masked rows across tenancies (§8's query 19).
        Assert.Equal(one, again);
        for (var i = 0; i < one.Count; i++)
        {
            if (one[i] is not null)
            {
                Assert.NotEqual(one[i], other[i]);
            }
        }
    }

    /// <summary>A NULL key gives NULL, so an unbound mask key can never leak an unkeyed hash.</summary>
    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Fingerprint_of_a_null_key_is_null(int batchSize)
    {
        var row = Table.RowType();
        var expr = IrBuilder.Call(
            FunctionId.Fingerprint,
            IrBuilder.Str(true),
            IrBuilder.Ref(row, 0),
            IrBuilder.Null(IrBuilder.Str(true)));

        var values = await Runner.ProjectAsync(expr, Source, Table, batchSize);

        Assert.All(values, Assert.Null);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Present_is_false_for_a_null_and_for_the_empty_string(int batchSize)
    {
        var row = Table.RowType();
        var expr = IrBuilder.Call(
            FunctionId.Present, IrBuilder.Bool(false), IrBuilder.Ref(row, 0));

        var values = await Runner.ProjectAsync(expr, Source, Table, batchSize);

        Assert.Equal(true, values[0]);    // "abcdef"
        Assert.Equal(false, values[1]);   // "" — a mask that replaced a value looks like this
        Assert.Equal(true, values[2]);
        Assert.Equal(false, values[5]);   // NULL
        Assert.All(values, v => Assert.NotNull(v));
    }

    private static Expr Fingerprint(string key)
    {
        var row = Table.RowType();
        return IrBuilder.Call(
            FunctionId.Fingerprint,
            IrBuilder.Str(true),
            IrBuilder.Ref(row, 0),
            IrBuilder.Lit(key));
    }
}
