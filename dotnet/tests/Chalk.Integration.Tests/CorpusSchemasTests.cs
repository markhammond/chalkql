using Chalk.TestKit;
using Google.Protobuf;

namespace Chalk.Integration.Tests;

/// <summary>
/// The checked-in <c>corpus/schemas/</c> is what the fixture generator produces today. The planner's
/// own tests plan against <c>corpus.binpb</c> (<c>TestCatalogs.corpus()</c>), so a fixture change
/// that is not re-recorded leaves them planning against statistics nobody's data has any more — which
/// is how the file drifted silently for a week (the corpus records moved to <c>Utf8String</c> and six
/// statistics moved with them). Regenerate with <c>scripts/record-plans.sh</c>; nothing here needs a
/// sidecar.
/// </summary>
public sealed class CorpusSchemasTests
{
    private static string SchemasDir => Path.Combine(RepoLayout.Corpus.FullName, "schemas");

    [Fact]
    public void The_recorded_catalog_is_what_the_fixture_produces()
    {
        var recorded = Ir.CatalogContext.Parser.ParseFrom(
            File.ReadAllBytes(Path.Combine(SchemasDir, "corpus.binpb")));
        var produced = CorpusFixture.Shared.CatalogMessage();

        Assert.True(
            recorded.Equals(produced),
            "corpus/schemas/corpus.binpb is not what CorpusFixture produces; the fixture generator "
            + "changed without a re-record. Run scripts/record-plans.sh and commit corpus/schemas/.");
    }

    [Fact]
    public void The_json_twin_is_the_recorded_catalog()
    {
        var recorded = Ir.CatalogContext.Parser.ParseFrom(
            File.ReadAllBytes(Path.Combine(SchemasDir, "corpus.binpb")));
        var expected =
            new JsonFormatter(JsonFormatter.Settings.Default.WithIndentation("  ")).Format(recorded) + "\n";
        var actual = File.ReadAllText(Path.Combine(SchemasDir, "corpus.json"));

        Assert.True(
            string.Equals(expected, actual, StringComparison.Ordinal),
            "corpus/schemas/corpus.json is not the twin of corpus.binpb. Run scripts/record-plans.sh.");
    }
}
