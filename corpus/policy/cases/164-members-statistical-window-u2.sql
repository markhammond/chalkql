-- D203 (part 2b): no window in a statistical statement -- a partition can be one row.
SELECT first_name, COUNT(*) OVER (PARTITION BY first_name) AS n FROM members
