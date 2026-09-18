-- expect: has(Window)
-- expect: not(Sort)
SELECT symbol, ts, COUNT(*) OVER () AS n FROM bars_small
