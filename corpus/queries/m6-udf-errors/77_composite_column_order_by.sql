-- A composite column has no ordering, as a function's composite has none (D302). ORDER BY one the
-- statement does not select is refused by name when the statement is validated, like any other.
-- expect: error=UNSUPPORTED
SELECT id FROM quotes ORDER BY bid
