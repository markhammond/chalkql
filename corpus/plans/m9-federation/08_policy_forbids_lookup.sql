source=duck dialect=duckdb parameters=0
SELECT "symbol", "ts", "close" FROM (SELECT "symbol", "ts", "close", "volume" FROM "bars") AS "t" WHERE "volume" > 9900
