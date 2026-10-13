# Chalk.Incremental: standing views

A standing view is a statement kept current as one row per key. A reader drains it from a cursor and
gets the latest row of each key that changed since, never a queue of every change. Deletes arrive as
deletes, and a reader that falls too far behind gets the whole result again, marked as a reset.

> **As of writing, only the delivery is incremental.** Each update recomputes the statement in full
> and compares the result with the rows the view already holds. Support for computing only what
> changed, in the manner of Differential Dataflow, is aspirational: the API is shaped so that it could
> be added without changing anything a host writes, but it is not built.

The view keeps the rows the host maps them to, so a drain copies the host's own structs into the
host's own sink, and keeps a checksum of them as they change.

## A view in five steps

```csharp
using Chalk.Incremental;

// 1. Inputs say how they change.
var desk = new PocoSourceBuilder("desk")
    .AddTable("fills", () => fills, t => t.AppendOnly(f => f.Ts))
    .AddTable("marks", () => marks, t => t.AsOf(m => m.Ts, m => m.Asset))
    .AddTable("positions", () => positions, t => t.Keyed(p => p.Book, p => p.Asset))
    .Build();

// 2. The engine names its clock: @now, in a view, reads it.
await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
{
    Sources = [desk],
    Planner = planner,
    Clock = "now",
});

// 3. A view keeps one row per key, mapped to the host's own structs.
await using var view = await engine.MaterializeAsync(sql, new ViewOptions<CellKey, Cell>
{
    Key = ["row_key", "column_key"],
    Map = Cells.Map,          // (in ViewRow row, out CellKey key, out Cell cell)
    RowHash = Cells.Hash,     // optional: a hash of each mapped row, summed as the checksum
});

// 4. One refresh per tick: atomic over every input, numbered with the host's version.
await engine.RefreshAsync(epoch => epoch
    .Version(version)
    .Append(desk, "fills", newFills)
    .Upsert(desk, "positions", changedPositions)
    .AdvanceClock(now));

// 5. Bring the view up to date, then drain what changed since the cursor.
await view.UpdateAsync();
var outcome = view.Drain(ref cursor, sink, budget: 4096);    // Complete, or Partial: call again
```

The sink is the host's:

```csharp
public interface IViewSink<TKey, TRow>
{
    void Reset();                            // the whole result follows
    void Upsert(in TKey key, in TRow row);
    void Delete(in TKey key);
    void Commit(long version, uint checksum);
}
```

## What a view promises

After each update, a view's rows are exactly what its statement returns when run at that epoch.
So a view refuses, when it is made, what would make that untrue or unstable:

- **Floating-point accumulation**, such as `SUM` over `DOUBLE`, unless the view opts in with
  `Exactness = ViewExactness.EqualUpToRounding`. Cast to `DECIMAL` to stay exact.
- **A function that is not deterministic**: a host function declared `Stable()` or `Volatile()`.
  With `RequireExplicitImmutable`, only one registered with `Immutable()` is admitted.
- **The current time without a clock**: `CURRENT_TIMESTAMP` and its kin read the engine's clock in
  a view.
- **An `ORDER BY` without a `LIMIT`**, since a keyed result has no order, and a `LIMIT` whose order
  does not include the view's key.
- **A source whose rows change outside the engine's refreshes**, such as a database. Load what the
  view needs into an in-process table and refresh it in epochs.

A key the result gives twice faults the view, naming the key, until the view is disposed.

## Inputs

A POCO table may declare how it changes:

| Declaration | Changes by | Late rows |
|---|---|---|
| `AppendOnly(time, lateness)` | `Append` | refused past the frontier less the lateness |
| `AsOf(time, keys…)` | `Append` | the same; indexed on the keys, then the time |
| `Keyed(keys…)` | `Upsert`, `Delete` | — |

A late row refuses the whole refresh it came in, naming the row. The default lateness is zero.
