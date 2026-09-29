using Chalk.Catalog;
using Chalk.Sources.Poco;

namespace Chalk.Entitlements.Tenancy.Tests;

/// <summary>
/// One source of small tables for the direction of a conjunction across a path (F158) and the row a
/// confining kind is read off (F159): children that hold a classification of their own and inherit
/// a releasability from a parent, which may hold a classification of its own too.
/// </summary>
internal static class DirectionFixture
{
    internal const string Source = "s";

    internal sealed record Parent(int Id, string Releasability, string ParentClassification);

    internal sealed record Grade(int Id, string Classification);

    internal sealed record Child(int Id, int ParentId, string Classification);

    internal sealed record Twin(int Id, int ParentId, int GradeId);

    internal sealed record Report(int Id, string Classification, string Releasability);

    /// <summary>The catalog: every table, whichever a policy chooses to speak about.</summary>
    internal static CatalogContext Catalog { get; } = new()
    {
        ContextId = "direction",
        Schemas =
        [
            new PocoSourceBuilder(Source, Source)
                .AddTable("parents", Array.Empty<Parent>(), t => t.UniqueKey(x => x.Id))
                .AddTable("grades", Array.Empty<Grade>(), t => t.UniqueKey(x => x.Id))
                .AddTable("children", Array.Empty<Child>(), t => t
                    .UniqueKey(x => x.Id)
                    .ForeignKey<Parent>(x => x.ParentId, to: "parents", x => x.Id, verify: false))
                .AddTable("twins", Array.Empty<Twin>(), t => t
                    .UniqueKey(x => x.Id)
                    .ForeignKey<Parent>(x => x.ParentId, to: "parents", x => x.Id, verify: false)
                    .ForeignKey<Grade>(x => x.GradeId, to: "grades", x => x.Id, verify: false))
                .AddTable("reports", Array.Empty<Report>(), t => t.UniqueKey(x => x.Id))
                .Build()
                .DescribeSchema(),
        ],
    };
}
