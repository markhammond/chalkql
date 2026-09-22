source=duck dialect=duckdb parameters=0
SELECT "symbol", "ts", "close" FROM "bars_btcusdt"
source=duck dialect=duckdb parameters=0
SELECT "symbol", "ts", "close" FROM "bars_ethusdt"
source=duck2 dialect=duckdb parameters=0
SELECT "symbol", "ts", "close" FROM "bars_solusdt"
source=duck2 dialect=duckdb parameters=0
SELECT "symbol", "ts", "close" FROM "bars_adausdt"
source=sqlite dialect=sqlite parameters=0
SELECT "symbol", "ts", "close" FROM "bars_xrpusdt"
