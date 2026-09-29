using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Integration.Tests;

/// <summary>
/// A subject's path confined by a kind the table holds itself (F159) — end to end, over a sidecar.
/// </summary>
/// <remarks>
/// <para>
/// Two officers: P-7, whose personnel record is SECRET, and P-9, whose record is TOP SECRET. An
/// availability entry is about one of them, reached along a path through personnel, and carries a
/// classification of its own: P-7 has a SECRET entry (E-1) and a TOP SECRET one (E-2), P-9 a TOP
/// SECRET one (E-3). A training record is about one of them the same way and carries none.
/// </para>
/// <para>
/// A self grant confined to a classification reads the entries <em>of</em> that classification — the
/// entry's own, which the table declares — and the training records of an officer whose record is of
/// it, since that table declares none and the endpoint's value is all there is. Before, the
/// endpoint's value confined both: P-7 within SECRET read the TOP SECRET entry, and within TOP SECRET
/// read nothing.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class SubjectPathConfinementTests(SharedSidecar sidecar)
{
    private sealed record Person(string PersonId, string Classification);

    private sealed record Entry(string EntryId, string PersonId, string Classification);

    private sealed record Course(string CourseId, string PersonId);

    private static readonly Person[] Personnel = [new("P-7", "SECRET"), new("P-9", "TOP SECRET")];

    private static readonly Entry[] Availability =
    [
        new("E-1", "P-7", "SECRET"),
        new("E-2", "P-7", "TOP SECRET"),
        new("E-3", "P-9", "TOP SECRET"),
    ];

    private static readonly Course[] Training = [new("C-1", "P-7"), new("C-2", "P-9")];

    public static TheoryData<string, string, bool, string[], string[]> Answers() => new()
    {
        { "P-7", "SECRET", false, ["E-1"], ["C-1"] },
        { "P-7", "SECRET", true, ["E-1"], ["C-1"] },
        { "P-7", "TOP SECRET", false, ["E-2"], [] },
        { "P-7", "TOP SECRET", true, ["E-2"], [] },
        { "P-9", "TOP SECRET", false, ["E-3"], ["C-2"] },
        { "P-7", "anywhere", false, ["E-1", "E-2"], ["C-1"] },
    };

    [Theory]
    [MemberData(nameof(Answers))]
    public async Task A_self_grant_reads_the_entries_of_its_own_classification(
        string person, string within, bool asShape, string[] entries, string[] courses)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var catalog = new CatalogContext { ContextId = "subject-path", Schemas = [Source(null).DescribeSchema()] };
        var policy = TenancyPolicy.Declare(catalog);
        var classification = policy.Tenancy("classification");
        var officer = policy.Subject("person", within: [classification]);
        var self = policy.Role("self");
        var source = policy.Source("aus");
        var personnel = source.Table("personnel");
        var availability = source.Table("availability");
        var training = source.Table("training");
        personnel.Tenancy(t => t
            .Direct(officer, personnel.Column("PersonId"))
            .Direct(classification, personnel.Column("Classification")));
        availability.Tenancy(t => t
            .Direct(classification, availability.Column("Classification"))
            .Inherited(officer).Through(personnel));
        training.Tenancy(t => t.Inherited(officer).Through(personnel));
        var entitlements = policy.Compile(catalog);

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "subject-path",
            Sources = [Source(entitlements)],
            Planner = sidecar.CreatePlanner(),
        });

        var context = entitlements.Bind(new TenancyPrincipal
        {
            User = person,
            Grants =
            [
                Grant.ForSubject(
                    officer, person, self, within: within == "anywhere" ? Tenancy.Anywhere : within),
            ],
        });
        var entitled = engine.WithEntitlements();

        async Task<string[]> Read(string sql) => asShape
            ? await ConfinementDirectionTests.RowsAsync(engine, await entitled.PrepareAsync(sql, context.Shape()), context)
            : await ConfinementDirectionTests.RowsAsync(engine, await entitled.PrepareAsync(sql, context));

        Assert.Equal(entries, await Read("SELECT EntryId FROM aus.availability ORDER BY EntryId"));
        Assert.Equal(courses, await Read("SELECT CourseId FROM aus.training ORDER BY CourseId"));
    }

    private static PocoSource Source(TenancyEntitlements? entitlements)
    {
        TableEntitlementDescriptor? Of(string table) => entitlements?.For("aus", table);

        return new PocoSourceBuilder("aus", "aus")
            .AddTable("personnel", Personnel, t =>
            {
                t.UniqueKey(x => x.PersonId);
                if (Of("personnel") is { } d) t.Entitlement(d);
            })
            .AddTable("availability", Availability, t =>
            {
                t.UniqueKey(x => x.EntryId)
                    .ForeignKey<Person>(x => x.PersonId, to: "personnel", x => x.PersonId, verify: true);
                if (Of("availability") is { } d) t.Entitlement(d);
            })
            .AddTable("training", Training, t =>
            {
                t.UniqueKey(x => x.CourseId)
                    .ForeignKey<Person>(x => x.PersonId, to: "personnel", x => x.PersonId, verify: true);
                if (Of("training") is { } d) t.Entitlement(d);
            })
            .Build();
    }
}
