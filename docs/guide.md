# ChalkQL guide

ChalkQL is a SQL query engine that runs inside your .NET application. One query can join
data from several places: your own in-memory objects, databases and other remote
sources. This is called *federated* querying.

ChalkQL has two halves:

- **The planner** decides how to run a query. It is built on Apache Calcite and runs as
  a separate Java process, called the *sidecar*.
- **The engine** runs the plan inside your .NET process. It works on a batch of values
  at a time rather than one row at a time (it is *vectorised*), and it holds data in the
  Apache Arrow format.

You can also add *entitlements*: your application's rules about what each user may see.
They cover which rows a user may read, which columns, which values only as totals, and
which tenancies (groups such as organisations) a user belongs to. ChalkQL applies them
to the query plan itself.

ChalkQL runs read-only `SELECT` statements. It has no DML (such as `INSERT`), no DDL
(such as `CREATE TABLE`) and no transactions.

The [README](../README.md) explains what ChalkQL is for and how it is deployed. This
guide covers configuration, behaviour and extension points. For a worked example that
builds up step by step, see the [tutorial](tutorial.md).

## Components

ChalkQL is split between your .NET application and the Calcite planner:

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

The two sides share exactly one contract: the plan IR in `proto/chalk/v1/*.proto`. (IR
stands for *intermediate representation*. It is the plan, written as protobuf messages.)
Both builds compile the same `proto/` directory. Generated code is not checked in.

## Requirements

|          |                                                                                            |
| -------- | ------------------------------------------------------------------------------------------ |
| .NET SDK | 10.0.1xx                                                                                   |
| JDK      | 21 — only to build and run the planner sidecar. CI uses Temurin; any JDK 21 works locally. |

On macOS, `scripts/install-tools.sh` uses Homebrew to install the JDK, Gradle, `buf`,
`protoc` and the gRPC C# plugin. An Apple Silicon Mac needs Homebrew's `protoc` and
`grpc_csharp_plugin`, because `Grpc.Tools` has no arm64 macOS `protoc`.
`Directory.Build.props` finds them automatically.

## Installing

The .NET packages are published as `ChalkQL.*`. The assemblies and namespaces inside
them keep the name `Chalk`.

```bash
dotnet add package ChalkQL
```

`ChalkQL` contains the engine, the client, the entitlements layer and the source
abstractions. It also contains the Calcite planner: `Chalk.Client.dll` embeds it as a
runnable JAR. So when the planner runs on the same machine, there is nothing else from
ChalkQL to deploy. You still need JDK 21 or newer.

The stock source adapters and their conformance tooling are in a separate package:

```bash
dotnet add package ChalkQL.Sources
```

`ChalkQL.Sources` adds the POCO, ADO.NET, DuckDB and Akade sources. It depends on the
matching version of `ChalkQL`.

In the common case, the planner runs on the same machine, and starting it needs no path
or port:

```csharp
await using var sidecar = await PlannerProcess.StartAsync();
```

The first time it is needed, `PlannerProcess` writes the embedded planner to a per-user
cache and starts it from there. The cache is *content-addressed*: each file is named
after its contents, so each version gets its own file. Later starts reuse the cached
JAR.

To run a different JAR, name it with `PlannerProcessOptions.JarPath` or the
`CHALK_PLANNER_JAR` environment variable. If the named file does not exist,
`PlannerProcess` refuses it, rather than quietly using the embedded planner instead. A
relative path is searched for in two places, and the nearest match wins:

1. from the working directory upward;
2. then from the application's directory upward.

So a path written from a checkout's root, such as
`planner/build/libs/chalk-planner-0.1.0-SNAPSHOT-all.jar`, still works for a test host
or a sample started in a directory below the root. If nothing is found, the error lists
every directory that was tried.

To move the cache, set `PlannerProcessOptions.ArtifactCacheDirectory` or
`CHALK_PLANNER_CACHE`. Otherwise ChalkQL uses the platform's normal per-user cache
folder. Loading ChalkQL writes nothing by itself. An application that only connects to a
planner managed somewhere else never writes the embedded JAR to disk.

`scripts/pack.sh` builds the packages from a checkout.
`dotnet/packaging/ChalkQL.Package/THIRD-PARTY-NOTICES.txt` lists the third-party
components bundled into the embedded planner JAR, and their licences.

## Quickstart

```bash
git clone <this repo> && cd chalk
./scripts/install-tools.sh      # macOS; on Linux install a JDK 21 and buf yourself
./scripts/build.sh              # gradle shadowJar + dotnet build
./scripts/test.sh               # java tests, .NET unit tests, then integration tests
./scripts/battery.sh            # the lot, phase by phase, with a summary at the end
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

`dotnet/samples/Chalk.Sample.Quickstart` is a runnable version of exactly this program:

```bash
./scripts/quickstart.sh
```

For a guided tour of ChalkQL, see the [tutorial](tutorial.md). It follows one
marketplace through in-process tables, federation, functions, entitlements, replanning
and live temporal queries.

Every result shown in the tutorial comes from a real run of
`dotnet/samples/Chalk.Sample.Tutorial`, and `./scripts/tutorial.sh` checks them.

### Transports

The .NET side talks to the planner with gRPC, over one of two *transports*:

- A **Unix domain socket** is a local connection through a path in the file system. It
  is the default on macOS and Linux when `PlannerProcess` starts the sidecar. ChalkQL
  picks a short, unique path under `/tmp`, so there is no port to choose and no address
  to configure.
- **TCP** is the default on other platforms. ChalkQL picks a free port on the loopback
  address.

A host can choose either transport with `PlannerProcessOptions.Transport`, and can give
its own socket path with `PlannerProcessOptions.SocketPath`.

A planner you manage yourself accepts the same transports:

```bash
java -jar chalk-planner.jar --socket /tmp/chalk.sock
# chalk-planner listening on unix:/tmp/chalk.sock

java -jar chalk-planner.jar --port 7433
# chalk-planner listening on 127.0.0.1:7433
```

To connect to a planner that is already running, use `GrpcQueryPlanner`:

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

Some settings cannot be combined:

- On one command line, `--socket` cannot be used with `--host` or `--port`.
- In one environment, `CHALK_PLANNER_SOCKET` cannot be used with `CHALK_PLANNER_HOST` or
  `CHALK_PLANNER_PORT`.

If the command line and the environment disagree, the command line wins, as it does for
every option.

The sidecar deletes its socket file when it shuts down. If it finds a leftover socket
file when it starts, it removes it. It never touches a path that a running planner is
listening on.

### Deploying the planner independently

`PlannerProcess` is for a planner that runs next to the .NET host. In production you may
prefer to run the Java sidecar on its own, even on another machine, and connect to it
with `GrpcQueryPlanner`.

You can export the exact planner JAR that matches your installed ChalkQL client. You
don't need to know how the NuGet package is laid out or what its resources are called:

```csharp
await using var output = File.Create("chalk-planner.jar");
await PlannerArtifact.CopyToAsync(output);
```

The embedded JAR's SHA-256 hash is also available, for deployment tools that want to
verify the exported JAR or name it by its contents:

```csharp
var sha256 = await PlannerArtifact.Sha256Async();
```

For example, a deployment pipeline might store it as `chalk-planner-<sha256>.jar`.
ChalkQL does not need any particular file name when the planner runs on its own. A fixed
name such as `chalk-planner.jar` is fine too, if your deployment system already versions
or hashes its files.

Copy the exported JAR to the planner's machine and start it in the usual way:

```bash
java -jar chalk-planner.jar --host <bind-address> --port 7433
```

The .NET host then connects to it directly, instead of starting a child process:

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

In this setup nothing calls `PlannerProcess`, so the .NET process never writes the
embedded planner to disk and never starts it. The embedded copy is still useful. It is
the official planner for that version of the ChalkQL client, and an easy source for
deployment tools.

### How deep a plan may nest

A plan is a tree. Each step takes its rows from the steps below it, and an expression
can hold other expressions. The client reads the plan it receives from the top down: the
parser, the validator and the compiler each go one level deeper on their thread's stack
for every level of the tree. A plan deep enough to use up the stack would crash the
whole process, and nothing can catch that.

So when you prepare a statement, the client refuses a plan that nests more deeply than a
limit, before it reads any further. The refusal is a `PlanningException`, and its inner
exception is a `PlanTooDeepException` that names the limit.

The limit is 256 levels by default. (A level here is one protobuf message; each step or
expression in a plan takes about two.) That is less than half the depth measured to run
safely. Ordinary statements come nowhere near it, and neither do entitlement policies,
however many lists they test. A statement reaches it only if it is nested by the
hundred: an expression such as `a + 1 + 1 + …` with hundreds of terms, for example.

To change the limit, set `PlanNestingLimit` where you create the planner:

```csharp
var planner = new GrpcQueryPlanner(new GrpcPlannerOptions
{
    Address = new Uri("http://planner.internal:7433"),
    PlanNestingLimit = 128,   // stricter than the default
});
```

`PlannerProcess.CreatePlanner(options)` keeps the limit you pass it, and
`RecordedPlanner` has the same setting. `GrpcQueryPlanner` refuses a limit below 1, and
there is no "unlimited" setting, because the limit is what protects the stack. Raise it
only for statements that need it, and only on a host whose threads have the stack space
to match.

### The shape of a result, before it runs

`PrepareAsync` plans and compiles a statement, and reads no rows. The `PreparedQuery` it
returns already knows the shape of the result. `OutputSchema` is an Arrow schema holding
each column's name, type and nullability. You can read it before you run the statement, or
without running it at all. Every batch carries the same schema, so a result with no rows
has the same shape as a full one.

This is what other APIs call *describing* a statement. JDBC puts it on the prepared
statement (`PreparedStatement.getMetaData()`), and so does Chalk. ADO.NET needs
`CommandBehavior.SchemaOnly` for it, because `DbCommand.Prepare()` returns nothing.

For code that works with `System.Data`, `ToDataTable()` turns a schema into an empty
`DataTable`, as `DbDataAdapter.FillSchema` does:

```csharp
var q = await engine.PrepareAsync("SELECT currency, ts, rate FROM usd_rates");
DataTable table = q.OutputSchema.ToDataTable("usd_rates");   // three columns, no rows
```

- Each column has the .NET type Chalk reads the value as: `DateOnly` for a date,
  `TimeOnly` for a time, `DateTime` for a timestamp and `DateTimeOffset` for one with a
  time zone, `TimeSpan` for a day-to-second interval, an `int` of months for a year-month
  one, and `Guid` for a UUID. `AllowDBNull` is the column's nullability.
- A `DataTable` has no nested columns. So a composite column is one column of type
  `object`, and its fields are described in `ExtendedProperties["chalk.fields"]`.
- Where two columns of the result share a name, the second is numbered, as `FillSchema`
  numbers it: `id`, then `id1`.
- Where the schema carries a column's disclosure (see
  [Entitlements](#entitlements--row-and-column-disclosure)), it is in
  `ExtendedProperties["chalk.disclosure"]` too.

A statement the entitlements refuse still has a shape, and the refusal carries it: see
[When a statement is refused](#when-a-statement-is-refused).


## Sources

A *source* is anything that can describe some tables and produce Arrow batches of their
rows. ChalkQL ships with four: POCO, ADO.NET, DuckDB and Akade. The extension point is
public, so you can write your own.

### POCO — Plain Old C# Objects

`Chalk.Sources.Poco` turns an `IEnumerable<T>` into a table. It works out the columns
from the row type. You declare keys, indexes and foreign keys with a fluent builder.

Nothing is pushed into a POCO table, because there is nothing to push *to*. The engine
reads the objects directly, so Chalk's own vectorised kernels evaluate every filter.

```csharp
var source = new PocoSourceBuilder("mem")
    .AddTable("orders", orders, t => t
        .UniqueKey(o => o.Id)
        .ForeignKey(o => o.CustomerId).References<Customer>(c => c.Id, verify: true))
    .AddTable("customers", customers, t => t.UniqueKey(c => c.Id))
    .Build();
