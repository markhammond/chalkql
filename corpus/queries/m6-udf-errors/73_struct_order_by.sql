-- A struct has no ordering (D291). ORDER BY one is refused by name when the statement is validated —
-- before Calcite could convert it into a sort nothing can run — and the refusal says to sort by one
-- of its fields instead.
-- expect: error=UNSUPPORTED
SELECT symbol, ts FROM bars_small ORDER BY price_move("open", "close")
