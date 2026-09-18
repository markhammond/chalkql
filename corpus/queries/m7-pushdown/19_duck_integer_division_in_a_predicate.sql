-- F27 (ADR 0027), the silent half. The division is in the predicate and nowhere else, so no column
-- of the result carries its type and nothing about the answer looks wrong: under integer division
-- `o_custkey / 3 = 2` selects custkeys 6, 7 and 8, and under DuckDB's real division it selects only
-- 6, because 7 / 3 is 2.333… and 8 / 3 is 2.666… . Query 17's projection fails the scan contract
-- loudly; this one came back with a third of its rows missing and a plausible-looking result set.
--
-- It is the case the differential exists for, and the reason a corpus query that divides integers
-- on DuckDB had to be written before the fix could be called proven: there was not one.
-- expect: has(RemoteQuery)
-- expect: not(Project)
-- expect: not(Filter)
SELECT o_orderkey, o_custkey
FROM duck.orders
WHERE o_custkey / 3 = 2
ORDER BY o_orderkey
