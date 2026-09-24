-- Design 51 §0, the owner's query: two fields of one composite-valued call. Calcite makes it one
-- projection of two field accesses over the call — the driver never splits the call per field
-- (D292) — and the executor evaluates the call once per row for both of them (D293).
-- expect: has_user_function(main.price_move)
-- expect: count(FieldAccess)=2
SELECT symbol, ts,
       price_move("open", "close").direction,
       price_move("open", "close").change
FROM bars_small
ORDER BY ts, symbol
