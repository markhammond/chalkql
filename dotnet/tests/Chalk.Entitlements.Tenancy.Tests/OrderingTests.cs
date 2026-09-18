using Chalk.Catalog;
using Chalk.TestKit;

namespace Chalk.Entitlements.Tenancy.Tests;

/// <summary>
/// The host's rule list is the priority (<c>docs/design/16-entitlements.md</c> §5, D222): for each
/// set of roles held <em>in the row's own tenancy</em>, what the emitted rules disclose.
/// </summary>
/// <remarks>
/// <para>
/// The model is an ordinal walk: rules are read in the order the host wrote them, a matching rule
/// sets the tentative verdict, and <c>StopOnMatch</c> ends the reading — so the answer is the first
/// matching stop-rule, else the last matching continue-rule, else nothing. The core knows none of
/// that: it evaluates an ordered list of rules and takes the first that matches. These are the tests
/// that the order the compiler emits reproduces the walk — the stop-rules in host order, then the
/// continue-rules in reverse host order — rather than something near it.
/// </para>
/// <para>
/// Rules are evaluated here rather than through the planner because what is under test is the
/// <em>ordering</em>, and the shape the compiler emits is small enough to read directly: a rule
/// condition is a disjunction of membership tests over context lists, optionally AND-ed with the
/// rule's own condition, plus the resource-owner term. Holding a role in the row's tenancy is
/// holding that role's list; the corpus then proves the same table end to end, through the real
/// fold.
/// </para>
/// </remarks>
public sealed class OrderingTests
{
    private static readonly TenancyEntitlements Compiled = TenancyPolicyFixture.Shared.Entitlements;

    private static TableEntitlementDescriptor Members =>
        Compiled.For(TenancyPolicyFixture.SourceName, "members")!;

    private static TableEntitlementDescriptor Orders =>
        Compiled.For(TenancyPolicyFixture.SourceName, "orders")!;

    // ---------------------------------------------------------------- §8's own table

    /// <summary>A protected column of a realm two roles mask differently and one discloses.</summary>
    public static TheoryData<string, string, Disclosure, string> FirstName() => new()
    {
        // roles held in this row's tenancy, whether the principal wrote the row, the name, the mask
        { "", "", Disclosure.None, "" },
        { "manager", "", Disclosure.Full, "" },
        { "agent", "", Disclosure.Masked, "SUBSTRING(first_name, 1, 1)" },
        { "auditor", "", Disclosure.Masked, "FINGERPRINT(first_name, @ctx.mask_key)" },
        { "manager,agent", "", Disclosure.Full, "" },
        { "manager,auditor", "", Disclosure.Full, "" },
        // The agent-and-auditor row: both grant MASK, and the earlier rule wins, so the mask is the
        // one the policy wrote first — the initial, not the token.
        { "agent,auditor", "", Disclosure.Masked, "SUBSTRING(first_name, 1, 1)" },
        { "self", "", Disclosure.None, "" },
    };

    [Theory]
    [MemberData(nameof(FirstName))]
    public void A_realm_column_resolves_as_the_order_says(
        string roles, string creator, Disclosure expected, string mask)
    {
        var (name, applied) = Evaluate(Members, 2, roles, creator.Length > 0);

        Assert.Equal(expected, name);
        Assert.Equal(mask, applied);
    }

    /// <summary>
    /// A realm no rule names: nothing discloses it, whoever asks. That is D216's inherit-is-deny,
    /// which D222 leaves standing — the walk ends with no tentative verdict at all.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("manager")]
    [InlineData("agent")]
    [InlineData("manager,agent,auditor,self")]
    public void A_realm_nobody_grants_is_disclosed_to_nobody(string roles)
    {
        Assert.Equal(Disclosure.None, Evaluate(Members, 4, roles, false).Name);
    }

    /// <summary>
    /// A population-only column: <c>amount</c> is <em>protected</em> because a rule names it, so a
    /// role no rule speaks for gets nothing. Only the auditor's rule names it, and an agent who is
    /// also an auditor therefore sees the aggregates. The resource-owner row is the fail-safe, and
    /// it comes before every rule: the principal owns the values.
    /// </summary>
    [Theory]
    [InlineData("", false, Disclosure.None)]
    [InlineData("manager", false, Disclosure.None)]
    [InlineData("agent", false, Disclosure.None)]
    [InlineData("auditor", false, Disclosure.AggregateOnly)]
    [InlineData("agent,auditor", false, Disclosure.AggregateOnly)]
    [InlineData("", true, Disclosure.Full)]
    [InlineData("auditor", true, Disclosure.Full)]
    public void A_population_only_column_resolves_as_the_order_says(
        string roles, bool creator, Disclosure expected)
    {
        Assert.Equal(expected, Evaluate(Orders, 3, roles, creator).Name);
    }

