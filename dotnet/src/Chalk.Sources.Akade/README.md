# ChalkQL source for [Akade.IndexedSet](https://github.com/akade/Akade.IndexedSet)

For maximum convenience ChalkQL lets you query an [Akade.IndexedSet](https://github.com/akade/Akade.IndexedSet) with ordinary SQL.

Supported Akade indexes are discovered automatically and exposed to the ChalkQL planner, so hash, range, ordered and prefix lookups can be selected without query hints or adapter-specific SQL.

One `IndexedSet<T>` becomes one ChalkQL source containing one logical table.

```bash
dotnet add package ChalkQL.Sources.Akade
```

See the [ChalkQL guide](../../../docs/guide.md) for engine configuration, SQL behaviour, federation and the broader source lifecycle.

## Quick start

Build an `IndexedSet<T>` as usual:

```csharp
var purchases = rows
    .ToIndexedSet()
    .WithUniqueIndex(x => x.Id)
    .WithIndex(x => x.ProductId)
    .WithRangeIndex(x => x.UnitPrice)
    .Build();
```

Expose it to ChalkQL:

```csharp
var source = AkadeSource
    .From("purchases", purchases)
    .Build();
```

The source contains a single table named `purchases` by default. Override it only when the source and table names should differ:

```csharp
var source = AkadeSource
.From("sales", purchases)
.TableName("purchases")
.Build();
````

Add the source to the engine and query it normally:

```csharp
await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
{
    ContextId = "app",
    Sources = [source],
    Planner = planner,
});

var query = await engine.PrepareAsync("""
    SELECT id, product_id, unit_price
    FROM purchases
    WHERE product_id = ?
    ORDER BY unit_price
    LIMIT 20
    """);

await using var execution =
    await engine.ExecuteAsync(query, [productId]);

await foreach (var batch in execution.Batches)
{
    // Apache.Arrow.RecordBatch
}
```

There is no index syntax in the SQL. ChalkQL costs the access paths and defers to the appropriate Akade index.

For example:

```sql
WHERE product_id = ?                         -- HASH
WHERE unit_price BETWEEN ? AND ?             -- ORDERED
ORDER BY unit_price LIMIT 10                 -- ORDERED, early exit
WHERE name LIKE 'Int%'                       -- PREFIX or ordered string index
```

## Supported indexes

One Akade index becomes one ChalkQL index when ChalkQL can describe its semantics faithfully.

| You build                                                                                                       | ChalkQL sees                                                       | It can serve                                           |
| --------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------ | ------------------------------------------------------ |
| `.WithUniqueIndex(x => x.M)` or `.WithIndex(x => x.M)`                                                          | `HASH` on `M`; `Unique` where applicable; exact distinct-key count | `= ?`, `IN (...)`                                      |
| `.WithIndex(x => (x.A, x.B))`                                                                                   | compound `HASH`                                                    | equality on every component                            |
| `.WithRangeIndex(x => x.M)` on an integer, decimal or temporal key                                              | `ORDERED` on `M`                                                   | `=`, `<`, `>`, `BETWEEN`, `ORDER BY`, early `LIMIT`    |
| `.WithRangeIndex(x => x.M)` on `float`, `double`, `string` or `Utf8String`, with a declared compatible comparer | `ORDERED` on `M`                                                   | the same                                               |
| `.WithRangeIndex(x => (x.A, x.B))`                                                                              | compound `ORDERED`                                                 | equality on leading components and a range on the next |
| `.WithPrefixIndex(x => x.Text)`                                                                                 | `PREFIX`                                                           | `LIKE 'prefix%'`                                       |
| full-text, spatial, vector or computed-key indexes                                                              | not advertised                                                     | remain available through Akade directly                |

Supported compound keys contain two to four direct members.

Unsupported index families are deliberately invisible to the planner rather than being represented approximately.

Registration validates every advertised claim. A tuple accessor with the wrong arity, an unrecognised comparer, or a compound declaration inconsistent with its accessor is refused by name.

## Compound indexes

An ordinary tuple accessor needs no additional description:

```csharp
var set = rows
    .ToIndexedSet()
    .WithIndex(x => (x.ProductId, x.UnitPrice))
    .Build();
```

A method accessor does not expose its component members in the expression text, so name them when registering the source:

```csharp
var set = rows
    .ToIndexedSet()
    .WithIndex(PurchaseKeys.ProductAndUnitPrice)
    .Build();

