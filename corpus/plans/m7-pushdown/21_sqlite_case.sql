-- Full
source=sqlite dialect=sqlite parameters=0
SELECT "o_orderkey", CASE WHEN "o_custkey" > 100 THEN 3 WHEN "o_custkey" > 50 THEN 2 ELSE 1 END AS "band", CASE WHEN "o_shippriority" = 0 THEN 10 WHEN "o_shippriority" = 1 THEN 20 ELSE 30 END AS "priority_code", CASE WHEN "o_custkey" > 120 THEN "o_custkey" ELSE NULL END AS "big_customer", CASE WHEN "o_custkey" > 130 THEN NULL ELSE "o_orderkey" END AS "small_order" FROM (SELECT "o_orderkey", "o_custkey", "o_shippriority" FROM "orders") AS "t" WHERE "o_shippriority" > 0 AND "o_custkey" > 140 OR "o_orderkey" > 140 AND "o_shippriority" <= 0

-- FiltersOnly
source=sqlite dialect=sqlite parameters=0
SELECT "o_orderkey", CASE WHEN "o_custkey" > 100 THEN 3 WHEN "o_custkey" > 50 THEN 2 ELSE 1 END AS "band", CASE WHEN "o_shippriority" = 0 THEN 10 WHEN "o_shippriority" = 1 THEN 20 ELSE 30 END AS "priority_code", CASE WHEN "o_custkey" > 120 THEN "o_custkey" ELSE NULL END AS "big_customer", CASE WHEN "o_custkey" > 130 THEN NULL ELSE "o_orderkey" END AS "small_order" FROM (SELECT "o_orderkey", "o_custkey", "o_shippriority" FROM "orders") AS "t" WHERE "o_shippriority" > 0 AND "o_custkey" > 140 OR "o_orderkey" > 140 AND "o_shippriority" <= 0

-- ProjectionOnly
source=sqlite dialect=sqlite parameters=0
SELECT "o_orderkey", "o_custkey", "o_shippriority" FROM "orders"

