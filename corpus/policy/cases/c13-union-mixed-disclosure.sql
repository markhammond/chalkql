-- CALCITE-1268, CALCITE-2346: one branch's second column is masked and the other's is not, so
-- the reported disclosure of the output column is the meet of the two. The branches are read
-- positionally by the client, so a set operation that aligned them by name would be a wrong
-- column read with no error.
SELECT id, first_name FROM members WHERE org_id = 1
UNION ALL
SELECT id, CAST(postcode AS VARCHAR) FROM members WHERE org_id = 1
