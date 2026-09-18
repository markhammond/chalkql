-- CALCITE-4250: RelMdColumnOrigins returns the wrong column index for an Aggregate, and the
-- taint check's clauses 2 and 4 are origin walks. The group key is deliberately last in the
-- select list and the guarded measure is in the middle, so a mis-indexed origin walk labels the
-- wrong column and guards the wrong measure.
SELECT COUNT(*) AS n, SUM(amount) AS s, org_id
FROM orders GROUP BY org_id ORDER BY org_id
