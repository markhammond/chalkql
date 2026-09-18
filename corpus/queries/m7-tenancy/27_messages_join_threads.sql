-- §3.13 corpus 27. The statement's *own* join on the correlation key, beside the pass's. The
-- planner's join reads `thread_id` raw beneath the child's sanitiser and this one reads what the
-- child discloses, which is the same column and the same value — the package leaves a correlation
-- key in full unless the host puts it in a realm (D227).
-- expect: principals(all)
SELECT m.id, m.content, t.org_id FROM messages m JOIN threads t ON m.thread_id = t.id ORDER BY m.id
