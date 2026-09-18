using Chalk.Catalog;
using Chalk.Client;

namespace Chalk.Entitlements.Tenancy;

/// <summary>
/// The tenancy a subject grant reaches (§5, D204).
/// </summary>
public static class Tenancy
{
    /// <summary>
    /// The explicit "any tenancy": a subject grant on an individual wherever their rows are. Written
    /// out rather than left to a null, because a grant that reaches everywhere is a decision.
    /// </summary>
    public static object Anywhere { get; } = new AnywhereMarker();

    private sealed class AnywhereMarker
    {
        public override string ToString() => "Tenancy.Anywhere";
    }
}

/// <summary>
/// One confinement of a grant: a tenancy kind and the tenancy of it the grant is confined to
/// (D266 §1, <c>docs/design/40-conjoined-confinement.md</c>).
/// </summary>
/// <remarks>
/// A grant carries zero or more of these and <b>all of them must hold</b> for it to reach a row —
/// the conjunction lives inside one grant, while grants go on OR-ing with each other. An empty
/// <see cref="Kind"/> is what <c>ForSubject(kind, id, role, within: …)</c> writes: the sugar names
/// no kind because the subject dimension's declaration already does, and the binding resolves it to
/// the first kind that declaration names.
/// </remarks>
public sealed class Confinement
{
    /// <summary>
    /// The confining tenancy kind, or null for "the first kind the dimension's declaration names".
    /// </summary>
    public required Kind? Kind { get; init; }

    /// <summary>The tenancy of that kind this grant is confined to.</summary>
    public required object Id { get; init; }

    internal string KindName => Kind?.Name ?? "";

    public override string ToString() =>
        (KindName.Length == 0 ? "within" : KindName) + " = " + Id;
}

/// <summary>
/// One grant a principal holds (§5, D204–D205; D266 §1): a dimension, an identifier, a role, and the
/// tenancies it is confined to — none, one, or several at once.
/// </summary>
/// <remarks>
/// <para>
/// The package's context convention is <c>grants(kind, id, within, role, scope)</c>, and this is one
/// row of it, with <c>within</c> a list of <c>(kind, id)</c> since D266. What reaches the wire is
/// never this: the principal binds one list per <em>group</em> — a dimension, a role and the ordered
/// set of confining kinds — so every predicate the compiler emits is a membership test or a scalar
/// and folds natively (§2).
/// </para>
/// <para>
/// <b>The conjunction lives inside one grant.</b> Grants OR with each other and restrictions OR, so
/// a principal holding a region grant and a customer grant sees the union of the two; a principal
/// who should see only their intersection holds <em>one</em> grant carrying both confinements
/// (D266 §0).
/// </para>
/// </remarks>
public sealed class Grant
{
    /// <summary>The kind of the dimension this grant resolves against, or null for a global grant.</summary>
    internal KindDeclaration? Declared { get; init; }

    /// <summary>The kind's name, which is what a dimension is matched by.</summary>
    internal string Kind => Declared?.Name ?? "";

    /// <summary>The tenancy or subject identifier.</summary>
    public required object Id { get; init; }

    /// <summary>The role this grant confers.</summary>
    public required Role Grantee { get; init; }

    internal string Role => Grantee.Name;

    /// <summary>Whether this is a grant on an individual rather than on a container.</summary>
    public bool IsSubject { get; init; }

    /// <summary>Whether this grant reaches every tenancy, whatever a row's own.</summary>
    public bool IsGlobal { get; init; }

    /// <summary>
    /// The tenancies this grant is confined to, all of which must hold (D266 §1). Empty is
    /// <see cref="Tenancy.Anywhere"/>: no confinement at all.
    /// </summary>
    public IReadOnlyList<Confinement> Confinements { get; init; } = [];

