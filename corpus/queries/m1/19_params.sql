-- expect: param_types=[STRING?, TIMESTAMP(9)?]
-- expect: count(DynamicParam)=3
SELECT symbol, ts, "close" FROM bars WHERE symbol = ? AND ts >= ?
