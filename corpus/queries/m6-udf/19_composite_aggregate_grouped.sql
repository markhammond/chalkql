-- A composite-valued aggregate, grouped: one aggregate call whose measure column is a composite,
-- and two field accesses over that column in the projection above it (D291).
-- expect: has(HashAggregate)
-- expect: has_user_function(main.close_range)
-- expect: count(FieldAccess)=2
SELECT symbol,
       close_range("close").low AS low,
       close_range("close").high AS high
FROM bars_small
GROUP BY symbol
ORDER BY symbol
