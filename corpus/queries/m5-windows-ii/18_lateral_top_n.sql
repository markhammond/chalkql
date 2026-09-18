-- D67: per-group top-N. The decorrelator rewrites it into ROW_NUMBER plus a filter and a join.
-- expect: not(Correlate)
-- expect: has(Window)
SELECT s.symbol, b.ts, b."close"
FROM symbols s
CROSS JOIN LATERAL (SELECT ts, "close" FROM bars_small b WHERE b.symbol = s.symbol
                    ORDER BY ts DESC LIMIT 3) b
ORDER BY s.symbol, b.ts
