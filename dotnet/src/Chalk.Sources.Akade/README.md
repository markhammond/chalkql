# ChalkQL Akade source

This implementation uses the agreed model:

> one `IndexedSet<T>` -> one Chalk source -> one logical table

`AkadeSource.From(...)` infers `T` and returns either `IndexedSetSourceBuilder<T>` or
`ConcurrentIndexedSetSourceBuilder<T>`. `Build()` returns the corresponding typed source, whose
`Table` is an `ITableTarget<T>`.

## Supported indexes at a glance

One `IndexedSet<T>` index becomes one Chalk index when Chalk can describe it truthfully. What you
build, what the planner sees, and what that serves:

| You build | Chalk sees | It serves |
|---|---|---|
| `.WithUniqueIndex(x => x.M)` or `.WithIndex(x => x.M)` | a `HASH` index on `M`, `Unique` when it is; its exact distinct key count | `= ?` and `IN (…)` lookups |
| `.WithIndex(x => (x.A, x.B))`, or `.WithIndex(Keys.Method)` plus `.CompoundIndex(Keys.Method, x => x.A, x => x.B)` | a `HASH` index on a tuple of two to four members | equality on every component; a scan that does not project the whole key is never offered the index |
| `.WithRangeIndex(x => x.M)` on an integer, decimal or temporal key | an `ORDERED` index on `M` | `=`, `<`, `>`, `BETWEEN`; `ORDER BY M` without a sort; a `LIMIT` above it stopping early; `ORDER BY M DESC` read from the last row down for a range with no upper bound |
| `.WithRangeIndex(x => x.M)` on a `float`, `double`, `string` or `Utf8String` key, plus `.Comparer(x => x.M, ChalkComparers.For<T>())` — or `StringComparer.Ordinal` | the same `ORDERED` index, once the order the index was built with is declared; undisclosed until then | the same; `ChalkComparers.For<T>(descending: true)` declares a descending index, which serves `ORDER BY M DESC` |
| `.WithRangeIndex(x => (x.A, x.B))`, a tuple of two to four members | an `ORDERED` index on the tuple | equality on a leading run of components and a range on the next, in tuple order |
| `.WithPrefixIndex(x => x.Text)` — a trie | a `PREFIX` index | `LIKE 'p%'`; nothing else, and no order |
| `.WithFullTextIndex(…)`, spatial and vector indexes, a computed key such as `x => x.End - x.Start` | nothing, deliberately | the structure stays usable through Akade itself and is not advertised to the planner |

Everything in the table is checked at registration rather than trusted: a tuple accessor of the wrong
arity, a comparer Chalk cannot classify and a compound declaration that does not match its accessor
are each refused by name. The sections below say why each line reads as it does.

## Measuring the overhead

`dotnet/bench/Chalk.Benchmarks.Akade` asks the same four questions of a two-hundred-thousand-row set
twice — once of Akade directly, once as a prepared ChalkQL statement executed with the same values —
so that what ChalkQL's execution costs over the raw set is a number: a point lookup through the hash
index, a band through an ordered index, the cheapest row through the other ordered index, and a sum
over every row. The statements are prepared once, so nothing in it measures the planner.

```
dotnet run -c Release --project dotnet/bench/Chalk.Benchmarks.Akade
dotnet run -c Release --project dotnet/bench/Chalk.Benchmarks.Akade -- --job short   # a quick look
```

Read `Ratio` and `Allocated`: the ratio is what a request pays for going through the engine, and the
allocation is what says the per-row path costs nothing on the heap. One short job on a laptop with
other work running, for the shape of the numbers rather than the numbers:

| case | rows | Akade directly | through ChalkQL | ratio | allocated per execution |
|---|---|---|---|---|---|
| the cheapest row, `ORDER BY unit_price LIMIT 1` | 1 | 19 ns | 2.2 µs | 115× | 2.8 KB |
| a point lookup, `WHERE product_id = ?` | 400 | 450 ns | 6.7 µs | 15× | 3.3 KB |
| a band, `WHERE amount BETWEEN ? AND ?` | 1,000 | 1.9 µs | 14.3 µs | 7.5× | 3.3 KB |
| a sum over every row | 200,000 | 207 µs | 3.5 ms | 17× | 2.6 KB |

Two things to take from it. An execution has a floor of about two microseconds and three kilobytes
— the pipeline, the batches and the arena's bookkeeping — which is what the one-row case is made of
and is paid once per request, never per row. Above the floor a row costs on the order of ten to
fifteen nanoseconds to stream through a filter or an aggregate, against one or two for Akade's own
enumeration, and the allocation does not move with the row count: two hundred thousand rows allocate
less than one does, because a wider batch is a cheaper one.