```

`verify: true` checks, at `Build()`, that every non-NULL child key has a matching
parent. You must say `true` or `false`, because there is no default. The planner trusts
a declared key whether or not anyone checked it. Say a query joins a child to its parent
on the declared key but reads nothing from the parent, and the key is unique in the
parent. Then the key promises that the join changes nothing, so the planner *deletes*
it. If the key is wrong, the answer is wrong.

You can extend POCO indexing too. Implement `IPocoIndex<T>` to adapt an existing index
structure. Then run the test kit's `PocoIndexConformance.Verify` against it. It compares
the index with a full scan over a battery of ranges, to check that the index keeps
Chalk's rules for ranges and ordering. That way, an adapter's claims are checked rather
than trusted.

[`Chalk.Sources.Akade`](../dotnet/src/Chalk.Sources.Akade/) uses this extension point in
earnest. `AkadeSource.From` publishes an
[Akade.IndexedSet](https://github.com/akade/Akade.IndexedSet) as a table and discovers
its indexes — hash, ordered, compound and prefix — with no adapter code from the host.
Its [`README`](../dotnet/src/Chalk.Sources.Akade/) explains what each Akade index
becomes, and how to change a published set safely.

Some indexes answer prefixes rather than ranges; a trie is one example. Such an index
declares `IndexKind.Prefix`, and `WHERE name LIKE 'p%'` then becomes a lookup on it
rather than a filter. An ordered string index serves the same query as the plain range
`[p, next(p))`, with no change to the adapter at all.

An index that can also be walked backwards, from its last matching row to its first,
implements `IReversiblePocoIndex<T>`. That lets `ORDER BY ts DESC LIMIT 1` be one walk
and one row, instead of a sort.

Custom indexes follow the same snapshot rules as the rows they index. If the underlying
structure can change, do not change it while a published snapshot might still be running
a query. Instead, publish the new data and indexes through `RefreshAsync`, and reclaim
the old ones only after `SnapshotReleased`. See [Replacing the rows while queries
run](#replacing-the-rows-while-queries-run).

#### Replacing the rows while queries run

A table's rows, the indexes over them and their statistics together make one
**snapshot**. A snapshot never changes. Each scan takes the current snapshot once, when
it starts.

So replacing the rows never changes anything in place. Chalk builds the new snapshot
away from any running query, then switches to it. A query that is already running reads
all the rows it started with. The next query sees the new ones.

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

**Your one duty is never to change a collection you have given to Chalk.** Give it a new
one instead. The zero-copy paths read your own arrays, and this rule is what keeps a
running query's view of the data whole. `SnapshotReleased` tells you when a replaced
collection is no longer being read.

A table's *shape* is its columns, keys, collations, indexes and foreign keys.
(Statistics and row counts are not part of it.) A refresh that changes no table's shape
leaves every `PreparedQuery` valid, and it runs against the new rows. A schema change
makes them stale, as before (`PreparedQuery.IsStale`, `ChalkEngine.ShapeEpoch`). There
is no operation that updates or deletes rows.

### ADO.NET — a real database

`Chalk.Sources.Ado` works with any `DbProviderFactory`. Give it a connection, pick a
dialect preset, say what the source may be asked to do, and let it discover its tables:

```csharp
var source = new AdoSourceBuilder("sales", SqliteFactory.Instance, connectionString, "main")
    .Dialect(DialectProfiles.Sqlite)
    .Capabilities(AdoCapabilities.For(DialectProfiles.Sqlite))
    .DiscoverTables()
    .Build();
```

Presets ship for **SQLite**, **DuckDB**, **PostgreSQL** and **ANSI**. Each has its own
dialect subclass, and a conformance-kit run behind it.

You can also name any other Calcite `SqlDialect.DatabaseProduct`, such as `oracle`,
`mssql` or `big_query`. Case does not matter, and `-` and `_` are both accepted. This
picks that product's own stock dialect over the same profile, untuned. Nobody has run
the conformance kit against it yet; that is the host's job.

A source is described by two things:

- The **profile** says how the source writes and *evaluates* SQL: identifier quoting and
  case, string collation, where NULLs sort, decimal and timestamp precision, whether it
  may answer approximately, and the placeholder style its driver takes.
- The **capabilities** say what the source may be asked to do: which filter shapes,
  functions and aggregates it takes; whether projection, sorts, limits, grouping and
  joins can be sent to it; and limits on the number of rows pushed to it and the length
  of an `IN` list.

`PlannerInfo.Dialects` lists every dialect name a running sidecar accepts.

The PostgreSQL preset declares `StringCollation.Locale`. A PostgreSQL database takes its
default collation from its cluster's locale, so Chalk evaluates string equality, ranges
and sorts itself, unless the host says its database is `C`-collated. `LIKE` is still
pushed to PostgreSQL. PostgreSQL uses the collation to order and compare, but its `LIKE`
ignores a deterministic collation: it matches code point by code point, and it is
case-sensitive. The preset says so with `LikeMatchesCodePoints`, and the conformance kit
checks it.

**Nothing is pushed unless it is declared.** If you have only pointed Chalk at a source,
nobody has checked what it can do. So the safe reading of a descriptor that says nothing
is "just scan it". Two settings are worth knowing about:

- `.RowCounts(RowCountMode.Exact)` runs `COUNT(*)` on each table at build time. It is
  off by default, because it costs time, and the planner's estimates work without it.
- `.Options(new SourceOptions { QueryTimeout = … })` limits how long one query may run.
  A host can override it per source with `ExecutionOptions.SourceOptions`, without
  rebuilding a source it did not write.

Every failure names where it came from:

- A provider exception becomes a `SourceExecutionException` that names the source and
  the query.
- A source that runs too long causes a `SourceTimeoutException` that names the time
  limit.
- A column whose provider type does not match the catalog causes a
  `SourceContractException` that names both types.

Cancelling a query calls `DbCommand.Cancel` on the driver.

### DuckDB — the same source, without the per-row cost

`Chalk.Sources.DuckDb` is `Chalk.Sources.Ado` with a different reader underneath.
Discovery, capabilities, pushdown and the dialect profile are all the same. What changes
is how rows arrive. A `DbDataReader` hands them over one cell at a time. This reader
copies DuckDB's *data chunks* straight into the execution's memory arena:

```csharp
var warehouse = DuckDbSources.AddDuckDbSource("warehouse", "DataSource=warehouse.duckdb")
    .Capabilities(AdoCapabilities.For(DialectProfiles.DuckDb))
    .RowCounts(AdoSourceBuilder.RowCountMode.Exact)
    .DiscoverTables()
    .Build();
```

A fetched row then costs **nothing** on the managed heap, text included. Measured on a
scan of two text columns, a fetched row cost 0.012 bytes, against 3 288 for the same
query through the provider's own reader.

The native reader does not copy some result column types, such as `LIST`, `STRUCT` and
`ENUM`. A query with such a column goes back to the `DbDataReader` path before the first
chunk arrives. `Stats.SourcePaths` tells you which reader ran.

A host that already registers DuckDB through `AdoSourceBuilder` can add the reader with
`.UseNativeReader()`. A host that uses neither `DuckDbSources` nor `.UseNativeReader()`
keeps the `DbDataReader` path it has today.

The extension point is public. `IRemoteFetch` is what a driver implements if it can hand
over whole columns. A fetch that builds its own `DbCommand` binds its parameters through
`AdoParameters`. That gives the same names, and the same `DbType` for each Chalk type,
as the default reader binds, so two readers never disagree about what a parameter means.

`.UseArrowReader()` picks a second native reader instead: DuckDB's own export from
chunks to Arrow. It reads every type DuckDB can export, and it is faster on a plain
scan. It is not the default, because its batch is always DuckDB's own 2 048-row chunk,
whatever batch size you asked for.

**You choose the native library.** `Chalk.Sources.DuckDb` references `DuckDB.NET.Data`,
not `DuckDB.NET.Data.Full`. The `.Full` suffix only adds the bundled `libduckdb`
binaries, and a library package should not choose your platform's native code for you.
Add `DuckDB.NET.Bindings.Full` yourself, or your own build of the same DuckDB release.
**Keep the versions matched**, because both packages carry the same
`DuckDB.NET.Data.dll`.

### Writing your own

Implement `ISourceRuntime`. The minimum is two methods: `DescribeSchema()` for the
catalog, and `ScanAsync` for the rows. That is enough to be queried, because the engine
can do every filter, join and aggregate itself.

From there you can add more:

- `IndexLookupAsync` lets a declared index answer a point or range lookup.
- `ExecuteQueryAsync` lets Chalk push a whole subtree of the plan to the source. A
  source that speaks SQL reads `RemoteQueryRequest.QueryText`. A source that speaks
  Chalk's IR reads `RemoteQueryRequest.PushedPlan` and ignores the text, which will be
  empty.

`ScanAsync` is required even for a source that can do everything else. Every other path
is checked against it.

### Running the conformance kit

A descriptor is a set of claims, and the planner acts on them. A claim that is not true
gives a **wrong answer**, not an error. `Chalk.Sources.Conformance` is how you find out
before your users do:

```csharp
var report = await SourceConformance.RunAsync(source, new ConformanceOptions
{
    Seed = async (seed, ct) => { /* create seed.CreateTable, run seed.Inserts */ },
});

Console.WriteLine(report);          // every finding as "declared X, observed Y"
Assert.True(report.Passed);         // or SourceConformance.VerifyAsync, which throws
```

The kit asks your source each question twice: once through the pushed path, and once by
working out the answer itself from your `ScanAsync`. Then it compares the two. It also:

- runs every capability you declared, both on and off;
- probes the dialect: string collation, accent ordering, `LIKE` case and escapes, where
  NULLs sort, whether `BETWEEN` includes its ends, empty string versus NULL, integer
  division and modulus of negative numbers, and decimal and timestamp precision;
- checks that each unique key you declared really is unique.

It needs no planner and no sidecar.

If the kit's schema already exists, point it at your own tables with
`ConformanceOptions.Table`, instead of seeding.

The reports for the in-box source are checked in at `corpus/conformance/`, one for each
dialect. So if a probe starts to observe something different, it shows up as a changed
line, not as a test that quietly passes. Running the kit against SQLite found that
SQLite's `LIKE` ignores ASCII case, even though its `=` is binary and so case-sensitive.
So the in-box preset no longer claims the `LIKE` shapes for SQLite.

## Federation

Every source is a schema in one catalog, and a query may name as many of them as it
likes. `main.customers JOIN warehouse.orders JOIN metrics.bars` is one statement, one
plan and one result. Nothing in the query says where each table's rows live.

```csharp
await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
{
    ContextId = "app",
    Sources = [poco, warehouse, metrics],   // the first is the default schema
    Planner = planner,
});
```

### Four strategies, and cost chooses

A join between tables in two different sources can run in four ways. The planner lists
the ways the policy allows, and compares their costs:

| Strategy | What it does | When it wins |
|---|---|---|
| **Local** | Fetch both sides and join them here. | Both sides are small, or nothing better is possible. This fallback always exists. |
| **Lookup** | Stream the driving side. For each batch, ask the other source once, for that batch's distinct keys. | A small side against a large remote table. |
| **Broadcast** | Send the small side's rows inside the other source's query, and let that source do the join. | The same case, in one call, when the source accepts a `VALUES` relation and the keys would otherwise need several calls. |
| **Adaptive** | Plan both, and choose when the query runs, from how many distinct keys the small side *actually* has. | Whenever the small side's size is a guess — which is most of the time. That is why this is the default. |

The counters tell you what happened: `RemoteCalls`, `RowsFetched`, and
`Stats.AdaptiveDecisions`, which records the branch each adaptive join took and the key
count it decided on. The plan itself never tells you. Both branches are in it, and its
*digest* (the hash that identifies a plan) covers both. So the same statement has the
same digest, whatever the data turned out to be.

### The policy is yours

The shipped default prefers a lookup or a broadcast when one side is small compared with
the other. You can replace it without forking anything. Implement
`ICrossSourceJoinPolicy` and return a descriptor:

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

Chalk calls it when the engine is created, and again on every catalog refresh, and
passes it the catalog it is about to describe. One statement can override it with
`PrepareOptions.JoinPolicy`, which is merged over the catalog's policy field by field.
That is how you forbid a strategy for one report without changing anything else.

`LocalJoinMaxRows` is a guardrail, checked at planning. If a plan would pull more rows
than this across a source boundary into a local join, it fails **at planning**, naming
the join and the estimate. That is better than running for a minute and then answering.

### Zones: one catalog is one zone

A source keeps the queries it receives: in its statement log, its slow-query log, its
audit trail. So whatever a plan writes into a source's query can be read by whoever runs
that source. That might be another source's keys, or the groups the *principal* (the
user a query runs for) belongs to. Chalk does not try to police this inside a plan.

The model is simpler: **one catalog is one sovereign zone.** A host that serves several
zones builds one engine per zone, over that zone's sources alone. It picks the engine
for each request by its own rule. A report that needs two zones is two statements
against two engines, joined in application code. There, the seam between the zones is
visible and deliberate.

Declare the zone on each source, so the assumption is checked rather than assumed. When
the engine is created, it refuses a catalog whose sources declare two zones, or where
some declare one and others none. The error names the zones and the sources:

```csharp
var euOrders = DuckDbSources.AddDuckDbSource("orders", "DataSource=orders-eu.duckdb")
    .Zone("eu")
    .Capabilities(AdoCapabilities.For(DialectProfiles.DuckDb))
    .DiscoverTables()
    .Build();