var source = AkadeSource
    .From("purchases", set)
    .CompoundIndex(
        PurchaseKeys.ProductAndUnitPrice,
        x => x.ProductId,
        x => x.UnitPrice)
    .Build();
```

The accessor must return a `ValueTuple` containing exactly those member types in that order.

A compound ordered index may constrain only a leading run of its components. For example, an index on:

```text
(product_id, unit_price)
```

can serve equality on `product_id`, optionally followed by a range on `unit_price`.

A hash index requires equality on every indexed component.

## Computed keys

Expression-valued Akade keys are not currently advertised:

```csharp
.WithIndex(x => x.End - x.Start)
.WithIndex(ComputedKey.SomeStaticMethod)
```

ChalkQL's current index descriptor represents an index key using base-table columns. Until index keys can be represented as scalar expressions, a computed Akade key cannot be described to the planner truthfully.

The index remains usable through Akade itself.

## Ordered indexes and comparers

An `ORDERED` index is more than an efficient lookup. It tells the planner that rows arrive in ChalkQL's SQL order, which may allow an explicit sort to disappear.

For example:

```sql
SELECT *
FROM purchases
ORDER BY unit_price
LIMIT 1
```

can become one ordered lookup and one row rather than a scan and sort.

That makes the comparer used to build the Akade index part of the contract.

### Keys whose default order is compatible

Integer, decimal and temporal keys may be advertised directly because their CLR default order agrees with ChalkQL's order:

* integer types;
* decimal;
* `DateTime`;
* `DateTimeOffset`;
* `DateOnly`;
* `TimeOnly`;
* `TimeSpan`.

### Keys that require an explicit comparer

For these key types, ChalkQL needs to know what ordering the Akade index was built with:

* `float`;
* `double`;
* `string`;
* `Utf8String`.

Use `ChalkComparers.For<T>()` to build and declare the index in ChalkQL's order:

```csharp
var set = rows
    .ToIndexedSet()
    .WithRangeIndex(
        x => x.Symbol,
        ChalkComparers.For<string>())
    .Build();

var source = AkadeSource
    .From("bars", set)
    .Comparer(
        x => x.Symbol,
        ChalkComparers.For<string>())
    .Build();
```

ChalkQL classifies the declared comparer rather than trusting an arbitrary implementation.

Accepted comparers include ChalkQL's own comparer, CLR defaults where they are compatible, and `StringComparer.Ordinal` where applicable.

`Guid` remains equality-only because CLR `Guid` comparison does not match ChalkQL's UUID byte ordering.

Nullable range-index keys are not currently advertised.

### Strings and `StringComparer.Ordinal`

`StringComparer.Ordinal` compares UTF-16 code units while ChalkQL orders strings by Unicode code point.

Those orders agree except around supplementary Unicode characters represented as surrogate pairs.

The adapter verifies the order emitted by an ordered index as it is consumed. If an index produces rows out of its declared ChalkQL order, execution fails with a `SourceContractException` naming the index and offending keys rather than silently returning a wrongly ordered result.

For an exact match to ChalkQL ordering, prefer:

```csharp
ChalkComparers.For<string>()
```

### Descending indexes

A descending Akade range index can be declared with:

```csharp
ChalkComparers.For<T>(descending: true)
```

ChalkQL then advertises the physical ordering as descending.

For example:

```sql
SELECT *
FROM bars
ORDER BY ts DESC
LIMIT 1
```

can be satisfied directly by the index without inserting a sort.

## Prefix indexes and `LIKE`

An Akade prefix index:

```csharp
.WithPrefixIndex(x => x.Name)
```

becomes a ChalkQL `PREFIX` index.

A bare SQL prefix pattern can use it:

```sql
WHERE name LIKE 'Int%'
```

A prefix pattern means:

* one trailing `%`;
* no other `%`;
* no `_`;
* no `ESCAPE`.

More general patterns remain ordinary predicates:

```sql
WHERE name LIKE '%USDT'
WHERE name LIKE 'a%b%'
WHERE name LIKE 'A_'
```

An ordered string index can also serve a bare prefix query. ChalkQL converts the prefix into the corresponding half-open string range.

A prefix trie itself claims no ordering.

Akade's fuzzy trie search is not advertised because ChalkQL's current index access paths express equality, ranges and prefixes, not edit distance.

## Lifecycle and mutation

ChalkQL reads the published `IndexedSet<T>` directly. It does not make a private copy for every execution.

**Do not mutate a published set while ChalkQL may be reading it.**

Mutation must not overlap:

* query execution against the source;
* a scoped refresh reading the set; or
* preparation of a transactional refresh that reads the current rows.

`ConcurrentIndexedSet<T>` does not relax this rule.

There are three supported update patterns.

### 1. Mutate between requests

When the host can guarantee a quiescent period, it may modify the existing set directly:

```csharp
// Host guarantee: nothing is currently reading `orders`.
lock (ordersGate)
{
    set.Add(order);
    set.Update(changed);
}

