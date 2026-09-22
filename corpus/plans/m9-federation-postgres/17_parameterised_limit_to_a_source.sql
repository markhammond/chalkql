source=pg dialect=postgresql parameters=1
SELECT "o_orderkey", "o_totalprice" FROM (SELECT "o_orderkey", "o_totalprice" FROM "orders") AS "t" WHERE "o_totalprice" > 100000.00 ORDER BY "o_orderkey" FETCH NEXT ? ROWS ONLY
