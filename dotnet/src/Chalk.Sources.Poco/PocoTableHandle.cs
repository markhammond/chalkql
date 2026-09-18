namespace Chalk.Sources.Poco;

/// <summary>
/// One registered POCO table, as a handle (D271 (h),
/// <c>docs/design/44-catalog-registration.md</c> §6 (h)): the source it belongs to, the schema, the
/// name, and the row type it was built over — obtained once, at the registration that declared it or
/// by <see cref="PocoSource.Table{T}(string)"/>, and passed everywhere the table is meant afterwards.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes <c>refresh.Replace(orders, rows)</c> carry the source with the name, so two
/// sources holding <c>orders</c> over one row type cannot be confused: the only check the string
/// forms can make compares row types, and there they agree. The row type is checked at compile time
/// here, where a string form checks it at the refresh's validation step.
/// </para>
/// <para>
/// This is not the generic surface design 45 refuses for the policy vocabulary (§1, §6 (a)): a POCO
/// table is generic over its row type already — <c>AddTable&lt;T&gt;</c> declared it — and design 45
/// §1 anticipated the handle as the POCO package's own convenience. A host that builds its catalog
/// from configuration at run time keeps the <c>(source, name, rows)</c> forms, which stay.
/// </para>
/// </remarks>
/// <typeparam name="T">The row type the table was registered over.</typeparam>
public readonly record struct PocoTable<T> : ITableTarget<T>
{
    internal PocoTable(PocoTableBinding binding) => Binding = binding;

    internal PocoTableBinding? Binding { get; }

    /// <summary>The table's name, as the catalog declares it.</summary>
    public string Name => Binding?.Table ?? "";

    /// <summary>The identifier of the runtime that serves it.</summary>
    public string SourceId => Binding?.SourceId ?? "";

    /// <summary>The schema's federation-level name, which is how SQL addresses this table.</summary>
    public string Schema => Binding?.Schema ?? "";

    string IRefreshTarget.Table => Name;

    /// <summary>
    /// The source this table belongs to. Available once the builder's <c>Build()</c> has run — before
    /// that there is no runtime to name, and asking says so.
    /// </summary>
    public ISourceRuntime Runtime => Required().Runtime;

    internal PocoTableBinding Required() =>
        Binding ?? throw new InvalidOperationException(
            "a default PocoTable<T> handle names no table. Obtain one from PocoSourceBuilder's "
            + "AddTable(…, out var table, …) or from PocoSource.Table<T>(name) "
            + "(docs/design/44-catalog-registration.md §6 (h), D271).");

    public override string ToString() => $"{Schema}.{Name}";
}

/// <summary>
/// What a <see cref="PocoTable{T}"/> is a handle <em>to</em>: the names a registration fixed, and the
/// source that serves them once <c>Build()</c> has made one.
/// </summary>
/// <remarks>
/// A handle is obtained at <c>AddTable</c>, which is before the source exists; the binding is what
/// lets the same handle resolve afterwards without the host having to fetch it again.
/// </remarks>
internal sealed class PocoTableBinding
{
    internal required string SourceId { get; init; }

    internal required string Schema { get; init; }

    internal required string Table { get; init; }

    internal required Type RowType { get; init; }

    private PocoSource? _source;

    internal ISourceRuntime Runtime =>
        _source ?? throw new InvalidOperationException(
            $"the handle for table '{Table}' has no source yet: PocoSourceBuilder.Build() is what "
            + "creates one. Take the handle at AddTable and use it after Build.");

    internal void Bind(PocoSource source) => _source = source;
}
