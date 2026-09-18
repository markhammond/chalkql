-- CALCITE-6611, CALCITE-3352, ADR 0014: the sort keys are not in the select list, so the root
-- projection has to come after optimisation and the required collation is stated over the
-- unprojected row. SORT_PROJECT_TRANSPOSE runs inside top-down Volcano.
SELECT first_name, id FROM members ORDER BY postcode, id
