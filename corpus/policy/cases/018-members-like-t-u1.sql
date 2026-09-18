-- §8 corpus 04: the predicate compares the DISCLOSED value (§3.1, D195) -- raw names in the
-- manager's org, initials in the agent's, so O2 matches on the initial alone.
SELECT * FROM members WHERE first_name LIKE 'T%'
