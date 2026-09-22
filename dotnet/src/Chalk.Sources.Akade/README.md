# Chalk Akade source

This implementation uses the agreed model:

> one `IndexedSet<T>` -> one Chalk source -> one logical table

`AkadeSource.From(...)` infers `T` and returns either `IndexedSetSourceBuilder<T>` or
`ConcurrentIndexedSetSourceBuilder<T>`. `Build()` returns the corresponding typed source, whose
`Table` is an `ITableTarget<T>`.

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
- unique Akade indexes carry `Unique = true`;
- the key is one direct member, or a tuple of two to four of them, in tuple order;
- a hash index reports its own distinct key count, which the planner would otherwise guess;
- computed, multi-key, nullable and specialised string/spatial/vector access paths remain
  undisclosed until Chalk can represent and verify their semantics faithfully.

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
Descending enumeration of an *ascending* index is not offered, so `ORDER BY … DESC` over one still
sorts; an index the host built descending serves it directly.