    /// <summary>The resource-owner row on a masked column: the owner sees what they own.</summary>
    [Theory]
    [InlineData("", false, Disclosure.None)]
    [InlineData("", true, Disclosure.Full)]
    [InlineData("agent", false, Disclosure.Masked)]
    [InlineData("agent", true, Disclosure.Full)]
    [InlineData("self", false, Disclosure.Full)]
    public void The_resource_owner_rule_comes_before_every_other(
        string roles, bool creator, Disclosure expected)
    {
        Assert.Equal(expected, Evaluate(Orders, 4, roles, creator).Name);
    }

    // ---------------------------------------------------------------- the order is the meaning

    /// <summary>
    /// The same two rules in both orders: a principal who holds both roles gets whichever the host
    /// wrote first. This is the whole of D222 in one test — precedence is position, and there is no
    /// combination rule behind it to argue with.
    /// </summary>
    [Theory]
    [InlineData("manager", "manager", Disclosure.Full)]
    [InlineData("manager", "agent", Disclosure.Masked)]
    [InlineData("manager", "manager,agent", Disclosure.Full)]
    [InlineData("agent", "manager", Disclosure.Full)]
    [InlineData("agent", "agent", Disclosure.Masked)]
    [InlineData("agent", "manager,agent", Disclosure.Masked)]
    public void The_same_roles_in_both_orders_give_the_rule_written_first(
        string first, string roles, Disclosure expected)
    {
        var compiled = Compile(p => first == "manager"
            ? new[] { Full(p, "manager"), Masked(p, "agent") }
            : [Masked(p, "agent"), Full(p, "manager")]);

        Assert.Equal(expected, Evaluate(compiled, 2, roles, false).Name);
    }

    /// <summary>
    /// A stop-rule after a continue-rule: the stop-rule is the answer wherever it matches, and the
    /// continue-rule is the answer only where it does not. That is what "the first matching
    /// stop-rule, else the last matching continue-rule" comes to for two rules.
    /// </summary>
    [Theory]
    [InlineData("agent", Disclosure.Masked)]
    [InlineData("manager", Disclosure.Full)]
    [InlineData("manager,agent", Disclosure.Full)]
    public void A_stop_rule_after_a_continue_rule_wins_wherever_it_matches(
        string roles, Disclosure expected)
    {
        var compiled = Compile(p => [Masked(p, "agent", stop: false), Full(p, "manager")]);

        Assert.Equal(expected, Evaluate(compiled, 2, roles, false).Name);
    }

    /// <summary>
    /// Two continue-rules: neither ends the walk, so the tentative verdict is whichever matched
    /// <em>last</em> — the reverse of what two stop-rules would have given, which is exactly why the
    /// compiler emits the continue-rules reversed.
    /// </summary>
    [Theory]
    [InlineData("manager", Disclosure.Full)]
    [InlineData("agent", Disclosure.Masked)]
    [InlineData("manager,agent", Disclosure.Masked)]
    public void Two_continue_rules_give_the_last_one_that_matched(string roles, Disclosure expected)
    {
        var compiled = Compile(p =>
            [Full(p, "manager", stop: false), Masked(p, "agent", stop: false)]);

        Assert.Equal(expected, Evaluate(compiled, 2, roles, false).Name);
    }

    /// <summary>
    /// A rule naming neither realm nor column — the explicit permissive form a role that should see
    /// everything writes (D216) — is an ordinary rule, so where the host puts it is what it means:
    /// first, it overrides the rule that would have capped it; last, it is what a role falls
    /// through to.
    /// </summary>
    [Theory]
    [InlineData(true, "ops", Disclosure.Full)]
    [InlineData(true, "agent", Disclosure.Masked)]
    [InlineData(true, "ops,agent", Disclosure.Full)]
    [InlineData(false, "ops", Disclosure.Full)]
    [InlineData(false, "agent", Disclosure.Masked)]
    [InlineData(false, "ops,agent", Disclosure.Masked)]
    public void A_rule_naming_neither_realm_nor_column_reads_where_it_stands(
        bool first, string roles, Disclosure expected)
    {
        var compiled = Compile(
            p =>
            {
                var everything = new AccessRule { Roles = [p.Role("ops")], Grants = Verdict.Full };
                return first
                    ? new[] { everything, Masked(p, "agent") }
                    : [Masked(p, "agent"), everything];
            },
            "ops");

        Assert.Equal(expected, Evaluate(compiled, 2, roles, false).Name);
    }

    /// <summary>
    /// A rule with a condition of its own (D221) matches only the rows the condition holds for, so
    /// the same principal sees one thing on one row and another on the next — which is the whole
    /// reason a per-row disclosure column exists.
    /// </summary>
    [Theory]
    [InlineData(true, Disclosure.Full)]
    [InlineData(false, Disclosure.Masked)]
    public void A_conditional_rule_matches_only_the_rows_its_condition_holds_for(
        bool holds, Disclosure expected)
    {
        var compiled = Compile(
            p => [Full(p, "analyst", when: Sql.Of(Condition)), Masked(p, "analyst")], "analyst");

        Assert.Equal(
            expected, Evaluate(compiled, 2, "analyst", false, holds ? Condition : "").Name);
    }

