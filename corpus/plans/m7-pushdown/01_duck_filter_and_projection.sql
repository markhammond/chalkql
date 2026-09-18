-- Full
source=duck dialect=duckdb parameters=0
SELECT "l_orderkey", "l_quantity" FROM (SELECT "l_orderkey", "l_quantity", "l_discount", "l_shipdate" FROM "lineitem") AS "t" WHERE "l_shipdate" >= DATE '1995-01-01' AND ("l_discount" >= 0.05 AND "l_discount" <= 0.07)

-- FiltersOnly
source=duck dialect=duckdb parameters=0
SELECT "l_orderkey", "l_quantity" FROM (SELECT "l_orderkey", "l_quantity", "l_discount", "l_shipdate" FROM "lineitem") AS "t" WHERE "l_shipdate" >= DATE '1995-01-01' AND ("l_discount" >= 0.05 AND "l_discount" <= 0.07)

-- ProjectionOnly
source=duck dialect=duckdb parameters=0
SELECT "l_orderkey", "l_quantity", "l_discount", "l_shipdate" FROM "lineitem"

