namespace Chalk.Sources.Poco;

/// <summary>
/// Overrides what inference would decide for one member (<c>docs/design/04-client.md</c> §5.1).
/// </summary>
/// <remarks>
/// D26 asks for <c>init</c> properties on public types, but a named attribute argument must bind to a
/// settable property (CS0617), so these are <c>get; set;</c>. The unset sentinel for
/// <see cref="Precision"/> and <see cref="Scale"/> is -1 rather than 0 because 0 is a legal scale.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class ChalkColumnAttribute : Attribute
{
    /// <summary>The SQL column name, used verbatim — the naming policy does not touch it.</summary>
    public string? Name { get; set; }

    /// <summary>DECIMAL total digits, or TIMESTAMP / TIMESTAMP_TZ / TIME fractional-second digits. -1 leaves the default.</summary>
    public int Precision { get; set; } = -1;

    /// <summary>DECIMAL fractional digits. -1 uses the builder's <c>DefaultDecimalScale</c>.</summary>
    public int Scale { get; set; } = -1;

    /// <summary>Leaves the member out of the table, exactly like <see cref="ChalkIgnoreAttribute"/>.</summary>
    public bool Ignore { get; set; }

    /// <summary>Maps an enum to its underlying integer kind instead of its name (D16).</summary>
    public bool AsInteger { get; set; }
}

/// <summary>Sugar for <c>[ChalkColumn(Ignore = true)]</c>.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class ChalkIgnoreAttribute : Attribute;
