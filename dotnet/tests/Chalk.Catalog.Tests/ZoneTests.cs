using Chalk.Ir;

namespace Chalk.Catalog.Tests;

/// <summary>
/// One catalog is one zone (D311): a source's zone is a declaration the validator checks and nothing
/// else reads, so a catalog whose sources disagree is refused where it is registered, by name.
/// </summary>
public sealed class ZoneTests
{
    [Fact]
    public void A_catalog_declaring_no_zone_validates_and_carries_no_zone()
    {
        var catalog = Catalog(("mem", "main", ""), ("pg", "warehouse", ""));

        CatalogValidator.Validate(catalog);

        Assert.All(CatalogSerialization.ToProto(catalog).Schemas, s => Assert.Equal(string.Empty, s.Zone));
    }

    [Fact]
    public void Sources_of_one_zone_validate_and_the_zone_survives_the_wire()
    {
        var catalog = Catalog(("mem", "main", "eu"), ("pg", "warehouse", "eu"));

        CatalogValidator.Validate(catalog);

        var wire = CatalogSerialization.ToProto(catalog);
        Assert.All(wire.Schemas, s => Assert.Equal("eu", s.Zone));
        Assert.All(CatalogSerialization.FromProto(wire).Schemas, s => Assert.Equal("eu", s.Zone));
    }

    [Fact]
    public void Two_zones_in_one_catalog_are_refused_naming_the_zones_and_the_sources()
    {
        var error = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Catalog(("mem", "main", "eu"), ("pg", "warehouse", "us"))));

        Assert.Contains("one catalog is one zone", error.Message, StringComparison.Ordinal);
        Assert.Contains("'eu' (main)", error.Message, StringComparison.Ordinal);
        Assert.Contains("'us' (warehouse)", error.Message, StringComparison.Ordinal);
        Assert.Contains("one engine per zone", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zone_declared_on_some_sources_and_not_others_is_refused_naming_both_sides()
    {
        var error = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Catalog(("mem", "main", "eu"), ("pg", "warehouse", ""))));

        Assert.Contains("main declare the zone 'eu'", error.Message, StringComparison.Ordinal);
        Assert.Contains("warehouse declare none", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(" eu")]
    [InlineData("eu ")]
    public void A_zone_with_white_space_around_it_is_refused_where_it_is_declared(string zone)
    {
        var error = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Catalog(("mem", "main", zone))));

        Assert.Equal("schemas[0] (main).zone", error.Path);
        Assert.Contains("white space", error.Message, StringComparison.Ordinal);
    }

    private static CatalogContext Catalog(params (string SourceId, string Name, string Zone)[] schemas) => new()
    {
        ContextId = "demo",
        Epoch = 1,
        Schemas =
        [
            .. schemas.Select(s => new SchemaDescriptor
            {
                SourceId = s.SourceId,
                Name = s.Name,
                Kind = SourceKind.Local,
                Zone = s.Zone,
                Tables =
                [
                    new TableDescriptor
                    {
                        Name = "bars",
                        RowCount = 3,
                        Columns = [new ColumnDescriptor { Name = "symbol", Type = ChalkType.String() }],
                    },
                ],
            }),
        ],
    };
}
