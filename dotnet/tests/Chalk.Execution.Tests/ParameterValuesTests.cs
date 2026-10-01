using Chalk.Catalog;

namespace Chalk.Execution.Tests;

/// <summary>
/// F155, D317 — a bound value binds only when the parameter's type holds it exactly. Each row of the
/// matrix: what binds, what it binds as, and what is refused — with a message that names the
/// parameter and the CLR type and never quotes the value.
/// </summary>
public sealed class ParameterValuesTests
{
    private enum Colour : byte
    {
        Red = 1,
        Blue = 200,
    }

    public static TheoryData<object, ChalkType, object> Accepted() => new()
    {
        // Integers: any integer type, and an enum as its number, when the value is in range.
        { 42, ChalkType.Int64(), 42L },
        { 42L, ChalkType.Int32(), 42L },
        { (byte)7, ChalkType.Int16(), 7L },
        { Colour.Blue, ChalkType.Int32(), 200L },
        { ulong.MaxValue / 4, ChalkType.Int64(), (long)(ulong.MaxValue / 4) },

        // DECIMAL: integers, decimals, and float/double by their shortest round-trip decimal, when they fit.
        { 25.5m, ChalkType.Decimal(10, 2), 25.5m },
        { 25.500m, ChalkType.Decimal(10, 2), 25.5m },
        { 42, ChalkType.Decimal(10, 2), 42m },
        { 0.1, ChalkType.Decimal(10, 2), 0.1m },
        { 25.25f, ChalkType.Decimal(10, 2), 25.25m },
        { 1e20, ChalkType.Decimal(38, 0), 100_000_000_000_000_000_000m },

        // DOUBLE: float, double, and integers or decimals at their nearest double.
        { 1.5f, ChalkType.Float64(), 1.5d },
        { 0.1, ChalkType.Float64(), 0.1d },
        { 3, ChalkType.Float64(), 3d },
        { 0.1m, ChalkType.Float64(), 0.1d },

        // REAL: float, and anything else only when it holds it exactly.
        { 0.5f, ChalkType.Float32(), 0.5f },
        { 0.5, ChalkType.Float32(), 0.5f },
        { 16_777_216, ChalkType.Float32(), 16_777_216f },

        // Text, bytes, identifiers, and the kinds of time.
        { "BTC", ChalkType.String(), "BTC" },
        { 'x', ChalkType.String(), "x" },
        { true, ChalkType.Bool(), true },
        { new DateOnly(2026, 1, 3), ChalkType.Date(), new DateOnly(2026, 1, 3) },
        { new DateTime(2026, 1, 3), ChalkType.Date(), new DateOnly(2026, 1, 3) },
        { new TimeOnly(9, 30), ChalkType.Time(), new TimeOnly(9, 30) },
        { TimeSpan.FromHours(9.5), ChalkType.Time(), new TimeOnly(9, 30) },
        { new DateTime(2026, 1, 3, 9, 30, 0, DateTimeKind.Unspecified), ChalkType.Timestamp(9), new DateTime(2026, 1, 3, 9, 30, 0) },
        { new DateTime(2026, 1, 3, 9, 30, 0, DateTimeKind.Local), ChalkType.Timestamp(9), new DateTime(2026, 1, 3, 9, 30, 0) },
        { new DateOnly(2026, 1, 3), ChalkType.Timestamp(6), new DateTime(2026, 1, 3) },
        { new DateTime(2026, 1, 3, 9, 30, 0, DateTimeKind.Utc), ChalkType.TimestampTz(6), new DateTimeOffset(2026, 1, 3, 9, 30, 0, TimeSpan.Zero) },
        { new DateTimeOffset(2026, 1, 3, 17, 30, 0, TimeSpan.FromHours(8)), ChalkType.TimestampTz(6), new DateTimeOffset(2026, 1, 3, 9, 30, 0, TimeSpan.Zero) },
        { TimeSpan.FromDays(3), ChalkType.IntervalDay(), TimeSpan.FromDays(3) },
        { 14, ChalkType.IntervalYear(), 14 },
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public void A_value_the_type_holds_exactly_binds(object value, ChalkType type, object expected) =>
        Assert.Equal(expected, ParameterValues.Exact(value, type, "@p"));

    [Fact]
    public void A_utf8_string_binds_as_itself_and_a_guid_as_a_guid()
    {
        var text = Utf8String.FromString("BTC");
        Assert.Equal(text, ParameterValues.Exact(text, ChalkType.String(), "@p"));
        var uuid = Guid.NewGuid();
        Assert.Equal(uuid, ParameterValues.Exact(uuid, ChalkType.Uuid(), "@p"));
    }

    public static TheoryData<object, ChalkType, string> Refused() => new()
    {
        // The F155 cases: a fraction, text, a bool and a char never become an integer.
        { 3.5, ChalkType.Int32(), "holds no fraction" },
        { 3.0, ChalkType.Int32(), "not bound to one even when its value is whole" },
        { 3m, ChalkType.Int64(), "holds no fraction" },
        { "3", ChalkType.Int32(), "takes an integer" },
        { true, ChalkType.Int32(), "takes an integer" },
        { 'A', ChalkType.Int32(), "takes an integer" },
        { 3_000_000_000L, ChalkType.Int32(), "outside" },
        { Colour.Blue, ChalkType.Int8(), "outside" },

        // DECIMAL: nothing is rounded to the scale, and nothing overflows the precision.
        { 25.555m, ChalkType.Decimal(10, 2), "would be rounded" },
        { 1.0 / 3, ChalkType.Decimal(10, 2), "would be rounded" },
        { 123_456_789m, ChalkType.Decimal(10, 2), "integer digits" },
        { double.NaN, ChalkType.Decimal(10, 2), "infinity, NaN" },
        { "30", ChalkType.Decimal(10, 2), "takes a number" },

        // REAL holds 0.1 only approximately, as a different value from the double.
        { 0.1, ChalkType.Float32(), "does not hold this value exactly" },
        { 16_777_217, ChalkType.Float32(), "does not hold this value exactly" },

        // Nothing is converted to text, and nothing is parsed from it.
        { 42, ChalkType.String(), "nothing is converted to text" },
        { new DateTime(2026, 1, 3), ChalkType.String(), "nothing is converted to text" },
        { "2026-01-03", ChalkType.Date(), "takes a DateOnly" },
        { "0e0e0e0e-0000-0000-0000-000000000000", ChalkType.Uuid(), "nothing is parsed" },
        { 1, ChalkType.Bool(), "takes a bool" },

        // Time: a DATE has no time of day; the kinds of timestamp follow Npgsql 6.
        { new DateTime(2026, 1, 3, 9, 30, 0), ChalkType.Date(), "no time of day" },
        { TimeSpan.FromHours(25), ChalkType.Time(), "not within one" },
        { new DateTime(2026, 1, 3, 9, 30, 0, DateTimeKind.Utc), ChalkType.Timestamp(9), "kind Utc is an instant" },
        { new DateTimeOffset(2026, 1, 3, 9, 30, 0, TimeSpan.Zero), ChalkType.Timestamp(9), "DateTimeOffset is an instant" },
        { new DateTime(2026, 1, 3, 9, 30, 0, DateTimeKind.Unspecified), ChalkType.TimestampTz(6), "is a wall clock" },
        { new DateTime(2026, 1, 3, 9, 30, 0, DateTimeKind.Local), ChalkType.TimestampTz(6), "is a wall clock" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void A_value_the_type_does_not_hold_exactly_is_refused_by_name(object value, ChalkType type, string reason)
    {
        var error = Assert.Throws<ParameterBindingException>(
            () => ParameterValues.Exact(value, type, "Parameter @amount"));

        Assert.StartsWith("Parameter @amount is ", error.Message, StringComparison.Ordinal);
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
        Assert.Equal(ChalkErrorCodes.ParameterBinding, error.Code);
        Assert.EndsWith(" [ParameterBinding]", error.Message, StringComparison.Ordinal);
        Assert.Equal("Parameter @amount", error.Subject);
    }

    [Fact]
    public void A_refusal_names_the_clr_type_and_never_the_value()
    {
        var error = Assert.Throws<ParameterBindingException>(
            () => ParameterValues.Exact("secret-42", ChalkType.Int32(), "Parameter @amount"));

        Assert.Contains("String", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refusal_of_a_converted_value_names_the_converter()
    {
        var error = Assert.Throws<ParameterBindingException>(
            () => ParameterValues.Exact(3.5, ChalkType.Int32(), "Context scalar 'tenant'", "TenantIdConverter"));

        Assert.StartsWith(
            "Context scalar 'tenant' is I32, and a Double returned by TenantIdConverter was bound to it:",
            error.Message,
            StringComparison.Ordinal);
    }

    /// <summary>Time finer than the parameter's precision is truncated: the one loss D317 tolerates.</summary>
    [Fact]
    public void A_timestamp_finer_than_its_parameter_binds_and_is_truncated()
    {
        var second = new DateTime(2026, 1, 3, 9, 30, 0);

        Assert.Equal(
            Micros(second.AddTicks(1_234_560)),
            Micros(second.AddTicks(1_234_567)));
        Assert.NotEqual(
            Micros(second.AddTicks(1_234_560)),
            Micros(second.AddTicks(1_234_570)));

        static long Micros(DateTime value) => ParameterBinder.Bind([value], [ChalkType.Timestamp(6)])[0].Integer;
    }

    /// <summary>The engine's binder holds a value to the same rule, for anything that reaches a plan without the client.</summary>
    [Fact]
    public void The_engine_binder_refuses_what_the_client_refuses()
    {
        var error = Assert.Throws<ParameterBindingException>(() => ParameterBinder.Bind([3.5], [ChalkType.Int32()]));

        Assert.Equal(ChalkErrorCodes.ParameterBinding, error.Code);
        Assert.StartsWith("Parameter 0 is I32", error.Message, StringComparison.Ordinal);
    }
}
