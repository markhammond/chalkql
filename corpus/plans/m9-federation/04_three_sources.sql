source=duck dialect=duckdb parameters=0
SELECT "symbol", "ts" FROM "bars"
source=sqlite dialect=sqlite parameters=0
SELECT "symbol", "rate" FROM "funding"
