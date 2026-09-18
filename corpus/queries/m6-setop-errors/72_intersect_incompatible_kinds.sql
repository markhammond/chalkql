-- D69: the branches' row types have to be assignable to a common one. A TIMESTAMP against a
-- DECIMAL has no least-restrictive type, so the validator refuses it and says which column.
-- expect: error=VALIDATION
-- expect: position
SELECT ts FROM bars_small
INTERSECT
SELECT vwap FROM bars_small