    // ---------------------------------------------------------------- the little declarations

    private const string Condition = "role = 'analyst'";

    private static AccessRule Full(
        TenancyPolicy policy, string role, bool stop = true, Sql? when = null) =>
        new()
        {
            Roles = [policy.Role(role)],
            Realm = policy.Realm("pii"),
            Grants = Verdict.Full,
            StopOnMatch = stop,
            When = when,
        };

    private static AccessRule Masked(TenancyPolicy policy, string role, bool stop = true) => new()
    {
        Roles = [policy.Role(role)],
        Realm = policy.Realm("pii"),
        Grants = Verdict.Mask,
        Mask = Sql.Of("lambda v: SUBSTRING(v, 1, 1)"),
        StopOnMatch = stop,
    };

    private static readonly SchemaDescriptor Schema =
        TenancyFixture.Unentitled.Source.DescribeSchema();

    /// <summary>
    /// The catalog these little policies are declared over, which here is that one schema: a table
    /// is obtained from the source it belongs to and from nowhere else (D270 §1).
    /// </summary>
    private static readonly CatalogContext Catalog = new()
    {
        ContextId = TenancyFixture.ContextId,
        Epoch = TenancyFixture.Epoch,
        Schemas = [Schema],
    };

    /// <summary>One realm of one column, and the rules in the order the case wrote them.</summary>
    /// <remarks>
    /// A case writes its rules against the policy it is handed rather than on its own, because a
    /// rule names the role and realm handles <em>that</em> policy declared and no string produces one
    /// (D270 §1). The role names are still the case's, and naming one twice declares it once.
    /// </remarks>
    private static TableEntitlementDescriptor Compile(
        Func<TenancyPolicy, IReadOnlyList<AccessRule>> rules, params string[] extraRoles)
    {
        var policy = TenancyPolicy.Declare(Catalog);
        var members = policy.Source(TenancyPolicyFixture.SourceName).Table("members");
        policy.Role("manager");
        policy.Role("agent");
        foreach (var extra in extraRoles)
        {
            policy.Role(extra);
        }

        members
            .Tenancy(r => r.Direct(policy.Tenancy("org"), members.Column("org_id")))
            .Realm(policy.Realm("pii"), members.Column("first_name"));
        foreach (var rule in rules(policy))
        {
            members.Access(rule);
        }

        return policy.Compile([Schema]).For(members)!;
    }

    // ---------------------------------------------------------------- the little evaluator

    /// <summary>
    /// First match wins, over the shape the compiler emits: a disjunction of membership tests,
    /// optionally AND-ed with the rule's own condition, plus the resource-owner term.
    /// </summary>
    private static (Disclosure Name, string Mask) Evaluate(
        TableEntitlementDescriptor table,
        int column,
        string roles,
        bool creator,
        string trueCondition = "")
    {
        var held = new HashSet<string>(
            roles.Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        var entitled = table.FindColumn(column)
            ?? throw new InvalidOperationException($"no entitlement for column {column}");

        foreach (var rule in entitled.Rules)
        {
            if (Holds(rule.When, held, creator, trueCondition))
            {
                var mask = rule.Mask.Length > 0 ? rule.Mask : entitled.Mask;
                return (rule.Then, rule.Then == Disclosure.Masked ? mask : "");
            }
        }

        return (entitled.Otherwise, "");
    }

    /// <summary>
    /// The roles half and, where the rule carried one, the condition half. The roles half is all
    /// <c>OR</c>s, so the first <c>") AND ("</c> is where the compiler joined the two (D221).
    /// </summary>
    private static bool Holds(
        string condition, HashSet<string> roles, bool creator, string trueCondition)
    {
        var split = condition.IndexOf(") AND (", StringComparison.Ordinal);
        var positive = split < 0 ? condition : condition[..(split + 1)];
        var when = split < 0 ? "" : condition[(split + ") AND (".Length)..^1];
        return Any(positive, roles, creator)
            && (when.Length == 0 || string.Equals(when, trueCondition, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether any term of this disjunction holds. A role is held in the row's own tenancy exactly
    /// when the tenancy list of that role is: the pair, identifier and global terms are how a grant
    /// reaches a row from somewhere else, and this table is about the tenancy itself.
    /// </summary>
    private static bool Any(string text, HashSet<string> roles, bool creator)
    {
        if (creator && text.Contains("created_by = @ctx.user", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var role in roles)
        {
            if (text.Contains("@ctx.org_" + role + ")", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
