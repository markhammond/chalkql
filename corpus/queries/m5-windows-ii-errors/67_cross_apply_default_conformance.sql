-- D68: APPLY is gated by the conformance level, and the gate is in the parser.
-- expect: error=PARSE
-- expect: position
SELECT s.symbol, b.ts
FROM symbols s CROSS APPLY (SELECT ts FROM bars_small b WHERE b.symbol = s.symbol) b
