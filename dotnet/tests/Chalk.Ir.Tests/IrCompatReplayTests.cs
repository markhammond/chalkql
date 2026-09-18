using Chalk.TestKit;

namespace Chalk.Ir.Tests;

/// <summary>
/// Compatibility is tested, not asserted (<c>docs/design/02-ir.md</c> §2 rule 4): every released IR
/// version keeps a frozen copy of its recorded plans under <c>corpus/ir-compat/v&lt;N&gt;/</c>, and
/// the current client must still parse, validate, print and digest each one.
/// <para>
/// The directory is empty until the first tagged release, so today this proves the harness works and
/// that nothing has been dropped in without being replayed.
/// </para>
/// </summary>
public sealed class IrCompatReplayTests
{
    public static TheoryData<string, string> RecordedPlans()
    {
        var data = new TheoryData<string, string>();
        if (RepoLayout.IrCompat.Exists)
        {
            foreach (var versionDir in RepoLayout.IrCompat.GetDirectories("v*")
                         .OrderBy(d => d.Name, StringComparer.Ordinal))
            {
                foreach (var file in versionDir.GetFiles("*.binpb", SearchOption.AllDirectories)
                             .OrderBy(f => f.FullName, StringComparer.Ordinal))
                {
                    data.Add(versionDir.Name, file.FullName);
                }
            }
        }

        if (data.Count == 0)
        {
            // xunit needs at least one row. Until the first tagged release there is nothing frozen to
            // replay; NoneYet keeps the harness wired up and visible in the test list.
            data.Add(NoneYet, NoneYet);
        }

        return data;
    }

    private const string NoneYet = "(no released IR version yet)";

    [Fact]
    public void Every_ir_compat_directory_is_a_version_this_client_can_still_read()
    {
        if (!RepoLayout.IrCompat.Exists)
        {
            return;   // no tagged release yet; 02-ir.md §2 "pre-release freedom"
        }

        foreach (var dir in RepoLayout.IrCompat.GetDirectories())
        {
            Assert.StartsWith("v", dir.Name, StringComparison.Ordinal);
            var version = uint.Parse(dir.Name[1..], System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(version, IrVersion.Minimum, IrVersion.Current);
        }
    }

    [Theory]
    [MemberData(nameof(RecordedPlans))]
    public void A_recorded_plan_still_parses_validates_prints_and_digests(string version, string path)
    {
        if (version == NoneYet)
        {
            Assert.False(
                RepoLayout.IrCompat.Exists && RepoLayout.IrCompat.GetDirectories("v*").Length > 0,
                "corpus/ir-compat has version directories but no recorded plans were found in them");
            return;
        }

        var plan = Plan.Parser.ParseFrom(File.ReadAllBytes(path));

        Assert.Equal(uint.Parse(version[1..], System.Globalization.CultureInfo.InvariantCulture), plan.IrVersion);
        PlanValidator.Validate(plan);
        Assert.NotEmpty(PlanPrinter.Print(plan));
        Assert.Equal(plan.PlanDigest, PlanDigest.Compute(plan));
    }
}
