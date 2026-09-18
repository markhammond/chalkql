-- expect: has(Project)
-- expect: has(IndexLookup)
-- The equality predicate pins `symbol` to a constant, so constant folding evaluates every one of
-- these at plan time — which is the right thing for the planner to do and the reason query 36
-- exists to exercise the kernels themselves.
SELECT UPPER(symbol) AS u, LOWER(symbol) AS l, CHAR_LENGTH(symbol) AS n,
       SUBSTRING(symbol FROM 1 FOR 3) AS pfx, symbol || '-PERP' AS perp
FROM bars WHERE symbol = 'SOLUSDT'
