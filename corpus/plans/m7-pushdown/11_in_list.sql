-- Full
source=duck dialect=duckdb parameters=0
SELECT "l_orderkey", "l_partkey" FROM (SELECT "l_orderkey", "l_partkey" FROM "lineitem") AS "t" WHERE "l_partkey" IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20) ORDER BY "l_orderkey", "l_partkey"

-- FiltersOnly
source=duck dialect=duckdb parameters=0
SELECT "l_orderkey", "l_partkey" FROM (SELECT "l_orderkey", "l_partkey" FROM "lineitem") AS "t" WHERE "l_partkey" IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20)

-- ProjectionOnly
source=duck dialect=duckdb parameters=0
SELECT "l_orderkey", "l_partkey" FROM "lineitem"

