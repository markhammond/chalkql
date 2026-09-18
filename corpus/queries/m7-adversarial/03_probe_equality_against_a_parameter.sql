-- §3.1. The same guess through a bound parameter rather than a literal, because a host's own
-- statement is a shape and the end user supplies the values (§0): a parameter must reach exactly
-- the same comparison the literal did, and never the raw column.
-- expect: principals(all)
SELECT id FROM members WHERE first_name = @guess ORDER BY id
