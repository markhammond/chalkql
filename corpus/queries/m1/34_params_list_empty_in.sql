-- expect: param_style=named
-- expect: parameters=[symbols]
-- expect: accepts_list=[symbols]
SELECT COUNT(*) AS n FROM bars WHERE symbol IN @symbols
