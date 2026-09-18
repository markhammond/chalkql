-- Full
source=duck dialect=duckdb parameters=0
SELECT "o_orderkey", "o_custkey" FROM (SELECT "o_orderkey", "o_custkey" FROM "orders") AS "t" WHERE ("o_custkey" // 3) = 2 ORDER BY "o_orderkey"

-- FiltersOnly
source=duck dialect=duckdb parameters=0
SELECT "o_orderkey", "o_custkey" FROM (SELECT "o_orderkey", "o_custkey" FROM "orders") AS "t" WHERE ("o_custkey" // 3) = 2

-- ProjectionOnly
source=duck dialect=duckdb parameters=0
SELECT "o_orderkey", "o_custkey" FROM "orders"

