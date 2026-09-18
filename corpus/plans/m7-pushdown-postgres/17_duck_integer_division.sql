-- Full
source=pg dialect=postgresql parameters=0
SELECT "o_orderkey", "o_custkey", "o_custkey" / 3 AS "third", (0 - "o_custkey") / 3 AS "negative_third", "o_custkey" / 3 + 1 AS "third_then_plus", 10 - "o_custkey" / 3 AS "third_on_the_right", ("o_custkey" + 1) / 3 AS "sum_then_third", CAST("o_custkey" AS DOUBLE PRECISION) / 3 AS "real_third" FROM (SELECT "o_orderkey", "o_custkey" FROM "orders") AS "t" WHERE "o_custkey" / 3 = 2 ORDER BY "o_orderkey"

-- FiltersOnly
source=pg dialect=postgresql parameters=0
SELECT "o_orderkey", "o_custkey", "o_custkey" / 3 AS "third", (0 - "o_custkey") / 3 AS "negative_third", "o_custkey" / 3 + 1 AS "third_then_plus", 10 - "o_custkey" / 3 AS "third_on_the_right", ("o_custkey" + 1) / 3 AS "sum_then_third", CAST("o_custkey" AS DOUBLE PRECISION) / 3 AS "real_third" FROM (SELECT "o_orderkey", "o_custkey" FROM "orders") AS "t" WHERE "o_custkey" / 3 = 2

-- ProjectionOnly
source=pg dialect=postgresql parameters=0
SELECT "o_orderkey", "o_custkey" FROM "orders"

