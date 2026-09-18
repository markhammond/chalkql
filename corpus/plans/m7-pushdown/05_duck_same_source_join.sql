-- Full
source=duck dialect=duckdb parameters=0
SELECT "t3"."o_orderkey", "t3"."o_totalprice", "t1"."c_name" FROM (SELECT "c_custkey", "c_name" FROM (SELECT "c_custkey", "c_name", "c_mktsegment" FROM "customer") AS "t" WHERE "c_mktsegment" = 'BUILDING') AS "t1" INNER JOIN (SELECT "o_orderkey", "o_custkey", "o_totalprice" FROM (SELECT "o_orderkey", "o_custkey", "o_totalprice" FROM "orders") AS "t2" WHERE "o_orderkey" < 500) AS "t3" ON "t1"."c_custkey" = "t3"."o_custkey" ORDER BY "t3"."o_orderkey"

-- FiltersOnly
source=duck dialect=duckdb parameters=0
SELECT "o_orderkey", "o_custkey", "o_totalprice" FROM (SELECT "o_orderkey", "o_custkey", "o_totalprice" FROM "orders") AS "t" WHERE "o_orderkey" < 500
source=duck dialect=duckdb parameters=0
SELECT "c_custkey", "c_name" FROM (SELECT "c_custkey", "c_name", "c_mktsegment" FROM "customer") AS "t" WHERE "c_mktsegment" = 'BUILDING'

-- ProjectionOnly
source=duck dialect=duckdb parameters=0
SELECT "o_orderkey", "o_custkey", "o_totalprice" FROM "orders"
source=duck dialect=duckdb parameters=0
SELECT "c_custkey", "c_name", "c_mktsegment" FROM "customer"

