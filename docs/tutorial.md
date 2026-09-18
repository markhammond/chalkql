# The ChalkQL tutorial

Eighteen chapters over one marketplace — customers, suppliers, products, orders and their
lines, employees, regions, warehouses, stock and rates — each one a small program that prints
what it does, the SQL it runs, the plan where the plan is the point, and the result.

Every block of output below was **captured from a real run** in this repository. Nothing
here was typed by hand, and `scripts/tutorial.sh` checks it: every block in this document
must appear, contiguously, in the output of the run. If the engine changes, the run changes,
and the script says so before anybody reads a stale number.

```bash
export JAVA_HOME="$(bash scripts/java-home.sh)"
export PATH="$JAVA_HOME/bin:$PATH"
./planner/gradlew -p planner shadowJar      # once; scripts/tutorial.sh does it for you if the jar is missing
./scripts/tutorial.sh                       # all eighteen chapters, then the comparison with this document
./scripts/tutorial.sh 3                     # chapter 3 alone (no comparison)
```

The code is `dotnet/samples/Chalk.Sample.Tutorial`: one file per chapter, plus `Domain.cs`
(the row types and their data), `Marketplace.cs` (the same rows registered three ways),
`Perspectives.cs` (the policy, declared once), `Databases.cs` (the SQLite and DuckDB
databases the sample creates, fills and deletes), `Live.cs` (what chapters 15 to 17 share)
and `Tutorial.cs` (printing, and nothing else).

## The marketplace

Two tenancies — customers and suppliers — are two **perspectives** over one relational
graph, which is what the entitlement chapters are about:

```text
                       order_details
                      /             \
                   orders         products
                     │               │
                 customers       suppliers
                     │               │
                 CUSTOMER        SUPPLIER
                 TENANCY         TENANCY
```

`order_details.unit_price` is the customer's negotiated price; `products.unit_price` is the
supplier's list price; `employees.manager_id` names another row of `employees`. Orders are
invoiced in the customer's own money and `usd_rates` says what one unit of each currency is
worth in dollars, which is chapter 6. Four more tables — `warehouses`,
`inventory_positions`, `inventory_movements` and `market_prices` — are chapters 15 to 17's.

## Contents

