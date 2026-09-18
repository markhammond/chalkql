-- expect: has(Project)
-- expect: has_if_then
SELECT symbol, ts,
       CASE WHEN "close" > "open" THEN 'up' WHEN "close" < "open" THEN 'down' ELSE 'flat' END AS dir
FROM bars
