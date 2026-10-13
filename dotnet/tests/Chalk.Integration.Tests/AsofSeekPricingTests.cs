using System.Text.Json;
using System.Text.Json.Serialization;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// ASOF joins priced again once the search is over (F198). The catalog is a host's, as it registered
/// it, statistics and all, with its tables renamed: a key set derived through three joins, crossed
/// with the day from a table function whose own ASOF joins read a zone table, then four ASOF joins
/// into indexed tables. While the search runs, that table function's rows are estimated in the
/// hundreds where the final plan has one, so the search priced three of the four seeks at hundreds of
/// times their final numbers and read those tables whole (F199).
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class AsofSeekPricingTests(SharedSidecar sidecar)
{
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "AsofSeeks");

    /// <summary>
    /// Every ASOF join over an indexed table seeks in the plan the planner returns: the two over
    /// positions, and the ones over prices and rates. The three over the zone table, which has no
    /// index, read it. The stage list says the pass after the search made three of them seek.
    /// </summary>
    [Fact]
    public async Task Joins_priced_on_unsettled_estimates_seek_once_they_settle()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;
        var catalog = CatalogFixture.Load(Fixtures);
        await using var planner = sidecar.Sidecar.CreatePlanner();
        await planner.RegisterCatalogAsync(catalog, ct);

        var result = await planner.PlanAsync(
            new PlanRequest
            {
                Sql = File.ReadAllText(Path.Combine(Fixtures, "statement.sql")),
                ContextId = catalog.ContextId,
                CatalogEpoch = catalog.Epoch,
                Options = new Chalk.Client.PlannerOptions { Pushdown = PushdownLevel.Full },
            },
            ct);

        var rels = Chalk.Ir.PlanWalker.Rels(result.Plan).ToList();
        Assert.Equal(4, rels.Count(r => r.KindCase == Chalk.Ir.Rel.KindOneofCase.AsOfLookupJoin));
        Assert.Equal(3, rels.Count(r => r.KindCase == Chalk.Ir.Rel.KindOneofCase.AsOfJoin));
        Assert.Contains("as-of seeks after search 3", result.Stats.Stages);
    }

    /// <summary>
    /// A catalog serialized from a host's <see cref="ChalkEngine.Catalog"/>, read back. A function's
    /// body is an abstract type the serializer writes empty: each SQL body is put back from its own
    /// file, and every other is a client function's. Statistics come back as JSON values and are given
    /// the client's own forms again, a timestamp as its units since the epoch.
    /// </summary>
    private static class CatalogFixture
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter(), new BodyConverter() },
        };

        public static CatalogContext Load(string directory)
        {
            var catalog = JsonSerializer.Deserialize<CatalogContext>(
                File.ReadAllText(Path.Combine(directory, "catalog.json")), Options)!;
            foreach (var schema in catalog.Schemas)
            {
                foreach (var column in schema.Tables.SelectMany(t => t.Columns))
                {
                    Restore(column.Statistics);
                }

                foreach (var function in schema.Functions)
                {
                    var body = Path.Combine(directory, function.Name + ".body.sql");
                    if (File.Exists(body))
                    {
                        Set(function, nameof(FunctionDescriptor.Body), new SqlFunctionBody { Text = File.ReadAllText(body) });
                    }
                }
            }

            return catalog;
        }

        private static void Restore(ColumnStatistics statistics)
        {
            Set(statistics, nameof(ColumnStatistics.Min), Value(statistics.Min));
            Set(statistics, nameof(ColumnStatistics.Max), Value(statistics.Max));
            foreach (var bucket in statistics.Histogram)
            {
                Set(bucket, nameof(HistogramBucket.Upper), Value(bucket.Upper));
            }

            foreach (var frequent in statistics.FrequentValues)
            {
                Set(frequent, nameof(FrequentValue.Value), Value(frequent.Value));
            }
        }

        // The statistics' properties are init-only: set once more, by reflection, after reading.
        private static void Set(object target, string property, object? value) =>
            target.GetType().GetProperty(property)!.SetValue(target, value);

        private static object? Value(object? value) => value is not JsonElement json
            ? value
            : json.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => json.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when json.TryGetInt64(out var whole) => whole,
                JsonValueKind.Number => json.GetDecimal(),
                _ => throw new NotSupportedException($"a statistic written as {json.GetRawText()}"),
            };

        private sealed class BodyConverter : JsonConverter<FunctionBody>
        {
            public override FunctionBody Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
            {
                reader.Skip();
                return new ClientFunctionBody();
            }

            public override void Write(Utf8JsonWriter writer, FunctionBody value, JsonSerializerOptions options) =>
                throw new NotSupportedException();
        }
    }
}
