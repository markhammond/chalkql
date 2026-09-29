using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Ir;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// F160, D317 and D323 end to end: a context value whose type is known is held to the rule a
/// statement's parameter is — refused where it is handed over, named as the host named it — and a
/// host's <see cref="BindingConverter"/> turns its own types into values for both, once.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class BindingConverterTests(SharedSidecar sidecar)
{
    private const string Members = "SELECT id, org_id FROM members ORDER BY id";

    private readonly record struct OrgId(int Value);

    private readonly record struct UserId(int Value);

    private static readonly BindingConverter[] Converters =
    [
        BindingConverter.From<OrgId>(o => o.Value),
        BindingConverter.From<UserId>(u => u.Value),
    ];

    /// <summary>Text of 36 characters as the UUID a UUID target takes, and left alone everywhere else.</summary>
    private sealed class GuidText : BindingConverter<string>
    {
        public override bool TryConvert(string value, ChalkType target, out object? converted)
        {
            converted = null;
            if (target.Kind != TypeKind.Uuid || value.Length != 36 || !Guid.TryParse(value, out var uuid))
            {
                return false;
            }

            converted = uuid;
            return true;
        }
    }

    private sealed class Throws : BindingConverter<OrgId>
    {
        public override bool TryConvert(OrgId value, ChalkType target, out object? converted) =>
            throw new InvalidOperationException($"no org {value.Value}");
    }

    private sealed class Nothing : BindingConverter<OrgId>
    {
        public override bool TryConvert(OrgId value, ChalkType target, out object? converted)
        {
            converted = null;
            return true;
        }
    }

    // ---------------------------------------------------------------- the rule, without an engine

    [Fact]
    public void A_converter_turns_a_host_type_into_a_value_the_rule_then_holds()
    {
        var binding = new ValueBinding(Converters);

        Assert.Equal(7L, binding.Bind(new OrgId(7), ChalkType.Int32(), "Parameter @org"));
        Assert.Equal(7m, binding.Bind(new OrgId(7), ChalkType.Decimal(10, 2), "Parameter @org"));

        var refused = Assert.Throws<ArgumentException>(
            () => binding.Bind(new OrgId(7), ChalkType.String(), "Parameter @org"));
        Assert.StartsWith(
            "Parameter @org is STRING, and a Int32 returned by BindingConverter.From<OrgId> was bound to it:",
            refused.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_converter_that_declines_leaves_the_value_to_the_rule()
    {
        var binding = new ValueBinding([new GuidText()]);
        const string Text = "0e0e0e0e-0000-4000-8000-000000000001";

        Assert.Equal(Guid.Parse(Text), binding.Bind(Text, ChalkType.Uuid(), "Parameter @id"));
        Assert.Equal(Text, binding.Bind(Text, ChalkType.String(), "Parameter @name"));
        Assert.Throws<ArgumentException>(() => binding.Bind("not-a-guid", ChalkType.Uuid(), "Parameter @id"));
    }

    [Fact]
    public void A_converter_that_throws_is_named_with_the_subject_and_never_the_value()
    {
        var binding = new ValueBinding([new Throws()]);

        var refused = Assert.Throws<ArgumentException>(
            () => binding.Bind(new OrgId(4711), ChalkType.Int32(), "Context scalar 'org'"));

        Assert.StartsWith("Context scalar 'org' is I32, and Throws threw", refused.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(refused.InnerException);
        Assert.DoesNotContain("4711", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_converter_that_returns_null_binds_null_only_where_the_type_is_nullable()
    {
        var binding = new ValueBinding([new Nothing()]);

        Assert.Null(binding.Bind(new OrgId(1), ChalkType.Int32(nullable: true), "Parameter @org"));
        var refused = Assert.Throws<ArgumentException>(
            () => binding.Bind(new OrgId(1), ChalkType.Int32(), "Parameter @org"));
        Assert.Contains("which is not nullable, and Nothing returned NULL", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Converters_no_value_could_ever_choose_are_refused_when_registered()
    {
        Assert.Contains(
            "both convert OrgId",
            Assert.Throws<ArgumentException>(
                () => new ValueBinding([BindingConverter.From<OrgId>(o => o.Value), new Throws()])).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "must be a concrete type",
            Assert.Throws<ArgumentException>(
                () => new ValueBinding([BindingConverter.From<IComparable>(c => c)])).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "must be a concrete type",
            Assert.Throws<ArgumentException>(
                () => new ValueBinding([BindingConverter.From<Stream>(s => s.Length)])).Message,
            StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- F160: a folded context value

    /// <summary>0.3 rounded 1.5 to 2 and granted organization 2; the value is refused where it was handed over.</summary>
    [Fact]
    public async Task A_fraction_in_a_typed_context_list_is_refused_at_prepare()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync(TenancyFixture.Shared);

        var context = TenancyFixture.U1.WithRows("manager_orgs", Orgs(1.5));
        var refused = await Assert.ThrowsAsync<ArgumentException>(
            () => engine.WithEntitlements().PrepareAsync(Members, context).AsTask());

        Assert.StartsWith(
            "Context binding 'manager_orgs' column 'id' is I32, and a Double was bound to it:",
            refused.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_typed_context_scalar_that_would_change_is_refused_at_prepare()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync(TenancyFixture.Shared);

        var refused = await Assert.ThrowsAsync<ArgumentException>(
            () => engine.WithEntitlements().PrepareAsync(Members, Typed(TenancyFixture.U1, "1")).AsTask());

        Assert.StartsWith(
            "Context scalar 'user' is I32, and a String was bound to it:", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_type_in_a_context_list_binds_through_its_converter_and_is_refused_without_one()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var converted = TenancyFixture.U1
            .WithRows("manager_orgs", Orgs(new OrgId(1)))
            .WithRows("agent_orgs", Orgs(new OrgId(2)));

        await using (var engine = await EngineAsync(TenancyFixture.Shared, Converters))
        {
            var expected = await RowsAsync(engine, await engine.WithEntitlements().PrepareAsync(Members, TenancyFixture.U1));

            Assert.NotEmpty(expected);
            Assert.Equal(
                expected, await RowsAsync(engine, await engine.WithEntitlements().PrepareAsync(Members, converted)));
        }

        await using (var engine = await EngineAsync(TenancyFixture.Shared))
        {
            var refused = await Assert.ThrowsAsync<ArgumentException>(
                () => engine.WithEntitlements().PrepareAsync(Members, converted).AsTask());

            Assert.StartsWith(
                "Context binding 'manager_orgs' column 'id' is I32, and a OrgId was bound to it:",
                refused.Message,
                StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- a statement's parameter

    [Fact]
    public async Task A_host_type_bound_to_a_parameter_binds_through_its_converter()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync(TenancyFixture.Unentitled, Converters);
        var prepared = await engine.PrepareAsync("SELECT id FROM members WHERE org_id = ? ORDER BY id");

        var expected = await RowsAsync(engine, prepared, [1]);

        Assert.NotEmpty(expected);
        Assert.Equal(expected, await RowsAsync(engine, prepared, [new OrgId(1)]));
        Assert.Equal(expected, await RowsAsync(engine, prepared, [1L]));
        var refused = await Assert.ThrowsAsync<ArgumentException>(
            async () => await RowsAsync(engine, prepared, [1.0]));
        Assert.StartsWith("Parameter ", refused.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- execute-time binding

    /// <summary>
    /// A plan that left the subject open binds the principal's identity and pairs at execution: the
    /// same rule and the same converters apply there, and a value that would change is refused by
    /// <c>ExecuteAsync</c> naming the context entry.
    /// </summary>
    [Fact]
    public async Task An_execution_binds_its_context_as_a_prepare_does()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync(TenancyFixture.Shared, Converters);

        var baseline = Typed(TenancyFixture.U3, 3)
            .WithRows("subject_pairs", Pairs(3, 2));
        var converted = Typed(TenancyFixture.U3, new UserId(3))
            .WithRows("subject_pairs", Pairs(new OrgId(3), new OrgId(2)));
        var prepared = await engine.WithEntitlements()
            .PrepareAsync(Members, TenancyFixture.PartiallyBound(baseline));

        var expected = await RowsAsync(engine, prepared.Query, baseline);

        Assert.NotEmpty(expected);
        Assert.Equal(expected, await RowsAsync(engine, prepared.Query, converted));

        var refused = await Assert.ThrowsAsync<ArgumentException>(
            async () => await RowsAsync(engine, prepared.Query, Typed(TenancyFixture.U3, 3.5).WithRows("subject_pairs", Pairs(3, 2))));
        Assert.StartsWith(
            "Context scalar 'user' is I32, and a Double was bound to it:", refused.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private static ContextRelation Orgs(params object[] ids) => new()
    {
        Columns = ["id"],
        Rows = ids.Select(i => (IReadOnlyList<object?>)[i]).ToArray(),
        ColumnTypes = [ChalkType.Int32()],
    };

    private static ContextRelation Pairs(object subject, object within) => new()
    {
        Columns = ["subject", "within"],
        Rows = [[subject, within]],
        ColumnTypes = [ChalkType.Int32(), ChalkType.Int32()],
    };

    /// <summary>The principal with its identity bound to <paramref name="user"/> and typed I32.</summary>
    private static RequestContext Typed(RequestContext context, object user) => new()
    {
        Scalars = new Dictionary<string, object?>(context.Scalars, StringComparer.Ordinal) { ["user"] = user },
        ScalarTypes = new Dictionary<string, ChalkType>(StringComparer.Ordinal) { ["user"] = ChalkType.Int32() },
        Lists = context.Lists,
        Relations = context.Relations,
    };

    private async Task<ChalkEngine> EngineAsync(
        TenancyFixture fixture, IReadOnlyList<BindingConverter>? converters = null) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [fixture.Source],
            Planner = sidecar.CreatePlanner(),
            BindingConverters = converters ?? [],
        });

    private static async Task<string[]> RowsAsync(
        ChalkEngine engine, EntitledQuery prepared) =>
        await RowsAsync(engine, prepared.Query, (IReadOnlyList<object?>?)null);

    private static async Task<string[]> RowsAsync(
        ChalkEngine engine, PreparedQuery prepared, IReadOnlyList<object?>? parameters)
    {
        await using var execution = await engine.ExecuteAsync(prepared, parameters);
        return await DrainAsync(execution);
    }

    private static async Task<string[]> RowsAsync(
        ChalkEngine engine, PreparedQuery prepared, RequestContext context)
    {
        await using var execution = await engine.ExecuteAsync(prepared, context);
        return await DrainAsync(execution);
    }

    private static async Task<string[]> DrainAsync(QueryExecution execution)
    {
        var rows = new List<string>();
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                rows.AddRange(BatchReader.ToRows(batch).Select(r => string.Join("|", r.Select(v => v?.ToString() ?? ""))));
            }
        }

        return [.. rows];
    }
}