var euCatalogue = new PocoSourceBuilder("catalogue").Zone("eu").AddTable("items", items).Build();

await using var eu = await ChalkEngine.CreateAsync(new ChalkEngineOptions
{
    ContextId = "eu",
    Sources = [euOrders, euCatalogue],      // one zone; a "us" source here is refused by name
    Planner = planner,
});

// elsewhere: the same for "us", and the host picks `eu` or `us` per request.
```

Planning never reads the zone. A catalog that declares none is planned and sent exactly
as before. What a source may learn about the *principal* is a separate question, decided
per table (see [Entitlements](#entitlements--row-and-column-disclosure)).
`Enforcement.Local` keeps a table's row filter, and the tenant set in it, out of the
source's query.

### Partitioned tables

A table's rows may live in several places. Declare where, and a scan becomes the union
of just the partitions that could hold a matching row:

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

`WHERE currency = 'EUR'` reads one partition. A query that names neither currency reads
both, at the same time. `ExecutionOptions.MaxRemoteConcurrency` limits how many fetches
run at once overall, and each source's `SourceOptions.MaxConcurrentQueries` limits them
for that source. The union promises no order, so ask for one if you need one.

### Letting a source do the truncating

A source that applies a `LIMIT` itself sends back only the rows you asked for. Every SQL
text in this section is what the source was actually sent, copied from the test that
reads it off the connection.

**Enabling it.** `AdoCapabilities.For(profile)` works out, from a source's dialect
profile, what the source may be asked to do. `SupportsSort`, `SupportsLimit` and
`SupportsOffset` are among what it turns on. The two are separate on purpose: the
*profile* says how to write SQL for this source, and the *capabilities* say what it may
be asked to do. A source built with `SourceCapabilities.None` is only ever scanned.

```csharp
var duck = DuckDbSources.AddDuckDbSource("duck", "DataSource=orders.duckdb")
    .Capabilities(AdoCapabilities.For(DialectProfiles.DuckDb))
    .DiscoverTables()
    .Build();
```

**A literal limit travels.** The source cuts the result short, and twenty-five rows
cross the boundary instead of every open order:

```sql
SELECT id, total FROM duck.orders WHERE status = 'open' ORDER BY id DESC LIMIT 25
-- SELECT "id", "total" FROM "orders" WHERE "status" = 'open'
--   ORDER BY "id" DESC NULLS FIRST LIMIT 25
```

**A parameter limit travels too, written in at each execution.** Prepare once, then
execute with one page size, and again with another. The text the source is sent carries
each execution's own number. The statement's other parameters are still bound by the
provider (`$p0` here, because that is how this dialect writes a placeholder).

```sql
SELECT id, total FROM duck.orders WHERE status = ? ORDER BY id DESC LIMIT ?
-- executed with ("open", 25):
-- SELECT "id", "total" FROM "orders" WHERE "status" = $p0
--   ORDER BY "id" DESC NULLS FIRST LIMIT 25
-- executed with ("open", 100):
-- SELECT "id", "total" FROM "orders" WHERE "status" = $p0
--   ORDER BY "id" DESC NULLS FIRST LIMIT 100
```

The number written into the text is always the value bound at execution. It is never
something the planner was told beforehand: a value hint changes a cost estimate, and is
not a value.

**Every branch gets it.** Over a `UNION ALL` or a partitioned table, the limit is copied
into each branch and written into each branch's own query. The local top-N step above
them (the one that keeps the first N rows in order) still decides the answer, so each
source only has to offer its first few candidates:

```sql
SELECT region, id FROM books.all_orders ORDER BY id LIMIT ?
-- executed with (4):
-- SELECT "region", "id" FROM "orders_north" ORDER BY "id" LIMIT 4
-- SELECT "region", "id" FROM "orders_south" ORDER BY "id" LIMIT 4
```

A limit with an `OFFSET` beside it is not copied. A branch's share would be
`offset + fetch`, which is an expression rather than a number, so the limit stays where
the statement put it.

**When it stays local.** A host can register a narrower descriptor than the derived one,
for a source it would rather not have sort or truncate for it. Over a source whose
capabilities say `SupportsLimit = false`, the same statement sends the query without a
limit. The local fetch stops pulling rows once it has enough:

```sql
SELECT id, total FROM duck.orders WHERE status = ? ORDER BY id DESC LIMIT ?
-- SELECT "id", "total" FROM "orders" WHERE "status" = $p0
```

The answer is the same, but the cost is not. Every matching row crosses the boundary,
and the ordering is done here. Know that price before you turn a capability off.

### Cancellation, failure, and what a result means

- **Cancellation reaches the fetches.** Each execution has one linked cancellation
  token. When the caller cancels, every fetch in flight is cancelled, and the enumerator
  finishes within `ExecutionOptions.CancellationGracePeriod` (five seconds by default).
  A fetch that has not stopped by then is abandoned, and the log names the source that
  would not stop.
- **A failure names its source, and is never partial.** The first source to fail cancels
  the others. The enumerator throws once, with a `SourceExecutionException` naming the
  source, and **nothing is yielded after the fault**. A query that fails produced no
  rows, not some of them.
- **There is no snapshot across sources, and Chalk does not pretend there is.** A
  federated result shows each source as it was at the moment it was read.
  `Stats.SourceFetches` records that moment for each source: the first fetch, the last,
  and the rows. A host that needs consistency across sources can see exactly what it did
  not get. Providing that consistency is a stated non-goal.

## Parameters

There are three styles of parameter, and one statement uses only one of them:

| Style | Example | Bound with |
|---|---|---|
| Positional | `WHERE symbol = ?` | `IReadOnlyList<object?>` |
| Ordinal | `WHERE ts >= $1 AND ts < $1 + …` | `IReadOnlyList<object?>`, `$1` may recur |
| Named | `WHERE symbol = @symbol` | dictionary, anonymous object or POCO |

Say a parameter always comes after `IN` or `NOT IN`, and you bind a non-string
enumerable to it. It then expands into an `IN (?, ?, …)` list, as in Dapper. An empty
list gives no rows for `IN`, and all rows for `NOT IN`.

### What a value binds to

A parameter's type comes from the statement: the column it is compared with, the
function it is passed to, or `ParameterTypes`. A value binds only if that type holds it
**exactly**. Anything else is refused by `ExecuteAsync` with an `ArgumentException`,
before anything reaches a source. The error names the parameter (`@amount`, `$2`, or its
position) and the CLR type you bound, but never the value:

| Parameter (as a refusal names it) | Binds from |
|---|---|
| `TINYINT` … `BIGINT` (`I8` … `I64`) | any integer type, and an enum as its number, when the value is in range |
| `DECIMAL(p, s)` | an integer, a `decimal`, or a `float`/`double` read as its shortest round-trip decimal (`0.1` is 0.1), when it fits `p` and `s` without rounding |
| `DOUBLE` (`FP64`) | a `double`, a `float`, an integer or a `decimal` (at the nearest double) |
| `REAL` (`FP32`) | a `float`, or any number a float holds exactly |
| `VARCHAR` (`STRING`) | a `string`, a `Utf8String` or a `char` |
| `BOOLEAN` (`BOOL`) | a `bool` |
| `DATE` | a `DateOnly`, or a `DateTime` at midnight |
| `TIME` | a `TimeOnly`, or a `TimeSpan` within one day |
| `TIMESTAMP` | a `DateTime` of kind `Local` or `Unspecified` (a wall clock), or a `DateOnly` |
| `TIMESTAMP WITH TIME ZONE` (`TIMESTAMP_TZ`) | a `DateTimeOffset`, or a `DateTime` of kind `Utc` (an instant) |
| `UUID` | a `Guid` |
| `VARBINARY` (`BINARY`) | a `byte[]` or a `ReadOnlyMemory<byte>` |

So all of these are refused:

- `3.5` for an `INTEGER` (and so is `3.0`: a type with fractions never binds to an
  integer);
- `25.555m` for a `DECIMAL(10, 2)`;
- `42` for a `VARCHAR`;
- `"2026-01-03"` for a `DATE`.

Nothing is parsed from text, and nothing is turned into text. The two kinds of timestamp
follow Npgsql 6, so a `DateTime.UtcNow` bound to a `TIMESTAMP` is refused, rather than
read as a wall-clock time. Only one thing is tolerated: time finer than the parameter's
precision is truncated, as Npgsql truncates it, because a microsecond column cannot hold
the rest.

### Your own types

A host type, such as a strongly typed identifier or a money type, binds through a
`BindingConverter` registered on the engine. Chalk:

1. picks the converter by the value's exact runtime type;
2. asks it for the type the value is bound to;
3. holds what it returns to the rule above.

So a converter widens what may be bound, but it cannot make a lossy conversion silent:

```csharp
readonly record struct OrderId(long Value);

sealed class GuidText : BindingConverter<string>
{
    // A 36-character string where a UUID is wanted; left alone everywhere else.
    public override bool TryConvert(string value, ChalkType target, out object? converted)
    {
        converted = target.Kind == TypeKind.Uuid && value.Length == 36 && Guid.TryParse(value, out var id) ? id : null;
        return converted is not null;
    }
}

await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
{
    Sources = sources,
    Planner = planner,
    BindingConverters = [BindingConverter.From<OrderId>(id => id.Value), new GuidText()],
});

await using var execution = await engine.ExecuteAsync(query, [new OrderId(42)]);
```

Converters apply wherever the target type is known: a statement's parameters, and every
context value whose type is stated or declared by a shape. A value whose type is read
off the value itself is not converted, because there is no target type to convert it to.
A parameter value hint is one example, and a context value with no stated type is
another. State the type instead.

You may register one converter per type, and the type must be concrete. An interface, an
abstract class or `int?` would never be chosen. The engine refuses anything else when it
is created.

A `LIKE` pattern may be a parameter. Its value is the pattern exactly as ADO.NET and
Dapper pass it: `%` and `_` in it are wildcards, and there is no escape character unless
the statement names one with `ESCAPE`. Any value is answered, on every source:

- Where an index serves the lookup, the index is asked for the value's literal start,
  and the rest of the pattern is checked on the rows it reads.
- Elsewhere, the pattern is compiled once per execution.

A value that is malformed under the statement's escape (see [`LIKE`](#like)) is refused
by `ExecuteAsync` with an `ArgumentException` naming the parameter, before anything
reaches a source. To match user input literally, name an escape and escape the input:

```csharp
var query = await engine.PrepareAsync("SELECT id FROM items WHERE sku LIKE ? ESCAPE '\\'");
var pattern = input.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
await using var execution = await engine.ExecuteAsync(query, [pattern]);
```

## UTF-8 strings

Inside Chalk, all text is already UTF-8. A STRING column is Arrow UTF-8 data in the
engine's memory arena, and a kernel reads each value as bytes. `Utf8String` lets a host
work with those bytes directly, with no .NET `string` in between. It is a readonly
struct over `ReadOnlyMemory<byte>`. Its equality, hashing and ordering compare **raw
bytes** (*ordinal byte* order). That is the same order as Chalk's binary collation and
as Arrow's:

```csharp
public sealed record Symbol(Utf8String Ticker, Utf8String? Label, long Rank);

// Reading a result without decoding anything you did not ask to decode. The convention is the
// span: GetUtf8 gives one cell as a ReadOnlySpan<byte> over the batch's own buffer, whichever
// layout the column arrived in, and the compiler keeps it inside the batch's lifetime.
var watched = new Dictionary<string, double>(Utf8StringComparer.ForString) { ["BTCUSDT"] = 0.4 };
var byBytes = watched.GetAlternateLookup<ReadOnlySpan<byte>>();

