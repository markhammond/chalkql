source=duck dialect=duckdb parameters=1
SELECT "o_orderkey" FROM "orders" ORDER BY "o_orderkey" LIMIT ?
source=pg dialect=postgresql parameters=1
SELECT "o_orderkey" FROM "orders" ORDER BY "o_orderkey" FETCH NEXT ? ROWS ONLY
