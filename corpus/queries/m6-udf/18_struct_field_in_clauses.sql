-- A field in WHERE, GROUP BY and ORDER BY. Calcite projects the scalar field before it filters,
-- groups or sorts, so nothing ever compares, groups or sorts a struct.
-- expect: has(HashAggregate)
-- expect: has_user_function(main.price_move)
-- expect: has_field_access
SELECT price_move("open", "close").direction AS direction,
       COUNT(*) AS bars,
       MAX(price_move("open", "close").change) AS widest
FROM bars_small
WHERE price_move("open", "close").change <> 0.0
GROUP BY price_move("open", "close").direction
ORDER BY direction
