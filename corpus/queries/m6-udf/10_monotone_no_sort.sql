-- §5 corpus 10. `minute_of` is declared INCREASING in its argument, so the table's declared (ts,
-- symbol) collation passes through the call and the ORDER BY needs no Sort at all.
-- expect: not(Sort)
-- expect: has_user_function(main.minute_of)
SELECT symbol FROM bars_small ORDER BY minute_of(ts)