await foreach (var batch in engine.QueryAsync("SELECT ticker FROM symbols"))
{
    var column = batch.Column(0);
    for (var i = 0; i < batch.Length; i++)
    {
        ReadOnlySpan<byte> ticker = column.GetUtf8(i);            // no copy, no string
        if (byBytes.TryGetValue(ticker, out var weight)) { … }   // a string-keyed dictionary, asked in bytes
        if (ticker.TextEquals("ETHUSDT")) { … }                  // compared with a string, without making one
    }
}
```

It applies in four places. Everywhere it does, `string` still works, and still costs
what it always did:

- **A POCO property** of type `Utf8String` or `Utf8String?` maps to STRING. Its bytes
  are copied, not converted. `byte[]` and `ReadOnlyMemory<byte>` still map to BINARY:
  the `Utf8String` type is what says "this is text". Such a column is binary-collated,
  so keys, indexes, foreign keys and the build-time checks all compare bytes.
- **A Tier 1 user function** (see [Functions](#functions)) takes a STRING as a
  `ReadOnlySpan<byte>`. These are the value's own bytes, lent for the call, and the
  compiler keeps them inside it. The function may return a `ReadOnlySpan<byte>` too — a
  slice of its input, or of a buffer it owns — which is copied into the result column
  before the next call. Or it may return a `Utf8String` over its own memory. A delegate
  is never handed a `Utf8String` over the engine's memory. Registering one for a
  parameter is refused when the engine is created, because the function could keep that
  value past the call, and the engine reuses the memory. A span has no NULL, so a
  non-strict function, which sees every NULL, takes a STRING parameter as `string?`.
- **Reading a result.** `GetUtf8(int)` gives a cell as a `ReadOnlySpan<byte>`, on any
  Arrow string array. That covers the view layout a STRING column arrives in by default,
  the classic layout a host can ask for with `ChalkEngineOptions.Output.Strings`, and
  the large layout a host's own batch may carry. `TryGetUtf8` also says whether the cell
  was NULL. A batch never hands out a borrowed `Utf8String`: the span cannot outlive the
  batch, and `ToUtf8String()` on it makes a copy you can keep. Inside a Tier 2 kernel,
  the same thing is `ColumnView.VarValue(row)`, which is a span too.
- **Looking a result up.** `Utf8StringComparer.ForString` (for a dictionary keyed by
  `string`) and `Utf8StringComparer.Ordinal` (for one keyed by `Utf8String`) support
  .NET's alternate-key lookups. So the collection's
  `GetAlternateLookup<ReadOnlySpan<byte>>()` can look up a batch's bytes without making
  a string, and without allocating anything. A `string` key costs a conversion on every
  insert and on every lookup by `string`; that is the price of meeting bytes. A lookup
  by bytes costs nothing. `TextEquals` compares a span with a `string` in the same way,
  and `Utf8Hash` is the hash that every form shares.

**The lifetime rule.** A span from `GetUtf8` cannot outlive its batch, and a span handed
to a delegate cannot outlive the call. The compiler refuses to store or capture either
one. That is why a batch hands out bytes and never a borrowed `Utf8String`, and why a
delegate is never handed one. A borrowed `Utf8String` could be kept after its buffer was
reused, and then read again. No `Utf8String` you meet is borrowed. One you construct
yourself, copy out of a span with `ToUtf8String()`, or read back from a composite owns
its memory, and outlives everything.

`ToString()` is the one place a .NET string is made, and you are the one who calls it.
`Utf8String` deliberately does not do two things:

- It is not culture-aware. Casing, culture collations and `LIKE` with non-ASCII case
  folding stay on the string kernels, which decode text when they must.
- It does not validate when it is constructed. Bytes are checked with `Utf8.IsValid`
  exactly where they enter a column. So an Arrow buffer never holds invalid UTF-8, and
  nothing later pays to check again.

One sharp edge: `Utf8String` converts implicitly from `byte[]`. So a `null` in the other
branch of a conditional binds to *that* conversion, and
`Utf8String? x = flag ? value : null` is never null. Wherever a value may be absent,
write the NULL out: `Utf8String? x = default; if (flag) { x = value; }`.

## Identifiers

Unquoted identifiers keep their case, but match regardless of case. Double quotes make
an identifier exact. So `SELECT Symbol FROM Bars` finds a `symbol` column and names the
output field `Symbol`, while `"symbol"` matches exactly.

One trap: Calcite reserves a few SQL keywords that are ordinary .NET property names —
**`OPEN` and `CLOSE` in particular**. A POCO with `Open` and `Close` properties needs
them quoted:

```sql
SELECT symbol, ts, "close" - "open" AS change FROM bars
```

## SQL dialect

By default, statements are standard SQL. Some familiar non-standard forms need a dialect
that allows them: `!=` for `<>`, `%` for `MOD`, `GROUP BY` on a `SELECT` alias, and
`OFFSET` before `LIMIT`. A statement asks for one like this:

```csharp
await engine.PrepareAsync(
    "SELECT currency, COUNT(*) AS n FROM usd_rates WHERE currency != 'EUR' GROUP BY currency",
    new PrepareOptions { Conformance = SqlConformance.Lenient });
```

`PrepareOptions` is a record. So you make a variant from a base with `with`, instead of
copying it field by field, and an option added later is carried along:

```csharp
var reporting = new PrepareOptions { IncludePlanText = true, IncludeRedactedSql = true };
var lenient = reporting with { Conformance = SqlConformance.Lenient };
```

`SqlConformance` names a Calcite *conformance level*: a set of rules for which SQL is
accepted. It has a member for each level the sidecar's Calcite has: `Default`,
`Lenient`, `Babel`, `Strict92`, `Strict99`, `Pragmatic99`, `Strict2003`,
`Pragmatic2003`, `MySql5`, `Oracle10`, `Oracle12`, `SqlServer2008`, `Presto`,
`BigQuery`. You choose it per statement, so one engine can serve queries written to
different rules. Stricter levels reject more, not less. For example, SQL:2003 requires a
`FROM` clause, so `SELECT 1` fails under `Strict2003` and plans under `Default`.

`SqlConformance` and `SqlLibrary` (both in `Chalk.Catalog`) are names, not numbers.
`SqlConformance.Lenient.Name` is `"LENIENT"`, Calcite's own constant, and that name is
what travels to the sidecar. If a newer Calcite adds a level or a library before ChalkQL
has a member for it, you can still use it, as `SqlConformance.Named("…")` spelled the
way Calcite spells it. `engine.PlannerInfo.Conformances` and `.Libraries` list what the
sidecar has. A name it does not have is refused: at prepare for a statement, and when
the engine is created for a source's `DialectProfile`.

Identifier quoting and case sensitivity are *not* part of the dialect. They are the same
under every level, `Babel` included (see [Identifiers](#identifiers)).

### `LIKE`

`string LIKE pattern [ESCAPE escape]` follows the SQL standard, which is also what
Calcite does:

- `%` matches any run of characters, and `_` matches exactly one. (A character is a code
  point.)
- Matching is case-sensitive.
- A `NULL` on either side gives `NULL`.
- There is no escape character unless `ESCAPE` names one. So a backslash is an ordinary
  character in `LIKE 'KB\_%'`.

An escape is exactly one character, and in the pattern it must be followed by `%`, `_`
or itself. Anything else is refused rather than guessed at: a pattern that ends with its
escape, an escape before an ordinary character, `ESCAPE '!!'`, or `ESCAPE ''`. A literal
is refused when the statement is prepared, as a validation error with its position. A
parameter's value is refused when the statement is executed. The escape itself must be a
literal. PostgreSQL and DuckDB each accept some of these, and they disagree about what
they mean. Refusing them is what keeps the answer the same wherever the `LIKE` runs.

The result does not depend on where the table lives. When a statement names no escape, a
`LIKE` pushed to PostgreSQL is written with `ESCAPE ''`, because PostgreSQL's default
escape is a backslash. DuckDB has no default escape, like Chalk. A bare prefix — a
pattern's literal start and one trailing `%` — is a lookup on an ordered or prefix
index, with or without an `ESCAPE` clause.

`string ILIKE pattern [ESCAPE escape]` (and `NOT ILIKE`) is `LIKE` that ignores case. It
folds case the way `LOWER` does: culture-invariant, one character to one character. So
`x ILIKE p` is `LOWER(x) LIKE p`, with `p`'s literal characters lowered too. For
example:

- `É` matches `é`, `Σ` matches `σ`, and the Kelvin sign matches `k`;
- `ß` does not match `SS`, and the Turkish capital `İ` does not match `i`.

`%`, `_` and the escape are read as written, and the escape rules are the same as
`LIKE`'s. `ILIKE` needs no function library.

Databases disagree about case. PostgreSQL folds case by its database's locale (ASCII
only under `C`). DuckDB folds a Turkish capital that `LOWER` leaves alone. Calcite's own
runtime folds ASCII only. So Chalk always evaluates an `ILIKE` itself, over the rows a
source returns, and no index answers it, because an index is case-sensitive.

### `Babel` also changes the parser

`Babel` is the one level that swaps the parser itself. The sidecar ships Calcite's
`calcite-babel`, and uses its grammar for `Babel` and nothing else, so every other level
parses exactly as it always has. `Babel` gives you two things:

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

`SELECT * EXCLUDE (col)`, `SELECT * EXCEPT (col)` and `SELECT * REPLACE (expr AS col)`
need none of this. They are in the core parser, and work at every level, `Default`
included.

Babel's grammar also has `CREATE TABLE`, `BEGIN`, `COMMIT`, `SHOW` and similar
statements. Chalk only plans queries, so under `Babel` it refuses these by name with
`PlanErrorKind.Unsupported`, instead of parsing them and then failing somewhere
surprising.

## Planning on a budget

By default, the optimiser searches until it has nothing left to try. A host that would
rather have a good plan now can say so, per statement or once per engine:

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

Every field defaults to today's behaviour, so a prepare that sets none of them installs
no listener and samples no costs.

- The interval is a **count of rule evaluations, never a duration**. That is what makes
  a plan that stopped on convergence the same plan on every machine.
- The time budget is the one control that can give different plans on different runs. A
  budget spent before there is any complete plan gives an error rather than a plan.
- `StopToken` always returns a plan: the best complete one so far, or, if there is none
  yet, the first. It is not the call's own cancellation token. Cancelling that abandons
  the prepare and returns nothing.

A plan that was cut short passes every physical check that a full search's plan does.

The sidecar shares planning time between statements in slices, across a limited pool of
workers, so a complex query never holds up a simple one. Every statement gets its first
slice straight away. After that, `PlanningOptions.Priority` (`High`, `Normal` or `Low`;
the default is `Normal`) says which queue it moves to. So priority only matters when the
sidecar is busy. `PlanningOptions.Session` groups statements that should share a
concurrency limit, by name. You declare it inline, with no setup call:

```csharp
Planning = new PlanningOptions
{
    Priority = PlanningPriority.Low,                          // a batch report, not an interactive query
    Session = new PlanningSession { Name = "reports", MaxConcurrency = 2 },
}
```

The sidecar sets its number of workers when it starts, with `--planning-workers <n>` and
`--planning-load-factor <f>` (a fraction of the machine's processors). When you launch
it from .NET, use `PlannerProcessOptions.Workers` and `LoadFactor`. If neither is set,
it uses every available processor.

See chapter 14 of [the tutorial](tutorial.md).

## Logging a statement safely

Parameters keep values out of the SQL text. But hosts also write literal values into
SQL, and a slow-query log, a trace or an audit trail then keeps whatever was written.
Ask for a **redacted** text, and every literal becomes a keyed pseudonym: a short code
worked out from the value and a secret (the *salt*):

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

Counts that are positions rather than values are kept: `LIMIT`, `OFFSET`, a window
frame's row count, and an ordinal in `GROUP BY` or `ORDER BY`. So the statement still
shows its shape.

The same option covers `PreparedQuery.PlanText`, and the query text a
`SourceExecutionException` quotes, so no log line about the statement is only half safe.
`ChalkEngine.RedactSqlAsync(sql)` redacts text that was never prepared. Its main use is
text that failed to parse: for that, it works from the parser's stream of tokens, and
keeps no values at all.

Two things are shown by name rather than by position:

- A parameter you wrote as `@name` reads as `@name` in the redacted text and in the plan
  text, not as the positional `?` the planner saw.
- A literal that is one of the request context's bound values — say, a tenant list
  folded into a policy's filter — carries the name it was bound under, next to its
  pseudonym. It does so in the plan text, and in the query text a source failure quotes:

```
/*REDACTED-3f9a1c2e:DECIMAL @ctx.tenant_id*/
```

The label says only that the value is the one bound under that name. Three details:

- A membership list folded into one set is named as the set.
- A value the optimiser converted to another type is another value, and carries no name.
- The label is only how the value is shown. It changes neither the pseudonym nor the
  structural hash.

Know two properties before you rely on redaction:

- **Equal values match within a statement's shape, and not across shapes.** The seed is
  keyed by the statement's structure, so `WHERE currency = 'EUR'` and
  `WHERE 'EUR' = currency` give different pseudonyms for the same value. With no salt
  supplied, a random one is made per engine, so nothing matches between two processes or
  two runs. That is the cautious default.
- **A pseudonym is not anonymisation.** A value with few possible values — a boolean, a
  status code, a small integer — can be found by trying them all, by anyone who knows
  the statement's shape and holds the salt. Keep the salt secret.

## Functions

A schema declares functions the way it declares tables, and they travel with the
catalog. Declaring one moves the catalog's *epoch* (its version), and every plan made in
that epoch knows about the function. A function may not take a name a built-in already
has. That is a registration error, not a shadowing, so a query that means `UPPER` never
quietly gets somebody else's.

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

There are three kinds of implementation, and the planner treats each as what it is:

| Kind | Written as | What the planner does | Where it may run |
|---|---|---|---|
| **SQL** | `.Sql("(b - a) / a")` | Replaces the call with its body before validation, so the call is gone. A scalar body becomes its expression. An aggregate body becomes the built-in aggregates it is written with. A table body is a macro that expands into a sub-query. | Wherever the parts of its body may run |
| **Client** | `.Client()` | Nothing. The call travels by name, and is never pushed to a source or computed ahead of time. | In this process |
| **Native** | `.Native("md5")` | Writes it the way the source spells it, inside the pushed query. | In that source only. Anywhere else is a planning error that names it. |

The properties you declare change what Chalk does; they are not decoration:

- `Strict()` means NULL in, NULL out, without the body running.
- `Immutable()` (the default) means the planner may compute a call ahead of time, which
  is called *folding* it.
- `Stable()` means one value per execution when the arguments are constant.
- `Volatile()` means evaluated once per row, and never de-duplicated.
- `Increasing("t")` says an ordering survives the call. So `ORDER BY minute_of(ts)` over
  a table sorted by `ts` plans with no sort at all.
- `Cost(n)` and `Rows(n)` reach the cost model.

### Two ways to write the implementation

**Tier 1** is ordinary delegates, and it is the one to reach for. Chalk generates the
loop: it reads each value, calls the delegate, writes the result and whether it is NULL,
and skips a strict function's NULL values before the call. Nothing is allocated per row
unless the delegate allocates.

Aggregates are state machines of the same shape as PostgreSQL's: a starting state, a
step for each row, and a final result. The state and the result are fixed-width. A
STRING or BINARY input reaches one as a `ReadOnlySpan<byte>` over the value's own bytes.
So a byte total, a prefix count or a hash over text can be a Tier 1 aggregate.

An aggregate that keeps data of varying length per group — the longest label, a
concatenation, a sketch, a bitmap — is an `ArenaAggregateSpec`. Its state is still a
struct. What the struct cannot hold, it rents from the `ArenaScope` that every step
receives, and it keeps `ArenaHandle`s, which are two integers each. A STRING or BINARY
result is a span over the scope, copied into the result before the next group.

Neither form puts anything on the heap, and a state with a reference field is refused
when it is registered. The plain form first:

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

`Remove` and `Merge` are promises about arithmetic, not hints. `Remove` says the state
can be moved backwards exactly. That lets a sliding window frame be updated, instead of
worked out again from scratch. With neither, each frame is recomputed from its rows: the
same answer, with more work.

You write a Tier 1 delegate in the CLR types a POCO property maps from:

| SQL type | CLR type |
|---|---|
| BOOL, I8, I16, I32, I64, FP32, FP64 | `bool`, `sbyte`, `short`, `int`, `long`, `float`, `double` |
| STRING | `ReadOnlySpan<byte>` in; `ReadOnlySpan<byte>` or `Utf8String` out; `string` either way |
| DECIMAL of up to 28 digits | `decimal` |
| DATE, TIME | `DateOnly`, `TimeOnly` |
| TIMESTAMP, TIMESTAMP_TZ | `DateTime`, `DateTimeOffset` (in UTC) |
| INTERVAL_DAY | `TimeSpan` |
| UUID | `Guid` |
| BINARY | `ReadOnlySpan<byte>` or `byte[]` in, declared BINARY explicitly for a span; `ReadOnlySpan<byte>`, `ReadOnlyMemory<byte>` or `byte[]` out |

A function that is not strict takes the nullable form of a value type, so it can see a
NULL. `string` and `byte[]` cost an allocation per row; the other types do not.

`Parameter<T>()` and `Returns<T>()` work out the SQL type from the CLR type, as a POCO
property would: `decimal` is DECIMAL(28, 10) and `DateTime` is TIMESTAMP(9). To declare
a narrower type, use `Parameter(name, type)`, and still implement it with `decimal` or
`DateTime`. A date or time may also be written as its raw count: `int` days, or `long`
units.

Two rules of arithmetic hold everywhere a number is rounded:

- A DECIMAL result, and a cast to a DECIMAL, round a midpoint away from zero, as
  PostgreSQL, DuckDB, SQL Server and SQLite do. So a statement gives the same answer
  wherever it runs.
- `ROUND` on a double or a float rounds the exact value the number holds, not a scaled
  copy of it. So `ROUND(655.925, 2)` is `655.92`, because that double is really
  `655.92499999999995…`. The same digits as a DECIMAL round to `655.93`, because a
  decimal is exact.

The casts between DECIMAL and floating point are exact in both directions.

Some values do not fit a Tier 1 type exactly:

- A DECIMAL wider than 28 digits has no CLR type. A Tier 1 function that uses one is
  refused when the engine is created. A Tier 2 kernel can read it.
- A time the SQL type cannot hold exactly is refused, not rounded. That includes a
  `TimeOnly` or `TimeSpan` with a fraction of a microsecond, and a `DateTime` with
  sub-millisecond ticks returned for a TIMESTAMP(3).
- A nanosecond TIMESTAMP read as a `DateTime` is truncated to its 100-nanosecond tick.

A Tier 1 aggregate works on fixed-width values. Its result may be any of these types
except BINARY. Its input may be any of them, or a STRING or BINARY read as a
`ReadOnlySpan<byte>`. A text or binary result needs an `ArenaAggregateSpec`.

**Tier 2** is the expression evaluator's own contract. Use it if you need SIMD, or want
to avoid a call per value:

```csharp
[Experimental("CHALK001")]
public interface IVectorFunction
{
    FunctionSignature Signature { get; }
    void Invoke(ReadOnlySpan<ColumnView> args, ColumnWriter result, in FunctionContext context);
}
```

It is public under the `CHALK001` diagnostic ID, because it is the engine's own
interface made visible. If you opt in, it may change before the first tagged release.
Tier 1 is built on top of Tier 2, so the two cannot drift apart.

Every signature is checked against the catalog when the engine is created. A missing
implementation, or a delegate with the wrong CLR types, fails before any query runs,
naming the function and both types. The row-at-a-time reference executor calls the same
implementations. That is what makes the differential test (see [Testing](#testing)) a
test of Chalk's plumbing rather than of your arithmetic.

### Composite results

A client-bodied function can return a small record instead of a single value, and SQL
can take the record apart by field. The record is the declaration:

```csharp
public readonly record struct Classification(Utf8String Category, double Confidence);

