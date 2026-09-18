-- expect: has(Window)
-- expect: has(Filter)
-- The DESC order key is the one the (symbol, ts) index cannot serve, so this is the query that sorts
-- (ADR 0017); every other per-symbol window below streams out of the index.
-- expect: has(Sort)
SELECT symbol, ts, "close"
FROM (SELECT symbol, ts, "close",
             ROW_NUMBER() OVER (PARTITION BY symbol ORDER BY ts DESC) AS rn
      FROM bars_small)
WHERE rn = 1
ORDER BY symbol
