namespace Chalk.Entitlements.Tenancy;

/// <summary>
/// The grantees an access rule may name beside the policy's own roles (D269 (c), design 43).
/// </summary>
/// <remarks>
/// <para>
/// A rule's role half is "the roles held in this row's tenancy", which is a narrower thing than
/// "everyone who may see this row": a row admitted by the resource-owner fail-safe alone, or by a
/// global grant, is within no role's scope. A host that means the wider thing had no way to say so,
/// and a rule that meant it read per-row where the same grant written by hand read as a constant —
/// which is the divergence F75 measured between the two layers.
/// </para>
/// <para>
/// So the host spells the grant. <see cref="Owner"/> names the row's resource owner beside the
/// roles; <see cref="Visible"/> names everyone the table's row predicate admits — every declared
/// role, the owner and the global grant — and the compiler writes that predicate, textually, as the
/// rule's condition, so §3.3's fold reads the column as a constant exactly as it does for a
/// hand-written descriptor.
/// </para>
/// <para>
/// Since D270 these are two <see cref="Role"/> <b>instances</b> rather than reserved names
/// (docs/design/45-typed-tenancy-surface.md §1, §6 (b)). A <see cref="Role"/> exists only where a
/// policy declared one or where one of these two was named, so no string a host writes — by accident
/// or otherwise — can produce either, and the reservation check <c>WithRoles</c> carried is gone
/// with the strings it checked.
/// </para>
/// </remarks>
public static class Roles
{
    /// <summary>
    /// The row's resource owner: the rule's condition gains
    /// <c>&lt;ResourceOwner column&gt; = @ctx.user</c> beside the roles' scopes.
    /// </summary>
    /// <remarks>
    /// Refused by name on a table that declares no <c>ResourceOwner</c>: there is no column to
    /// compare, and the alternative — a condition that quietly grants nothing — is the kind of
    /// silence a policy language should not have.
    /// </remarks>
    public static Role Owner { get; } =
        new(new RoleDeclaration { Name = "Roles.Owner", Marker = RoleMarker.Owner });

    /// <summary>
    /// Everyone the table's row predicate admits: the rule's condition <em>is</em> that predicate.
    /// </summary>
    /// <remarks>
    /// It is the whole of the row's visibility written once — every declared role's scope, the
    /// resource owner's fail-safe where the table declares one, and the global grant — so it names
    /// no other role beside it, and a rule that does is refused rather than silently widened.
    /// </remarks>
    public static Role Visible { get; } =
        new(new RoleDeclaration { Name = "Roles.Visible", Marker = RoleMarker.Visible });
}