var source = new PocoSourceBuilder("mem")
    .AddTable("transactions", transactions)
    .AddFunction("classify_transaction", f => f
        .Scalar<ReadOnlySpan<byte>, double, Classification>("description", "amount")
        .Strict()
        .Client())
    .Build();

await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
{
    ContextId = "app",
    Sources = [source],
    Planner = planner,
    Functions = registry => registry.AddScalar<ReadOnlySpan<byte>, double, Classification>(
        "classify_transaction", (description, amount) => classifier.Classify(description, amount)),
});
```

The result type is read off the record. Its fields are the record's public properties,
in the order a positional record's constructor declares them, and under the names it
gives them. So `Classification` is `COMPOSITE(Category STRING, Confidence FP64)`, with
both fields non-nullable.

- A `Nullable<T>` or `Utf8String?` property is a nullable field, and so is a `string`
  property.
- A `Classification?` result makes the composite itself nullable.
- An aggregate's `Finish` may return a record in the same way, grouped or over a window.

When the engine is created, the registered delegate's record is checked against the
declaration, field by field, and a mismatch names both. As with a parameter, a field may
be declared narrower than the record's type: a `decimal` property can serve a
DECIMAL(18, 2) field, and a `DateTime` a TIMESTAMP(6). A value the field cannot hold is
refused when it is written.

You read a field with `.name`, matched regardless of case like any identifier:

```sql
SELECT id,
       classify_transaction(description, amount).category   AS category,
       classify_transaction(description, amount).confidence AS confidence
FROM transactions
WHERE classify_transaction(description, amount).confidence > 0.8
ORDER BY confidence DESC
```

You can name a composite value in a sub-query and read it through the alias. Two
spellings look right but do not work:

| Instead of | Write | Because |
|---|---|---|
| `SELECT (classify_transaction(description, amount)).* FROM transactions` | `SELECT s.c.* FROM (SELECT classify_transaction(description, amount) AS c FROM transactions) AS s` | `(…).*` does not parse. Expand the composite through an alias |
| `SELECT c.category FROM (…) AS s` | `SELECT (c).category FROM (…) AS s`, or `s.c.category` | a bare `c.category` reads `c` as a table name, and fails with *Table 'c' not found* |

Selected whole, as in `SELECT id, classify_transaction(description, amount) AS c`, the
value reaches the host as one Arrow struct column. That is a `StructArray` whose
children are named `Category` and `Confidence`, and carry the fields' nullability. The
composite's own nullability is on the column, and a NULL composite is a null slot of the
column. Its STRING fields arrive in the layout `ChalkEngineOptions.Output.Strings` asks
for, like every STRING column:

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

Or read the column back as the record itself, a batch at a time:

```csharp
var classifications = new Classification?[batch.Length];
batch.Column(1).ReadComposites<Classification?>(classifications);   // null where the composite is
```

or one cell at a time:

```csharp
var c = batch.Column(1);
for (var i = 0; i < batch.Length; i++)
{
    if (c.TryGetComposite<Classification>(i, out var classification))
    {
        // classification.Category borrows the batch's memory; ToString() copies it.
    }
}
```

`ReadComposites<T>`, `GetComposite<T>` and `TryGetComposite<T>` (in `Chalk.Arrow`) match
`T`'s properties, or a positional record's constructor parameters, to the composite's
fields by name, ignoring case. Each field is read as a function's parameter would be, so
any Tier 1 form of the field's type works: `decimal` for a DECIMAL(18, 2), and
`DateOnly` or `int` days for a DATE.

- `T` may be a record struct, a record class, or a class with a parameterless
  constructor and settable properties.
- A field that `T` does not name is not read. A nullable field needs a member that can
  hold its NULL.
- For a NULL composite, `TryGetComposite` returns `false`. `GetComposite` and
  `ReadComposites` return null into a class or a `Classification?`. Into any other
  struct, a NULL composite is refused, naming the row.
- The binding is built on the first read of a given `T` and struct type, and then
  cached. A mismatch is refused on that first read, naming both sides.

Value fields are read from the batch's own buffers, so reading a record struct of value
fields allocates nothing. A `Utf8String`, `ReadOnlyMemory<byte>`, `string` or `byte[]`
field is a copy, allocated per row. That is because the record may be kept after the
batch, and a slice of the batch could be read again after the engine had reused its
memory. A host that wants the text without a copy reads the field's own Arrow array as a
span. A record class is one object per row.

A DECIMAL field wider than 28 digits binds to a `decimal` too, and a value that does not
fit is refused by name, at its row. `ReadComposites` sets the read up once for the whole
batch, so it is the way to read many rows.

Say the same call appears more than once in one select list, or more than once in one
condition. If its function is `Immutable()` or `Stable()`, it runs once per row. So the
two fields in the select list above cost one classification per row, not two. The
`WHERE` clause is a step of its own, and classifies the rows it filters once more. A
`Volatile()` function still runs once for each time it appears. The same holds for any
expression written more than once within one step — a built-in call, a `CAST`, a `CASE`,
an `IN` or a field access — unless something inside it is `Volatile()`.

Under entitlements (see [Entitlements](#entitlements--row-and-column-disclosure)), a
composite-valued function is a client body like any other. It is handed each column as
the principal may see it, including a mask or a placeholder. The composite, and each of
its fields, are reported as disclosing what that column discloses.

A *population-only* column is one a principal may aggregate but not read row by row. Its
allow-list may name a user-defined aggregate, including a composite-valued one, only
when the aggregate's declaration says `.Population()`:

```csharp
public readonly record struct AmountSummary(long Total, long Tally);

