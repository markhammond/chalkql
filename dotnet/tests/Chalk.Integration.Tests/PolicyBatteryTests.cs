using System.Security.Cryptography;
using System.Text;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Ir;
using Chalk.Sources.Conformance;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The entitlement conformance battery of <c>corpus/policy</c>, run
/// (<c>docs/design/16-entitlements.md</c> §7; <c>corpus/policy/README.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// Two hundred and twenty-five cases written as code — a statement, a principal, a catalog, and
/// the rows, labels, acknowledgements and refusals it must produce — executed against the engine and held to
/// what they say. It is the one corpus in the repository whose expectations were computed by hand
/// rather than recorded from a run, which is exactly what makes it worth running: a golden can only
/// tell you the engine changed, and this can tell you it is wrong.
/// </para>
/// <para>
/// Where a case and the engine disagree the case is <b>not</b> edited to match. Each disagreement is
/// judged and carried on the case as a <see cref="PolicyAdjudication"/> with both answers, and this
/// holds the set exactly: a case that starts disagreeing without one fails, and so does one that
/// carries one and now agrees.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class PolicyBatteryTests(
    SharedSidecar sidecar, SharedPostgres postgres, PolicyEngines engines)
{
    /// <summary>Every case this run executes, one theory row each.</summary>
    public static TheoryData<string> Runnable
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var battery in PolicyCases.All.Where(Runs))
            {
                data.Add(battery.Id);
            }

            return data;
        }
    }

    /// <summary>
    /// Whether this run executes the case. Everything but the one layer-B check, whose subject is
    /// the tenancy package's own and has no layer-A answer at all.
    /// </summary>
    private static bool Runs(PolicyBatteryCase battery) => !battery.LayerBOnly;

    [Theory]
    [MemberData(nameof(Runnable))]
    public async Task Case_agrees_with_the_engine_or_disagrees_exactly_as_adjudicated(string id)
    {
        var battery = PolicyCases.Find(id);
        if (battery.Source == PolicySourceProfile.AdoPostgres
            && postgres.Fixture.SkipReason is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        var findings = await RunAsync(battery);
        var disagreed = string.Join("\n  ", findings.Select(f => f.ToString()));

        if (battery.Disputed is { } adjudication)
        {
            Assert.True(
                findings.Count > 0,
                $"case {id} carries an adjudication and now agrees with the engine. The engine did "
                + $"'{adjudication.Engine}' where the case declares '{adjudication.Corpus}'. If the "
                + "engine has changed, drop the adjudication; the expectation itself is not edited "
                + "to match an engine.");
            return;
        }

        Assert.True(
            findings.Count == 0,
            $"case {id} disagrees with the engine and carries no adjudication:\n  {disagreed}\n"
            + "Judge it and put the judgement on the case (PolicyCases), never on the expectation.");
    }

    /// <summary>
    /// The battery's own shape, asserted without running anything: how many cases there are, how
    /// many this run executes, how many are refusals it expects, and how many disagreements are
    /// adjudicated. The numbers started as the ones the part-2d run reported and are here so that a
    /// case lost or quietly re-expected shows up as a number rather than as a silent gap.
    /// </summary>
    [Fact]
    public void The_battery_is_the_size_the_run_reported()
    {
        var all = PolicyCases.All;
        var run = all.Where(Runs).ToList();
        var disputed = run.Count(c => c.Disputed is not null);
        var refusals = run.Count(c => c.Expect.Refusal is not null && c.Disputed is null);

        // Six more than the F58 run: D261's six layer-A cases, 191–196, the TEST verdict and its
        // aggregate form written directly as `DisclosureRule`s. Four more since D266: 197–200, a
        // grant confined along two tenancy kinds at once, written directly as one membership over
        // a tuple of arity three. And four more since D265 clause (h): 201–204, group U, related
        // visibility written directly as an `Inherited` entry with its endpoint predicate.
        Assert.Equal(229, all.Count);
        Assert.Equal(228, run.Count);
        // Six fewer than the part-2d run reported: 079, 080, 089, 090, 093 and 094 were the five
        // F43 adjudications and the one §3.4 laxity, all of which rested on `orders.amount` being
        // NOT NULL where the statement could see it. It is nullable there from the F58 run onwards,
        // the converter no longer erases COUNT's argument, and the six cases say what the engine
        // now does — so their adjudications are dropped and the expectations hold as written.
        Assert.Equal(39, disputed);
        // D261's six are undisputed, and so are D266's four and D265 clause (h)'s four: the engine
        // answers what each of them declares.
        Assert.Equal(189, run.Count - disputed);
        Assert.Equal(28, refusals);

        // Every case has a statement, and every statement has a case.
        foreach (var battery in all)
        {
            Assert.False(
                PolicyStatements.Of(battery).Length == 0, $"case {battery.Id} has no statement.");
        }

        var named = all.Select(c => c.File).ToHashSet(StringComparer.Ordinal);
        var orphans = new DirectoryInfo(Path.Combine(PolicyStatements.Directory.FullName, "cases"))
            .GetFiles("*.sql")
            .Where(f => !named.Contains("cases/" + f.Name, StringComparer.Ordinal))
            .Select(f => f.Name)
            .ToList();
        Assert.True(orphans.Count == 0, $"statements no case names: {string.Join(", ", orphans)}");
        Assert.Equal(all.Count, all.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// <c>corpus/policy/README.md</c> §5's uncertainties: what the run makes of each one, against
    /// what <see cref="PolicyCases.Uncertainties"/> records. Computed from the adjudications rather
    /// than from a run, because that is what an adjudication is.
    /// </summary>
    [Fact]
    public void Every_uncertainty_is_answered_as_recorded()
    {
        foreach (var uncertainty in PolicyCases.Uncertainties)
        {
            var cases = uncertainty.Cases.Select(PolicyCases.Find).Where(Runs).ToList();
            var verdict = cases.Count == 0
                ? PolicyVerdict.NotRun
                : cases.All(c => c.Disputed is null)
                    ? PolicyVerdict.Agreed
                    : cases.All(c => c.Disputed is not null)
                        ? PolicyVerdict.Disputed
                        : PolicyVerdict.Mixed;

            Assert.True(
                verdict == uncertainty.Verdict,
                $"{uncertainty.Id} is recorded as {uncertainty.Verdict} and the battery now says "
                + $"{verdict}. Its cases are {string.Join(", ", uncertainty.Cases)}.");
        }
    }

    // ---------------------------------------------------------------- one case

    private async Task<IReadOnlyList<ConformanceFinding>> RunAsync(PolicyBatteryCase battery)
    {
        var context = PolicyPrincipals.All[battery.Principal];
        var sql = PolicyStatements.Of(battery);
        var kit = battery.ToBatteryCase(sql);

        // Registration itself is what a group-O case is about, so its failure is the observation
        // rather than an error.
        ChalkEngine engine;
        try
        {
            engine = await EngineAsync(battery);
        }
        catch (Exception refusal) when (refusal is not Xunit.Sdk.XunitException)
        {
            return PolicyBattery.Check(
                kit,
                new BatteryRun { Refusal = refusal.Message, RefusalKind = KindOf(refusal) });
        }

        try
        {
            var execute = battery.Binding == PolicyBinding.Execute;
            // A case may ask for a dialect of its own; conformance is per request (D34).
            var prepare = battery.Options.Lenient
                ? new PrepareOptions { Conformance = Chalk.Client.SqlConformance.Lenient }
                : null;
            var prepared = await engine
                .WithEntitlements(Options(battery.Options))
                .PrepareAsync(sql, execute ? context.Shape() : context, prepare);

            var rows = await RowsAsync(engine, prepared.Query, execute ? context : null);
            var run = new BatteryRun
            {
                Columns = [.. prepared.Columns.Select(c => c.Name)],
                Disclosures = [.. prepared.Columns.Select(c => c.Disclosure.ToString())],
                Rows = rows,
                Report = Report(prepared),
                RemoteQueries = RemoteQueries(prepared.Plan),
                PlanFacts = PlanFacts(kit, prepared),
            };

            var findings = new List<ConformanceFinding>(
                PolicyBattery.Check(Substituted(kit, context), run));
            findings.AddRange(Claims(battery, prepared, rows));
            return findings;
        }
        catch (Exception refusal) when (refusal is not Xunit.Sdk.XunitException)
        {
            return PolicyBattery.Check(
                kit,
                new BatteryRun { Refusal = refusal.Message, RefusalKind = KindOf(refusal) });
        }
    }

    /// <summary>
    /// The engine for that case's catalog and source. One per distinct pair, shared across the
    /// theory's rows: registering a catalog and loading a database are the expensive halves, and
    /// 221 cases name fewer than thirty pairs between them.
    /// </summary>
    private Task<ChalkEngine> EngineAsync(PolicyBatteryCase battery)
    {
        var key = battery.Source.Name() + "|" + string.Join(
            ",",
            battery.Catalog.ByTable().OrderBy(e => e.Key, StringComparer.Ordinal)
                .Select(e => $"{e.Key}={e.Value}"));
        return engines.OfAsync(key, () => Sources(key, battery), sidecar.CreatePlanner);
    }

    /// <summary>The fixture this case reads: in-process collections, or a real database (F41).</summary>
    private PolicySources Sources(string key, PolicyBatteryCase battery) => battery.Source switch
    {
        PolicySourceProfile.Poco =>
            new PolicySources([PolicyFixture.Create(battery.Catalog).Source], null),
        PolicySourceProfile.AdoPostgres => Owned(
            PolicyAdoFixture.CreatePostgres(
                postgres.Fixture.CreateDatabase(
                    "chalk_policy_" + Math.Abs(key.GetHashCode(StringComparison.Ordinal))),
                s => new Npgsql.NpgsqlConnection(s),
                battery.Catalog,
                battery.Source)),
        _ => Owned(PolicyAdoFixture.CreateDuckDb(battery.Catalog, battery.Source)),
    };

    private static PolicySources Owned(PolicyAdoFixture fixture) =>
        new(fixture.Sources, fixture);

    private static string KindOf(Exception refusal) => refusal switch
    {
        EntitlementException => "POLICY",
        Chalk.Catalog.CatalogValidationException => "INVALID_CATALOG",
        // The sidecar's own refusals travel as a PlanningException carrying the planner's kind, so
        // a catalog it refused at registration is INVALID_CATALOG and not a validation error.
        PlanningException { Kind: Chalk.Client.Rpc.PlanErrorKind.InvalidCatalog } => "INVALID_CATALOG",
        PlanningException { Kind: Chalk.Client.Rpc.PlanErrorKind.Policy } => "POLICY",
        PlanningException => "VALIDATION",
        _ => refusal.GetType().Name.Replace("Exception", "", StringComparison.Ordinal)
            .ToUpperInvariant(),
    };

    // ---------------------------------------------------------------- what the case asked for

    private static EntitlementsOptions Options(PolicyCaseOptions options) => new()
    {
        StarPolicy = options.StarPolicy,
        Redaction = options.Redaction,
        PlaceholderPolicy = options.PlaceholderPolicy,
        RefuseWhenNoVisibleRows = options.RefuseWhenNoVisibleRows,
        IncludeDisclosureColumns = options.IncludeDisclosureColumns,
        DisclosureColumnSuffix = options.DisclosureColumnSuffix,
        DefaultMinGroupSize = options.DefaultMinGroupSize,
    };

    /// <summary>
    /// The case with every <c>"!fp:&lt;raw&gt;"</c> replaced by the token that principal's mask key
    /// really produces (README §3 step 5). The value is HMAC-SHA-256 and is not hand-computable;
    /// what the case asserts is the token's equality behaviour, so the kit computes it the same way
    /// the kernel does and compares the results.
    /// </summary>
    private static BatteryCase Substituted(BatteryCase battery, RequestContext context)
    {
        if (battery.Expect.Rows is not { } rows
            || !rows.Any(r => r.Any(v => v is string s && s.StartsWith("!fp:", StringComparison.Ordinal))))
        {
            return battery;
        }

        var key = context.Scalars.TryGetValue("mask_key", out var bound) ? bound?.ToString() ?? "" : "";
        var substituted = rows
            .Select(row => (IReadOnlyList<object?>)[.. row.Select(v =>
                v is string text && text.StartsWith("!fp:", StringComparison.Ordinal)
                    ? Fingerprint(text[4..], key)
                    : v)])
            .ToList();

        return new BatteryCase
        {
            Id = battery.Id,
            Group = battery.Group,
            File = battery.File,
            Sql = battery.Sql,
            Principal = battery.Principal,
            Binding = battery.Binding,
            Source = battery.Source,
            Phase = battery.Phase,
            Catalog = battery.Catalog,
            Options = battery.Options,
            Compare = battery.Compare,
            Notes = battery.Notes,
            Expect = new BatteryExpectation
            {
                Rows = substituted,
                Columns = battery.Expect.Columns,
                Report = battery.Expect.Report,
                Error = battery.Expect.Error,
                Asserts = battery.Expect.Asserts,
            },
        };
    }

    /// <summary>The kernel's own answer: HMAC-SHA-256, the first sixteen bytes as lower-case hex.</summary>
    private static string Fingerprint(string value, string key) =>
        Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value))
                .AsSpan(0, 16));

    // ---------------------------------------------------------------- what the run produced

    private static async Task<IReadOnlyList<IReadOnlyList<object?>>> RowsAsync(
        ChalkEngine engine, PreparedQuery prepared, RequestContext? executeWith)
    {
        var rows = new List<IReadOnlyList<object?>>();
        await using var execution = executeWith is null
            ? await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>?)null)
            : await engine.ExecuteAsync(prepared, executeWith);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                rows.AddRange(BatchReader.ToRows(batch));
            }
        }

        return rows;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Report(
        EntitledQuery prepared)
    {
        var report = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var table in prepared.Entitlements.Tables)
        {
            report[table.Table] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["visibility"] = table.Visibility.ToString().ToUpperInvariant(),
                ["row_predicate_pushed"] = table.RowPredicatePushed ? "true" : "false",
                ["contradiction"] = table.Contradiction ? "true" : "false",
                ["contradiction_column"] = table.ContradictionColumn,
            };
        }

        return report;
    }

    private static IReadOnlyList<string> RemoteQueries(Plan plan)
    {
        var texts = new List<string>();
        foreach (var rel in PlanWalker.Rels(plan))
        {
            if (rel.KindCase == Rel.KindOneofCase.RemoteQuery
                && rel.RemoteQuery.QueryText.Length > 0)
            {
                texts.Add(rel.RemoteQuery.QueryText);
            }
        }

        return texts;
    }

    /// <summary>
    /// The facts the claims of <see cref="PolicyClaims"/> are about, read off the plan and the
    /// labels. A verb this does not compute is left out, and the dispatcher reports it as unanswered
    /// rather than as agreement.
    /// </summary>
    private static IReadOnlyDictionary<string, string> PlanFacts(
        BatteryCase battery, EntitledQuery prepared)
    {
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var verb in battery.Expect.Asserts.Keys)
        {
            switch (verb)
            {
                case "plan_has_no_aggregate_in_project":
                    facts[verb] = PlanWalker.Rels(prepared.Plan).Any(HasAggregateInProject)
                        ? "false"
                        : "true";
                    break;

                case "labels_equal_arrow_metadata":
                    facts[verb] = LabelsEqualMetadata(prepared) ? "true" : "false";
                    break;

                case "labels_agree_with_values":
                    // The claims harness is what judges this; saying so here keeps the dispatcher
                    // from reporting the verb as unanswered twice.
                    facts[verb] = "true";
                    break;

                case "join_nulls_are_not_placeholders":
                    facts[verb] = string.Join(
                        ", ",
                        battery.Expect.Asserts[verb].Where(name =>
                            prepared.Columns.FirstOrDefault(c =>
                                string.Equals(c.Name, name, StringComparison.Ordinal))
                            is { Disclosure: not ReportedDisclosure.Redacted }));
                    break;

                case "remote_query_no_duplicate_output_names":
                    facts[verb] = NoDuplicateOutputNames(prepared) ? "true" : "false";
                    break;

                case "remote_query_projection_is_catalog_order":
                {
                    var table = battery.Expect.Asserts[verb][0];
                    facts[verb] = InCatalogOrder(prepared.Plan, table)
                        ? table
                        : "not in catalog order: "
                            + string.Join(" / ", RemoteQueries(prepared.Plan));
                    break;
                }

                case "read_projection":
                case "read_disclosures":
                    // Only the tables the case names: a claim about one table's read is not a claim
                    // that the statement reads no other.
                    facts[verb] = Reads(
                        prepared.Plan,
                        verb,
                        new HashSet<string>(
                            battery.Expect.Asserts[verb].Select(
                                e => e[..e.IndexOf('=', StringComparison.Ordinal)]),
                            StringComparer.Ordinal));
                    break;

                default:
                    break;
            }
        }

        return facts;
    }

    /// <summary>
    /// Whether a projection holds an aggregate call. Chalk's IR has no aggregate <em>expression</em>
    /// at all — an aggregate is a call of an <c>Aggregate</c> node — so a plan that broke Calcite's
    /// invariant would reach <c>RelToIr</c> as an unmappable call and never arrive here. The verb is
    /// answered "true" by construction, and that is what it asserts.
    /// </summary>
    private static bool HasAggregateInProject(Rel rel) => false;

    /// <summary>
    /// Whether the generated text either names that table's columns in the order the read's own
    /// projection gives, or is a <c>SELECT *</c> whose promise — the source's column order equals
    /// the registered catalog's — holds. The fixture creates the table in the catalog's own order
    /// and the source discovers it back, so the promise is true by construction here; what is
    /// checked is that the plan and the text agree about which columns and in what order.
    /// </summary>
    private static bool InCatalogOrder(Plan plan, string table)
    {
        foreach (var rel in PlanWalker.Rels(plan))
        {
            if (rel.KindCase != Rel.KindOneofCase.RemoteQuery
                || rel.RemoteQuery.PushedPlan is not { } pushed
                || !PlanWalker.Rels(pushed).Any(r => r.KindCase == Rel.KindOneofCase.Read
                    && r.Read.Table.Table == table))
            {
                continue;
            }

            if (Ordinals(rel, table) is not { } ordinals)
            {
                return false;
            }

            if (!ordinals.SequenceEqual(ordinals.Order()))
            {
                return false;
            }

            var columns = PolicyColumns.Layouts[table];
            var text = rel.RemoteQuery.QueryText;
            if (text.Contains("SELECT *", StringComparison.OrdinalIgnoreCase))
            {
                return ordinals.Count == columns.Count;
            }

            var at = -1;
            foreach (var ordinal in ordinals)
            {
                var found = text.IndexOf(columns[ordinal], at + 1, StringComparison.OrdinalIgnoreCase);
                if (found <= at)
                {
                    return false;
                }

                at = found;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// No two columns of a generated query's outermost select list share a name after case folding.
    /// True by construction where nothing was generated, which is every POCO case.
    /// </summary>
    private static bool NoDuplicateOutputNames(EntitledQuery prepared)
    {
        foreach (var rel in PlanWalker.Rels(prepared.Plan))
        {
            if (rel.KindCase != Rel.KindOneofCase.RemoteQuery)
            {
                continue;
            }

            var names = rel.RowType.Fields.Select(f => f.Name).ToList();
            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
            {
                return false;
            }
        }

        return true;
    }

    private static bool LabelsEqualMetadata(EntitledQuery prepared)
    {
        var fields = prepared.OutputSchema.FieldsList;
        for (var i = 0; i < fields.Count && i < prepared.Columns.Count; i++)
        {
            var declared = MetadataName(prepared.Columns[i].Disclosure);
            var carried = fields[i].Metadata is { } metadata
                && metadata.TryGetValue("chalk.disclosure", out var value)
                ? value
                : declared;
            if (!string.Equals(carried, declared, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The upper-case name the Arrow metadata carries for a label (D218 amended).</summary>
    private static string MetadataName(ReportedDisclosure disclosure) => disclosure switch
    {
        ReportedDisclosure.Masked => "MASKED",
        ReportedDisclosure.Redacted => "REDACTED",
        ReportedDisclosure.PerRow => "PER_ROW",
        ReportedDisclosure.Aggregate => "AGGREGATE",
        _ => "FULL",
    };

    /// <summary>
    /// The reads' projections or per-column outcomes, as <c>table=[…]</c> entries — one per distinct
    /// table, sorted, so that a statement reading a table twice states the claim once and two reads
    /// that disagree show up as two entries.
    /// </summary>
    private static string Reads(Plan plan, string verb, IReadOnlySet<string> tables)
    {
        // The executed walk, and `Claimed` descends into a pushed plan itself (F92): what it yields
        // for a pushed read is the *RemoteQuery's* output mapped back to the table's ordinals, and
        // the read inside carries the unpruned list. Walking the pushed nodes here as well would
        // state the claim twice, once each way.
        var entries = new List<string>();
        foreach (var rel in PlanWalker.ExecutedRels(plan))
        {
            foreach (var (table, entry) in Claimed(rel, verb))
            {
                if (tables.Contains(table) && !entries.Contains(entry, StringComparer.Ordinal))
                {
                    entries.Add(entry);
                }
            }
        }

        entries.Sort(StringComparer.Ordinal);
        return string.Join(", ", entries);
    }

    /// <summary>
    /// What one node says about a table's read, as <c>table=[…]</c>.
    /// </summary>
    /// <remarks>
    /// Over a local source the read is the node the executor walks, and its own projection is the
    /// list it reads the result by. Over a remote one it is not: the read sits inside the
    /// <c>RemoteQuery</c> the planner pushed, unpruned, and what the executor reads by position is
    /// the pushed query's own output row. So the projection claim is answered from the
    /// <c>RemoteQuery</c>'s row type, mapped back to the table's ordinals — which is the list the
    /// claim is about — and the per-column outcomes from the read inside it, which is where they
    /// are recorded.
    /// </remarks>
    private static IEnumerable<(string Table, string Entry)> Claimed(Rel rel, string verb)
    {
        if (rel.KindCase == Rel.KindOneofCase.Read)
        {
            var table = rel.Read.Table.Table;
            yield return (
                table,
                verb == "read_projection"
                    ? $"{table}=[{string.Join(", ", rel.Read.Projection)}]"
                    : $"{table}=[{string.Join(", ", Outcomes(rel.Read))}]");
            yield break;
        }

        if (rel.KindCase != Rel.KindOneofCase.RemoteQuery || rel.RemoteQuery.PushedPlan is not { } pushed)
        {
            yield break;
        }

        foreach (var inner in PlanWalker.Rels(pushed))
        {
            if (inner.KindCase != Rel.KindOneofCase.Read)
            {
                continue;
            }

            var table = inner.Read.Table.Table;
            if (verb != "read_projection")
            {
                yield return (table, $"{table}=[{string.Join(", ", Outcomes(inner.Read))}]");
                continue;
            }

            if (Ordinals(rel, table) is { } ordinals)
            {
                yield return (table, $"{table}=[{string.Join(", ", ordinals)}]");
            }
        }
    }

    /// <summary>
    /// The pushed query's output columns as ordinals of <paramref name="table"/>, or null when a
    /// name is not one of that table's — a pushed join, where the claim is not about one read.
    /// </summary>
    private static IReadOnlyList<int>? Ordinals(Rel remote, string table)
    {
        if (!PolicyColumns.Layouts.TryGetValue(table, out var columns))
        {
            return null;
        }

        var ordinals = new List<int>();
        foreach (var field in remote.RowType.Fields)
        {
            var at = columns.ToList().FindIndex(
                c => string.Equals(c, field.Name, StringComparison.OrdinalIgnoreCase));
            if (at < 0)
            {
                return null;
            }

            ordinals.Add(at);
        }

        return ordinals;
    }

    private static IReadOnlyList<string> Outcomes(Read read)
    {
        var outcomes = new SortedDictionary<uint, string>();
        foreach (var disclosure in read.Disclosures)
        {
            outcomes[disclosure.Column] = disclosure.Outcome switch
            {
                DisclosureOutcome.Masked => "MASKED",
                DisclosureOutcome.Aggregate => "AGGREGATE",
                DisclosureOutcome.Redacted => "REDACTED",
                DisclosureOutcome.PerRow => "PER_ROW",
                _ => "FULL",
            };
        }

        return [.. outcomes.Values];
    }

    /// <summary>The claims harness of §7, over what this case actually produced.</summary>
    private static IReadOnlyList<ConformanceFinding> Claims(
        PolicyBatteryCase battery, EntitledQuery prepared, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        if (battery.Expect.Rows is null)
        {
            return [];
        }

        var columns = new List<PolicyColumn>(prepared.Columns.Count);
        var fields = prepared.OutputSchema.FieldsList;
        for (var i = 0; i < prepared.Columns.Count; i++)
        {
            columns.Add(new PolicyColumn
            {
                Name = prepared.Columns[i].Name,
                Reported = prepared.Columns[i].Disclosure.ToString(),
                ArrowMetadata = i < fields.Count && fields[i].Metadata is { } metadata
                    && metadata.TryGetValue("chalk.disclosure", out var value)
                    ? value
                    : null,
            });
        }

        var tables = new List<PolicyTable>(prepared.Entitlements.Tables.Count);
        foreach (var table in prepared.Entitlements.Tables)
        {
            tables.Add(new PolicyTable
            {
                Table = table.Table,
                Visibility = table.Visibility.ToString(),
                RowPredicatePushed = table.RowPredicatePushed,
            });
        }

        return PolicyConformance.Check(new PolicyCase
        {
            Name = $"case {battery.Id}",
            Principal = battery.Principal,
            Columns = columns,
            Rows = rows,
            Tables = tables,
            // The visibility claim is about a statement that reads the table and nothing else: a
            // global aggregate over no visible rows is one row holding zero.
            // The executed walk (F92): one leaf as the executor sees it, whether that leaf is a
            // read or the remote query a read was pushed into.
            SingleTableScan = PlanWalker.ExecutedRels(prepared.Plan).Count(
                r => r.KindCase is Rel.KindOneofCase.Read or Rel.KindOneofCase.RemoteQuery) == 1
                && !PlanWalker.ExecutedRels(prepared.Plan).Any(r => r.KindCase
                    is Rel.KindOneofCase.Aggregate or Rel.KindOneofCase.HashAggregate
                    or Rel.KindOneofCase.StreamAggregate),
        });
    }
}

/// <summary>
/// One engine per distinct catalog the battery registers, for the whole run
/// (<c>corpus/policy/README.md</c> §3 step 2).
/// </summary>
/// <remarks>
/// Registering a catalog is the expensive half of a case — it starts a plan session against the
/// sidecar — and 211 cases name fewer than thirty catalogs between them, so they are built once and
/// shared. A collection fixture rather than a static, so they are disposed when the collection ends
/// rather than left to the process.
/// </remarks>
public sealed class PolicyEngines : IAsyncLifetime
{
    private readonly Dictionary<string, Task<ChalkEngine>> _engines = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _fixtures = [];

    /// <summary>The engine for that catalog and source, built on first use.</summary>
    public Task<ChalkEngine> OfAsync(
        string key, Func<PolicySources> sources, Func<IQueryPlanner> planner)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(planner);

        lock (_engines)
        {
            if (!_engines.TryGetValue(key, out var engine))
            {
                var fixture = sources();
                if (fixture.Owned is { } owned)
                {
                    _fixtures.Add(owned);
                }

                engine = ChalkEngine.CreateAsync(new ChalkEngineOptions
                {
                    ContextId = PolicyFixture.ContextId,
                    Sources = fixture.Sources,
                    Planner = planner(),
                    Functions = PolicyFixture.RegisterFunctions,
                }).AsTask();
                _engines[key] = engine;
            }

            return engine;
        }
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        List<Task<ChalkEngine>> engines;
        List<IDisposable> fixtures;
        lock (_engines)
        {
            engines = [.. _engines.Values];
            fixtures = [.. _fixtures];
            _engines.Clear();
            _fixtures.Clear();
        }

        foreach (var engine in engines)
        {
            try
            {
                await (await engine).DisposeAsync();
            }
            catch (Exception)
            {
                // A catalog a case expected to be refused never became an engine; nothing to close.
            }
        }

        foreach (var fixture in fixtures)
        {
            fixture.Dispose();
        }
    }
}

/// <summary>
/// The sources one engine reads, and the fixture that owns them when there is one: a database this
/// run created and must take down again (F41).
/// </summary>
public sealed record PolicySources(
    IReadOnlyList<Chalk.Sources.ISourceRuntime> Sources, IDisposable? Owned);
