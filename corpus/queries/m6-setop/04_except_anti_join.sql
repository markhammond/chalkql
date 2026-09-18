-- D69 and V29: a single-column EXCEPT DISTINCT is also an ANTI join, and MINUS_TO_ANTI_JOIN offers
-- that alternative to the cost model. This is the corpus's first ANTI join either way — the joins
-- report's open item (a).
-- expect: has(HashJoin)
-- expect: not(SetOp)
SELECT symbol FROM symbols
EXCEPT
SELECT symbol FROM events
ORDER BY symbol
