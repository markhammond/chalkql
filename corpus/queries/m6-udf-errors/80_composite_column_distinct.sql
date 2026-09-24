-- SELECT DISTINCT over a composite column is refused by name, for the same reason.
-- expect: error=UNSUPPORTED
SELECT DISTINCT venue FROM quotes
