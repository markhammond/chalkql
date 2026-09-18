-- V26's residual shape (ADR 0018): a non-COUNT aggregate on the inner side of a CROSS JOIN LATERAL.
-- Calcite's general decorrelator widens the measure to nullable over an inner join that cannot
-- produce a NULL, and the first rule that merges the projection back disagrees with it. D67 turns
-- that into UNSUPPORTED naming the shape rather than into an internal error.
-- expect: error=UNSUPPORTED
SELECT s.symbol, x.m
FROM symbols s, LATERAL (SELECT MAX(b."close") AS m FROM bars_small b WHERE b.symbol = s.symbol) x