| | Chapter | What it shows |
|---|---|---|
| 1 | [A list is a table](#1-a-list-is-a-table) | eleven POCO collections as tables; keys and foreign keys checked; the result read as Arrow |
| 2 | [What the planner did](#2-what-the-planner-did) | the logical and physical plans; a filter that becomes an index lookup |
| 3 | [Two sources, one query](#3-two-sources-one-query) | the reference tables in SQLite and the facts in DuckDB; the generated remote SQL; pushdown turned down |
| 4 | [Strings without allocation](#4-strings-without-allocation) | a `Utf8String` column, measured at zero bytes per row |
| 5 | [Time series](#5-time-series) | a running total over an aggregate, a moving average, a `TUMBLE` bucket |
| 6 | [Your functions](#6-your-functions) | a SQL body inlined and pushed with the join that feeds it; a C# body kept local |
| 7 | [One table, many places](#7-one-table-many-places) | a partitioned table across two databases, pruned by a predicate |
| 8 | [Parameters and prepared statements](#8-parameters-and-prepared-statements) | one plan, three bindings; named parameters and a list |
| 9 | [When the source cannot](#9-when-the-source-cannot) | a predicate the source cannot evaluate, split at the boundary |
| 10 | [Who's asking](#10-whos-asking) | one line of an order, read by its customer and by its supplier; an employee subject read two ways; the region axis |
| 11 | [Through the parent](#11-through-the-parent) | a table with no tenancy column of its own, entitled through the two that have |
| 12 | [Through the lines](#12-through-the-lines) | the existential hop: a semi-join whose reached keys come from another source |
| 13 | [Replanning](#13-replanning) | one statement, three plans; a plan narrowed rather than written again |
| 14 | [Planning on a budget](#14-planning-on-a-budget) | a search that stops when it stops improving, a search the host stops, and how the prepared query reports it |
| 15 | [Live pivot](#15-live-pivot) | `PIVOT`, snapshot consistency, and warehouse and supplier tenancy |
| 16 | [Streaming live pivot](#16-streaming-live-pivot) | append-only facts, two tables under one epoch, and bootstrapping a stream |
| 17 | [Temporal streaming live pivot](#17-temporal-streaming-live-pivot) | a second stream on its own cadence, valued with an `ASOF` join |
| 18 | [Advanced topics: runtime policy configuration](#18-advanced-topics-runtime-policy-configuration) | a host mapping its own configuration to a policy at run time, over the same typed surface |

Then: [Explore further](#explore-further), which points each chapter at the design document
behind it.

---

## 1. A list is a table

Two `IReadOnlyList<T>`s of ordinary C# records, and nine more beside them. No ORM, no
mapping file, no copy of the rows: the columns are the record's properties, and the engine
reads the list itself.

```csharp
public sealed record Supplier(string SupplierId, Utf8String CompanyName, string Country);
public sealed record Product(int ProductId, string SupplierId, Utf8String ProductName, decimal UnitPrice);

new PocoSourceBuilder("shop", "main")
    .NamingPolicy(PocoNamingPolicy.SnakeCase)
    .DefaultDecimalScale(2)
    .AddTable("suppliers", Data.Suppliers, t => t
        .OrderedBy(s => s.SupplierId)
        .UniqueKey(s => s.SupplierId))
    .AddTable("products", Data.Products, t => t
        .OrderedBy(p => p.ProductId)
        .UniqueKey(p => p.ProductId)
        .Index(p => p.SupplierId)
        .ForeignKey(p => p.SupplierId).References<Supplier>(s => s.SupplierId, verify: true))
    .Build();
```

That is the whole registration. `verify: true` is not decoration: it checks every non-NULL
child key against the parent at `Build()`, because the planner *acts* on a declared
constraint, and a claim nobody checked is a wrong answer waiting to happen. One of those
foreign keys points at the table it is on — `employees.manager_id` — and it is verified like
any other.

The types came from the CLR types; the names came from the property names through
`PocoNamingPolicy.SnakeCase`, which is why `SupplierId` is `supplier_id` in SQL.

```
-- the whole marketplace, as the catalog sees it
    customers: 8 rows, columns customer_id I32, company_name String, contact_name String, country String
    suppliers: 6 rows, columns supplier_id String, company_name String, country String
    regions: 8 rows, columns region_id I32, name String
    warehouses: 3 rows, columns warehouse_id String, name String, country String
    employees: 6 rows, columns employee_id I32, manager_id I32?, name String, title String
    products: 20 rows, columns product_id I32, supplier_id String, product_name String, unit_price Decimal
    orders: 60 rows, columns order_id I32, customer_id I32, employee_id I32, region_id I32, order_date Timestamp, freight Decimal, currency String
    usd_rates: 300 rows, columns currency String, ts Date, rate Fp64
    order_details: 150 rows, columns order_id I32, product_id I32, unit_price Decimal, quantity I32, discount Decimal
    inventory_positions: 36 rows, columns warehouse_id String, product_id I32, supplier_id String, quantity I32
    inventory_movements: 24 rows, columns movement_id I32, ts Timestamp, warehouse_id String, product_id I32, supplier_id String, delta I32
    market_prices: 96 rows, columns price_id I32, ts Timestamp, product_id I32, price Decimal

    Every key and every foreign key on those tables was declared and checked,
    including employees.manager_id — which names another row of employees, and
    is verified against it like any other.
```

`company_name` and `product_name` are `Utf8String` properties, which is chapter 4's
subject; here they are simply STRING columns like any other.

```
-- one statement over two of them

    SELECT s.company_name AS supplier, s.country, COUNT(*) AS lines
    FROM suppliers s
    JOIN products p ON p.supplier_id = s.supplier_id
    WHERE p.unit_price > 20
    GROUP BY s.company_name, s.country
    ORDER BY lines DESC, supplier

    Kräuterhof Süd     DE  3
    Trattoria Piñón    IT  3
    Åkerlund Bryggeri  SE  3
    Épicerie Mourain   FR  1
    Øresund Fiskeri    DK  1
    南陽茶業               TW  1

6 rows produced, 26 scanned, 1 batch(es), 0 bytes fetched
```

**Why this matters.** The result is Apache Arrow and the chapter reads it column by
column rather than row by row. A STRING column is a `StringViewArray` — sixteen-byte views
over UTF-8, which is what `OutputSchema` declares unless the host asks for the classic layout
with `Output.Strings`; the counts are one buffer of `int64`. Nothing was boxed into an
`object[]` on the way past, and nothing was materialised into a row object. `0 bytes fetched`
is the honest answer for an in-process source: there was no wire and no disk. The batch is
yours until you dispose it.

---

## 2. What the planner did

`PrepareOptions.IncludePlanText` asks the sidecar to say what it made of the statement.
It answers twice: the logical algebra Calcite validated, then the physical operators it
chose and what it thought each would cost.

```csharp
var query = await engine.PrepareAsync(Joined, new PrepareOptions { IncludePlanText = true });
Tutorial.Block("planner", query.PlanText ?? "(none)");
Tutorial.Plan("the IR Chalk executes", query.Plan);
```

Read the physical plan bottom-up. The scan of `orders` starts at six columns and ends at
four — `projection=[[0, 1, 4, 5]]` — because the other two are never referenced.
`sel=[guess(0.1500)]` is the planner saying out loud that it has no statistic for `country`
and is guessing: the POCO source measures the columns an index or a collation leads with, and
`country` is neither. (The `id = …` numbers are the planner process's own rel counter. They
move if you run one chapter instead of eighteen; nothing else in this document does.)

Below the plan text is the IR — the actual contract between the two sides, and the thing
Chalk executes. Its `digest=…` is the plan's identity: stable across processes and sidecar
restarts, which is what makes "did this change alter planning?" a question with an answer.

```
-- an order joined to its customer, planned with IncludePlanText

    SELECT c.company_name AS customer, o.order_id, o.order_date, o.freight
    FROM orders o
    JOIN customers c ON c.customer_id = o.customer_id
    WHERE c.country = 'GB'
    ORDER BY o.order_id

planner:
    -- planning: converged after 33 evaluations
    -- logical
    LogicalSort(sort0=[$1], dir0=[ASC])
      LogicalProject(customer=[$5], order_id=[$0], order_date=[$2], freight=[$3])
        LogicalJoin(condition=[=($4, $1)], joinType=[inner])
          LogicalProject(order_id=[$0], customer_id=[$1], order_date=[$4], freight=[$5])
            ChalkTableScan(table=[[main, orders]], projection=[[0, 1, 2, 3, 4, 5, 6]])
          LogicalProject(customer_id=[$0], company_name=[$1])
            LogicalFilter(condition=[=($2, 'GB')])
              LogicalProject(customer_id=[$0], company_name=[$1], country=[$3])
                ChalkTableScan(table=[[main, customers]], projection=[[0, 1, 2, 3]])
    -- physical
    ChalkProject(customer=[$5], order_id=[$0], order_date=[$2], freight=[$3]): rowcount = 60.0, cumulative cost = {231.88571428571427 rows, 413.0857142857143 cpu, 0.0 io}, id = 398
      ChalkHashJoin(condition=[=($4, $1)], joinType=[inner]): rowcount = 60.0, cumulative cost = {171.88571428571427 rows, 173.0857142857143 cpu, 0.0 io}, id = 397
        ChalkTableScan(table=[[main, orders]], projection=[[0, 1, 4, 5]]): rowcount = 60.0, cumulative cost = {34.285714285714285 rows, 34.285714285714285 cpu, 0.0 io}, id = 331
        ChalkProject(customer_id=[$0], company_name=[$1]): rowcount = 1.2, cumulative cost = {15.2 rows, 16.4 cpu, 0.0 io}, id = 396
          ChalkFilter(condition=[=($2, 'GB')], sel=[guess(0.1500)]): rowcount = 1.2, cumulative cost = {14.0 rows, 14.0 cpu, 0.0 io}, id = 395
            ChalkTableScan(table=[[main, customers]], projection=[[0, 1, 3]]): rowcount = 8.0, cumulative cost = {6.0 rows, 6.0 cpu, 0.0 io}, id = 340
the IR Chalk executes:
    Plan ir_version=1 digest=b0c82e2d81e20454 context_id=tutorial-02a catalog_epoch=1
      Project [$5, $0, $2, $3] rows=60 out=[customer:STRING, order_id:I32, order_date:TIMESTAMP(9), freight:DECIMAL(28,2)] collations=[($1 ASC NULLS LAST)]
        HashJoin Inner left_keys=[1] right_keys=[0] rows=60 out=[order_id:I32, customer_id:I32, order_date:TIMESTAMP(9), freight:DECIMAL(28,2), customer_id0:I32, company_name:STRING] collations=[($0 ASC NULLS LAST)]
          Read shop.main.orders projection=[0,1,4,5] rows=60 out=[order_id:I32, customer_id:I32, order_date:TIMESTAMP(9), freight:DECIMAL(28,2)] collations=[($0 ASC NULLS LAST)]
          Project [$0, $1] rows=1.2 out=[customer_id:I32, company_name:STRING] collations=[($0 ASC NULLS LAST)]
            Filter EQ($2, 'GB') rows=1.2 out=[customer_id:I32, company_name:STRING, country:STRING] collations=[($0 ASC NULLS LAST)]
              Read shop.main.customers projection=[0,1,3] rows=8 out=[customer_id:I32, company_name:STRING, country:STRING] collations=[($0 ASC NULLS LAST)]
```

### The same filter, before and after an index

Now twenty thousand orders in a list, and a filter over them. First with nothing declared
but the primary key:

```
-- no index declared

    SELECT order_id, customer_id, region_id, freight FROM orders WHERE customer_id = 3 AND region_id = 5

plan:
    Plan ir_version=1 digest=2a29537a7691fdc1 context_id=tutorial-02b catalog_epoch=1
      Project [$0, 3, 5, $3] rows=450 out=[order_id:I32, customer_id:I32, region_id:I32, freight:DECIMAL(28,2)] collations=[($0 ASC NULLS LAST)]
        Filter AND(EQ($1, 3), EQ($2, 5)) rows=450 out=[order_id:I32, customer_id:I32, region_id:I32, freight:DECIMAL(28,2)] collations=[($0 ASC NULLS LAST)]
          Read shop.main.orders projection=[0,1,3,5] rows=20000 out=[order_id:I32, customer_id:I32, region_id:I32, freight:DECIMAL(28,2)] collations=[($0 ASC NULLS LAST)]
    313 rows produced, 20000 scanned of 20000
```

Then with one more line on the table builder — `.Index(o => o.CustomerId)` — and the same
statement, unchanged:

```
-- an index on (customer_id)

    SELECT order_id, customer_id, region_id, freight FROM orders WHERE customer_id = 3 AND region_id = 5

plan:
    Plan ir_version=1 digest=89856d24b53f707f context_id=tutorial-02c catalog_epoch=1
      Project [$0, 3, 5, $3] rows=375 out=[order_id:I32, customer_id:I32, region_id:I32, freight:DECIMAL(28,2)]
        Filter EQ($2, 5) rows=375 out=[order_id:I32, customer_id:I32, region_id:I32, freight:DECIMAL(28,2)] collations=[($1 ASC NULLS LAST)]
          IndexLookup shop.main.orders index=ix_orders_customer_id ranges=1 projection=[0,1,3,5] rows=2500 out=[order_id:I32, customer_id:I32, region_id:I32, freight:DECIMAL(28,2)] collations=[($1 ASC NULLS LAST)]
    313 rows produced, 2500 scanned of 20000
```

**Why this matters.** `Read` became `IndexLookup`; the `customer_id` conjunct was
*consumed* by the lookup's range and the `region_id` conjunct stayed behind as the residual
`Filter`; and `RowsScanned` went from 20 000 to 2 500 for the same 313 rows. `RowsScanned` is
a machine-independent counter, which is why this document quotes it and never quotes a
duration.

The index is Chalk's own `PermutationIndex` — one `int` per row per index, or nothing at all
when the key is a prefix of a declared collation. It is also an extension point: a host can
register any structure that implements `IPocoIndex<T>`, and
`dotnet/samples/Chalk.Sample.AkadeIndexedSet` does exactly that with a third-party library.

Selectivity decides. Had the predicate matched most of the table, the cost model would have
kept the scan — the design's own corpus has a query that asserts it does.

---

## 3. Two sources, one query

The marketplace's reference tables — customers, suppliers, products, employees, regions,
warehouses — live in SQLite, standing in for an OLTP store. The transactional facts, `orders`
and `order_details`, are co-located in DuckDB: an order and its lines belong together. One
statement names both, and says nothing about where either lives.

The DDL is generated from the same descriptors the POCO source describes, so the three copies
of the marketplace cannot drift from each other.

```
-- what each side actually holds
    oltp (sqlite):
      CREATE TABLE "products" (
        "product_id" INTEGER NOT NULL,
        "supplier_id" VARCHAR NOT NULL,
        "product_name" VARCHAR NOT NULL,
        "unit_price" DECIMAL(28,2) NOT NULL
      )
    facts (duckdb):
      CREATE TABLE "order_details" (
        "order_id" INTEGER NOT NULL,
        "product_id" INTEGER NOT NULL,
        "unit_price" DECIMAL(28,2) NOT NULL,
        "quantity" INTEGER NOT NULL,
        "discount" DECIMAL(28,2) NOT NULL
      )
```

Both sides were pushed. What travelled to each database is SQL Chalk generated in that
database's own dialect, carrying every predicate the source declared it can evaluate — and
the join between them happened here, over the rows that came back.

```
-- the keys travel, the rows do not

    SELECT s.company_name AS supplier, p.product_name, d.order_id, d.quantity
    FROM oltp.products p
    JOIN oltp.suppliers s ON s.supplier_id = p.supplier_id
    JOIN facts.order_details d ON d.product_id = p.product_id
    WHERE p.supplier_id = 'A' AND d.quantity > 22
    ORDER BY d.order_id, p.product_id

plan:
    Plan ir_version=1 digest=6b3d312600278220 context_id=tutorial-03 catalog_epoch=1
      Project [$0, $1, $2, $3] rows=11250 out=[supplier:STRING, product_name:STRING, order_id:I32, quantity:I32]
        Sort [$2 ASC NULLS LAST, $4 ASC NULLS LAST] rows=11250 out=[supplier:STRING, product_name:STRING, order_id:I32, quantity:I32, product_id:I32] collations=[($2 ASC NULLS LAST, $4 ASC NULLS LAST)]
          Project [$5, $4, $0, $2, $3] rows=11250 out=[supplier:STRING, product_name:STRING, order_id:I32, quantity:I32, product_id:I32]
            HashJoin Inner left_keys=[1] right_keys=[0] rows=11250 out=[order_id:I32, product_id:I32, quantity:I32, product_id0:I32, product_name:STRING, company_name:STRING]
              RemoteQuery source=facts dialect=duckdb sql='SELECT "order_id", "product_id", "quantity" FROM (SELECT "order_id", "product_id", "quantity" FROM "order_details") AS "t" WHERE "quantity" > 22' pushed_plan=yes rows=25000 out=[order_id:I32, product_id:I32, quantity:I32]
              RemoteQuery source=oltp dialect=sqlite sql='SELECT "t3"."product_id", "t3"."product_name", "t0"."company_name" FROM (SELECT "supplier_id", "company_name" FROM (SELECT "supplier_id", "company_name" FROM "suppliers") AS "t" WHERE "supplier_id" = ''A'') AS "t0" INNER JOIN (SELECT "product_id", ''A'' AS "supplier_id", "product_name" FROM (SELECT "product_id", "supplier_id", "product_name" FROM "products") AS "t1" WHERE "supplier_id" = ''A'') AS "t3" ON "t0"."supplier_id" = "t3"."supplier_id"' pushed_plan=yes rows=3 out=[product_id:I32, product_name:STRING, company_name:STRING]

what each source was asked to run:
    facts (duckdb): SELECT "order_id", "product_id", "quantity" FROM (SELECT "order_id", "product_id", "quantity" FROM "order_details") AS "t" WHERE "quantity" > 22
    oltp (sqlite): SELECT "t3"."product_id", "t3"."product_name", "t0"."company_name" FROM (SELECT "supplier_id", "company_name" FROM (SELECT "supplier_id", "company_name" FROM "suppliers") AS "t" WHERE "supplier_id" = 'A') AS "t0" INNER JOIN (SELECT "product_id", 'A' AS "supplier_id", "product_name" FROM (SELECT "product_id", "supplier_id", "product_name" FROM "products") AS "t1" WHERE "supplier_id" = 'A') AS "t3" ON "t0"."supplier_id" = "t3"."supplier_id"

    supplier           product_name  order_id  quantity
    -----------------  ------------  --------  --------
    Åkerlund Bryggeri  Björnkorv     76        24
    Åkerlund Bryggeri  Björnkorv     159       24
    Åkerlund Bryggeri  Havssalt      260       23
    Åkerlund Bryggeri  Havssalt      343       23
    Åkerlund Bryggeri  Havssalt      387       23
    Åkerlund Bryggeri  Havssalt      470       23
    … 951 more rows

    957 produced, 4862 scanned, 2 remote call(s), 4862 rows fetched
    facts: calls=1 rows=4858
    oltp: calls=1 rows=4
```

The same shape with the customer side narrowed, which is what a tenant's query looks like
before there is a policy.

```
-- a customer-side predicate, in the other dialect

    SELECT c.company_name AS customer, o.order_id, o.order_date, o.freight
    FROM oltp.customers c
    JOIN facts.orders o ON o.customer_id = c.customer_id
    WHERE c.country IN ('GB', 'JP') AND o.freight > 60
    ORDER BY o.order_id

plan:
    Plan ir_version=1 digest=c1221ed4a730cef7 context_id=tutorial-03 catalog_epoch=1
      Sort [$1 ASC NULLS LAST] rows=3000 out=[customer:STRING, order_id:I32, order_date:TIMESTAMP(9), freight:DECIMAL(28,2)] collations=[($1 ASC NULLS LAST)]
        Project [$5, $0, $2, $3] rows=3000 out=[customer:STRING, order_id:I32, order_date:TIMESTAMP(9), freight:DECIMAL(28,2)]
          HashJoin Inner left_keys=[1] right_keys=[0] rows=3000 out=[order_id:I32, customer_id:I32, order_date:TIMESTAMP(9), freight:DECIMAL(28,2), customer_id0:I32, company_name:STRING]
            RemoteQuery source=facts dialect=duckdb sql='SELECT "order_id", "customer_id", "order_date", "freight" FROM (SELECT "order_id", "customer_id", "order_date", "freight" FROM "orders") AS "t" WHERE "freight" > 60.00' pushed_plan=yes rows=10000 out=[order_id:I32, customer_id:I32, order_date:TIMESTAMP(9), freight:DECIMAL(28,2)]
            RemoteQuery source=oltp dialect=sqlite sql='SELECT "customer_id", "company_name" FROM (SELECT "customer_id", "company_name", "country" FROM "customers") AS "t" WHERE "country" IN (''GB'', ''JP'')' pushed_plan=yes rows=2 out=[customer_id:I32, company_name:STRING]

what each source was asked to run:
    facts (duckdb): SELECT "order_id", "customer_id", "order_date", "freight" FROM (SELECT "order_id", "customer_id", "order_date", "freight" FROM "orders") AS "t" WHERE "freight" > 60.00
    oltp (sqlite): SELECT "customer_id", "company_name" FROM (SELECT "customer_id", "company_name", "country" FROM "customers") AS "t" WHERE "country" IN ('GB', 'JP')

    customer          order_id  order_date           freight
    ----------------  --------  -------------------  -------
    Harbour Partners  3         2026-03-02 10:30:00  88.5
    Harbour Partners  19        2026-03-02 22:30:00  70.5
    Kanto Supply      22        2026-03-03 00:45:00  95.25
    Kanto Supply      38        2026-03-03 12:45:00  77.25
    Harbour Partners  43        2026-03-03 16:30:00  88.5
    Harbour Partners  59        2026-03-04 04:30:00  70.5
    … 1994 more rows

    2000 produced, 9002 scanned, 2 remote call(s), 9002 rows fetched
    facts: calls=1 rows=9000
    oltp: calls=1 rows=2
```

And with pushdown turned off entirely, which is the I4 oracle's view: bare scans, every
predicate evaluated here. The rows are the same, which is the point of having the oracle.

```
-- PushdownLevel.None — bare scans, which is the I4 oracle's view

    SELECT s.company_name AS supplier, p.product_name, d.order_id, d.quantity
    FROM oltp.products p
    JOIN oltp.suppliers s ON s.supplier_id = p.supplier_id
    JOIN facts.order_details d ON d.product_id = p.product_id
    WHERE p.supplier_id = 'A' AND d.quantity > 22
    ORDER BY d.order_id, p.product_id

plan:
    Plan ir_version=1 digest=2332fd7611b5434e context_id=tutorial-03 catalog_epoch=1
      Project [$0, $1, $2, $3] rows=2500 out=[supplier:STRING, product_name:STRING, order_id:I32, quantity:I32]
        Sort [$2 ASC NULLS LAST, $4 ASC NULLS LAST] rows=2500 out=[supplier:STRING, product_name:STRING, order_id:I32, quantity:I32, product_id:I32] collations=[($2 ASC NULLS LAST, $4 ASC NULLS LAST)]
          Project [$2, $1, $3, $5, $0] rows=2500 out=[supplier:STRING, product_name:STRING, order_id:I32, quantity:I32, product_id:I32]
            NestedLoopJoin Inner on EQ($4, $0) rows=2500 out=[product_id:I32, product_name:STRING, company_name:STRING, order_id:I32, product_id0:I32, quantity:I32]
              Project [$0, $2, $4] rows=1 out=[product_id:I32, product_name:STRING, company_name:STRING]
                NestedLoopJoin Inner on EQ($3, $1) rows=1 out=[product_id:I32, supplier_id:STRING, product_name:STRING, supplier_id0:STRING, company_name:STRING]
                  Project [$0, 'A', $2] rows=3.333 out=[product_id:I32, supplier_id:STRING, product_name:STRING]
                    Filter EQ($1, 'A') rows=3.333 out=[product_id:I32, supplier_id:STRING, product_name:STRING]
                      Project [$0, $1, $2] rows=20 out=[product_id:I32, supplier_id:STRING, product_name:STRING]
                        Read oltp.oltp.products projection=[0,1,2,3] rows=20 out=[product_id:I32, supplier_id:STRING, product_name:STRING, unit_price:DECIMAL(28,2)]
                  Filter EQ($0, 'A') rows=1 out=[supplier_id:STRING, company_name:STRING]
                    Project [$0, $1] rows=6 out=[supplier_id:STRING, company_name:STRING]
                      Read oltp.oltp.suppliers projection=[0,1,2] rows=6 out=[supplier_id:STRING, company_name:STRING, country:STRING]
              Filter GT($2, 22) rows=5263.158 out=[order_id:I32, product_id:I32, quantity:I32]
                Project [$0, $1, $3] rows=50000 out=[order_id:I32, product_id:I32, quantity:I32]
                  Read facts.facts.order_details projection=[0,1,2,3,4] rows=50000 out=[order_id:I32, product_id:I32, unit_price:DECIMAL(28,2), quantity:I32, discount:DECIMAL(28,2)]

what each source was asked to run:
    (no RemoteQuery: nothing was pushed, so the source is scanned)

    supplier           product_name  order_id  quantity
    -----------------  ------------  --------  --------
    Åkerlund Bryggeri  Björnkorv     76        24
    Åkerlund Bryggeri  Björnkorv     159       24
    Åkerlund Bryggeri  Havssalt      260       23
    Åkerlund Bryggeri  Havssalt      343       23
    Åkerlund Bryggeri  Havssalt      387       23
    Åkerlund Bryggeri  Havssalt      470       23
    … 951 more rows

    957 produced, 50026 scanned, 0 remote call(s), 0 rows fetched
```

---

## 4. Strings without allocation

The marketplace's supplier and product names are not ASCII — that is what they are for. A
POCO property of type `Utf8String` maps to STRING and its bytes are copied straight into the
Arrow buffer: nothing is transcoded on the way in and nothing is decoded on the way out.

```csharp
public sealed record Product(int ProductId, string SupplierId, Utf8String ProductName, decimal UnitPrice);

ReadOnlySpan<byte> bytes = name.GetUtf8(row).AsSpan();   // over the batch's own buffer: no copy, no decode
Console.WriteLine(Encoding.UTF8.GetString(bytes));       // the host deciding to make a string, to print it
```

```
-- product names, read back as bytes

    SELECT product_name, supplier_id FROM products WHERE unit_price > 10

    Lingonsylt         10 bytes  supplier=B
    Knäckebröd         12 bytes  supplier=C
    Crème Fraîche      15 bytes  supplier=D
    Pâté Mourain       14 bytes  supplier=E
    Cassoulet Épicé    17 bytes  supplier=F
    Kräutertee         11 bytes  supplier=A
    Süßmost             9 bytes  supplier=B
    Bergkäse            9 bytes  supplier=C

    Count the bytes against the characters: 凍頂烏龍 is four characters and
    twelve bytes, and nothing in the path between the list and the print
    above had to know that.
```

The measurement asks the only question worth asking: what does a row cost on the managed
heap? Two sizes, each read in one batch, because a slope taken with the batch count growing
is a per-batch cost in disguise.

```
-- what a row costs on the managed heap
    17000 rows in one batch: 2672 bytes allocated
    34000 rows in one batch: 2672 bytes allocated
    slope: 0.0000 bytes per row
    The allocation gates hold a POCO Utf8String column at 0.0000 bytes per row.
    Two sizes read in one batch each, because a slope taken with the batch count
    growing is a per-batch cost in disguise.
```

**Why this matters.** Pooled output means the batch's buffers come from the execution's
arena rather than from managed arrays. The host's obligation is the other half of that
bargain: dispose every batch promptly, and never hold a `Utf8String` past the batch it came
from.

---

## 5. Time series

Sixty orders, forty-five minutes apart, and a month of daily rates. Three questions about
time over one marketplace, and none of them a subquery.

A window over an aggregate: the inner `SUM` adds a line up per order, the outer one runs
along that customer's orders in date order.

```
-- running revenue per customer

    SELECT c.company_name AS customer, o.order_date,
           SUM(d.unit_price * d.quantity) AS order_value,
           SUM(SUM(d.unit_price * d.quantity))
             OVER (PARTITION BY o.customer_id ORDER BY o.order_date) AS running_revenue
    FROM orders o
    JOIN customers c ON c.customer_id = o.customer_id
    JOIN order_details d ON d.order_id = o.order_id
    WHERE o.customer_id IN (3, 7)
    GROUP BY c.company_name, o.customer_id, o.order_date
    ORDER BY c.company_name, o.order_date

plan:
    Plan ir_version=1 digest=cc514f2eda6e52ad context_id=tutorial-05 catalog_epoch=1
      Sort [$0 ASC NULLS LAST, $1 ASC NULLS LAST] rows=3.75 out=[customer:STRING, order_date:TIMESTAMP(9), order_value:DECIMAL(38,2), running_revenue:DECIMAL(38,2)] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
        Project [$2, $1, $3, $4] rows=3.75 out=[customer:STRING, order_date:TIMESTAMP(9), order_value:DECIMAL(38,2), running_revenue:DECIMAL(38,2)]
          Window partition=[$0] order=[$1 ASC NULLS LAST] frame=RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW calls=[SUM($3)->DECIMAL(38,2)] rows=3.75 out=[customer_id:I32, order_date:TIMESTAMP(9), company_name:STRING, order_value:DECIMAL(38,2), running_revenue:DECIMAL(38,2)] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
            Sort [$0 ASC NULLS LAST, $1 ASC NULLS LAST] rows=3.75 out=[customer_id:I32, order_date:TIMESTAMP(9), company_name:STRING, order_value:DECIMAL(38,2)] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
              HashAggregate keys=[1,2,3] measures=[SUM0($5)->DECIMAL(38,2)] rows=3.75 out=[customer_id:I32, order_date:TIMESTAMP(9), company_name:STRING, order_value:DECIMAL(38,2)]
                Project [$2, $3, $4, $5, $0, $1] rows=37.5 out=[order_id:I32, customer_id:I32, order_date:TIMESTAMP(9), company_name:STRING, order_id0:I32, $f3:DECIMAL(38,2)]
                  HashJoin Inner left_keys=[0] right_keys=[0] rows=37.5 out=[order_id:I32, $f3:DECIMAL(38,2), order_id0:I32, customer_id:I32, order_date:TIMESTAMP(9), company_name:STRING]
                    Project [$0, MULTIPLY($2, CAST($3 AS DECIMAL(28,2)))] rows=150 out=[order_id:I32, $f3:DECIMAL(38,2)]
                      Read shop.main.order_details projection=[0,1,2,3,4] rows=150 out=[order_id:I32, product_id:I32, unit_price:DECIMAL(28,2), quantity:I32, discount:DECIMAL(28,2)] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
                    Project [$0, $1, $2, $4] rows=15 out=[order_id:I32, customer_id:I32, order_date:TIMESTAMP(9), company_name:STRING]
                      HashJoin Inner left_keys=[1] right_keys=[0] rows=15 out=[order_id:I32, customer_id:I32, order_date:TIMESTAMP(9), customer_id0:I32, company_name:STRING]
                        Filter $1 IN (3, 7) rows=15 out=[order_id:I32, customer_id:I32, order_date:TIMESTAMP(9)] collations=[($0 ASC NULLS LAST)]
                          Read shop.main.orders projection=[0,1,4] rows=60 out=[order_id:I32, customer_id:I32, order_date:TIMESTAMP(9)] collations=[($0 ASC NULLS LAST)]
                        Filter $0 IN (3, 7) rows=2 out=[customer_id:I32, company_name:STRING] collations=[($0 ASC NULLS LAST)]
                          Read shop.main.customers projection=[0,1] rows=8 out=[customer_id:I32, company_name:STRING] collations=[($0 ASC NULLS LAST)]

    customer          order_date           order_value  running_revenue
    ----------------  -------------------  -----------  ---------------
    Harbour Partners  2026-03-02 10:30:00  1106.64      1106.64
    Harbour Partners  2026-03-02 16:30:00  1362.12      2468.76
    Harbour Partners  2026-03-02 22:30:00  565.31       3034.07
    Harbour Partners  2026-03-03 04:30:00  464.08       3498.15
    Harbour Partners  2026-03-03 10:30:00  1287.37      4785.52
    Harbour Partners  2026-03-03 16:30:00  1448.04      6233.56
    Harbour Partners  2026-03-03 22:30:00  384.51       6618.07
    Harbour Partners  2026-03-04 04:30:00  387.16       7005.23
    … 7 more rows
    15 rows, 218 scanned

    A window over an aggregate: the inner SUM adds a line up per order,
    the outer one runs along that customer's orders in date order. One
    statement, two levels, and no subquery.
```

`usd_rates` is declared ordered by `(currency, ts)`; the window partitions by `currency`
and orders by `ts`, and those are the same thing — so the plan carries no `Sort` at all and
the frame walks the rows where they already are.

```
-- a five-day moving average of the euro rate

    SELECT currency, ts, rate,
           AVG(rate) OVER (PARTITION BY currency ORDER BY ts ROWS 4 PRECEDING) AS moving_5
    FROM usd_rates
    WHERE currency = 'EUR'

plan:
    Plan ir_version=1 digest=14cc0cb86882b613 context_id=tutorial-05 catalog_epoch=1
      Project ['EUR', $0, $1, DIVIDE(CASE WHEN GT($2, 0) THEN $3 ELSE NULL:FP64? END, CAST($2 AS FP64?))] rows=30 out=[currency:STRING, ts:DATE, rate:FP64, moving_5:FP64?]
        Window partition=[] order=[$0 ASC NULLS LAST] frame=ROWS BETWEEN 4 PRECEDING AND CURRENT ROW calls=[COUNT($1)->I64, SUM($1)->FP64] rows=30 out=[ts:DATE, rate:FP64, w0$o0:I64, w0$o1:FP64] collations=[($0 ASC NULLS LAST)]
          Sort [$0 ASC NULLS LAST] rows=30 out=[ts:DATE, rate:FP64] collations=[($0 ASC NULLS LAST)]
            Project [$1, $2] rows=30 out=[ts:DATE, rate:FP64]
              Filter EQ($0, 'EUR') rows=30 out=[currency:STRING, ts:DATE, rate:FP64] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
                Read shop.main.usd_rates projection=[0,1,2] rows=300 out=[currency:STRING, ts:DATE, rate:FP64] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]

    currency  ts          rate    moving_5
    --------  ----------  ------  --------
    EUR       2026-03-02  1.0828  1.0828
    EUR       2026-03-03  1.0904  1.0866
    EUR       2026-03-04  1.0839  1.0857
    EUR       2026-03-05  1.0915  1.0872
    EUR       2026-03-06  1.085   1.0867
    EUR       2026-03-07  1.0785  1.0859
    EUR       2026-03-08  1.0861  1.085
    EUR       2026-03-09  1.0796  1.0841
    … 22 more rows

    `usd_rates` holds USD per one unit of a currency, already the right
    way up — a feed a host owns usually is — so a euro amount times this
    number is dollars, and chapter 6 is that multiplication in a function.
```

And a tumbling bucket, which is a table function rather than a window: `TUMBLE` assigns
each row a `window_start` and `GROUP BY` does the rest.

```
-- six-hour tumbling buckets

    SELECT window_start, COUNT(*) AS orders, SUM(freight) AS freight
    FROM TABLE(TUMBLE(TABLE orders, DESCRIPTOR(order_date), INTERVAL '6' HOUR))
    GROUP BY window_start
    ORDER BY window_start

    window_start         orders  freight
    -------------------  ------  -------
    2026-03-02 06:00:00  4       187.5
    2026-03-02 12:00:00  8       501
    2026-03-02 18:00:00  8       429
    2026-03-03 00:00:00  8       447
    2026-03-03 06:00:00  8       465
    2026-03-03 12:00:00  8       393
    … 2 more rows
```

---

## 6. Your functions

Every order is invoiced in the customer's own money, and `usd_rates` holds USD per one
unit of each currency for every day — already canonicalised, with USD itself at 1.0. What a
line came to in dollars is therefore the same arithmetic every time, which is exactly the
thing to write once, declare on the catalog, and never write again.

A function is a catalog object like a table: it belongs to a schema, travels with the
catalog and moves the epoch, and a name a built-in already has is refused at registration
rather than quietly shadowed.

```
-- the declaration
    .AddFunction("usd_line_total", f => f
        .Scalar()
        .Parameter("unit_price", ChalkType.Decimal(28, 2))
        .Parameter<int>("quantity")
        .Parameter("discount", ChalkType.Decimal(28, 2))
        .Parameter<double>("rate")
        .Returns(ChalkType.Decimal(18, 2))
        .Strict()
        .Sql("CAST(unit_price * quantity * (1 - discount) * rate AS DECIMAL(18, 2))"))

    The types are the catalog's, so they are written out rather than
    guessed from CLR types that have more than one SQL spelling. `Strict`
    says NULL in, NULL out, which is a fact the planner uses.

    The rate is a double and the money is exact, and this function is
    where the two meet: the IR harmonises an arithmetic call's operands by
    kind, so the decimal price times the double rate is computed in
    double, and the cast brings the product back to money, rounding
    half-even to the scale.
```

There is no `usd_line_total` in the generated SQL: the body was inlined before validation,
so what the planner had was arithmetic over four columns — and arithmetic is something DuckDB
can do. Both joins went with it, and the whole statement is one remote query.

```
-- what every line came to, in dollars

    SELECT d.order_id, d.product_id, o.currency, d.quantity, r.rate,
           usd_line_total(d.unit_price, d.quantity, d.discount, r.rate) AS usd
    FROM facts.order_details d
    JOIN facts.orders o ON o.order_id = d.order_id
    JOIN facts.usd_rates r
      ON r.currency = o.currency AND r.ts = CAST(o.order_date AS DATE)
    WHERE d.order_id <= 8
    ORDER BY d.order_id, d.product_id

plan:
    Plan ir_version=1 digest=70d8555c47fb92ac context_id=tutorial-06 catalog_epoch=1
      RemoteQuery source=facts dialect=duckdb sql='SELECT "t4"."order_id", "t4"."product_id", "t4"."currency", "t4"."quantity", "usd_rates"."rate", CAST("t4"."EXPR$0" * "usd_rates"."rate" AS DECIMAL(18, 2)) AS "usd" FROM "usd_rates" INNER JOIN (SELECT "t3"."order_id", "t3"."product_id", "t3"."quantity", "t1"."currency", "t1"."order_date0", "t3"."EXPR$0" FROM (SELECT "order_id", "currency", CAST("order_date" AS DATE) AS "order_date0" FROM (SELECT "order_id", "order_date", "currency" FROM "orders") AS "t" WHERE "order_id" <= 8) AS "t1" INNER JOIN (SELECT "order_id", "product_id", "quantity", CAST("unit_price" AS DECIMAL(28, 2)) * "quantity" * (1 - CAST("discount" AS DECIMAL(28, 2))) AS "EXPR$0" FROM "order_details" WHERE "order_id" <= 8) AS "t3" ON "t1"."order_id" = "t3"."order_id") AS "t4" ON "usd_rates"."currency" = "t4"."currency" AND "usd_rates"."ts" = "t4"."order_date0" ORDER BY "t4"."order_id", "t4"."product_id"' pushed_plan=yes rows=25000 out=[order_id:I32, product_id:I32, currency:STRING, quantity:I32, rate:FP64, usd:DECIMAL(18,2)] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]

what the database was asked to run:
    facts (duckdb): SELECT "t4"."order_id", "t4"."product_id", "t4"."currency", "t4"."quantity", "usd_rates"."rate", CAST("t4"."EXPR$0" * "usd_rates"."rate" AS DECIMAL(18, 2)) AS "usd" FROM "usd_rates" INNER JOIN (SELECT "t3"."order_id", "t3"."product_id", "t3"."quantity", "t1"."currency", "t1"."order_date0", "t3"."EXPR$0" FROM (SELECT "order_id", "currency", CAST("order_date" AS DATE) AS "order_date0" FROM (SELECT "order_id", "order_date", "currency" FROM "orders") AS "t" WHERE "order_id" <= 8) AS "t1" INNER JOIN (SELECT "order_id", "product_id", "quantity", CAST("unit_price" AS DECIMAL(28, 2)) * "quantity" * (1 - CAST("discount" AS DECIMAL(28, 2))) AS "EXPR$0" FROM "order_details" WHERE "order_id" <= 8) AS "t3" ON "t1"."order_id" = "t3"."order_id") AS "t4" ON "usd_rates"."currency" = "t4"."currency" AND "usd_rates"."ts" = "t4"."order_date0" ORDER BY "t4"."order_id", "t4"."product_id"

    order_id  product_id  currency  quantity  rate    usd
    --------  ----------  --------  --------  ------  ------
    1         8           SEK       8         0.0956  12.77
    1         13          SEK       9         0.0956  25.2
    1         18          SEK       10        0.0956  7.52
    2         15          NOK       11        0.092   28.14
    2         20          NOK       12        0.092   7.14
    3         2           GBP       14        1.27    340.95
    3         7           GBP       15        1.27    714.76
    3         12          GBP       16        1.27    275.08
    4         9           USD       17        1       609.62
    … 11 more rows

    20 produced, 20 scanned, 1 remote call(s), 20 rows fetched
    facts: calls=1 rows=20

    Read the generated SQL. There is no `usd_line_total` in it: the body
    was inlined before validation, so what the planner had was arithmetic
    over four columns, and arithmetic is something DuckDB can do. The two
    joins went with it — the line to its order for the currency and the
    date, the order to the rate in force that day — and the whole
    statement is one remote query.
```

The claim that makes that safe is that the two answers agree. The same statement is run
twice, once pushed and once with nothing pushed at all, and the money is compared cent for
cent.

```
-- the pushed answer and the local one, compared
    20 rows pushed into DuckDB, 20 computed here
    every value equal to the cent: True
```

A C# body is the other case: opaque, and the source has never heard of it.

```
-- and one the source cannot run
    `size_band` is a C# delegate in this process. Watch what happens to it.
```

```
-- a C# body in the select list

    SELECT d.order_id, d.product_id, d.quantity,
           size_band(d.quantity) AS band
    FROM facts.order_details d
    WHERE d.order_id <= 4
    ORDER BY d.order_id, d.product_id

plan:
    Plan ir_version=1 digest=7cf3d53ef64be848 context_id=tutorial-06 catalog_epoch=1
      Sort [$0 ASC NULLS LAST, $1 ASC NULLS LAST] rows=25000 out=[order_id:I32, product_id:I32, quantity:I32, band:STRING] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
        Project [$0, $1, $2, facts.size_band($2)] rows=25000 out=[order_id:I32, product_id:I32, quantity:I32, band:STRING]
          RemoteQuery source=facts dialect=duckdb sql='SELECT "order_id", "product_id", "quantity" FROM (SELECT "order_id", "product_id", "quantity" FROM "order_details") AS "t" WHERE "order_id" <= 4' pushed_plan=yes rows=25000 out=[order_id:I32, product_id:I32, quantity:I32]

what the database was asked to run:
    facts (duckdb): SELECT "order_id", "product_id", "quantity" FROM (SELECT "order_id", "product_id", "quantity" FROM "order_details") AS "t" WHERE "order_id" <= 4

    order_id  product_id  quantity  band
    --------  ----------  --------  ------
    1         8           8         small
    1         13          9         small
    1         18          10        medium
    2         15          11        medium
    2         20          12        medium
    3         2           14        medium
    3         7           15        medium
    3         12          16        medium
    4         9           17        medium
    … 1 more rows

    10 produced, 10 scanned, 1 remote call(s), 10 rows fetched
    facts: calls=1 rows=10

    `size_band` is still there by name in the plan and nowhere in the SQL,
    because DuckDB has never heard of it. It travels by name, is never
    pushed and never folded, and runs here over columns that arrived —
    which is chapter 9's whole subject.
```

---

## 7. One table, many places

`federated.orders_by_year` is one table to whoever queries it and two physical tables in
two DuckDB databases underneath. A warehouse that partitions by year carries the year as a
column, and that column is what the partitions are keyed by.

```csharp
PartitionCatalog.Table(
    "orders_by_year",
    columns,
    partitionColumn: 7,                       // `order_year`
    placement,                                // ("archive", "2026"), ("current", "2027")
    Physical,                                 // 2026 -> "orders_2026"
    rowsPerPartition);
```

Nothing about the statement says where a row lives; the descriptor does, and the planner
reads it before anything asks who owns the table.

```
    archive.orders_2026 holds order_year = 2026
    current.orders_2027 holds order_year = 2027
```

A predicate on the partition key removes the partitions that cannot hold a matching row,
at planning: `partitions=1`, one remote call.

```
-- a predicate on the partition key

    SELECT order_year, COUNT(*) AS orders, SUM(freight) AS freight
    FROM federated.orders_by_year
    WHERE order_year = 2026
    GROUP BY order_year
    ORDER BY order_year

plan:
    Plan ir_version=1 digest=a5ed632af1d35c47 context_id=tutorial-07 catalog_epoch=1
      Sort [$0 ASC NULLS LAST] rows=1 out=[order_year:I32, orders:I64, freight:DECIMAL(38,2)] collations=[($0 ASC NULLS LAST)]
        HashAggregate keys=[0] measures=[COUNT()->I64, SUM0($1)->DECIMAL(38,2)] rows=1 out=[order_year:I32, orders:I64, freight:DECIMAL(38,2)]
          Project [2026, $5] rows=1462.2 out=[order_year:I32, freight:DECIMAL(28,2)]
            Filter EQ($7, 2026) rows=1462.2 out=[order_id:I32, customer_id:I32, employee_id:I32, region_id:I32, order_date:TIMESTAMP(9), freight:DECIMAL(28,2), currency:STRING, order_year:I32]
              PartitionedScan partitions=1 values=[2026] rows=9748 out=[order_id:I32, customer_id:I32, employee_id:I32, region_id:I32, order_date:TIMESTAMP(9), freight:DECIMAL(28,2), currency:STRING, order_year:I32]
                RemoteQuery source=archive dialect=duckdb sql='SELECT "order_id", "customer_id", "employee_id", "region_id", "order_date", "freight", "currency", "order_year" FROM "orders_2026"' pushed_plan=yes rows=9748 out=[order_id:I32, customer_id:I32, employee_id:I32, region_id:I32, order_date:TIMESTAMP(9), freight:DECIMAL(28,2), currency:STRING, order_year:I32]

what each partition was asked to run:
    archive (duckdb): SELECT "order_id", "customer_id", "employee_id", "region_id", "order_date", "freight", "currency", "order_year" FROM "orders_2026"

    order_year  orders  freight
    ----------  ------  --------
    2026        9748    544669.5

    1 produced, 9748 scanned, 1 remote call(s), 9748 rows fetched
    archive: calls=1 rows=9748
```

Without it, both are read — concurrently, bounded by
`ExecutionOptions.MaxRemoteConcurrency` overall and by each source's own
`SourceOptions.MaxConcurrentQueries`. The union claims no ordering, which is why this query
asks for one.

```
-- and one without it

    SELECT order_year, COUNT(*) AS orders, SUM(freight) AS freight
    FROM federated.orders_by_year
    GROUP BY order_year
    ORDER BY order_year

plan:
    Plan ir_version=1 digest=1846cb62f5f8cd26 context_id=tutorial-07 catalog_epoch=1
      Sort [$0 ASC NULLS LAST] rows=2000 out=[order_year:I32, orders:I64, freight:DECIMAL(38,2)] collations=[($0 ASC NULLS LAST)]
        HashAggregate keys=[7] measures=[COUNT()->I64, SUM0($5)->DECIMAL(38,2)] rows=2000 out=[order_year:I32, orders:I64, freight:DECIMAL(38,2)]
          PartitionedScan partitions=2 values=[2026, 2027] rows=20000 out=[order_id:I32, customer_id:I32, employee_id:I32, region_id:I32, order_date:TIMESTAMP(9), freight:DECIMAL(28,2), currency:STRING, order_year:I32]
            RemoteQuery source=archive dialect=duckdb sql='SELECT "order_id", "customer_id", "employee_id", "region_id", "order_date", "freight", "currency", "order_year" FROM "orders_2026"' pushed_plan=yes rows=9748 out=[order_id:I32, customer_id:I32, employee_id:I32, region_id:I32, order_date:TIMESTAMP(9), freight:DECIMAL(28,2), currency:STRING, order_year:I32]
            RemoteQuery source=current dialect=duckdb sql='SELECT "order_id", "customer_id", "employee_id", "region_id", "order_date", "freight", "currency", "order_year" FROM "orders_2027"' pushed_plan=yes rows=10252 out=[order_id:I32, customer_id:I32, employee_id:I32, region_id:I32, order_date:TIMESTAMP(9), freight:DECIMAL(28,2), currency:STRING, order_year:I32]

what each partition was asked to run:
    archive (duckdb): SELECT "order_id", "customer_id", "employee_id", "region_id", "order_date", "freight", "currency", "order_year" FROM "orders_2026"
    current (duckdb): SELECT "order_id", "customer_id", "employee_id", "region_id", "order_date", "freight", "currency", "order_year" FROM "orders_2027"

    order_year  orders  freight
    ----------  ------  --------
    2026        9748    544669.5
    2027        10252   572830.5

    2 produced, 20000 scanned, 2 remote call(s), 20000 rows fetched
    archive: calls=1 rows=9748
    current: calls=1 rows=10252
```

---

## 8. Parameters and prepared statements

`PrepareAsync` plans and compiles; `ExecuteAsync` only binds. A `PreparedQuery` is
thread-safe and reusable, so the planning round trip happens once however many times the
statement runs.

Three bindings, one plan digest. The values never reach the SQL.

```
-- prepared once

    SELECT c.company_name AS customer, COUNT(*) AS orders, SUM(o.freight) AS freight
    FROM customers c JOIN orders o ON o.customer_id = c.customer_id
    WHERE c.country = ? AND o.region_id = ?
    GROUP BY c.company_name
    ORDER BY c.company_name

    style Positional, 2 parameters (String, I32), plan 0102734e1e65a77a

    country = 'GB', region_id = 3
    customer          orders  freight
    ----------------  ------  -------
    Harbour Partners  1       88.5
    plan 0102734e1e65a77a — the same plan every time

    country = 'SE', region_id = 1
    customer           orders  freight
    -----------------  ------  -------
    Northwind Trading  1       12
    plan 0102734e1e65a77a — the same plan every time

    country = 'JP', region_id = 8
    customer      orders  freight
    ------------  ------  -------
    Kanto Supply  1       59.25
    plan 0102734e1e65a77a — the same plan every time
```

The other two styles: Dapper-style named parameters, and a list expanded into the `IN`
clause.

```
-- named parameters, and a list expanded into the IN clause

    SELECT s.supplier_id, COUNT(*) AS products
    FROM products p JOIN suppliers s ON s.supplier_id = p.supplier_id
    WHERE s.country IN @countries AND p.unit_price > @floor
    GROUP BY s.supplier_id
    ORDER BY s.supplier_id


    countries = [SE, FR], plan 3199ae41effe2219
    supplier_id  products
    -----------  --------
    A            3
    B            3

    countries = [DE, TW, IT], plan 7f6f65e2b9f7f30e
    supplier_id  products
    -----------  --------
    C            3
    D            3
    E            3

    A list parameter is planned per list length: n literals make an
    n-dependent plan, so the two runs above have different digests and
    each length is compiled once and cached on the PreparedQuery.
```

---

## 9. When the source cannot

`size_band` is a C# delegate in this process. DuckDB cannot run it and Chalk does not
pretend otherwise: the conjunct it can run goes into the generated query, the one it cannot
stays above the boundary as a residual `Filter`, and the answer is the same either way.

```
    SELECT d.order_id, d.product_id, d.quantity, d.unit_price
    FROM facts.order_details d
    WHERE d.order_id <= 40 AND size_band(d.quantity) = 'large'
    ORDER BY d.order_id, d.product_id

plan:
    Plan ir_version=1 digest=e9fce24a9c058c27 context_id=tutorial-09 catalog_epoch=1
      Sort [$0 ASC NULLS LAST, $1 ASC NULLS LAST] rows=3750 out=[order_id:I32, product_id:I32, quantity:I32, unit_price:DECIMAL(28,2)] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
        Project [$0, $1, $3, $2] rows=3750 out=[order_id:I32, product_id:I32, quantity:I32, unit_price:DECIMAL(28,2)]
          Filter EQ(facts.size_band($3), 'large') rows=3750 out=[order_id:I32, product_id:I32, unit_price:DECIMAL(28,2), quantity:I32]
            RemoteQuery source=facts dialect=duckdb sql='SELECT "order_id", "product_id", "unit_price", "quantity" FROM (SELECT "order_id", "product_id", "unit_price", "quantity" FROM "order_details") AS "t" WHERE "order_id" <= 40' pushed_plan=yes rows=25000 out=[order_id:I32, product_id:I32, unit_price:DECIMAL(28,2), quantity:I32]

what the database was asked to run:
    facts (duckdb): SELECT "order_id", "product_id", "unit_price", "quantity" FROM (SELECT "order_id", "product_id", "unit_price", "quantity" FROM "order_details") AS "t" WHERE "order_id" <= 40

plan shape: Sort Project Filter RemoteQuery Filter Project Read

    order_id  product_id  quantity  unit_price
    --------  ----------  --------  ----------
    4         14          18        12.59
    5         1           21        4.28
    5         6           22        19.24
    5         16          20        10.92
    6         3           23        40.85
    6         8           24        17.58
    … 29 more rows

    35 produced, 100 scanned, 1 remote call(s), 100 rows fetched
    facts: calls=1 rows=100

    Rows fetched is what the pushed conjunct left; rows produced is what
    survived the residual. The difference is the price of a predicate the
    source could not evaluate — visible, rather than guessed at.
```

**Why this matters.** Rows fetched is what the pushed conjunct left; rows produced is what
survived the residual. The difference is the price of a predicate the source could not
evaluate — visible in a counter, rather than guessed at. A host that finds the gap too wide
knows exactly which function to give the source a spelling for.

---

## 10. Who's asking

A line of an order belongs to two tenancies at once: the customer who bought it, and the
supplier whose product it is. They are two perspectives over one row, and the policy says
what each may read of it. The statements below say nothing about either.

The policy is declared once, over the typed surface — every name enters once and
comes back as a handle, so a misspelling is caught where it is written rather than where it
is used:

```csharp
var customer = p.Tenancy("customer");
var supplier = p.Tenancy("supplier");
var region   = p.Tenancy("region");
var employee = p.Subject("employee", within: [customer]);

products.Tenancy(t => t.Direct(supplier, products.Column("supplier_id")).Predicate(Sql.Of("TRUE")));
orders.Tenancy(t => t
    .Direct(customer, orders.Column("customer_id"))
    .Direct(employee, orders.Column("employee_id"))
    .Direct(region,   orders.Column("region_id"))
    .Related(supplier).Through(orderDetails).Through(products));
orderDetails.Tenancy(t => t
    .Inherited(customer).Through(orders)
    .Inherited(supplier).Through(products));
```

The decorator is the whole of the call site: `engine` knows nothing about policies, and
`engine.WithEntitlements()` is what attaches one and hands the report back.

```
    SELECT order_id, product_id, quantity, unit_price
    FROM order_details
    ORDER BY order_id, product_id
```

A customer's own buyer sees their lines, and every column of them.

```
-- Harbour Partners' own buyer — their lines, every column of them
plan:
    Plan ir_version=1 digest=4fb1a574ddd79daa context_id=tutorial-10 catalog_epoch=1
      Project [$0, $1, $3, $2] rows=150 out=[order_id:I32, product_id:I32, quantity:I32, unit_price:DECIMAL(28,2)] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
        HashJoin Semi left_keys=[0] right_keys=[0] rows=150 out=[order_id:I32, product_id:I32, unit_price:DECIMAL(28,2), quantity:I32] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
          Read shop.main.order_details projection=[0,1,2,3] descriptor=add2eaf8e4e15354898f3ee1c559d415 entitled rows=150 out=[order_id:I32, product_id:I32, unit_price:DECIMAL(28,2), quantity:I32] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
          Project [$0] rows=9 out=[$chalk$key:I32] collations=[($0 ASC NULLS LAST)]
            Filter EQ($1, 3) rows=9 out=[order_id:I32, customer_id:I32] collations=[($0 ASC NULLS LAST)]
              Read shop.main.orders projection=[0,1] entitled rows=60 out=[order_id:I32, customer_id:I32] collations=[($0 ASC NULLS LAST)]

report:
    columns  order_id:Full, product_id:Full, quantity:Full, unit_price:Full
    tables   order_details: Some rows

    order_id  product_id  quantity  unit_price
    --------  ----------  --------  ----------
    3         2           14        22.56
    3         7           15        37.52
    3         12          16        14.25
    11        3           20        40.85
    11        8           21        17.58
    11        18          19        9.26
    19        4           6         20.9
    19        14          24        12.59
    … 16 more rows
```

The same statement, the other perspective.

```
-- Supplier A's representative — the same statement, the other perspective
plan:
    Plan ir_version=1 digest=e3b99e109b4642a7 context_id=tutorial-10 catalog_epoch=1
      Project [$0, $1, $2, NULL:DECIMAL(28,2)?] rows=150 out=[order_id:I32, product_id:I32, quantity:I32, unit_price:DECIMAL(28,2)?] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
        HashJoin Semi left_keys=[1] right_keys=[0] rows=150 out=[order_id:I32, product_id:I32, quantity:I32] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
          Read shop.main.order_details projection=[0,1,3] disclosures=[2:REDACTED, 4:AGGREGATE] descriptor=add2eaf8e4e15354898f3ee1c559d415 entitled rows=150 out=[order_id:I32, product_id:I32, quantity:I32] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
          Project [$0] rows=3.333 out=[$chalk$key:I32] collations=[($0 ASC NULLS LAST)]
            Filter EQ($1, 'A') rows=3.333 out=[product_id:I32, supplier_id:STRING] collations=[($0 ASC NULLS LAST)]
              Read shop.main.products projection=[0,1] entitled rows=20 out=[product_id:I32, supplier_id:STRING] collations=[($0 ASC NULLS LAST)]

report:
    columns  order_id:Full, product_id:Full, quantity:Full, unit_price:Redacted
    tables   order_details: Some rows

    order_id  product_id  quantity  unit_price
    --------  ----------  --------  ----------
    1         13          9         NULL
    3         7           15        NULL
    5         1           21        NULL
    14        19          9         NULL
    16        13          15        NULL
    18        7           21        NULL
    19        19          5         NULL
    20        7           8         NULL
    … 19 more rows

    Different rows and a different column. The customer's leaf reaches its
    tenancy through the order; the supplier's reaches its own through the
    product. And `unit_price` on a line is the price this customer
    negotiated, which is not the supplier's to read — so it is withheld
    outright rather than masked, and the report says REDACTED.
```

Different rows and a different column. The customer's leaf reaches its tenancy through the
order; the supplier's reaches its own through the product. `unit_price` on a line is the
price *this customer* negotiated, which is not the supplier's to read — so it is withheld
outright rather than masked, and the report says `Redacted`.

`discount` is the other kind of answer: a supplier may have the average over a large enough
group, and no individual value at all.

```
-- The discount a supplier may ask about, and the one it may not

    SELECT product_id, AVG(discount) AS average_discount, COUNT(*) AS lines
    FROM order_details
    GROUP BY product_id
    ORDER BY product_id

report:
    columns  product_id:Full, average_discount:Aggregate, lines:Full
    tables   order_details: Some rows

    product_id  average_discount  lines
    ----------  ----------------  -----
    1           0.08              6
    7           0.08              7
    13          0.06              7
    19          0.08              7

    `discount` is AggregateOnly for a supplier under a group-size floor of
    three: the average over a large enough group is a fact about the market,
    and one row's discount is a fact about one customer. Asking for the
    column itself is the second thing:
    Refused by the entitlements: main.order_details.discount is population-only for this principal and a projection to the result is not one of the aggregates it permits.
```

### The merchant's own staff

`employee` is a **subject** rather than a tenancy: the merchant's staff serve every customer
and belong to none. `within: [customer]` names the tenancy a subject grant may be *confined
to* — never where the employee belongs — so the same subject reads two ways.

```
-- The merchant's own staff: a subject rather than a tenancy

    SELECT order_id, customer_id, employee_id, region_id, order_date
    FROM orders
    ORDER BY order_id

    `employee` is declared `within: [customer]`, which names the tenancy a
    subject grant may be confined to — never where the employee belongs.
    So the same subject reads two ways, and the tutorial shows both.
```

`Grant.ForSubject(employee, 5, self, within: Tenancy.Anywhere)`: the orders employee 5
handled, whoever bought.

```
-- employee 5, everywhere: the orders they handled, whoever bought
plan:
    Plan ir_version=1 digest=2be82e0120c8e249 context_id=tutorial-10 catalog_epoch=1
      Project [$0, $1, 5, $3, $4] rows=9 out=[order_id:I32, customer_id:I32, employee_id:I32, region_id:I32, order_date:TIMESTAMP(9)] collations=[($0 ASC NULLS LAST)]
        Filter EQ($2, 5) rows=9 out=[order_id:I32, customer_id:I32, employee_id:I32, region_id:I32, order_date:TIMESTAMP(9)] collations=[($0 ASC NULLS LAST)]
          Read shop.main.orders projection=[0,1,2,3,4] descriptor=5654c31a2f4b8c0f2808586e5178d4ba entitled rows=60 out=[order_id:I32, customer_id:I32, employee_id:I32, region_id:I32, order_date:TIMESTAMP(9)] collations=[($0 ASC NULLS LAST)]

    order_id  customer_id  employee_id  region_id  order_date
    --------  -----------  -----------  ---------  -------------------
    5         5            5            5          2026-03-02 12:00:00
    11        3            5            4          2026-03-02 16:30:00
    17        1            5            3          2026-03-02 21:00:00
    23        7            5            1          2026-03-03 01:30:00
    29        5            5            8          2026-03-03 06:00:00
    35        3            5            7          2026-03-03 10:30:00
    41        1            5            6          2026-03-03 15:00:00
    47        7            5            4          2026-03-03 19:30:00
    … 2 more rows
```

`Grant.ForSubject(employee, 5, auditor, within: 7)`: customer 7's auditor, seeing what
employee 5 did on their account and nothing of any other customer.

```
-- employee 5, within customer 7: one auditor, one account
plan:
    Plan ir_version=1 digest=9ce8906027e40d7a context_id=tutorial-10 catalog_epoch=1
      Project [$0, 7, 5, $3, $4] rows=1.35 out=[order_id:I32, customer_id:I32, employee_id:I32, region_id:I32, order_date:TIMESTAMP(9)] collations=[($0 ASC NULLS LAST)]
        Filter AND(EQ($2, 5), EQ($1, 7)) rows=1.35 out=[order_id:I32, customer_id:I32, employee_id:I32, region_id:I32, order_date:TIMESTAMP(9)] collations=[($0 ASC NULLS LAST)]
          Read shop.main.orders projection=[0,1,2,3,4] descriptor=5654c31a2f4b8c0f2808586e5178d4ba entitled rows=60 out=[order_id:I32, customer_id:I32, employee_id:I32, region_id:I32, order_date:TIMESTAMP(9)] collations=[($0 ASC NULLS LAST)]

    order_id  customer_id  employee_id  region_id  order_date
    --------  -----------  -----------  ---------  -------------------
    23        7            5            1          2026-03-03 01:30:00
    47        7            5            4          2026-03-03 19:30:00

    Read the two leaves. `employee_id = 5` alone, and `employee_id = 5 AND
    customer_id = 7`: a grant written in terms of a subject, a role and a
    customer, compiled into an ordinary relational predicate that a source
    can push and an index can answer. A confined subject grant can only
    apply where both can be resolved, and `orders` resolves both.
```

### And a third axis

`region` is on `orders` and on nothing else.

```
-- And the third axis: a region, on orders and on nothing else
plan:
    Plan ir_version=1 digest=091f7b7a63f98924 context_id=tutorial-10 catalog_epoch=1
      Project [$0, $1, $2, 7, $4] rows=9 out=[order_id:I32, customer_id:I32, employee_id:I32, region_id:I32, order_date:TIMESTAMP(9)] collations=[($0 ASC NULLS LAST)]
        Filter EQ($3, 7) rows=9 out=[order_id:I32, customer_id:I32, employee_id:I32, region_id:I32, order_date:TIMESTAMP(9)] collations=[($0 ASC NULLS LAST)]
          Read shop.main.orders projection=[0,1,2,3,4] descriptor=5654c31a2f4b8c0f2808586e5178d4ba entitled rows=60 out=[order_id:I32, customer_id:I32, employee_id:I32, region_id:I32, order_date:TIMESTAMP(9)] collations=[($0 ASC NULLS LAST)]

    order_id  customer_id  employee_id  region_id  order_date
    --------  -----------  -----------  ---------  -------------------
    7         7            1            7          2026-03-02 13:30:00
    10        2            4            7          2026-03-02 15:45:00
    21        5            3            7          2026-03-03 00:00:00
    32        8            2            7          2026-03-03 08:15:00
    35        3            5            7          2026-03-03 10:30:00
    46        6            4            7          2026-03-03 18:45:00
    49        1            1            7          2026-03-03 21:00:00
    60        4            6            7          2026-03-04 05:15:00

    Three dimensions on one table, OR-ed: a row is visible when the caller
    holds a role in its customer, or a grant for the employee who handled
    it, or a role in its region. One declaration serves all three.
```

---

## 11. Through the parent

Look at the columns of `order_details`: an order, a product, a quantity, a price and a
discount. Not a tenancy column among them. Which principal may read a line is decided
entirely by the order it is on and the product it names — one declaration each, and the same
statement returns different rows to different callers without ever mentioning either policy.

```
    SELECT d.order_id, d.product_id, d.quantity, o.order_date
    FROM order_details d
    JOIN orders o ON o.order_id = d.order_id
    ORDER BY d.order_id, d.product_id

    The whole of the declaration is two lines:

        orderDetails.Tenancy(t => t
            .Inherited(customer).Through(orders)
            .Inherited(supplier).Through(products));
```

The customer's hop: a line is visible when its order is.

```
-- the customer's hop: a line is visible when its order is
plan:
    Plan ir_version=1 digest=60256cb744b4a8eb context_id=tutorial-11 catalog_epoch=1
      Project [$0, $1, $2, $4] rows=22.5 out=[order_id:I32, product_id:I32, quantity:I32, order_date:TIMESTAMP(9)] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
        HashJoin Inner left_keys=[0] right_keys=[0] rows=22.5 out=[order_id:I32, product_id:I32, quantity:I32, order_id0:I32, order_date:TIMESTAMP(9)] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
          HashJoin Semi left_keys=[0] right_keys=[0] rows=150 out=[order_id:I32, product_id:I32, quantity:I32] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
            Read shop.main.order_details projection=[0,1,3] descriptor=add2eaf8e4e15354898f3ee1c559d415 entitled rows=150 out=[order_id:I32, product_id:I32, quantity:I32] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
            Project [$0] rows=9 out=[$chalk$key:I32] collations=[($0 ASC NULLS LAST)]
              Filter EQ($1, 3) rows=9 out=[order_id:I32, customer_id:I32] collations=[($0 ASC NULLS LAST)]
                Read shop.main.orders projection=[0,1] entitled rows=60 out=[order_id:I32, customer_id:I32] collations=[($0 ASC NULLS LAST)]
          Project [$0, $2] rows=9 out=[order_id:I32, order_date:TIMESTAMP(9)] collations=[($0 ASC NULLS LAST)]
            Filter EQ($1, 3) rows=9 out=[order_id:I32, customer_id:I32, order_date:TIMESTAMP(9)] collations=[($0 ASC NULLS LAST)]
              Read shop.main.orders projection=[0,1,4] descriptor=5654c31a2f4b8c0f2808586e5178d4ba entitled rows=60 out=[order_id:I32, customer_id:I32, order_date:TIMESTAMP(9)] collations=[($0 ASC NULLS LAST)]

report:
    columns  order_id:Full, product_id:Full, quantity:Full, order_date:Full
    tables   order_details: Some rows, orders: Some rows

    order_id  product_id  quantity  order_date
    --------  ----------  --------  -------------------
    3         2           14        2026-03-02 10:30:00
    3         7           15        2026-03-02 10:30:00
    3         12          16        2026-03-02 10:30:00
    11        3           20        2026-03-02 16:30:00
    11        8           21        2026-03-02 16:30:00
    11        18          19        2026-03-02 16:30:00
    19        4           6         2026-03-02 22:30:00
    19        14          24        2026-03-02 22:30:00
    … 16 more rows

    Read the leaf on order_details: it has no filter of its own, because it
    has nothing to filter on. What restricts it is the join above it, to a
    scan of orders carrying `customer_id = 3` — the order's policy, reached
    through a relationship the statement never wrote.
```

The supplier's hop: the same line, visible because of its product. Note that the statement
never mentions `products` — the policy brought it.

```
-- the supplier's hop: the same line, visible because of its product
plan:
    Plan ir_version=1 digest=9765799b38918758 context_id=tutorial-11 catalog_epoch=1
      Project [$0, $1, $2, $4] rows=137.944 out=[order_id:I32, product_id:I32, quantity:I32, order_date:TIMESTAMP(9)] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
        HashJoin Inner left_keys=[0] right_keys=[0] rows=137.944 out=[order_id:I32, product_id:I32, quantity:I32, order_id0:I32, order_date:TIMESTAMP(9)] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
          HashJoin Semi left_keys=[1] right_keys=[0] rows=150 out=[order_id:I32, product_id:I32, quantity:I32] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
            Read shop.main.order_details projection=[0,1,3] disclosures=[2:REDACTED, 4:AGGREGATE] descriptor=add2eaf8e4e15354898f3ee1c559d415 entitled rows=150 out=[order_id:I32, product_id:I32, quantity:I32] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
            Project [$0] rows=3.333 out=[product_id:I32] collations=[($0 ASC NULLS LAST)]
              Filter EQ($1, 'A') rows=3.333 out=[product_id:I32, supplier_id:STRING] collations=[($0 ASC NULLS LAST)]
                Read shop.main.products projection=[0,1] entitled rows=20 out=[product_id:I32, supplier_id:STRING] collations=[($0 ASC NULLS LAST)]
          HashJoin Semi left_keys=[0] right_keys=[0] rows=55.178 out=[order_id:I32, order_date:TIMESTAMP(9)]
            Read shop.main.orders projection=[0,4] disclosures=[1:TESTED, 5:REDACTED] descriptor=5654c31a2f4b8c0f2808586e5178d4ba entitled rows=60 out=[order_id:I32, order_date:TIMESTAMP(9)] collations=[($0 ASC NULLS LAST)]
            HashJoin Inner left_keys=[1] right_keys=[0] rows=150 out=[order_id:I32, product_id:I32, product_id0:I32]
              Read shop.main.order_details projection=[0,1] entitled rows=150 out=[order_id:I32, product_id:I32] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
              Project [$0] rows=3.333 out=[product_id:I32] collations=[($0 ASC NULLS LAST)]
                Filter EQ($1, 'A') rows=3.333 out=[product_id:I32, supplier_id:STRING] collations=[($0 ASC NULLS LAST)]
                  Read shop.main.products projection=[0,1] entitled rows=20 out=[product_id:I32, supplier_id:STRING] collations=[($0 ASC NULLS LAST)]

report:
    columns  order_id:Full, product_id:Full, quantity:Full, order_date:Full
    tables   order_details: Some rows, orders: Some rows

    order_id  product_id  quantity  order_date
    --------  ----------  --------  -------------------
    1         13          9         2026-03-02 09:00:00
    3         7           15        2026-03-02 10:30:00
    5         1           21        2026-03-02 12:00:00
    14        19          9         2026-03-02 18:45:00
    16        13          15        2026-03-02 20:15:00
    18        7           21        2026-03-02 21:45:00
    19        19          5         2026-03-02 22:30:00
    20        7           8         2026-03-02 23:15:00
    … 19 more rows

    The same two lines of policy, the other hop — and note that the
    statement never mentions `products`. The policy brought it: `products`
    holds its supplier directly, that is the foot of the path, and the
    line reaches it through `product_id`, so the scan the policy added
    carries `supplier_id = ''A''` and the join above it does the rest.
```

And a grant that reaches everywhere, where both joins disappear.

```
-- Finance reaches everywhere — and the joins disappear
plan:
    Plan ir_version=1 digest=a7d4abbfe10bc23f context_id=tutorial-11 catalog_epoch=1
      Project [$0, $1, $2, $4] rows=150 out=[order_id:I32, product_id:I32, quantity:I32, order_date:TIMESTAMP(9)] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
        HashJoin Inner left_keys=[0] right_keys=[0] rows=150 out=[order_id:I32, product_id:I32, quantity:I32, order_id0:I32, order_date:TIMESTAMP(9)] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
          Read shop.main.order_details projection=[0,1,3] descriptor=add2eaf8e4e15354898f3ee1c559d415 entitled rows=150 out=[order_id:I32, product_id:I32, quantity:I32] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
          Read shop.main.orders projection=[0,4] descriptor=5654c31a2f4b8c0f2808586e5178d4ba entitled rows=60 out=[order_id:I32, order_date:TIMESTAMP(9)] collations=[($0 ASC NULLS LAST)]

report:
    columns  order_id:Full, product_id:Full, quantity:Full, order_date:Full
    tables   order_details: All rows, orders: All rows

    order_id  product_id  quantity  order_date
    --------  ----------  --------  -------------------
    1         8           8         2026-03-02 09:00:00
    1         13          9         2026-03-02 09:00:00
    1         18          10        2026-03-02 09:00:00
    2         15          11        2026-03-02 09:45:00
    2         20          12        2026-03-02 09:45:00
    3         2           14        2026-03-02 10:30:00
    3         7           15        2026-03-02 10:30:00
    3         12          16        2026-03-02 10:30:00
    … 142 more rows

    For Finance each parent's predicate folds to TRUE, the foreign keys to
    orders and to products are declared and neither key is NULL — so every
    line has a visible parent and the policy's joins are left out
    altogether. Count the reads: the entitled plans above have the
    statement's own and the ones the policy added; this one has the
    statement's own. The report says ALL rows for every table, which is
    what the elision was read off.
```

---

## 12. Through the lines

`Related`, not `Inherited`. A line inherits its supplier from its product: exactly one,
always. An order is *related* to a supplier through the lines that carry its products: zero,
one or many. The declaration is one clause; the plan is a semi-join, and this time the
reached keys come from another source.

```
    SELECT o.order_id, o.order_date, o.employee_id
    FROM facts.orders o
    WHERE o.order_id <= 40
    ORDER BY o.order_id

    The clause, in the middle of the orders declaration:

        .Related(supplier).Through(orderDetails).Through(products)

    The two sources are why the path needs one more thing. A declared step
    resolves through a foreign key, and a foreign key names a table of its
    own schema — so `order_details.product_id` in DuckDB could not reach
    `products` in SQLite. An association says across two sources what a
    foreign key says within one:

        orderDetails.Column("product_id").References(products.Column("product_id"))
```

Read the three remote queries in order. SQLite is asked which products are supplier A's;
those three keys travel to DuckDB as a key set — `WHERE "product_id" IN (?)` — and come back
as order keys; those order keys travel again, and DuckDB answers the statement's own question
over them. The target and the bridge are in one source and the reached table is in the other,
which is exactly the layout the schema was arranged for.

```
-- supplier A's representative asks for orders
plan:
    Plan ir_version=1 digest=969bf3f0986f2ee1 context_id=tutorial-12 catalog_epoch=1
      Sort [$0 ASC NULLS LAST] rows=7.5 out=[order_id:I32, order_date:TIMESTAMP(9), employee_id:I32] collations=[($0 ASC NULLS LAST)]
        Project [$0, $2, $1] rows=7.5 out=[order_id:I32, order_date:TIMESTAMP(9), employee_id:I32]
          Project [$1, $2, $3] rows=7.5 out=[order_id:I32, employee_id:I32, order_date:TIMESTAMP(9)]
            LookupJoin Inner driving_keys=[0] lookup_keys=[0] max_keys_per_call=1000 key_set=in rows=7.5 out=[order_id:I32, order_id0:I32, employee_id:I32, order_date:TIMESTAMP(9)]
              Filter LE($0, 40) rows=1 out=[order_id:I32]
                HashAggregate keys=[0] measures=[] rows=1.688 out=[order_id:I32]
                  Project [$1, $2, $0] rows=16.875 out=[order_id:I32, product_id:I32, product_id0:I32]
                    LookupJoin Inner driving_keys=[0] lookup_keys=[1] max_keys_per_call=1000 key_set=in rows=16.875 out=[product_id:I32, order_id:I32, product_id0:I32]
                      RemoteQuery source=oltp dialect=sqlite sql='SELECT "product_id" FROM (SELECT "product_id", "supplier_id" FROM "products") AS "t" WHERE "supplier_id" = ''A''' pushed_plan=yes rows=3 out=[product_id:I32]
                      RemoteQuery source=facts dialect=duckdb sql='SELECT "order_id", "product_id" FROM "order_details" WHERE "product_id" IN (?)' pushed_plan=yes rows=37.5 out=[order_id:I32, product_id:I32]
              RemoteQuery source=facts dialect=duckdb sql='SELECT "order_id", "employee_id", "order_date" FROM "orders" WHERE "order_id" <= 40 AND "order_id" IN (?)' pushed_plan=yes rows=7.5 out=[order_id:I32, employee_id:I32, order_date:TIMESTAMP(9)]

report:
    columns  order_id:Full, order_date:Full, employee_id:Full
    tables   orders: Some rows

what each source was asked to run:
    oltp (sqlite): SELECT "product_id" FROM (SELECT "product_id", "supplier_id" FROM "products") AS "t" WHERE "supplier_id" = 'A'
    facts (duckdb): SELECT "order_id", "product_id" FROM "order_details" WHERE "product_id" IN (?)
    facts (duckdb): SELECT "order_id", "employee_id", "order_date" FROM "orders" WHERE "order_id" <= 40 AND "order_id" IN (?)

    order_id  order_date           employee_id
    --------  -------------------  -----------
    1         2026-03-02 09:00:00  1
    3         2026-03-02 10:30:00  3
    5         2026-03-02 12:00:00  5
    14        2026-03-02 18:45:00  2
    16        2026-03-02 20:15:00  4
    18        2026-03-02 21:45:00  6
    19        2026-03-02 22:30:00  1
    20        2026-03-02 23:15:00  2
    … 11 more rows

    `freight` is the merchant's cost of shipping and a supplier does not
    read it; `customer_id` may be tested for equality without being read,
    which is a different thing from being masked — a supplier chasing a
    dispute can confirm the account they were already given, and learn
    nothing by guessing.
```

The customer reaches the same table the other way, and their plan is one remote query:
`customer_id = 3` is a column on the row, so it folds to a literal the database can be
asked.

```
-- and the customer, who reaches the same table the other way
plan:
    Plan ir_version=1 digest=7a62b1010de35d01 context_id=tutorial-12 catalog_epoch=1
      RemoteQuery source=facts dialect=duckdb sql='SELECT "order_id", "order_date", "employee_id" FROM (SELECT "order_id", "customer_id", "employee_id", "order_date" FROM "orders") AS "t" WHERE "customer_id" = 3 AND "order_id" <= 40 ORDER BY "order_id"' pushed_plan=yes rows=4.5 out=[order_id:I32, order_date:TIMESTAMP(9), employee_id:I32] collations=[($0 ASC NULLS LAST)]

what each source was asked to run:
    facts (duckdb): SELECT "order_id", "order_date", "employee_id" FROM (SELECT "order_id", "customer_id", "employee_id", "order_date" FROM "orders") AS "t" WHERE "customer_id" = 3 AND "order_id" <= 40 ORDER BY "order_id"

    order_id  order_date           employee_id
    --------  -------------------  -----------
    3         2026-03-02 10:30:00  3
    11        2026-03-02 16:30:00  5
    19        2026-03-02 22:30:00  1
    27        2026-03-03 04:30:00  3
    35        2026-03-03 10:30:00  5

    One table, two shapes of reach. The customer's is a column on the row
    and folds to a literal the database can be asked. The supplier's is an
    existence question over the lines, and it stays a join.
```

**Why this matters.** One table, two shapes of reach. A column on the row folds into the
leaf and pushes; an existence question over the lines stays a join. The policy said which is
which once, and neither statement mentions it.

---

## 13. Replanning

Chapters 10 to 12 bind a principal's grants at prepare and get that principal's plan. That
is the primary mode and the one that pushes best — and it means one plan per principal. A
host with ten thousand users and fifty customers would rather have fifty.

`RequestContext` lets a name be bound as a **value** or left as a **shape**: a type, a kind
and no value at all. What is bound folds into the plan; what is left open becomes a parameter,
or a bound table the executor materialises, and is bound per execution. And a plan that left
something open can be **narrowed** — told more later:

```csharp
var basePlan   = await entitled.PrepareAsync(Sql, entitlements.Bind(p).Shape(open));
var tenantPlan = await basePlan.NarrowAsync(Customer(entitlements, p));
var personPlan = await tenantPlan.NarrowAsync(Person(entitlements, p));
```

Narrowing is the union of the bindings and nothing else, so `NarrowAsync` gives the plan
`PrepareAsync(sql, union)` gives — the same plan, the same digest. What it buys is that the
sidecar may still be holding the base plan's converted tree and start from there.

The tables are in a **DuckDB database**, so each tier is not only a different plan but a
different question asked of the source.

```
    SELECT order_id, product_id, quantity
    FROM facts.order_details
    ORDER BY order_id, product_id
```

`Shape(names)` is what chose the line: the supplier, region and warehouse axes are bound,
because every principal on the customer's side of this marketplace binds them identically,
and what is left open is the customer, the employee and the caller. A host draws that line
where its own principals differ.

```
-- the base plan: the customer and the caller left open, one plan for everybody
plan:
    Plan ir_version=1 digest=af176e16e558d00d context_id=tutorial-13 catalog_epoch=1
      HashJoin Semi left_keys=[0] right_keys=[0] rows=37.5 out=[order_id:I32, product_id:I32, quantity:I32] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
        Sort [$0 ASC NULLS LAST, $1 ASC NULLS LAST] rows=150 out=[order_id:I32, product_id:I32, quantity:I32] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
          RemoteQuery source=facts dialect=duckdb sql='SELECT "order_id", "product_id", "quantity" FROM "order_details"' pushed_plan=yes rows=150 out=[order_id:I32, product_id:I32, quantity:I32]
        Project [$0] rows=15 out=[$chalk$key:I32]
          Filter OR(ISNOTNULL($5), ISNOTNULL($7), ISNOTNULL($10), ISNOTNULL($12), ISNOTNULL($15), ISNOTNULL($17), ISNOTNULL($20), ISNOTNULL($22), ISNOTNULL($25), ISNOTNULL($27), ISNOTNULL($30), ISNOTNULL($32), ISNOTNULL($35), ISNOTNULL($37), ISNOTNULL($40), ISNOTNULL($42)) rows=15 out=[order_id:I32, customer_id:I32, region_id:I32, id:I32?, region:I32?, i:BOOL?, id0:I32?, i0:BOOL?, id1:I32?, region0:I32?, i1:BOOL?, id2:I32?, i2:BOOL?, id3:I32?, region1:I32?, i3:BOOL?, id4:I32?, i4:BOOL?, id5:I32?, region2:I32?, i5:BOOL?, id6:I32?, i6:BOOL?, id7:I32?, region3:I32?, i7:BOOL?, id8:I32?, i8:BOOL?, id9:I32?, region4:I32?, i9:BOOL?, id10:I32?, i10:BOOL?, id11:I32?, region5:I32?, i11:BOOL?, id12:I32?, i12:BOOL?, id13:I32?, region6:I32?, i13:BOOL?, id14:I32?, i14:BOOL?]
            NestedLoopJoin Left on EQ($1, $41) rows=60 out=[order_id:I32, customer_id:I32, region_id:I32, id:I32?, region:I32?, i:BOOL?, id0:I32?, i0:BOOL?, id1:I32?, region0:I32?, i1:BOOL?, id2:I32?, i2:BOOL?, id3:I32?, region1:I32?, i3:BOOL?, id4:I32?, i4:BOOL?, id5:I32?, region2:I32?, i5:BOOL?, id6:I32?, i6:BOOL?, id7:I32?, region3:I32?, i7:BOOL?, id8:I32?, i8:BOOL?, id9:I32?, region4:I32?, i9:BOOL?, id10:I32?, i10:BOOL?, id11:I32?, region5:I32?, i11:BOOL?, id12:I32?, i12:BOOL?, id13:I32?, region6:I32?, i13:BOOL?, id14:I32?, i14:BOOL?]
              NestedLoopJoin Left on AND(EQ($1, $38), EQ($2, $39)) rows=60 out=[order_id:I32, customer_id:I32, region_id:I32, id:I32?, region:I32?, i:BOOL?, id0:I32?, i0:BOOL?, id1:I32?, region0:I32?, i1:BOOL?, id2:I32?, i2:BOOL?, id3:I32?, region1:I32?, i3:BOOL?, id4:I32?, i4:BOOL?, id5:I32?, region2:I32?, i5:BOOL?, id6:I32?, i6:BOOL?, id7:I32?, region3:I32?, i7:BOOL?, id8:I32?, i8:BOOL?, id9:I32?, region4:I32?, i9:BOOL?, id10:I32?, i10:BOOL?, id11:I32?, region5:I32?, i11:BOOL?, id12:I32?, i12:BOOL?, id13:I32?, region6:I32?, i13:BOOL?]
                NestedLoopJoin Left on EQ($1, $36) rows=60 out=[order_id:I32, customer_id:I32, region_id:I32, id:I32?, region:I32?, i:BOOL?, id0:I32?, i0:BOOL?, id1:I32?, region0:I32?, i1:BOOL?, id2:I32?, i2:BOOL?, id3:I32?, region1:I32?, i3:BOOL?, id4:I32?, i4:BOOL?, id5:I32?, region2:I32?, i5:BOOL?, id6:I32?, i6:BOOL?, id7:I32?, region3:I32?, i7:BOOL?, id8:I32?, i8:BOOL?, id9:I32?, region4:I32?, i9:BOOL?, id10:I32?, i10:BOOL?, id11:I32?, region5:I32?, i11:BOOL?, id12:I32?, i12:BOOL?]
                  NestedLoopJoin Left on AND(EQ($1, $33), EQ($2, $34)) rows=60 out=[order_id:I32, customer_id:I32, region_id:I32, id:I32?, region:I32?, i:BOOL?, id0:I32?, i0:BOOL?, id1:I32?, region0:I32?, i1:BOOL?, id2:I32?, i2:BOOL?, id3:I32?, region1:I32?, i3:BOOL?, id4:I32?, i4:BOOL?, id5:I32?, region2:I32?, i5:BOOL?, id6:I32?, i6:BOOL?, id7:I32?, region3:I32?, i7:BOOL?, id8:I32?, i8:BOOL?, id9:I32?, region4:I32?, i9:BOOL?, id10:I32?, i10:BOOL?, id11:I32?, region5:I32?, i11:BOOL?]
                    NestedLoopJoin Left on EQ($1, $31) rows=60 out=[order_id:I32, customer_id:I32, region_id:I32, id:I32?, region:I32?, i:BOOL?, id0:I32?, i0:BOOL?, id1:I32?, region0:I32?, i1:BOOL?, id2:I32?, i2:BOOL?, id3:I32?, region1:I32?, i3:BOOL?, id4:I32?, i4:BOOL?, id5:I32?, region2:I32?, i5:BOOL?, id6:I32?, i6:BOOL?, id7:I32?, region3:I32?, i7:BOOL?, id8:I32?, i8:BOOL?, id9:I32?, region4:I32?, i9:BOOL?, id10:I32?, i10:BOOL?]
                      NestedLoopJoin Left on AND(EQ($1, $28), EQ($2, $29)) rows=60 out=[order_id:I32, customer_id:I32, region_id:I32, id:I32?, region:I32?, i:BOOL?, id0:I32?, i0:BOOL?, id1:I32?, region0:I32?, i1:BOOL?, id2:I32?, i2:BOOL?, id3:I32?, region1:I32?, i3:BOOL?, id4:I32?, i4:BOOL?, id5:I32?, region2:I32?, i5:BOOL?, id6:I32?, i6:BOOL?, id7:I32?, region3:I32?, i7:BOOL?, id8:I32?, i8:BOOL?, id9:I32?, region4:I32?, i9:BOOL?]
                        NestedLoopJoin Left on EQ($1, $26) rows=60 out=[order_id:I32, customer_id:I32, region_id:I32, id:I32?, region:I32?, i:BOOL?, id0:I32?, i0:BOOL?, id1:I32?, region0:I32?, i1:BOOL?, id2:I32?, i2:BOOL?, id3:I32?, region1:I32?, i3:BOOL?, id4:I32?, i4:BOOL?, id5:I32?, region2:I32?, i5:BOOL?, id6:I32?, i6:BOOL?, id7:I32?, region3:I32?, i7:BOOL?, id8:I32?, i8:BOOL?]
                          NestedLoopJoin Left on AND(EQ($1, $23), EQ($2, $24)) rows=60 out=[order_id:I32, customer_id:I32, region_id:I32, id:I32?, region:I32?, i:BOOL?, id0:I32?, i0:BOOL?, id1:I32?, region0:I32?, i1:BOOL?, id2:I32?, i2:BOOL?, id3:I32?, region1:I32?, i3:BOOL?, id4:I32?, i4:BOOL?, id5:I32?, region2:I32?, i5:BOOL?, id6:I32?, i6:BOOL?, id7:I32?, region3:I32?, i7:BOOL?]
                            NestedLoopJoin Left on EQ($1, $21) rows=60 out=[order_id:I32, customer_id:I32, region_id:I32, id:I32?, region:I32?, i:BOOL?, id0:I32?, i0:BOOL?, id1:I32?, region0:I32?, i1:BOOL?, id2:I32?, i2:BOOL?, id3:I32?, region1:I32?, i3:BOOL?, id4:I32?, i4:BOOL?, id5:I32?, region2:I32?, i5:BOOL?, id6:I32?, i6:BOOL?]
                              NestedLoopJoin Left on AND(EQ($1, $18), EQ($2, $19)) rows=60 out=[order_id:I32, customer_id:I32, region_id:I32, id:I32?, region:I32?, i:BOOL?, id0:I32?, i0:BOOL?, id1:I32?, region0:I32?, i1:BOOL?, id2:I32?, i2:BOOL?, id3:I32?, region1:I32?, i3:BOOL?, id4:I32?, i4:BOOL?, id5:I32?, region2:I32?, i5:BOOL?]
                                NestedLoopJoin Left on EQ($1, $16) rows=60 out=[order_id:I32, customer_id:I32, region_id:I32, id:I32?, region:I32?, i:BOOL?, id0:I32?, i0:BOOL?, id1:I32?, region0:I32?, i1:BOOL?, id2:I32?, i2:BOOL?, id3:I32?, region1:I32?, i3:BOOL?, id4:I32?, i4:BOOL?]
                                  NestedLoopJoin Left on AND(EQ($1, $13), EQ($2, $14)) rows=60 out=[order_id:I32, customer_id:I32, region_id:I32, id:I32?, region:I32?, i:BOOL?, id0:I32?, i0:BOOL?, id1:I32?, region0:I32?, i1:BOOL?, id2:I32?, i2:BOOL?, id3:I32?, region1:I32?, i3:BOOL?]
                                    NestedLoopJoin Left on EQ($1, $11) rows=60 out=[order_id:I32, customer_id:I32, region_id:I32, id:I32?, region:I32?, i:BOOL?, id0:I32?, i0:BOOL?, id1:I32?, region0:I32?, i1:BOOL?, id2:I32?, i2:BOOL?]
                                      NestedLoopJoin Left on AND(EQ($1, $8), EQ($2, $9)) rows=60 out=[order_id:I32, customer_id:I32, region_id:I32, id:I32?, region:I32?, i:BOOL?, id0:I32?, i0:BOOL?, id1:I32?, region0:I32?, i1:BOOL?]
                                        NestedLoopJoin Left on EQ($1, $6) rows=60 out=[order_id:I32, customer_id:I32, region_id:I32, id:I32?, region:I32?, i:BOOL?, id0:I32?, i0:BOOL?]
                                          NestedLoopJoin Left on AND(EQ($1, $3), EQ($2, $4)) rows=60 out=[order_id:I32, customer_id:I32, region_id:I32, id:I32?, region:I32?, i:BOOL?]
                                            RemoteQuery source=facts dialect=duckdb sql='SELECT "order_id", "customer_id", "region_id" FROM "orders"' pushed_plan=yes rows=60 out=[order_id:I32, customer_id:I32, region_id:I32]
                                            Project [$0, $1, true] rows=1 out=[id:I32, region:I32, i:BOOL]
                                              HashAggregate keys=[0,1] measures=[] rows=1 out=[id:I32, region:I32]
                                                BoundTable customer_buyer_within_region rows=1 out=[id:I32, region:I32]
                                          Project [$0, true] rows=1 out=[id:I32, i:BOOL]
                                            HashAggregate keys=[0] measures=[] rows=1 out=[id:I32]
                                              BoundTable customer_buyer rows=1 out=[id:I32]
                                        Project [$0, $1, true] rows=1 out=[id:I32, region:I32, i:BOOL]
                                          HashAggregate keys=[0,1] measures=[] rows=1 out=[id:I32, region:I32]
                                            BoundTable customer_sales_within_region rows=1 out=[id:I32, region:I32]
                                      Project [$0, true] rows=1 out=[id:I32, i:BOOL]
                                        HashAggregate keys=[0] measures=[] rows=1 out=[id:I32]
                                          BoundTable customer_sales rows=1 out=[id:I32]
                                    Project [$0, $1, true] rows=1 out=[id:I32, region:I32, i:BOOL]
                                      HashAggregate keys=[0,1] measures=[] rows=1 out=[id:I32, region:I32]
                                        BoundTable customer_representative_within_region rows=1 out=[id:I32, region:I32]
                                  Project [$0, true] rows=1 out=[id:I32, i:BOOL]
                                    HashAggregate keys=[0] measures=[] rows=1 out=[id:I32]
                                      BoundTable customer_representative rows=1 out=[id:I32]
                                Project [$0, $1, true] rows=1 out=[id:I32, region:I32, i:BOOL]
                                  HashAggregate keys=[0,1] measures=[] rows=1 out=[id:I32, region:I32]
                                    BoundTable customer_reviewer_within_region rows=1 out=[id:I32, region:I32]
                              Project [$0, true] rows=1 out=[id:I32, i:BOOL]
                                HashAggregate keys=[0] measures=[] rows=1 out=[id:I32]
                                  BoundTable customer_reviewer rows=1 out=[id:I32]
                            Project [$0, $1, true] rows=1 out=[id:I32, region:I32, i:BOOL]
                              HashAggregate keys=[0,1] measures=[] rows=1 out=[id:I32, region:I32]
                                BoundTable customer_operator_within_region rows=1 out=[id:I32, region:I32]
                          Project [$0, true] rows=1 out=[id:I32, i:BOOL]
                            HashAggregate keys=[0] measures=[] rows=1 out=[id:I32]
                              BoundTable customer_operator rows=1 out=[id:I32]
                        Project [$0, $1, true] rows=1 out=[id:I32, region:I32, i:BOOL]
                          HashAggregate keys=[0,1] measures=[] rows=1 out=[id:I32, region:I32]
                            BoundTable customer_self_within_region rows=1 out=[id:I32, region:I32]
                      Project [$0, true] rows=1 out=[id:I32, i:BOOL]
                        HashAggregate keys=[0] measures=[] rows=1 out=[id:I32]
                          BoundTable customer_self rows=1 out=[id:I32]
                    Project [$0, $1, true] rows=1 out=[id:I32, region:I32, i:BOOL]
                      HashAggregate keys=[0,1] measures=[] rows=1 out=[id:I32, region:I32]
                        BoundTable customer_auditor_within_region rows=1 out=[id:I32, region:I32]
                  Project [$0, true] rows=1 out=[id:I32, i:BOOL]
                    HashAggregate keys=[0] measures=[] rows=1 out=[id:I32]
                      BoundTable customer_auditor rows=1 out=[id:I32]
                Project [$0, $1, true] rows=1 out=[id:I32, region:I32, i:BOOL]
                  HashAggregate keys=[0,1] measures=[] rows=1 out=[id:I32, region:I32]
                    BoundTable customer_finance_within_region rows=1 out=[id:I32, region:I32]
              Project [$0, true] rows=1 out=[id:I32, i:BOOL]
                HashAggregate keys=[0] measures=[] rows=1 out=[id:I32]
                  BoundTable customer_finance rows=1 out=[id:I32]

context:
    folded    33 names, over global, region, supplier
    required  16 names, over customer

report:
    columns  order_id:Full, product_id:Full, quantity:Full
    tables   order_details: Some rows

remote queries:
    facts (duckdb): SELECT "order_id", "product_id", "quantity" FROM "order_details"
    facts (duckdb): SELECT "order_id", "customer_id", "region_id" FROM "orders"

    as Harbour Partners' buyer:
    order_id  product_id  quantity
    --------  ----------  --------
    3         2           14
    3         7           15
    3         12          16
    11        3           20
    11        8           21
    11        18          19
    19        4           6
    19        14          24
    … 16 more rows

    as Solvang Retail's:
    order_id  product_id  quantity
    --------  ----------  --------
    8         2           11
    8         17          10
    16        13          15
    16        18          16
    24        10          20
    24        15          21
    32        6           5
    32        11          6
    … 6 more rows

    One plan, one digest, two principals. Nothing about either customer
    is in it: every membership is a join to a table the executor
    materialises from the binding, and no customer is named in the SQL.

    `Shape(names)` is what chose the line: the supplier, region and
    warehouse axes are bound, because every principal on the customer's
    side of this marketplace binds them identically, and what is left open
    is the customer, the employee and the caller. A host draws that line
    where its own principals differ.
```

Now the customer's grants are folded. `customer_id = 3` is in the SQL, inside an `EXISTS`
the database can answer, and the marker joins that carried it are gone.

```
-- the tenant plan: the customer's grants folded, the person left open
plan:
    Plan ir_version=1 digest=e6877ff4e9aab18f context_id=tutorial-13 catalog_epoch=1
      RemoteQuery source=facts dialect=duckdb sql='SELECT "order_id", "product_id", "quantity" FROM "order_details" AS "t" WHERE EXISTS (SELECT 1 FROM (SELECT "order_id" AS "$chalk$key" FROM (SELECT "order_id", "customer_id" FROM "orders") AS "t0" WHERE "customer_id" = 3) AS "t2" WHERE "t"."order_id" = "t2"."$chalk$key") ORDER BY "order_id", "product_id"' pushed_plan=yes rows=22.5 out=[order_id:I32, product_id:I32, quantity:I32] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]

context:
    folded    49 names, over customer, global, region, supplier
    required  (none)

report:
    columns  order_id:Full, product_id:Full, quantity:Full
    tables   order_details: Some rows

remote queries:
    facts (duckdb): SELECT "order_id", "product_id", "quantity" FROM "order_details" AS "t" WHERE EXISTS (SELECT 1 FROM (SELECT "order_id" AS "$chalk$key" FROM (SELECT "order_id", "customer_id" FROM "orders") AS "t0" WHERE "customer_id" = 3) AS "t2" WHERE "t"."order_id" = "t2"."$chalk$key") ORDER BY "order_id", "product_id"

stages:
    narrowed from af176e16e558d00d -> entitlements -> hep -> volcano -> root-project

    as Harbour Partners' buyer:
    order_id  product_id  quantity
    --------  ----------  --------
    3         2           14
    3         7           15
    3         12          16
    11        3           20
    11        8           21
    11        18          19
    19        4           6
    19        14          24
    … 16 more rows

    `customer_id = 3` is in the SQL now, and the marker joins that carried
    it are gone: the customer is decided, so the predicate is something the
    database can be asked, and the source returns the lines this binding
    reaches rather than all of them. That is what a fold is for, reached by
    a plan built before the tenant was known. What is still open is who is
    asking, which is what lets every one of that customer's staff share
    this plan.
```

And the last tier: everything folded, nothing required at execution. This is the plan
preparing with the whole binding would have given — digest included — reached by narrowing
rather than by writing the statement out again.

```
-- the person's plan: everything folded, nothing left to bind
plan:
    Plan ir_version=1 digest=e6877ff4e9aab18f context_id=tutorial-13 catalog_epoch=1
      RemoteQuery source=facts dialect=duckdb sql='SELECT "order_id", "product_id", "quantity" FROM "order_details" AS "t" WHERE EXISTS (SELECT 1 FROM (SELECT "order_id" AS "$chalk$key" FROM (SELECT "order_id", "customer_id" FROM "orders") AS "t0" WHERE "customer_id" = 3) AS "t2" WHERE "t"."order_id" = "t2"."$chalk$key") ORDER BY "order_id", "product_id"' pushed_plan=yes rows=22.5 out=[order_id:I32, product_id:I32, quantity:I32] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]

context:
    folded    67 names, over customer, employee, global, mask, region, supplier, user
    required  (none)

report:
    columns  order_id:Full, product_id:Full, quantity:Full
    tables   order_details: Some rows

remote queries:
    facts (duckdb): SELECT "order_id", "product_id", "quantity" FROM "order_details" AS "t" WHERE EXISTS (SELECT 1 FROM (SELECT "order_id" AS "$chalk$key" FROM (SELECT "order_id", "customer_id" FROM "orders") AS "t0" WHERE "customer_id" = 3) AS "t2" WHERE "t"."order_id" = "t2"."$chalk$key") ORDER BY "order_id", "product_id"

stages:
    narrowed from e6877ff4e9aab18f -> entitlements -> hep -> volcano -> root-project

    as employee 5 of Harbour Partners:
    order_id  product_id  quantity
    --------  ----------  --------
    3         2           14
    3         7           15
    3         12          16
    11        3           20
    11        8           21
    11        18          19
    19        4           6
    19        14          24
    … 16 more rows

    Nothing is required at execution and nothing of the context is left in
    the plan as a parameter. This is the plan preparing with the whole
    binding would have given — digest included — reached by narrowing
    rather than by writing the statement out again.

    Three tiers of specialisation from one statement, chosen by what the
    host binds when — and a tenant's plan shared by all its staff.
```

**Why this matters.** Three tiers of specialisation from one statement, chosen by what the
host binds when — and a tenant's plan shared by all its staff.

---

## 14. Planning on a budget

By default the optimiser searches until it has nothing left to try. A host that would
rather have a good plan now than the best plan later says so, and the prepared query reports
how planning ended: converged, budget spent, or stopped because you asked.

Convergence looks by default on a wall-clock cadence (20 ms) rather than a count of rule
evaluations, so it costs nothing to leave on and needs nothing measured up front; a host that
needs the same plan on every machine asks for a count explicitly, which is what the example
below does — so its numbers are the same wherever this runs.

Nothing printed in this chapter is a duration or a count that could differ between machines.

```
    SELECT c.company_name AS customer, s.company_name AS supplier, COUNT(*) AS lines
    FROM customers c
    JOIN facts.orders o ON o.customer_id = c.customer_id
    JOIN facts.order_details d ON d.order_id = o.order_id
    JOIN facts.products p ON p.product_id = d.product_id
    JOIN facts.suppliers s ON s.supplier_id = p.supplier_id
    GROUP BY c.company_name, s.company_name
    ORDER BY lines DESC, customer, supplier
```

```
-- prepared with no options — the search runs to completion
    reason:      Converged
    sampled:     nothing — with no option able to end the search early, no listener is
                 installed and no cost is read
```

Sampling sooner stops sooner and settles for the plan it had. The count is where an
interval comes from: measure the statement once, then pick an interval that samples the part
of the search worth watching.

```
-- prepared with a convergence test — stop when the best plan stops improving

    Sample the root's best cost every N rule evaluations, and stop when
    three samples in a row show no improvement:

    patience 3, interval 5: Converged after 188 rule matches, cost ratio 0.6971, the plan the full search found

    patience 3, interval 20: Converged after 188 rule matches, cost ratio 0.6971, the plan the full search found

    Sampling sooner stops sooner and settles for the plan it had. The count
    is where an interval comes from: measure the statement once, then pick
    an interval that samples the part of the search worth watching.
```

A stop token is the host's "that will do". The prepare still returns a plan.

```
-- prepared with a stop token the host had already cancelled
    reason:      StoppedByHost
    plan:        yes — a stop returns the best complete plan there is, and
                 waits for the first one when there is none yet
```

And the stopped plan is a plan like any other: every physical check ran on it, and it
executes.

```
-- the stopped plan runs
    customer           supplier           lines
    -----------------  -----------------  -----
    Harbour Partners   Épicerie Mourain   1600
    Northwind Trading  Épicerie Mourain   1600
    Rivermouth Foods   Åkerlund Bryggeri  1600
    Vasa Import        Åkerlund Bryggeri  1600
    Harbour Partners   Åkerlund Bryggeri  1400
    … 43 more rows
```

---

## 15. Live pivot

*What is inventory by supplier and warehouse right now?*

One statement, asked four times by four principals, while a refresh replaces every position
underneath it. Four tables carry this and the next two chapters: `warehouses`,
`inventory_positions` (the host's own materialisation of the ledger, replaced whole by each
refresh), `inventory_movements` (append-only — the ledger) and `market_prices`.

The four principals are held constant across chapters 15, 16 and 17, so that access control
is an invariant of the progression rather than something each chapter reintroduces:

| Principal | Grant | Sees |
|---|---|---|
| Warehouse operator | `Grant.ForTenancy(warehouse, "SIN", operator)` | every position and movement in Singapore, whoever supplies it |
| Supplier representative | `Grant.ForTenancy(supplier, "A", representative)` | supplier A's positions and movements in every warehouse |
| Supplier warehouse reviewer | `Grant.ForTenancy(supplier, "A", reviewer).Within(warehouse, "SIN")` | supplier A's rows in Singapore and nothing else — the **intersection** |
| Finance | `Grant.Global(finance)`, with `AllowGlobalGrants()` on the policy | everything, anywhere |

`PIVOT` is ordinary SQL: Calcite parses it in the core grammar and rewrites it to filtered
aggregates before the IR ever sees it.

```
-- the question

    SELECT *
    FROM (SELECT p.supplier_id, ip.warehouse_id, ip.quantity AS amount
          FROM inventory_positions ip
          JOIN products p ON p.product_id = ip.product_id)
    PIVOT (SUM(amount) AS qty FOR warehouse_id IN ('AMS' AS ams, 'SFO' AS sfo, 'SIN' AS sin))
    ORDER BY supplier_id

plan as Finance:
    Plan ir_version=1 digest=b764a327c9b9e0df context_id=tutorial-15 catalog_epoch=1
      Sort [$0 ASC NULLS LAST] rows=5.992 out=[supplier_id:STRING, ams_qty:I32?, sfo_qty:I32?, sin_qty:I32?] collations=[($0 ASC NULLS LAST)]
        Project [$0, CASE WHEN EQ($2, CAST(0 AS I64)) THEN NULL:I32? ELSE $1 END, CASE WHEN EQ($4, CAST(0 AS I64)) THEN NULL:I32? ELSE $3 END, CASE WHEN EQ($6, CAST(0 AS I64)) THEN NULL:I32? ELSE $5 END] rows=5.992 out=[supplier_id:STRING, ams_qty:I32?, sfo_qty:I32?, sin_qty:I32?]
          HashAggregate keys=[6] measures=[SUM0($1) FILTER ISTRUE($2)->I32, COUNT() FILTER ISTRUE($2)->I64, SUM0($1) FILTER ISTRUE($3)->I32, COUNT() FILTER ISTRUE($3)->I64, SUM0($1) FILTER ISTRUE($4)->I32, COUNT() FILTER ISTRUE($4)->I64] rows=5.992 out=[supplier_id:STRING, ams_qty:I32, $f2:I64, sfo_qty:I32, $f4:I64, sin_qty:I32, $f6:I64]
            HashJoin Inner left_keys=[0] right_keys=[0] rows=36 out=[product_id:I32, quantity:I32, $f3:BOOL, $f4:BOOL, $f5:BOOL, product_id0:I32, supplier_id:STRING]
              Project [$1, $3, EQ($0, 'AMS'), EQ($0, 'SFO'), EQ($0, 'SIN')] rows=36 out=[product_id:I32, quantity:I32, $f3:BOOL, $f4:BOOL, $f5:BOOL]
                Read shop.main.inventory_positions projection=[0,1,2,3] descriptor=cd043ab066bf843c86ccfca2e40950e7 entitled rows=36 out=[warehouse_id:STRING, product_id:I32, supplier_id:STRING, quantity:I32] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
              Read shop.main.products projection=[0,1] descriptor=a87b3b38d0608c63b89c102be0be5745 entitled rows=20 out=[product_id:I32, supplier_id:STRING] collations=[($0 ASC NULLS LAST)]

    The PIVOT is gone by the time the IR sees it: it is one aggregate whose
    measures carry FILTER, and a projection that puts NULL back where a
    group had no rows at all. Nothing in the executor knows the word.
```

The entitlement is applied at the leaf, before anything is added up — so the four answers
are four aggregations over four different sets of rows, not one aggregation filtered
afterwards.

```
-- the same statement, four principals

    warehouse operator, Singapore:
    supplier_id  ams_qty  sfo_qty  sin_qty
    -----------  -------  -------  -------
    A            NULL     NULL     330
    B            NULL     NULL     356
    C            NULL     NULL     292
    D            NULL     NULL     228
    E            NULL     NULL     254
    F            NULL     NULL     280

    supplier A's representative:
    supplier_id  ams_qty  sfo_qty  sin_qty
    -----------  -------  -------  -------
    A            214      272      330

    supplier A's reviewer in Singapore:
    supplier_id  ams_qty  sfo_qty  sin_qty
    -----------  -------  -------  -------
    A            NULL     NULL     330

    Finance:
    supplier_id  ams_qty  sfo_qty  sin_qty
    -----------  -------  -------  -------
    A            214      272      330
    B            240      298      356
    C            266      324      292
    D            292      350      228
    E            318      286      254
    F            344      222      280

    Read the four. The operator sees Singapore, whoever supplies it. The
    representative sees supplier A everywhere. The reviewer sees supplier A
    in Singapore and nothing else — the *intersection*, which is what
    `.Within(warehouse, "SIN")` conjoins; two separate grants would have
    given the union of the first two. And Finance sees everything, because
    the policy says AllowGlobalGrants() — off by default, because a grant
    that reaches everywhere should be a thing a deployment decided.
```

### A snapshot is a snapshot

A table's rows are a snapshot. Replacing them never mutates anything: Chalk builds the new
snapshot off to one side and swaps it, so a query already reading the old rows reads all of
them.

```
-- start reading the positions, and stop after one batch

    SELECT warehouse_id, product_id, quantity
    FROM inventory_positions
    ORDER BY warehouse_id, product_id

    AMS    1  113
    AMS    2  126
    AMS    3  139
    AMS    4  152
```

The refresh names the table by the handle the builder handed back, so the rows it
takes are that table's row type and a misnamed table is a compile error rather than a runtime
one.

```
-- replace every position, under one epoch, while that read is open
    catalog epoch 2, shape epoch 1
    the prepared statement is stale: False

    `refresh.Replace(positions, next)` names the table by the handle the
    builder handed back — a PocoTable<InventoryPosition>, so the rows it
    takes are that table's row type and a misnamed table is not a runtime
    error but a compile one. (A host whose tables arrive at run time has a
    string form of the same call; this tutorial uses the handle.)
```

```
-- finish the read that was already open
    32 more rows, and every one of them from the snapshot that read
    started with. It was never shown a row of the replacement.
```

Same statement, same four principals, same plan — different numbers, because the epoch
underneath moved.

```
-- and the pivot again, on the next epoch

    warehouse operator, Singapore:
    supplier_id  ams_qty  sfo_qty  sin_qty
    -----------  -------  -------  -------
    A            NULL     NULL     326
    B            NULL     NULL     366
    C            NULL     NULL     316
    D            NULL     NULL     266
    E            NULL     NULL     306
    F            NULL     NULL     266

    supplier A's representative:
    supplier_id  ams_qty  sfo_qty  sin_qty
    -----------  -------  -------  -------
    A            210      268      326

    supplier A's reviewer in Singapore:
    supplier_id  ams_qty  sfo_qty  sin_qty
    -----------  -------  -------  -------
    A            NULL     NULL     326

    Finance:
    supplier_id  ams_qty  sfo_qty  sin_qty
    -----------  -------  -------  -------
    A            210      268      326
    B            250      308      366
    C            290      348      316
    D            330      388      266
    E            370      338      306
    F            330      208      266

    Same statement, same four principals, same plan — different numbers,
    because the epoch underneath moved. That is the whole of what "live"
    means here: nothing is mutated, a new snapshot is built beside the old
    one and swapped, and a reader that started before the swap finishes
    the answer it started.
```

---

## 16. Streaming live pivot

*Keep that same pivot live as stock moves.*

One concept is added and the pivot text does not change: append-only facts. A batch of
movements arrives, the host applies it to get the next positions, and both land under one
epoch.

```
-- the ledger as it stands, newest first
    movement_id  ts                   warehouse_id  product_id  delta
    -----------  -------------------  ------------  ----------  -----
    24           2026-03-02 12:50:00  SIN           8           -16
    23           2026-03-02 12:40:00  SFO           3           9
    22           2026-03-02 12:30:00  AMS           10          27
    21           2026-03-02 12:20:00  SIN           5           -20
    … 20 more rows
```

The same statement as chapter 15, and the same four slices.

```
-- the pivot, before the batch

    SELECT *
    FROM (SELECT p.supplier_id, ip.warehouse_id, ip.quantity AS amount
          FROM inventory_positions ip
          JOIN products p ON p.product_id = ip.product_id)
    PIVOT (SUM(amount) AS qty FOR warehouse_id IN ('AMS' AS ams, 'SFO' AS sfo, 'SIN' AS sin))
    ORDER BY supplier_id


    warehouse operator, Singapore:
    supplier_id  ams_qty  sfo_qty  sin_qty
    -----------  -------  -------  -------
    A            NULL     NULL     330
    B            NULL     NULL     356
    C            NULL     NULL     292
    D            NULL     NULL     228
    E            NULL     NULL     254
    F            NULL     NULL     280

    supplier A's representative:
    supplier_id  ams_qty  sfo_qty  sin_qty
    -----------  -------  -------  -------
    A            214      272      330

    supplier A's reviewer in Singapore:
    supplier_id  ams_qty  sfo_qty  sin_qty
    -----------  -------  -------  -------
    A            NULL     NULL     330

    Finance:
    supplier_id  ams_qty  sfo_qty  sin_qty
    -----------  -------  -------  -------
    A            214      272      330
    B            240      298      356
    C            266      324      292
    D            292      350      228
    E            318      286      254
    F            344      222      280
```

The batch, and the refresh that lands it.

```
-- a batch of movements, appended — and the positions it implies, replaced
    13:00  SIN  product  1    40
    13:01  SIN  product  5   -15
    13:02  AMS  product  2    25
    13:03  SFO  product  1   -10

    catalog epoch 2 — one epoch, two tables

        await engine.RefreshAsync(refresh =>
        {
            refresh.Append(movements, batch);
            refresh.Replace(positions, next);
        });

    An append extends the table in place: the columns grow, the declared
    order is checked where the new rows join the old, and the index over
    movement_id extends without sorting anything, because the appended
    keys were already in order. The replacement is a whole new snapshot
    beside the old one. Both are visible at the same instant or neither
    is, which is what one epoch buys: no execution can see a movement
    that its positions have not accounted for.
```

```
-- the ledger again
    movement_id  ts                   warehouse_id  product_id  delta
    -----------  -------------------  ------------  ----------  -----
    28           2026-03-02 13:03:00  SFO           1           -10
    27           2026-03-02 13:02:00  AMS           2           25
    26           2026-03-02 13:01:00  SIN           5           -15
    25           2026-03-02 13:00:00  SIN           1           40
    … 24 more rows
```

The next epoch, through the same statement.

```
-- and the pivot — the same text, the next epoch

    warehouse operator, Singapore:
    supplier_id  ams_qty  sfo_qty  sin_qty
    -----------  -------  -------  -------
    A            NULL     NULL     370
    B            NULL     NULL     356
    C            NULL     NULL     292
    D            NULL     NULL     228
    E            NULL     NULL     239
    F            NULL     NULL     280

    supplier A's representative:
    supplier_id  ams_qty  sfo_qty  sin_qty
    -----------  -------  -------  -------
    A            214      262      370

    supplier A's reviewer in Singapore:
    supplier_id  ams_qty  sfo_qty  sin_qty
    -----------  -------  -------  -------
    A            NULL     NULL     370

    Finance:
    supplier_id  ams_qty  sfo_qty  sin_qty
    -----------  -------  -------  -------
    A            214      262      370
    B            265      298      356
    C            266      324      292
    D            292      350      228
    E            318      286      239
    F            344      222      280

    Singapore moved, because that is where two of the four movements
    landed, and the reviewer's slice moved with the representative's where
    the two overlap. Nobody's statement changed.
```

### Bootstrapping a stream

A live pivot that starts from nothing is a live pivot that is wrong for as long as the
history takes to arrive. The usual answer is two tables and a union in every query. The
answer here is one table whose partitions live in two different **kinds** of source — the
history in a warehouse, the tail in memory — so the statement is the same statement before
and after the feed has caught up.

```csharp
PartitionCatalog.Table(
    "ledger",
    columns,
    partitionColumn: 5,                        // `stage`
    [("warehouse", "history"), ("tail", "live")],
    stage => stage == "history" ? "ledger_history" : "ledger_live",
    rowsPerPartition: 12);
```

```
-- Bootstrapping a stream
    The ledger so far, split where the feed took over: everything up to
    movement 16 was loaded from the warehouse, and everything after it
    arrived on the wire. Two physical tables, in two kinds of source, and
    one logical table over them.

    SELECT warehouse_id, stage, COUNT(*) AS entries, MAX(movement_id) AS frontier
    FROM federated.ledger
    GROUP BY warehouse_id, stage
    ORDER BY warehouse_id, stage

plan:
    Plan ir_version=1 digest=c3ab1ad486e0ef84 context_id=tutorial-16b catalog_epoch=1
      Sort [$0 ASC NULLS LAST, $1 ASC NULLS LAST] rows=2.4 out=[warehouse_id:STRING, stage:STRING, entries:I64, frontier:I32] collations=[($0 ASC NULLS LAST, $1 ASC NULLS LAST)]
        HashAggregate keys=[2,5] measures=[COUNT()->I64, MAX($0)->I32] rows=2.4 out=[warehouse_id:STRING, stage:STRING, entries:I64, frontier:I32]
          PartitionedScan partitions=2 values=['history', 'live'] rows=24 out=[movement_id:I32, ts:TIMESTAMP(9), warehouse_id:STRING, product_id:I32, delta:I32, stage:STRING]
            RemoteQuery source=warehouse dialect=duckdb sql='SELECT "movement_id", "ts", "warehouse_id", "product_id", "delta", "stage" FROM "ledger_history"' pushed_plan=yes rows=16 out=[movement_id:I32, ts:TIMESTAMP(9), warehouse_id:STRING, product_id:I32, delta:I32, stage:STRING]
            Read tail.tail.ledger_live projection=[0,1,2,3,4,5] rows=8 out=[movement_id:I32, ts:TIMESTAMP(9), warehouse_id:STRING, product_id:I32, delta:I32, stage:STRING] collations=[($0 ASC NULLS LAST)]

what each partition was asked to run:
    warehouse (duckdb): SELECT "movement_id", "ts", "warehouse_id", "product_id", "delta", "stage" FROM "ledger_history"

    warehouse_id  stage    entries  frontier
    ------------  -------  -------  --------
    AMS           history  6        16
    AMS           live     2        22
    SFO           history  5        14
    SFO           live     3        23
    SIN           history  5        15
    SIN           live     3        24

    6 produced, 24 scanned, 1 remote call(s), 16 rows fetched
    warehouse: calls=1 rows=16

    One partition became SQL and the other did not, because the other has
    no dialect: an in-process list is read where it stands. The frontier a
    feed processor needs — the last movement it has per warehouse — is
    read off the bootstrap's own snapshot by the same statement, which is
    what makes "where do I resume?" a query rather than a protocol.
```

The tail-buffer swap is the whole of a feed processor: accumulate into a list the host
owns, and swap the list under one epoch.

```
-- the tail-buffer swap, which is the whole of a feed processor
    4 entries buffered and swapped in, catalog epoch 2
    warehouse_id  stage    entries  frontier
    ------------  -------  -------  --------
    AMS           history  6        16
    AMS           live     3        27
    SFO           history  5        14
    SFO           live     4        28
    SIN           history  5        15
    SIN           live     5        26

    The feed accumulates into a list the host owns and swaps the whole list
    under one epoch; readers in flight finish on the list they started
    with. No row is ever mutated, so there is no lock anywhere in this and
    no reader is ever blocked by a writer.
```

---

## 17. Temporal streaming live pivot

*Keep the pivot live when both quantities and prices move independently.*

Movements land every ten minutes; prices land every forty. There is no instant at which both
are known, and waiting for one would be waiting forever — so the question changes shape: not
"the price now" but "the price that was in force when this happened". That is what an `ASOF`
join is, and it arrives here as the answer to a problem the progression has just created.

A price is public and carries no tenancy at all. What is confidential is how much of a
thing somebody holds, so a valuation is restricted exactly as the movements are — through the
join, and not at all by the price.

```
-- the second stream: prices, on nobody's cadence but their own

    SELECT product_id, ts, price
    FROM market_prices
    WHERE product_id = 1
    ORDER BY ts

    product_id  ts                   price
    ----------  -------------------  -----
    1           2026-03-02 09:00:00  4.5
    1           2026-03-02 09:40:00  6.25
    1           2026-03-02 10:20:00  5.25
    1           2026-03-02 11:00:00  7
    1           2026-03-02 11:40:00  6
    1           2026-03-02 12:20:00  5
    1           2026-03-02 13:00:00  6.75
    1           2026-03-02 13:40:00  5.75

    `market_prices` is declared Unrestricted: a price is public, and there
    is nothing on the row to restrict. What is confidential is how much of
    a thing somebody holds — so a valuation is restricted exactly as the
    movements are, through the join, and not at all by the price.
```

The pivot clause is chapter 15's, character for character. What changed is underneath it.

```
-- the join a question like this needs

    SELECT *
    FROM (SELECT p.supplier_id, m.warehouse_id, m.delta * mp.price AS amount
          FROM inventory_movements m
          ASOF JOIN market_prices mp
            MATCH_CONDITION mp.ts <= m.ts
            ON mp.product_id = m.product_id
          JOIN products p ON p.product_id = m.product_id)
    PIVOT (SUM(amount) AS qty FOR warehouse_id IN ('AMS' AS ams, 'SFO' AS sfo, 'SIN' AS sin))
    ORDER BY supplier_id

    The pivot clause is chapter 15's, character for character:

        PIVOT (SUM(amount) AS qty FOR warehouse_id IN ('AMS' AS ams, 'SFO' AS sfo, 'SIN' AS sin))

    What changed is underneath it. An ASOF join takes, for each movement,
    the last price at or before the movement's own timestamp — one pass
    down two ordered streams rather than a range join and a window over
    its output.
plan as Finance:
    Plan ir_version=1 digest=894623342d5af176 context_id=tutorial-17 catalog_epoch=1
      Sort [$0 ASC NULLS LAST] rows=1.833 out=[supplier_id:STRING, ams_qty:DECIMAL(38,2)?, sfo_qty:DECIMAL(38,2)?, sin_qty:DECIMAL(38,2)?] collations=[($0 ASC NULLS LAST)]
        Project [$0, CASE WHEN EQ($2, CAST(0 AS I64)) THEN NULL:DECIMAL(38,2)? ELSE $1 END, CASE WHEN EQ($4, CAST(0 AS I64)) THEN NULL:DECIMAL(38,2)? ELSE $3 END, CASE WHEN EQ($6, CAST(0 AS I64)) THEN NULL:DECIMAL(38,2)? ELSE $5 END] rows=1.833 out=[supplier_id:STRING, ams_qty:DECIMAL(38,2)?, sfo_qty:DECIMAL(38,2)?, sin_qty:DECIMAL(38,2)?]
          HashAggregate keys=[6] measures=[SUM0($1) FILTER ISTRUE($2)->DECIMAL(38,2), COUNT() FILTER ISTRUE($2)->I64, SUM0($1) FILTER ISTRUE($3)->DECIMAL(38,2), COUNT() FILTER ISTRUE($3)->I64, SUM0($1) FILTER ISTRUE($4)->DECIMAL(38,2), COUNT() FILTER ISTRUE($4)->I64] rows=1.833 out=[supplier_id:STRING, ams_qty:DECIMAL(38,2), $f2:I64, sfo_qty:DECIMAL(38,2), $f4:I64, sin_qty:DECIMAL(38,2), $f6:I64]
            Project [$2, $3, $4, $5, $6, $0, $1] rows=2 out=[product_id:I32, amount:DECIMAL(38,2), $f3:BOOL, $f4:BOOL, $f5:BOOL, product_id0:I32, supplier_id:STRING]
              HashJoin Inner left_keys=[0] right_keys=[0] rows=2 out=[product_id:I32, supplier_id:STRING, product_id0:I32, amount:DECIMAL(38,2), $f3:BOOL, $f4:BOOL, $f5:BOOL]
                Read shop.main.products projection=[0,1] descriptor=a87b3b38d0608c63b89c102be0be5745 entitled rows=20 out=[product_id:I32, supplier_id:STRING] collations=[($0 ASC NULLS LAST)]
                Project [$3, MULTIPLY(CAST($4 AS DECIMAL(28,2)), $8), EQ($2, 'AMS'), EQ($2, 'SFO'), EQ($2, 'SIN')] rows=2 out=[product_id:I32, amount:DECIMAL(38,2), $f3:BOOL, $f4:BOOL, $f5:BOOL]
                  AsOfJoin Inner left_keys=[3] right_keys=[2] match=$1 >= $1 rows=2 out=[movement_id:I32, ts:TIMESTAMP(9), warehouse_id:STRING, product_id:I32, delta:I32, price_id:I32, ts0:TIMESTAMP(9), product_id0:I32, price:DECIMAL(28,2)]
                    Read shop.main.inventory_movements projection=[0,1,2,3,5] descriptor=cd043ab066bf843c86ccfca2e40950e7 entitled rows=24 out=[movement_id:I32, ts:TIMESTAMP(9), warehouse_id:STRING, product_id:I32, delta:I32] collations=[($0 ASC NULLS LAST)]
                    Read shop.main.market_prices projection=[0,1,2,3] rows=96 out=[price_id:I32, ts:TIMESTAMP(9), product_id:I32, price:DECIMAL(28,2)] collations=[($0 ASC NULLS LAST)]
```

```
-- the valuation, four principals

    warehouse operator, Singapore:
    supplier_id  ams_qty  sfo_qty  sin_qty
    -----------  -------  -------  --------
    B            NULL     NULL     -1427.25
    E            NULL     NULL     -3059

    supplier A's representative:
    supplier_id  ams_qty  sfo_qty  sin_qty
    -----------  -------  -------  -------
    A            1274     NULL     NULL

    supplier A's reviewer in Singapore:
    supplier_id  ams_qty  sfo_qty  sin_qty
    -----------  -------  -------  -------

    Finance:
    supplier_id  ams_qty  sfo_qty  sin_qty
    -----------  -------  -------  --------
    A            1274     NULL     NULL
    B            NULL     NULL     -1427.25
    C            NULL     2468.5   NULL
    D            1597.75  NULL     NULL
    E            NULL     NULL     -3059
    F            NULL     1421     NULL

    The reviewer's slice is empty, and that is the intersection being
    honest: supplier A has moved nothing through Singapore yet. Watch it
    when the next batch lands.
```

Both feeds advance, independently, each under its own epoch.

```
-- both streams advance, independently, under one epoch each
    4 movements appended, catalog epoch 2
    12 prices appended, catalog epoch 3

    Two refreshes, not one: the two feeds are independent and neither waits
    for the other. That is exactly the situation an ASOF join is for — each
    execution values whatever movements it can see at whatever prices were
    in force, and nothing is ever valued at a price from its own future.
```

Three chapters, one pivot clause. In 15 it summed a position the host replaced; in 16 a
position the host derived from a batch it appended; here a valuation of that same ledger at
the prices a second feed delivered. The four principals never changed, and neither did what
each of them is allowed to see.

```
-- and the valuation again

    warehouse operator, Singapore:
    supplier_id  ams_qty  sfo_qty  sin_qty
    -----------  -------  -------  --------
    A            NULL     NULL     270
    B            NULL     NULL     -1427.25
    E            NULL     NULL     -3711.5

    supplier A's representative:
    supplier_id  ams_qty  sfo_qty  sin_qty
    -----------  -------  -------  -------
    A            1274     -67.5    270

    supplier A's reviewer in Singapore:
    supplier_id  ams_qty  sfo_qty  sin_qty
    -----------  -------  -------  -------
    A            NULL     NULL     270

    Finance:
    supplier_id  ams_qty  sfo_qty  sin_qty
    -----------  -------  -------  --------
    A            1274     -67.5    270
    B            650      NULL     -1427.25
    C            NULL     2468.5   NULL
    D            1597.75  NULL     NULL
    E            NULL     NULL     -3711.5
    F            NULL     1421     NULL

    Three chapters, one pivot clause. In 15 it summed a position the host
    replaced; in 16 a position the host derived from a batch it appended;
    here a valuation of that same ledger at the prices a second feed
    delivered. The four principals never changed, and neither did what
    each of them is allowed to see.
```

---

## 18. Advanced topics: runtime policy configuration

Every policy in this tutorial so far is written as code, with a field per name. A host that
layers its own API over Chalk has none of that: its tenancies, roles, tables and rules arrive
as strings in a configuration file, and the policy has to be built while the process starts.

The typed surface is the same surface there. Nothing in it is generic over a row type,
because at this point in a host's startup there is no row type to name — which is the one
thing the surface deliberately does not do.

This is the typed surface's own example verbatim, and it is also a
test: if the surface moves under it, the sample stops compiling.

```
-- the mapping, whole
    var p = TenancyPolicy.Declare(catalog);
    var kinds = config.Tenancies.ToDictionary(t => t.Name, t => p.Tenancy(t.Name));
    var staff = p.Subject(config.Subject.Name, within: config.Subject.Within.Select(n => kinds[n]));
    var roles = config.Roles.ToDictionary(r => r.Name, r => p.Role(r.Name));

    foreach (var t in config.Tables)
    {
        Table table = p.Source(t.Source).Table(t.Name);
        table.Tenancy(x =>
        {
            foreach (var d in t.Direct)  x.Direct(kinds[d.Kind], table.Column(d.Column));
            foreach (var r in t.Related) x.Related(kinds[r.Kind]).Through(r.Steps.Select(s => p.Source(s.Source).Table(s.Table)));
        });
        foreach (var rule in t.Rules)
            table.Access(table.Column(rule.Column), Grantees(rule, roles), rule.Verdict,
                         placeholder: rule.Placeholder is null ? null : Sql.Of(rule.Placeholder));
    }
```

Two of those rules name a **marker** rather than a role. `Roles.Owner` is the row's
resource owner, and the rule's condition gains `employee_id = @ctx.user`. `Roles.Visible` is
everyone the table's row predicate admits — every declared role's scope, the owner and the
global grant — and the compiler writes that predicate, textually, as the rule's condition. A
host that means "anyone who can see this row" can say so instead of listing the roles and
getting the list wrong later.

```
-- what that configuration compiled to
orders, row predicate:
    (((customer_id, region_id) IN (@ctx.customer_buyer_within_region) OR customer_id IN (@ctx.customer_buyer) OR (region_id, customer_id) IN (@ctx.region_buyer_within_customer) OR region_id IN (@ctx.region_buyer) OR (employee_id, customer_id) IN (@ctx.employee_buyer_pairs) OR employee_id IN (@ctx.employee_buyer_ids) OR @ctx.global_buyer) OR ((customer_id, region_id) IN (@ctx.customer_representative_within_region) OR customer_id IN (@ctx.customer_representative) OR (region_id, customer_id) IN (@ctx.region_representative_within_customer) OR region_id IN (@ctx.region_representative) OR (employee_id, customer_id) IN (@ctx.employee_representative_pairs) OR employee_id IN (@ctx.employee_representative_ids) OR @ctx.global_representative) OR ((customer_id, region_id) IN (@ctx.customer_sales_within_region) OR customer_id IN (@ctx.customer_sales) OR (region_id, customer_id) IN (@ctx.region_sales_within_customer) OR region_id IN (@ctx.region_sales) OR (employee_id, customer_id) IN (@ctx.employee_sales_pairs) OR employee_id IN (@ctx.employee_sales_ids) OR @ctx.global_sales)) OR employee_id = @ctx.user OR @ctx.global

    inherited paths:
      supplier -> products via order_details -> products

    column rules:
      column 4: Full when employee_id = @ctx.user
      column 4: Full when (((customer_id, region_id) IN (@ctx.customer_buyer_within_region) OR customer_id IN (@ctx.custom… (1117 characters)
      column 5: Full when employee_id = @ctx.user
      column 5: Full when employee_id = @ctx.user
      column 5: None placeholder CAST(NULL AS DECIMAL(19,2)) when ((customer_id, region_id) IN (@ctx.customer_representative_within_region) OR customer_id IN (@ct… (455 characters)

    Two of those rules name a marker rather than a role. `owner` is the
    row's resource owner, and the rule's condition gains
    `employee_id = @ctx.user`. `visible` is everyone the table's row
    predicate admits — every declared role's scope, the owner and the
    global grant — and the compiler writes that predicate as the rule's
    condition, so a host that means "anyone who can see this row" can say
    so instead of listing the roles and getting it wrong later.
```

And the result is a policy like any other.

```
-- and it is a policy like any other

    SELECT order_id, region_id, employee_id, order_date, freight
    FROM orders
    ORDER BY order_id

plan:
    Plan ir_version=1 digest=4fcef75c47f95670 context_id=tutorial-18 catalog_epoch=1
      Project [$0, $2, $1, $3, CAST(CASE WHEN EQ($1, 4) THEN $4 ELSE NULL:DECIMAL(28,2)? END AS DECIMAL(28,2)?)] rows=15 out=[order_id:I32, region_id:I32, employee_id:I32, order_date:TIMESTAMP(9), freight:DECIMAL(28,2)?] collations=[($0 ASC NULLS LAST)]
        Filter OR(EQ($2, 4), EQ($1, 4)) rows=15 out=[order_id:I32, employee_id:I32, region_id:I32, order_date:TIMESTAMP(9), freight:DECIMAL(28,2)] collations=[($0 ASC NULLS LAST)]
          Read shop.main.orders projection=[0,2,3,4,5] disclosures=[5:PER_ROW] descriptor=f92cda3b39f90d54b3eaa48af97a811a entitled rows=60 out=[order_id:I32, employee_id:I32, region_id:I32, order_date:TIMESTAMP(9), freight:DECIMAL(28,2)] collations=[($0 ASC NULLS LAST)]

report:
    columns  order_id:Full, region_id:Full, employee_id:Full, order_date:Full, freight:PerRow
    tables   orders: Some rows

    order_id  region_id  employee_id  order_date           freight
    --------  ---------  -----------  -------------------  -------
    4         8          4            2026-03-02 11:15:00  36.75
    8         4          2            2026-03-02 14:15:00  NULL
    10        7          4            2026-03-02 15:45:00  86.25
    11        4          5            2026-03-02 16:30:00  NULL
    16        5          4            2026-03-02 20:15:00  45.75
    22        4          4            2026-03-03 00:45:00  95.25
    25        4          1            2026-03-03 03:00:00  NULL
    28        3          4            2026-03-03 05:15:00  54.75
    … 8 more rows

    Nothing about that plan says the policy came from a file. The typed
    surface is the surface either way; what a configuration loses is the
    compiler checking the *names*, and what it keeps is everything else —
    a kind where a kind belongs, a column that must come from the table it
    is on, and a source a table is obtained from rather than guessed at.

Done. docs/tutorial.md walks through this output.
```

**Why this matters.** What a configuration loses is the compiler checking the *names*.
What it keeps is everything else: a kind where a kind belongs, a column that must come from
the table it is on, and a source a table is obtained from rather than guessed at.

---

