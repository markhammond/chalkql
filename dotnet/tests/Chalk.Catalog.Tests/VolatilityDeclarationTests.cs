using Chalk.Ir;

namespace Chalk.Catalog.Tests;

/// <summary>
/// A function's volatility as the host said it (D339, <c>docs/design/64-incremental-views.md</c>
/// §8 g): unspecified until the host says, which reads as immutable everywhere, so that a standing
/// view asking for an explicit <c>Immutable()</c> can tell the default from the declaration.
/// </summary>
public sealed class VolatilityDeclarationTests
{
    [Fact]
    public void A_function_that_says_nothing_is_unspecified()
    {
        var function = new FunctionBuilder("f").Returns(ChalkType.Int64()).Client().Build();

        Assert.Equal(Volatility.Unspecified, function.Volatility);
    }

    [Theory]
    [InlineData(Volatility.Immutable)]
    [InlineData(Volatility.Stable)]
    [InlineData(Volatility.Volatile)]
    public void A_function_that_says_is_what_it_says(Volatility said)
    {
        var builder = new FunctionBuilder("f").Returns(ChalkType.Int64()).Client();
        builder = said switch
        {
            Volatility.Immutable => builder.Immutable(),
            Volatility.Stable => builder.Stable(),
            _ => builder.Volatile(),
        };

        Assert.Equal(said, builder.Build().Volatility);
    }

    /// <summary>The catalog carries the difference to the planner unchanged.</summary>
    [Fact]
    public void The_difference_survives_the_wire()
    {
        var unspecified = new FunctionBuilder("f").Returns(ChalkType.Int64()).Client().Build();
        var immutable = new FunctionBuilder("g").Returns(ChalkType.Int64()).Client().Immutable().Build();
        var catalog = new CatalogContext
        {
            ContextId = "volatility",
            Epoch = 1,
            Schemas =
            [
                new SchemaDescriptor
                {
                    SourceId = "src",
                    Name = "main",
                    Kind = SourceKind.Local,
                    Tables = [],
                    Functions = [unspecified, immutable],
                },
            ],
        };

        var back = CatalogProtoMapping.FromProto(catalog.ToProto());

        Assert.Equal(
            [Volatility.Unspecified, Volatility.Immutable],
            back.Schemas[0].Functions.Select(f => f.Volatility));
    }
}
