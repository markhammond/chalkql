-- expect: not(IndexLookup)
-- expect: has(Read)
-- expect: has(Filter)
SELECT COUNT(*) FROM bars WHERE ts >= TIMESTAMP '2026-01-02 00:00:00'
