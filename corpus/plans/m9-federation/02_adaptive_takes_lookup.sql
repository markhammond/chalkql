source=duck dialect=duckdb parameters=1
SELECT "o_orderkey", "o_custkey", "o_totalprice" FROM "orders" WHERE "o_orderkey" <= 3000 AND "o_custkey" IN (?)
source=duck dialect=duckdb parameters=0
SELECT "o_orderkey", "o_custkey", "o_totalprice" FROM (SELECT "o_orderkey", "o_custkey", "o_totalprice" FROM "orders") AS "t" WHERE "o_orderkey" <= 3000
