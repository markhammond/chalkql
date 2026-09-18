-- D67: no bar has a volume that large, so every symbol is NULL-padded.
-- expect: not(Correlate)
SELECT s.symbol, b.ts
FROM symbols s
LEFT JOIN LATERAL (SELECT ts FROM bars_small b
                   WHERE b.symbol = s.symbol AND b.volume > 999999 LIMIT 1) b ON TRUE
ORDER BY s.symbol