## Modifying the set safely

ChalkQL never mutates a published set; the host does, and one rule governs how. **No mutation may
overlap a ChalkQL execution, a scoped refresh, or the preparation of a transactional refresh that
reads the set.** An execution streams rows from the set it captured when it started, outside any lock
Akade holds, so a row added while a scan is half way through is neither reliably seen nor reliably
unseen. Three patterns keep the rule, and they are the only three.

**Between requests, in place.** When the host can bracket its own writes — a single writer, an
application lock, a request queue — it mutates the set directly and then tells ChalkQL that the
table's metadata moved:

```csharp
// Nothing is executing against `orders` while this runs: that is the host's guarantee.
lock (ordersGate)
{
    set.Add(order);
    set.Update(changed);
}

await engine.RefreshAsync(r => r.Refresh(source.Table));   // row count and statistics; no rebuild
```

The refresh is cheap and asks nothing of Akade beyond a re-count; it exists so the planner's
estimates follow the data. Reads that begin after it see the new rows; reads that begin before the
`lock` is released are the ones the rule forbids.

**Swapping the whole set.** When the set is rebuilt rather than edited, register it through a
delegate and refresh the source, which also rediscovers the index topology:

```csharp
IndexedSet<Order> current = BuildIndexedSet(initialRows);
var source = AkadeSource.From("orders", () => current).TableName("orders").Build();

current = BuildIndexedSet(nextRows);                      // built off to the side, then published
await engine.RefreshAsync(r => r.Refresh(source));
```

**While requests are in flight.** When the host cannot promise a quiet moment, it uses the
transactional refresh, and ChalkQL keeps the promise for it: an execution already reading `orders`
finishes against the set it started on, and one started after the commit sees the successor. The
successor is built by the host's `RebuildWith` from the rows ChalkQL hands it, never by editing the
published set:

```csharp
var source = AkadeSource.From("orders", set)
    .TableName("orders")
    .RebuildWith(rows => BuildIndexedSet(rows))
    .Build();

await engine.RefreshAsync(r => r.Append(source.Table, batch));   // or r.Replace(source.Table, rows)
```

This is the pattern to reach for by default in a server: it costs a rebuild per commit, and buys the
one thing the other two cannot, a snapshot per execution.

**On `ConcurrentIndexedSet<T>`.** Akade's concurrent set protects each of its *own* operations with
a lock, and that is all it protects. It does not make a ChalkQL execution a point-in-time read: the
adapter captures the wrapped set once and streams from it after Akade's reader lock has been
released, so the host must still exclude mutation for the whole of an execution — exactly what the
rule above already requires of a plain `IndexedSet<T>`. With the rule kept, the concurrent wrapper
adds locking that nothing needs; without it, the wrapper does not save the read. Its practical
utility under ChalkQL's lifecycle is therefore limited, which is why the `From(...)` overloads that
take one are marked experimental (`CHALK002`, below): they exist for a host that already holds a
concurrent set and wants to publish it as it is, not as a way around the rule.

## The two consistency modes

The source deliberately supports both the cheap in-memory mode and Chalk's stronger transactional
refresh protocol.

### 1. Live host mutation

`RebuildWith(...)` is optional:

```csharp
var set = BuildIndexedSet(initialRows);

var source = AkadeSource
    .From("orders", set)
    .TableName("orders")
    .Build();

set.Add(order);

await engine.ExecuteAsync(...);
```

Chalk never mutates the published set itself. The host is nevertheless free to do so.

For ordinary `IndexedSet<T>`, the host is responsible for not mutating concurrently with a read if
Akade itself cannot tolerate that concurrency.

For `ConcurrentIndexedSet<T>`, Akade protects individual operations with its concurrent
implementation. That still does **not** turn a whole Chalk SQL execution into a point-in-time
snapshot: two scans/lookups performed at different moments can observe different host mutations.

Direct mutation also means catalog statistics can lag the live collection until a scoped refresh.

### 2. Transactional `Replace` / `Append`

`RefreshBuilder.Replace` and `RefreshBuilder.Append` promise stronger semantics: an execution already
reading the table keeps the old logical state, while one started after commit sees the new state.

Those operations therefore require a fresh Akade set:

```csharp
var source = AkadeSource
    .From("orders", set)
    .TableName("orders")
    .RebuildWith(rows => BuildIndexedSet(rows))
    .Build();

await engine.RefreshAsync(refresh =>
{
    refresh.Append(source.Table, batch);
});
```

