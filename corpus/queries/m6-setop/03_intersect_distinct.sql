-- D69: INTERSECT DISTINCT. Every bar's symbol is a symbols row (that is the F14 foreign key), so
-- the answer is every symbol that has bars.
-- expect: has(SetOp)
SELECT symbol FROM bars_small
INTERSECT
SELECT symbol FROM symbols
ORDER BY symbol
