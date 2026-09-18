-- §8 corpus 08, as u2: the creator rule lifts three notes out of the agent's mask.
SELECT m.last_name, o.note
FROM members m JOIN orders o ON o.member_id = m.id
