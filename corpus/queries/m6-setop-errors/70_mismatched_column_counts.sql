-- D69: the branches of a set operation have the same number of columns. Calcite checks it and says
-- where.
-- expect: error=VALIDATION
-- expect: position
SELECT symbol, ts FROM bars_small
UNION ALL
SELECT symbol FROM symbols
