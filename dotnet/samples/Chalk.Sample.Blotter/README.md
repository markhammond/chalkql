# Chalk.Sample.Blotter — a virtual blotter of client orders and fills

A Blazor Server page with two QuickGrid tabs: **client orders** (five million by default), each with
its fills nested beneath it, and the **fills** themselves. Both live in a database the host cannot
hold in memory, while the house keeps taking orders and filling them. Each tab scrolls through all
of its rows, sorts by any columns, filters on any column, and flashes what changes. The host keeps
what it holds within a memory budget you set on the page (64 MB by default), stops any statement
after 5 s, and never shows a stale or missing version to stay within either.

```bash
JAVA_HOME=/path/to/jdk-21 dotnet run -c Release --project dotnet/samples/Chalk.Sample.Blotter
```

Then open `http://localhost:5910`. Options:

| Option | Default | |
|---|---|---|
| `--orders 500k` | `5m` | the corpus generated at start: orders, their versions and their fills (about 9 s for five million orders, 12.5 million versions and 8.8 million fills) |
| `--budget-mb 32` | `64` | the host's memory budget; the page can change it while it runs |
| `--maintenance recompute` | `incremental` | how the live scopes keep current (below) |
| `--urls http://localhost:5911` | `http://localhost:5910` | |
| `--probe` | | no web app: prints the plans and estimates, then checks random pages of both tabs against the oracle for 45 s (`PROBE_SECONDS`) |

ChalkQL's planner runs on a JDK 21+, which `PlannerProcess` starts itself: set `JAVA_HOME`, or install
Homebrew's `openjdk@21`, which the sample looks for.

## The domain

The reference data is the Tapline PivotTable sample's: twelve clients trading the house's linear
perpetuals (`BTC/USD Perp` and seven others) in US dollars, each in its region and through its
channels (FIX, API, GUI, Mobile, Voice), at the mark plus its channel's markup. A **client order** is
versioned: a fill or a cancel is a new version, numbered one higher, with its filled quantity, its
average price (the fills', weighted) and its status (New, Partial, Filled, Cancelled). A **fill** never
changes, and carries its order's details (client, region, channel, instrument, side), so a fill can be
filtered without its order. Orders and fills are identified by UUIDs (version 7), and nothing in the
host relies on their order: a new order is looked up in the snapshot like any other, and ids only break
ties in a sort.

Order versions and fills come on **two streams**, each in its own time order and neither waiting for
the other. The order stream runs up to 600 ms behind the fills, and a quarter of new orders are filled
at once, so a fill often arrives **before its order**, or before the version that counts it.

## What you see

- **Two tabs.** Client orders, and fills. Each has its own sort, filters and position. The tab bar
  shows the tab's rows (`~rows`: counted in the database at the snapshot, with the tail's added) and
  its sort (`sort by`).
- **A virtual grid.** QuickGrid's `Virtualize` asks for rows by position, and the host answers for any
  position in the millions: at five million orders, half the probe's pages took under 40 ms, and the
  deepest pages of an unfiltered sort of the 8.8 million fills up to about 3 s (DuckDB's top-N with an
  offset of millions; starting deep pages from a cached key instead is a next step).
- **Fills nested under their orders.** The ▸ in the first column opens an order's fills beneath it,
  newest first and set in, as rows of the grid's one list (`Virtualize` wants every row one height);
  the order's own row does not move. They stay open while new
  fills arrive. A fill that came before its order is there once the order comes.
- **From a fill to its order.** Hovering a fill's order number shows the order as both sides know it
  now. The card also says when the order has not arrived yet, or when its latest version does not
  count this fill yet. The link opens the order's page: every version and every fill, read from the
  database and the live tail in one statement each.
