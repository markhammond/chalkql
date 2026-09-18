-- D196, first match wins: u7 is an agent and an auditor in the one org, and the agent's FULL
-- rule stands first, so AGGREGATE_ONLY is not reachable and the projection stands.
SELECT id, amount FROM orders ORDER BY id
