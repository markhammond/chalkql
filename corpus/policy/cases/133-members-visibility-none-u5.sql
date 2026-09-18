-- §3.12: `visibility = NONE` is the row predicate folding to FALSE, and it is computed from the
-- context and the statement alone -- no count of hidden rows is ever reported (D207).
SELECT COUNT(*) AS n FROM members
