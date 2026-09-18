-- §3.1, as u1: the same predicate matches the agent's org, where the value is an initial, and
-- none of the manager's rows, where it is a name.
SELECT id FROM members WHERE first_name = 'T' ORDER BY id
