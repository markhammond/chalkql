using Chalk.Catalog;
using Chalk.Client;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// Context binding end to end (step 26, <c>docs/design/16-entitlements.md</c> §2, D152): what a host
/// binds, what reaches the sidecar, and what the sidecar refuses.
/// </summary>
/// <remarks>
/// Nothing here has an entitlement to enforce yet, which is exactly what makes the zero-cost half of
/// it checkable: a context bound over a catalog no descriptor refers to must plan the same statement
/// to the same bytes as no context at all.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class RequestContextTests(SharedSidecar sidecar)
{
    private static readonly CorpusFixture Fixture = CorpusFixture.Create(barMinutes: 60);

    private const string Sql = "SELECT symbol, COUNT(*) AS n FROM main.bars GROUP BY symbol ORDER BY symbol";

    // ---------------------------------------------------------------- the client's own shape

    [Fact]
    public void An_empty_context_binds_nothing()
    {
        Assert.True(RequestContext.Empty.IsEmpty);
        Assert.Equal(64, RequestContext.DefaultFoldMaxRows);
        Assert.Equal(64, new RequestContext().FoldMaxRows);
    }

    [Fact]
    public void Two_equal_contexts_hash_the_same_and_any_change_moves_the_hash()
    {
        var baseline = Manager(org: 1);

        Assert.Equal(baseline.CanonicalHash, Manager(org: 1).CanonicalHash);
        Assert.Equal(32, baseline.CanonicalHash.Length);
        Assert.NotEqual(baseline.CanonicalHash, Manager(org: 2).CanonicalHash);
        Assert.NotEqual(
            baseline.CanonicalHash,
            new RequestContext
            {
                Scalars = baseline.Scalars,
                Lists = baseline.Lists,
                FoldMaxRows = 8,
            }.CanonicalHash);
    }

    /// <summary>
    /// Audit metadata is not context: it reaches the audit event and never a predicate, so two
    /// principals with the same grants and different reasons share a plan.
    /// </summary>
    [Fact]
    public void Purpose_and_actor_are_not_in_the_hash()
    {
        var baseline = Manager(org: 1);
        var annotated = new RequestContext
        {
            Scalars = baseline.Scalars,
            Lists = baseline.Lists,
            Purpose = "support ticket 4711",
            Actor = "agent-7",
        };

        Assert.Equal(baseline.CanonicalHash, annotated.CanonicalHash);
    }

    /// <summary>A dictionary's iteration order must never reach the wire or the hash.</summary>
    [Fact]
    public void The_binding_order_does_not_change_the_hash()
    {
        var forward = new RequestContext
        {
            Scalars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["user"] = 7,
                ["role"] = "manager",
            },
        };
        var backward = new RequestContext
        {
            Scalars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["role"] = "manager",
                ["user"] = 7,
            },
        };

        Assert.Equal(forward.CanonicalHash, backward.CanonicalHash);
    }

    [Fact]
    public void A_column_with_no_value_to_read_a_type_from_needs_a_declared_type()
    {
        var context = new RequestContext
        {
            Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
            {
                ["orgs"] = new ContextRelation { Columns = ["id"], Rows = [] },
            },
        };

        var ex = Assert.Throws<ArgumentException>(() => _ = context.CanonicalHash);
        Assert.Contains("holds no value to read a type from", ex.Message, StringComparison.Ordinal);

        var declared = new RequestContext
        {
            Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
            {
                ["orgs"] = new ContextRelation
                {
                    Columns = ["id"],
                    Rows = [],
                    ColumnTypes = [ChalkType.Int32()],
                },
            },
        };

        Assert.Equal(32, declared.CanonicalHash.Length);
    }

    // ---------------------------------------------------------------- partial binding (D232)

    /// <summary>
    /// A shape and an empty binding are the same rows and must not be the same context (V96): the
    /// entry says which it is, and the canonical hash — the plan cache's key — says so too.
    /// </summary>
    [Fact]
    public void A_shape_and_an_empty_binding_are_told_apart_by_the_entry_and_by_the_hash()
    {
        var empty = new RequestContext
        {
            Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
            {
                ["orgs"] = new ContextRelation
                {
                    Columns = ["id"],
                    Rows = [],
                    ColumnTypes = [ChalkType.Int32()],
                },
            },
        };
        var shape = empty.Shape(["orgs"]);

        Assert.False(empty.IsShape("orgs"));
        Assert.True(shape.IsShape("orgs"));
        Assert.Equal(["orgs"], shape.ShapeNames);
        Assert.Empty(empty.ShapeNames);
        Assert.NotEqual(empty.CanonicalHash, shape.CanonicalHash);
        Assert.Equal(shape.CanonicalHash, empty.Shape(["orgs"]).CanonicalHash);
    }

    /// <summary>
    /// A scalar's shape is its type stated without a value. A NULL is a value like any other, so the
    /// two spellings never collide.
    /// </summary>
    [Fact]
    public void A_scalar_is_a_shape_when_its_type_is_stated_and_its_value_is_not()
    {
        var context = new RequestContext
        {
            Scalars = new Dictionary<string, object?>(StringComparer.Ordinal) { ["user"] = 4711 },
            ScalarTypes = new Dictionary<string, ChalkType>(StringComparer.Ordinal)
            {
                ["mask_key"] = ChalkType.String(),
            },
        };

        Assert.True(context.IsShape("mask_key"));
        Assert.False(context.IsShape("user"));
        Assert.Equal(["mask_key"], context.ShapeNames);

        var withNull = context.With("scope", null);
        Assert.False(withNull.IsShape("scope"));
        Assert.Equal(["mask_key"], withNull.ShapeNames);
    }

    /// <summary>
    /// <c>Shape(names)</c> leaves the named half open and folds the rest, and <c>With</c> fills one
    /// back in. <c>Shape()</c> with no names is the whole context, which is D209's mode.
    /// </summary>
    [Fact]
    public void Shaping_some_names_leaves_the_rest_folded()
    {
        var whole = Manager(org: 1);
        var partial = whole.Shape(["user"]);

        Assert.Equal(["user"], partial.ShapeNames);
        Assert.False(partial.IsShape("manager_orgs"));
        Assert.True(whole.Shape().IsShape("manager_orgs"));
        Assert.Equal(whole.CanonicalHash, partial.With("user", 4711).CanonicalHash);

        var unknown = Assert.Throws<ArgumentException>(() => whole.Shape(["nobody"]));
        Assert.Contains("is not bound by this context", unknown.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- through the sidecar

    /// <summary>
    /// §0's zero-cost property at this level: a context over a catalog whose tables carry no
    /// entitlement is planned with and folds nothing, so the plan is byte-identical to the one the
    /// same statement gets with no context at all.
    /// </summary>
    [Fact]
    public async Task A_context_over_an_unentitled_catalog_changes_no_plan()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateEngineAsync();
        var without = await engine.PrepareAsync(Sql);
        var with = await engine.PrepareAsync(Sql, Manager(org: 1));

        Assert.Equal(without.PlanDigest, with.PlanDigest);
        Assert.Equal(
            Google.Protobuf.MessageExtensions.ToByteArray(without.Plan),
            Google.Protobuf.MessageExtensions.ToByteArray(with.Plan));
        Assert.Null(without.Context);
        Assert.NotNull(with.Context);
        Assert.Empty(with.RequiredContext);
    }

    /// <summary>And it executes to the same rows, because nothing about it is enforced.</summary>
    [Fact]
    public async Task A_statement_prepared_with_a_context_executes()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateEngineAsync();
        var prepared = await engine.PrepareAsync(Sql, Manager(org: 1));

        await using var execution = await engine.ExecuteAsync(prepared);
        var rows = 0;
        await foreach (var batch in execution.Batches)
        {
            rows += batch.Length;
            batch.Dispose();
        }

        Assert.True(rows > 0);
    }

    /// <summary>
    /// A NULL member of a list would make every non-matching row UNKNOWN and <c>NOT IN</c> never
    /// true, and Calcite would plan it as a join rather than an OR chain. The sidecar refuses it at
    /// bind, naming the row and the column.
    /// </summary>
    [Fact]
    public async Task A_null_member_of_a_list_is_refused_by_the_planner()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateEngineAsync();
        var context = new RequestContext
        {
            Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
            {
                ["orgs"] = new ContextRelation
                {
                    Columns = ["id"],
                    Rows = [[1], [null]],
                    ColumnTypes = [ChalkType.Int32(nullable: true)],
                },
            },
        };

        var ex = await Assert.ThrowsAsync<PlanningException>(() => engine.PrepareAsync(Sql, context).AsTask());

        Assert.Contains("has a NULL in row 1", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A name bound twice is the host's mistake and is named at bind, not at use.</summary>
    [Fact]
    public async Task A_name_bound_as_both_a_scalar_and_a_list_is_refused()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateEngineAsync();
        var context = new RequestContext
        {
            Scalars = new Dictionary<string, object?>(StringComparer.Ordinal) { ["orgs"] = 1 },
            Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
            {
                ["orgs"] = ContextRelation.Of("id", 1, 2),
            },
        };

        var ex = await Assert.ThrowsAsync<PlanningException>(() => engine.PrepareAsync(Sql, context).AsTask());

        Assert.Contains("bound twice", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// A principal of the shape the tenancy package will compile to: one scalar identity, one list
    /// of tenancies and one list of confined subject pairs.
    /// </summary>
    private static RequestContext Manager(int org) =>
        new()
        {
            Scalars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["user"] = 4711,
                ["mask_key"] = "a-key",
                ["global"] = false,
            },
            Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
            {
                ["manager_orgs"] = ContextRelation.Of("id", org),
                ["subject_pairs"] = ContextRelation.Of(["member_id", "org_id"], [4711, org]),
            },
        };

    private async Task<ChalkEngine> CreateEngineAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = Fixture.Sources,
            Planner = sidecar.CreatePlanner(),
        });
}