await engine.RefreshAsync(
    r => r.Refresh(source.Table));
```

The table refresh recomputes row count and statistics. It does not rebuild the Akade collection.

This is appropriate when the host already serialises access through a request queue, application lock, single writer or equivalent mechanism.

The lock shown above is illustrative: the important contract is that no ChalkQL execution or refresh is using the set while it is modified.

### 2. Swap the whole set

When the host rebuilds a complete set, register it through a delegate:

```csharp
IndexedSet<Order> current =
    BuildIndexedSet(initialRows);

var source = AkadeSource
    .From("orders", () => current)
    .TableName("orders")
    .Build();
```

Build the replacement away from the published state, swap the reference, then refresh the source:

```csharp
current = BuildIndexedSet(nextRows);

await engine.RefreshAsync(
    r => r.Refresh(source));
```

A source-scoped refresh re-reads the delegate and rediscovers supported index topology as well as statistics.

### 3. Replace or append while requests are in flight

When reads and writes may overlap, use ChalkQL's transactional refresh mechanism.

Configure the source with a function capable of rebuilding the Akade set:

```csharp
var source = AkadeSource
    .From("orders", set)
    .RebuildWith(rows => BuildIndexedSet(rows))
    .Build();
```

Then publish changes through `Append` or `Replace`:

```csharp
await engine.RefreshAsync(refresh =>
{
    refresh.Append(source.Table, batch);
});
```

or:

```csharp
await engine.RefreshAsync(refresh =>
{
    refresh.Replace(source.Table, rows);
});
```

`RebuildWith` receives the rows for an unpublished successor. It never receives the currently published set.

For `Append`, ChalkQL materialises:

```text
current rows + appended rows
```

and asks the callback to build a fresh set containing that sequence.

For `Replace`, the callback receives exactly the replacement rows.

Only after the successor has been built and validated does commit publish it.

An execution already reading the old set finishes against that set. An execution beginning after commit sees the new one.

For a server where requests may be in flight when data changes, this is the recommended default.

## `ConcurrentIndexedSet<T>`

The `AkadeSource.From(...)` overloads accepting `ConcurrentIndexedSet<T>` are experimental under diagnostic ID `CHALK002`.

Akade's concurrent wrapper protects individual Akade operations with its own locking. That does not make a complete ChalkQL execution a point-in-time read.

For execution, ChalkQL captures the wrapped `IndexedSet<T>` and then streams from it after Akade's reader lock has been released. Holding the Akade reader lock for an entire scan would require materialising the scan result before ChalkQL could stream it.

The host must therefore still guarantee:

> no mutation overlaps an execution, scoped refresh, or transactional-refresh preparation that reads the set.

`ConcurrentIndexedSet<T>` is useful when an application already owns one and wants to publish it through ChalkQL. It is not a mechanism for allowing host mutation to race ChalkQL reads.

## Scoped refresh

There are two deliberately different refresh scopes.

### `Refresh(source)`

A source-scoped refresh:

1. re-reads the registered `Func<TSet>`;
2. rediscovers supported Akade indexes;
3. recomputes row count and statistics;
4. publishes the resulting ChalkQL source snapshot.

For example:

```csharp
IndexedSet<Order> current =
    BuildIndexedSet(initialRows);

var source = AkadeSource
    .From("orders", () => current)
    .Build();

current = BuildIndexedSet(nextRows);

await engine.RefreshAsync(
    r => r.Refresh(source));
```

Use this form when the physical set or its supported index topology may have changed.

### `Refresh(source.Table)`

A table-scoped refresh:

1. re-reads the registration;
2. recomputes the table's statistics;
3. does not rediscover Akade index topology.

For the common quiescent-mutation case:

```csharp
set.Add(...);
set.Update(...);

