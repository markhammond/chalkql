-- CALCITE-4149, CALCITE-6070, CALCITE-3922: the only output column is a rename, and both sort
-- keys are dropped from the output. The trimmer must keep postcode and id below the sort and out
-- of the row, and the rename must survive both the trimmer and PROJECT_REMOVE.
SELECT last_name AS surname FROM members ORDER BY postcode, id LIMIT 3
