-- expect: join_type=NestedLoopJoin:FULL
SELECT s.symbol, s.base, e.id, e.kind
FROM (SELECT symbol, base FROM symbols WHERE base <> 'BTC') s
FULL JOIN (SELECT id, symbol, kind FROM events WHERE kind = 'listing') e
  ON s.symbol = e.symbol
ORDER BY s.symbol, e.id
