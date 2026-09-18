-- D57: the fraction is a constant in [0, 1], which Calcite checks itself.
-- expect: error=VALIDATION
-- expect: position
SELECT PERCENTILE_CONT(1.5) WITHIN GROUP (ORDER BY "close") FROM bars_small
