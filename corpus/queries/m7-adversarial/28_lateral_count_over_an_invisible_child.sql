-- §3.13. A correlated count, decorrelated into a join, asking how many messages each thread has.
-- Both tables are entitled and the child through the parent, so the count is over the messages this
-- principal may see — a thread they can see with messages they cannot would count zero.
-- expect: principals(all)
SELECT t.id, c.n
FROM threads t LEFT JOIN LATERAL (
  SELECT COUNT(*) AS n FROM messages m WHERE m.thread_id = t.id) c ON TRUE
ORDER BY t.id
