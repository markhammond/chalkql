-- CALCITE-4541, CALCITE-2610: GROUP BY by ordinal, which needs a conformance that allows it.
-- The grouping key must be `org_id` and not the literal 1.
SELECT org_id, COUNT(*) AS n FROM members GROUP BY 1
