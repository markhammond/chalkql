-- Full
source=pg dialect=postgresql parameters=0
SELECT "o_orderkey", "o_custkey" FROM (SELECT "o_orderkey", "o_custkey" FROM "orders") AS "t" WHERE "o_custkey" / 3 = 2 ORDER BY "o_orderkey"

-- FiltersOnly
source=pg dialect=postgresql parameters=0
SELECT "o_orderkey", "o_custkey" FROM (SELECT "o_orderkey", "o_custkey" FROM "orders") AS "t" WHERE "o_custkey" / 3 = 2

-- ProjectionOnly
source=pg dialect=postgresql parameters=0
SELECT "o_orderkey", "o_custkey" FROM "orders"

