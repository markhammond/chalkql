-- Full
source=duck dialect=duckdb parameters=1
SELECT "l_orderkey", "l_shipdate" FROM (SELECT "l_orderkey", "l_shipdate" FROM "lineitem") AS "t" WHERE "l_shipdate" >= ? AND "l_orderkey" < 300 ORDER BY "l_orderkey"

-- FiltersOnly
source=duck dialect=duckdb parameters=1
SELECT "l_orderkey", "l_shipdate" FROM (SELECT "l_orderkey", "l_shipdate" FROM "lineitem") AS "t" WHERE "l_shipdate" >= ? AND "l_orderkey" < 300

-- ProjectionOnly
source=duck dialect=duckdb parameters=0
SELECT "l_orderkey", "l_shipdate" FROM "lineitem"

