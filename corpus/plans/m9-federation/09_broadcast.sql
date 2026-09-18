source=sqlite dialect=sqlite parameters=1
SELECT "o_orderkey", "o_custkey" FROM "orders" WHERE "o_orderkey" <= 800 AND "o_custkey" IN (VALUES (?))
