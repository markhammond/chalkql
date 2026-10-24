# Chalk.Sample.Blotter — a virtual trade blotter

A Blazor Server page with two QuickGrid tabs: **client orders** (five million by default), each with
its fills nested beneath it, and the **fills** themselves. Both live in PostgreSQL, which the host
cannot hold in memory, while the house keeps taking orders and filling them. Each tab scrolls through
all of its rows, sorts by any columns, filters on any column, and flashes what changes. The host keeps
what it holds within a memory budget, the smallest that serves of 8, 16, 32 and 64 MB unless you set
one (on the page, or at start), stops any statement after
5 s, and never shows an order at an older version than one it knows of to stay within either.

```bash
JAVA_HOME=/path/to/jdk-21 dotnet run -c Release --project dotnet/samples/Chalk.Sample.Blotter
```

Then open `http://localhost:5910`. The sample starts a PostgreSQL server of its own, in a temporary
directory, from PostgreSQL 16 or later installed on the machine (`brew install postgresql@16`), and
deletes it when it stops; one left by a sample that was killed is swept away by the next. Options:

| Option | Default | |
|---|---|---|
| `--orders 500k` | `5m` | the corpus loaded at start: orders, their versions and their fills |
| `--current lagging` | `trigger` | how `orders_current` keeps up with `order_versions` (below) |
| `--postgres "Host=…;Username=…;Database=…"` | | a server of your own instead: the sample makes a schema `blotter` there, refuses if one exists, and drops it when it stops |
| `--pg-bin /path/to/postgresql/bin` | found | the installation to start a server from |
| `--budget-mb 32` | found | the host's memory budget, fixed; without it, the host finds one (below); the page can change it while it runs |
| `--maintenance recompute` | `incremental` | how the live scopes keep current (below) |
| `--urls http://localhost:5911` | `http://localhost:5910` | |
| `--link-keys /path/to/folder` | `Chalk.Sample.Blotter/link-keys` under the user's local application data | where the keys that shroud shared links are kept (below) |
| `--probe` | | no web app: prints the estimates, then checks random pages of both tabs for 45 s (`PROBE_SECONDS`) (below) |

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
host relies on their order: ids only break ties in a sort.

Order versions and fills come on **two streams**, each in its own time order and neither waiting for
the other. The order stream runs up to 600 ms behind the fills, and a quarter of new orders are filled
at once, so a fill often arrives **before its order**, or before the version that counts it. The
streams are reliable from the moment a reader subscribes, and do not replay.

## The database

Three tables in the `blotter` schema, `UNLOGGED`, their text collated `"C"`:

- `order_versions`: every version of every order, keyed by order and version.
- `orders_current`: one row per order, at its latest version, indexed newest first. This is what the
  blotter's order tab reads.
- `fills`: every fill, keyed by fill, indexed by order and newest first.

Ingestion jobs write each stream's rows some time after the stream sends them, 150 ms to 3 s, and
sometimes stall. `orders_current` keeps up one of two ways (`--current`):

- **trigger** (the default): a trigger on `order_versions` upserts the order's row in the same
  statement, the newer version winning.
- **lagging**: a job of its own applies what `order_versions` has been written, 0.5 s to 4 s later,
  and stalls now and then, so the two tables are often out of step.

The corpus is generated in C#, each order worked out from its number alone, and written with binary
`COPY` on several connections at once before any index or trigger exists; then the indexes are built
in parallel. Five million orders, about 12.5 million versions and 8.8 million fills load in seconds.

## What you see

- **Two tabs.** Client orders, and fills. Each has its own sort, filters and position.
- **A virtual grid.** QuickGrid's `Virtualize` asks for rows by position, and the host answers for any
  position in the millions (below).
- **Fills nested under their orders.** The chevron in the first column opens an order's fills beneath it,
  newest first and each marked ↳, as rows of the grid's one list (`Virtualize` wants every row one height);
  the order's own row does not move. They stay open while new fills arrive. A fill that came before
  its order is there once the order comes.
- **From a fill to its order.** Hovering a fill's order shows the order as both sides know it now. The
  card also says when the order has not arrived yet, or when its latest version does not count this
  fill yet. The link opens the order's page: every version and every fill, read from the database and
  the live tail in one statement each.
- **Sorting by any columns.** A click on a header sorts by that column, descending; a second click
  turns it ascending; a third takes it out of the sort, back to the tab's own order, newest first, in
  which only Time shows an arrow, muted, as no one chose it. A click on Time there turns it ascending,
  as it is descending already. A shift-click does the same within a sort by several columns, adding
  the column last, and the header shows each column's place in it. QuickGrid sorts by one column itself; its multi-column sort is a pull request
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
- **The cost, on request.** "How this tab is answered", under the grid, shows the page's SQL, ChalkQL's
  plan for it, and PostgreSQL's own plan of the same page with the filter's values written in: newest
  first, a read of a hundred rows down the index; sorted by a column no index has, the whole table.