builder.AddFunction("amount_summary", f => f
    .Aggregate<long, AmountSummary>("amount")
    .Population()
    .Client());
```

`.Population()` is your promise that the aggregate's result describes the group as a
whole, and never one row's value — in every field of a composite result, too. A total, a
count or a mean keeps the promise. A minimum, a maximum, a first value or a list of the
values does not. Nothing can check the promise, so, like `.Leakproof()`, Chalk records
it and relies on it. An allow-list that names a user-defined aggregate without it is
refused when the engine is created, and the message says how to declare it. The
group-size floor then guards the aggregate as it guards `COUNT`: a group smaller than
the floor gives a NULL composite, the whole value, and the report says `Aggregate`.

The limits:

- A field is one of the Tier 1 types above, or a nullable form of one. A `byte[]` or
  `string` property is a nullable field. A property of any other type is refused at
  registration, naming it.
- A composite value is one level deep. A record inside a record, or a list inside one,
  is refused.
- A composite value has no ordering and no equality. Chalk refuses comparing one,
  sorting, grouping or partitioning by one, `DISTINCT` over one, `CAST`ing one, joining
  on one, and passing one to a built-in aggregate. The message names the construct, in
  the statement's own words, and the way around it, which is nearly always one of the
  composite's fields. `IS NULL` works, and `UNION ALL` carries a composite value.
- `CASE` and `COALESCE` can choose between composite values of one type (the same
  fields, named and typed alike) and a `NULL`. A choice between composites of different
  types, or between a composite and a scalar, is refused.
- A composite value comes out of a client-bodied function, or out of an in-process
  table's column (below). It is never a parameter. A SQL-bodied or native function
  cannot return one, and SQL cannot build one: `ROW(…)` is refused.

### Composite columns

A member of a POCO table whose type is a record is a composite column. Its type is
worked out exactly like a function's record result: its fields are the record's public
properties, in order and under the record's own names, and each is one of the Tier 1
types.

- A record struct member is never NULL, and its `Nullable<T>` form may be.
- A record class member follows the rule for a `string` member: it is nullable, unless
  the compiler annotates it as not null. So `Venue?` is a nullable composite.

```csharp
public readonly record struct Side(double Price, long Size);
public sealed record Venue(Utf8String Name, string? Country);
public sealed record Quote(long Id, Utf8String Symbol, Side Bid, Side? Ask, Venue? Venue);

var source = new PocoSourceBuilder("mem")
    .NamingPolicy(PocoNamingPolicy.SnakeCase)
    .AddTable("quotes", quotes, t => t
        .OrderedBy(q => q.Id)
        .UniqueKey(q => q.Id)
        .Index(q => q.Symbol))
    .Build();
```

`bid` is `COMPOSITE(Price FP64, Size I64)`, `ask` is the same made nullable, and `venue`
is a nullable `COMPOSITE(Name STRING, Country STRING?)`. The naming policy names the
columns. The fields keep the record's names, and match regardless of case like any
identifier. You read a composite column the same ways as a function's composite: whole,
by field, with `t.c.*`, and with `SELECT *`, which carries it whole.

```sql
SELECT q.id, q.ask.price AS ask, q.venue.country AS country
FROM quotes q
WHERE q.bid.price > 400 AND q.ask IS NOT NULL
ORDER BY q.ask.price DESC
```

A field can filter, order and group. The column itself is carried whole through a sort,
a join, or a lookup on another column. It reaches the host as a struct column, which
`ReadComposites<T>` reads back as the record. Each field is staged as a column of its
own type would be, so scanning a composite column allocates nothing per row beyond what
its fields would as separate columns.

The limits, on top of those for every composite value above:

- A composite column is read only from an in-process source. A `REMOTE` source that
  declares one is refused at registration, naming the table and the column. Declare its
  fields as columns of their own.
- Nothing is keyed, indexed or ordered on a composite column, or on a field of one.
  `UniqueKey`, `OrderedBy`, `ForeignKey`, `Index` or a clustered index's `Covering` that
  names a composite member, or a field of one, is refused at `Build()`, naming the
  member. A clustered index on a table with a composite column names its covering set,
  and leaves the column out.
- A composite column carries no statistics, and an override for one is refused.
- `AkadeSource` describes no index over a composite member. An Akade index keyed by the
  record is not an access path, and the column is read through the scan like any
  unindexed member.

Under entitlements, a composite column is a column, and a rule may name it — the column
as a whole, never a field. It is disclosed whole or withheld whole: `Full`, or `None`.
The placeholder for `None` is the NULL composite, so every field of a withheld value
reads as NULL. `Masked`, `AggregateOnly` and `Test` are refused at registration (for a
POCO table, at `Build()`), naming the column and the verdict, because:

- no expression builds a composite value to mask it with;
- no built-in aggregate takes one;
- a composite value has no equality to test.

A mask, a placeholder, an allow-list and the statistical opt-in are refused on one too.
A rule's condition may read a field of it:

```csharp
new ColumnEntitlementDescriptor
{
    Column = 2,   // contact, a ContactCard
    Rules =
    [
        new DisclosureRule { When = "org_id IN (@ctx.manager_orgs)", Then = Disclosure.Full },
        new DisclosureRule
        {
            When = "org_id IN (@ctx.agent_orgs) AND (contact).tier >= 2",
            Then = Disclosure.Full,
        },
    ],
    Otherwise = Disclosure.None,
}
```

The report gives the column its own disclosure, and gives a field read from it the same
one.

## Entitlements — row and column disclosure

*Entitlements* decide what each principal may see. (The *principal* is the user a query
runs for.) For every table, they answer two questions: which rows may this principal
see, and how much of each column may they read?

Entitlements come in two layers, and both are complete:

- **Layer A** is the core. It knows one small vocabulary. Each table may have an
  *entitlement*: a *row predicate* (a filter that picks the rows the principal may see)
  and, for each column, *disclosure rules*. Layer A knows nothing about tenancies, roles
  or realms.
- **Layer B** is a policy model — the model a host thinks in — with a compiler that
  turns it into layer A's vocabulary. The shipped tenancy package is one. It compiles a
  role model down to those descriptors.

Row and column disclosure is a layer of its own, separate from everything else. It is
enforced by one rewrite of the logical plan, above every source and below every planner
rule. So POCO, ADO.NET, DuckDB and federated queries are all covered in the same way.

For a catalog that carries an entitlement, Chalk:

- rewrites each statement;
- refuses a statement where a disclosure forbids it, and says what it refused and what the
  result would have been;
- reports what each column disclosed;
- enforces all of this whether the table is a POCO collection, or a table in a database
  on the other side of a network.

The separation is part of the structure. The policy is an extension that the request
carries, and a decorator that the client applies. The core contract names no entitlement
type.

**The way in is a decorator.** `engine.WithEntitlements(options)` wraps an engine. Every
statement prepared through the wrapper carries the policy, and you get back what the
policy did:

```csharp
await using var engine = await ChalkEngine.CreateAsync(options);
var entitled = engine.WithEntitlements(new EntitlementsOptions(), audit: null);

var prepared = await entitled.PrepareAsync(
    "SELECT id, first_name FROM members ORDER BY id", entitlements.Bind(principal));

