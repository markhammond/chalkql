# ChalkQL.Sources

Standard data-source implementations for [ChalkQL](https://www.nuget.org/packages/ChalkQL), the
federated SQL query engine for .NET powered by [Apache Calcite](https://calcite.apache.org/).

[![ci](https://github.com/markhammond/chalkql/actions/workflows/ci.yml/badge.svg)](https://github.com/markhammond/chalkql/actions/workflows/ci.yml)

Targets .NET 10.

```bash
dotnet add package ChalkQL.Sources
```

`ChalkQL.Sources` depends on `ChalkQL`, so a separate reference to the core package is not required.

Included source implementations:

| Source                      |                                                                     |
|-----------------------------|---------------------------------------------------------------------|
| `Chalk.Sources.Poco`        | application-owned POCO collections exposed as relational tables     |
| `Chalk.Sources.Akade`       | [Akade.IndexedSet](https://github.com/akade/Akade.IndexedSet) collections exposed as relational tables, their indexes discovered |
| `Chalk.Sources.Ado`         | ADO.NET sources with capability-aware SQL pushdown                  |
| `Chalk.Sources.DuckDb`      | DuckDB integration with native data-chunk reading                   |
| `Chalk.Sources.Conformance` | verifies a source's declared capabilities against the source itself |

## POCO sources

Application-owned collections can be exposed directly without copying their rows into another
store. Tables may declare keys, collations, statistics and indexes so the planner can make use of
structure the application already knows about.

```csharp
var source = new PocoSourceBuilder("mem")
    .AddTable("rates", rows, t => t
        .OrderedBy(r => r.Ts)
        .ThenBy(r => r.Currency)
        .UniqueKey(r => r.Ts, r => r.Currency))
    .Build();
```

A `Utf8String` member is a STRING column whose bytes are copied in as they are, never
transcoded, and its keys, indexes and foreign keys compare bytes; `byte[]` and
`ReadOnlyMemory<byte>` members are BINARY.

A member whose type is a record is a composite column: one column whose fields are the record's
public properties, in order and under the record's own names, each of a type a member may have.
SQL reads a field by name to filter, order or group, and the column itself travels whole through
`SELECT *`, a sort or a join and reaches the host as an Arrow struct column, which
`ReadComposites<T>` reads back as the record. Each field is staged as a column of its own type
would be. A record struct member is never NULL and its `Nullable<T>` may be; a record class
member is nullable unless the compiler annotates it as not null.

```csharp
public readonly record struct Side(double Price, long Size);
public sealed record Quote(long Id, Utf8String Symbol, Side Bid, Side? Ask);

var source = new PocoSourceBuilder("mem")
    .NamingPolicy(PocoNamingPolicy.SnakeCase)
    .AddTable("quotes", quotes, t => t
        .UniqueKey(q => q.Id)
        .Index(q => q.Symbol))
    .Build();
```

```sql
SELECT q.id, q.ask.price AS ask
FROM quotes q
WHERE q.bid.price > 400 AND q.ask IS NOT NULL
ORDER BY q.ask.price DESC
```

No key, index or declared ordering may name a composite column or a field of one: a `UniqueKey`,
`OrderedBy`, `ForeignKey`, `Index` or clustered covering set that names one is refused at
`Build()`, naming the member, and the column carries no statistics. Under entitlements a
composite column is disclosed whole or withheld whole.

POCO indexing is extensible: implement `IPocoIndex<T>` to adapt an index structure the
application already maintains, and run the test kit's `PocoIndexConformance.Verify` against it to
assert ChalkQL's range and ordering contract.

## Akade.IndexedSet sources

An `IndexedSet<T>` is exposed as one table, and the indexes it was built with become the planner's
access paths without an adapter written by the host: unique and non-unique indexes as hash
lookups, range indexes as ordered lookups that also serve `ORDER BY` and stop early under a
`LIMIT`, compound keys of two to four members, prefix tries for `LIKE 'p%'`, and an ordered index
read backwards for `ORDER BY … DESC LIMIT 1`.

```csharp
var purchases = rows.ToIndexedSet(x => x.Id)
    .WithIndex(x => x.ProductId)
    .WithRangeIndex(x => x.Amount)
    .WithRangeIndex(x => x.UnitPrice)
    .Build();

var source = AkadeSource
    .From("purchases", purchases)
    .NamingPolicy(PocoNamingPolicy.SnakeCase)
    .Build();

await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
{
    ContextId = "shop",
    Sources = [source],
    Planner = planner,
});

var cheapest = await engine.PrepareAsync(
    "SELECT id, amount, unit_price FROM purchases WHERE amount BETWEEN ? AND ? ORDER BY unit_price LIMIT ?");
```

The table takes the source's name unless `TableName` says otherwise. A `float`, `double` or string
key becomes an ordered access path once the comparer the index was built with is declared, and a
descending comparer registers a descending index. The
[Akade source README](https://github.com/markhammond/chalkql/blob/main/dotnet/src/Chalk.Sources.Akade/README.md)
lists what each Akade index becomes, shows how to modify a published set safely, and carries a
benchmark of ChalkQL's execution overhead against Akade's own calls.

A record member is a composite column here, as on a POCO table. An Akade index keyed by the
record itself is not an access path, since nothing is keyed on a composite value, so that column
is read through the scan while the set's other indexes still serve their lookups.

## Remote SQL sources

ADO.NET and DuckDB sources declare which relational operations they can evaluate. ChalkQL pushes
supported work to the source — filters, projections, sorts, limits, aggregates and joins within one
source — and executes the remainder inside the .NET host. `AdoCapabilities.For(profile)` derives a
source's capabilities from its dialect profile; a host may narrow the derived set for a source it
would rather not have sort or truncate on its behalf.

A `LIMIT` travels with a pushed query whether it is a literal or a parameter: a parameterised bound
is written into the query text with the value bound at execution, in the spelling the dialect uses.

Queries may span several sources at once, including joins between remote databases and
application-owned tables.

## Conformance

`Chalk.Sources.Conformance` probes a source's declared capabilities against the source itself —
which functions, operators and collations it evaluates the way ChalkQL does — so that pushdown
rests on what was verified rather than on what was hoped. Run it once against each database and
dialect profile a deployment uses.

## Custom sources

`Chalk.Sources.Abstractions` is distributed with the core `ChalkQL` package. Implement
`ISourceRuntime` when the built-in sources are not appropriate. A composite column belongs to an
in-process source alone: a remote source that declares one is refused at registration, naming the
table and the column, so a remote table declares a record's fields as columns of their own.

## Things that will bite you

**Not all ADO.NET providers support zero-allocation reads.** Whether ChalkQL can avoid intermediate
allocations depends on the provider and the data-access path it exposes.

**A published collection must not change under a running query.** For POCO and Akade sources the
host keeps one rule: no mutation overlaps an execution or a refresh. Mutate between requests and
refresh the table's metadata, swap the whole set behind a delegate, or use the transactional
`Append` and `Replace` when requests are in flight, which give each execution its own snapshot.

**Source capabilities are promises.** Pushdown is based on what a source declares. The conformance
package exists to test those declarations against the database rather than discovering
disagreement in production.

## Documentation

See the [ChalkQL guide](https://github.com/markhammond/chalkql/blob/main/docs/guide.md) for source
configuration,
[composite columns](https://github.com/markhammond/chalkql/blob/main/docs/guide.md#composite-columns)
and extension points, the
[tutorial](https://github.com/markhammond/chalkql/blob/main/docs/tutorial.md) for worked federation
examples, and the
[Akade source README](https://github.com/markhammond/chalkql/blob/main/dotnet/src/Chalk.Sources.Akade/README.md).

The full project source and documentation are in the
[ChalkQL repository](https://github.com/markhammond/chalkql).

## Licence

ChalkQL.Sources is licensed under the Apache License 2.0.
