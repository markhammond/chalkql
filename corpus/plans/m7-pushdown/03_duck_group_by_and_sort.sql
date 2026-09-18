-- Full
source=duck dialect=duckdb parameters=0
SELECT "l_returnflag", COALESCE(SUM("l_quantity"), 0) AS "total" FROM "lineitem" GROUP BY "l_returnflag" ORDER BY "l_returnflag"

-- FiltersOnly
source=duck dialect=duckdb parameters=0
SELECT "l_quantity", "l_returnflag" FROM "lineitem"

-- ProjectionOnly
source=duck dialect=duckdb parameters=0
SELECT "l_quantity", "l_returnflag" FROM "lineitem"