- **A time limit.** Every statement is stopped after 5 s, and a page that hits it says so. The status
  bar under the grid shows the limit, beside PostgreSQL's size, and counts the statements stopped.
- **Columns hold their widths.** Each column keeps the width it first took with rows in it, so the
  headings do not shift as the grid scrolls; a value wider than its column is cut short with an
  ellipsis.
- **Time first, and frozen.** Each tab leads with its time, `Time`: when the client placed the order,
  or when the order was filled. The first columns stay in place as the grid scrolls sideways, so every
  row can still be told apart: the expander, Time and Order on the orders tab; Time and Fill on the
  fills tab.
- **Choosing columns.** "Columns" at the right of the tab bar shows or hides any column but the frozen
  ones. Ver, Region, Channel and Filled start hidden.
- **Shown only.** Fill % (of an order) is worked out from the row in the host: shown, not sorted or
  filtered by, and refused if asked to be.
- **Times with their date, cut from the left.** A time shows as `… 03:26:23`, an ellipsis where its
  date is; on hover, it shows in full, date and microseconds, as an order's page does.
- **Numbers as finance writes them:** no zero before the point of a fraction, none after its last digit
  that counts (.25, not 0.2500), and a dash for nought. Prices are rounded to six significant figures,
  and written in scientific notation (1.23457e-5) where they would take more than eight digits. Every
  value is in the system's monospace, its zeros slashed. Columns 2em apart, each with a gutter at its left that
  holds its heading's funnel; a heading keeps room for its sort arrow whether or not its column is
  sorted.
- **To an order's page.** An order's id opens its page: every version and every fill, read from the
  database and the live tail in one statement each. A fill's id opens its order's page, that fill
  marked. The ↗ after the id shows while the pointer is on its row.
- **A new sort or filter dims the grid** until its first page is in.
- **Flashes.** The cells of a row that changed flash for 2.4 s, and an order or fill the streams have
  just sent flashes whole for 3 s. A row scrolled into view does not flash.
- **Copy and share.** On the tab bar, ⧉ copies the rows in view, at least half of each, under the
  headings of the columns shown: as text with a tab between values, which a sheet takes as cells, and
  as a table. Each value is as the grid shows it, but a time has its date and an id is whole. The link
  icon copies a link to the view, which opens it in a new window. The link holds everything, and
  nothing is kept for it: the tab, the sort, the filters, the columns hidden when they are not those
  hidden at first, and, scrolled away from the live top, the row first in full view, by its keyset
  (its value of each column sorted by, then its key). Opened, the row's place is counted, and the grid
  is scrolled there and held, counting what has arrived above it since; at the live top, the link
  holds no row and follows the top. A link not understood is refused whole, saying what, and the
  blotter opens as it always does. For example:
  `/?tab=fills&sort=-client,qty&hide=&f.side=Buy&f.qty.min=0.1&f.instrument.starts=B&at.client=FERN&at.qty=0.5&at.fill_id=…`.
- **Shrouded links.** The link the share button copies shows none of the view's data: the filters'
  values and the row held at, which are data, go in one opaque `s=`, encrypted and signed with ASP.NET
  Data Protection; the tab, the sort and the hidden columns, only how the view is laid out, stay as
  they are (`/?sort=-client&s=CfDJ8…`). The keys are kept in a folder (`--link-keys`), so a link opens
  after a restart; they rotate every 90 days, the old ones kept to open what they shrouded. A link may
  give any filter or the row held at as is, shrouded, or some of each, but not one part both ways; a
  shrouded part altered, or shrouded by another blotter's keys, is refused. The keys are kept as they
  are, not encrypted at rest: the folder is what keeps them private.
- **New rows push down at the top.** Scrolled away from the top, the rows in view stay where they are,
  and an "N new above" badge counts what has arrived. Scrolling up within 200 rows of the top lets
  them in, 30 at a time, above the rows in view, which stay where they are (the grid moves every
  position it holds, and the scroll with it, by as many rows), so scrolling on up reveals them in turn
  and the badge counts down. A click on the badge, or reaching the top however fast, goes back to the
  live top, where new rows push down again.
- **Rows cut by the grid's edges fade.** A band two thirds of a row high over the bottom edge, and over the top
  edge under the headings once the grid is scrolled down: plain CSS over the grid, nothing run as it
  scrolls. At the top, the newest row stays clear.
