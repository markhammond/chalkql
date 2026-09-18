-- §3.6: a grouping that puts every group below the floor. COUNT(*) still answers, which is the
-- design's stated limit: the guard is query-set-size control, not differential privacy.
SELECT member_id, COUNT(*) AS n, SUM(amount) AS s
FROM orders GROUP BY member_id ORDER BY member_id
