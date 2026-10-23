# Chalk.Sample.Blotter — a virtual trade blotter over millions of trades

A Blazor Server page with a QuickGrid over five million client trades and their fills. The trades
are in a database the host cannot hold in memory, and a market keeps sending new ones. The grid
scrolls through all of them, sorts by any columns, filters, and flashes what changes. The host keeps
everything it holds within a memory budget you set on the page (64 MB by default), and it never
shows a stale or missing version to stay within it.

```bash
JAVA_HOME=/path/to/jdk-21 dotnet run -c Release --project dotnet/samples/Chalk.Sample.Blotter
```

Then open `http://localhost:5910`. Options:

| Option | Default | |
|---|---|---|
| `--trades 500k` | `5m` | the corpus generated at start (about 1.6 s for five million) |
| `--budget-mb 32` | `64` | the host's memory budget; the page can change it while it runs |
| `--maintenance recompute` | `incremental` | how the live scopes keep current (below) |
| `--urls http://localhost:5911` | `http://localhost:5910` | |
| `--probe` | | no web app: prints the plans, then checks random pages against the oracle for 45 s (`PROBE_SECONDS`) |

ChalkQL's planner runs on a JDK 21+, which `PlannerProcess` starts itself: set `JAVA_HOME`, or install
Homebrew's `openjdk@21`, which the sample looks for.

## What you see

- **A virtual grid.** QuickGrid's `Virtualize` asks for rows by position, and the host answers for any
  position in the five million, in 5–150 ms near the top or under a filter and in up to about 650 ms
  for the deepest pages of an unfiltered sort.
