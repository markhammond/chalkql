-- §3.2: a constant mask hides NULL-ness as well as the value, so IS NULL matches nothing.
SELECT id FROM orders WHERE note IS NULL ORDER BY id
