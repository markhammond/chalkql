using System.Data.Common;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Client.Rpc;
using Chalk.Ir;
using Chalk.Sources.Ado;
using Chalk.TestKit;
using Microsoft.Data.Sqlite;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Integration.Tests;

/// <summary>
/// D256: a source's dialect the sidecar does not recognise is refused at <c>RegisterCatalog</c>
/// (the existing <see cref="PlanErrorKind.InvalidCatalog"/> kind) — <c>ansi</c> or a
/// <c>DatabaseProduct</c> name (aliases included) still register; empty is untouched.
/// <see cref="ChalkEngine.CreateAsync"/> checks first, against <see cref="PlannerInfo.Dialects"/>
/// (D248), so a host's mistyped name is reported locally, naming the source, before the round trip
/// — but the sidecar remains the authority, which the second test here reaches directly.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class DialectRegistrationTests(SharedSidecar sidecar)
{
    [Fact]
    public async Task An_unrecognised_dialect_on_an_ado_source_is_refused_locally_naming_the_source_and_the_name()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var connectionString = $"Data Source=chalk-d256-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        DbConnection Connect() => new SqliteConnection(connectionString);
        using var keepAlive = Connect();
        keepAlive.Open();
        using (var command = keepAlive.CreateCommand())
        {
            command.CommandText = "CREATE TABLE widget (id INTEGER NOT NULL)";
            command.ExecuteNonQuery();
        }

        var badProfile = new DialectProfileDescriptor
        {
            Dialect = "not-a-real-dialect",
            Quoting = IdentifierQuoting.DoubleQuote,
        };
        var source = new AdoSourceBuilder("bad256", Connect, "main")
            .Dialect(badProfile)
            .Capabilities(AdoCapabilities.For(badProfile))
            .DiscoverTables()
            .Build();

        var error = await Assert.ThrowsAsync<CatalogValidationException>(
            () => ChalkEngine.CreateAsync(new ChalkEngineOptions
            {
                ContextId = "d256-client-check",
                Sources = [source],
                Planner = sidecar.CreatePlanner(),
            }).AsTask());

        Assert.Contains("bad256", error.Message, StringComparison.Ordinal);
        Assert.Contains("not-a-real-dialect", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The client-side check lives in <see cref="ChalkEngine.CreateAsync"/>, so a caller that
    /// registers a catalog directly — the only way a host or a test reaches the sidecar without it
    /// — meets the sidecar's own refusal instead, with the same error kind D256 names.
    /// </summary>
    [Fact]
    public async Task The_sidecars_own_refusal_is_reached_when_the_client_check_is_bypassed()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var catalog = new CatalogContext
        {
            ContextId = "d256-direct",
            Epoch = 1,
            Schemas =
            [
                new SchemaDescriptor
                {
                    SourceId = "bad256b",
                    Name = "bad256b",
                    Kind = SourceKind.Remote,
                    // A real AdoSource keeps these two in step (AdoSource sets Dialect = Profile.Dialect);
                    // both are set by hand here for the same reason.
                    Dialect = "not-a-real-dialect",
                    DialectProfile = new DialectProfileDescriptor
                    {
                        Dialect = "not-a-real-dialect",
                        Quoting = IdentifierQuoting.DoubleQuote,
                    },
                    Tables =
                    [
                        new TableDescriptor
                        {
                            Name = "widget",
                            RowCount = -1,
                            Columns = [new ColumnDescriptor { Name = "id", Type = ChalkType.Int64() }],
                        },
                    ],
                },
            ],
        };

        await using var planner = sidecar.CreatePlanner();
        var error = await Assert.ThrowsAsync<PlanningException>(
            () => planner.RegisterCatalogAsync(catalog).AsTask());

        Assert.Equal(PlanErrorKind.InvalidCatalog, error.Kind);
        Assert.Contains("not-a-real-dialect", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A recorded planner reports no dialects at all (D250: nothing here ever asked a sidecar
    /// anything), so the client-side check has nothing to compare against and does not fire —
    /// engine creation is not refused locally for a name that would have been fine, or even for one
    /// that would not, because a recorded planner never registers anything to refuse either.
    /// </summary>
    [Fact]
    public async Task A_recorded_planner_reports_no_dialects_so_the_client_check_does_not_fire()
    {
        var connectionString = $"Data Source=chalk-d256-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        DbConnection Connect() => new SqliteConnection(connectionString);
        using var keepAlive = Connect();
        keepAlive.Open();
        using (var command = keepAlive.CreateCommand())
        {
            command.CommandText = "CREATE TABLE widget (id INTEGER NOT NULL)";
            command.ExecuteNonQuery();
        }

        var badProfile = new DialectProfileDescriptor
        {
            Dialect = "not-a-real-dialect",
            Quoting = IdentifierQuoting.DoubleQuote,
        };
        var source = new AdoSourceBuilder("bad256", Connect, "main")
            .Dialect(badProfile)
            .Capabilities(AdoCapabilities.For(badProfile))
            .DiscoverTables()
            .Build();

        await using var recorded = new RecordedPlanner(RepoLayout.Plans.FullName);
        var info = await recorded.GetInfoAsync();
        Assert.Empty(info.Dialects);

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "d256-recorded",
            Sources = [source],
            Planner = recorded,
        });

        Assert.Equal("d256-recorded", engine.Catalog.ContextId);
    }
}
