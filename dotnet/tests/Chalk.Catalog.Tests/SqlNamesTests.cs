using Chalk.Ir;

namespace Chalk.Catalog.Tests;

/// <summary>
/// D318 — a conformance level and a library are Calcite's constant names, carried as strings: a
/// member for each one the sidecar's Calcite has today, <c>Named</c> for any other, and the wire
/// field empty for a profile that keeps its preset's own level.
/// </summary>
public sealed class SqlNamesTests
{
    [Fact]
    public void A_member_is_calcites_constant_name()
    {
        Assert.Equal("STRICT_2003", SqlConformance.Strict2003.Name);
        Assert.Equal("SQL_SERVER_2008", SqlConformance.SqlServer2008.ToString());
        Assert.Equal("BIG_QUERY", SqlLibrary.BigQuery.Name);
        Assert.Equal("POSTGRESQL", SqlLibrary.Postgresql.ToString());
    }

    [Fact]
    public void The_default_values_are_default_and_standard()
    {
        Assert.Equal(SqlConformance.Default, default);
        Assert.Equal("DEFAULT", default(SqlConformance).Name);
        Assert.Equal(SqlLibrary.Standard, default);
    }

    [Fact]
    public void A_name_equals_the_member_of_the_same_name()
    {
        Assert.True(SqlConformance.Named("LENIENT") == SqlConformance.Lenient);
        Assert.True(SqlLibrary.Named("POSTGRESQL") == SqlLibrary.Postgresql);
        Assert.NotEqual(SqlConformance.Lenient, SqlConformance.Babel);
        Assert.Equal(SqlLibrary.Named("SPATIAL").GetHashCode(), SqlLibrary.Spatial.GetHashCode());
    }

    /// <summary>A name the members do not have is still a name: the sidecar is what knows it or not.</summary>
    [Fact]
    public void A_name_no_member_has_is_carried_as_written()
    {
        Assert.Equal("POSTGRES_16", SqlConformance.Named("POSTGRES_16").Name);
        Assert.Equal("DB2", SqlLibrary.Named("DB2").Name);
    }

    [Theory]
    [InlineData("lenient")]
    [InlineData("BigQuery")]
    [InlineData("STRICT-2003")]
    [InlineData("_DEFAULT")]
    [InlineData("2003")]
    public void A_spelling_no_calcite_constant_has_is_refused_where_it_is_named(string name)
    {
        var conformance = Assert.Throws<ArgumentException>(() => SqlConformance.Named(name));
        var library = Assert.Throws<ArgumentException>(() => SqlLibrary.Named(name));

        Assert.Contains($"'{name}' is not a Calcite conformance level name", conformance.Message, StringComparison.Ordinal);
        Assert.Contains("STRICT_2003", conformance.Message, StringComparison.Ordinal);
        Assert.Contains($"'{name}' is not a Calcite library name", library.Message, StringComparison.Ordinal);
        Assert.Contains("BIG_QUERY", library.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_name_is_refused()
    {
        Assert.ThrowsAny<ArgumentException>(() => SqlConformance.Named(string.Empty));
        Assert.ThrowsAny<ArgumentException>(() => SqlLibrary.Named(null!));
    }

    /// <summary>Names on the wire, and an empty conformance for a profile that keeps its preset's own.</summary>
    [Fact]
    public void A_profile_carries_its_names_on_the_wire_and_back()
    {
        var profile = DialectProfiles.PostgreSql.With(
            conformance: SqlConformance.Named("POSTGRES_16"),
            libraries: [SqlLibrary.Postgresql, SqlLibrary.Named("DB2")]);

        var message = Round(profile, out var back);

        Assert.Equal("POSTGRES_16", message.Conformance);
        Assert.Equal(["POSTGRESQL", "DB2"], message.Libraries);
        Assert.Equal(SqlConformance.Named("POSTGRES_16"), back.Conformance);
        Assert.Equal([SqlLibrary.Postgresql, SqlLibrary.Named("DB2")], back.Libraries);
    }

    [Fact]
    public void A_profile_without_a_level_keeps_its_presets_own()
    {
        var message = Round(new DialectProfileDescriptor { Dialect = "duckdb" }, out var back);

        Assert.Equal(string.Empty, message.Conformance);
        Assert.Null(back.Conformance);
    }

    private static DialectProfile Round(DialectProfileDescriptor profile, out DialectProfileDescriptor back)
    {
        var catalog = new CatalogContext
        {
            ContextId = "d318",
            Epoch = 1,
            Schemas =
            [
                new SchemaDescriptor
                {
                    SourceId = "s",
                    Name = "s",
                    Kind = SourceKind.Remote,
                    Dialect = profile.Dialect,
                    DialectProfile = profile,
                    Tables = [],
                },
            ],
        };

        var message = catalog.ToProto();
        back = CatalogProtoMapping.FromProto(message).Schemas[0].DialectProfile;
        return message.Schemas[0].DialectProfile;
    }
}
