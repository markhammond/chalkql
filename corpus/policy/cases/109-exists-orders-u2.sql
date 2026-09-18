-- §3: the pass recurses into every sub-query relation, which with `expand = false` is still a
-- RexSubQuery inside the Filter's condition when the pass runs.
SELECT id FROM members m WHERE EXISTS (SELECT 1 FROM orders o WHERE o.member_id = m.id)
ORDER BY id
