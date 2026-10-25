# Timing partitions

Every rule in the blotter that a clock decides, the boundary it turns on, and the classes of input
either side of it. The host and the simulated house read time from one `TimeProvider`; the tests in
`TimingTests` give them a `FakeTimeProvider` and step the host's catch-up loop and the house by hand,
so each boundary is met at an exact instant: just before it, on it, and just after.

A comparison written `<` or `>` is strict: the value on the boundary falls on the near side. One
written `<=` or `>=` takes the boundary with it. The tests pin which.

Times below: _s_ when the house sends a version on the stream, _w_ when it is written to
`order_versions`, _c_ when `orders_current` holds it, _g_ when the host subscribes, and _h_ the gap
fill's haircut, `GapFillHaircut` (5 s): the one gap fill reads at _g_ + _h_.

## The host

| # | Rule | Boundary | Partitions, and what each must show | Test |
|---|---|---|---|---|
| 1 | Gap fill on going live | `event_time >= g − GapFill` (30 s) in `order_versions`, read at _g_ + _h_; the stream from _g_ | **a** _s_ ≥ _g_: the stream brings it. **b** _s_ < _g_, _w_ ≤ _g_ + _h_, _s_ ≥ _g_ − GapFill: the gap fill brings it, in transit at _g_ or not; _s_ = _g_ − GapFill exactly is brought. **c** _s_ < _g_ − GapFill: not brought; shown from `orders_current`, which the host assumes holds it by then. **d** _w_ > _g_ + _h_, still in transit when the gap fill reads: not brought, and shown from `orders_current` once it holds it; the haircut assumed too short | `Gap_fill_…` |
| 2 | Confirmation | a lookup once `elapsed >= ConfirmEvery` (0.5 s) | before: no lookup; on it: a lookup | `Confirmation_…` |
| 3 | Eviction | `now − confirmed >= EvictAfter` (5 s); a newer version unconfirms | unconfirmed: stays; confirmed for less: stays; for exactly EvictAfter: goes; a newer version within: stays, unconfirmed | `Eviction_…` |
| 4 | Kept windows | reused while `age <= WindowFor`, WindowFor = EvictAfter − 1 s (none at EvictAfter ≤ 1 s) | younger and on the boundary: reused; older: read again. A window read before an order moves in the database, used after the tail lets the order go, would lose it: WindowFor < EvictAfter rules that out | `Kept_windows_…` |
| 5 | Estimates, counts, an order's fills | estimated again once `age > EstimateFor` (3 s), counted again `> RecountAfter` (30 s), fills read again `> FillsFor` (1 s), forgotten `> 10 × FillsFor` | reused on the boundary; read again past it | not yet |
| 6 | Scope grace | closed once `idle > ScopeGrace` (20 s) | on the boundary: kept, and a page coming back reuses it; past it: closed | `Scope_grace_…` |
| 7 | Refusing and resuming | versions kept while refusing go once `event_time < now − GapFill`; resuming is checked at most once a second; it resumes once the newest order is no further behind than min(ResumeWithin, half the lag at refusal) | kept on the boundary, gone past it; resumes on the boundary, not past it | not yet |
| 8 | Alert tables | arrived fills kept while `arrived >= now − 10 s`; orders swept at most once `> 1 s`, dropped once unchanged `> 10 min` | kept on each boundary; gone past it | not yet |
| 9 | Lag alerts | `since < @now − n` and `since < until − n`; a wait with no end runs `until` 9999, held in microseconds (nanoseconds reach only 2262) | a wait of exactly _n_ does not fire; a microsecond more does; a database row held at exactly _n_ never fires | `Lag_alerts_…` |
| 10 | Count alerts | a slice is counted while `slice_start > @now − W`; slices are max(1 s, ⌈W ÷ 60⌉) wide; none is judged once `@now >= began + W`; the engine's clock is set on going live | a match leaves the window exactly _W_ after its slice starts; none fires on the boundary of its first window, not before; a count added before anything has arrived runs | `Count_alerts_…` |
| 11 | Alert sessions | sounds once `now − sounded >= Throttle`; a browser's alerts go once unattended `> KeepFor` (10 min); counts are not throttled | on the throttle: sounds; within it: gathers. Unattended for exactly KeepFor: kept | `Alert_sessions_…` |

## The head of every fetch

A row the house has sent may still be in transit to the database when the host reads: a fetch
bounded by time can miss it at its newest edge. That loses nothing where something else covers the
row; it loses the row where nothing does. Every fetch the host makes:

| Fetch | What covers a row in transit at its head | Verdict |
|---|---|---|
| The gap fill (`order_versions` since _g_ − GapFill, read at _g_ + _h_) | the haircut: it reads once what was in transit at _g_ has landed | covered within _h_; partition 1d past it |
| A page's read (`orders_current`, `fills`) | the tail, which holds what the stream sent until the database is seen to hold it | covered, but for what the gap fill missed |
| The merge's lookup of tail rows a read did not reach | the tail's own version stands | covered |
| The lookup as a version arrives, and the confirmation | a row not seen yet only waits longer in the tail | conservative |
| An order's fills, history and tail-only versions | the tail's fills and versions | covered |
| The resume check (newest order in `orders_current`) | it resumes later | conservative |
| An order as of the hold instant (`ArrivalsAbove`) | nothing | **exposed**, but only the count of rows new above a held view |
| Counts, estimates, rank, offset | approximate by design | not a correctness rule |

## The house

| # | Rule | Boundary | Partitions | Test |
|---|---|---|---|---|
| 12 | Ingestion | a row is written once `due <= now`, its lag in [Min, Max]; a stall holds every write `while now < stalledUntil` | written on its due instant, not before; a stall ending releases what fell due during it, at once | used by 1 |
| 13 | Order skew | an order version goes out once `release <= now`, up to OrderSkew (600 ms) after its fills | held, then sent on its instant | used by 1 |

## Not exercised here

- The statement time limit: PostgreSQL stops a statement by its own clock (`statement_timeout`).
- The planner process, and the browser's flashes, ticks and spinner, which only draw.
