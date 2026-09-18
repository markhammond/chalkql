-- §3.6: positions are unchanged, so an ORDER BY over a guarded aggregate keeps its reference and
-- sorts the NULL the guard produced. NULLS LAST is spelled out so the order is determined.
SELECT org_id, SUM(amount) AS s
FROM orders GROUP BY org_id ORDER BY s DESC NULLS LAST