- **Above the grid**, whether the host is serving and what it holds against the budget. **Vital
  statistics**, under the grid, closed at first: the feed, and how many order versions the order
  stream is holding back behind the fills; how `orders_current` is kept (and, by the lagging job, how
  far behind it is); PostgreSQL's size, how far it is behind the streams and the statement time limit;
  the tail and what of it is confirmed; the statements run and stopped, and the reads and lookups pages
  made; and the confirmations, what they let go, and the gap fill. Closed, none of it is rendered, and
  the host does not ask PostgreSQL its size.
- **Chevrons** are drawn, one size wherever they are, set in em. Vital statistics and how the tab is
  answered open from one row under the grid.
- **"N new above"** sits across the top edge of the grid, over no more than the top quarter of the
  headings, so they and the first row stay readable.
- **Simulate burst** (shown, with the stalls, while the pointer is on the page) makes 3,000 changes at once; **Stall database 20 s** stops the database writing
  while the streams go on; in lagging mode, **Stall orders_current 20 s** stops only the job that keeps
  it. Do them with a small budget chosen on the page to see the host refuse.

## How it works

The blotter is a **bolt-on**. It asks of the database what any reader could: rows found by key, sorted
and filtered. It asks for no watermark, no snapshot, and no promise about when `orders_current`
catches up with `order_versions`.

Each tab is two halves, merged.

1. **The database, read live.** For orders, `orders_current`; for fills, `fills`. Read through ChalkQL
   with the filter, the sort and the limit pushed down into PostgreSQL's own SQL.
2. **The live tail**: every order version and fill the streams have sent since the host subscribed,
   that the database has not been seen to hold yet. The orders are a keyed POCO table in ChalkQL, each
   row carrying the order's latest version and, column for column, `orders_current`'s row for it when
   last looked up. The versions behind an order, and the fills, are tables of their own.

**Confirmation.** Every half second the host looks the tail's rows up in the database by key. An order
the database holds at the tail's version or later, and a fill it holds, is confirmed, and leaves the
tail five seconds later. That is a second longer than a window of the database's rows is kept (below),
so no page merges a database read from before a version landed with a tail that has already let it go.

**Reading a page.** A page is read from a key: the database's first rows at or after it in the tab's
order, then every tail row between that key and the last row read. The read decides nothing past its
last row, so a tail row beyond it waits for the next read. An order read at a version older than the
tail's gives way to the tail's version, placed where that version sorts, or not at all if it no
longer passes the filter. A tail row placed in the stretch whose database row was not read is looked
up: if the database has moved past the tail meanwhile, the database's row stands, wherever it sorts.
So each order appears once, at the newest version either side knows. The read is written so that
PostgreSQL can start an index at the key: the condition is spelled column by column, with the leading
column's bound repeated alone in front.

**Positions.** `Virtualize` asks by position, and a live database has no fixed positions. Each grid
keeps positions it has read, each tied to the row found there (`PageView`). A page near one is read
from that row's key, forwards, or backwards in the reversed order for rows above it. At the top,
positions are counted from the first row on every state, so new rows push the rest down. Scrolled
away, they hold. A jump far from every position known (dragging the scrollbar) is placed by the
database's own `OFFSET`, which knows nothing of the tail, so positions deep in a tab are approximate,
as the total is. Each page is still read whole from one key, so a page never repeats or skips a row.

**Windows and read-ahead.** What the database returns is kept for four seconds as a window: a run of
its rows in one order from one key, shared by every reader of the same tab, filter and sort. A read a
window covers is answered from it, with the tail laid over it afresh, so the tail's rows stay as
current as the stream. A sort whose reads take longer than 25 ms (no index serves it, and the database
sorts the whole table) is read in windows of 2,048 rows, which cost the database about what a page
does. After each page, the window after the one it came from, in the direction of scrolling, is read
in the background and joined onto it. The grid also renders 60 rows beyond the view either side. At
5 million orders, scrolling a ClOrdId sort 30 rows at a time, two steps in thirty showed placeholder
rows, briefly, and the database was read about once in ten.

**Starting, and the gap fill.** The host subscribes to the streams, and then sends the database one
last request: every version `order_versions` holds from the last 30 s. It takes each as if the stream
had sent it, and lets go at once of those `orders_current` already shows. A version that was in flight
when the host subscribed, sent before the subscription and written before the gap fill, is then shown
even though `orders_current` does not have it yet. One sent before the subscription and written after
the gap fill shows when `orders_current` has it. Fills need no gap fill: the blotter reads them from the
table they are written to.

