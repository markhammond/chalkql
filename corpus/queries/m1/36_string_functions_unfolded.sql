-- expect: has(Project)
-- expect: has_function(UPPER)
-- expect: has_function(LOWER)
-- expect: has_function(CHAR_LENGTH)
-- expect: has_function(SUBSTRING)
-- expect: has_function(CONCAT)
-- expect: has_function(LIKE)
-- Query 30 as the design writes it, with a predicate that does not pin `symbol` to a constant, so
-- the string kernels actually run rather than being folded away at plan time.
SELECT UPPER(symbol) AS u, LOWER(symbol) AS l, CHAR_LENGTH(symbol) AS n,
       SUBSTRING(symbol FROM 1 FOR 3) AS pfx, symbol || '-PERP' AS perp
FROM bars WHERE symbol LIKE '%USDT'
