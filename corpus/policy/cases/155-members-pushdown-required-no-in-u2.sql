-- §8 corpus 21, D199: PUSHDOWN_REQUIRED over a profile that does not declare the IN shape. For
-- a source holding every tenancy's rows a silent full fetch is the worse failure.
SELECT * FROM members ORDER BY id
