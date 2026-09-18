-- §3.1. The same read with an offset, which is the binary search written as one statement: walking
-- the offset enumerates the sorted values one at a time. Over masks it enumerates masks.
-- expect: principals(all)
SELECT first_name FROM members ORDER BY first_name, id LIMIT 1 OFFSET 2
