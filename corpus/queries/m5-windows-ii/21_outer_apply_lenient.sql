-- D68: APPLY rides on the conformance option, and means the same as query 20.
-- conformance: LENIENT
-- expect: not(Correlate)
SELECT s.symbol, b.ts
FROM symbols s
OUTER APPLY (SELECT ts FROM bars_small b
             WHERE b.symbol = s.symbol AND b.volume > 999999 LIMIT 1) b
ORDER BY s.symbol
