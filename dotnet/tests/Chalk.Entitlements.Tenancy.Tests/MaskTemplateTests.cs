using Chalk.Catalog;
using Chalk.TestKit;

namespace Chalk.Entitlements.Tenancy.Tests;

/// <summary>
/// Mask templates in lambda notation (<c>docs/design/16-entitlements.md</c> §5, D219).
/// </summary>
/// <remarks>
/// The text the compiler emits is the contract with the core, so these assert the instantiated SQL
/// rather than the declaration: one template, several columns, and each column's own name where the
/// parameter stood.
/// </remarks>
public sealed class MaskTemplateTests
{
    /// <summary>
    /// One template over a realm of two columns: the same rule, and each column's own name where the
    /// parameter stood. This is what the template is for.
    /// </summary>
    [Fact]
    public void One_template_covers_every_column_of_a_realm()
    {
        var members = Compile("lambda v: SUBSTRING(v, 1, 1)");

        Assert.Equal("SUBSTRING(first_name, 1, 1)", Mask(members, 2));
        Assert.Equal("SUBSTRING(last_name, 1, 1)", Mask(members, 3));
    }

    /// <summary>A template that reads the context: only the parameter is substituted.</summary>
    [Fact]
    public void The_context_is_not_a_parameter()
    {
        var members = Compile("lambda v: FINGERPRINT(v, @ctx.mask_key)");

        Assert.Equal("FINGERPRINT(first_name, @ctx.mask_key)", Mask(members, 2));
        Assert.Equal("FINGERPRINT(last_name, @ctx.mask_key)", Mask(members, 3));
    }

    /// <summary>
    /// Token-wise, not textually: the parameter inside a string literal or a quoted identifier is
    /// text, and so is an identifier standing after a <c>.</c>.
    /// </summary>
    [Theory]
    [InlineData("lambda v: COALESCE(v, 'v')", "COALESCE(first_name, 'v')")]
    [InlineData("lambda v: COALESCE(v, 'it''s v')", "COALESCE(first_name, 'it''s v')")]
    [InlineData("lambda v: COALESCE(\"v\", v)", "COALESCE(\"v\", first_name)")]
    [InlineData("lambda v: CASE WHEN @ctx.v THEN v ELSE '' END", "CASE WHEN @ctx.v THEN first_name ELSE '' END")]
    [InlineData("lambda v: UPPER(V)", "UPPER(first_name)")]
    [InlineData("lambda value: SUBSTRING(value, 1, 1) || 'value'", "SUBSTRING(first_name, 1, 1) || 'value'")]
    public void The_substitution_is_token_wise(string template, string expected) =>
        Assert.Equal(expected, Mask(Compile(template), 2));

    /// <summary>
    /// A mask without the prefix is what it always was: the text itself. It can only name one
    /// column, which is what a template is for — over a realm of two, naming one of them would be a
    /// mask reading another protected column and D220 refuses it.
    /// </summary>
    [Fact]
    public void A_mask_without_the_prefix_is_verbatim() =>
        Assert.Equal(
            "SUBSTRING(first_name, 1, 1)",
            Mask(Compile("SUBSTRING(first_name, 1, 1)", "first_name"), 2));

