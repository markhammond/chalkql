-- CALCITE-4803, CALCITE-4250: the grouping key set is (org_id, postcode) and the select list is
-- (postcode, org_id, ...). AGGREGATE_PROJECT_MERGE folds the projection below the aggregate into
-- its group set, which is where the two orders have to be kept apart.
SELECT postcode, org_id, COUNT(*) AS n, MIN(first_name) AS f
FROM members GROUP BY org_id, postcode
