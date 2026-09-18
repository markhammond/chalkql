namespace Chalk.Sources.Poco.Tests;

public enum Colour
{
    Red = 0,
    Green = 1,
    Blue = 2,
}

/// <summary>One member per row of the CLR type table in <c>docs/design/04-client.md</c> §5.2.</summary>
public sealed class AllTypesRow
{
    public bool Flag { get; set; }

    public sbyte Tiny { get; set; }

    public short Small { get; set; }

    public int Medium { get; set; }

    public long Big { get; set; }

    public byte UnsignedTiny { get; set; }

    public ushort UnsignedSmall { get; set; }

    public uint UnsignedMedium { get; set; }

    public ulong UnsignedBig { get; set; }

    public float Single { get; set; }

    public double Real { get; set; }

    public decimal Money { get; set; }

    public string Text { get; set; } = string.Empty;

    public byte[] Blob { get; set; } = [];

    public ReadOnlyMemory<byte> Memory { get; set; }

    public DateTime Moment { get; set; }

    public DateTimeOffset Instant { get; set; }

    public DateOnly Day { get; set; }

    public TimeOnly Clock { get; set; }

    public TimeSpan Span { get; set; }

    public Guid Key { get; set; }

    public Colour Shade { get; set; }

    public char Letter { get; set; }
}

/// <summary>The same table, every member nullable, so the <c>Nullable&lt;T&gt;</c> rows are covered too.</summary>
public sealed class NullableTypesRow
{
    public bool? Flag { get; set; }

    public sbyte? Tiny { get; set; }

    public short? Small { get; set; }

    public int? Medium { get; set; }

    public long? Big { get; set; }

    public byte? UnsignedTiny { get; set; }

    public ushort? UnsignedSmall { get; set; }

    public uint? UnsignedMedium { get; set; }

    public ulong? UnsignedBig { get; set; }

    public float? Single { get; set; }

    public double? Real { get; set; }

    public decimal? Money { get; set; }

    public string? Text { get; set; }

    public byte[]? Blob { get; set; }

    public ReadOnlyMemory<byte>? Memory { get; set; }

    public DateTime? Moment { get; set; }

    public DateTimeOffset? Instant { get; set; }

    public DateOnly? Day { get; set; }

    public TimeOnly? Clock { get; set; }

    public TimeSpan? Span { get; set; }

    public Guid? Key { get; set; }

    public Colour? Shade { get; set; }

    public char? Letter { get; set; }
}

/// <summary>A struct row, the shape the <c>lineitem</c> fixture uses.</summary>
public readonly record struct BarRow(
    DateTime Ts,
    string Symbol,
    decimal Close,
    double? Volume,
    Colour Shade);

/// <summary>Public instance fields are columns too (§5.2), and they keep declaration order with properties.</summary>
public sealed class MixedMemberRow
{
    public int First { get; set; }

    public string Second = string.Empty;

    public long Third { get; set; }

    public static int Ignored { get; set; }

    public int WriteOnly
    {
        set => First = value;
    }

    public int this[int i] => i;

    private int Hidden { get; set; }
}

public sealed class AttributedRow
{
    [ChalkColumn(Name = "px")]
    public decimal Price { get; set; }

    [ChalkColumn(Precision = 18, Scale = 4)]
    public decimal Fee { get; set; }

    [ChalkColumn(AsInteger = true)]
    public Colour Shade { get; set; }

    [ChalkIgnore]
    public string Skipped { get; set; } = string.Empty;

    [ChalkColumn(Ignore = true)]
    public string AlsoSkipped { get; set; } = string.Empty;

    public string SymbolId { get; set; } = string.Empty;

    public int HTTPStatus { get; set; }
}

/// <summary>A member with no mapping in §5.2; registering it must fail and say so.</summary>
public sealed class UnsupportedRow
{
    public int Id { get; set; }

    public Uri Link { get; set; } = new("https://example.invalid");
}

#nullable disable

/// <summary>
/// Compiled without nullable annotations, so <c>NullabilityInfoContext</c> reports Unknown and §5.2's
/// "without annotations → nullable" rule applies.
/// </summary>
public sealed class UnannotatedRow
{
    public string Text { get; set; }

    public int Number { get; set; }
}

#nullable restore
