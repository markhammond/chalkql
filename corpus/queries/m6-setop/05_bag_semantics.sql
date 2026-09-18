-- D69: the ALL forms keep multiplicities. `bars_small` holds 1 000 rows per symbol and `bars` holds
-- 20 160, so INTERSECT ALL keeps the smaller count and EXCEPT ALL the difference.
-- expect: has(SetOp)
SELECT symbol FROM bars_small
INTERSECT ALL
SELECT symbol FROM bars
ORDER BY symbol
