-- §5 corpus 01. The SQL body is inlined before validation, so the plan shows the DIVIDE the body is
-- written as and no call at all. Both arguments are NOT NULL, so the STRICT guard folds away too.
-- expect: has_function(DIVIDE)
-- expect: not_if_then
-- expect: not_user_function
SELECT symbol, ts, pct_change("open", "close") AS chg
FROM bars_small
ORDER BY ts, symbol
