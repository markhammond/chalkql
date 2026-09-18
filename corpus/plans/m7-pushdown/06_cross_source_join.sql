-- Full
source=duck dialect=duckdb parameters=1
SELECT "o_orderkey", "o_custkey" FROM "orders" WHERE "o_orderkey" < 200 AND "o_custkey" IN (?)

-- FiltersOnly
source=duck dialect=duckdb parameters=0
SELECT "o_orderkey", "o_custkey" FROM (SELECT "o_orderkey", "o_custkey" FROM "orders") AS "t" WHERE "o_orderkey" < 200

-- ProjectionOnly
source=duck dialect=duckdb parameters=0
SELECT "o_orderkey", "o_custkey" FROM "orders"

