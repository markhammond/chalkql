-- Full
source=duck dialect=duckdb parameters=0
SELECT "t7"."l_orderkey", COALESCE(SUM("t7"."$f3"), 0) AS "revenue", "t4"."o_orderdate", "t4"."o_shippriority" FROM (SELECT "t0"."o_orderkey", "t0"."o_orderdate", "t0"."o_shippriority" FROM (SELECT "o_orderkey", "o_custkey", "o_orderdate", "o_shippriority" FROM (SELECT "o_orderkey", "o_custkey", "o_orderdate", "o_shippriority" FROM "orders") AS "t" WHERE "o_orderdate" < DATE '1995-03-15') AS "t0" INNER JOIN (SELECT "c_custkey" FROM (SELECT "c_custkey", "c_mktsegment" FROM "customer") AS "t1" WHERE "c_mktsegment" = 'BUILDING') AS "t3" ON "t0"."o_custkey" = "t3"."c_custkey") AS "t4" INNER JOIN (SELECT "l_orderkey", "l_extendedprice" * (1 - "l_discount") AS "$f3" FROM (SELECT "l_orderkey", "l_extendedprice", "l_discount", "l_shipdate" FROM "lineitem") AS "t5" WHERE "l_shipdate" > DATE '1995-03-15') AS "t7" ON "t4"."o_orderkey" = "t7"."l_orderkey" GROUP BY "t4"."o_orderdate", "t4"."o_shippriority", "t7"."l_orderkey" ORDER BY 2 DESC NULLS FIRST, "t4"."o_orderdate" LIMIT 10

-- FiltersOnly
source=duck dialect=duckdb parameters=0
SELECT "o_orderkey", "o_custkey", "o_orderdate", "o_shippriority" FROM (SELECT "o_orderkey", "o_custkey", "o_orderdate", "o_shippriority" FROM "orders") AS "t" WHERE "o_orderdate" < DATE '1995-03-15'
source=duck dialect=duckdb parameters=0
SELECT "c_custkey" FROM (SELECT "c_custkey", "c_mktsegment" FROM "customer") AS "t" WHERE "c_mktsegment" = 'BUILDING'
source=duck dialect=duckdb parameters=0
SELECT "l_orderkey", "l_extendedprice" * (1 - "l_discount") AS "$f3" FROM (SELECT "l_orderkey", "l_extendedprice", "l_discount", "l_shipdate" FROM "lineitem") AS "t" WHERE "l_shipdate" > DATE '1995-03-15'

-- ProjectionOnly
source=duck dialect=duckdb parameters=0
SELECT "o_orderkey", "o_custkey", "o_orderdate", "o_shippriority" FROM "orders"
source=duck dialect=duckdb parameters=0
SELECT "c_custkey", "c_mktsegment" FROM "customer"
source=duck dialect=duckdb parameters=0
SELECT "l_orderkey", "l_extendedprice", "l_discount", "l_shipdate" FROM "lineitem"

