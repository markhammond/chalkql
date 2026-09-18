-- A1: a descending fetch on a key that never ties, for the case that *is* determined.
-- expect: has(TopN)
SELECT label, amount FROM sales ORDER BY label DESC FETCH NEXT 2 ROWS ONLY
