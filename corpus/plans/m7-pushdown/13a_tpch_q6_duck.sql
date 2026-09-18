-- Full
source=duck dialect=duckdb parameters=0
SELECT COALESCE(SUM("l_extendedprice" * "l_discount"), 0) AS "revenue", COUNT(*) AS "$f1" FROM (SELECT "l_quantity", "l_extendedprice", "l_discount", "l_shipdate" FROM "lineitem") AS "t" WHERE "l_shipdate" >= DATE '1995-01-01' AND "l_shipdate" < DATE '1996-01-01' AND ("l_discount" >= 0.05 AND "l_discount" <= 0.07) AND "l_quantity" < 24.00

-- FiltersOnly
source=duck dialect=duckdb parameters=0
SELECT "l_extendedprice" * "l_discount" AS "$f0" FROM (SELECT "l_quantity", "l_extendedprice", "l_discount", "l_shipdate" FROM "lineitem") AS "t" WHERE "l_shipdate" >= DATE '1995-01-01' AND "l_shipdate" < DATE '1996-01-01' AND ("l_discount" >= 0.05 AND "l_discount" <= 0.07) AND "l_quantity" < 24.00

-- ProjectionOnly
source=duck dialect=duckdb parameters=0
SELECT "l_quantity", "l_extendedprice", "l_discount", "l_shipdate" FROM "lineitem"