`RebuildWith` / `Editor` is **only** the mechanism for building that unpublished successor. The
editor never receives the currently published set. Returning the published set is refused.

`Append` first materialises:

```text
current rows + appended rows
```

and asks the editor to construct a fresh set containing exactly that sequence.

`Replace` asks the editor to construct a fresh set containing exactly the supplied rows.

`Commit()` performs only the final publish.

## Scoped refresh semantics

The two `RefreshBuilder.Refresh(...)` scopes are intentionally different.

### `Refresh(source)`

Source-scoped refresh:

1. re-reads the `Func<TSet>` registration;
2. rediscovers supported Akade index topology;
3. rebuilds row count and statistics;
4. publishes the new Chalk source snapshot.

It always re-describes even if the registration returns the same object, because a live set can have
been mutated in place.

Use the delegate registration when the host may swap the entire set:

```csharp
IndexedSet<Order> current = BuildIndexedSet(initialRows);

var source = AkadeSource
    .From("orders", () => current)
    .Build();

current = BuildIndexedSet(nextRows);

await engine.RefreshAsync(r => r.Refresh(source));
```

### `Refresh(source.Table)`

Table-scoped refresh:

1. re-reads the registration;
2. re-describes the single table and recomputes statistics;
3. does **not** discover new Akade indexes.

The newly returned set must therefore have the same supported Akade topology. If topology changed,
the table refresh is refused and the host must use `Refresh(source)`.

For the common live-mutation case this is the inexpensive conceptual operation:

```csharp
set.Add(...);
set.Update(...);

await engine.RefreshAsync(r => r.Refresh(source.Table));
```

It refreshes planner metadata without asking Chalk to rebuild the physical Akade collection.

A host that continues mutating while a scoped refresh is collecting statistics accepts correspondingly
live/fuzzy metadata. Scoped refresh cannot manufacture snapshot isolation around mutations performed
outside Chalk.

## `StatisticsRefresh.Defer`

For transactional `Append`/`Replace`, `StatisticsRefresh.Defer` carries the previous column value
distributions forward but never the row count. The successor's row count and physical/table shape are
fresh; only distributions age.

The next scoped refresh recomputes them.

## Topology rules

Supported Akade index topology is part of the published table shape.

- `Refresh(source)` may rediscover it.
- `Refresh(table)` must preserve it.
- `Replace` / `Append` must preserve it.

The reflection adapter fingerprints supported indexes by Akade index name, physical kind, key type,
accessor method identity and key members. A successor rebuilt with different supported topology is
rejected rather than silently changing the planner's access paths.

Unsupported Akade index families remain invisible to Chalk until there is a matching planner/runtime
access-path representation.

## Execution backend

Each Akade snapshot owns an internal one-table `PocoSource`, which reuses Chalk's existing POCO column
inference, statistics and Arrow/columnar scan machinery. Akade-specific index lookup remains the
physical-index seam.

This means transactional publication preserves the existing POCO pattern:

```text
PrepareRefreshAsync
    build complete successor
    validate topology/statistics
    return commit

Commit
    atomic source-snapshot publish only
```

while live host mutation deliberately bypasses those snapshot guarantees.

## Current catalogue boundary

Member indexes — one member, or a tuple of two to four — are registered through
`PocoTableBuilder<T>`'s late-bound host-index API, so the POCO builder resolves each member to its
final catalog column ordinal after naming rules have been applied. The per-snapshot factory closes
over the exact `IndexedSet<T>` used by that Akade snapshot; scan rows and index lookups therefore
address the same physical set.

A compound key is written in one of two ways, and Chalk reads both:

```csharp
.WithIndex(x => (x.ProductId, x.UnitPrice))     // the members are in the text Akade files it under
.WithIndex(PurchaseKeys.ProductAndUnitPrice)    // the text names no members, so the host says
```

For the second, the host names them once, against the same accessor:

```csharp
AkadeSource.From("purchases", set)
    .CompoundIndex(PurchaseKeys.ProductAndUnitPrice, x => x.ProductId, x => x.UnitPrice)
```

The accessor must return a `ValueTuple` of exactly those members' types, in that order; anything else
is refused at registration, by name, rather than becoming a key claim the planner would act on.

Expression-valued Akade keys remain deliberately undisclosed. The current `IndexDescriptor`
represents keys as base-table `Columns`, so these cannot yet be described truthfully:

```csharp
.WithIndex(x => x.End - x.Start)
.WithIndex(ComputedKey.SomeStaticMethod)
```

