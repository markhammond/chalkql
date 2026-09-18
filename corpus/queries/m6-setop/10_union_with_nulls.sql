-- D69: NULLs are values in a set operation, so two of them are equal and a DISTINCT keeps one.
-- `vwap` is nullable in bars_small, and the second branch adds a NULL of its own.
-- expect: has(SetOp)
SELECT vwap FROM bars_small WHERE symbol = 'BTCUSDT' AND ts < TIMESTAMP '2026-01-01 00:10:00'
UNION
SELECT CAST(NULL AS DECIMAL(28, 10)) AS vwap
ORDER BY vwap