    /// <summary>
    /// Whether this grant <b>says</b> it reaches every tenancy: <c>within: Tenancy.Anywhere</c>
    /// (§5, design 40 §1 as amended 2026-09-16).
    /// </summary>
    /// <remarks>
    /// It is the difference between a host that meant "anywhere" and a host that forgot a
    /// <c>within:</c>, which <see cref="Confinements"/> alone cannot tell: both are empty. A
    /// <em>subject</em> grant that is neither confined nor explicitly unconfined is refused at
    /// binding, because reaching every tenancy is the widest thing a grant can do and is said rather
    /// than implied by omission. A tenancy grant needs no such word — it names its own container
    /// already — so this is meaningless there and never set.
    /// </remarks>
    public bool Unconfined { get; init; }

    /// <summary>
    /// The request scope this grant applies in; null means every scope. A principal bound for one
    /// scope carries only the grants that scope admits, which is why the scope never has to appear
    /// in a membership test.
    /// </summary>
    public Scope? Scope { get; init; }

    internal string ScopeName => Scope?.Name ?? "";

    /// <summary>A grant on a container: every row of that tenancy, in this role.</summary>
    public static Grant ForTenancy(Kind kind, object id, Role role, Scope? scope = null) =>
        new()
        {
            Declared = Declaration(kind.Declaration, nameof(kind)),
            Id = id,
            Grantee = role,
            Scope = scope,
        };

    /// <summary>
    /// A grant on an individual, confined to one tenancy, to several through
    /// <see cref="Within(Kind, object)"/>, or to none at all (§5, D205: an escalation is a narrow
    /// grant, not a profile; D266 §1).
    /// </summary>
    /// <param name="within">
    /// The tenancy of the subject dimension's <em>first declared</em> confining kind, or
    /// <see cref="Tenancy.Anywhere"/>. Confinement along a kind this does not name is written with
    /// <see cref="Within(Kind, object)"/>, which may follow this call or stand in place of it.
    /// <b>Omitting it is not the same as <see cref="Tenancy.Anywhere"/></b>: a subject grant that
    /// names no tenancy and no <see cref="Within(Kind, object)"/> either is refused at binding
    /// (§5, design 40 §1 as amended 2026-09-16).
    /// </param>
    public static Grant ForSubject(
        SubjectKind kind, object id, Role role, object? within = null, Scope? scope = null) =>
        new()
        {
            Declared = Declaration(kind.Declaration, nameof(kind)),
            Id = id,
            Grantee = role,
            IsSubject = true,
            Unconfined = ReferenceEquals(within, Tenancy.Anywhere),
            Confinements = within is null || ReferenceEquals(within, Tenancy.Anywhere)
                ? []
                : [new Confinement { Kind = null, Id = within }],
            Scope = scope,
        };

    /// <summary>
    /// A grant that holds everywhere, in this role. Refused at bind unless the policy sets
    /// <c>AllowGlobalGrants</c> (D205): a tier that may see everything says so once, out loud.
    /// </summary>
    public static Grant Global(Role role) =>
        new() { Id = "", Grantee = role, IsGlobal = true };

    private static KindDeclaration Declaration(KindDeclaration? declaration, string parameter) =>
        declaration ?? throw new ArgumentException(
            "a default kind handle names no kind. Obtain one from TenancyPolicy.Tenancy(name) or "
            + "TenancyPolicy.Subject(name, within: …) "
            + "(docs/design/45-typed-tenancy-surface.md §1, D270).",
            parameter);

