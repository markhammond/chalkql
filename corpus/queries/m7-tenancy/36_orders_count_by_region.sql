-- D266 corpus 36. A count by region: the confined grants show as whole regions missing rather than
-- as rows missing from a region, which is what a conjoined confinement does to an aggregate. COUNT(*)
-- reads no column, so nothing here is population-only and every principal gets an answer.
-- expect: principals(all)
SELECT region_id, COUNT(*) AS n FROM orders GROUP BY region_id ORDER BY region_id
