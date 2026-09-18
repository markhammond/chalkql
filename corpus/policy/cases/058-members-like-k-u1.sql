-- §3.1, as u1: the raw surname matches in the manager's org; the agent's org has initials
-- M, B and H, none of which start with K.
SELECT id FROM members WHERE last_name LIKE 'K%' ORDER BY id
