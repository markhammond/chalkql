-- Full
source=duck dialect=duckdb parameters=0
SELECT COUNT(DISTINCT "l_suppkey") AS "suppliers" FROM "lineitem"

-- FiltersOnly
source=duck dialect=duckdb parameters=0
SELECT "l_suppkey" FROM "lineitem"

-- ProjectionOnly
source=duck dialect=duckdb parameters=0
SELECT "l_suppkey" FROM "lineitem"

