-- expect: has(Project)
-- expect: has_function(ADD)
-- expect: has_function(MULTIPLY)
-- Nullable operands through arithmetic and comparison: the null-propagation paths the corpus
-- otherwise only touches through aggregates.
SELECT symbol,
       trade_count + 1 AS tc_plus,
       vwap * 2 AS vwap_doubled,
       trade_count > 100 AS busy
FROM bars
