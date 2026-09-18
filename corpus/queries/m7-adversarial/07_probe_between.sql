-- §3.1. A range, which is a binary search with one comparison: repeated halving recovers a value
-- from an ordering. What is ordered here is the disclosed value, so the search converges on the
-- mask.
-- expect: principals(all)
SELECT id FROM members WHERE first_name BETWEEN 'A' AND 'C' ORDER BY id
