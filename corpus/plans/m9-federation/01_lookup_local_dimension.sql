source=duck dialect=duckdb parameters=1
SELECT "symbol", "ts", "close" FROM "bars" WHERE "volume" > 9900 AND "symbol" IN (?)
