# ChalkQL guide

ChalkQL is an embedded federated SQL query engine for .NET. Apache Calcite plans queries in JVM sidecars, while ChalkQL executes them within the .NET host using a vectorised engine over Apache Arrow, spanning application-owned data and remote sources.

Optional entitlements apply application-defined row, column, aggregate and tenancy policy at the relational-plan level. ChalkQL currently supports read-only `SELECT`; there is no DML, DDL or transaction support.

The [README](../README.md) introduces ChalkQL, its motivation and deployment model. This guide covers configuration, behaviour and extension points; for a progressive worked example, see the [tutorial](tutorial.md).

## Components

At implementation level, ChalkQL is split between the .NET host and the Calcite planner:

```text
host application · .NET
  Chalk.Client                SQL in, Arrow RecordBatch out; IQueryPlanner, gRPC transport
  Chalk.Ir                    Plan IR (protobuf) + validator, walker, printer, digest
  Chalk.Catalog               CatalogContext, TableDescriptor, capabilities
  Chalk.Execution             internal: vectorised operators, kernels, reference executor
  Chalk.Sources.Abstractions  ISourceRuntime — the public extension point
  Chalk.Sources.Poco          in-process POCO tables
  Chalk.Sources.Ado           any DbProviderFactory, with pushdown per declared capability
  Chalk.Sources.DuckDb        the same source, reading DuckDB's data chunks natively
  Chalk.Sources.Akade         an Akade.IndexedSet as a table, its indexes discovered and served
  Chalk.Sources.Conformance   checks a source's descriptor against the source itself
        │ gRPC — UDS or TCP
planner sidecar · JVM 21
  chalk-planner               Calcite catalog assembly, rule sets, cost model, RelNode → PlanIR
```

The plan IR (`proto/chalk/v1/*.proto`) is the sole contract between the .NET and JVM sides. Both builds compile the same `proto/` directory; generated code is not checked in.

## Requirements

|          |                                                                                            |
| -------- | ------------------------------------------------------------------------------------------ |
| .NET SDK | 10.0.1xx                                                                                   |
| JDK      | 21 — only to build and run the planner sidecar. CI uses Temurin; any JDK 21 works locally. |

`scripts/install-tools.sh` installs the JDK, Gradle, `buf`, `protoc` and the gRPC
C# plugin with Homebrew on macOS. On an Apple Silicon Mac the Homebrew `protoc`
and `grpc_csharp_plugin` are required because `Grpc.Tools` ships no arm64 macOS
`protoc`; `Directory.Build.props` picks them up automatically.

## Installing

The .NET packages are published as `ChalkQL.*`; the assemblies and namespaces inside
them keep the `Chalk` name.

```bash
dotnet add package ChalkQL
```

`ChalkQL` contains the engine, client, entitlements layer and source abstractions. The
Calcite planner sidecar is embedded in `Chalk.Client.dll` as a runnable JAR, so a
co-located planner needs no separately deployed Chalk artefact. A host still needs a
JDK 21 or newer.

The stock source adapters and their conformance tooling are packaged separately:

```bash
dotnet add package ChalkQL.Sources
```

`ChalkQL.Sources` adds the POCO, ADO.NET, DuckDB and Akade sources and depends on the matching
version of `ChalkQL`.

For the common co-located case, starting the planner requires no path or port
configuration:

```csharp
await using var sidecar = await PlannerProcess.StartAsync();
```

When first needed, `PlannerProcess` materialises the embedded planner into a
content-addressed per-user cache and launches it from there. Subsequent starts reuse the
same cached JAR. `PlannerProcessOptions.JarPath` or `CHALK_PLANNER_JAR` may instead name
an external JAR explicitly; a path so named that does not exist is refused rather than
silently replaced by the embedded planner.

The cache root may be overridden with `PlannerProcessOptions.ArtifactCacheDirectory` or
`CHALK_PLANNER_CACHE`; otherwise ChalkQL uses the platform's normal per-user cache
location. Nothing is materialised merely by loading ChalkQL, and applications that
connect only to an independently managed planner do not write the embedded JAR to disk.

`scripts/pack.sh` builds the packages from a checkout. The third-party components
shaded into the embedded planner JAR, and their licences, are listed in
`dotnet/packaging/ChalkQL.Package/THIRD-PARTY-NOTICES.txt`.

## Quickstart

```bash
git clone <this repo> && cd chalk
./scripts/install-tools.sh      # macOS; on Linux install a JDK 21 and buf yourself
./scripts/build.sh              # gradle shadowJar + dotnet build
./scripts/test.sh               # java tests, .NET unit tests, then integration tests
```

Then, in your own application:

```csharp
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Sources.Poco;

public sealed record UsdRate(string Currency, DateOnly Ts, double Rate);

var rows = new List<UsdRate> { /* … sorted by (Ts, Currency) … */ };

var rates = new PocoSourceBuilder("mem")                   // source id; schema name defaults to "main"
    .AddTable("usd_rates", rows, t => t
        .OrderedBy(r => r.Ts).ThenBy(r => r.Currency)      // declared collation — this is what deletes sorts
        .UniqueKey(r => r.Ts, r => r.Currency))
    .Build();

// Starts the bundled sidecar with no planner path or port to configure.
// On Unix-like systems the default transport is a Unix domain socket.
await using var sidecar = await PlannerProcess.StartAsync();

await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
{
    ContextId = "demo",
    Sources = [rates],
    Planner = sidecar.CreatePlanner(),
});

var q = await engine.PrepareAsync(
    "SELECT currency, ts, rate FROM usd_rates WHERE currency = ? AND ts >= ?");

await using var exec = await engine.ExecuteAsync(q, ["EUR", new DateOnly(2026, 1, 3)]);
await foreach (var batch in exec.Batches)
{
    // Apache.Arrow.RecordBatch — yours to keep; dispose it when done
    batch.Dispose();
}
Console.WriteLine(exec.Stats.RowsScanned);

// Dapper-style named parameters, including a list expanded into an IN clause
var byName = await engine.PrepareAsync(
    "SELECT currency, ts, rate FROM usd_rates WHERE currency IN @currencies AND ts >= @since");
await using var exec2 = await engine.ExecuteAsync(byName,
    new { currencies = new[] { "EUR", "JPY" }, since = new DateOnly(2026, 1, 3) });
```

A runnable version of exactly this program is `dotnet/samples/Chalk.Sample.Quickstart`:

```bash
./scripts/quickstart.sh
```

For a worked tour of ChalkQL, see the [tutorial](tutorial.md). It follows one marketplace
through in-process tables, federation, functions, entitlements, replanning and live
temporal queries.

Every result shown there is captured from a real run of
`dotnet/samples/Chalk.Sample.Tutorial` and verified by `./scripts/tutorial.sh`.

### Transports

The planner speaks gRPC over two transports. A **Unix domain socket** is the default for
a sidecar launched by `PlannerProcess` on macOS and Linux: ChalkQL chooses a short unique
path under `/tmp`, so there is no port to allocate and no address to configure. On
platforms where the default is TCP, an ephemeral loopback port is chosen instead.

A host may select either transport explicitly with `PlannerProcessOptions.Transport`,
and may provide its own Unix socket path with `PlannerProcessOptions.SocketPath`.

An independently managed planner accepts the same transports:

```bash
java -jar chalk-planner.jar --socket /tmp/chalk.sock
# chalk-planner listening on unix:/tmp/chalk.sock

java -jar chalk-planner.jar --port 7433
# chalk-planner listening on 127.0.0.1:7433
```

Connect to an already-running planner directly with `GrpcQueryPlanner`:

```csharp
var planner = new GrpcQueryPlanner(new GrpcPlannerOptions
{
    Address = new Uri("unix:///tmp/chalk.sock"),
});
```

or:

```csharp
var planner = new GrpcQueryPlanner(new GrpcPlannerOptions
{
    Address = new Uri("http://127.0.0.1:7433"),
});
```

`--socket` and `--host`/`--port` are mutually exclusive on one command line, as are
`CHALK_PLANNER_SOCKET` and `CHALK_PLANNER_HOST`/`CHALK_PLANNER_PORT` in one environment;
between the two, the command line wins as it does for every other option. The sidecar
removes its socket file on shutdown and unlinks a stale one at the next start, but
refuses to touch a path a live planner is already listening on.

### Deploying the planner independently

`PlannerProcess` is intended for a planner co-located with the .NET host. A production
deployment may instead run the JVM sidecar independently — including on another host —
and connect to it using `GrpcQueryPlanner`.

The exact planner JAR paired with the installed ChalkQL client can be exported without
depending on NuGet package layout or manifest-resource names:

```csharp
await using var output = File.Create("chalk-planner.jar");
await PlannerArtifact.CopyToAsync(output);
```

The embedded artefact's SHA-256 is also available when deployment tooling wants to
verify or content-address the exported JAR:

```csharp
var sha256 = await PlannerArtifact.Sha256Async();
```

A deployment pipeline may, for example, store it as
`chalk-planner-<sha256>.jar`. ChalkQL does not require any particular filename when the
planner is run independently; a stable `chalk-planner.jar` is equally valid when the
deployment system already versions or hashes its artefacts.

