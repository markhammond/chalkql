source=duck dialect=duckdb parameters=1
SELECT "o_orderkey" FROM "orders" ORDER BY "o_orderkey" LIMIT ?
source=sqlite dialect=sqlite parameters=0
SELECT "o_orderkey" FROM "orders"