Console.WriteLine(prepared.Columns[1].Disclosure);        // Full, Masked, Redacted, …
Console.WriteLine(prepared.Entitlements.Tables[0].Visibility);
await using var run = await engine.ExecuteAsync(prepared);
```

Everything to do with entitlements lives in the `Chalk.Entitlements` package. None of it
is on `ChalkEngine`, `PrepareOptions` or `PreparedQuery`. The options travel as one
`google.protobuf.Any`, in the request's single extension slot, and the report and the
explanation come back in the response's slot. A host that does not reference the package
**cannot construct** an entitlement message. The sidecar refuses an extension it has no
handler for, rather than ignoring it, because a policy that was silently dropped would
be a policy not enforced.

So entitlements cost nothing when unused, by design as well as by measurement:

- A table without an entitlement adds no bytes to the catalog.
- The entitlement pass is installed only when the registered catalog carries an
  entitlement. Its absence shows in the planner's stage list.
- The same statements over the same tables, without the descriptors, plan to the same
  bytes and the same digest.

`RequestContext` and `PrepareAsync(sql, context)` stay on the core engine, because named
bound values are a core feature. A statement may say `org_id IN (@ctx.my_orgs)` with no
policy anywhere in sight.

**A table with no tenancy column of its own.** A chat message belongs to a thread, a
line item to an order, a comment to a post — and the row itself says nothing about who
may read it. `Restriction.Through("thread_id")` declares the relationship once, and
reads the parent table and its key from the declared foreign key. The planner compiles
it into one join with the parent's own entitled scan. What the child's columns disclose
is decided on the *parent's* side of that join: once per parent row, not once per child
row. Where the parent's whole chain is visible, over a NOT NULL declared key, the join
is left out altogether, and the visibility is ALL. Tutorial chapter 11 shows three
principals running one statement over such a table.

**A column that says what each row disclosed.** The per-column report is one label for
the whole result. That is all a caller needs — until a principal is a manager in one
tenancy and an analyst in another, and the label is `PerRow`. Set
`IncludeDisclosureColumns`, and every column that comes from an entitled table is joined
by a companion column, `<name>__disclosure`. It is a STRING holding `FULL`, `MASKED`,
`AGGREGATE` or `REDACTED` for that row. You can configure the suffix. A companion whose
name collides with a name the statement already produces is refused at prepare, not
renamed.

**One policy, two members, for a column that discloses nothing:**

- `Redaction.StarExpansion` says what happens to such a column when a `SELECT *` brought
  it in: a placeholder, left out, or refused.
- `Redaction.NamedColumns` says what happens when the statement named the column itself.
  It is a placeholder, unless the host would rather be told and says `Refuse`.

Dropping a named column is not offered, because that would be a silent failure.

### Binding the context

A *request context* holds named scalars, lists and relations that a policy's SQL refers
to as `@ctx.<name>`. A context does not have to be all or nothing. Each name may be
bound in one of two ways:

- as a **value**, which the planner writes straight into the plan (it *folds* it);
- as a **shape** — a kind and a type, but no value — which becomes a parameter or a
  bound table, and is filled in at each execution.

For example, fold a tenant's grants and leave the principal open. The plan is then the
*tenant's*: one plan and one digest, shared by every principal of that tenant, with the
tenancy filter in the plan's leaf, where a source can be asked to apply it.

**One plan for many principals.** Usually you prepare a query with one principal's full
context, and the planner writes that principal's values straight into the plan. The plan
then fits those values only, so a principal with other values needs another plan. A host
with many principals can prepare once with `context.Shape()` instead. A shape lists the
context's names, and says whether each is a single value or a list and what type it has,
but it holds no values. The planner folds nothing, and the plan leaves a gap wherever a
value belongs:

- A test of whether a row's value is in a context list, such as
  `org_id IN (@ctx.manager_orgs)`, is answered at execution, from the list you bind
  then.
- Each `@ctx` scalar becomes a parameter that carries its own name.

Two principals whose contexts have the same shape share one plan and one digest.
`engine.ExecuteAsync(query, context)` then binds the values. A context of another shape
is refused before anything runs.

**Narrowing.** A plan that left something open can be told more later.
`PreparedQuery.NarrowAsync(more)` returns the plan that `PrepareAsync(sql, union)` would
have given, where `union` is the base context plus `more`. It is the same plan with the
same digest, because it *is* the union. A value the base plan already folded is refused,
naming it, because that value is already in the leaf and in the digest. What narrowing
buys is speed: the sidecar may still hold the base plan's converted tree, and start from
there. The stage list then says `narrowed from <digest>`. A sidecar that no longer holds
it plans from SQL, and produces exactly the same plan.

`docs/tutorial.md` chapter 13 shows three tiers side by side — a base plan, a tenant's
plan and one person's plan — over a DuckDB database. It shows the digests, the stage
lists, the rows and, because the tables are in a real source, the SQL each tier asks the
source for.

### The guarantees

- **The descriptor.** `TableDescriptor.Entitlement` carries:
  - a row predicate;
  - for each column, an ordered list of `DisclosureRule`s (the first match wins, and
    each may have a mask), an `Otherwise` default, a mask, a placeholder, a group-size
    floor, and the population aggregates allowed over the column.

  Conditions and masks are SQL text in Chalk's own dialect. Any host language can
  produce it, and it can be read in plan text and in an audit log. The descriptor has a
  content hash, so a changed policy is a different plan by construction. The descriptor
  is validated at registration, which refuses, for example:
  - a mask that no rule could reach;
  - an aggregate that reports one row's value rather than a population;
  - a column whose default is anything but `None`, on a table whose rows are filtered.
- **Context binding.** `PrepareAsync(sql, context)` takes a `RequestContext` of named
  scalars, lists and relations, which a policy's SQL refers to as `@ctx.<name>`. The
  planner folds it at prepare:
  - a scalar becomes a literal;
  - a small list becomes an `IN` list;
  - an empty list makes its membership test `FALSE`;
  - a list too large to fold stays a relation, which the executor loads when the query
    runs (it *materialises* it). So its rows are never in the plan, the digest or a
    cache key.

  A value whose type is stated, in `ScalarTypes` or in a binding's `ColumnTypes`, is
  held to the same rule as a statement's parameter (see [What a value binds
  to](#what-a-value-binds-to)). It is converted by the engine's `BindingConverters`. For
  example, `1.5` bound to an `I32` list is refused by `PrepareAsync` (or by
  `ExecuteAsync`, for a value bound at execution), naming the binding and the column. It
  never grants organisation 2. A value with no stated type takes its type from its CLR
  type, as before, and a `DateTime` of kind `Utc` is a `TIMESTAMP WITH TIME ZONE`.
- **The rewrite.** Every scan of an entitled table becomes three steps, one on top of
  the other: the scan; a filter holding the folded row predicate; and a projection of
  per-column *sanitisers*, the expressions that decide what each column shows. Nothing
  else in the plan changes. So a filter, a join key, a grouping, an ordering or a window
  on a masked column works on the *masked* value. For example, an agent's
  `ORDER BY last_name` sorts by initials, rather than returning nothing. The folded
  rules are also simplified using what the leaf's own filter already guarantees. So a
  principal who is an agent in the only organisation they can see gets the plain mask,
  not a `CASE` that tests an identifier the filter has already fixed.
- **Population-only columns.** Say an auditor may aggregate a column but not read it.
  Chalk follows that column upward to everything that uses it. Unless every use is an
  allow-listed population aggregate that takes the column directly, as a bare reference,
  the statement is refused, naming the table, the column and the use, and the aggregates
  the column allows (see [When a statement is refused](#when-a-statement-is-refused)). A
  user-defined
  aggregate counts only when its declaration says `.Population()` (see *Composite
  results*). Where the host asks for a floor, each aggregate that uses the column is
  guarded: `CASE WHEN COUNT(c) >= k THEN agg ELSE NULL END`. No floor ships, so a
  catalog with population-only columns and no small-group rule pays nothing. A host that
  wants a floor gives it at planning, through `PrepareOptions.DefaultMinGroupSize`, or
  per column on the descriptor.
- **Statistical columns.** A host may decide that a column is protected by
  *query-set-size control* rather than by a mask: `ColumnEntitlement.Statistical`. The
  raw value may then reach filters, join conditions, an aggregate's `FILTER`, and
  grouping keys, but only in a statement where:
  - every output column is an aggregate or a group key;
  - there is no window anywhere;
  - no individual is pinned or excluded. (A comparison on, or a grouping by, a declared
    unique key is refused.)

  A group smaller than the floor is then **dropped**, rather than set to NULL, because
  with a raw grouping key, the key itself is the disclosure. Outside such a statement,
  the opt-in does not apply, and the column keeps its mask.
- **Two kernels.**
  - `FINGERPRINT(value, key)` is a keyed, stable pseudonym: HMAC-SHA-256, with the first
    sixteen bytes as hex. So an equality join on fingerprints matches masked rows across
    tenancies without disclosing either value. A host can also fingerprint what a user
    supplied, and compare it with what a masked read returned.
  - `PRESENT(value)` says whether there was a value at all, which a constant mask would
    otherwise hide.

  Both are ordinary scalar functions that allocate nothing per batch. Neither has a
  mapping in any dialect, so neither is ever pushed to a source.
- **What the caller is told.**
  - `EntitledQuery.Columns[i].Disclosure` is the column's disclosure: the most
    restrictive of the disclosures of everything the column is computed from (their
    *meet*). The same name is on every batch, as the Arrow field metadata
    `chalk.disclosure`.
  - For each entitled table, the caller gets the descriptor hash, whether the row
    predicate reached the source, and how much of the table this principal holds any
    grant on.
  - An `IEntitlementsAudit` passed to `WithEntitlements` observes each execution. It
    sees the digest, the hashes, the row counts of the context lists, and the host's
    `Purpose` and `Actor` — and no value.
  - A statement that is refused is told what was refused and what its result would have
    been: see [When a statement is refused](#when-a-statement-is-refused).
- **Fail closed, twice.** Chalk proves the model for every plan, rather than assuming
  it.
  1. In the planner, a four-clause *taint check* proves that:
     - every entitled scan is one the pass wrapped;
     - a raw population-only value reaches only a permitted aggregate;
     - the row predicate survives to each leaf's consumer;
     - no output column is a redacted column itself.

     A failure is a refusal *and* a bug report, and the message says so.
  2. Then the client checks the same things over the plan it received, from its **own**
     catalog rather than from the planner's claims (`I-IR-E`):
     - every read of a table it entitled went through the rewrite, and carries one
       verdict per column;
     - a population-only value reaches only an aggregate;
     - no root column is a masked or redacted column itself;
     - every output column's label, worked out again from the reads, matches the
       report's.

  These checks catch *escapes*. Catching misclassification is the job of the corpus and
  of the package's `Reconcile`.
- **The oracle.** `entitled.ExplainAsync(sql, context)` says what this principal's
  policy comes to, without running anything:
  - for each table: the folded row predicate, and what was pushed and what stayed local;
  - for each column: the folded disclosure (a name where folding settled it, or the
    conditions where it varies by row), the mask, the stand-in and the floor.

  Beside it, the report says when the statement asked for a tenancy *outside* the
  principal's scope, naming the column. An empty result and an empty table look the
  same, and this is how to tell them apart.

### When a statement is refused

A refusal is an `EntitlementException`. Its message names the table, the column and the
use. Its `Refusal` property says the same things in a form your code can act on, and adds
the result the statement would have had:

```csharp
try
{
    await entitled.PrepareAsync("SELECT * FROM members", context);
}
catch (EntitlementException refused) when (refused.Refusal is { } refusal)
{
    // PopulationOnly  main.members.national_id  a projection to the result  COUNT
    Console.WriteLine(
        $"{refusal.Reason}  {refusal.Table}.{refusal.Column}  {refusal.Use}  "
        + string.Join(", ", refusal.Permitted));

    // The columns the statement would have returned, in the form OutputSchema takes.
    DataTable shape = refusal.OutputSchema?.ToDataTable() ?? new DataTable();
}
```

- **`Reason`** says what kind of refusal it is. `PopulationOnly` means a column the
  principal may only aggregate was used some other way, and `Permitted` then names the
  aggregates it allows, such as `COUNT`. The others include `Statistical`, `Redacted` (a
  column that discloses nothing, under a redaction policy that refuses it), `Star`,
  `NoVisibleRows`, `PushdownRequired`, `NoContext` and `InvalidEntitlement`. `Internal`
  means the planner caught itself in a bug, which is worth reporting.
- **`OutputSchema`** has the same form as a prepared statement's `OutputSchema`. It holds
  the statement's own columns, typed as they would come back. Whenever any column
  discloses less than its full value, every field carries `chalk.disclosure`, as a
  prepared statement's schema does. Nullability is what the policy makes it: a column the
  principal is not shown is nullable, and so is an aggregate that the group-size floor can
  hide. Companion disclosure columns and `Omit` apply only to a result that runs, so
  neither is in it.
- It is **null** where the refusal came before the columns were known: a star under a
  refusing star policy, a prepare with no context, or an entitlement that does not parse.
- **The engine remembers each refusal.** The same request is refused again without asking
  the planner, and carries the same `Refusal` object. A different statement, context or
  catalog is a different request.

Whether a statement is refused can depend on when the principal's values are bound. Bound
as the statement is prepared, the planner knows this principal's lists, so a `WHERE` clause
can lift the refusal by ruling out the rows the column is counted on. Prepared from a
shape, the planner plans once for every principal of that shape, and refuses a use that
would be unsafe for any of them.

### The tenancy package

`Chalk.Entitlements.Tenancy` is layer B: the model a host actually thinks in, and a
compiler down to the descriptor above. The core knows none of its words, which are:

- a **tenancy** kind: a container that rows belong to, such as an organisation (`org`);
- a **subject** kind: an individual, such as a member, who belongs within tenancies;
- a **realm**: a named group of protected columns, such as `pii` for personal details;
- a **role**: what a principal is within a tenancy, such as a `manager` or an `agent`;
- a **grant**: what gives a principal a role, such as manager of organisation 1.

Every name is entered once and comes back as a *handle* — a kind, a role, a realm, a
table, a column — and every later mention uses that handle. Nothing is generic over a
row type. A host that builds its policy from its own configuration at run time has no
row type to name, and that is the main way the package is used.

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

A table is obtained from a `Source` and from nowhere else, because two sources may hold
tables with the same name. A column is obtained from its table, so a column of the wrong
table is refused where it is used, rather than looked up by its spelling. Those two, and
`Sql.Of` for the places where a policy holds an expression rather than a name, are the
only places a string enters at all.

A table is either `Unrestricted()` or restricted through `Tenancy(t => …)`. `Tenancy`
takes one or more *restrictions*, OR-ed together:

- `Direct`, on a column of the row;
- `Inherited` and `Related`, down and up a path of declared foreign keys;
- `Predicate`, the host's own SQL over the bound context, kept exactly as written;
- `ResourceOwner`.

Unrestricted means every row is visible. The column rules still apply, so a reference
table with one sensitive column belongs here too.

Sometimes a path has to leave its source. `Column.References(Column)` declares a link
that a foreign key cannot state, because a foreign key is one source's claim about a
table in *its own* schema. The link travels on the catalog, and a path step follows it
exactly as it follows a foreign key: declared, never verified, which is the standing a
declared foreign key already has.

There are four kinds of grant, and no fifth:

- `Grant.ForTenancy`, on a container;
- `Grant.ForSubject`, on an individual. It is confined to one tenancy or to
  `Tenancy.Anywhere`, and it is never built from a request payload;
- `Grant.Global`, which is refused at binding unless the policy opts in;
- the resource-owner fail-safe, which is not a grant at all, but a row the principal
  owns.

**The list is the priority.** Rules are read in the order you wrote them. A rule matches
a row when the principal holds one of the rule's roles in that row's tenancy, and the
rule's `When` holds. `StopOnMatch`, true by default, ends the reading there. So the
answer is the first matching stop-rule; failing that, the last matching continue-rule;
failing that, nothing. Position is the only priority: no hidden rule combines the
matches behind your back:

```csharp
// Read top to bottom. The first rule that matches is the answer.
.Access(new AccessRule { Roles = [auditor], Realm = pii, Grants = Verdict.None, Placeholder = Sql.Of("'withheld'") })
.Access(new AccessRule { Roles = [analyst], Realm = pii, Grants = Verdict.Full, When = Sql.Of("role = 'analyst'") })
.Access(new AccessRule { Roles = [analyst], Realm = pii, Grants = Verdict.Mask, Mask = Sql.Of("lambda v: SUBSTRING(v, 1, 1)") })
.Access(new AccessRule { Roles = [ops], Grants = Verdict.Full, StopOnMatch = false })
```

Reading the example:

- An auditor sees nothing of `pii`, whatever else they are, because that rule is first.
  `Placeholder` is what stands in for the value under *that* rule. It wins over the
  column's own stand-in, and over the request's placeholder policy. A placeholder is the
  redaction's counterpart to a mask, and in a plan the two are opposites. A mask is a
  value a statement may compare. A placeholder stands where there is no value to compare
  at all, so the column is still reported as `Redacted`.
- An analyst sees another analyst's name in full, and everyone else's as an initial,
  because `When` is an ordinary true-or-false condition over the row. The mask is a
  *template*: `lambda v: …` is filled in once for each column of the realm, with `v`
  replaced by that column's name, so one rule covers a realm of ten columns.
- The last rule names neither a realm nor a column, so it is about every protected
  column. It is the explicit, permissive form for a role that is meant to see everything
  a policy protects. It is a continue-rule, so it is what `ops` falls through to, not
  something that overrides the rules above.

**Silence grants nothing.** A column is *protected* when it is in a realm, or when any
rule names it. On a protected column, a role that no rule speaks for gets nothing. A
column in no realm, which no rule names, is not protected at all, and keeps the table's
default. Non-interactive work runs against a catalog built *without* the policy. There
is no privileged profile to acquire.

**A mask reads its own column, and nothing else protected.** The sanitiser is evaluated
over the raw row. So a mask that read another realm's column would put that column's raw
value inside this one, and hand it to a principal entitled to neither. This is refused,
naming both columns, by the compiler and again at registration. A rule's *condition* may
read anything: what it discloses is one bit, true or false, and you chose it.

The compiler writes only membership tests over bound lists and comparisons with scalars.
So everything folds natively, and nothing needs a round trip. A foreign-key path is
*proved* against the declared keys, and must end at a column the entitled table carries.
If a tenancy could only be answered by joining the parent, it is refused, naming what
would be needed, rather than compiled into an N+1 query.

`entitlements.Reconcile(prepared, principal)` then works out again, from the grants
alone and with no plan involved, what every column of every entitled read must disclose,
and returns any differences. The taint check proves that nothing escaped; this is the
half that proves nothing was misclassified. Beside them, it gives a three-valued verdict
for each table on how much of it the principal can see: `Agrees`, `Disagrees`, or
`Indeterminate`. `Indeterminate` is for a host predicate outside the small grammar it
reads, because a guess that happened to agree would be worse than no answer.

### Grants that hold several kinds at once

**`Within` means AND; everything else means OR.**
`Grant.ForTenancy(classification, "SECRET", analyst).Within(mission, "KESTREL")` reaches
a row whose classification is SECRET *and* whose mission is KESTREL. Two grants are two
alternatives, and so are a table's restrictions. Say a table declares both a
classification and a releasability. A row can be reached along either one, so a grant
that names one kind alone reaches every row with that value, whatever the other kinds
hold. The AND lives in the grant, not in the table.

**A grant reaches a table where all its kinds can be read together:** on the row itself,
on one path's endpoint read beside the row, or on a parent's row. It reaches nothing of
a table where they cannot. On a table that carries more kinds, a grant that names fewer
is the broader one.

**A grant is its set of kinds.** The kind it names first is only a way of writing it.
`ForTenancy(releasability, …).Within(classification, …)` and the reverse reach the same
rows, on every table. That includes a table that holds a classification of its own, and
inherits its releasability along a path.

**A kind the table declares is read from the table's own row.** Say a table holds a kind
directly, and also reaches another kind along a path whose endpoint holds the first kind
too. A grant confined along that path is decided by the table's own value, not the
endpoint's. So a grant confined to SECRET does not reach a TOP SECRET row whose parent
is SECRET, and one confined to TOP SECRET does. A kind the table reaches along a
*different* path confines nothing on this one, because its value is on another row.

A subject's path reads the same way.
`Grant.ForSubject(person, "P-7", self, within: "SECRET")` reaches the SECRET
availability entries about P-7, and not a TOP SECRET one, whatever P-7's personnel
record holds. Only where the table declares no such kind does the endpoint's value
confine the grant. For example, P-7's training records are reached where P-7's record is
SECRET.

**Declaring the combinations.** Left to itself, the compiler writes a membership test
for every subset of the kinds a row carries, for each role. Five kinds on one row are
eighty tests per role, and the compiler refuses to write more than four confining kinds
for a dimension. A policy that declares the combinations its grants hold compiles those
and nothing else:

```csharp
policy
    .Combination([analyst, commander], classification, mission, compartment, releasability, environment)
    .Combination([planner], classification, releasability);
