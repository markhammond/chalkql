using Chalk.Client;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// D248, D250: <c>GetInfo</c> over a real sidecar reports the dialects, conformance levels and
/// libraries it accepts, and <see cref="GrpcQueryPlanner"/> carries them into
/// <see cref="PlannerInfo"/>.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class DialectDiscoveryTests(SharedSidecar sidecar)
{
    [Fact]
    public async Task GetInfo_lists_the_tuned_presets_with_postgres_as_an_alias_and_every_conformance_and_library()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var planner = sidecar.CreatePlanner();
        var info = await planner.GetInfoAsync(TestContext.Current.CancellationToken);

        var byName = info.Dialects.ToDictionary(d => d.Name);
        foreach (var tuned in new[] { "sqlite", "duckdb", "postgresql", "ansi" })
        {
            Assert.True(byName.TryGetValue(tuned, out var preset), $"expected a '{tuned}' preset");
            Assert.True(preset!.Tuned, $"'{tuned}' should be tuned");
        }

        Assert.Equal(["postgres"], byName["postgresql"].Aliases);
        Assert.Equal(string.Empty, byName["ansi"].DatabaseProduct);
        // postgres is an alias, not its own entry.
        Assert.False(byName.ContainsKey("postgres"));

        // Every other Calcite product is untuned: the conformance kit, run by the host, is how it
        // learns what holds for one of these (design 31 §4).
        foreach (var untuned in new[] { "oracle", "mssql", "big_query" })
        {
            Assert.True(byName.TryGetValue(untuned, out var preset), $"expected a '{untuned}' preset");
            Assert.False(preset!.Tuned, $"'{untuned}' should not be tuned");
            Assert.Equal(untuned.ToUpperInvariant(), preset.DatabaseProduct);
        }

        // jethro's own default dialect factory refuses a context-free instance (V196, ADR 0032); it
        // is not offered as a name this sidecar accepts.
        Assert.False(byName.ContainsKey("jethro"));

        Assert.Equal(
            Enum.GetValues<SqlConformance>().OrderBy(v => v),
            info.Conformances.OrderBy(v => v));
        Assert.Equal(Enum.GetValues<SqlLibrary>().OrderBy(v => v), info.Libraries.OrderBy(v => v));
    }
}
