source=pg dialect=postgresql parameters=0
SELECT "o_orderkey", "o_custkey", "o_totalprice" FROM (SELECT "o_orderkey", "o_custkey", "o_totalprice" FROM "orders") AS "t" WHERE "o_custkey" <= 40
source=duck dialect=duckdb parameters=0
SELECT "c_custkey", "c_name" FROM (SELECT "c_custkey", "c_name" FROM "customer") AS "t" WHERE "c_custkey" <= 40
