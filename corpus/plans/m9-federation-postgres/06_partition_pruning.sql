source=duck dialect=duckdb parameters=0
SELECT "symbol", "ts", "open", "high", "low", "close", "volume", "vwap", "trade_count" FROM "bars_btcusdt"
source=pg dialect=postgresql parameters=0
SELECT "symbol", "ts", "open", "high", "low", "close", "volume", "vwap", "trade_count" FROM "bars_xrpusdt"
