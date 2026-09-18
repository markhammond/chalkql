-- expect: param_style=ordinal
-- expect: parameters=[1, 2, 3]
-- expect: count(DynamicParam)=5
SELECT symbol, ts FROM bars
WHERE ts >= $1 AND symbol = $2 AND (trade_count IS NULL OR ts < $3 AND ts >= $1)
