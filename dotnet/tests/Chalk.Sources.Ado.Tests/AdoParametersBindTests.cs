using System.Data;
using System.Data.Common;
using Chalk.Catalog;
using Chalk.Ir;

namespace Chalk.Sources.Ado.Tests;

/// <summary>
/// <see cref="AdoParameters.Bind(DbCommand, RemoteFetchRequest)"/> (D264): the request overload an
/// <see cref="IRemoteFetch"/> implementation calls when it builds its own <see cref="DbCommand"/>
/// must bind exactly what the four-argument overload binds from the same three lists — same names,
/// same values, same <see cref="DbType"/>s, in the same order.
/// </summary>
public sealed class AdoParametersBindTests
{
    private static readonly ExecutionArena SharedArena = new();

    private static readonly IReadOnlyList<string> Names = ["p0", "p1", "p2"];
    private static readonly IReadOnlyList<object?> Values = ["hello", null, 42L];
    private static readonly IReadOnlyList<ChalkType> Types =
    [
        ChalkType.String(nullable: true),
        ChalkType.String(nullable: true),
        ChalkType.Int64(),
    ];

    /// <summary>
    /// The request overload reads <see cref="RemoteFetchRequest.ParameterNames"/>,
    /// <see cref="RemoteFetchRequest.Parameters"/> and <see cref="RemoteFetchRequest.ParameterTypes"/>
    /// itself and binds exactly what the four-argument overload binds from those same three lists,
    /// in the same order — the guard against an implementer mis-ordering them by hand.
    /// </summary>
    [Fact]
    public void The_request_overload_agrees_with_the_four_argument_overload()
    {
        using var viaRequest = MakeCommand();
        using var viaFourArguments = MakeCommand();

        AdoParameters.Bind(viaRequest, MakeRequest(Names, Values, Types));
        AdoParameters.Bind(viaFourArguments, Names, Values, Types);

        Assert.Equal(Bound(viaFourArguments), Bound(viaRequest));
    }

    /// <summary>A null value binds as <see cref="DBNull.Value"/>, never a CLR null.</summary>
    [Fact]
    public void A_null_value_binds_as_dbnull()
    {
        using var command = MakeCommand();

        AdoParameters.Bind(command, MakeRequest(Names, Values, Types));

        Assert.Equal(DBNull.Value, command.Parameters[1].Value);
    }

    /// <summary>
    /// The <see cref="DbType"/> each parameter gets, through either overload, is
    /// <see cref="AdoTypeMapping.ToDbType"/> of its Chalk type — a representative kind from each
    /// family the mapping names, rather than the whole table (<c>AdoTypeMatrixTests</c> already
    /// exercises that end to end, D167).
    /// </summary>
    [Theory]
    [InlineData(TypeKind.Bool)]
    [InlineData(TypeKind.I32)]
    [InlineData(TypeKind.I64)]
    [InlineData(TypeKind.Fp64)]
    [InlineData(TypeKind.Decimal)]
    [InlineData(TypeKind.String)]
    [InlineData(TypeKind.Date)]
    [InlineData(TypeKind.Uuid)]
    public void The_dbtype_is_the_one_the_four_argument_overload_sets(TypeKind kind)
    {
        var type = new ChalkType(kind, false);
        IReadOnlyList<string> names = ["p0"];
        IReadOnlyList<object?> values = ["the CLR shape of the value is not what this checks"];
        IReadOnlyList<ChalkType> types = [type];
        var expected = AdoTypeMapping.ToDbType(type);

        using var viaRequest = MakeCommand();
        using var viaFourArguments = MakeCommand();

        AdoParameters.Bind(viaRequest, MakeRequest(names, values, types));
        AdoParameters.Bind(viaFourArguments, names, values, types);

        Assert.Equal(expected, viaRequest.Parameters[0].DbType);
        Assert.Equal(expected, viaFourArguments.Parameters[0].DbType);
    }

    /// <summary>
    /// A pushed bound the engine rendered is gone from the text and gone from the values, together,
    /// so the placeholders this rewrites and the values bound onto them still line up by position.
    /// </summary>
    /// <remarks>
    /// The engine writes the number into the text before the request is made, so a source sees
    /// ordinary SQL with one placeholder fewer. Nothing in this package knows a bound from any
    /// other value — which is the point, and is what this pins: the arithmetic that has to agree is
    /// "one placeholder per value", and it does.
    /// </remarks>
    [Theory]
    [InlineData(ParameterPlaceholder.NamedAt)]
    [InlineData(ParameterPlaceholder.NamedDollar)]
    [InlineData(ParameterPlaceholder.OrdinalDollar)]
    [InlineData(ParameterPlaceholder.Question)]
    public void A_rendered_bound_leaves_the_placeholders_and_the_values_lined_up(
        ParameterPlaceholder style)
    {
        var (sql, names) = AdoParameters.Rewrite("SELECT k FROM t WHERE k >= ? LIMIT 25", style);

        Assert.Single(names);
        Assert.DoesNotContain("LIMIT ?", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 25", sql, StringComparison.Ordinal);

        using var command = MakeCommand();
        AdoParameters.Bind(command, names, ["a"], [ChalkType.String()]);

        Assert.Single(command.Parameters.Cast<DbParameter>());
        Assert.Equal("a", command.Parameters[0].Value);
    }

    private static DbCommand MakeCommand() => new FakeProvider().Connect().CreateCommand();

    private static RemoteFetchRequest MakeRequest(
        IReadOnlyList<string> names, IReadOnlyList<object?> values, IReadOnlyList<ChalkType> types) =>
        new()
        {
            SourceId = "fake",
            Subject = "t",
            Connection = new FakeProvider().Connect(),
            Sql = "SELECT 1",
            ParameterNames = names,
            Parameters = values,
            ParameterTypes = types,
            OutputSchema = ArrowTypeMapping.ToArrowSchema([]),
            ColumnTypes = [],
            Arena = SharedArena,
            Stats = new ExecutionStats(),
            BatchSize = 4096,
            Timeout = TimeSpan.FromSeconds(30),
            Profile = DialectProfiles.Ansi,
        };

    private static (string Name, object? Value, DbType DbType)[] Bound(DbCommand command) =>
        [.. command.Parameters.Cast<DbParameter>().Select(p => (p.ParameterName, p.Value, p.DbType))];
}