    /// <summary>The retired token, refused where it was written and naming what replaces it.</summary>
    [Fact]
    public void The_retired_token_is_refused_naming_the_lambda_form()
    {
        var refusal = Assert.Throws<CatalogValidationException>(
            () => Compile("SUBSTRING({column}, 1, 1)"));

        Assert.Contains("{column}", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("lambda v: SUBSTRING(v, 1, 1)", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two parameters: there is one thing for a parameter to stand for, so a second has nothing to
    /// name and the refusal says so.
    /// </summary>
    [Fact]
    public void Two_parameters_are_refused()
    {
        var refusal = Assert.Throws<CatalogValidationException>(
            () => Compile("lambda v, w: SUBSTRING(v, 1, 1)"));

        Assert.Contains("2 parameters", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A parameter that is also a column name: every token of that name would become the masked
    /// column, so the template could not read the real column at all.
    /// </summary>
    [Fact]
    public void A_parameter_that_shadows_a_column_is_refused()
    {
        var refusal = Assert.Throws<CatalogValidationException>(
            () => Compile("lambda postcode: SUBSTRING(postcode, 1, 1)"));

        Assert.Contains("postcode", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("column of 'members'", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A template with no body, and one with no colon: both refused naming the form.</summary>
    [Theory]
    [InlineData("lambda v:")]
    [InlineData("lambda v SUBSTRING(v, 1, 1)")]
    public void A_malformed_template_is_refused(string template) =>
        Assert.Throws<CatalogValidationException>(() => Compile(template));

    /// <summary>
    /// A template a winning rule never reaches is refused too: it is a mistake in the declaration,
    /// and where it was written is the only place a message can name.
    /// </summary>
    [Fact]
    public void A_template_no_rule_reaches_is_refused_all_the_same()
    {
        var policy = TenancyPolicy.Declare(Catalog);
        var members = policy.Source(TenancyPolicyFixture.SourceName).Table("members");
        var pii = policy.Realm("pii");
        members
            .Tenancy(r => r.Direct(policy.Tenancy("org"), members.Column("org_id")))
            .Realm(pii, members.Column("first_name"))
            // Full, so the mask is never emitted — and still refused.
            .Access(new AccessRule
            {
                Roles = [policy.Role("manager")],
                Realm = pii,
                Grants = Verdict.Full,
                Mask = Sql.Of("lambda v, w: v"),
            });

        var refusal = Assert.Throws<CatalogValidationException>(() => policy.Compile([Schema]));

        Assert.Contains("2 parameters", refusal.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- what a mask may read (D220)

    /// <summary>
    /// A mask reads its own column and anything unprotected; another protected column is refused
    /// naming both, because the sanitiser is evaluated over the raw row (D220).
    /// </summary>
    [Fact]
    public void A_mask_that_reads_another_protected_column_is_refused()
    {
        var refusal = Assert.Throws<CatalogValidationException>(
            () => Compile("lambda v: SUBSTRING(last_name, 1, 1)"));

        Assert.Contains("reads 'last_name'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("protected column", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>An unprotected column of the same row is ordinary data and may be read.</summary>
    [Fact]
    public void A_mask_may_read_an_unprotected_column() =>
        Assert.Equal(
            "SUBSTRING(first_name, 1, 1) || postcode",
            Mask(Compile("lambda v: SUBSTRING(v, 1, 1) || postcode"), 2));

    // ---------------------------------------------------------------- the little fixture

    private static readonly SchemaDescriptor Schema =
        TenancyFixture.Unentitled.Source.DescribeSchema();

    /// <summary>
    /// The catalog a policy is declared over, which here is that one schema: a table is obtained
    /// from the source it belongs to and from nowhere else (D270 §1).
    /// </summary>
    private static readonly CatalogContext Catalog = new()
    {
        ContextId = TenancyFixture.ContextId,
        Epoch = TenancyFixture.Epoch,
        Schemas = [Schema],
    };

    /// <summary>§8's <c>pii</c> realm with one masking rule, so the mask is what varies.</summary>
    private static TableEntitlementDescriptor Compile(string mask, params string[] realm)
    {
        var policy = TenancyPolicy.Declare(Catalog);
        var members = policy.Source(TenancyPolicyFixture.SourceName).Table("members");
        var pii = policy.Realm("pii");
        string[] columns = realm.Length > 0 ? realm : ["first_name", "last_name"];

        members
            .Tenancy(r => r.Direct(policy.Tenancy("org"), members.Column("org_id")))
            .Realm(pii, columns.Select(name => members.Column(name)).ToArray())
            .Access(new AccessRule
            {
                Roles = [policy.Role("agent")],
                Realm = pii,
                Grants = Verdict.Mask,
                Mask = Sql.Of(mask),
            });

        return policy.Compile([Schema]).For(members)!;
    }

    private static string Mask(TableEntitlementDescriptor table, int ordinal) =>
        (table.FindColumn(ordinal)
            ?? throw new InvalidOperationException($"no entitlement for column {ordinal}"))
        .Rules[0].Mask;
}
