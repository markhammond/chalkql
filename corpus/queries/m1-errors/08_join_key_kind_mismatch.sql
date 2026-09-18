-- expect: error=VALIDATION
-- expect: position
SELECT b.symbol
FROM bars b JOIN symbols s ON b.ts = s.tick_size