```

Each combination is one test per admitted role, on each route that answers it. So the
five-kind table compiles two tests where it compiled a hundred and sixty, and there is
no limit on the number of kinds. Declaring is opt-in. But once a policy declares one
combination, it must declare all of them:

- A tenancy grant must be one of the combinations its role declares — a grant on one
  kind alone as well — or it is refused at binding, naming the combinations the role
  has.
- A combination whose kinds a table declares, but that no single route answers (say its
  kinds are reached along two different paths), is refused when the policy compiles,
  naming the table and the role. A combination with a kind a table does not declare is
  not that table's, and is passed over.
- Subject grants keep `Subject(name, within: …)`.

Fewer tests mean fewer lists to bind, and fewer lookups for each row. When the context
is bound at execution, the engine answers each test from the list it holds. So a policy
that declares nothing still prepares as a shape, however many tests it compiles. It is
only slower to plan and to run than one that declares what it grants.

### Pushdown and locality

The filter on the tenancy column has to reach the source, or a table of any size is
impossible to query. `Filter_R`, the row-predicate filter, sits directly on the scan,
below every mask, so it is the first thing the pushdown rules meet. A folded tenant set
travels to the source as an `IN` list, in the source's own dialect.

`prepared.Entitlements.Tables[i].RowPredicatePushed` says, honestly, whether it did. It
is false for an in-process table, for a source that does not declare the filter's shape,
and under the settings below. If a source can take only part of a filter, the filter is
split: the tenancy part goes to the source, and a client-bodied condition beside it
stays here. Without the split, that condition would hold the tenancy filter back, and
the whole table would be fetched.

**Masks do not travel.** Take an expression that is not a bare column reference, and
that reads a column this principal may not see in full. It never enters a source's
query. A manager's `UPPER(first_name) = 'T'` is pushed, because they see the column in
full. An agent's becomes `SUBSTRING(first_name, 1, 1) LIKE …`, and is evaluated here,
over the tenancy's rows that the pushed filter already returned.

A mask travels only where the table declares `PushMasks` *and* the source declares
`SupportsMaskPushdown`. Even then, it is an optimisation the cost model may turn down;
the answer is the same either way. Where a mask does travel, the **whole** sanitiser
goes. `CASE WHEN … THEN note WHEN … THEN '********' ELSE NULL END` is computed in the
database, so the raw value of a row this principal may not read never leaves it. That
needs a third thing, which every source has unless it says otherwise: `SupportsCase`. It
is false only for a source that cannot evaluate a conditional at all.

These settings apply per table or per source. The last one is set on a source builder:

| Setting | What it does |
|---|---|
| `Enforcement.Pushdown` (default) | The row predicate travels wherever the source can take it, and the report says whether it did. |
| `Enforcement.Local` | No predicate of the table is pushed at all. The source receives a plain scan of the needed columns, and the tenant set never appears in another system's query log. |
| `Enforcement.PushdownRequired` | The planner prefers a plan that pushes this table's row predicate, wherever one exists. A plan that would evaluate it locally is refused, naming the table, the source and the shape the source does not take. For a source holding every tenancy's rows, a silent full fetch is the worse failure. |
| `TrustSourceRowLevelSecurity(RowLevelSecurityPreconditions)` on a source builder | The host trusts this source's own row-level security. The pass adds no row predicate for its tables, and **nothing else changes**: the column disclosures are still Chalk's. Chalk cannot check the claim, so the call states its three preconditions by name: the connection identifies the principal to the source; row-level security is enabled and forced on every entitled table; and the source's policies admit exactly the rows the entitlement would. A call that asserts fewer is refused, naming the ones it left out. |

### The trust model, and the stated limit

The application developer writes the shape of every statement. The end user supplies
bound values, which cannot change what the statement selects, or add a function or a
join. So the rewrite judges *shapes*, and at prepare it refuses the shapes a principal's
disclosure forbids. That is why the corpus runs every statement as every principal: that
is when a refusal shows. Non-interactive work, such as a batch job or a migration, runs
against a catalog built *without* the policy. Nothing in the core is bypassed, and the
process boundary is the audit boundary.

**What a caller is told follows the same rule.** It may depend on the caller's own
context and on the statement they wrote, but never on a row they cannot see. So there is
no count of filtered rows, wherever the filter runs, because "three rows matched but
were redacted" is another tenancy's data. `visibility`, `contradiction`, the per-column
labels and the explanation are all worked out from the context and the statement alone.
That is what makes them safe to hand over.

**The stated limit.** The group-size guard over a population-only column, and the
`Statistical` opt-in that rests on it, are **query-set-size control, not differential
privacy**. A group of *k* rows reveals its aggregate, whatever those *k* rows are. A
*tracker* — an attack that combines several statements to single out one person from
details that together identify them (*quasi-identifiers*) — is not prevented, and a
floor cannot prevent it. Differential privacy answers that problem, and Chalk does not
implement it. A host that turns `Statistical` on for a column accepts exactly that
trade-off, which is why it is off by default and set per column.

One remaining channel is documented rather than closed. Error text from a function that
fails on some inputs, in a statement shape the developer wrote, may quote a non-entitled
column of an excluded row.

## Testing

The backbone of correctness is a second executor that is naive on purpose
(`ExecutionOptions.Engine = Reference`). It is a row-at-a-time interpreter of the same
plan IR, and it shares no kernels and no operators with the vectorised engine. Every
corpus query runs through both, and the results are compared; this is the *differential
test*. To run a suspect query the slow, obvious way, set it on an engine:

```csharp
Execution = new ExecutionOptions { Engine = ExecutionEngine.Reference }
```

Plan shape is checked separately. `corpus/plans/` holds a recorded plan, its JSON twin
and its digest for every corpus query, at two pushdown levels. A changed digest in a
pull request is the signal that a rule or cost change altered planning. Re-record them
with `./scripts/record-plans.sh`.

A third opinion comes from DuckDB, a test-only dependency of `Chalk.Integration.Tests`.
The same fixtures are loaded into an in-memory DuckDB, and every corpus query is asked
of it too. Two executors written from one design can agree on a misreading of SQL. An
engine with no stake in that design cannot. The tests skip themselves if DuckDB's native
library will not load.

### The `edge` family

`corpus/queries/edge/` is the one query family that is not a feature area. It holds
ninety queries over two small tables that are hostile on purpose:

- `sales` has six rows, including a duplicate value and a NULL, and has no declared
  collation and no unique key;
- `sorted` has four rows, and is declared ordered by a key that repeats and skips.

The queries aim at the places a vectorised executor breaks: ties that span a fetch
boundary, an offset that lands inside a tie, null-safe join keys, window frames that are
empty for every row, set operations over a nullable key, and `UNNEST` of a NULL and of
an empty list. This family is where the grafted coverage lives.
`corpus/queries/edge/README.md` has the groups and where they came from.

### Comparison modes

Most queries are compared row for row (`Ordered`). A query with no total order is
compared as a multiset (`Multiset`): the same rows, in any order. Now take a query whose
`LIMIT` cut falls inside a tie. Its answer is fixed everywhere except at the cut, where
only the number of rows is fixed. Such a query opts into a third mode with a header:

```sql
-- compare: top-k-under-ties
```

`TopKUnderTies` takes the ordering, fetch and offset from the recorded plan. It checks:

- the number of rows;
- the key order;
- that no row lies outside the boundary keys;
- that every determined row is present;
- that the rows at a boundary come from the group tied there.

The header does not repeat anything the plan already says.

### The reader type matrix

`AdoTypeMatrixTests` is the ADO.NET reader's contract, written out: twenty-one Chalk
types against five read paths. The paths are SQLite; DuckDB through the provider's
reader, through the native data-chunk copier and through the Arrow export; and
PostgreSQL. Each cell checks the Arrow array type, the value as the IR stores it, and a
NULL. On every path:

- a `DATE` is days since the epoch, with the epoch as day zero;
- midnight is midnight wherever the machine is;
- a `DECIMAL` keeps its scale;
- an empty string is not a NULL;
- a column whose provider type the declared one cannot hold causes a
  `SourceContractException` that names both.

## Repository layout

| Path | |
|---|---|
| `proto/` | the shared contract, compiled by both builds |
| `dotnet/` | the client: `src/`, `tests/`, `tools/`, `bench/`, `samples/` |
| `planner/` | the Calcite sidecar (Gradle, Kotlin DSL) |
| `corpus/` | queries, recorded plans and digests shared by both test suites |
| `docs/` | this guide and the tutorial |
| `scripts/` | build, test, the battery, plan recording, tool install |

## Documentation

- `docs/tutorial.md` — the tutorial: eighteen chapters over one marketplace.
- `CONTRIBUTING.md` — how to build, test and record plans.

## Licence

Apache-2.0. See `LICENSE` and `NOTICE`.
