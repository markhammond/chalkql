-- The whole value as a column (D291): no field is selected, so the struct reaches the host as one
-- Arrow struct column whose children are named after the record's properties.
-- expect: has_user_function(main.price_move)
-- expect: count(FieldAccess)=0
SELECT symbol, ts, price_move("open", "close") AS m
FROM bars_small
ORDER BY ts, symbol