await engine.RefreshAsync(
    r => r.Refresh(source.Table));
```

The supported index topology must remain the same.

If it has changed, the table refresh is refused and the host must refresh the source instead.

A scoped refresh cannot create snapshot isolation around mutation occurring outside ChalkQL. If the host modifies the set while statistics are being collected, those statistics are correspondingly live or fuzzy.

## Topology and transactional refresh

Supported Akade index topology forms part of the published table shape.

The rules are:

| Operation               | May index topology change? |
| ----------------------- | -------------------------: |
| `Refresh(source)`       |                        yes |
| `Refresh(source.Table)` |                         no |
| `Replace`               |                         no |
| `Append`                |                         no |

The adapter fingerprints supported indexes using properties including:

* Akade index name;
* physical index kind;
* key type;
* accessor method identity;
* key members.

A transactional successor with different supported topology is rejected instead of silently changing the access paths available to an existing prepared plan.

## Deferred statistics

For transactional `Append` and `Replace`, `StatisticsRefresh.Defer` carries the previous column-value distributions forward.

It never carries the old row count forward.

The successor therefore always has its actual row count and physical shape, while potentially retaining older distribution statistics until the next scoped refresh.

## Ordered-index execution

Akade documents range indexes as supporting ordered access, but not every detail of the enumeration order of every individual range API is part of its public contract.

The adapter therefore verifies an ordered result as it streams it.

Each row's key is compared with the preceding key. This adds one key comparison per row and no per-row allocation.

If the sequence violates the ordering ChalkQL advertised to the planner, execution fails immediately with a `SourceContractException` naming the Akade index and the conflicting keys.

The adapter deliberately does not repair such a result by sorting it.

Once the planner has relied on the physical index's declared ordering, it may have removed the logical sort entirely. Silently sorting inside the adapter would also defeat the early-exit property that makes queries such as:

```sql
ORDER BY amount
LIMIT 1
```

worth serving through an ordered index.

### Akade range operations

Against Akade 1.5.0, `Range(...)`, `Min()`, `Max()` and `OrderBy(...)` honour the comparer used to build a range index.

The one-sided `GreaterThan[OrEqual](...)` and `LessThan[OrEqual](...)` operations do not reliably honour a non-default comparer.

The adapter therefore expresses a one-sided ChalkQL range using `Range(...)` against the physical index's appropriate extreme rather than relying on those one-sided APIs.

## Reading an ordered index backwards

A common query is:

```sql
SELECT *
FROM bars
ORDER BY ts DESC
LIMIT 1
```

An ascending Akade index can serve this efficiently by walking from its last row towards its first.

ChalkQL offers the reverse access path when the requested range has no upper bound.

That includes an unbounded lookup over the whole index and a lower-bounded range. Akade can begin at the highest key and stop when it passes the lower bound without copying or buffering rows.

A bounded range with an upper bound is not currently offered backwards. Reaching the upper bound would require either skipping rows above it or buffering the matched range before reversing it.

Those plans retain an explicit sort.

For example, a compound query such as:

```sql
WHERE symbol = ?
ORDER BY ts DESC
```

may still require sorting if satisfying the compound bound would require a bounded reverse enumeration that Akade does not expose without buffering.

The same order verification used for forward walks is applied to reverse walks.

## Execution backend

Each published Akade snapshot owns an internal one-table POCO source.

That lets the adapter reuse ChalkQL's existing:

* POCO column inference;
* statistics;
* Arrow/columnar scan machinery;
* table metadata;
* execution pipeline.

Akade-specific lookup remains the physical-index integration point.

A transactional refresh therefore follows the same publication pattern as the rest of ChalkQL:

```text
PrepareRefreshAsync
    build complete successor
    validate topology and statistics
    return commit

Commit
    atomically publish source snapshot
```

Quiescent host mutation is the explicit exception: it edits the live Akade structure outside that snapshot protocol and relies on the host to prevent overlapping readers.

## Declaring functions on the source

An Akade source declares scalar, aggregate and table functions exactly as a POCO source does, with
`AddFunction`. The one worth knowing for an `IndexedSet<T>` of integers is a widened sum: `SUM` keeps
its argument's type, so a `SUM(amount)` over a large set refuses with an overflow where a `long` would
have done. Declare the widened form once and the statement stops carrying a cast:

```csharp
var source = AkadeSource
    .From("purchases", purchases)
    .AddFunction("accumulate", f => f
        .Aggregate<int, long>("v")
        .Sql("SUM(CAST(v AS BIGINT))"))
    .Build();