**Live scopes.** For each tab and distinct filter on the open pages, a ChalkQL standing view over the
tail (`LiveScope`) holds what the filter can see. For orders, that is every order that passes the
filter now, or whose database row did: its two sets of columns let the filter be tested against both
in one statement. For fills, it is every fill in the tail that passes. Pages that filter alike share
one scope, whatever they sort by and wherever they are scrolled. A page sorts its scope in the host,
which is cheap because a scope is a slice of the tail, never of the corpus.

**Finding the budget.** Unless `--budget-mb` sets one, the host starts at 8 MB, and whatever would be
refused for want of budget (a scope that would not fit, or a tail grown past it) raises the budget to
the next of 16, 32 and 64 MB instead. It never lowers it. Past 64 MB, or once a budget is chosen on the
page (4 MB is there too, to see the host refuse), a refusal is a refusal.

**The budget** covers what the host holds: the tail, in ChalkQL and in the host; the scopes' views and
their states; and the windows, counts and fills it keeps for a moment, which may hold a quarter of the
budget at most and give way first to the tail and the scopes. Past it, the scopes no page is using
give way first. If that is not enough, because the database is too far behind, the host stops serving
and says why. It never drops a version to stay in budget. While it refuses, it keeps reading the
streams and keeps the last 30 s of what they send. It serves again once `orders_current`'s newest order
is within 5 s of the stream's, from what it kept and a new gap fill, and the pages take their scopes
again by themselves.

## What correct means here

Each order is shown at the highest version known when the page is read: known to the database (in
`orders_current`, or in `order_versions` through the gap fill) or sent on the stream since the host
subscribed. No order is missing or shown twice, and the rows are in order.

`Oracle` works out what a page should be in PostgreSQL alone. The truth is `orders_current` with every
newer version laid over it: those `order_versions` holds, and those the stream has sent, which the
oracle copies into a temporary table. For fills, it is the fills the database holds and those the
stream has sent. Then it filters, sorts and limits in one statement, with the filter's values written
in.

That is exact only while nothing moves, so `--probe` and
`tests/Chalk.Sample.Blotter.Tests/BlotterHostTests.cs` check two ways:

- **While the market runs**, through bursts and stalls, every page must hold its invariants: rows in
  order, none twice, each passing the filter.
- **Held still**, in rounds: the market paused, a burst the stalled database cannot write, the host
  caught up with the streams. Every page must then be the oracle's, row for row: once with the tail
  covering the database, and again once the database has written everything. A page at the top is
  compared from the top; a page deep in a tab, from its own first row.

The pages use random filters on one or two columns of every kind (picked values, starts-with, ranges,
times), random sorts of one to three columns, and random positions. On 2026-10-23, at 200,000 orders,
in both `--current` modes, every page under load held its invariants and every page held still matched
the oracle. The tests also cover:

- fills that arrive before their orders;
- the gap fill, with `orders_current` five seconds behind;
- the time limit;
- the budget's refusals, and serving again;
- rows held in place while rows arrive above them;
- an order's history and fills read from both sides.

`SortSpecTests`, `FilterSpecTests`, `FlashesTests` and `NestingTests` cover the host-side rules.

## What it found in ChalkQL

Registered in the design log as F273–F276; design 79 proposes what the owner asked for next.

- **F273.** A pushed-down query names a table by its catalog name, not its registered remote name, so
  the sample registers its tables in ChalkQL under the PostgreSQL schema's own name, `blotter`.
- **F274.** A DuckDB `TIMESTAMP` that discovery finds without a precision is typed `TIMESTAMP(9)`, finer
  than DuckDB keeps, so the planner sorted on it locally. Found when the sample ran on DuckDB; the
  sample declares its columns with `TIMESTAMP(6)`.
- **F275.** Discovery in a schema the database lacks fails with a SQLite pragma error.
- **F276.** An incremental scope holds about 440 bytes a tail row where a recomputing one holds about
  100 (`--maintenance recompute` to compare).
- **Design 79** (proposed, not built): what a statement examines per source, refusing at prepare when
  no access path keeps the rows read under a ceiling the host sets, and a time limit for a whole
  statement and for a view's update. Until then the sample asks PostgreSQL for its estimate, and
  cancels statements after 5 s itself.

## Not here, yet

- Free-text search.
- A refusal by cost (design 79).
- Tapline. On Blazor Server the grid renders on the server, and ChalkQL's views already give each
  reader what changed. Tapline would carry the same answers to a WebAssembly grid, headless clients or
  many nodes. `LiveScope`, `ScopeState` and `Pages` are the seam.
- Memory figures are the host's own estimates of what it holds, and the views' own count of their
  state. The status bar shows the process's managed heap beside them for comparison.
