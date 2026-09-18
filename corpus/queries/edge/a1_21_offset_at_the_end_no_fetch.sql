-- A1: an offset that lands exactly on the end, with no fetch.
-- expect: has(Fetch)
SELECT k, v FROM sorted ORDER BY k, v OFFSET 4 ROWS
