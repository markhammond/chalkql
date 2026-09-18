-- CALCITE-3558, CALCITE-597: a pruned projection whose output order is not the table's. The
-- read's projection has to be the table ordinals in output order, and the read's per-column
-- disclosure outcomes are stated for every column of the table, not only the projected ones.
SELECT postcode, id FROM members WHERE org_id = 1
