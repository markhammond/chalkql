-- D67: a lateral that computes an expression decorrelates into a projection.
-- expect: not(Correlate)
SELECT b.symbol, r.rng
FROM bars_small b, LATERAL (SELECT b.high - b.low AS rng) r
WHERE r.rng > 1
ORDER BY b.symbol, b.ts
