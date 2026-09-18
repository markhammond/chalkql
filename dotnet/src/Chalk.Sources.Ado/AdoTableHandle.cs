namespace Chalk.Sources.Ado;

/// <summary>
/// One table of an ADO source, as a handle (D271 (h),
/// <c>docs/design/44-catalog-registration.md</c> §6 (h)): the source it belongs to, the schema and
/// the name, obtained once and passed everywhere the table is meant afterwards.
/// </summary>
/// <remarks>
/// Untyped by row, unlike <c>PocoTable&lt;T&gt;</c>: an ADO source's rows are a reader's, not a
/// CLR type's, and there is nothing for a row type to be. What the handle is for here is
/// <c>refresh.Refresh(orders)</c> — re-describing that one table, statistics included, on the source
/// it actually belongs to — which is the scoped refresh a database-backed source needs and the one
/// a string pair can get wrong.
/// </remarks>
public readonly record struct AdoTable : ITableTarget
{
    internal AdoTable(AdoTableBinding binding) => Binding = binding;

    internal AdoTableBinding? Binding { get; }

    /// <summary>The name Chalk addresses this table by, as the catalog declares it.</summary>
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

    internal AdoTableBinding Required() =>
        Binding ?? throw new InvalidOperationException(
            "a default AdoTable handle names no table. Obtain one from AdoSourceBuilder's "
            + "AddTable(…, out var table, …) or from AdoSource.Table(name) "
            + "(docs/design/44-catalog-registration.md §6 (h), D271).");

    public override string ToString() => $"{Schema}.{Name}";
}

/// <summary>
/// What an <see cref="AdoTable"/> is a handle <em>to</em>: the names a registration fixed, and the
/// source that serves them once <c>Build()</c> has made one.
/// </summary>
internal sealed class AdoTableBinding
{
    internal required string SourceId { get; init; }

    internal required string Schema { get; init; }

    internal required string Table { get; init; }

    private AdoSource? _source;

    internal ISourceRuntime Runtime =>
        _source ?? throw new InvalidOperationException(
            $"the handle for table '{Table}' has no source yet: AdoSourceBuilder.Build() is what "
            + "creates one. Take the handle at AddTable and use it after Build.");

    internal void Bind(AdoSource source) => _source = source;
}
