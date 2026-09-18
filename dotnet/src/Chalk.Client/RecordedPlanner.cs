using System.Security.Cryptography;
using System.Text;
using Chalk.Ir;
using Google.Protobuf;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Client;

/// <summary>
/// Serves plans recorded by <see cref="RecordingPlanner"/> from a directory, so executor tests and
/// the corpus run without a sidecar (invariant I3). Missing plans fail with the command that records
/// them rather than by silently planning something else.
/// </summary>
public sealed class RecordedPlanner : IQueryPlanner
{
    private readonly DirectoryInfo _directory;
    private CatalogContext? _catalog;

    public RecordedPlanner(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = new DirectoryInfo(directory);
    }

    /// <summary>Where the <c>.binpb</c> files live.</summary>
    public string Directory => _directory.FullName;

    /// <summary>
    /// False (D271 (d)): this transport holds one catalog and no history, so there is no base a
    /// delta could be applied to. The engine registers whole against it.
    /// </summary>
    public bool AcceptsCatalogDeltas => false;

    public ValueTask<PlannerInfo> GetInfoAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(new PlannerInfo
        {
            MinIrVersion = IrVersion.Minimum,
            MaxIrVersion = IrVersion.Current,
            PlannerVersion = "recorded",
            CalciteVersion = "recorded",
            // Recorded plans came from one planner configuration; the digest of the plan itself is
            // what a cache would key on, so a constant here is honest rather than misleading.
            PlannerConfigHash = 0,
            // A directory of recorded plans carries no capability probe to replay (D248, D250): no
            // sidecar was asked what it accepts, so there is no dialect, conformance or library list
            // to be honest about beyond empty — the same reasoning as the fields above.
            Dialects = [],
            Conformances = [],
            Libraries = [],
        });

    public ValueTask RegisterCatalogAsync(
        CatalogRegistration registration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        // Always the whole catalog: a directory of recorded plans is not a registry with versions to
        // apply a delta to, which is what AcceptsCatalogDeltas says, so the engine never sends one.
        _catalog = registration.Catalog;
        return ValueTask.CompletedTask;
    }

    public ValueTask<PlanResult> PlanAsync(PlanRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var key = KeyFor(
            request.Sql,
            request.Options.Pushdown,
            request.Options.Conformance,
            request.Options.Libraries,
            request.ContextId,
            request.Options.DisabledCapabilities,
            request.Planning);
        var file = new FileInfo(Path.Combine(_directory.FullName, key + ".binpb"));
        if (!file.Exists)
        {
            throw new RecordedPlanMissingException(request.Sql, request.Options.Pushdown, key, Directory);
        }

        var plan = Plan.Parser.ParseFrom(File.ReadAllBytes(file.FullName));

        // A recorded plan is only valid against a catalog whose *shape* it still fits; serving it
        // against another is exactly the wrong-answer bug this check exists to stop. Since D271 (a)
        // the comparison is by shape and by statement, never by a minted version: a version is new in
        // every process, so a recording keyed on one could never be replayed at all. And since
        // D271 (d) it is per table — the tables this plan reads, and no others.
        if (_catalog is not null)
        {
            RequireShape(plan, _catalog);
        }

        // The redaction recorded beside the plan, and only for a request that asks for one (D262).
        // A corpus recorded before this existed — or by an engine that never redacts — has no such
        // file, and a request that asks for nothing does not look for it.
        var redacted = request.Redaction is null
            ? null
            : ReadRedaction(Path.Combine(_directory.FullName, key + RedactedSuffix));

        return ValueTask.FromResult(new PlanResult { Plan = plan, RedactedSql = redacted });
    }