    /// <summary>
    /// The same grant with one more confinement: it reaches a row only where the row's tenancy of
    /// <paramref name="kind"/> is <paramref name="id"/> as well (D266 §1).
    /// </summary>
    /// <remarks>
    /// It may be called once per tenancy kind. A kind named twice is refused, and so is a
    /// confinement along the grant's own kind — which would be a grant on two tenancies of one kind,
    /// and that is two grants — and a confinement of a global grant, which reaches every tenancy by
    /// construction.
    /// </remarks>
    /// <exception cref="CatalogValidationException">Any of those three.</exception>
    public Grant Within(Kind kind, object id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var confining = Declaration(kind.Declaration, nameof(kind)).Name;

        if (IsGlobal)
        {
            throw new CatalogValidationException(
                "tenancy.grants",
                $"a global grant is confined along '{confining}'. A global grant reaches every tenancy "
                + "whatever a row's own, so confining one is a contradiction: hold an ordinary grant "
                + "on the tenancy instead (docs/design/40-conjoined-confinement.md §1, D266).");
        }

        if (string.Equals(confining, Kind, StringComparison.Ordinal))
        {
            throw new CatalogValidationException(
                "tenancy.grants",
                $"the grant on '{Kind}' is confined along '{confining}', which is its own kind. A grant "
                + "names one tenancy of its kind already; two tenancies of one kind are two grants "
                + "(docs/design/40-conjoined-confinement.md §1, D266).");
        }

        foreach (var confinement in Confinements)
        {
            if (string.Equals(confinement.KindName, confining, StringComparison.Ordinal))
            {
                throw new CatalogValidationException(
                    "tenancy.grants",
                    $"the grant on '{Kind}' names the confining kind '{confining}' twice. Within may be "
                    + "called once per tenancy kind: all the confinements of one grant must hold at "
                    + "once, so two of one kind would reach no row at all "
                    + "(docs/design/40-conjoined-confinement.md §1, D266).");
            }
        }

        return new Grant
        {
            Declared = Declared,
            Id = Id,
            Grantee = Grantee,
            IsSubject = IsSubject,
            IsGlobal = IsGlobal,
            Scope = Scope,
            Confinements = [.. Confinements, new Confinement { Kind = kind, Id = id }],
        };
    }
}

/// <summary>
/// A caller, as the grants they hold — and the <see cref="RequestContext"/> those grants bind to
/// (§5, D210).
/// </summary>
/// <remarks>
/// <para>
/// The binding is the whole of layer B's runtime: one list per group — a dimension, a role and the
/// ordered set of kinds confining it (D266 §4) — and the scalars <c>user</c>, <c>mask_key</c> and
/// <c>global</c> — plus one boolean per role a global grant confers, so that a rule written for a
/// role holds for a principal who holds it everywhere. Nothing of the policy's vocabulary reaches
/// the wire (D143); what does is a set of names the descriptor's SQL already refers to.
/// </para>
/// <para>
/// <see cref="Purpose"/> and <see cref="Actor"/> are audit metadata rather than context (D205): they
/// are carried into the audit event, which is otherwise value-free.
/// </para>
/// </remarks>
public sealed class TenancyPrincipal
{
    /// <summary>The caller's own identifier, bound as the scalar <c>user</c>.</summary>
    public required object User { get; init; }

    /// <summary>The grants this caller holds.</summary>
    public IReadOnlyList<Grant> Grants { get; init; } = [];

    /// <summary>
    /// The key every keyed mask is computed under, bound as the scalar <c>mask_key</c>. One per
    /// principal and supplied by the host: a fingerprint that is stable for one caller and different
    /// for another is what makes a token joinable without being a value.
    /// </summary>
    public string MaskKey { get; init; } = "";

    /// <summary>
    /// The request scope. A grant carrying a scope is bound only when it matches; a grant carrying
    /// none is bound in every scope.
    /// </summary>
    public Scope? Scope { get; init; }

    internal string ScopeName => Scope?.Name ?? "";

    /// <summary>Audit metadata: why this request is being made (D205). Never a context value.</summary>
    public string Purpose { get; init; } = "";

    /// <summary>Audit metadata: who is making it on whose behalf (D205).</summary>
    public string Actor { get; init; } = "";

    /// <summary>The fold ceiling this request plans under (§2); 0 leaves the planner's own.</summary>
    public int FoldMaxRows { get; init; }

    /// <summary>
    /// Context lists and scalars the host computes itself, for a table in
    /// <see cref="TenancyMode.Custom"/> whose predicate names them.
    /// </summary>
    public IReadOnlyDictionary<string, ContextRelation> CustomLists { get; init; } =
        new Dictionary<string, ContextRelation>(StringComparer.Ordinal);

    /// <summary>The same for scalars a custom predicate names.</summary>
    public IReadOnlyDictionary<string, object?> CustomScalars { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);
}