Copy the exported JAR to the planner host and launch it in the usual way:

```bash
java -jar chalk-planner.jar --host <bind-address> --port 7433
```

The .NET host then connects directly rather than starting a child process:

```csharp
var planner = new GrpcQueryPlanner(new GrpcPlannerOptions
{
    Address = new Uri("http://planner.internal:7433"),
});

await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
{
    ContextId = "app",
    Sources = [source],
    Planner = planner,
});
```

In this topology `PlannerProcess` is never invoked, so the embedded planner is neither
materialised nor launched by the .NET process. The embedded copy remains useful as the
canonical planner artefact corresponding to that version of the ChalkQL client and as a
convenient source for deployment tooling.


## Sources

A source is anything that can describe some tables and produce Arrow batches for
them. Two ship in the box, and the extension point is public.

#### POCO — Plain Old C# Objects

`Chalk.Sources.Poco` turns `IEnumerable<T>` into a table: columns are inferred
from the row type, and keys, indexes and foreign keys are declared fluently.
Nothing is pushed into it because there is nothing to push *to* — the engine
reads it directly, so a filter is evaluated by Chalk's own vectorised kernels.

```csharp
var source = new PocoSourceBuilder("mem")
    .AddTable("orders", orders, t => t
        .UniqueKey(o => o.Id)
        .ForeignKey(o => o.CustomerId).References<Customer>(c => c.Id, verify: true))
    .AddTable("customers", customers, t => t.UniqueKey(c => c.Id))
    .Build();
```

`verify: true` checks every non-NULL child key against the parent at `Build()`.
It has no default: a key nobody checked is a claim, and the planner acts on it —
a join to a parent nothing reads on a key that parent is unique in is *deleted*.

