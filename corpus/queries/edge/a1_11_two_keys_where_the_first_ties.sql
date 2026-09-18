-- A1: k ties and v breaks it, so the order is total after all.
-- expect: has(Sort)
SELECT k, v FROM sorted ORDER BY k, v
