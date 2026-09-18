-- D69: UNION_MERGE folds the nested UNION into one n-ary SetOp, so the plan has exactly one of
-- them with three inputs rather than two nested ones.
-- expect: count(SetOp) = 1
SELECT symbol FROM bars_small WHERE symbol = 'BTCUSDT'
UNION
SELECT symbol FROM bars_small WHERE symbol = 'ETHUSDT'
UNION
SELECT symbol FROM bars_small WHERE symbol = 'SOLUSDT'
ORDER BY symbol
