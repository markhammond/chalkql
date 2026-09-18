-- CALCITE-3352, CALCITE-7616, CALCITE-7644: a window partitioned by a masked column. The
-- partition key is a mask, so the row numbers are the masks' groups and not the raw values';
-- PROJECT_TO_LOGICAL_PROJECT_AND_WINDOW and PROJECT_WINDOW_TRANSPOSE both rewrite this shape.
SELECT id, first_name, ROW_NUMBER() OVER (PARTITION BY first_name ORDER BY id) AS rn
FROM members
