-- D58: a LIST has no equality, so nothing groups by one; refused by name, naming the column.
-- expect: error=UNSUPPORTED
SELECT tags, COUNT(*) FROM symbols GROUP BY tags
