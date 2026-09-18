namespace Chalk.Sources.Ado;

/// <summary>
/// What a host or a vendor package knows about its ADO.NET provider, declared once on
/// <see cref="AdoSourceBuilder.Provider"/> so the reader never has to find it out for itself (D263,
/// ADR 0046).
/// </summary>
/// <remarks>
/// <para>
/// Today this is the one trait D149's text strategy needs. <c>Chalk.Sources.Ado</c> otherwise names
/// no provider (D263): the reader decides the synchronous null check by reflection over the type
/// itself, and decides the text strategy from this declaration when there is one, or measures it
/// once per process per column type when there is not — the same measurement the reader always
/// ran, just now the fallback rather than the only path.
/// </para>
/// <para>
/// Extensible on purpose: a nullable property here says nothing about the others, so a future trait
/// — a batch-size hint, a native-array capability — is another nullable property added the same
/// way, and a host that declared only <see cref="Text"/> keeps compiling and keeps meaning the same
/// thing. Nothing beyond <see cref="Text"/> is declared here yet; ADR 0046 §2 says what else was
/// considered and set aside for a case that asks for it.
/// </para>
/// </remarks>
public sealed class AdoProviderTraits
{
    /// <summary>
    /// How this provider's reader should reach a TEXT value's bytes (D149) — declared instead of
    /// measured. Null (the default) leaves it to <c>AdoBatchReader</c>'s per-process, per-column-type
    /// probe, exactly as an undeclared provider is read today. Never <see cref="TextStrategy.Undecided"/>:
    /// that value means "not declared", which leaving this null already says, and
    /// <see cref="AdoSourceBuilder.Provider"/> refuses it.
    /// </summary>
    public TextStrategy? Text { get; init; }
}
