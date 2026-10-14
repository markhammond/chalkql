# Chalk.Incremental: standing views

> **Experimental: incremental maintenance.** `ViewMaintenance.Incremental` carries the diagnostic
> `CHALK003`, which a project acknowledges before it can ask for it: `<NoWarn>$(NoWarn);CHALK003</NoWarn>`
> in the project, or `#pragma warning disable CHALK003` where it asks. Until it is declared stable,
> its behaviour and its API may change in any release; check what a maintained view gives against its
> statement before relying on it. A view that recomputes carries no such mark.

A standing view is a statement kept current as one row per key. A reader drains it from a cursor and
gets the latest row of each key that changed since, never a queue of every change. Deletes arrive as
deletes, and a reader that falls too far behind gets the whole result again, marked as a reset.

By default each update recomputes the statement in full and compares the result with the rows the
view already holds. A view made with `Maintenance = ViewMaintenance.Incremental` computes only what
changed instead: its statement's plan runs on the changes to its tables, and its joins, as-of joins
and aggregates keep state between updates (see [Incremental maintenance](#incremental-maintenance)).

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
    LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows, // required: the most a cross-source join may fetch
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

A reader is sent the whole result when it is new, and when its changes reach `ResetWhen` of the
view's rows (1 by default). A host resets one at any moment by starting its cursor over. A view
prepares its statement again once it is stale, after a shape change or a tenfold move in a table's
rows; its readers keep their place when the result's columns and key are as they were. To check a
view against its statement, make it from the host's own prepared statement,
`engine.MaterializeAsync(prepared, options)`: a run of that statement at the view's epoch gives
the view's rows bit for bit, and its checksum.

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
- **A source whose rows change outside the engine's refreshes**, such as a database, unless the
  view sets `DatabaseReads = ViewDatabaseReads.WhenRefreshed`: then it reads the database when it is
  made, keeps what it read, and reads it again only after a refresh names the table. Otherwise,
  load what the view needs into an in-process table and refresh it in epochs.

A key the result gives twice faults the view, naming the key, until the view is disposed.

## Incremental maintenance

```csharp
await using var view = await engine.MaterializeAsync(sql, new ViewOptions<CellKey, Cell>
{
    Key = ["row_key", "column_key"],
    LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
    Maintenance = ViewMaintenance.Incremental,  // compute only what changed
    Map = Cells.Map,
});
```

- A table gives the rows appended since the last update. A `Replace` that keeps the rest of the rows,
  the same objects in their order, gives the rows it took out and appended, as a compaction does; any
  other change gives its old rows out and its new rows in. A replacement prepared off the refresh lock
  and committed later reaches the view the same way, and a compaction that takes out only rows no
  as-of join matches moves no match.
- A plan the background made under `DeferPlanning()` is taken at the view's next update, maintained
  from the start; a plan the view cannot maintain is declined, as the one it keeps answers the same
  rows.
- A view may be built with a tracker of what its updates cost (`ViewOptions.Tracker`), and with a
  host callback that may veto requesting a new plan for drift (`ViewOptions.RefreshQueryPlan`): the
  statement keeps its plan, and the next refresh that defers its planning asks again.
- A view may set the fraction of its plan's cost a new plan must save
  (`ViewOptions.PlanImprovementMargin`): the planner holds back a plan that saves less.
- Joins (inner and left), as-of joins, aggregates with grouping sets, filters, projections,
  `UNION ALL`, `VALUES` and `WITH` subtrees are maintained, the clock in a filter or a projection,
  and an index lookup pinned to keys, as a constant key or an `IN` list makes, which is read as its
  table is, kept to those keys.
- An as-of join that seeks an index keeps only its left rows and the rows they match, never the
  table's, so its state does not grow with the table's history; it keeps each key's last row too,
  so a row at the clock needs no seek.
- A `DOUBLE` or `REAL` sum is kept exactly and rounded once, so it never drifts; a recompute adds in
  input order, so the two may differ in the last bits.
- What the statement keeps counts in `StateBytes`, against `StateBudget`.
- An update that fails part way leaves the next to start from nothing; readers get only the keys
  that differ.
- A statement maintenance does not keep — window functions, `LIMIT`, set operations but `UNION ALL`,
  right, full, semi and anti joins, `DISTINCT` and ordered aggregates, an index lookup over a range
  of keys, the reference executor — is refused when the view is made, with `UnsupportedInView`,
  naming it.

## Inputs

A POCO table may declare how it changes:

| Declaration | Changes by | Late rows |
|---|---|---|
| `AppendOnly(time, lateness)` | `Append` | refused past the frontier less the lateness |
| `AsOf(time, keys…)` | `Append` | the same; indexed on the keys, then the time |
| `Keyed(keys…)` | `Upsert`, `Delete` | — |

A late row refuses the whole refresh it came in, naming the row. The default lateness is zero.
