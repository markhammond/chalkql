-- D253, and the one statement here that is *not* held to the oracle. The group-size guard NULLs an
-- aggregate over a group below the floor; two aggregates over sets that differ by that group,
-- each above the floor, give it back by subtraction. This is query-set-size control's documented
-- limit rather than a defect — defending it is differential privacy's problem, not a leaf
-- rewrite's — and the statement is here so the limit is visible in the corpus rather than assumed.
-- The golden shows it happening: for the auditor who holds both organisations the two answers
-- differ by exactly the sum of the group the guard NULLs when it is asked for directly.
-- expect: principals(all)
-- expect: known-limit
SELECT SUM(amount) AS total, SUM(amount) FILTER (WHERE org_id <> 2) AS without_o2 FROM orders
