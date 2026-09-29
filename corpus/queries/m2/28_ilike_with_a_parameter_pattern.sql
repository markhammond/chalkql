-- D314, D316: a parameter ILIKE pattern, compiled once per execution with its literals lowered, under
-- an escape it does not use. Bound to iNT%, it keeps Int, Int32, Int64 and Interesting.
-- expect: has(Filter)
-- expect: not(IndexLookup)
-- expect: has_function(ILIKE)
SELECT name FROM terms WHERE name ILIKE ? ESCAPE '!'