A future catalogue change can make index keys expression-valued using Chalk's scalar IR. Until then,
computed and multi-key Akade structures remain usable through Akade itself but are not advertised to
Calcite.


## CHALK002: ConcurrentIndexedSet

The `ConcurrentIndexedSet<T>` overloads of `AkadeSource.From(...)` are marked
`[Experimental("CHALK002")]`.

Akade's `Read(...)` contract materialises the returned sequence while its reader lock is held. Chalk
does not use that path for execution because a full scan would incur an O(n) copy before streaming.

Instead `ConcurrentIndexedSetAccess.CaptureForQuiescentRead` uses the public stateful `Read(...)`
overload to capture the wrapped `IndexedSet<T>` and returns `Array.Empty<T>()` from the callback.
Akade therefore materialises only an empty result during capture. Chalk subsequently streams directly
from the captured set.

The reference deliberately outlives Akade's reader lock. The host must guarantee that no mutation
overlaps any Chalk execution, scoped refresh, or transactional-refresh preparation reading that
source.

Index discovery uses the same capture helper, so Chalk no longer reflects into
`ConcurrentIndexedSet<T>` itself. Reflection remains only for Akade's private index registries and
selector metadata.


## Planner-visible Akade indexes

The adapter now uses `PocoTableBuilder<T>`'s late-bound host-index registration overload. Akade
supplies the member selector and physical index; `PocoTableBuilder` resolves that selector to the
final POCO column ordinal after naming/ignore rules have been applied.

What is supported:

- unique and non-unique Akade indexes become Chalk `HASH` indexes;
- Akade range indexes become Chalk `ORDERED` indexes;
- Akade prefix indexes — tries — become Chalk `PREFIX` indexes;
- unique Akade indexes carry `Unique = true`;
- the key is one direct member, or a tuple of two to four of them, in tuple order;
- a hash index reports its own distinct key count, which the planner would otherwise guess;
- computed, multi-key, nullable and specialised full-text/spatial/vector access paths remain
  undisclosed until Chalk can represent and verify their semantics faithfully.

## `LIKE 'p%'` and the prefix index

`WHERE name LIKE 'Int%'` is a range, not a predicate: one trailing `%`, no other wildcard and no
`ESCAPE`. The planner turns such a pattern on a STRING key column into a **prefix range**, and what
the source is finally asked for depends on the index's kind:

- an **ordered** string index is sent the plain half-open range `[Int, Inu)` — the prefix and the
  smallest text above everything it matches — so it serves a `LIKE` with no change at all;
- a **prefix** index is sent the prefix itself, and Akade's trie walks straight to it.

A pattern with more than a prefix in it — `'%USDT'`, `'a%b%'`, anything with `_`, anything with an
`ESCAPE` — is left where it was, as a predicate evaluated per row. A parameter's text is the one
thing the plan could not know, so it is checked when it is bound and refused by name if it turns out
not to be a bare prefix.

A trie's fuzzy search is deliberately not an access path: Chalk's ranges say "between these bounds"
and "starting with this text", and there is no range that says "within one edit of".

For the README `Purchase` example this means the planner sees `Id`, `ProductId`, `Amount` and
`UnitPrice`, and `PurchaseKeys.ProductAndUnitPrice` as soon as the host names its two members;
`PurchaseKeys.Total` is a computed key and remains Akade-native only.

A compound key's bounds become a tuple. Chalk may bound only a prefix of the key, and a tuple cannot
say "anything" for the rest, so the components a range does not reach take their type's minimum or
maximum according to the bound's inclusivity: `product_id >= 4` is `(4, min)`, and `product_id > 4`
is `(4, max)`, because filling with the minimum there would keep every row whose `product_id` *is*
4. Tuples are structs and the key order is compiled over their fields, so the check below still
costs one comparison and no allocation per row.

Registration closes each `IPocoIndex<T>` factory over the exact `IndexedSet<T>` used by the enclosing
Akade source snapshot. The concurrent source captures its inner `IndexedSet<T>` once and gives that
same instance to both the row wrapper and every registered index, so a scan and an index lookup
cannot accidentally address different Akade states.

## Key types and the comparer an index was built with

An `ORDERED` index is a claim that its keys arrive in Chalk's order — numbers and temporals by
value, NaN above every number, strings by code point — and the planner deletes sorts on the strength
of it. So the key type decides what Chalk will claim:

- the integer, decimal and temporal types (`DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly`,
  `TimeSpan`) are ordered access paths on the strength of the type alone, because the CLR's default
  order for them already is Chalk's;