    /// <summary>
    /// Refuses a recorded plan whose reads no longer fit the registered catalog (D271 (d)). Per
    /// table: the table must still be declared, and every column the plan projects must still be the
    /// column it projected — same name, same type, same position. A table the plan never reads may
    /// have changed in any way at all without making this plan wrong, which is the whole of what
    /// per-table staleness means.
    /// </summary>
    private static void RequireShape(Plan plan, CatalogContext catalog)
    {
        foreach (var rel in PlanWalker.Rels(plan))
        {
            var (table, projection) = rel.KindCase switch
            {
                Rel.KindOneofCase.Read => (rel.Read.Table, (IReadOnlyList<uint>)rel.Read.Projection),
                Rel.KindOneofCase.IndexLookup =>
                    (rel.IndexLookup.Table, (IReadOnlyList<uint>)rel.IndexLookup.Projection),
                _ => (null, []),
            };
            if (table is null)
            {
                continue;
            }

            var declared = Find(catalog, table);
            if (declared is null)
            {
                throw new Chalk.Sources.StalePlanException(
                    plan.ContextId,
                    plan.CatalogEpoch,
                    catalog.ContextId,
                    catalog.Epoch,
                    $"{table.Schema}.{table.Table}");
            }

            for (var i = 0; i < projection.Count; i++)
            {
                var ordinal = (int)projection[i];
                var field = i < rel.RowType.Fields.Count ? rel.RowType.Fields[i] : null;
                if (ordinal < declared.Columns.Count
                    && field is not null
                    && string.Equals(declared.Columns[ordinal].Name, field.Name, StringComparison.OrdinalIgnoreCase)
                    && declared.Columns[ordinal].Type.ToProto().Equals(field.Type))
                {
                    continue;
                }

                throw new Chalk.Sources.StalePlanException(
                    plan.ContextId,
                    plan.CatalogEpoch,
                    catalog.ContextId,
                    catalog.Epoch,
                    $"{table.Schema}.{table.Table}");
            }
        }
    }

