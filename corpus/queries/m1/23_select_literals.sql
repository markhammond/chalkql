-- expect: has(VirtualTable)
-- expect: not(Read)
SELECT 1 AS "one", 'x' AS s, CAST(NULL AS BIGINT) AS n