- **Sorting by any columns.** A click on a header sorts by that column; a second click turns it round;
  a shift-click adds the column to the sort, and the header shows each column's place in it. QuickGrid
  sorts by one column itself; its multi-column sort is a pull request
  ([dotnet/aspnetcore#62604](https://github.com/dotnet/aspnetcore/pull/62604)) not yet merged, so the
  page draws its own headers.
- **A filter on every column.** The funnel in each header opens the column's filter, and it fills in
  while the column is filtered. Active filters show as pills above the grid; × removes one, and
  "Clear all" removes them all.
  - Ids (order, fill): typed or pasted as pills, matched exactly.
  - Text (client, ClOrdId, instrument): pick values, or type them as pills (Enter or a comma adds one,
    × removes one, the cross at the end clears them), matched exactly or by what they **start with**.
    The chooser offers the column's most frequent values over the period of interest (the tab's time
    filter, or the last day), 50 at most, read from the database and narrowed as you type.
  - Region, channel, side, status: tick the values.
  - Numbers: at least, at most. Times: from, to (UTC).
- **The cost, before you scroll.** Under the filters, a line shows the planner's estimate for the tab's
  pages: how many rows the database reads and sorts, and whether the sort runs in the database or, as a
  warning, in the host. It also shows the rows the filter passes, counted, so the estimate can be judged.
- **A time limit.** Every statement is stopped after 5 s, and a page that hits it says so. The status
  bar counts those stopped.
- **A new sort or filter dims the grid** until its first page is in.
- **Flashes.** The cells of a row that changed flash, and an order or fill the streams have just sent
  flashes whole. A row scrolled into view does not flash.
- **New rows push down at the top.** Scrolled away from the top, the rows in view stay where they are,
  and an "N new above" badge counts what has arrived. A click on the badge, or a scroll back to the
  top, shows them.
- **The status bar**: the snapshot's generation and its age, how far the database is behind, how many
  order versions the order stream is holding back behind the fills, the tail, what the host holds
  against the budget, DuckDB's own memory, the statements run and stopped, and the turnovers.
- **Burst 3,000** makes 3,000 changes at once; **Stall database 20 s** stops the database writing
  while the streams go on. Do both with a small budget to see the host refuse.

## How it works

The database (DuckDB, in memory, standing in for a database in another process, with an 8 GB limit)
writes each stream's rows some time later, 150 ms to 3 s, and sometimes stalls. It keeps a
watermark for each stream: every order version, or every fill, at or before it is in.

Each tab is two halves, merged.

1. **A snapshot generation.** For orders: the latest version of each order, written by one
   `INSERT … SELECT … QUALIFY row_number() …` into `snapshots` under a generation number, after both
   watermarks are read. For fills, nothing is written: they never change, so a generation's fills are
   those at or before its fills watermark. Pages are read through ChalkQL with the filter, the sort,
   the `LIMIT` and the `OFFSET` pushed down into DuckDB's own SQL, in aligned chunks of 256 rows
   cached by tab, generation, filter and sort.
2. **The live tail**: every order whose latest version the snapshot does not have, and every fill
   after the fills watermark. The streams are replayed from each watermark and then followed live,
   each read apart from the other. The orders are a keyed POCO table in ChalkQL; each row carries the
   order's latest version and, column for column, the snapshot's row for it. The versions behind an
   order, and the fills, are tables of their own.

**Correctness.** The snapshot holds every order version through the orders watermark, and the replay
sends everything after it. Where the two overlap, in the in-flight crossing, the higher version wins.
Fills cannot overlap at all: the database's are read up to the watermark and the tail's start after
it. So neither the database's lag, nor the crossing, nor a fill arriving before its order can show a
stale or a missing row. Each page is checked as it is put together: its rows' positions must follow
one another, or the page is not shown.

**Live scopes.** For each tab and distinct filter on the open pages, a ChalkQL standing view over the
tail (`LiveScope`) holds what the filter can see. For orders, that is every order that passes the
filter now, or whose snapshot row did: its two sets of columns let the filter be tested against both
in one statement. For fills, it is every fill in the tail that passes. Pages that filter alike share
one scope, whatever they sort by and wherever they are scrolled. A page sorts its scope in the host,
which is cheap because a scope is a slice of the tail, never of the corpus.

**Placing a row.** A snapshot row at position `j` of the snapshot's own order is at position
`j − (overridden rows before it) + (tail rows before it)` of the tab. A tail row is at
`(tail rows before it) + (snapshot rows before it) − (overridden rows before it)` (`Pages`). Nested
fills are let in after their orders, each opened order moving the rows below it down by its fills
(`Nesting`).

**Generations turn over** when the tail and the scopes hold 40% of the budget, or every 30 s. In one
refresh the tail drops every order the new generation has caught up with and every fill at or before
its watermark.

**The budget** covers what the host holds: the tail, in ChalkQL and in the host; the scopes' views and
their states; and the page cache. Past it, the page cache gives way first, then the scopes no page is
using, then the tail, by a new generation taken at once. If that is still not enough, because the
database is too far behind, the host stops serving and says why. It never drops a version to stay in
budget. It starts again once the database has caught up, and the pages take their scopes again by
themselves.

## Checking it

`Oracle` works out a page the slow way, in DuckDB alone. For orders: the state's generation and every
version the order stream sent up to how far the state has read it, keeping each order's highest
version. For fills: every fill the fill stream sent up to the state's mark, from the database and the
stream, whatever the generation. Then it filters, sorts and pages them in one statement with the
filter's values written in.

`--probe`, and `tests/Chalk.Sample.Blotter.Tests/BlotterHostTests.cs`, compare random pages of both
tabs with the oracle while the market runs, through bursts, stalls and turnovers. The pages use random
filters on one or two columns of every kind (picked values, starts-with, ranges, times), random sorts
of one to three columns, and random positions. On 2026-10-23, 79 probe pages over three generations,
and every test run since, with no page wrong. The tests also cover:

- fills that arrive before their orders;
- the time limit;
- the budget's refusals;
- rows held in place while rows arrive above them;
- an order's history and fills read from both sides.

`SortSpecTests`, `FilterSpecTests`, `FlashesTests` and `NestingTests` cover the host-side rules.

## What it found in ChalkQL

Registered in the design log as F273–F276; design 79 proposes what the owner asked for next.

- **F273.** A pushed-down query names a table by its catalog name, not its registered remote name, so
  the sample keeps its tables in DuckDB's `main` schema, and its SQL reads `main.snapshots`.
- **F274.** A DuckDB `TIMESTAMP` that discovery finds without a precision is typed `TIMESTAMP(9)`, finer
  than DuckDB keeps, so the planner sorted on it locally. The sample declares its tables' columns with
  `TIMESTAMP(6)`, and every sort is pushed.
- **F275.** Discovery in a schema the database lacks fails with a SQLite pragma error.
- **F276.** An incremental scope holds about 440 bytes a tail row where a recomputing one holds about
  100 (`--maintenance recompute` to compare).
- **Design 79** (proposed, not built): what a statement examines per source, refusing at prepare when
  no access path keeps the rows read under a ceiling the host sets, and a time limit for a whole
  statement and for a view's update. Until then the sample parses the plan text for its estimate and
  cancels statements after 5 s itself.

## Not here, yet

- Free-text search.
- A refusal by cost (design 79).
- A database in another process: DuckDB 2.0's client-server mode is the candidate, once DuckDB.NET
  supports it.
- Tapline. On Blazor Server the grid renders on the server, and ChalkQL's views already give each
  reader what changed. Tapline would carry the same answers to a WebAssembly grid, headless clients or
  many nodes. `LiveScope`, `ScopeState` and `Pages` are the seam.
- A writer-kept current-state table. Without one, each generation is derived from the versions.
- Memory figures are the host's own estimates of what it holds, and the views' own count of their
  state. The status bar shows the process's managed heap beside them for comparison.
