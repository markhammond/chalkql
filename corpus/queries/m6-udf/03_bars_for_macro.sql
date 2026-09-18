-- §5 corpus 03. A SQL-bodied table function is a Calcite macro: the call is gone and what is left is
-- an ordinary filtered scan, which the (symbol, ts) index answers.
-- expect: has(IndexLookup)
-- expect: not_user_function
SELECT symbol, ts, "close" FROM TABLE(bars_for('BTCUSDT')) ORDER BY ts
