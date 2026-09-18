-- D207, D202 (part 2b): a derived column's sibling is the per-row meet of its origins' names,
-- under the report's order, so a grid trusts it the same way.
SELECT first_name || '/' || postcode AS tag FROM members ORDER BY id
