-- expect: param_style=named
-- expect: parameters=[symbol, from, to]
-- expect: count(DynamicParam)=4
SELECT symbol, ts, "close" FROM bars
WHERE symbol = @symbol AND ts >= @from AND ts < @to
