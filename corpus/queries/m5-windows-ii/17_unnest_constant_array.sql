-- D66: a constant array has no correlation, so Calcite emits a bare Uncollect.
-- expect: has(Unnest)
-- expect: has(VirtualTable)
SELECT * FROM UNNEST(ARRAY[1, 2, 3]) AS t(x)