POCO indexing is extensible too: implement `IPocoIndex<T>` to adapt an existing index
structure, and run the test kit's `PocoIndexConformance.Verify` against it to assert Chalk's
range and ordering contract — a full-scan comparison over a battery of ranges, which is what
makes an adapter's claims checked rather than trusted. [`Chalk.Sources.Akade`](../dotnet/src/Chalk.Sources.Akade/) is that seam used
in earnest: `AkadeSource.From` publishes an
[Akade.IndexedSet](https://github.com/akade/Akade.IndexedSet) as a table and discovers its
indexes — hash, ordered, compound and prefix — with no adapter written by the host; its [`README`](../dotnet/src/Chalk.Sources.Akade/)
says what each Akade index becomes and how to modify a published set safely.

An index that answers prefixes rather than ranges — a trie — declares `IndexKind.Prefix`,
and `WHERE name LIKE 'p%'` is then a lookup on it rather than a predicate; an ordered
string index serves the same query as the plain range `[p, next(p))`, with no adapter
change at all. An index that can also be walked from its last matching row to its first
implements `IReversiblePocoIndex<T>`, which is what lets `ORDER BY ts DESC LIMIT 1` be
one walk and one row instead of a sort.

Custom indexes participate in the same snapshot lifecycle as the rows they index.
If the underlying structure is mutable, do not modify it while a published snapshot
may still be executing; publish replacement data and indexes through `RefreshAsync`
instead, and reclaim the old state only after `SnapshotReleased`. 
See [Replacing the rows while queries run](#replacing-the-rows-while-queries-run).

#### Replacing the rows while queries run

A registered table's rows, the indexes over them and the statistics of them are one
immutable **snapshot**, and every scan captures it once at entry. Replacing them is
therefore never mutation: Chalk builds the new snapshot off the execution path and swaps
the reference, so an execution already running reads the rows it started with, all of them,
and the next one sees the new ones.

```csharp
var bars = currentBars;
var mem = new PocoSourceBuilder("mem")
    .AddTable("bars", () => bars, t => t.OrderedBy(b => b.Ts))   // the Func a refresh re-reads
    .AddTable("ticks", ticks)
    .Build();

// Several tables, several sources, one catalog epoch. Every entry is validated before
// anything is built, so a bad one changes nothing anywhere.
await engine.RefreshAsync(refresh =>
{
    refresh.Replace(mem, "bars", newBars);        // a new collection; the old one is never mutated
    refresh.Replace(other, "orders", newOrders);
    refresh.Append(mem, "ticks", moreTicks);      // the one incremental operation
}, ct);

// Or the simple form: every source re-reads its Funcs, then one epoch.
await engine.RefreshAsync(ct);

// When the last execution over the old rows finishes, you may have them back.
mem.SnapshotReleased += r => Recycle(r.Table, r.Collection);
```

**Your one obligation is to never mutate a collection you have given Chalk** — hand it a
new one instead. The zero-copy paths read your own arrays, and that rule is what keeps an
in-flight execution's view intact. `SnapshotReleased` is how you know when a replaced
collection is no longer being read.

A refresh that changes no *shape* — the columns, keys, collations, indexes and foreign keys
of every table, statistics and row counts aside — leaves every `PreparedQuery` valid, and
it runs against the new rows. A schema change makes them stale exactly as before
(`PreparedQuery.IsStale`, `ChalkEngine.ShapeEpoch`). Updates and deletes are not built.

### ADO.NET — a real database

`Chalk.Sources.Ado` works with any `DbProviderFactory`. Point it at a
connection, pick a dialect preset, say what the source may be asked to do, and
let it discover its tables:

```csharp
var source = new AdoSourceBuilder("sales", SqliteFactory.Instance, connectionString, "main")
    .Dialect(DialectProfiles.Sqlite)
    .Capabilities(AdoCapabilities.For(DialectProfiles.Sqlite))
    .DiscoverTables()
    .Build();
```

Presets ship for **SQLite**, **DuckDB**, **PostgreSQL** and **ANSI**, each with
its own dialect subclass and a conformance-kit run behind it. Naming any other
Calcite `SqlDialect.DatabaseProduct` — `oracle`, `mssql`, `big_query`, and so
on, case-insensitively with `-` or `_` — selects that product's own stock
dialect over the same profile, untuned: nothing has run the conformance kit
against it yet, which is the host's job. A profile describes how the source
spells and *evaluates* SQL — identifier quoting and casing, string collation,
where NULLs sort, decimal and timestamp precision, whether it may answer
approximately, and the placeholder style its driver takes. The capabilities
describe what it may be asked to do: which predicate shapes, functions and
aggregates, whether projection, sorts, limits, grouping and joins travel, and
ceilings on pushed rows and `IN` list length. `PlannerInfo.Dialects` lists
every name a running sidecar accepts.

**Nothing is pushed that is not declared.** A source you have only pointed at is
a source nobody has checked, and the safe reading of a silent descriptor is
"scan it". Two settings are worth knowing about:

- `.RowCounts(RowCountMode.Exact)` runs `COUNT(*)` per table at build time. Off
  by default, because it is not free and the planner's estimates survive without
  it.
- `.Options(new SourceOptions { QueryTimeout = … })` bounds one query. A host can
  override it per source on `ExecutionOptions.SourceOptions` without rebuilding a
  source it did not write.

Failures are attributed: a provider exception becomes a `SourceExecutionException`
naming the source and the query, a source that runs long becomes a
`SourceTimeoutException` naming the budget, and a column whose provider type does
not match the catalog becomes a `SourceContractException` naming both. Cancelling
a query calls `DbCommand.Cancel` on the driver.

### DuckDB — the same source, without the per-row cost

`Chalk.Sources.DuckDb` is `Chalk.Sources.Ado` with a different reader underneath.
Everything else is unchanged — discovery, capabilities, pushdown, the dialect
profile — but a query's rows arrive as DuckDB *data chunks* copied straight into
the execution's arena rather than one cell at a time through a `DbDataReader`:

```csharp
var warehouse = DuckDbSources.AddDuckDbSource("warehouse", "DataSource=warehouse.duckdb")
    .Capabilities(AdoCapabilities.For(DialectProfiles.DuckDb))
    .RowCounts(AdoSourceBuilder.RowCountMode.Exact)
    .DiscoverTables()
    .Build();
```

A fetched row then costs **nothing** on the managed heap, text included: measured
at 0.012 bytes per fetched row for a scan of two text columns, against 3 288 for
the same query through the provider's own reader. A result column of a type the
native reader does not copy — a `LIST`, a `STRUCT`, an `ENUM` — sends that query
back to the `DbDataReader` path before the first chunk arrives, and
`Stats.SourcePaths` says which reader ran.

A host that already registers DuckDB through `AdoSourceBuilder` can add the
reader to it with `.UseNativeReader()`, and a host that does neither keeps the
`DbDataReader` path it has today. The extension point is public: `IRemoteFetch`
is what a driver that can hand over whole columns implements, and a fetch that
builds its own `DbCommand` binds its parameters through `AdoParameters` — the
same names and the same `DbType` per Chalk type that the default reader binds,
so two readers never disagree about what a parameter means.

`.UseArrowReader()` selects a second native reader instead: DuckDB's own
chunk-to-Arrow export, which reads every type DuckDB can export and is faster on
a plain scan. It is not the default, because its batch is DuckDB's own 2 048-row
chunk whatever batch size you asked for.

**The native library is yours to choose.** `Chalk.Sources.DuckDb` references
`DuckDB.NET.Data`, not `DuckDB.NET.Data.Full`: the `.Full` suffix adds only the
bundled `libduckdb` binaries, and a library package should not pick your
platform's native code for you. Add `DuckDB.NET.Bindings.Full` yourself, or your
own build of the same DuckDB release — and **match the versions**, because both
packages carry the same `DuckDB.NET.Data.dll`.

### Writing your own

Implement `ISourceRuntime`: `DescribeSchema()` for the catalog and `ScanAsync`
for the rows. That is the whole minimum, and it is enough to be queried — the
engine will do every filter, join and aggregate itself. From there, `IndexLookupAsync`
lets a declared index answer a point or range lookup, and `ExecuteQueryAsync`
lets a whole subtree be pushed. A source that speaks SQL reads
`RemoteQueryRequest.QueryText`; one that speaks Chalk's IR reads
`RemoteQueryRequest.PushedPlan` and ignores the text, which will be empty.

`ScanAsync` is not optional even for a source that can do everything else: it is
the oracle every other path is checked against.

### Running the conformance kit

A descriptor is a set of claims the planner acts on, and a claim that is not true
produces a **wrong answer**, not an error. `Chalk.Sources.Conformance` is how you
find out before your users do:

```csharp
var report = await SourceConformance.RunAsync(source, new ConformanceOptions
{
    Seed = async (seed, ct) => { /* create seed.CreateTable, run seed.Inserts */ },
});

Console.WriteLine(report);          // every finding as "declared X, observed Y"
Assert.True(report.Passed);         // or SourceConformance.VerifyAsync, which throws
```

It asks your source the same question twice — once through the pushed path and
once by computing the answer itself over your `ScanAsync` — and compares. It runs
every capability you declared on and off, probes the dialect (string collation,
accent ordering, `LIKE` case and escapes, NULL placement, `BETWEEN` inclusivity,
empty string versus NULL, integer division and modulus of negatives, decimal and
timestamp precision), and checks that a unique key you declared is unique. It
needs no planner and no sidecar.

Point it at your own tables instead of seeding, with
`ConformanceOptions.Table`, if the kit's schema already exists.

The reports for the in-box source are checked in at `corpus/conformance/`, one
per dialect, so a probe that starts observing something different shows up as a
changed line rather than a passing test. Running the kit against SQLite is what
found that its `LIKE` folds ASCII case even though its `=` is binary — the
in-box preset no longer claims the `LIKE` shapes there.

## Federation

Every source is a schema in one catalog, and a query may name as many of them as
it likes. `main.customers JOIN warehouse.orders JOIN metrics.bars` is one
statement, one plan and one result; nothing about writing it says which rows live
where.

```csharp
await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
{
    ContextId = "app",
    Sources = [poco, warehouse, metrics],   // the first is the default schema
    Planner = planner,
});
```

### Four strategies, and cost chooses

A join whose two sides are in different sources has four ways to run. The
planner enumerates the ones the
policy allows and costs them against each other:

| Strategy | What it does | When it wins |
|---|---|---|
| **Local** | fetch both sides and join here | both sides small, or nothing better is possible — the fallback that always exists |
| **Lookup** | stream the driving side; ask the other source once per batch of its distinct keys | a small side against a large remote table |
| **Broadcast** | ship the small side's rows into the other source's query and let it do the join | the same, in one call, when the source takes a `VALUES` relation and the keys would need several calls |
| **Adaptive** | plan both, and choose at execution from the small side's *measured* distinct keys | whenever the small side's estimate is a guess — which is most of the time, and is why this is the default |

The counters say what happened: `RemoteCalls`, `RowsFetched`, and
`Stats.AdaptiveDecisions`, which records the branch each adaptive join took and
the key count it decided on. The plan itself never says — both branches are in
it, and the digest covers both, so the same statement has the same digest
whatever the data turned out to be.

### The policy is yours

The shipped default prefers a lookup or a broadcast when one side is small
relative to the other. Replacing it needs no fork: implement
`ICrossSourceJoinPolicy` and return a descriptor.

```csharp
public sealed class NeverLookIntoTheWarehouse : ICrossSourceJoinPolicy
{
    public CrossSourceJoinPolicy Build(CatalogContext catalog) => new()
    {
        LocalJoinMaxRows = 5_000_000,     // fail rather than fetch more than this
        Pairs =
        [
            new SourcePairRule
            {
                RightSource = "warehouse",
                Allowed = [JoinStrategy.Local],
            },
        ],
    };
}
```

It is called at engine creation and again on every catalog refresh, and given the
catalog it is about to describe. One statement can override it —
`PrepareOptions.JoinPolicy` is merged over the catalog's, field by field — which
is how you forbid a strategy for one report without changing what everything else
does.

`LocalJoinMaxRows` is a guardrail rather than a limit: a plan that would pull more
than that across a boundary into a local join fails **at planning**, naming the
join and the estimate, instead of running for a minute and then answering.

### Partitioned tables

A table's rows may live in several places. Declare where, and a scan becomes the
union of the partitions that can hold a matching row:

```csharp
new TableDescriptor
{
    Name = "usd_rates", Columns = columns, RowCount = -1,
    Partitioning = new PartitioningDescriptor
    {
        PartitionColumn = 0,                       // `currency`
        Partitions =
        [
            new() { Schema = "eu", Table = "rates_eur", Value = "EUR", HasValue = true },
            new() { Schema = "jp", Table = "rates_jpy", Value = "JPY", HasValue = true },
        ],
    },
}
```

`WHERE currency = 'EUR'` reads one partition; a query that names neither reads
both, concurrently, bounded by `ExecutionOptions.MaxRemoteConcurrency` overall and
by each source's own `SourceOptions.MaxConcurrentQueries`. The union claims no
ordering, so ask for one if you need one.

### Letting a source do the truncating

Every SQL text below is what the source was actually sent, copied from the test
that reads it off the connection.

**Enabling it.** `AdoCapabilities.For(profile)` derives what a source may be asked
from its dialect profile, and `SupportsSort`, `SupportsLimit` and `SupportsOffset`
are part of what it turns on. The two are separate on purpose: the *profile* says
how to spell SQL for this source, and the *capabilities* say what it may be asked
to do. A source built with `SourceCapabilities.None` is scanned and nothing else.

```csharp
var duck = DuckDbSources.AddDuckDbSource("duck", "DataSource=orders.duckdb")
    .Capabilities(AdoCapabilities.For(DialectProfiles.DuckDb))
    .DiscoverTables()
    .Build();
```

**A literal bound travels.** The source truncates, and twenty-five rows cross the
boundary instead of every open order:

```sql
SELECT id, total FROM duck.orders WHERE status = 'open' ORDER BY id DESC LIMIT 25
-- SELECT "id", "total" FROM "orders" WHERE "status" = 'open'
--   ORDER BY "id" DESC NULLS FIRST LIMIT 25
```

**A parameterised bound travels too, rendered per execution.** Prepare once,
execute with a page size, execute again with another: the text the source is sent
carries each execution's own number, while the statement's other parameters are
still bound by the provider (`$p0` here, because that is how this dialect spells a
placeholder).

```sql
SELECT id, total FROM duck.orders WHERE status = ? ORDER BY id DESC LIMIT ?
-- executed with ("open", 25):
-- SELECT "id", "total" FROM "orders" WHERE "status" = $p0
--   ORDER BY "id" DESC NULLS FIRST LIMIT 25
-- executed with ("open", 100):
-- SELECT "id", "total" FROM "orders" WHERE "status" = $p0
--   ORDER BY "id" DESC NULLS FIRST LIMIT 100
```

The number written into the text is the value bound at execution, and never
anything the planner was told beforehand: a value hint moves a cost estimate and
is not a value.

**Every branch gets it.** Over a `UNION ALL` or a partitioned table, the bound is
copied into each branch and rendered in each branch's own query. The local top-N
above them still decides the answer, so each source only has to offer its first
few candidates:

```sql
SELECT region, id FROM books.all_orders ORDER BY id LIMIT ?
-- executed with (4):
-- SELECT "region", "id" FROM "orders_north" ORDER BY "id" LIMIT 4
-- SELECT "region", "id" FROM "orders_south" ORDER BY "id" LIMIT 4
```

A bound with an `OFFSET` beside it is not copied: a branch's share of it would be
`offset + fetch`, which is an expression rather than a number, so the bound stays
where the statement put it.

**When it stays local.** The same statement over a source whose capabilities say
`SupportsLimit = false` — a host can register a narrower descriptor than the
derived one for a source it would rather not have sort or truncate for it — ships
the subtree without a bound, and the local fetch stops pulling once it has enough:

```sql
SELECT id, total FROM duck.orders WHERE status = ? ORDER BY id DESC LIMIT ?
-- SELECT "id", "total" FROM "orders" WHERE "status" = $p0
```

The answer is the same; the cost is not. Every matching row crosses the boundary
and the ordering is done here, which is worth knowing the price of before turning
a capability off.

### Cancellation, failure, and what a result means

- **Cancellation reaches the fetches.** One linked token per execution; the
  caller's cancellation cancels every in-flight fetch, and the enumerator
  completes within `ExecutionOptions.CancellationGracePeriod` (five seconds by
  default). A fetch that has not noticed by then is abandoned and logged with the
  source that would not stop.
- **A failure is attributable, and never partial.** The first source to fail
  cancels its siblings; the enumerator throws once, with
  `SourceExecutionException` naming the source, and **nothing is yielded after
  the fault**. A query that fails produced no rows, not some of them.
- **There is no cross-source snapshot, and Chalk does not pretend there is.** A
  federated result reflects each source at the moment it was read.
  `Stats.SourceFetches` records that moment per source — the first fetch, the
  last, and the rows — so a host that needs consistency across sources can see
  exactly what it did not get. Providing it is a stated non-goal.

## Parameters

Three styles, never mixed within one statement:

| Style | Example | Bound with |
|---|---|---|
| Positional | `WHERE symbol = ?` | `IReadOnlyList<object?>` |
| Ordinal | `WHERE ts >= $1 AND ts < $1 + …` | `IReadOnlyList<object?>`, `$1` may recur |
| Named | `WHERE symbol = @symbol` | dictionary, anonymous object or POCO |

A non-string enumerable bound to a parameter that always follows `IN` / `NOT IN`
expands into an `IN (?, ?, …)` list, Dapper-style; the empty list yields no rows
for `IN` and all rows for `NOT IN`.

## UTF-8 strings

Everything inside Chalk is UTF-8 already: a STRING column is Arrow UTF-8 in
arena buffers, and a kernel reads a row as bytes. `Utf8String` is how a host
joins that without a `string` in between — a readonly struct over
`ReadOnlyMemory<byte>` with **ordinal byte** equality, hashing and order, which
is the same order as Chalk's binary collation and as Arrow's:

```csharp
public sealed record Symbol(Utf8String Ticker, Utf8String? Label, long Rank);

// Reading a result without decoding anything you did not ask to decode.
await foreach (var batch in engine.QueryAsync("SELECT ticker FROM symbols"))
{
    var column = (StringViewArray)batch.Column(0);   // utf8view by default
    for (var i = 0; i < batch.Length; i++)
    {
        ReadOnlySpan<byte> bytes = column.GetUtf8(i).Span;   // no string
    }
}
```

It applies in three places, and everywhere it does, `string` still works and
still costs what it always did:

- **A POCO property** of type `Utf8String` or `Utf8String?` maps to STRING and
  its bytes are copied rather than transcoded. `byte[]` and
  `ReadOnlyMemory<byte>` still map to BINARY — the marker type is what says
  "this is text". Such a column is binary-collated: keys, indexes, foreign keys
  and the build-time verification all compare bytes.
- **A Tier 1 user function** may take and return `Utf8String`, in which case a
  lane is lent to it without a copy and its answer is copied straight into the
  result column.
- **Reading a result**: `GetUtf8(int)` on Arrow's `StringViewArray` — the layout a
  STRING column arrives in by default — or on its `StringArray` where
  the host asked for the classic layout with
  `ChalkEngineOptions.Output.Strings`, and `ColumnView.Utf8(row)` inside a
  Tier 2 kernel.

**The lifetime rule.** A `Utf8String` handed out of a batch or a lane points
into that batch's buffers and is valid until the batch is disposed or the arena
reuses it — exactly the rule a `ColumnView` has. Keep one past its batch by
calling `ToArray()` or `ToString()`. A `Utf8String` you construct yourself is
your own memory and outlives everything.

`ToString()` is the one place a .NET string is made, and you are the one who
calls it. Two things it is deliberately not: it is not culture-aware — casing,
culture collations and `LIKE` with non-ASCII folding stay on the string kernels,
which decode when they must — and it does not validate on construction. Bytes
are checked with `Utf8.IsValid` exactly where they enter a column, so an Arrow
buffer never holds invalid UTF-8 and nothing downstream pays to look again.

One sharp edge, because `Utf8String` converts implicitly from `byte[]`: a `null`
literal in the other arm of a conditional binds to *that* conversion, so
`Utf8String? x = flag ? value : null` is never null. Write the NULL out —
`Utf8String? x = default; if (flag) { x = value; }` — wherever a value may be
absent.

## Identifiers

Unquoted identifiers keep their case and match case-insensitively; double quotes
quote. So `SELECT Symbol FROM Bars` finds a `symbol` column and names the output
field `Symbol`, and `"symbol"` is exact.

One trap: Calcite reserves a few SQL keywords that are perfectly ordinary .NET
property names — **`OPEN` and `CLOSE` in particular**. A POCO with `Open` and
`Close` properties needs them quoted:

```sql
SELECT symbol, ts, "close" - "open" AS change FROM bars
```


## SQL dialect

Statements are standard SQL by default. A statement that wants one of the
familiar non-standard spellings — `!=` for `<>`, `%` for `MOD`, `GROUP BY` on a
SELECT alias, `OFFSET` before `LIMIT` — asks for a dialect that allows it:

```csharp
await engine.PrepareAsync(
    "SELECT currency, COUNT(*) AS n FROM usd_rates WHERE currency != 'EUR' GROUP BY currency",
    new PrepareOptions { Conformance = SqlConformance.Lenient });
```

`SqlConformance` has one value per Calcite conformance level — `Default`,
`Lenient`, `Babel`, `Strict92`, `Strict99`, `Pragmatic99`, `Strict2003`,
`Pragmatic2003`, `MySql5`, `Oracle10`, `Oracle12`, `SqlServer2008`, `Presto`,
`BigQuery` — and it is chosen per statement, so one engine can serve queries
written to different rules. Stricter levels reject more, not less: SQL:2003
requires a `FROM` clause, so `SELECT 1` fails under `Strict2003` and plans under
`Default`.

Identifier quoting and case sensitivity are *not* part of this. They are the same
under every dialect (see [Identifiers](#identifiers)), and that includes `Babel`.

### `Babel` also changes the parser

`Babel` is the one level that swaps the parser itself: the sidecar ships
Calcite's `calcite-babel` and uses its grammar for `Babel` and nothing else, so
every other level parses exactly as it always has. Two things that buys you:

```csharp
// PostgreSQL's :: cast. It needs the library as well as the dialect — the ::
// operator lives in Calcite's PostgreSQL library, not the standard table — so
// without Libraries the statement parses and then fails validation.
await engine.PrepareAsync(
    "SELECT id::VARCHAR AS id_text FROM events",
    new PrepareOptions
    {
        Conformance = SqlConformance.Babel,
        Libraries = [SqlLibrary.Postgresql],
    });

// And a much smaller set of reserved words: value, year, user, start, language,
// system and position are ordinary identifiers here, and parse failures
// everywhere else. (table and select stay reserved.)
await engine.PrepareAsync(
    "SELECT id AS value FROM events",
    new PrepareOptions { Conformance = SqlConformance.Babel });
```

`SELECT * EXCLUDE (col)`, `SELECT * EXCEPT (col)` and
`SELECT * REPLACE (expr AS col)` need none of this — they are in the core parser
and work at every level, `Default` included.

Chalk plans queries, and Babel's grammar also has `CREATE TABLE`, `BEGIN`,
`COMMIT`, `SHOW` and friends. Those are refused by name under `Babel` with
`PlanErrorKind.Unsupported`, rather than parsed and then failing somewhere
surprising.


## Planning on a budget

The optimiser searches until it has nothing left to try. A host that would
rather have a good plan now says so per statement, or once per engine:

```csharp
using var stop = new CancellationTokenSource();       // the user's "that will do"
var query = await engine.PrepareAsync(sql, new PrepareOptions
{
    Planning = new PlanningOptions
    {
        TimeBudget = TimeSpan.FromMilliseconds(250),
        ConvergencePatience = 3,                      // three samples with no gain is enough
        ConvergenceEvaluationInterval = 20,           // sampled every 20 rule evaluations
        StopToken = stop.Token,
    },
});
Console.WriteLine(query.PlanningState);               // Converged after 184 evaluations, ratio 0.9999
```

Every field defaults to today's behaviour, so a prepare that sets none installs
no listener and samples no cost. The interval is a **count of rule evaluations,
never a duration**, which is what makes a convergence-terminated plan the same
plan on every machine; the time budget is the one non-deterministic control, and
a budget spent before there is any complete plan is an error rather than a plan.
`StopToken` always returns a plan — the best complete one, or the first one if
there is none yet — and is not the call's own cancellation, which abandons the
prepare and returns nothing. A plan cut short passes every physical check that a
full search's plan does.

The sidecar itself time-slices planning under a bounded pool of workers, so a
complex query never holds up a simple one: `PlanningOptions.Priority` (`High`,
`Normal`, or the default `Normal`) says which queue a statement is relegated to
after its own first slice — every statement is served first regardless, so this
only matters under contention. `PlanningOptions.Session` groups statements that
should share a concurrency cap, by name, declared inline with no setup call:

```csharp
Planning = new PlanningOptions
{
    Priority = PlanningPriority.Low,                          // a batch report, not an interactive query
    Session = new PlanningSession { Name = "reports", MaxConcurrency = 2 },
}
```

The worker count is the sidecar's to set when it starts, with
`--planning-workers <n>` and `--planning-load-factor <f>` (a fraction of the
machine's processors) — or, launching one from .NET,
`PlannerProcessOptions.Workers`/`LoadFactor`. Neither set is every available
processor.

See chapter 14 of
[the tutorial](tutorial.md).

## Logging a statement safely

Parameters keep values out of the SQL string; hosts also write literals, and a
slow-query log, a trace or an audit trail then keeps whatever was written. Ask
for a **redacted** text and every literal becomes a keyed pseudonym:

```csharp
await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
{
    ContextId = "demo",
    Sources = [rates],
    Planner = planner,
    Redaction = new RedactionOptions
    {
        IncludeRedactedSql = true,          // off by default; nothing is computed unless you ask
        Salt = hostSecret,                  // absent means a random per-engine salt
    },
});

var q = await engine.PrepareAsync("SELECT currency, ts FROM usd_rates WHERE currency = 'EUR' LIMIT 10");
log.LogInformation("{Sql}", q.RedactedSql);
// SELECT "currency", "ts" FROM "usd_rates" WHERE "currency" = /*REDACTED-1a2b3c4d:CHAR*/ FETCH NEXT 10 ROWS ONLY
//                                                                  ^ eight hex characters, keyed by your salt
```

The counts that are positions rather than values — `LIMIT`, `OFFSET`, a window
frame's row count, an ordinal in `GROUP BY` or `ORDER BY` — are kept, so the
statement still reads as the shape it is.

The same option governs `PreparedQuery.PlanText` and the query text a
`SourceExecutionException` quotes, so no log line about the statement is half
safe. `ChalkEngine.RedactSqlAsync(sql)` serves text that was never prepared —
above all text that failed to parse, which falls back to the parser's token
stream and keeps nothing at all.

Two things read by name rather than by position. A parameter you wrote as
`@name` reads as `@name` in the redacted text and in the plan text, not as the
positional `?` the planner saw. And a literal that is one of the request
context's bound values — a tenant list folded into a policy's predicate, say —
carries the name it was bound under beside its pseudonym, in the plan text and
in the query text a source failure quotes:

```
/*REDACTED-3f9a1c2e:DECIMAL @ctx.tenant_id*/
```

The label says only that the value is the one bound under that name. A
membership list folded into one set is named as the set; a value the optimiser
coerced to another type is another value and carries no name; and the label is
a rendering, so it changes neither the pseudonym nor the structural hash.

Two properties are worth knowing before you rely on it:

- **Equal values correlate within a shape, and not across shapes.** The seed is
  keyed by the statement's structure, so `WHERE currency = 'EUR'` and
  `WHERE 'EUR' = currency` give different pseudonyms for the same value. With no
  salt supplied, a random one is generated per engine, so nothing correlates
  between two processes or two runs — the conservative default.
- **A pseudonym is not anonymisation.** A low-entropy value — a boolean, a
  status code, a small integer — is brute-forceable by anyone who knows the
  statement's shape and holds the salt. The salt is your secret.


## Functions

A schema declares functions the way it declares tables, and they travel with the
catalog: declaring one moves the epoch, and every plan made against that epoch
knows about it. A name a built-in already has is a registration error rather
than a shadowing — a query that means `UPPER` never quietly gets somebody
else's.

```csharp
var source = new PocoSourceBuilder("mem")
    .AddTable("bars", bars)

    // Inlined by the planner: the call disappears and what is left is algebra.
    .AddFunction("pct_change", f => f
        .Scalar<double, double, double>("a", "b")
        .Strict()
        .Sql("(b - a) / a"))

    // Implemented in this process. Never pushed, never folded.
    .AddFunction("bucket_price", f => f
        .Scalar<double, double, double>("price", "width")
        .Optional("off", ChalkType.Float64(nullable: true), 0.0)
        .Strict()
        .Client())
    .Build();

await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
{
    ContextId = "app",
    Sources = [source],
    Planner = planner,
    Functions = registry => registry.AddScalar<double, double, double, double>(
        "bucket_price", (price, width, off) => (Math.Floor(price / width) * width) + off),
});
```

There are three implementation kinds, and the planner treats each as what it is:

| Kind | Written as | What the planner does | Where it may run |
|---|---|---|---|
| **SQL** | `.Sql("(b - a) / a")` | Inlines it before validation, so the call is gone: a scalar body becomes its expression, an aggregate body becomes the built-in aggregates it is written over, a table body is a macro that expands into a sub-query | wherever its constituents may |
| **Client** | `.Client()` | Nothing — it travels by name, and is neither pushed nor folded | in this process |
| **Native** | `.Native("md5")` | Spells it the way the source does, inside the pushed query | that source alone; anywhere else is a planning error naming it |

Declared properties are acted on, not decoration. `Strict()` means NULL in,
NULL out without the body running. `Immutable()` (the default) is foldable,
`Stable()` is evaluated once per execution, `Volatile()` once per row and never
de-duplicated. `Increasing("t")` says an ordering survives the call, so
`ORDER BY minute_of(ts)` over a table collated by `ts` plans with no sort at
all. `Cost(n)` and `Rows(n)` reach the cost model.

### Two ways to write the implementation

**Tier 1** is ordinary delegates, and it is the one to reach for. Chalk
generates the lane loop: it reads the lanes, calls the delegate, writes the
result and its validity, and skips a strict function's NULL lanes before the
call. Nothing allocates per row unless the delegate does. Aggregates are a state
machine of the shape PostgreSQL's are:

```csharp
registry.AddAggregate("wsum", new AggregateSpec<Sum, double, double?>
{
    Init = () => default,
    Add = (ref Sum s, double x) => s.Total += x,
    Remove = (ref Sum s, double x) => s.Total -= x,   // ⇒ a sliding frame slides
    Merge = (a, b) => new Sum { Total = a.Total + b.Total },
    Finish = s => s.Total,
});
```

`Remove` and `Merge` are promises about arithmetic, not hints: `Remove` says the
state can be moved backwards exactly, which lets a sliding frame be maintained
rather than recomputed. With neither, a frame is recomputed from its rows — the
same answer, more work.

A Tier 1 delegate is written in the CLR types a POCO property maps from:

| SQL type | CLR type |
|---|---|
| BOOL, I8, I16, I32, I64, FP32, FP64 | `bool`, `sbyte`, `short`, `int`, `long`, `float`, `double` |
| STRING | `Utf8String`, or `string` |
| DECIMAL of up to 28 digits | `decimal` |
| DATE, TIME | `DateOnly`, `TimeOnly` |
| TIMESTAMP, TIMESTAMP_TZ | `DateTime`, `DateTimeOffset` (in UTC) |
| INTERVAL_DAY | `TimeSpan` |
| UUID | `Guid` |
| BINARY | `ReadOnlyMemory<byte>`, or `byte[]` |

A function that is not strict takes the nullable form of a value type, so it can
see a NULL. `string` and `byte[]` cost an allocation per row. The other types do
not. `Parameter<T>()` and `Returns<T>()` infer the SQL type from the CLR type as a
POCO property would: `decimal` is DECIMAL(28, 10) and `DateTime` is
TIMESTAMP(9). Declare a narrower type with `Parameter(name, type)` and still
implement it in `decimal` or `DateTime`. A temporal may also still be written as its
raw count: `int` days, or `long` units.

A DECIMAL wider than 28 digits has no CLR type. It is refused when the engine is
created, and a Tier 2 kernel reads it. A time the SQL type cannot hold exactly is
refused rather than rounded. That includes a `TimeOnly` or `TimeSpan` with a
fraction of a microsecond, and a `DateTime` with sub-millisecond ticks answered
for a TIMESTAMP(3). A nanosecond TIMESTAMP read as a `DateTime` is truncated to
its 100-nanosecond tick. A Tier 1 aggregate folds fixed-width values, so its input
and result may be any of these types except BINARY.

**Tier 2** is the expression evaluator's own contract, for a host that needs
SIMD or wants to avoid the per-lane call:

```csharp
[Experimental("CHALK001")]
public interface IVectorFunction
{
    FunctionSignature Signature { get; }
    void Invoke(ReadOnlySpan<ColumnView> args, ColumnWriter result, in FunctionContext context);
}
```

It is public under the `CHALK001` diagnostic id because it is the engine's own
shape made visible: opt in and it may change before the first tagged release.
Tier 1 is implemented on top of Tier 2, so the two cannot drift.

Every signature is checked against the catalog when the engine is created, so a
missing implementation or a delegate with the wrong CLR types fails before any
query runs, naming the function and both types. The row-at-a-time reference
executor calls the same implementations, which is what makes the differential
test a test of Chalk's plumbing rather than of your arithmetic.

### Composite results

A client-bodied function can answer a small record instead of a single value,
and SQL takes the record apart by field. The record is the declaration:

```csharp
public readonly record struct Classification(Utf8String Category, double Confidence);

var source = new PocoSourceBuilder("mem")
    .AddTable("transactions", transactions)
    .AddFunction("classify_transaction", f => f
        .Scalar<Utf8String, double, Classification>("description", "amount")
        .Strict()
        .Client())
    .Build();

await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
{
    ContextId = "app",
    Sources = [source],
    Planner = planner,
    Functions = registry => registry.AddScalar<Utf8String, double, Classification>(
        "classify_transaction", (description, amount) => classifier.Classify(description, amount)),
});
```

The result type is read off the record. Its fields are the record's public
properties, in the order a positional record's constructor declares them and
under the names it gives them, so `Classification` is
`COMPOSITE(Category STRING, Confidence FP64)` with both fields non-nullable. A
`Nullable<T>` or `Utf8String?` property is a nullable field, and so is a
`string` property. A `Classification?` result makes the composite itself
nullable. An aggregate's `Finish` may answer a record the same way, grouped or
over a window. When the engine is created, the registered delegate's record is
checked against the declaration field by field, and a mismatch names both.

A field is `.name`, matched ignoring case like any identifier:

```sql
SELECT id,
       classify_transaction(description, amount).category   AS category,
       classify_transaction(description, amount).confidence AS confidence
FROM transactions
WHERE classify_transaction(description, amount).confidence > 0.8
ORDER BY confidence DESC
```

A composite value can be named in a sub-query and read through the alias.
There are two spellings that look right and do not work:

| Instead of | Write | Because |
|---|---|---|
| `SELECT (classify_transaction(description, amount)).* FROM transactions` | `SELECT s.c.* FROM (SELECT classify_transaction(description, amount) AS c FROM transactions) AS s` | `(…).*` does not parse. Expand the composite through an alias |
| `SELECT c.category FROM (…) AS s` | `SELECT (c).category FROM (…) AS s`, or `s.c.category` | a bare `c.category` reads `c` as a table name, and fails with *Table 'c' not found* |

Selected whole, as in `SELECT id, classify_transaction(description, amount) AS
c`, the value reaches the host as one Arrow struct column. That is a
`StructArray` whose children are named `Category` and `Confidence` and carry the
fields' nullability; the composite's own nullability is on the column. A NULL
composite is a null slot of the column. Its STRING fields arrive in the layout
`ChalkEngineOptions.Output.Strings` asks for, as every STRING column does:

```csharp
var c = (StructArray)batch.Column(1);
var category = (StringViewArray)c.Fields[0];   // utf8view by default
var confidence = (DoubleArray)c.Fields[1];
for (var i = 0; i < batch.Length; i++)
{
    if (c.IsNull(i)) continue;                 // STRICT, and the amount was NULL
    ReadOnlySpan<byte> text = category.GetBytes(i);
    double sure = confidence.GetValue(i)!.Value;
}
```

A call written more than once in one select list, or more than once in one
condition, runs once per row when its function is `Immutable()` or `Stable()`.
The two fields in the select list above cost one classification per row, not
two. The `WHERE` clause is a step of its own and classifies the rows it filters
once more. A `Volatile()` function still runs once per occurrence.

Under entitlements a composite-valued function is a client body like any other:
it is handed each column as the principal may see it, the mask or the
placeholder included, and the composite and each of its fields are reported as
disclosing what that column does. A composite-valued aggregate cannot be
allow-listed for a population-only column, because no user-defined aggregate
can.

The limits:

- A field is one of the Tier 1 types above, or a nullable form of one. A
  `byte[]` or `string` property is a nullable field. A property of any other type
  is refused at registration, naming it.
- A composite value is one level deep: a record inside a record, or a list
  inside one, is refused.
- A composite value has no ordering and no equality. Comparing one, sorting,
  grouping or partitioning by one, `DISTINCT` over one, `CAST`ing one, joining
  on one, or passing one to a built-in aggregate is refused. The message names
  the construct and the way around it, which is nearly always one of the
  composite's fields. `IS NULL` works, and `UNION ALL` carries a composite
  value.
- A composite value only ever comes out of a client-bodied function. It is never
  a parameter or a table column, a SQL-bodied or native function cannot return
  one, and SQL cannot build one: `ROW(…)` is refused.


## Entitlements — row and column disclosure

**Both layers are complete**, and the separation is structural: the
policy is an extension the request carries and a decorator the client applies, and
the core contract names no entitlement type.
A catalog that carries an
entitlement is rewritten, refused where a disclosure forbids the statement,
reported on, and enforced whether the table is a POCO collection or a table in a
database on the other side of a network — and the shipped tenancy package
compiles a role model down to that descriptor.

**A table with no tenancy column of its own.** A chat message belongs to a thread, a
line item to an order, a comment to a post — and the row itself says nothing about
who may read it. `Restriction.Through("thread_id")` declares the relationship once
and reads the parent and its key off the declared foreign key; the planner compiles
it into one join against the parent's own entitled scan, and what the child's columns
disclose is decided on the *parent's* side of that join, once per parent row rather
than once per child row. Where the parent's whole chain is visible over a NOT NULL
declared key the join is left out altogether and the visibility is ALL. Tutorial
chapter 11 is three principals reading one statement over such a table.

**Binding at execution, for a host with many principals.** Prepare with
`context.Shape()` — the names, kinds and types of the bindings and none of their
values — and the planner folds nothing: the tenancy predicate becomes a bound
relation, each `@ctx` scalar a parameter carrying its own name, and two principals
of the same shape share one plan and one digest.
`engine.ExecuteAsync(query, context)` then binds the values, and a context of
another shape is refused before anything runs.

**A sibling column saying what each row disclosed.** The per-column report is one
label for the whole result, which is all a caller needs until a principal is a
manager in one tenancy and an analyst in another and the label is `PerRow`. Set
`IncludeDisclosureColumns` and every column with an entitled origin is joined by
`<name>__disclosure`, a STRING holding `FULL`, `MASKED`, `AGGREGATE` or `REDACTED`
for that row — the suffix is configurable, and one that collides with a name the
statement already produces is refused at prepare rather than renamed.

**One policy, two members, for a column that discloses nothing.**
`Redaction.StarExpansion` says what happens to one a `SELECT *` surfaced — a
placeholder, omitted, or refused — and `Redaction.NamedColumns` what happens to
one the statement named itself, which is a placeholder unless the host would
rather be told and says `Refuse`. Dropping a named column is not on offer: that
would be a silent failure.

Row and column disclosure is an orthogonal layer. The core knows one small
vocabulary — a per-table *entitlement* of a row predicate and per-column
*disclosure rules* — and nothing about tenancies, roles or realms; a policy model
is a compiler down to that vocabulary; and enforcement is one rewrite at the
logical level, above every source and below every planner rule, so POCO, ADO,
DuckDB and federation are covered identically.

**The way in is a decorator.** `engine.WithEntitlements(options)` wraps an engine
so every statement prepared through it carries the policy, and hands back what
the policy did:

```csharp
await using var engine = await ChalkEngine.CreateAsync(options);
var entitled = engine.WithEntitlements(new EntitlementsOptions(), audit: null);

var prepared = await entitled.PrepareAsync(
    "SELECT id, first_name FROM members ORDER BY id", entitlements.Bind(principal));

Console.WriteLine(prepared.Columns[1].Disclosure);        // Full, Masked, Redacted, …
Console.WriteLine(prepared.Entitlements.Tables[0].Visibility);
await using var run = await engine.ExecuteAsync(prepared);
```

Everything entitlement-shaped lives in the `Chalk.Entitlements` package, and
nothing of it is on `ChalkEngine`, `PrepareOptions` or `PreparedQuery`: the
options travel as one `google.protobuf.Any` in the request's single extension
slot, and the report and the explanation come back in the response's. A host that
does not reference the package **cannot construct** an entitlement message, and
the sidecar refuses an extension it has no handler for rather than ignoring it —
a policy silently dropped would be a policy not enforced.

It is therefore free when unused by construction as well as by measurement: a
table without an entitlement adds no bytes to the catalog, the pass is installed
only when the registered catalog carries one — its absence is visible in the
planner's stage list — and the same statements over the same tables without the
descriptors plan to the same bytes and the same digest. `RequestContext` and
`PrepareAsync(sql, context)` stay on the core engine, because named bound values
are a core facility: a statement may say `org_id IN (@ctx.my_orgs)` with no policy
anywhere in sight.

A context need not be all or nothing. A name may be bound as a **value**, which
folds into the plan, or left as a **shape** — a kind and a type and no value —
which becomes a parameter or a bound table and is bound per execution. Fold a
tenant's grants and leave the principal open and the plan is the *tenant's*: one
plan, one digest, shared by every principal of that tenant, with the tenancy in
the leaf where a source can be asked for it.

A plan that left something open can be told more later.
`PreparedQuery.NarrowAsync(more)` returns the plan `PrepareAsync(sql, union)`
would have given — the same plan and the same digest, because it *is* the union;
a value the base already folded is refused naming it, since that value is in the
leaf and in the digest. What narrowing buys is that the sidecar may still hold the
base plan's converted tree and start from there, which the stage list says as
`narrowed from <digest>`; a sidecar that no longer holds one plans from SQL and
produces exactly the same plan.

`docs/tutorial.md` chapter 13 is the three tiers side by side over a DuckDB
database, with the digests, the stage lists, the rows and — because the tables are
in a real source — the SQL each tier asks it for.

### The guarantees

- **The descriptor.** `TableDescriptor.Entitlement` carries a row predicate and,
  per column, an ordered list of `DisclosureRule`s (first match wins, each with
  an optional mask), an `Otherwise` default, a mask, a placeholder, a
  group-size floor and the population aggregates permitted over the column.
  Conditions and masks are SQL text in Chalk's own dialect, which any host
  language can produce and which is readable in plan text and in an audit log.
  The descriptor has a content hash, so a changed policy is a different plan by
  construction, and it is validated at registration — a mask that no rule could
  reach, an aggregate that reports an individual row rather than a population, a
  column that defaults to anything but `None` on a table whose rows are filtered.
- **Context binding.** `PrepareAsync(sql, context)` takes a `RequestContext`
  of named scalars, lists and relations, which a policy's SQL refers to as
  `@ctx.<name>`. The planner folds it at prepare: a scalar becomes a literal, a
  small list becomes an `IN` list, an empty list makes its membership test
  `FALSE`, and a list too large to fold stays a relation the executor
  materialises — so its rows are never in the plan, the digest or a cache key.
- **The rewrite.** Every scan of an entitled table becomes a projection of
  per-column sanitisers over the folded row predicate over the scan, and nothing
  else in the tree changes. A predicate, a join key, a grouping, an ordering or a
  window on a masked column therefore operates on the *masked* value: an agent's
  `ORDER BY last_name` sorts by initials rather than returning nothing. The
  folded rules are simplified under the leaf's own conjuncts, so a principal who
  is an agent in the one organisation they can see gets the bare mask rather than
  a `CASE` over an identifier the filter has already fixed.
- **Population-only columns.** A column an auditor may aggregate but not read is
  followed upward to every consumer; unless every one of them is an allow-listed
  population aggregate taking it as a bare reference, the statement is refused
  naming the table, the column and the use. Where the host asks for a floor, each
  consuming aggregate is guarded: `CASE WHEN COUNT(c) >= k THEN agg ELSE NULL END`.
  There is no shipped floor — a catalog with population-only columns and no
  small-cell rule pays nothing — and a host that wants one gives it at planning
  through `PrepareOptions.DefaultMinGroupSize` or per column on the descriptor.
- **Statistical columns.** A host may decide that one column is protected by
  *query-set-size control* rather than by a mask: `ColumnEntitlement.Statistical`.
  The raw value then reaches predicates, join conditions, an aggregate's `FILTER`
  and grouping keys — but only in a statement whose every output column is an
  aggregate or a group key, with no window anywhere, and with no individual
  pinned or excluded (a comparison on, or a grouping by, a declared unique key is
  refused). A group below the floor is then **dropped** rather than NULLed,
  because with a raw grouping key the key itself is the disclosure. Outside such
  a statement the opt-in simply does not apply and the column keeps its mask.
- **Two kernels.** `FINGERPRINT(value, key)` is a keyed, stable pseudonym —
  HMAC-SHA-256, the first sixteen bytes as hex — so an equality join on tokens
  matches masked rows across tenancies without either value being disclosed, and
  a host can fingerprint what a user supplied and compare it against what a masked
  read returned. `PRESENT(value)` says whether there was a value at all, which a
  constant mask otherwise hides. Both are ordinary scalar functions, allocation-free
  per batch, and neither has a mapping in any dialect, so neither ever pushes.
- **What the caller is told.** `EntitledQuery.Columns[i].Disclosure` — the meet
  over the column's origins — and the same name as Arrow field metadata
  `chalk.disclosure` on every batch; per entitled table the descriptor hash,
  whether the row predicate reached the source and how much of the table this
  principal holds any grant on. An `IEntitlementsAudit` passed to
  `WithEntitlements` observes each execution with the digest, the hashes, the
  context lists' row counts and the host's `Purpose` and `Actor` — and no value.
- **Fail closed, twice.** A four-clause taint check proves the model for every
  plan rather than asserting it: every entitled scan is one the pass wrapped, a
  raw population-only value reaches only a permitted aggregate, the row predicate
  survives to each leaf's consumer, and no output column is a redacted column
  itself. A failure is a refusal *and* a bug report, and says so. Then the client
  re-establishes the same thing over the plan it received, from its **own**
  catalog and not from the planner's claim (`I-IR-E`): every read of a table it
  entitled went through the rewrite and carries one verdict per column, a
  population-only value reaches only an aggregate, no root column is a masked or
  redacted column itself, and every output column's label is recomputed from the
  reads and compared with the report's. It covers *escapes*; misclassification is
  the corpus's job and the package's `Reconcile`.
- **The oracle.** `entitled.ExplainAsync(sql, context)` says what this
  principal's policy resolves to without executing anything: per table the folded
  row predicate, what pushed and what stayed local; per column the folded
  disclosure — a name where the fold settled it, the conditions where it varies by
  row — the mask, the stand-in and the floor. Beside it the report says when the
  statement asked for a tenancy *outside* the principal's scope, naming the
  column: an empty result and an empty table read the same, and this is the
  difference.

### The tenancy package

`Chalk.Entitlements.Tenancy` is layer B: the model a host actually thinks in,
and a compiler down to the descriptor above. The core knows none of its words.

Every name enters once and comes back as a *handle* — a kind, a role, a realm, a
table, a column — and every later mention is that handle. Nothing is generic over
a row type: a host that maps its own configuration to a policy at run time has no
row type to name, and that path is the primary one.

```csharp
var policy = TenancyPolicy.Declare(catalog);
var app = policy.Source("main");
var org = policy.Tenancy("org");
var member = policy.Subject("member", within: [org]);
var pii = policy.Realm("pii");
var manager = policy.Role("manager");
var agent = policy.Role("agent");
var auditor = policy.Role("auditor");

var members = app.Table("members");
members
    .Tenancy(r => r
        .Direct(org, members.Column("org_id"))
        .Direct(member, members.Column("id")))
    .Realm(pii, members.Column("first_name"), members.Column("last_name"))
    .Access(new AccessRule { Roles = [manager], Realm = pii, Grants = Verdict.Full })
    .Access(new AccessRule
    {
        Roles = [agent], Realm = pii, Grants = Verdict.Mask,
        Mask = Sql.Of("lambda v: SUBSTRING(v, 1, 1)"),
    });

var orders = app.Table("orders");
orders.Tenancy(r => r
    // Tenanted through the declared foreign key, not by naming a column twice.
    .Direct(org, orders.Column("member.org"))
    // The row's owner sees it wherever it is — the fail-safe, not a grant.
    .ResourceOwner(orders.Column("created_by")));

var entitlements = policy.Compile(catalog);
var context = entitlements.Bind(new TenancyPrincipal
{
    User = 42,
    MaskKey = key,
    Grants = [Grant.ForTenancy(org, 1, manager), Grant.ForTenancy(org, 2, agent)],
});
```

A table is obtained from a `Source` and from nowhere else, because two sources may
hold a table of one name; a column is obtained from its table, so a column of the
wrong table is refused where it is used rather than looked up by spelling. Those
two, and `Sql.Of` for the places a policy carries an expression rather than a
name, are the only places a string enters at all.

A table is `Unrestricted()` or restricted through `Tenancy(t => …)`, which takes
one or more *restrictions* OR-ed together: `Direct` on a column of the row,
`Inherited` and `Related` down and up a path of declared foreign keys, a
`Predicate` of the host's own SQL over the bound context kept verbatim, and
`ResourceOwner`. Unrestricted means every row is visible — the column rules still
apply, so a reference table with one sensitive column belongs here too.

Where a path has to leave its source, `Column.References(Column)` declares the
association a foreign key cannot state, because a foreign key is one source's
claim about a table of *its own* schema. It travels on the catalog, and a step
resolves through it exactly as through a foreign key — declared, never verified,
which is the standing a declared foreign key already has.

Four kinds of grant, and no fifth: `Grant.ForTenancy` on a container,
`Grant.ForSubject` on an individual — confined to one tenancy or to
`Tenancy.Anywhere`, and never built from a request payload — `Grant.Global`,
refused at bind unless the policy opts in, and the resource-owner fail-safe, which
is not a grant at all but a row the principal owns.

**The list is the priority.** Rules are read in the order you wrote them; a rule
matches when the row's tenancy holds one of its roles and its `When` holds; and
`StopOnMatch` — true by default — ends the reading there. So the answer is the
first matching stop-rule, else the last matching continue-rule, else nothing.
Precedence is position, and there is no combination rule behind it to argue with:

```csharp
// Read top to bottom. The first rule that matches is the answer.
.Access(new AccessRule { Roles = [auditor], Realm = pii, Grants = Verdict.None, Placeholder = Sql.Of("'withheld'") })
.Access(new AccessRule { Roles = [analyst], Realm = pii, Grants = Verdict.Full, When = Sql.Of("role = 'analyst'") })
.Access(new AccessRule { Roles = [analyst], Realm = pii, Grants = Verdict.Mask, Mask = Sql.Of("lambda v: SUBSTRING(v, 1, 1)") })
.Access(new AccessRule { Roles = [ops], Grants = Verdict.Full, StopOnMatch = false })
```

An auditor sees nothing of `pii` whatever else they are, because that rule is
first — and `Placeholder` is what stands in its place for *that* rule, over the
column's own stand-in and over the request's placeholder policy. It is the
redaction's counterpart to a mask, and the two are opposites in a plan: a mask is
a value a statement may compare, and a placeholder stands where there is no value
to compare at all, so the column is still reported `Redacted`. An analyst sees
another analyst's name in full and everyone else's as an
initial, because `When` is an ordinary boolean over the row. The mask is a
*template*: `lambda v: …` is instantiated once per column of the realm with the
parameter replaced by that column's name, so one rule covers a realm of ten. And
the last rule names neither realm nor column, so it is about every protected
column — the explicit permissive form for a role meant to see everything a policy
protects — and it is a continue-rule, so it is what `ops` falls through to rather
than something that overrides the rules above.

**Silence grants nothing.** On a *protected* column — one in a realm, or one any
rule names — a role no rule speaks for gets nothing, and a column in no realm
that no rule names is not protected at all and keeps the table's default.
Non-interactive work runs a catalog built *without* the policy; there is no
privileged profile to acquire.

**A mask reads its own column, and nothing else protected.** The sanitiser is
evaluated over the raw row, so a mask that read another realm's column would put
that column's raw value inside this one and hand it to a principal entitled to
neither. Refused, naming both, by the compiler and again at registration. A rule
*condition* may read anything: what it discloses is one bit, and you chose it.

The compiler emits only membership tests over bound lists and scalar
comparisons, so everything folds natively and nothing needs a round trip. A
foreign-key path is *proved* against the declared keys and must end at a column
the entitled table carries — a tenancy that could only be answered by joining the
parent is refused naming what would be needed, rather than compiled into an N+1.
`entitlements.Reconcile(prepared, principal)` then recomputes, from the grants
alone and with no plan involved, what every column of every entitled read must
disclose, and returns the differences: the taint check proves nothing escaped,
and this is the half that proves nothing was misclassified. Beside them it gives a
three-valued verdict per table on how much of it the principal can see — `Agrees`,
`Disagrees`, or `Indeterminate` for a host predicate outside the small grammar it
reads, because a guess that happened to agree would be worse than no answer.

### Pushdown and locality

Enforcement on the tenancy column has to reach the source, or a table of any size
is intractable: `Filter_R` sits directly on the scan, below every mask, so it is
the first thing the pushdown rules meet, and a folded tenant set travels as an
`IN` list in the source's own dialect. `prepared.Entitlements.Tables[i].RowPredicatePushed`
says whether it did, honestly — it is false for an in-process table, for a source
that does not declare the shape, and under the settings below. A filter the source
can take only part of is split: the tenancy conjunct goes and a client-bodied
predicate beside it stays here, where before it would have held the tenancy
predicate back and fetched the table.

**Masks do not travel.** An expression that is not a bare column reference and
that reads a column this principal is not disclosed plainly never enters a
source's convention. A manager's `UPPER(first_name) = 'T'` pushes, because they see
the column in full; an agent's becomes `SUBSTRING(first_name, 1, 1) LIKE …` and is
evaluated here, over the tenancy's rows the pushed predicate already returned. A
mask travels only where the table declares `PushMasks` *and* the source declares
`SupportsMaskPushdown`, and even then it is an optimisation the cost model may
decline; the answer is the same either way. Where it does travel, the **whole**
sanitiser goes — `CASE WHEN … THEN note WHEN … THEN '********' ELSE NULL END` is
computed in the database, so the raw value of a row this principal may not read
never leaves it. That needs a third thing, which every source has unless it says
otherwise: `SupportsCase`, false only for a source that cannot evaluate a
conditional at all.

Three settings, per table or per source:

| Setting | What it does |
|---|---|
| `Enforcement.Pushdown` (default) | The predicate travels where the source can take it, and the report says whether it did. |
| `Enforcement.Local` | No predicate of the table is pushed at all; the source receives a projected scan and the tenant set never appears in another system's query log. |
| `Enforcement.PushdownRequired` | A plan that would evaluate this table's row predicate locally is refused, naming the table, the source and the shape it does not take. For a source holding every tenancy's rows, a silent full fetch is the worse failure. |
| `AdoSourceBuilder.TrustSourceRowSecurity()` | The host trusts this source's own row security: the pass emits no row predicate for its tables and **nothing else changes** — the column disclosures are still Chalk's. Chalk cannot check the claim. |

### The trust model, and the stated limit

The application developer defines the shape of every statement and the end user
supplies bound values, which cannot add a selector, a function or a join. The
rewrite therefore judges *shapes* and refuses the ones a principal's disclosure
forbids at prepare — which is why the corpus runs every statement as every
principal, since that is when a refusal shows. Non-interactive work — a batch job,
a migration — runs a catalog built *without* the policy, so nothing in the core is
bypassed and the process boundary is the audit boundary.

**Acknowledgement is bounded by the same rule.** What a caller is told may depend
on their own context and on the statement they wrote, and never on a row they
cannot see: no count of filtered rows, under either enforcement locus, because
"three rows matched but were redacted" is another tenancy's data. `visibility`,
`contradiction`, the per-column labels and the explanation are all computed from
the context and the statement alone, which is what makes them safe to hand over.

The stated limit: the group-size guard over a population-only column, and the
`Statistical` opt-in that rests on it, are **query-set-size control, not
differential privacy**. A group of *k* rows reveals its aggregate whatever those
*k* rows are; a tracker assembled across several statements out of
quasi-identifiers is not prevented, and cannot be by a floor — differential
privacy is the answer to that question, and Chalk does not implement it. A host
that turns `Statistical` on for a column is accepting exactly that trade, which
is why it is off by default and per column. One residual channel is documented
rather than closed: error text from a function that fails on some inputs, in a
shape the developer wrote, may quote a non-entitled column of an excluded row
.

## Testing

The correctness backbone is a second, deliberately naive executor
(`ExecutionOptions.Engine = Reference`): a row-at-a-time interpreter of the same
plan IR that shares no kernels and no operators with the vectorised engine. Every
corpus query runs through both and the results are compared. Set it on an engine
to run a suspect query the slow, obvious way:

```csharp
Execution = new ExecutionOptions { Engine = ExecutionEngine.Reference }
```

Plan shape is checked separately: `corpus/plans/` holds a recorded plan, its JSON
twin and its digest for every corpus query at two pushdown levels. A changed
digest in a pull request is the signal that a rule or cost change altered
planning. Re-record with `./scripts/record-plans.sh`.

A third opinion comes from DuckDB, a test-only dependency of
`Chalk.Integration.Tests`: the same fixtures are loaded into an in-memory DuckDB
and every corpus query is asked of it too. Two executors written from one design
can agree on a misreading of SQL; an engine with no stake in that design cannot.
The tests skip themselves if DuckDB's native library will not load.

### The `edge` family

`corpus/queries/edge/` is the one family that is not a feature area. Ninety queries
over two deliberately hostile six- and four-row tables — `sales`, which has a
duplicate value, a NULL, no declared collation and no unique key, and `sorted`,
which is declared ordered by a key that repeats and skips — aimed at the places a
vectorised executor breaks: ties spanning a fetch boundary, an offset landing
inside a tie, null-safe join keys, window frames that are empty for every row, set
operations over a nullable key, `UNNEST` of a NULL and of an empty list. It is
where the grafted coverage lives; `corpus/queries/edge/README.md` has the groups
and the provenance.

### Comparison modes

Most queries are compared row for row (`Ordered`), and a query with no total order
as a multiset (`Multiset`). A query whose `LIMIT` boundary falls inside a tie has
an answer that is determined everywhere except at the boundary, where only the
count is — so it opts into a third mode with a header:

```sql
-- compare: top-k-under-ties
```

`TopKUnderTies` takes the ordering, fetch and offset from the recorded plan and
checks the cardinality, the key order, that no row lies outside the boundary keys,
that every determined row is present, and that the rows at a boundary are drawn
from the group tied there. Nothing about it is written twice in the header.

### The reader type matrix

`AdoTypeMatrixTests` is the ADO.NET reader's contract, written out: twenty-one
Chalk types against five read paths — SQLite, DuckDB through the provider's
reader, through the native data-chunk copier and through the Arrow export, and
PostgreSQL. Per cell, the Arrow array type, the value as the IR stores it, and a
NULL. A `DATE` is days since the epoch with the epoch as day zero; midnight is
midnight wherever the machine stands; a `DECIMAL` keeps its scale; an empty string
is not a NULL; and a column whose provider type the declared one cannot hold is a
`SourceContractException` naming both, on every path.

## Repository layout

| Path | |
|---|---|
| `proto/` | the shared contract, compiled by both builds |
| `dotnet/` | the client: `src/`, `tests/`, `tools/`, `bench/`, `samples/` |
| `planner/` | the Calcite sidecar (Gradle, Kotlin DSL) |
| `corpus/` | queries, recorded plans and digests shared by both test suites |
| `docs/` | this guide and the tutorial |
| `scripts/` | build, test, plan recording, tool install |

## Documentation

- `docs/tutorial.md` — the tutorial: eighteen chapters over one marketplace.
- `CONTRIBUTING.md` — how to build, test and record plans.

## Licence

Apache-2.0. See `LICENSE` and `NOTICE`.
]()