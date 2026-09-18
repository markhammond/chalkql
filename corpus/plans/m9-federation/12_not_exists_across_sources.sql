source=duck dialect=duckdb parameters=0
SELECT "o_custkey", TRUE AS "$f1" FROM "orders" GROUP BY "o_custkey"
