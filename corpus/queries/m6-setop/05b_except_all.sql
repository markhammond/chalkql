-- The other half of D69's bag semantics: EXCEPT ALL subtracts counts rather than removing keys.
-- expect: has(SetOp)
SELECT symbol FROM bars
EXCEPT ALL
SELECT symbol FROM bars_small
ORDER BY symbol