// SELECT product_id, accumulate(amount) AS total FROM purchases GROUP BY product_id
```

A SQL-bodied aggregate is inlined by the planner into the built-in aggregates it is written over, so
`accumulate(amount)` plans to exactly the sum-over-a-cast plan: the typed sum kernel, and pushable
wherever `SUM` is. The result is nullable by declaration, so an empty group answers `NULL` as `SUM`
does.

An aggregate SQL cannot express is implemented in the host process instead. Declare it with
`.Client()` and register the body where the engine looks for host functions; the state is a struct
held in arena memory, `Add` runs once per non-`NULL` row, and nothing is allocated per row or per
group:

```csharp
.AddFunction("accumulate", f => f.Aggregate<int, long>("v").Client())

public struct AccumulateState { public long Sum; public bool Seen; }

Functions = registry => registry.AddAggregate("accumulate",
    new AggregateSpec<AccumulateState, int, long?>
    {
        Init = static () => default,
        Add = static (ref s, v) => { s.Sum += v; s.Seen = true; },
        Remove = static (ref s, v) => s.Sum -= v,        // optional: an exact inverse, for sliding frames
        Merge = static (a, b) => new AccumulateState      // optional: partial states combine
            { Sum = a.Sum + b.Sum, Seen = a.Seen || b.Seen },
        Finish = static s => s.Seen ? s.Sum : null,       // no rows answer NULL, as SUM does
    }),
```

A client aggregate runs one delegate call per row rather than the typed kernel and is never pushed
to a source; declare `.Window()` before `.Client()` if it is to be used with `OVER`.

## Measuring the overhead

`dotnet/bench/Chalk.Benchmarks.Akade` runs equivalent operations directly against Akade and through a prepared ChalkQL statement using the same values.

It measures execution overhead rather than planning.

```bash
dotnet run -c Release --project dotnet/bench/Chalk.Benchmarks.Akade

# quicker indicative run
dotnet run -c Release --project dotnet/bench/Chalk.Benchmarks.Akade -- --job short
```

The benchmark covers:

* a hash-index point lookup;
* a band through an ordered index;
* the cheapest row through another ordered index;
* `accumulate(amount)`, a declared widened sum over the complete set.

One short run over a 200,000-row set produced:

| Case                           |    Rows | Akade directly | Through ChalkQL | Ratio | Allocated per execution |
| ------------------------------ | ------: | -------------: | --------------: | ----: | ----------------------: |
| `ORDER BY unit_price LIMIT 1`  |       1 |          19 ns |          2.2 µs |  115× |                  2.8 KB |
| `WHERE product_id = ?`         |     400 |         450 ns |          6.7 µs |   15× |                  3.3 KB |
| `WHERE amount BETWEEN ? AND ?` |   1,000 |         1.9 µs |         14.3 µs |  7.5× |                  3.3 KB |
| `accumulate(amount)` over every row | 200,000 |         207 µs |          3.5 ms |   17× |                  2.6 KB |

Treat a short benchmark run as indicative rather than a performance guarantee.

The useful shape of the result is that ChalkQL execution has a roughly fixed request-level floor — the execution pipeline, Arrow batches and arena bookkeeping — while allocation does not grow with the number of rows streamed.

The cost is paid for SQL planning integration, vectorised execution, federation and the rest of the ChalkQL execution model rather than for a direct replacement of Akade's native API.

When the application already knows exactly which Akade operation it wants, calling Akade directly remains the shortest path.

## Summary

Use an Akade source when application-owned data already benefits from `IndexedSet<T>` and also needs to participate in ChalkQL queries.

ChalkQL:

* exposes the set as an ordinary SQL table;
* discovers the Akade indexes it can represent faithfully;
* lets the planner choose those access paths;
* preserves ordered-index early exit where Akade can stream it;
* verifies ordering claims as rows are consumed;
* supports quiescent in-place mutation when the host can exclude readers;
* supports atomic replacement snapshots when it cannot.

And, for maximum convenience and enjoyment, the SQL remains SQL.
