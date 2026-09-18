source=duck dialect=duckdb parameters=0
SELECT "symbol", "ts" FROM "bars"
source=pg dialect=postgresql parameters=0
SELECT "symbol", "rate" FROM "funding"