- `float`, `double`, `string` and `Utf8String` are not, until the host says what the index was built
  with — `Comparer<double>.Default` puts NaN first where Chalk puts it last, and
  `Comparer<string>.Default` is culture-aware where Chalk compares by code point;
- `Guid` stays equality-only: its CLR comparison is not the byte order Chalk sorts UUIDs by;
- nullable keys stay excluded altogether.

`ChalkComparers.For<T>()` is Chalk's order as an `IComparer<T>`, for every type above and for a
tuple of them; the host builds the index with it and declares it:

```csharp
var set = rows.ToIndexedSet()
    .WithRangeIndex(x => x.Symbol, ChalkComparers.For<string>())
    .Build();

AkadeSource.From("bars", set)
    .Comparer(x => x.Symbol, ChalkComparers.For<string>());
```

The declaration is matched to the index by the text the compiler records for the accessor, which is
the text Akade files the index under, or by the accessor's method identity. Chalk then classifies
the comparer rather than trusting it: `Comparer<T>.Default` where that is Chalk's order, the
comparers above, and `StringComparer.Ordinal`. Anything else is refused at registration, by name,
with the accepted ones listed.

`StringComparer.Ordinal` comes with a caveat worth stating. It compares UTF-16 code *units*, and
Chalk's STRING order is by code point; the two agree everywhere except across the surrogate range,
where a code point above U+FFFF is stored as a pair of units below U+E000. A key from that range
makes the index arrive out of Chalk's order, and the order check below reports it by name at that
row rather than answering wrongly.

`ChalkComparers.For<T>(descending: true)` makes a descending index. Chalk registers it as one, and
the planner then serves `ORDER BY … DESC` from it without a sort — which, under a `LIMIT`, is one
seek and one row where it used to be a sort of the whole table.

## What the adapter asks Akade, and why only that

Akade's public documentation describes range indexes as supporting range predicates and ordered
access, and documents `OrderBy(...)` as the order the index defines — but it does not make the
enumeration order of `Range(...)`, `GreaterThan[OrEqual](...)` or `LessThan[OrEqual](...)` part of
the public contract.

Measured against Akade 1.5.0, there is a sharper reason than order to be careful here:
`GreaterThan[OrEqual](...)` and `LessThan[OrEqual](...)` do not honour the comparer the index was
built with. On an index whose order is not the CLR's default for the key type they return the wrong
rows, and usually none at all. `Range(...)`, `Min()`, `Max()` and `OrderBy(...)` do honour it. So a
half-open Chalk range becomes a `Range` from the bound to the index's own extreme on the open side,
and the adapter calls none of the one-sided shapes. An adapter of your own should do the same.

Chalk used to sort the matched rows before publishing any of them through an `ORDERED` index. It no
longer does: a consumer that reads one row and stops — `ORDER BY amount LIMIT 1` over an ordered
index — would have paid for the whole range before seeing it. The adapter now yields Akade's
enumeration as it comes and **verifies it as it yields**: one key comparison per row against the
previous key, nothing allocated per row, and a `SourceContractException` naming the Akade index and
the two keys the moment a row arrives out of ascending order. An unbounded ordered lookup uses
Akade's documented `OrderBy(...)`.

Failing by name is deliberate. A silently re-sorted lookup would be a wrong answer already relied
on, because the declared ordered index is why there is no sort above it in the plan at all; and a
defensive sort would give back exactly the early exit the ordered index is worth having for.

## Reading an ordered index backwards

`ORDER BY ts DESC LIMIT 1` — the latest row — is the commonest ordered query there is, and an
ascending index used to serve it by sorting everything it matched. An ordered Akade index now says
it can be read from its last row to its first, and the planner offers that beside the forward
lookup; a `LIMIT` above it then makes the whole thing one walk and one row.

What is offered is a range with **no upper bound**, the whole index included. That is what Akade's
public surface serves without a copy: `OrderByDescending` starts at the top, every row down to the
lower bound is wanted, and the walk simply stops at the first key below it — no skipping and no
buffering. A range with an upper bound is not offered backwards, because reaching it would mean
skipping every row above it or buffering the matched range to reverse it, which is exactly the copy
this adapter exists without; the planner sorts those instead. So `symbol = ? ORDER BY ts DESC` over
a compound index still sorts, and will until Akade grows a descending bounded enumeration.

The order check runs on the reversed walk too, against the order that walk claims — the bound and
the order are read from the same key, so it is still one key read and one comparison per row.
