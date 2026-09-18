source=pg dialect=postgresql parameters=1
SELECT "o_orderkey", "o_custkey" FROM "orders" WHERE "o_orderkey" <= 2000 AND "o_custkey" IN (?)
source=pg dialect=postgresql parameters=0
SELECT "o_orderkey", "o_custkey" FROM (SELECT "o_orderkey", "o_custkey" FROM "orders") AS "t" WHERE "o_orderkey" <= 2000
