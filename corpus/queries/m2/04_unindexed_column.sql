-- expect: not(IndexLookup)
-- expect: has(Read)
-- expect: has(Filter)
SELECT * FROM bars WHERE "close" > ?
