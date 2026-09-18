-- expect: param_style=named
-- expect: parameters=[symbols, before]
-- expect: accepts_list=[symbols]
SELECT symbol, ts FROM bars WHERE symbol IN @symbols AND ts < @before
