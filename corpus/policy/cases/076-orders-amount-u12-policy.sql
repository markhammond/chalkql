-- §3.4, the mixed principal: AGGREGATE_ONLY is possible for a row u12 can see, so the statement
-- is refused rather than half-answered -- values for one org and placeholders for the other.
SELECT amount FROM orders
