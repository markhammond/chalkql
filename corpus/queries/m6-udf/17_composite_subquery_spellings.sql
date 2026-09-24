-- The subquery spellings: `(m).change` reads a field of the alias's composite column and `s.m.*`
-- expands to every field of it. Once Calcite merges the projections each field access is over the
-- call itself, so the call runs once per row whichever spelling asked for it (D293).
-- expect: has_user_function(main.price_move)
-- expect: count(FieldAccess)=3
SELECT s.symbol, (m).change AS move, s.m.*
FROM (SELECT symbol, ts, price_move("open", "close") AS m FROM bars_small) AS s
ORDER BY s.ts, s.symbol