- **Sorting by any columns.** A click on a header sorts by that column, and a second click turns it
  round. A shift-click adds the column to the sort; the header shows each column's place in it.
  QuickGrid sorts by one column itself; its multi-column sort is a pull request
  ([dotnet/aspnetcore#62604](https://github.com/dotnet/aspnetcore/pull/62604)) not yet merged, so
  the page draws its own headers.
- **Filters:** clients and symbols (lists), side, statuses, and a range of trade times, all bound as
  parameters.
- **A new sort or filter dims the grid** until the first page of the new one is in. The old rows stay
  until then, so the grid is never empty between the two. A result for a sort or filter the reader
  has since left is dropped.
- **Flashes.** When a trade on screen has a new version, the cells that changed flash. A trade the feed
  has just opened flashes its whole row. A trade scrolled into view does not flash.
- **New rows push down at the top.** Scrolled away from the top, the rows in view stay where they
  are, and an "N new above" badge counts what has arrived. A click on the badge, or a scroll back to
  the top, shows them.
- **Every version of a trade.** The `v3 ↗` link opens the trade's history in a new tab. One statement
  reads it from both the database and the live tail, and marks the versions the database does not
  have yet.
- **The status bar**: the snapshot's generation and its age, how far the database is behind the
  feed, the tail, what the host holds against the budget, DuckDB's own memory, and the generation
  turnovers.
- **Burst 3,000** has the exchange send 3,000 versions at once. **Stall database 20 s** stops the
  database writing while the feed goes on. Do both with a small budget to see the host refuse.

## How it works

The market is simulated. An exchange opens trades and fills them; each fill is a new version of its
trade, numbered one higher than the last. Versions are ordered per trade only: nothing orders two
trades' versions. The feed sends every version the moment it exists, reliably, and can replay from
any time in the last 30 minutes. The database (DuckDB, in memory, standing in for a database in
another process, with an 8 GB limit) writes each version some time later, 150 ms to 3 s, and
sometimes stalls. Alongside the versions it keeps a watermark: every version at or before it is in.

The blotter is two halves, merged.

1. **A snapshot generation** in the database: the latest version of each trade, written by one
   `INSERT … SELECT … QUALIFY row_number() …` into `snapshots` under a generation number, after the
   watermark is read. It takes about 200 ms over 12.5 million versions. Pages of it are read
   through ChalkQL with the filter, the sort, the `LIMIT` and the `OFFSET` pushed down into DuckDB's
   own SQL. They are read in aligned chunks of 256 rows and cached by generation, filter and sort: a
   generation never changes, so a chunk stays right for as long as its generation is read.
2. **The live tail**: every trade whose latest version the snapshot does not have, as of the feed
   replayed from the snapshot's watermark and then live. It is a keyed POCO table in ChalkQL. Each
   row has the trade's latest version and the snapshot's own row for that trade, and the versions
   behind it sit in a second table.

**Correctness.** The snapshot holds everything through its watermark, and the replay sends everything
after it, so the two together hold every version. Where they overlap, in the in-flight crossing, the
higher version wins. So neither the database's lag nor the crossing can show a stale or a missing
row. Each page is checked as it is put together: its rows' positions must follow one another, or the
page is not shown.

**Live scopes.** For each distinct filter on the open pages, a ChalkQL standing view over the tail
(`LiveScope`) holds every trade that passes the filter now, or whose snapshot row did. Pages that
filter alike share one scope, whatever they sort by and wherever they are scrolled. The scope is
maintained incrementally, so an update costs what changed, and a page sorts its scope in the host,
which is cheap because a scope is a slice of the tail, never of the corpus. This is the "small
incremental view that mirrors the client's view": a new trade that falls within the scope is placed
by sorting and counting, without querying the database.

**Placing a row.** A snapshot row at position `j` of the snapshot's own order is at position
`j − (overridden rows before it) + (tail rows before it)` of the blotter. A tail row is at
`(tail rows before it) + (snapshot rows before it) − (overridden rows before it)`. Both counts of the
tail come from the scope, sorted in the host. So a page at position `i` reads the snapshot near `i`,
corrected by the tail, places every row of that stretch, and reads further when the stretch does not
cover the page (`Pages`). A row that keeps its place while trades arrive above it keeps its sort key,
and its position is worked out again by the same counts.

**Generations turn over.** When the tail and the scopes hold 40% of the budget, or every 30 s, the host
reads the watermark again and has the database write a new generation in the background. Then, in
one refresh, it drops from the tail every trade the new generation has caught up with, and moves the
rest onto the new generation's rows. Pages go on reading the previous generation until their next
state, so the swap does not show.

**The budget** covers what the host holds: the tail, in ChalkQL and in the host; the scopes' views and
their states; and the page cache. The page cache gives way first. Past the budget, the host takes a
new generation at once. If that is still not enough, because the database is too far behind, the host
stops serving and says why on the page. It never drops a version to stay in budget. It starts again
from a new generation and a new replay once the database has caught up, and the pages take their
scopes again by themselves. A filter whose scope would not fit is refused when it is asked for,
saying how much it would hold.

## Checking it

`Oracle` works out a page the slow way, in DuckDB alone. It takes the state's generation and every
version the feed sent up to the state's `IngestedThrough`, keeps each trade's highest version, then
filters, sorts and pages them in one statement. `--probe`, and
`tests/Chalk.Sample.Blotter.Tests/BlotterHostTests.cs`, compare random pages (random filters, sorts
of one to three columns, random positions) with the oracle while the market runs, through bursts,
stalls and turnovers. On 2026-10-23: 86 pages at five million trades with seven turnovers, and every
test run since, with no page wrong.

## What it found in ChalkQL

Registered in the design log as F273–F276:

- **F273.** A pushed-down query names a table by its catalog name, not its registered remote name, so
  the sample keeps its tables in DuckDB's `main` schema, and its SQL reads `main.snapshots`.
- **F274.** A DuckDB `TIMESTAMP` that discovery finds without a precision is typed `TIMESTAMP(9)`, finer
  than DuckDB keeps, so the planner sorted on it locally: every row the filter passed, pulled into the
  host. The sample declares its tables' columns with `TIMESTAMP(6)`, and every sort is pushed.
- **F275.** Discovery in a schema the database lacks fails with a SQLite pragma error.
- **F276.** An incremental scope holds about 440 bytes a tail row where a recomputing one holds about
  100. Run with `--maintenance recompute` to compare: it holds a quarter of the memory, and costs the
  whole tail per update.

## Not here, yet

- Free-text search.
- A database in another process: DuckDB 2.0's client-server mode is the candidate, once DuckDB.NET
  supports it.
- Tapline. On Blazor Server the grid renders on the server, and ChalkQL's views already give each
  reader what changed. Tapline would carry the same answers to a WebAssembly grid, headless clients or
  many nodes. `LiveScope`, `ScopeState` and `Pages` are the seam.
- A writer-kept current-state table. Without one, the sample derives each generation from the
  versions.
- Memory figures are the host's own estimates of what it holds, and the views' own count of their
  state. The status bar shows the process's managed heap beside them for comparison.
