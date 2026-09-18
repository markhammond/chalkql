-- expect: has(Fetch)
-- expect: not(Sort)
-- expect: not(TopN)
SELECT * FROM bars ORDER BY ts OFFSET 100 ROWS FETCH NEXT 50 ROWS ONLY
