-- D69: a set operation composes — one branch is bars, the other is funding shaped like bars, and
-- the union of the two joins symbols.
-- expect: has(SetOp)
-- expect: has(HashJoin)
SELECT u.symbol, s.quote, COUNT(*) AS n
FROM (SELECT symbol, ts FROM bars_small
      UNION ALL
      SELECT symbol, ts FROM funding) AS u
JOIN symbols s ON s.symbol = u.symbol
GROUP BY u.symbol, s.quote
ORDER BY u.symbol