    private static Chalk.Catalog.TableDescriptor? Find(CatalogContext catalog, TableRef table)
    {
        foreach (var schema in catalog.Schemas)
        {
            if (!string.Equals(schema.SourceId, table.SourceId, StringComparison.Ordinal)
                || !string.Equals(schema.Name, table.Schema, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var declared in schema.Tables)
            {
                if (string.Equals(declared.Name, table.Table, StringComparison.OrdinalIgnoreCase))
                {
                    return declared;
                }
            }
        }

        return null;
    }

    /// <summary>The recorded redaction, or null when none was recorded for this key.</summary>
    private static string? ReadRedaction(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? File.ReadAllText(file.FullName).TrimEnd('\n') : null;
    }

    /// <summary>
    /// What a recorded redaction is filed under, beside the plan's own <c>.binpb</c> (D262). Its own
    /// file rather than a field of the plan: a redaction is a rendering of the statement and never
    /// part of it, and adding it to the recorded <c>Plan</c> would move every fixture there is.
    /// </summary>
    internal const string RedactedSuffix = ".redacted";

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// The file name a plan is recorded under: a content hash, so the corpus does not need a naming
    /// convention that survives SQL edits. An <c>index.txt</c> written alongside maps keys back to
    /// their query for a human reading the directory.
    /// </summary>
    /// <remarks>
    /// Everything that can change the plan for one SQL text goes into the hash. The conformance
    /// level (D34) is one of those: the same statement under two dialects is two plans, or two
    /// different errors, and serving one for the other would be a wrong answer. So are the dialect
    /// function libraries (D60), and from M4 the capabilities a statement turned off (D87).
    /// </remarks>
    /// <remarks>
    /// The disabled capabilities are appended only when there are any, so a corpus recorded before
    /// M4 keeps the keys it already has. The property test that uses the option plans for real
    /// rather than from a recording, so nothing is recorded under the longer form today.
    /// </remarks>
    /// <remarks>
    /// The planning options join them from this milestone (D234): a budget and a convergence test
    /// can both change which plan the optimiser returns, so two prepares that differ only in them
    /// are two plans. Appended only when they are not the run-to-completion default, so a corpus
    /// recorded before they existed keeps every key it has — and the stop token is deliberately not
    /// in the material, because a stop is an event during one search and not a property of the
    /// statement.
    /// </remarks>
    public static string KeyFor(
        string sql,
        PushdownLevel pushdown,
        SqlConformance conformance,
        IReadOnlyList<SqlLibrary> libraries,
        string contextId,
        IReadOnlyList<DisabledCapability>? disabledCapabilities = null,
        PlanningOptions? planning = null)
    {
        var named = string.Join(",", libraries.Select(l => l.ToString()));
        var off = disabledCapabilities is { Count: > 0 }
            ? "\n" + string.Join(",", disabledCapabilities.Select(c => c.ToString()))
            : string.Empty;
        var budget = planning?.CacheKeyPart is { Length: > 0 } part ? "\n" + part : string.Empty;
        var material = $"{contextId}\n{pushdown}\n{conformance}\n{named}{off}{budget}\n{sql.Trim()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }
}

/// <summary>
/// Decorator that writes every plan it sees as <c>.binpb</c>, <c>.json</c> and <c>.digest</c>. Used by
/// <c>Chalk.CorpusTool record</c>; the JSON twin is what makes a planning change reviewable in a diff.
/// </summary>
public sealed class RecordingPlanner : IQueryPlanner
{
    private static readonly JsonFormatter Json =
        new(JsonFormatter.Settings.Default.WithIndentation("  ").WithFormatDefaultValues(false));

    private readonly IQueryPlanner _inner;
    private readonly DirectoryInfo _directory;
    private readonly Dictionary<string, string> _index = [];
    private string _contextId = string.Empty;

    public RecordingPlanner(IQueryPlanner inner, string directory)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _inner = inner;
        _directory = System.IO.Directory.CreateDirectory(directory);
    }

    /// <summary>The name to file the next plan under, in addition to its content-hash key.</summary>
    public string? NextName { get; set; }

    public ValueTask<PlannerInfo> GetInfoAsync(CancellationToken ct = default) => _inner.GetInfoAsync(ct);

    /// <summary>What the transport reports, so a recording run registers exactly as the engine would.</summary>
    public bool AcceptsCatalogDeltas => _inner.AcceptsCatalogDeltas;

    public ValueTask RegisterCatalogAsync(
        CatalogRegistration registration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        _contextId = registration.Catalog.ContextId;
        return _inner.RegisterCatalogAsync(registration, ct);
    }

    public ValueTask RegisterStatisticsAsync(
        StatisticsRegistration statistics, CancellationToken ct = default) =>
        _inner.RegisterStatisticsAsync(statistics, ct);

    public async ValueTask<PlanResult> PlanAsync(PlanRequest request, CancellationToken ct = default)
    {
        var result = await _inner.PlanAsync(request, ct).ConfigureAwait(false);
        Write(request, result);
        return result;
    }

    public ValueTask<RedactedSql> RedactSqlAsync(
        RedactSqlRequest request, CancellationToken ct = default) =>
        _inner.RedactSqlAsync(request, ct);

    private void Write(PlanRequest request, PlanResult result)
    {
        var key = RecordedPlanner.KeyFor(
            request.Sql,
            request.Options.Pushdown,
            request.Options.Conformance,
            request.Options.Libraries,
            request.ContextId,
            request.Options.DisabledCapabilities,
            request.Planning);
        var level = request.Options.Pushdown.ToString().ToLowerInvariant();

        // The content-hash name is what RecordedPlanner looks up; the readable name is what a human
        // diffing corpus/plans wants to see.
        WriteAll(key, result.Plan);
        if (NextName is { } name)
        {
            WriteAll($"{name}.{level}", result.Plan);
            _index[key] = $"{name}.{level}";
        }

        // The redaction beside the plan, for a request that asked for one (D262). A recording run
        // that asks for none — which is every corpus run — writes nothing here, so no fixture moves.
        if (result.RedactedSql is { } redacted)
        {
            File.WriteAllText(
                Path.Combine(_directory.FullName, key + RecordedPlanner.RedactedSuffix),
                redacted + "\n");
            if (NextName is { } named)
            {
                File.WriteAllText(
                    Path.Combine(
                        _directory.FullName, $"{named}.{level}" + RecordedPlanner.RedactedSuffix),
                    redacted + "\n");
            }
        }

        File.WriteAllLines(
            Path.Combine(_directory.FullName, "index.txt"),
            _index.OrderBy(e => e.Value, StringComparer.Ordinal).Select(e => $"{e.Key}  {e.Value}"));
    }

    private void WriteAll(string name, Plan plan)
    {
        File.WriteAllBytes(Path.Combine(_directory.FullName, name + ".binpb"), plan.ToByteArray());
        File.WriteAllText(Path.Combine(_directory.FullName, name + ".json"), Json.Format(plan) + "\n");
        File.WriteAllText(
            Path.Combine(_directory.FullName, name + ".digest"),
            PlanDigest.Format(plan.PlanDigest) + "\n");
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    /// <summary>The context id catalogs were registered under, for the recorder's own bookkeeping.</summary>
    public string ContextId => _contextId;

    /// <summary>Number of distinct plans recorded, for the tool's summary line.</summary>
    public int Count => _index.Count;
}
