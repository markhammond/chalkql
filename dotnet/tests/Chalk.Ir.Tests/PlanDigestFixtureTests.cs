using Chalk.TestKit;

namespace Chalk.Ir.Tests;

/// <summary>
/// The cross-language contract check (<c>docs/design/05-testing.md</c> §4, work-plan §2 "Contract
/// cross-check"). The plans and digests in <c>planner/src/test/resources/digest-fixtures/</c> are
/// written by the planner's <c>DigestFixturesTest</c>; the C# implementation must reproduce every
/// one. If the two ever disagree the build fails, which is the point.
/// </summary>
public sealed class PlanDigestFixtureTests
{
    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (var file in RepoLayout.DigestFixtures.GetFiles("*.binpb").OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            data.Add(Path.GetFileNameWithoutExtension(file.Name));
        }

        return data;
    }

    [Fact]
    public void The_fixture_set_is_present()
    {
        Assert.True(
            RepoLayout.DigestFixtures.Exists,
            $"{RepoLayout.DigestFixtures.FullName} is missing; run "
            + "'CHALK_WRITE_FIXTURES=1 planner/gradlew -p planner test --tests chalk.ir.DigestFixturesTest'");

        Assert.True(
            RepoLayout.DigestFixtures.GetFiles("*.binpb").Length >= 5,
            "the design calls for at least five cross-language digest fixtures");
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Csharp_reproduces_the_java_digest(string name)
    {
        var dir = RepoLayout.DigestFixtures.FullName;
        var plan = Plan.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(dir, name + ".binpb")));
        var expected = PlanDigest.Parse(File.ReadAllText(Path.Combine(dir, name + ".digest")));

        Assert.Equal(expected, plan.PlanDigest);
        Assert.Equal(PlanDigest.Format(expected), PlanDigest.Format(PlanDigest.Compute(plan)));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Every_fixture_passes_the_validator(string name)
    {
        var path = Path.Combine(RepoLayout.DigestFixtures.FullName, name + ".binpb");
        var plan = Plan.Parser.ParseFrom(File.ReadAllBytes(path));

        PlanValidator.Validate(plan);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Every_fixture_prints_without_throwing(string name)
    {
        var path = Path.Combine(RepoLayout.DigestFixtures.FullName, name + ".binpb");
        var plan = Plan.Parser.ParseFrom(File.ReadAllBytes(path));

        var text = PlanPrinter.Print(plan);

        Assert.Contains("Plan ir_version=1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<no kind set>", text, StringComparison.Ordinal);
    }
}
